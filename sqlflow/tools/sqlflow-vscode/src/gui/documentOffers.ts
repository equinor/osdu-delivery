import { kindLabel, type RecognisedDocument } from '../documents/recognition.ts';

// What the side bar offers on a document's row, and the heading it lists the document under: SQLFlow's own offers and
// the ones a host's modules add. Kept free of the editor's API, so the unit suite reads it with a module of its own.

/** A document as an offer is made for it: what it is, and where it is. */
export interface OfferedDocument extends RecognisedDocument {
    /** The path from the workspace folder, forward slashed. */
    relativePath: string;
}

/** Something that can be done with a document of the working copy, offered on its row of the side bar. */
export interface DocumentOffer {
    /** Unique among a document's offers. */
    id: string;
    label: string;
    /** A name the side bar draws a glyph for: `run`, `page`, `preview`, `compare`, `check`, `history`. */
    icon?: string;
    /** Shown on the row itself as well as in its menu. */
    primary?: boolean;
    run(): Promise<void>;
}

/** What a document is offered, given the catalog's flow of it when the estate has one. */
export type DocumentOfferProvider<D extends OfferedDocument, P> = (document: D, pipeline: P | undefined) => DocumentOffer[];

/** The offers of every provider, SQLFlow's own and the modules'. */
export class DocumentOffers<D extends OfferedDocument = OfferedDocument, P = unknown> {
    private readonly providers: DocumentOfferProvider<D, P>[] = [];
    private readonly groups = new Map<string, string>();

    register(provider: DocumentOfferProvider<D, P>): void {
        this.providers.push(provider);
    }

    /** Names the group the documents of a `documentType` are listed under: "Mappings" for `mapping`. */
    nameGroup(documentType: string, label: string): void {
        const named = this.groups.get(documentType);
        if (named !== undefined && named !== label) {
            throw new Error(`The documents of type '${documentType}' are already listed under '${named}'.`);
        }
        this.groups.set(documentType, label);
    }

    /** What a document is offered. Two offers of one id on a document would run the first, so the second is refused. */
    of(document: D, pipeline: P | undefined): DocumentOffer[] {
        const offers = this.providers.flatMap((provider) => provider(document, pipeline));
        const ids = new Set<string>();
        for (const offer of offers) {
            if (ids.has(offer.id)) {
                throw new Error(`Two offers on '${document.relativePath}' go by the id '${offer.id}'.`);
            }
            ids.add(offer.id);
        }
        return offers;
    }

    /** The heading a document is listed under. The side bar names a flow's group itself, by how its kind is shown. */
    groupOf(document: RecognisedDocument): string {
        switch (document.kind) {
            case 'flow':
                return `${kindLabel(document)} flows`;
            case 'schedules':
                return 'Schedules';
            case 'subscribers':
                return 'Subscribers';
            case 'ai-context':
                return 'AI contexts';
            default:
                return this.groups.get(document.documentType ?? '') ?? `${kindLabel(document)} documents`;
        }
    }
}

/**
 * The name a document is listed by: the one it gives itself, or its file's when it gives none. A document kept in a
 * file named for it and its version (`Orders@1.2.0.yaml`) is listed by the file's name, so two versions of one document
 * in a folder read apart.
 */
export function listedName(document: OfferedDocument): string {
    const file = document.relativePath.slice(document.relativePath.lastIndexOf('/') + 1);
    const stem = file.replace(/\.ya?ml$/i, '');
    if (document.name === undefined || document.name === '') {
        return file;
    }
    return stem.startsWith(`${document.name}@`) ? stem : document.name;
}
