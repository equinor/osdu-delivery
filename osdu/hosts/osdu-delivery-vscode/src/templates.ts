import { keyPathAtLine } from '../../../../sqlflow/tools/sqlflow-vscode/src/documents/yamlPath.ts';

/** One property of a template, by its place in the record: `data.Curves[].CurveUnit`, as a mapping fills it. */
export interface TemplateVariable {
    path: string;
    type: string;
    /** Whether the schema requires it in the object that holds it. */
    required: boolean;
    title?: string;
    description?: string;
    /** The entity types an id here points to. */
    entityTypes: string[];
    /** The unit context of a number. */
    unitContext?: string;
    /** An object that takes keys of its author's choosing, so anything under it is a property. */
    open: boolean;
}

/** The parts of a record a mapping fills: the rest (`id`, `kind`, the system fields) OSDU Delivery or OSDU writes. */
const FILLED_ROOTS = new Set(['acl', 'legal', 'tags', 'data', 'ancestry', 'meta']);

/** Deep enough for every published kind; a schema can refer to itself, which this stops. */
const MAX_DEPTH = 12;

type Schema = Record<string, unknown>;

interface ResolvedNode {
    types: string[];
    properties: Map<string, Schema>;
    required: Set<string>;
    items?: Schema;
    title?: string;
    description?: string;
    relationships: string[];
    unitContext?: string;
    additional: boolean;
}

function definition(root: Schema, reference: string): Schema | undefined {
    if (!reference.startsWith('#/')) {
        return undefined;
    }
    let node: unknown = root;
    for (const part of reference.slice(2).split('/')) {
        node = (node as Schema | undefined)?.[part.replace(/~1/g, '/').replace(/~0/g, '~')];
    }
    return node as Schema | undefined;
}

/** A schema node with its `$ref`s and `allOf`s folded in, which is how the published kinds are composed. */
function resolve(root: Schema, node: Schema, seen: readonly string[]): ResolvedNode {
    const resolved: ResolvedNode = { types: [], properties: new Map(), required: new Set(), relationships: [], additional: false };

    const merge = (other: Schema, trail: readonly string[]): void => {
        const reference = other.$ref;
        if (typeof reference === 'string' && !trail.includes(reference)) {
            const target = definition(root, reference);
            if (target) {
                merge(target, [...trail, reference]);
            }
        }
        for (const part of (other.allOf as Schema[] | undefined) ?? []) {
            merge(part, trail);
        }
        for (const [name, property] of Object.entries((other.properties as Record<string, Schema> | undefined) ?? {})) {
            resolved.properties.set(name, property);
        }
        for (const name of (other.required as string[] | undefined) ?? []) {
            resolved.required.add(name);
        }
        if (typeof other.type === 'string') {
            resolved.types.push(other.type);
        }
        if (other.items !== undefined) {
            resolved.items = other.items as Schema;
        }
        resolved.title ??= other.title as string | undefined;
        resolved.description ??= other.description as string | undefined;
        resolved.unitContext ??= (other['x-osdu-frame-of-reference'] as string | undefined);
        for (const relationship of (other['x-osdu-relationship'] as { EntityType?: string }[] | undefined) ?? []) {
            if (relationship.EntityType) {
                resolved.relationships.push(relationship.EntityType);
            }
        }
        if (other.additionalProperties !== undefined && other.additionalProperties !== false) {
            resolved.additional = true;
        }
    };

    merge(node, seen);
    return resolved;
}

/** Every property a mapping can fill in a template, walking the schema as the record lays it out. */
export function templateVariables(schema: Schema): TemplateVariable[] {
    const variables: TemplateVariable[] = [];

    const walk = (node: Schema, prefix: string, required: boolean, depth: number, trail: readonly string[]): void => {
        const resolved = resolve(schema, node, trail);
        const array = resolved.types.includes('array');
        const object = resolved.properties.size > 0;
        const open = resolved.additional || (resolved.types.includes('object') && !object);
        variables.push({
            path: prefix,
            type: array ? 'array' : resolved.types[0] ?? (object ? 'object' : 'any'),
            required,
            title: resolved.title,
            description: resolved.description,
            entityTypes: resolved.relationships,
            unitContext: resolved.unitContext,
            open,
        });
        if (depth >= MAX_DEPTH) {
            return;
        }

        const nextTrail = typeof node.$ref === 'string' ? [...trail, node.$ref] : trail;
        if (array && resolved.items) {
            const items = resolve(schema, resolved.items, nextTrail);
            for (const [name, property] of items.properties) {
                walk(property, `${prefix}[].${name}`, items.required.has(name), depth + 1, nextTrail);
            }
            return;
        }
        for (const [name, property] of resolved.properties) {
            walk(property, `${prefix}.${name}`, resolved.required.has(name), depth + 1, nextTrail);
        }
    };

    const top = resolve(schema, schema, []);
    for (const [name, property] of top.properties) {
        if (FILLED_ROOTS.has(name)) {
            walk(property, name, top.required.has(name), 0, []);
        }
    }
    return variables;
}

/** The kind a template file holds: its `x-osdu-schema-source`, or what its name spells. */
export function templateKind(schema: Schema, fileName: string): string | undefined {
    const declared = schema['x-osdu-schema-source'];
    if (typeof declared === 'string' && declared !== '') {
        return declared;
    }
    const named = /^([^_]+)_([^_]+)_(.+)_(\d+\.\d+\.\d+)\.json$/.exec(fileName);
    return named ? `${named[1]}:${named[2]}:${named[3]}:${named[4]}` : undefined;
}

/** A key a mapping's `record` holds that fills a property, by the property's path. */
export interface RecordEntry {
    path: string;
    line: number;
    keyStart: number;
    keyEnd: number;
}

/**
 * A mapping's key path as a template's: `record.data.Curves.$item.CurveUnit` is `data.Curves[].CurveUnit`. Null for what
 * is no property: anything outside `record`, a `$` word, and whatever a `$` word takes, whose keys are the word's own.
 */
export function recordPath(keyPath: string): string | null {
    if (!keyPath.startsWith('record.')) {
        return null;
    }
    let path = '';
    for (const segment of keyPath.split('.').slice(1)) {
        if (segment === '$item') {
            path += '[]';
        } else if (segment.startsWith('$')) {
            return null;
        } else {
            path += path === '' ? segment : `.${segment}`;
        }
    }
    return path;
}

/** Every property a mapping fills, where its key stands. */
export function recordEntries(lines: readonly string[]): RecordEntry[] {
    const entries: RecordEntry[] = [];
    lines.forEach((text, line) => {
        const key = keyPathAtLine(lines, line);
        const path = key ? recordPath(key.path) : null;
        // `$item:` opens a list's items; it fills nothing itself.
        if (path && !path.endsWith('[]')) {
            const name = path.slice(path.lastIndexOf('.') + 1);
            const keyStart = text.indexOf(name);
            entries.push({ path, line, keyStart: Math.max(0, keyStart), keyEnd: Math.max(0, keyStart) + name.length });
        }
    });
    return entries;
}

/** The record path of the object a new key on `line` would land in, read from its indentation. */
export function parentRecordPath(lines: readonly string[], line: number): string | null {
    const indent = (lines[line] ?? '').length - (lines[line] ?? '').trimStart().length;
    for (let index = line - 1; index >= 0; index--) {
        const text = lines[index];
        if (text.trim() === '' || text.trimStart().startsWith('#')) {
            continue;
        }
        const lead = text.length - text.trimStart().length;
        if (lead < indent) {
            const key = keyPathAtLine(lines, index);
            if (key?.path === 'record') {
                return '';
            }
            return key ? recordPath(key.path) : null;
        }
    }
    return null;
}

/** The properties directly under a record path. */
export function childrenOf(variables: readonly TemplateVariable[], parent: string): TemplateVariable[] {
    const base = parent === '' ? '' : `${parent}.`;
    return variables.filter((variable) => {
        if (!variable.path.startsWith(base) || variable.path === parent) {
            return false;
        }
        const rest = variable.path.slice(base.length);
        return !rest.includes('.') && !rest.includes('[');
    });
}

export interface MappingFinding {
    message: string;
    line: number;
    keyStart: number;
    keyEnd: number;
    severity: 'warning' | 'hint';
}

/**
 * What a mapping's `record` says against its template, read from the working copy: a key the template does not hold, and
 * a required property nothing fills, which is what makes OSDU refuse a record. The control plane's Check Mapping is the
 * final word; this is what can be said as a person types, with nothing signed in.
 */
export function checkRecord(lines: readonly string[], variables: readonly TemplateVariable[], kind: string): MappingFinding[] {
    const findings: MappingFinding[] = [];
    // A list of objects is written two ways, under `$item` or as literal `- key:` items, so the step into its items is
    // left out of what is compared.
    const plain = (path: string): string => path.replace(/\[\]/g, '');
    const known = new Map(variables.map((variable) => [plain(variable.path), variable]));
    const entries = recordEntries(lines);
    const filled = new Set(entries.map((entry) => plain(entry.path)));

    const underOpen = (path: string): boolean => {
        for (let end = path.lastIndexOf('.'); end > 0; end = path.lastIndexOf('.', end - 1)) {
            if (known.get(path.slice(0, end))?.open) {
                return true;
            }
        }
        return false;
    };
    for (const entry of entries) {
        const path = plain(entry.path);
        if (!known.has(path) && !underOpen(path)) {
            findings.push({
                message: `${kind} holds no property '${entry.path}'.`,
                line: entry.line,
                keyStart: entry.keyStart,
                keyEnd: entry.keyEnd,
                severity: 'warning',
            });
        }
    }

    const recordLine = lines.findIndex((line) => /^record\s*:/.test(line));
    for (const variable of variables) {
        const path = plain(variable.path);
        const end = path.lastIndexOf('.');
        const reached = end < 0 || filled.has(path.slice(0, end));
        if (variable.required && reached && !filled.has(path)) {
            findings.push({
                message: `The template requires '${variable.path}', which nothing fills: OSDU refuses a record without it.`,
                line: Math.max(0, recordLine),
                keyStart: 0,
                keyEnd: 6,
                severity: 'warning',
            });
        }
    }
    return findings;
}

/** The hover of a property: what the template says of it. */
export function describeVariable(variable: TemplateVariable): string {
    const facts = [`\`${variable.type}\``];
    if (variable.required) {
        facts.push('required');
    }
    if (variable.entityTypes.length > 0) {
        facts.push(`points to ${variable.entityTypes.join(', ')}`);
    }
    if (variable.unitContext) {
        facts.push(`unit context ${variable.unitContext}`);
    }
    const heading = `**${variable.title ?? variable.path}** (${facts.join(', ')})`;
    return variable.description ? `${heading}\n\n${variable.description}` : heading;
}

/** A flow's or a mapping's `parameters` block, by name. */
export interface DeclaredParameter {
    name: string;
    required: boolean;
    defaultValue?: string;
    description?: string;
}

export function declaredParameters(lines: readonly string[]): DeclaredParameter[] {
    const parameters: DeclaredParameter[] = [];
    let inside = false;
    for (const line of lines) {
        if (/^parameters\s*:/.test(line)) {
            inside = true;
            continue;
        }
        if (/^\S/.test(line) && !line.startsWith('#')) {
            inside = false;
        }
        if (!inside) {
            continue;
        }
        const name = /^ {2}([\w.-]+)\s*:/.exec(line);
        if (name) {
            parameters.push({ name: name[1], required: false });
            continue;
        }
        const setting = /^ {4}(required|default|description)\s*:\s*(.*)$/.exec(line);
        const parameter = parameters[parameters.length - 1];
        if (setting && parameter) {
            const value = setting[2].trim().replace(/^(["'])(.*)\1$/, '$2');
            if (setting[1] === 'required') {
                parameter.required = value === 'true';
            } else if (setting[1] === 'default') {
                parameter.defaultValue = value;
            } else {
                parameter.description = value;
            }
        }
    }
    return parameters;
}
