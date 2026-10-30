/** What a run of a flow can be given beyond its operation and parameters, as the kind's `payload` names them. */
export type RunOption = 'force' | 'submissionId' | 'recordKeys' | 'sendAgain' | 'interfaces' | 'types';

/** What a deliver run sends beyond what changed: what renders differently, or a part of its records whatever their hashes. */
export type SendAgain = 'changed' | 'uptodate' | 'all' | 'metadata' | 'payload';

/** The operation a kind's run performs when none is named, for the kinds that take options. */
const DEFAULT_OPERATION: Record<string, string> = {
    delivery: 'deliver',
    retrieval: 'retrieve',
    cache: 'refresh',
};

/** Which options an operation of a kind takes: the same rules as the GUI's run dialog (DeliveryTriggerFields). */
export function runOptions(kind: string, operation: string | undefined): RunOption[] {
    const effective = operation ?? DEFAULT_OPERATION[kind];
    if (effective === undefined) {
        return [];
    }

    const delivery = kind === 'delivery';
    const options: RunOption[] = [];
    // A drain delivers what a submission planned and a sync reads every row it is given: neither has a gate to force. A
    // refresh captures every type it is given in full, so a cache run has none either.
    if (kind !== 'cache' && !(delivery && (effective === 'drain' || effective === 'sync'))) {
        options.push('force');
    }
    if (delivery && ['deliver', 'intake', 'drain'].includes(effective)) {
        options.push('submissionId');
    }
    if (delivery && ['deliver', 'verify', 'sync'].includes(effective)) {
        options.push('recordKeys');
    }
    if (delivery && effective === 'deliver') {
        options.push('sendAgain');
    }
    if (delivery) {
        options.push('interfaces');
    }
    if (kind === 'cache') {
        options.push('types');
    }
    return options;
}

export interface RunChoices {
    force?: boolean;
    submissionId?: string;
    recordKeys?: string[];
    sendAgain?: SendAgain;
    interfaces?: string[];
    types?: string[];
}

/** The payload of what was chosen, left out altogether when nothing was. */
export function runPayload(options: readonly RunOption[], choices: RunChoices): Record<string, unknown> | undefined {
    const payload: Record<string, unknown> = {};
    if (options.includes('force') && choices.force) {
        payload.force = true;
    }
    if (options.includes('submissionId') && choices.submissionId) {
        payload.submissionId = choices.submissionId;
    }
    const recordKeys = options.includes('recordKeys') ? choices.recordKeys ?? [] : [];
    if (recordKeys.length > 0) {
        payload.recordKeys = recordKeys;
    }
    if (options.includes('sendAgain')) {
        // Records named by key are always sent again or brought up to date, so naming them makes "what changed" everything.
        const sendAgain = recordKeys.length > 0 && (choices.sendAgain ?? 'changed') === 'changed' ? 'all' : choices.sendAgain ?? 'changed';
        if (sendAgain === 'uptodate') {
            payload.rerender = true;
        } else if (sendAgain !== 'changed') {
            payload.redeliver = sendAgain;
        }
    }
    if (options.includes('interfaces') && (choices.interfaces ?? []).length > 0) {
        payload.interfaces = choices.interfaces;
    }
    if (options.includes('types') && (choices.types ?? []).length > 0) {
        payload.types = [...(choices.types ?? [])].sort((a, b) => a.localeCompare(b));
    }
    return Object.keys(payload).length > 0 ? payload : undefined;
}

/** A flow's `partitions`, written either way a document may: `[dev, test]`, or a list of names or of `- name:` entries. */
export function declaredPartitions(lines: readonly string[]): string[] {
    const start = lines.findIndex((line) => /^partitions\s*:/.test(line));
    if (start < 0) {
        return [];
    }
    const inline = /^partitions\s*:\s*\[(.*)\]/.exec(lines[start]);
    if (inline) {
        return inline[1].split(',').map((name) => name.trim().replace(/^["']|["']$/g, '')).filter((name) => name !== '');
    }
    const partitions: string[] = [];
    for (const line of lines.slice(start + 1)) {
        if (/^\S/.test(line) && !line.startsWith('#') && !line.startsWith('-')) {
            break;
        }
        const entry = /^\s*-\s+(?:name\s*:\s*)?["']?([\w.-]+)["']?\s*(?:#.*)?$/.exec(line);
        if (entry) {
            partitions.push(entry[1]);
        }
    }
    return partitions;
}

/** The file name a template of a kind goes by: `osdu:wks:master-data--Well:1.2.0` is `osdu_wks_master-data--Well_1.2.0.json`. */
export function templateFileName(kind: string): string {
    return `${kind.replace(/:/g, '_')}.json`;
}

/** The name, schema and database of an object a flow names (`OsduData.silver.Well`, or bracketed), for a lineage search. */
export function objectParts(object: string): { name: string; schema?: string; database?: string } {
    const parts = object.split('.').map((part) => part.replace(/^\[|\]$/g, ''));
    return { name: parts[parts.length - 1], schema: parts[parts.length - 2], database: parts[parts.length - 3] };
}
