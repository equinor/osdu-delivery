// A value check of a flow's rows as the delivery API answers it: the request, the dry run that travels with it and the
// Schema service's view of a kind, each variable's findings with their causes, the proof of a finding, the T-SQL
// reading what an attribute reads, and the export of every issue.

import type { RunValue } from "@/api/types";
import type { AssertionAction } from "../validation";
import type { DeliveryPreviewAction } from "./preview";

/**
 * A value check of a flow's rows: the variables to check (none for every variable of the mapping), the scope's values,
 * the rows to read (0 for the whole scope), the example records each finding names, how many it passes over first (the
 * next page of a finding), and the mapping it is asked of, which the flow must render with.
 */
export interface DeliveryValueCheckRequest {
  targets?: string[];
  values?: Record<string, RunValue>;
  maxRows?: number;
  samples?: number;
  skipSamples?: number;
  mapping?: string | null;
  /** Also take the rows through everything a delivery does short of sending them; it names no variable, since it reads them all. */
  dryRun?: boolean;
}

/** What a dry run says of one row: sent as it is, worth a look first, not sent, or never rendered at all. */
export type DeliveryDryRunOutcome = "ready" | "attention" | "held" | "passedOver";

/** What the partition's Schema service says of a kind, against the template a mapping pins. */
export type DeliverySchemaMatchState = "matches" | "differs" | "read" | "unregistered" | "unavailable" | "notApplicable";

/** One thing about a property that differs: its value in the template, and in the Schema service's schema. */
export interface DeliverySchemaDifferenceField {
  field: string;
  template?: string | null;
  service?: string | null;
}

/** A property the template and the Schema service's schema do not agree on. */
export interface DeliverySchemaDifference {
  path: string;
  change: "onlyInService" | "onlyInTemplate" | "different";
  impact: "breaking" | "additive";
  fields: DeliverySchemaDifferenceField[];
}

/** What the partition's Schema service holds for a kind, set against the template a mapping pins. */
export interface DeliverySchemaCrossMatch {
  kind: string;
  state: DeliverySchemaMatchState;
  why?: string | null;
  status?: string | null;
  scope?: string | null;
  templateVersion?: string | null;
  serviceVersion?: string | null;
  schemasRead: number;
  unresolved: string[];
  differenceCount: number;
  breakingCount: number;
  differences: DeliverySchemaDifference[];
}

/** One staged row as a dry run found it; its document is read on demand. */
export interface DeliveryDryRunRecord {
  sourceKey: string;
  /** The key's parts as a JSON array, which names the row to a preview; absent for a row whose key is incomplete. */
  keyJson?: string | null;
  deliveryKey?: string | null;
  label?: string | null;
  file?: string | null;
  row?: number | null;
  outcome: DeliveryDryRunOutcome;
  reason?: string | null;
  nextRun: DeliveryPreviewAction;
  targetId?: string | null;
  template?: string | null;
  schema?: string | null;
  schemaProblems: number;
  templateProblems: number;
  assertionFailures: number;
  missingReferences: number;
  occupied: boolean;
  addedRecords: number;
  addedProblems: number;
}

/** A rule the documents of a dry run break, how many records break it, and one example. */
export interface DeliveryDryRunRule {
  at: string;
  rule: string;
  records: number;
  exampleKey: string;
  exampleMessage: string;
  exampleValue: string;
}

/** What a schema's own rules said of the documents a dry run held to it; a reference nothing holds is counted apart. */
export interface DeliveryDryRunVerdicts {
  valid: number;
  invalid: number;
  unverified: number;
  notChecked: number;
}

/** A dry run over a flow's staged rows, which travels with the value check of the same rows. */
export interface DeliveryDryRun {
  outcomes: { rows: number; ready: number; attention: number; held: number; passedOver: number };
  nextRun: { create: number; update: number; unchanged: number; hold: number };
  template: DeliveryDryRunVerdicts;
  schema: DeliveryDryRunVerdicts;
  schemaMatch: DeliverySchemaCrossMatch;
  addedSchemas: DeliverySchemaCrossMatch[];
  legalTags: { state: "accepted" | "refused" | "notAsked" | "notChecked" | "notApplicable"; tags: string[]; why?: string | null };
  withMissingReferences: number;
  occupied: number;
  rules: DeliveryDryRunRule[];
  rulesUnlisted: number;
  records: DeliveryDryRunRecord[];
  cut: boolean;
  notes: string[];
}

/**
 * What `delivery-export-value-issues` answers: what the check found, and the workbook listing every issue, as Base64. It
 * lists fewer than it found only when they do not fit a task's result, which its About sheet says.
 */
export interface DeliveryValueIssuesExport {
  flow: string;
  interface?: string | null;
  rowsChecked: number;
  issues: number;
  listed: number;
  checkedUtc: string;
  workbook: string;
}

/**
 * What a variable came to in a row that did not give it a value the template accepts, that the mapping means no value for,
 * or (`asserted`) whose value fails an assertion of the mapping.
 */
export type ValueCheckOutcome = "held" | "invalid" | "restricted" | "empty" | "notApplicable" | "asserted";

/** A variable's rows (or items) by what they came to. */
export interface DeliveryValueCheckCounts {
  valid: number;
  invalid: number;
  empty: number;
  /**
   * Rows (or items) a node marked `$restrictWhenMissing` gave no value: the record goes out restricted until complete, without
   * it. Absent from an older node.
   */
  restricted?: number;
  notApplicable: number;
  held: number;
  /**
   * Rows (or items) whose value failed an assertion of the mapping, counted apart from the others: a value that fails an
   * assertion is still one the template accepts, or not.
   */
  asserted: number;
  total: number;
}

/** One record a check names: its key, where its row came from, and what the variable came to there. */
export interface DeliveryValueCheckSample {
  sourceKey: string;
  /** The record's key as the source read it, a JSON array of its parts, which names the row to a query of the source; absent from an older node. */
  keyJson?: string | null;
  label?: string | null;
  deliveryKey?: string | null;
  file?: string | null;
  row?: number | null;
  /** For an item of a repeated array, the child row it was written from, from 1. */
  item?: number | null;
  value?: string | null;
  message?: string | null;
  /** The source columns to fix, as the mapping names them (`dataset.curves.curve_unit`); absent when the check cannot say which. */
  columns?: string[] | null;
}

/** A value and how many times it was met. */
export interface DeliveryValueCheckValue {
  value: string;
  count: number;
}

/** One reason a variable is not written with a value the template accepts, and every occurrence of it. */
export interface DeliveryValueCheckFinding {
  outcome: ValueCheckOutcome;
  /** The variable the reason is about: the one checked, or a property inside the value written there. */
  at: string;
  /** For an invalid value, the rule of the template it breaks; for an asserted one, the assertion it fails. */
  rule?: string | null;
  /** For an asserted value, what a failure of the assertion does. */
  onFail?: AssertionAction | null;
  message: string;
  /** Occurrences: once per row, or once per item of a repeated array. */
  count: number;
  rows: number;
  values: DeliveryValueCheckValue[];
  otherValues: number;
  samples: DeliveryValueCheckSample[];
  /** How many occurrences were passed over before the examples were taken. */
  samplesFrom: number;
  /** What it comes from: usually one; a `$coalesce` none of whose alternatives gave a value has one per alternative. Absent from an older node. */
  causes?: DeliveryValueCheckCause[];
  /** What it means in plain words, and what to do about it; code is set in backticks. Absent from an older node. */
  explanation?: DeliveryValueCheckExplanation | null;
  /** What the source columns behind the first cause hold in its rows, as the source holds them, the most frequent first. */
  sourceValues?: DeliveryValueCheckSourceValue[];
  /** Occurrences whose source value is not among `sourceValues`. */
  otherSourceValues?: number;
}

/** What a finding comes from (osdu/docs/reference/concepts/value-proof.md). */
export type ValueCheckCauseKind =
  | "empty" | "idNotHeld" | "noMatch" | "ambiguous" | "noType" | "fieldEmpty" | "notFound" | "rule" | "assertion" | "other";

/** Why a finding's rows are not written with a value the template accepts, in parts a person and a query can act on. */
export interface DeliveryValueCheckCause {
  kind: ValueCheckCauseKind;
  /** The source columns the value came from, as the mapping names them. */
  columns: string[];
  /** The cached types looked in; for a search, the search the mapping declares. */
  types: string[];
  /** For an id, the entity type it names; for a search, the kind searched. */
  entityType?: string | null;
  /** The fields the value was compared with, in the order tried. */
  fields: string[];
  ignoreSeparators: boolean;
  /** Whether values were compared together, on a findBy line written with `and`: a record had to hold every one. */
  together?: boolean;
  /** For several records answering to a value, or a record found holding nothing at the field read, their ids. */
  candidates: string[];
  /** For a rule, the rule of the template; for an assertion, the assertion. */
  rule?: string | null;
}

/** What a finding means and what to do about it, the same words the workbook and the command line use. */
export interface DeliveryValueCheckExplanation {
  headline: string;
  effect: string;
  /** Where it was looked for and what the source holds, when that adds something. */
  detail?: string | null;
  /** What can be done about it, the likeliest first. */
  fixes: string[];
}

/** A value a source column holds in the rows a finding occurs in, and how many occurrences. */
export interface DeliveryValueCheckSourceValue {
  column: string;
  value: string;
  count: number;
}

/** What one statement of a proof read: its columns, its rows as text, and whether there are more. */
export interface DeliveryProofRows {
  columns: string[];
  rows: (string | null)[][];
  more: boolean;
}

/** One step of a finding's proof: what it shows, where it runs, the statement, and what it read when run. */
export interface DeliveryValueProofStep {
  step: "values" | "records" | "cache" | "search";
  title: string;
  /** How to read what it answers. */
  proves: string;
  runsOn: "source" | "catalog" | "platform";
  /** What it reads there, by name. */
  where: string;
  language: "sql" | "json";
  /** The statement as a person copies it: T-SQL with its values declared, or a search request. */
  statement: string;
  runnable: boolean;
  result?: DeliveryProofRows | null;
}

/** The proof of a finding (osdu/docs/reference/concepts/value-proof.md): the steps that show it from the data. */
export interface DeliveryValueProof {
  flow: string;
  interface?: string | null;
  mapping: string;
  target: string;
  steps: DeliveryValueProofStep[];
  notes: string[];
}

/** What a proof is asked for: the finding as the check answered it, and the step to run. */
export interface DeliveryValueProofRequest {
  target: string;
  values?: Record<string, RunValue>;
  cause: DeliveryValueCheckCause;
  sourceValues?: { column: string; value: string }[];
  looked?: string[];
  cacheVersion?: string | null;
  mapping?: string;
  run?: "values" | "records" | "cache";
  limit?: number;
}

/** A source column an attribute reads, as the mapping names it (`dataset.curves.curve_unit`), and the table holding it. */
export interface DeliverySourceSqlRead {
  column: string;
  table: string;
}

/** The T-SQL a person runs in SSMS to see what an attribute of a mapping reads. */
export interface DeliverySourceSql {
  flow: string;
  interface?: string | null;
  mapping: string;
  target?: string | null;
  /** How many entries of the mapping reach the attribute; none means nothing of the mapping fills it. */
  entries: number;
  reads: DeliverySourceSqlRead[];
  /** The columns the entries read that the tables do not hold, which the script leaves out. */
  missing: string[];
  /** The script; absent when the entries read no source column. */
  sql?: string | null;
  /** How many records the script is narrowed to; 0 for every row of the scope. */
  keys: number;
}

/** One variable checked: its rows by outcome, the values it was written with, and its findings. */
export interface DeliveryValueCheckVariable {
  target: string;
  /** The source columns the entry filling it reads, as the mapping names them (`dataset.curves.curve_unit`); absent from an older node. */
  columns?: string[];
  /** Where the mapping writes its entry; absent for a variable of data the schema requires that no entry fills directly. */
  entry?: string | null;
  required: boolean;
  /** For an entry of a repeated item, the array its items are written into. */
  repeater?: string | null;
  rows: DeliveryValueCheckCounts;
  items?: DeliveryValueCheckCounts | null;
  values: DeliveryValueCheckValue[];
  distinctValues: number;
  moreValues: boolean;
  findings: DeliveryValueCheckFinding[];
  unlisted: number;
}

/** The rows a check read, and what became of them as records. */
export interface DeliveryValueCheckRows {
  scopeRecords?: number | null;
  read: number;
  checked: number;
  complete: boolean;
  passedOver: number;
  passedOverWhy: { reason: string; count: number; samples: DeliveryValueCheckSample[] }[];
  keyless: number;
  keylessSamples: DeliveryValueCheckSample[];
  clean: number;
  withHeld: number;
  withInvalid: number;
  withEmpty: number;
  /** Rows a variable checked restricts: their record goes out restricted until complete. Absent from an older node. */
  withRestricted?: number;
  /** Rows whose record fails an assertion of the mapping on a variable checked, whatever the failure does. */
  withFailedAssertion: number;
  /** Of those, the rows whose record a failed assertion would hold at the check before sending. */
  heldByAssertion: number;
}

/**
 * The rows of a flow's scope that will not give the variables of its mapping the values the template expects: the
 * `delivery-check-values` task's result. Counts are exact; what is listed is bounded.
 */
export interface DeliveryValueCheck {
  flow: string;
  interface?: string | null;
  flowId: string;
  inputs: { mapping: string; kind: string; templateVersion: string; cachePartition?: string | null; cacheVersion?: string | null };
  asked: { targets: string[]; maxRows: number; samples: number; skipSamples: number; values: Record<string, RunValue> };
  rows: DeliveryValueCheckRows;
  variables: DeliveryValueCheckVariable[];
  issues: string[];
  notes: string[];
  /** What a dry run of the same rows came to; absent when none was asked for. */
  dryRun?: DeliveryDryRun | null;
  startedUtc: string;
  checkedUtc: string;
}
