/** What a document is: a flow, one of SQLFlow's library files, or a document a module adds, named by `documentType`. */
export type DocumentKind = 'flow' | 'schedules' | 'subscribers' | 'ai-context' | 'document';

export interface RecognisedDocument {
    kind: DocumentKind;
    /** A flow's `flowType`; absent on a file flow, which declares none. */
    flowType?: string;
    /** A document's `documentType`. */
    documentType?: string;
    /** The document's own name: `name`, or an AI context's `aiContext`. */
    name?: string;
}

/** A root key's scalar value, with its quotes and any comment off. */
function rootValue(text: string, key: string): string | undefined {
    const match = new RegExp(`^${key}[ \\t]*:[ \\t]*([^\\r\\n]*)`, 'm').exec(text);
    if (!match) {
        return undefined;
    }
    const value = match[1].trim();
    const quoted = /^(["'])(.*?)\1/.exec(value);
    return quoted ? quoted[2] : value.replace(/\s+#.*$/, '').trim();
}

function hasRootKey(text: string, key: string): boolean {
    return new RegExp(`^${key}[ \\t]*:`, 'm').test(text);
}

/**
 * Whether a YAML file is a pipeline document, and which. Discovery is by content, as the estate's own is: a repository's
 * flows are plain `*.yaml` files (the `.flow.yaml` suffix is historical and optional), so the name says little and the
 * root keys say what the file is. This follows `FlowDocumentRecognition` and the language engine's document kinds, reading
 * root keys at the start of a line so that a file with a syntax error is still recognised: a half-typed flow is exactly
 * the file that needs the editor's help. Anything else, a compose file or a CI workflow, answers undefined and stays
 * plain YAML.
 */
export function recogniseDocument(path: string, text: string): RecognisedDocument | undefined {
    const name = rootValue(text, 'name') || undefined;

    const flowType = rootValue(text, 'flowType');
    if (flowType) {
        return { kind: 'flow', flowType, name };
    }

    const documentType = rootValue(text, 'documentType');
    if (documentType) {
        return { kind: 'document', documentType, name };
    }

    if (hasRootKey(text, 'schedules')) {
        return { kind: 'schedules', name };
    }
    if (hasRootKey(text, 'subscribers')) {
        return { kind: 'subscribers', name };
    }
    if (hasRootKey(text, 'aiContext')) {
        return { kind: 'ai-context', name: rootValue(text, 'aiContext') || name };
    }

    const flowSuffix = path.toLowerCase().endsWith('.flow.yaml');
    if (flowSuffix || (hasRootKey(text, 'source') && hasRootKey(text, 'target'))) {
        return { kind: 'flow', name };
    }

    return undefined;
}

/** The `flowType` of a lineage description: a document that says what a step another system runs reads and writes. */
export const LINEAGE_DESCRIPTION = 'lin';

/** The `flowType` of a report: a document of read-only queries a person runs on demand from the report's page. */
export const REPORT = 'rpt';

/**
 * The kinds of flow document that are never run as a flow: a lineage description is read for lineage only, and a
 * report's queries are run from the report's page. The catalog keeps each as a pipeline so its text has one home, and
 * lists it only when it is asked for by its kind.
 */
export const NEVER_RUN_KINDS: readonly string[] = [LINEAGE_DESCRIPTION, REPORT];

/**
 * The kind the catalog lists a document by when its `flowType` is one that is never run as a flow, written as the
 * catalog writes it; undefined for a flow that runs, a file flow included.
 */
export function neverRunKind(flowType: string | undefined): string | undefined {
    const kind = flowType?.trim().toLowerCase();
    return kind !== undefined && NEVER_RUN_KINDS.includes(kind) ? kind : undefined;
}

/**
 * The page of the GUI a document's catalog entry opens on, by the kind the catalog lists it under: a report opens on its
 * own page, where its queries run and are drawn; every other flow on its flow page.
 */
export function pagePathOf(pipelineId: string, kind: string | undefined): string {
    return neverRunKind(kind) === REPORT
        ? `/reports/${encodeURIComponent(pipelineId)}`
        : `/pipelines/${encodeURIComponent(pipelineId)}`;
}

/** The word a person reads for a document's kind: its `flowType`, `documentType` or library. */
export function kindLabel(document: RecognisedDocument): string {
    switch (document.kind) {
        case 'flow':
            return document.flowType ?? 'file';
        case 'document':
            return document.documentType ?? 'document';
        default:
            return document.kind;
    }
}
