// Reading a dimension through the delivery API: the search composed from values picked across dimensions, the queries
// of its values, keys, changes and table, a page of its table, and the sets and formats it exports in.

import type { DimensionChangeKind } from "./dimensions";

/** An attribute picked: its name, and the values a key has to hold one of. */
export interface DeliveryDimensionAttributePick {
  name: string;
  values: string[];
}

/** What is picked in one dimension: values by id or as the dimension holds them, and attribute values its keys hold. */
export interface DeliveryDimensionPick {
  dimensionId: number;
  valueIds?: number[];
  values?: string[];
  attributes?: DeliveryDimensionAttributePick[];
}

/** A search to compose from the values picked across dimensions. */
export interface DeliveryDimensionSearchRequest {
  picks: DeliveryDimensionPick[];
  /** The kind to search; left out, the one kind every dimension picked in reads. */
  kind?: string | null;
  /** A query narrowing the search further. */
  within?: string | null;
}

/** A value a filter or a search covers. */
export interface DeliveryDimensionFilterValue {
  valueId: number;
  value: string;
  records: number;
  recordsExact: boolean;
  keys: number;
}

/** One dimension's part of a composed search: the values picked it holds now, the keys they compare, and its filter. */
export interface DeliveryDimensionSearchPart {
  dimensionId: number;
  dimension: string;
  aggregateBy: string;
  values: DeliveryDimensionFilterValue[];
  keys: number;
  unfilterable: number;
  filter: string;
  query: string | null;
  /** The attribute values its keys were picked by. */
  attributes: DeliveryDimensionAttributePick[];
}

/** A composed search: the kind and query to send, the request body the search service takes, and what the picks left out. */
export interface DeliveryDimensionSearch {
  kind: string;
  query: string;
  /** The body of POST /api/search/v2/query, as JSON text. */
  request: string;
  parts: DeliveryDimensionSearchPart[];
  clauses: number;
  removed: string[];
  missing: string[];
  notes: string[];
}

export interface DeliveryDimensionValueQuery {
  search?: string;
  order?: "value" | "records";
  removed?: boolean;
  after?: string | null;
  limit?: number;
  /** Only the values holding a key that holds every attribute value named. */
  attributes?: DimensionAttributeCondition[];
}

export interface DeliveryDimensionKeyQuery {
  search?: string;
  value?: number;
  leftOut?: boolean;
  removed?: boolean;
  order?: "arrival" | "count";
  after?: string | null;
  limit?: number;
  /** Only the keys holding every attribute value named. */
  attributes?: DimensionAttributeCondition[];
}

/** One attribute value a key has to hold: several of one attribute are any of them, several attributes all of them. */
export interface DimensionAttributeCondition {
  name: string;
  value: string;
}

export interface DeliveryDimensionChangeQuery {
  build?: number;
  key?: number;
  value?: number;
  change?: DimensionChangeKind;
  before?: number | null;
  limit?: number;
}

/**
 * One row of a dimension's table: the row's number (what a table of facts joins on), the key's number, the key, its value,
 * its attributes in the order of the table's attribute columns (null where the key has none), the records of the row,
 * the search finding the key's records, and when a build first wrote the row and last changed it, in UTC (the table's
 * `InsertedDate_DW` and `UpdatedDate_DW`; null from a table its next build has yet to give them).
 */
export interface DeliveryDimensionTableRow {
  id: number;
  keyId: number;
  key: string;
  value: string;
  attributes: (string | null)[];
  records: number;
  filter: string | null;
  insertedUtc: string | null;
  updatedUtc: string | null;
}

/**
 * A page of a dimension's table; `total` is how many rows the query matches, when the page says. `keyColumn` and
 * `valueColumn` are what the table names the columns holding each row's `key` and `value`, as it has them now.
 */
export interface DeliveryDimensionTable {
  table: string;
  keyColumn: string;
  valueColumn: string;
  attributes: string[];
  rows: DeliveryDimensionTableRow[];
  more: boolean;
  total: number | null;
}

export interface DeliveryDimensionTableQuery {
  search?: string;
  /** The column the rows are ordered by: one of the table by its name, or `value` and `key` for those two whatever the table names them. */
  order?: string;
  dir?: "asc" | "desc";
  offset?: number;
  limit?: number;
  /** Only the rows holding every attribute value named. */
  attributes?: DimensionAttributeCondition[];
}

export type DimensionExportSet = "values" | "keys" | "table";
export type DimensionExportFormat = "csv" | "jsonl";
