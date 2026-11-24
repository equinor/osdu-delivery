// A record preview as the delivery API answers it: the key asked for, the inputs, route and source rows it read, what
// the next run would decide, the document rendered with its enrichments, payload and references, its check against the
// partition's Schema service, and the staged rows a preview picks from.

import type { RunValue } from "@/api/types";
import type { AssertionFindings } from "../validation";
import type { DeliveryRecordStatus } from "./records";
import type { DeliverySchemaCrossMatch } from "./valueChecks";

/** How a record preview read the key it was given. */
export type DeliveryPreviewKeyForm = "first" | "delivery key" | "osdu id" | "source key" | "key parts";

/** What a preview was asked for and how it picked its row. */
export interface DeliveryPreviewAsked {
  key?: string | null;
  how: DeliveryPreviewKeyForm;
  keyParts?: string[] | null;
  keyColumns: string[];
  /** Rows before the first renderable one that a preview of the first record passed over (deleted, keyless, held). */
  passedOver: number;
  passedOverWhy: string[];
  /** How many records the scope holds, when the preview read the scope's first. */
  scopeRecords?: number | null;
  values: Record<string, RunValue>;
}

/** The render inputs a preview's document comes from. */
export interface DeliveryPreviewInputs {
  mapping: string;
  kind: string;
  templateVersion: string;
  cachePartition?: string | null;
  cacheVersion?: string | null;
  contextHash: string;
  /** The values the mapping's parameters render with, every reference resolved, as they reach the record. */
  parameters?: Record<string, string> | null;
}

/** The route the flow delivers by, why, and where a DDMS route sends the record. */
export interface DeliveryPreviewRoute {
  protocol: string;
  reason?: string | null;
  ddms?: string | null;
}

/** A child dataset's first rows, with how many it holds. */
export interface DeliveryPreviewRows {
  rows: Record<string, string | null>[];
  total: number;
  truncated: boolean;
}

/** The record's rows as the ingestion tables hold them now. */
export interface DeliveryPreviewSource {
  sourceKey: string;
  keyParts: (string | null)[];
  deliveryKey?: string | null;
  label?: string | null;
  identities: string[];
  originFile?: string | null;
  originRow?: number | null;
  originUpdatedUtc?: string | null;
  fingerprint?: string | null;
  deletedUtc?: string | null;
  hold?: string | null;
  row: Record<string, string | null>;
  datasets: Record<string, DeliveryPreviewRows>;
  /** Why rows were left out to keep the answer within bounds. */
  omitted?: string | null;
}

/** What the next run would do with a record. */
export type DeliveryPreviewAction = "create" | "updateMetadata" | "updatePayload" | "updateBoth" | "skip" | "hold" | "blocked";

/** The ledger's record of the previewed row, and whether the document rendered now is the one it holds. */
export interface DeliveryPreviewLedgerRecord {
  status: DeliveryRecordStatus;
  blocked: boolean;
  targetId?: string | null;
  targetVersion?: number | null;
  lastDeliveredUtc?: string | null;
  lastError?: string | null;
  metadataHash?: string | null;
  sameDocument?: boolean | null;
}

export interface DeliveryPreviewDecision {
  action: DeliveryPreviewAction;
  skipTier?: "fingerprint" | "contentHash" | "approval" | "stale" | null;
  reason: string;
  deliverMetadata: boolean;
  deliverPayload: boolean;
  ledger?: DeliveryPreviewLedgerRecord | null;
}

/** A value of the sent document the platform gives when the record is sent. */
export interface DeliveryPreviewPlaceholder {
  path: string;
  standsFor: string;
}

/** One question the render asked the platform's search, and what it found. */
export interface DeliveryPreviewSearch {
  kind: string;
  field: string;
  value: string;
  outcome: string;
  id?: string | null;
}

/** The record's document: as the mapping renders it, and as the route sends it where the route adds to it. */
export interface DeliveryPreviewDocument {
  targetId?: string | null;
  kind: string;
  metadataHash: string;
  characters: number;
  held: boolean;
  holds: string[];
  rendered?: Record<string, unknown> | null;
  /** The document as the route sends it; absent when the route sends the rendered document as it is. */
  sent?: Record<string, unknown> | null;
  placeholders: DeliveryPreviewPlaceholder[];
  /** Why the documents were left out: their size. */
  omitted?: string | null;
  searches: DeliveryPreviewSearch[];
  cacheValues: number;
  /** Which alternative each $coalesce node of the mapping took the value it wrote from. */
  choices: DeliveryPreviewChoice[];
  /** References the document carries that name no record the cache holds, written because the mapping says $unverified. */
  unverified: string[];
  /**
   * What the mapping's assertions found of the document (osdu/docs/reference/flow/mapping-assertions.md); absent when it
   * states none. A failure that holds would hold the record at the check before sending, its document kept; a value a
   * failure leaves out is already out of the rendered document.
   */
  assertions?: AssertionFindings | null;
  /**
   * What each enrichment of the flow's mapping added to the document, in the order they render
   * (osdu/docs/reference/flow/enrich.md); empty, or absent from an older control plane, for a mapping that declares none.
   */
  enrichment?: DeliveryPreviewEnrichStep[];
}

/** What one enrichment of the flow's mapping did to the previewed record. */
export interface DeliveryPreviewEnrichStep {
  /** The enrichment's name, as the mapping's enrich: declares it. */
  step: string;
  /** The enrichment mapping adding paths it rendered, Name@version; absent for one that adds records of their own. */
  mapping?: string;
  /** How many rows of its dataset the record had; absent for one that reads no rows. */
  rows?: number;
  /** Each path it set and each list it added items to, in the order it did them. */
  effects: DeliveryPreviewEnrichEffect[];
  /** The records it adds to the record, each under the id the record links; absent or empty for one that adds none. */
  records?: DeliveryPreviewEnrichRecord[];
  /** Why it holds the record, each prefixed with the enrichment. */
  holds: string[];
  /** True when it did not render: one before it holds the record, or it waits for the platform. */
  notRendered?: boolean;
  /** True when it held and was set aside: the record goes ahead with what it added to its last delivery, which `effects` lists. */
  setAside?: boolean;
  /** For one set aside, the records its last delivery added, which the record keeps linking. */
  keptRecords?: string[];
  /** What it does beside adding paths: records it adds, the record's bulk data, a workflow run over it. Absent from an older control plane. */
  supplies?: ("records" | "bulk" | "workflow")[];
}

/** One record an enrichment adds to the previewed record: its id, made from the record's id and the enrichment, its kind and its row. */
export interface DeliveryPreviewEnrichRecord {
  id: string;
  kind: string;
  /** The row of the enrichment's dataset it is rendered from, counting from zero. */
  row: number;
  /** What the enrichment's mapping rendered into it; absent when the preview is too large to show its document. */
  record?: unknown;
}

/** One thing an enrichment did to the record: set a path the record held nothing at, or add items to the list there. */
export interface DeliveryPreviewEnrichEffect {
  kind: "set" | "add";
  path: string;
  /** The value it set or the items it added; absent when the preview is too large to show its document. */
  value?: unknown;
}

/** The alternative of a $coalesce node that gave the value it wrote. */
export interface DeliveryPreviewChoice {
  /** The variable the node fills. */
  target: string;
  /** Which alternative gave it, counting from one in the order they are tried. */
  alternative: number;
  /** How many alternatives the node lists. */
  of: number;
  /** Where that alternative reads its value. */
  origin: string;
  /** How many values it gave: one, or one per item of a repeated array that took it. */
  values: number;
  /** True when a value it gave is a reference to a record the cache does not hold. */
  unverified: boolean;
}

/** The record of the ledger delivered to a referenced id. */
export interface DeliveryPreviewHolder {
  flowId: string;
  deliveryKey: string;
  sourceKey: string;
  label?: string | null;
  status: DeliveryRecordStatus;
}

export interface DeliveryPreviewReference {
  id: string;
  property: string;
  holder?: DeliveryPreviewHolder | null;
}

export interface DeliveryPreviewParquet {
  rows: number;
  columns: number;
  columnNames: string[];
  columnNamesTruncated: boolean;
}

export interface DeliveryPreviewFile {
  name: string;
  size: number;
  modifiedUtc?: string | null;
  parquet?: DeliveryPreviewParquet | null;
  footerProblem?: string | null;
}

/** One payload part of the record and the files a delivery would upload for it. */
export interface DeliveryPreviewPayloadPart {
  role?: string | null;
  payload: string;
  location?: string | null;
  files: DeliveryPreviewFile[];
  totalFiles: number;
  totalBytes: number;
  truncated: boolean;
  problem?: string | null;
}

/** One request of a delivery, in order. */
export interface DeliveryPreviewStep {
  order: number;
  service: string;
  request: string;
  what: string;
  returns?: string | null;
  repeats?: string | null;
  body?: Record<string, unknown> | null;
}

/**
 * One record of a flow rendered as a delivery would render it, with nothing sent: what a preview answers. `found` false
 * carries the reason no row was previewed (an unknown key, an empty scope).
 */
export interface DeliveryRecordPreview {
  flow: string;
  interface?: string | null;
  flowId: string;
  asked: DeliveryPreviewAsked;
  found: boolean;
  reason?: string | null;
  inputs: DeliveryPreviewInputs;
  route: DeliveryPreviewRoute;
  source?: DeliveryPreviewSource | null;
  decision?: DeliveryPreviewDecision | null;
  document?: DeliveryPreviewDocument | null;
  noDocument?: string | null;
  references: DeliveryPreviewReference[];
  referenceCount: number;
  payload: DeliveryPreviewPayloadPart[];
  steps: DeliveryPreviewStep[];
  notes: string[];
  issues: string[];
  /** The document against the partition's Schema service, when the preview was asked for that. */
  schemaCheck?: DeliveryPreviewSchemaCheck | null;
  previewedUtc: string;
}

/** A record one of a document's enrichments adds, against what the Schema service holds of its kind. */
export interface DeliveryPreviewAddedSchemaCheck {
  id: string;
  kind: string;
  row: number;
  schema: DeliverySchemaCrossMatch;
  verdict?: unknown;
}

/** One document against the partition's Schema service. */
export interface DeliveryPreviewSchemaCheck {
  schema: DeliverySchemaCrossMatch;
  verdict?: unknown;
  guidance?: unknown;
  problem?: string | null;
  templateOutcome?: string | null;
  templateProblems: number;
  added: DeliveryPreviewAddedSchemaCheck[];
}

/** One row staged in a flow's record table, as a page lists it: nothing of it is rendered. */
export interface DeliveryStagedRow {
  sourceKey: string;
  keyJson: string;
  deliveryKey?: string | null;
  label?: string | null;
  file?: string | null;
  row?: number | null;
  updatedUtc?: string | null;
  deletedUtc?: string | null;
  /** False when a part of its key is empty, so it has no record to name. */
  complete: boolean;
  children: Record<string, number>;
  /** The status of its record; null while the ledger holds none, so it is only staged. */
  ledgerStatus?: DeliveryRecordStatus | null;
}

/** A page of the rows staged in a flow's scope. */
export interface DeliveryStagedRows {
  flow: string;
  interface?: string | null;
  recordObject: string;
  keyColumns: string[];
  datasets: Record<string, string>;
  /** How many rows the scope and the search hold, on the first page. */
  total?: number | null;
  rows: DeliveryStagedRow[];
  /** Where the next page starts; absent on the last. */
  next?: string | null;
  mappingUnread?: string | null;
  readUtc: string;
  /** True when the ledger was read, so a row with no status is only staged. */
  ledgerRead?: boolean;
}
