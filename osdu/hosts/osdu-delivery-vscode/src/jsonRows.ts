/** A value from a tool's answer as rows a view can show: a label, a short value, and what opens under it. */
export interface JsonRow {
    label: string;
    description?: string;
    children?: JsonRow[];
}

/** Fields that say nothing to a person reading a row, and the links a row carries, which its actions use. */
const HIDDEN = new Set(['links', 'guide', 'reading']);

/** The field that names an object, in the order a person would look for one. */
const NAME_FIELDS = ['name', 'interface', 'displayName', 'kind', 'pattern', 'title', 'id'];

function scalar(value: unknown): string {
    return typeof value === 'string' ? value : String(value);
}

function isScalar(value: unknown): boolean {
    return value === null || ['string', 'number', 'boolean'].includes(typeof value);
}

function titleOf(value: Record<string, unknown>, fallback: string): string {
    const field = NAME_FIELDS.find((name) => typeof value[name] === 'string' && value[name] !== '');
    return field ? (value[field] as string) : fallback;
}

/**
 * Any answer as a tree. The delivery tools answer in shapes of their own, and a person developing a flow wants what they
 * hold rather than a page per tool, so a view reads every answer the same way: objects open, a list's items are named
 * for what they are, and the nulls an answer leaves empty stay out.
 */
export function rowsOf(value: unknown): JsonRow[] {
    if (Array.isArray(value)) {
        return value.map((item, index) => rowOf(`${index + 1}`, item));
    }
    if (value !== null && typeof value === 'object') {
        return Object.entries(value as Record<string, unknown>)
            .filter(([name, item]) => !HIDDEN.has(name) && item !== null && item !== undefined)
            .map(([name, item]) => rowOf(name, item));
    }
    return value === null || value === undefined ? [] : [{ label: scalar(value) }];
}

function rowOf(name: string, value: unknown): JsonRow {
    if (isScalar(value)) {
        return { label: name, description: value === null ? '' : scalar(value) };
    }
    if (Array.isArray(value)) {
        if (value.every(isScalar)) {
            return { label: name, description: value.map(scalar).join(', ') };
        }
        return { label: name, description: String(value.length), children: rowsOf(value) };
    }
    const object = value as Record<string, unknown>;
    return { label: titleOf(object, name), description: titleOf(object, name) === name ? undefined : name, children: rowsOf(object) };
}
