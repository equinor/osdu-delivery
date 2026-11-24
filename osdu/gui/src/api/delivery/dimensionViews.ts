// Dimension views as the delivery API answers them: each view with its joins, columns and the checks its builds made,
// what the templates say of its joins, the joins it could make, and a removed view.

import type { DeliveryDimensionYaml } from "./dimensionBlueprints";

/** What a build's check of a view came to: read whole, or not readable. */
export type DimensionViewCheckStatus = "passed" | "failed";

/** One join of a view: the alias its columns are read by, the dimension joined, the column joined on, and the joined table. */
export interface DeliveryDimensionViewJoin {
  alias: string;
  to: string;
  on: string;
  table: string;
}

/** One column of a view: its type, the expression it is computed by, the type it is converted to, and what it holds. */
export interface DeliveryDimensionViewColumn {
  name: string;
  type: string;
  expression?: string | null;
  dataType?: string | null;
  description?: string | null;
}

/** What a check found of one join: the rows that found their row, those whose value found none, and some of those values. */
export interface DeliveryDimensionViewJoinCheck {
  alias: string;
  to: string;
  on: string;
  matched: number;
  unmatched: number;
  examples: string[];
}

/** A row whose value a column's conversion could not read: the row's number (the view's `id`) and the value. */
export interface DeliveryDimensionViewExample {
  id: number;
  value: string;
}

/** What a check found of one column: the rows holding a value, and for a converted column the values it could not read. */
export interface DeliveryDimensionViewColumnCheck {
  name: string;
  values: number;
  /** Null for a column the view does not convert. */
  unconverted?: number | null;
  examples: DeliveryDimensionViewExample[];
}

/**
 * What a build's check of a view found in one partition: `passed` with its rows, joins and columns, or `failed` with SQL
 * Server's message; `notes` says what the run's result says of it, a sentence each.
 */
export interface DeliveryDimensionViewCheck {
  checkId: number;
  partition: string;
  runId?: string | null;
  status: DimensionViewCheckStatus;
  rows: number;
  joins: DeliveryDimensionViewJoinCheck[];
  columns: DeliveryDimensionViewColumnCheck[];
  error?: string | null;
  checkedUtc: string;
  durationMs: number;
  notes: string[];
  /** The check whose findings this one keeps, since nothing the view reads had changed since it; absent for a check that read the view. */
  reusedFrom?: number | null;
}

/**
 * A view of a dimension flow: as its flow declares it now (`declared`), as a build last wrote it (`recorded`, with
 * `written` false when a build dropped it to write it again, `note` saying why), and whether the two differ (`changed`:
 * the next build writes it again). A view a build wrote that no flow declares any more is listed with `declared` false.
 */
export interface DeliveryDimensionView {
  name: string;
  /** The view's name, without its schema (`v_dim_Curve`). */
  viewName: string;
  /**
   * Where the view and the tables it reads are kept, as a person types it before their names: `osdu`, or
   * `<database>.<schema>` when the flow's target names a database. Absent from an older control plane.
   */
  kept?: string;
  flowName: string;
  /** Null when the catalog holds no pipeline of the flow's name. */
  pipelineId?: string | null;
  declared: boolean;
  recorded: boolean;
  written: boolean;
  changed: boolean;
  description?: string | null;
  /** The dimension whose rows the view's rows are. */
  from: string;
  /** The condition a row of `from` is kept by; null when the view keeps every row. */
  where?: string | null;
  /** The columns a grouped view groups its rows by; empty for a view of a row per row of `from`. */
  groupBy?: string[] | null;
  joins: DeliveryDimensionViewJoin[];
  /** The view's columns in order, its `partition` and `id` first. */
  columns: DeliveryDimensionViewColumn[];
  /** The dimension tables the view reads (`dim_...`), its `from` table first. */
  tables: string[];
  note?: string | null;
  writtenRunId?: string | null;
  writtenBy?: string | null;
  writtenUtc?: string | null;
  /** The newest check, in whichever partition a build ran. */
  lastCheck?: DeliveryDimensionViewCheck | null;
}

/** What the flow's templates say of a join: it agrees, it joins a dimension of another entity type (and finds nothing), or no template says. */
export type DimensionViewJoinVerdictKind = "agrees" | "differs" | "unchecked";

/**
 * What the flow's templates say of one join a view declares: the entity types the joined column names (its template's
 * `x-osdu-relationship`, or an id's pattern), the entity type the joined dimension's kind names, and the verdict.
 */
export interface DeliveryDimensionViewJoinVerdict {
  alias: string;
  on: string;
  to: string;
  names: string[];
  /** Null for a dimension whose kind names any type, or one not keyed by id. */
  reads?: string | null;
  verdict: DimensionViewJoinVerdictKind;
  note: string;
}

/** A view with its newest checks (newest first), its SQL as last written and as declared now, its item of the flow's YAML, and what the templates say of its joins. */
export interface DeliveryDimensionViewDetail {
  view: DeliveryDimensionView;
  sql?: string | null;
  declaredSql?: string | null;
  checks: DeliveryDimensionViewCheck[];
  yaml?: DeliveryDimensionYaml | null;
  /** One per join the flow declares now; empty for a view no flow declares. */
  joinChecks?: DeliveryDimensionViewJoinVerdict[];
}

/** A join a view could make: the column joined on, the dimension joined, the alias, and why it is offered. */
export interface DeliveryDimensionViewJoinSuggestion {
  on: string;
  to: string;
  as: string;
  note: string;
}

/** The joins a view whose rows are `from`'s could make, and the same as a ready-to-paste `join:` block of a view's YAML. */
export interface DeliveryDimensionViewSuggestion {
  from: string;
  joins: DeliveryDimensionViewJoinSuggestion[];
  yaml: string;
}

/** What removing a view took: the view from the database when it was there, and its record with its checks. */
export interface DeliveryDimensionViewRemoved {
  name: string;
  viewName: string;
  /** Where the view was kept, as a person types it before its name. */
  kept?: string;
  flowName: string;
  dropped: boolean;
  checks: number;
  summary: string;
}
