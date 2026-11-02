// A rendered OSDU record as a Markdown document a person reads (osdu/docs/reference/concepts/mapping-overview.md, Looking at
// the rendered record): its kind and id under a heading, each part of the record (acl, legal, data...) a section with one
// line per property, a nested object's properties by their dotted path, and a list of objects (data.Curves) a table of its
// own, one row per item. The text is what the overview shows rendered and what Copy hands over, so it reads the same pasted
// into a ticket or a chat. No drawing here.

/** The most items of one list a document lists; a longer list says how many more it holds. */
export const MAX_LIST_ROWS = 200;

type Json = string | number | boolean | null | Json[] | { [key: string]: Json };

const isObject = (value: unknown): value is Record<string, Json> => typeof value === "object" && value !== null && !Array.isArray(value);

/** A value in one table cell: on one line, with a pipe that would end the cell escaped; nothing at all reads as `null`. */
function cell(value: Json | undefined): string {
  if (value === undefined) {
    return "";
  }

  if (value === null) {
    return "_null_";
  }

  if (Array.isArray(value)) {
    return value.every((item) => !isObject(item) && !Array.isArray(item)) ? value.map((item) => cell(item)).join(", ") : escape(JSON.stringify(value));
  }

  if (isObject(value)) {
    return escape(JSON.stringify(value));
  }

  const text = String(value);
  return text === "" ? "\"\"" : escape(text);
}

function escape(text: string): string {
  return text.replace(/\\/g, "\\\\").replace(/\|/g, "\\|").replace(/\r?\n/g, " ");
}

/** A heading's words, escaped where Markdown would read them as emphasis or code. */
function words(text: string): string {
  return text.replace(/([\\`*_[\]#<>])/g, "\\$1");
}

/**
 * An object's properties as lines, by dotted path, in the order the object holds them: a nested object's flattened into its
 * parent's lines, and a list of objects set apart as a list of its own.
 */
function flatten(value: Record<string, Json>, prefix: string, lines: [string, Json][], lists: [string, Json[]][]): void {
  for (const [key, inner] of Object.entries(value)) {
    const path = prefix === "" ? key : `${prefix}.${key}`;
    if (isObject(inner) && Object.keys(inner).length > 0) {
      flatten(inner, path, lines, lists);
    } else if (Array.isArray(inner) && inner.length > 0 && inner.every(isObject)) {
      lists.push([path, inner]);
    } else {
      lines.push([path, inner]);
    }
  }
}

function propertyTable(lines: readonly [string, Json][]): string[] {
  return ["| Property | Value |", "| --- | --- |", ...lines.map(([path, value]) => `| ${escape(path)} | ${cell(value)} |`)];
}

/** A list of objects as a table: a column per property any item holds, by dotted path within the item, a row per item. */
function listTable(items: readonly Json[]): string[] {
  const rows = items.slice(0, MAX_LIST_ROWS).map((item) => {
    const lines: [string, Json][] = [];
    const nested: [string, Json[]][] = [];
    flatten(item as Record<string, Json>, "", lines, nested);
    // A list inside an item stays one cell of it: the item's row is what the table is about.
    return new Map<string, Json>([...lines, ...nested.map(([path, list]): [string, Json] => [path, list])]);
  });
  const columns = [...new Set(rows.flatMap((row) => [...row.keys()]))];
  const table = [
    `| # | ${columns.map(escape).join(" | ")} |`,
    `| ---: | ${columns.map(() => "---").join(" | ")} |`,
    ...rows.map((row, index) => `| ${index + 1} | ${columns.map((column) => cell(row.get(column))).join(" | ")} |`),
  ];
  if (items.length > MAX_LIST_ROWS) {
    table.push("", `_And ${(items.length - MAX_LIST_ROWS).toLocaleString("en-US")} more._`);
  }

  return table;
}

/** A part of the record as the document sets it out: its heading, the properties it holds, and the lists set apart from them. */
interface Part {
  key: string;
  lines: [string, Json][];
  lists: [string, Json[]][];
}

/** The record's parts, in the order the record holds them: every property at its top holding an object. */
function partsOf(document: Record<string, Json>): Part[] {
  return Object.entries(document).filter((entry): entry is [string, Record<string, Json>] => isObject(entry[1])).map(([key, value]) => {
    const lines: [string, Json][] = [];
    const lists: [string, Json[]][] = [];
    flatten(value, "", lines, lists);
    return { key, lines, lists };
  });
}

/** A list's heading: its path in the part, and how many items it holds. */
const listHeading = (path: string, items: readonly Json[]) => `${path} (${items.length.toLocaleString("en-US")})`;

/** One entry of a document's outline: a heading as the document writes it, its level, and how many properties or items it holds. */
export interface RecordSection {
  heading: string;
  level: 2 | 3;
  count: number;
}

/** The headings `recordMarkdown` writes, in its order, with what each holds: what an outline beside the document lists. */
export function recordSections(record: Record<string, unknown>): RecordSection[] {
  return partsOf(record as Record<string, Json>).flatMap((part) => [
    { heading: part.key, level: 2 as const, count: part.lines.length + part.lists.length },
    ...part.lists.map(([path, items]) => ({ heading: listHeading(path, items), level: 3 as const, count: items.length })),
  ]);
}

/**
 * The rendered record as Markdown, under `title` (the record's name, as the overview calls it): what the record holds at its
 * top first, its kind and id leading, then a section per part of the record in the order the record holds them, the lists
 * each part holds after its properties.
 */
export function recordMarkdown(record: Record<string, unknown>, title: string): string {
  const document = record as Record<string, Json>;
  const out = [`# ${words(title)}`, ""];

  // What the record holds at its top beside its parts (its kind, its id, a version) is said once, before them.
  const top = Object.entries(document).filter(([, value]) => !isObject(value));
  const rank = (key: string) => (key === "kind" ? 0 : key === "id" ? 1 : 2);
  top.sort(([a], [b]) => rank(a) - rank(b));
  if (top.length > 0) {
    out.push(...propertyTable(top), "");
  }

  for (const part of partsOf(document)) {
    out.push(`## ${words(part.key)}`, "");
    if (part.lines.length === 0 && part.lists.length === 0) {
      out.push("_Empty._", "");
    }

    if (part.lines.length > 0) {
      out.push(...propertyTable(part.lines), "");
    }

    for (const [path, items] of part.lists) {
      out.push(`### ${words(listHeading(path, items))}`, "", ...listTable(items), "");
    }
  }

  return `${out.join("\n").trimEnd()}\n`;
}
