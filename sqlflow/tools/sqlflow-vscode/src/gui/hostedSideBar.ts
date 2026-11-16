import * as vscode from 'vscode';
import type { DocumentStanding, WorkspaceDocument, WorkspaceState } from '../../../../gui/src/embed/protocol';
import { DocumentIndex, IndexedDocument } from '../documents/documentIndex';
import { Estate, Pipeline } from '../mcp/estate';
import { SignIn } from '../mcp/signIn';
import { HostedBuild } from './hostedFiles';
import { HostedLink, HostedSurroundings } from './hostedLink';
import { DocumentOffer, DocumentOffers, listedName } from './documentOffers';

/** The view the side bar is contributed as. */
export const SIDE_BAR_VIEW = 'sqlflow.sideBar';

/** How long changes are gathered before the side bar is told: a save, a sync and a change of editor often come together. */
const SETTLE_MS = 120;

function normalise(text: string): string {
    return text.replace(/\r\n/g, '\n').trim();
}

/**
 * The extension's side bar: one drawn view in place of the editor's own trees. It shows the working copy, read from
 * disk, so it lists what is being written before any of it is pushed, each document with what can be done with it;
 * and under it the estate's pages, each opening in a tab of the editor. The view is the hosted build's side bar entry,
 * and what it lists is sent to it from here.
 */
export class HostedSideBar implements vscode.WebviewViewProvider, vscode.Disposable {
    private readonly subscriptions: vscode.Disposable[] = [];
    private link: HostedLink | undefined;
    private settle: NodeJS.Timeout | undefined;
    private asked = 0;
    /** What the rows last sent may run, by document and offer. */
    private offered = new Map<string, DocumentOffer>();

    constructor(
        private readonly build: () => HostedBuild,
        private readonly around: () => HostedSurroundings,
        private readonly documents: DocumentIndex,
        private readonly estate: Estate,
        private readonly signIn: SignIn,
        private readonly offers: DocumentOffers<IndexedDocument, Pipeline>,
        /** Runs an action that may need a sign-in, showing what goes wrong. */
        private readonly guarded: (action: () => Promise<void>) => Promise<void>
    ) {
        this.subscriptions.push(
            documents.changed(() => this.refresh()),
            vscode.window.onDidChangeActiveTextEditor(() => this.refresh()),
            vscode.workspace.onDidSaveTextDocument(() => this.refresh()),
            vscode.workspace.onDidChangeWorkspaceFolders(() => this.refresh())
        );
    }

    dispose(): void {
        clearTimeout(this.settle);
        this.subscriptions.forEach((subscription) => subscription.dispose());
        this.link?.dispose();
    }

    resolveWebviewView(view: vscode.WebviewView): void {
        const around = this.around();
        view.webview.options = HostedLink.options(around.folder);
        const link = new HostedLink(view.webview, around, {
            started: () => this.refresh(),
            documentAction: (document, action) => {
                const offer = this.offered.get(`${document}\n${action}`);
                if (offer) {
                    void this.guarded(() => offer.run());
                }
            },
        });
        this.link?.dispose();
        this.link = link;
        view.onDidDispose(() => {
            link.dispose();
            if (this.link === link) {
                this.link = undefined;
            }
        });
        try {
            link.load(this.build().sideBar, 'side-bar', around.product);
        } catch (error) {
            // A checkout whose pages are not built yet: the view says how to build them, where it would have been drawn.
            const message = error instanceof Error ? error.message : String(error);
            around.output.error(`[side bar] ${message}`);
            link.say(message);
        }
    }

    /** Tells the side bar the working copy again, once what is changing has settled. */
    refresh(): void {
        if (!this.link) {
            return;
        }
        clearTimeout(this.settle);
        this.settle = setTimeout(() => void this.send(), SETTLE_MS);
    }

    private async send(): Promise<void> {
        // Reading the working copy against the estate takes a moment, and what changed meanwhile asks again: only the
        // newest reading is sent, so an older one that finishes later never replaces it.
        const asked = ++this.asked;
        try {
            const workspace = await this.workspace();
            if (asked === this.asked) {
                this.link?.post({ type: 'workspace', workspace });
            }
        } catch (error) {
            this.around().output.error(`[side bar] ${error instanceof Error ? error.message : error}`);
        }
    }

    /** The catalog's flows by name, when somebody is signed in and the estate answers; a working copy is listed without. */
    private async flows(): Promise<Map<string, Pipeline[]> | undefined> {
        if (this.signIn.who === undefined) {
            return undefined;
        }
        try {
            const byName = new Map<string, Pipeline[]>();
            for (const pipeline of await this.estate.everyPipeline()) {
                byName.set(pipeline.name, [...(byName.get(pipeline.name) ?? []), pipeline]);
            }
            return byName;
        } catch {
            return undefined;
        }
    }

    private async workspace(): Promise<WorkspaceState> {
        const [documents, flows] = await Promise.all([this.documents.all(), this.flows()]);
        const front = vscode.window.activeTextEditor?.document;
        const offered = new Map<string, DocumentOffer>();
        const listed: WorkspaceDocument[] = [];
        let active: string | null = null;

        for (const document of documents) {
            const id = document.uri.toString();
            const pipeline = flowOf(document, flows);
            let standing: DocumentStanding = document.kind !== 'flow' || flows === undefined
                ? 'unknown'
                : pipeline === undefined ? 'local' : 'estate';
            if (front !== undefined && front.uri.toString() === id) {
                active = id;
                if (pipeline !== undefined) {
                    standing = await this.standingOf(pipeline, front.getText());
                }
            }

            const offers = this.offers.of(document, pipeline);
            for (const offer of offers) {
                offered.set(`${id}\n${offer.id}`, offer);
            }
            listed.push({
                id,
                name: listedName(document),
                path: document.relativePath,
                group: this.offers.groupOf(document),
                flowKind: document.kind === 'flow' ? document.flowType ?? 'file' : undefined,
                standing,
                actions: offers.map((offer) => ({ id: offer.id, label: offer.label, icon: offer.icon, primary: offer.primary })),
            });
        }

        this.offered = offered;
        return { documents: listed, active, reading: false };
    }

    /** Whether the text in the editor is what the estate runs. A flow the estate cannot be asked about is only known to be there. */
    private async standingOf(pipeline: Pipeline, text: string): Promise<DocumentStanding> {
        try {
            const synced = await this.estate.pipeline(pipeline.id);
            return normalise(text) === normalise(synced.yaml) ? 'same' : 'differs';
        } catch {
            return 'estate';
        }
    }
}

/** The catalog's flow of a document: the one of its name, and of its path when several repositories hold the name. */
function flowOf(document: IndexedDocument, flows: Map<string, Pipeline[]> | undefined): Pipeline | undefined {
    if (document.kind !== 'flow' || !document.name || flows === undefined) {
        return undefined;
    }
    const named = flows.get(document.name) ?? [];
    return named.find((pipeline) => pipeline.relativePath !== undefined
        && (document.relativePath.endsWith(pipeline.relativePath) || pipeline.relativePath.endsWith(document.relativePath))) ?? named[0];
}
