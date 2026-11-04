// What a mapping does, as the control plane lays it out for the overview (osdu/docs/reference/concepts/mapping-overview.md), and
// the tables a flow reads. It imports nothing, so what is worked out from it can be tested on its own.

/** One property a source of a mapping gives: a column, a parameter or a field of a cached record. */
export interface MappingOutlineField {
  name: string;
  /** A column of the dataset's key. */
  key: boolean;
  /** A column the label or the identity reads, which never reaches the record. */
  display: boolean;
  /** A field a lookup compares rather than reads. */
  match: boolean;
  /** How many value nodes read it. */
  reads: number;
  description?: string | null;
}

/** What a source of a mapping's values is. */
export type MappingOutlineSourceKind = "record" | "child" | "parameters" | "cache" | "search" | "parent" | "childRecord" | "base" | "link";

/** Something a mapping reads: a table, the flow's parameters, a cached type, a searched record set, another record. */
export interface MappingOutlineSource {
  /** What the outline's reads and lookups name it by: `dataset`, `dataset.curves`, `cache.UnitOfMeasure`. */
  id: string;
  kind: MappingOutlineSourceKind;
  name: string;
  detail?: string | null;
  fields: MappingOutlineField[];
}

/** A field a value enters a flow from: what it is made of, what a lookup compares, or what a condition tests. */
export interface MappingOutlineRead {
  source: string;
  field: string;
  role: "value" | "match" | "condition";
}

/** A table a step reads: the field it takes, and the lines that find the record holding it. */
export interface MappingOutlineLookup {
  source: string;
  field?: string | null;
  matches: { field: string; value: string }[];
}

/** One thing done to a value on its way to the property. */
export interface MappingOutlineStep {
  /** The modifier (`split`, `replace`, `ref`...), or `find`, `expr` or `key`. */
  kind: string;
  /** The word a diagram shows it by. */
  label: string;
  /** What it does, in plain words. */
  detail: string;
  /** How the mapping writes it. */
  text: string;
  lookups: MappingOutlineLookup[];
}

/** How one value node comes to its value. */
export interface MappingOutlineFlow {
  origin: string;
  /** The node on one line, as the record shape names it. */
  summary: string;
  /** What a literal writes, as JSON. */
  literal?: string | null;
  reads: MappingOutlineRead[];
  steps: MappingOutlineStep[];
  unverified: boolean;
}

/** How a value node comes to its value, as the overview counts them; empty for what only holds others. */
export type MappingOutlineHow = "copied" | "changed" | "lookedUp" | "fixed" | "linked" | "key" | "";

/** A property of the record, or a part of one (an alternative, an item), and how it is filled. */
export interface MappingOutlineNode {
  path: string;
  name: string;
  shape: "group" | "repeat" | "list" | "choice" | "value";
  how: MappingOutlineHow;
  required: boolean;
  type?: string | null;
  when?: string | null;
  where?: string | null;
  /** The child dataset a `$forEach` repeats. */
  dataset?: string | null;
  description?: string | null;
  /** The line of the document it starts on, from 1. */
  line?: number | null;
  endLine?: number | null;
  flow?: MappingOutlineFlow | null;
  assertions: { label: string; expected: string; onFail: string; stage: string }[];
  children: MappingOutlineNode[];
}

/** What a mapping does, laid out for a person to read: everything it reads, and how each property of the record is filled. */
export interface MappingOutline {
  reference: string;
  kind: string;
  description?: string | null;
  enrichment: boolean;
  dataset: { system: string; key: string[]; label?: string | null; identity: string[]; idFrom: string };
  sources: MappingOutlineSource[];
  record: MappingOutlineNode[];
  enrichments: { name: string; mapping?: string | null; rows?: string | null; bulk: boolean; description?: string | null }[];
}

/** A table a delivery flow reads, with the columns the catalog holds of it. */
export interface DeliverySourceTable {
  /** The child dataset it is read as; null for the record table. */
  dataset: string | null;
  table: string;
  /** Its node in lineage: where the flow's read of it lands, and what the ingestion flow writing it writes. */
  objectKey: string;
  key: string[];
  /** How a child row joins its record: child column to record column. */
  join: Record<string, string> | null;
  /** Without SQLFlow's system columns; empty when the catalog has not described the table. */
  columns: { name: string; dataType: string | null; nullable: boolean }[];
}

/**
 * The first rows of a table a flow reads, every column of it, over the rows a delivery reads in the scope asked: what the overview
 * shows of a table it is asked to look into. Null fields are left out of the answer, as every operation's are.
 */
export interface DeliveryTableRows {
  flow: string;
  interface?: string | null;
  /** The child dataset read; absent for the record table. */
  dataset?: string | null;
  table: string;
  /** In the table's order, without SQLFlow's system columns; `cut` when a value of the column was longer than `maxCharacters`. */
  columns: { name: string; sqlType: string; cut: boolean }[];
  /** Each row's values in the order of `columns`, as text; null where the row holds none. */
  rows: (string | null)[][];
  /** How many rows a delivery reads there, counted up to a cap. */
  counted: number;
  /** True when there are more rows than `counted`. */
  countCapped: boolean;
  maxCharacters: number;
  readUtc: string;
}
