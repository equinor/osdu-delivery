// What a table on the overview can show of its rows, and how (osdu/docs/reference/concepts/mapping-overview.md, Looking at a
// table's rows): a flow's table read in the flow's scope, a type of the partition's cache, or the records a search finds on the
// platform. The columns come in the order the table on the canvas lists its fields, so a reader finds a field where the card
// put it; what the mapping does not read follows, as the table holds it. No drawing here.

import type { DeliverySourceTable } from "../../../api/mappingOutline.ts";
import type { CardField, OutlineCard } from "./outlineModel.ts";

/** Where a table's rows are read from. */
export type RowsSource =
  /** A table the flow reads: the record table, or a child dataset by its name. */
  | { from: "table"; dataset: string | null }
  /** A type of the partition's cache, by its name. */
  | { from: "cache"; type: string }
  /** The records of a kind on the platform, which the search finds its record among. */
  | { from: "search"; kind: string };

/** The rows a preview reads. */
export const PREVIEW_ROWS = 100;

/** What a table's rows are read from, or null for one that holds none (the flow's parameters, another record). */
export function rowsSourceOf(card: OutlineCard): RowsSource | null {
  const { source } = card;
  switch (source.kind) {
    case "record":
      return { from: "table", dataset: null };
    case "child":
      return { from: "table", dataset: source.id.slice(source.id.indexOf(".") + 1) };
    case "cache":
      return { from: "cache", type: source.name };
    case "search":
      return source.detail === null || source.detail === undefined || source.detail === "" ? null : { from: "search", kind: source.detail };
    default:
      return null;
  }
}

/** The words for a look at what a table holds, as a menu offers it: its rows, or the values the flow's parameters render with. */
export function cardPreviewLabel(card: OutlineCard): string | null {
  if (card.source.kind === "parameters") {
    return "Preview the values";
  }

  const source = rowsSourceOf(card);
  return source === null ? null : previewLabel(source);
}

/** The words for what a preview reads, as a menu offers it. */
export function previewLabel(source: RowsSource): string {
  switch (source.from) {
    case "table":
      return `Preview the first ${PREVIEW_ROWS} rows`;
    case "cache":
      return `Preview ${PREVIEW_ROWS} cached records`;
    case "search":
      return `Preview ${PREVIEW_ROWS} records in OSDU`;
  }
}

/** A column of a preview: its name as the rows hold it, where its values sit in a row, and the field of the card it is. */
export interface PreviewColumn {
  name: string;
  index: number;
  /** The field the table on the canvas lists for it; null for a column the mapping does not read. */
  field: CardField | null;
}

/**
 * The columns a preview shows, of the names its rows hold (in the order they hold them): the card's fields first, in the order the
 * card lists them, then the rest as they come. A field takes the column of its own name; failing that, one whose name differs
 * only in case, as SQL Server matches a column's. A cached type can keep both `ID` and `id`, which stay two columns.
 */
export function previewColumns(card: OutlineCard, names: readonly string[]): PreviewColumn[] {
  // A field matched by case alone never takes a column another field is named by exactly.
  const named = new Set(card.fields.map((field) => names.indexOf(field.name)).filter((index) => index >= 0));
  const taken = new Set<number>();
  const columns: PreviewColumn[] = [];
  for (const field of card.fields) {
    const own = names.indexOf(field.name);
    const index = own >= 0
      ? own
      : names.findIndex((name, candidate) => !named.has(candidate) && !taken.has(candidate) && name.toLowerCase() === field.name.toLowerCase());
    if (index >= 0 && !taken.has(index)) {
      taken.add(index);
      columns.push({ name: names[index], index, field });
    }
  }

  names.forEach((name, index) => {
    if (!taken.has(index)) {
      columns.push({ name, index, field: null });
    }
  });

  return columns;
}

/**
 * The columns of a flow's table that say which record a row is of, in the order of the record's key: the key itself for the
 * record table, and for a child dataset the columns its join matches to the key. Null when the table's description does not
 * say, or the join leaves a part of the key out.
 */
export function recordColumnsOf(table: DeliverySourceTable, recordKey: readonly string[]): string[] | null {
  if (recordKey.length === 0) {
    return null;
  }

  if (table.dataset === null) {
    return [...recordKey];
  }

  const join = Object.entries(table.join ?? {});
  const columns = recordKey.map((part) => join.find(([, recordColumn]) => recordColumn.toLowerCase() === part.toLowerCase())?.[0]);
  return columns.every((column) => column !== undefined) ? columns as string[] : null;
}

/**
 * Which rows of a preview are of one record: those whose columns naming a record hold the record's key, part by part. The
 * parts are the text a delivery derives the key from, as the rows' values are.
 */
export function rowsOfRecord(names: readonly string[], rows: readonly (readonly (string | null)[])[], columns: readonly string[] | null, key: readonly string[] | null): Set<number> {
  const matched = new Set<number>();
  if (columns === null || key === null || columns.length !== key.length) {
    return matched;
  }

  const at = columns.map((column) => names.findIndex((name) => name.toLowerCase() === column.toLowerCase()));
  if (at.some((index) => index < 0)) {
    return matched;
  }

  rows.forEach((row, index) => {
    if (at.every((column, part) => row[column] === key[part])) {
      matched.add(index);
    }
  });

  return matched;
}
