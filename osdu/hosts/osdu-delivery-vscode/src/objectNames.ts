// The names a delivery document's values go by, where the editor needs them: the file a template is written to, and the
// parts of a table a flow names.

/** The file name a template of a kind goes by: `osdu:wks:master-data--Well:1.2.0` is `osdu_wks_master-data--Well_1.2.0.json`. */
export function templateFileName(kind: string): string {
    return `${kind.replace(/:/g, '_')}.json`;
}

/** The name, schema and database of an object a flow names (`OsduData.silver.Well`, or bracketed), for a lineage search. */
export function objectParts(object: string): { name: string; schema?: string; database?: string } {
    const parts = object.split('.').map((part) => part.replace(/^\[|\]$/g, ''));
    return { name: parts[parts.length - 1], schema: parts[parts.length - 2], database: parts[parts.length - 3] };
}
