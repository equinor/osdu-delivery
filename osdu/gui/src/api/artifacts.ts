// What deliveries created in OSDU, as the ledger names it (osdu/docs/reference/concepts/removal-and-reversal.md, Unfinished
// deliveries and their undo): a record's artifacts, and a flow's open undos with the records that hold them. Same conventions
// as delivery.ts: one function per endpoint, pages compose them with TanStack Query. The undo itself is the flow's `undo`
// operation, queued through the platform's one run dialog.

import { get } from "@/api/client";
import type { PagedResult } from "@/api/types";
import { flowPath, type DeliveryFlowScope, type DeliveryRecordRef } from "./delivery";

/**
 * Where an artifact stands: `intent` (the call that creates it is about to go), `pending` (created by a delivery not ended),
 * `live` (its delivery committed), `superseded` (a later delivery replaced it; it stays in OSDU), `due` (its delivery
 * aborted and the undo has not run), `removed`, `restored`, `gone` (OSDU no longer held it), `kept` (no call removes it)
 * or `failed` (the undo was refused or could not reach OSDU).
 */
export type ArtifactState =
  | "intent" | "pending" | "live" | "superseded" | "due" | "removed" | "restored" | "gone" | "kept" | "failed";

export const ARTIFACT_STATES: readonly ArtifactState[] = [
  "intent", "pending", "live", "superseded", "due", "removed", "restored", "gone", "kept", "failed",
];

/** One artifact of a record, newest first in a listing. */
export interface DeliveryArtifact {
  artifactId: number;
  /** The unit of work (one delivery of the record's pending work, across its tries) that created it, and when it began. */
  unitId: string;
  unitStartedUtc: string;
  /** The route's name for it within its unit: `record`, `dataset:0`, `session`. */
  slot: string;
  /** record, version, dataset, content, output, dataspace, session, lock, rows, points, objects or run. */
  role: string;
  targetId: string | null;
  /** What finds it when its id is not known, or what else names it (a landing-zone path). */
  locator: string | null;
  /** The version the delivery wrote, and the one it replaced. */
  version: number | null;
  priorVersion: number | null;
  state: ArtifactState;
  /** An undo may still take it. */
  open: boolean;
  /** Its undo failed as often as the sweep tries: only an undo run with force takes it now. */
  exhausted: boolean;
  /** What the route or the undo had to say about it, redacted. */
  note: string | null;
  undoAttempts: number;
  nextUndoUtc: string | null;
  submissionId: string | null;
  createdRunId: string | null;
  createdUtc: string;
  updatedUtc: string;
  /** When an undo settled it, the run it settled in, and who ran it (the worker, the sweep, or the operator of a removal). */
  settledUtc: string | null;
  settledRunId: string | null;
  settledBy: string | null;
}

/** A ledger's open artifacts, or a source's added up, by state; `toUndo` is due and failed together. */
export interface DeliveryArtifactCounts {
  intent: number;
  pending: number;
  due: number;
  failed: number;
  /** Of the failed, those tried as often as the sweep tries: left for an undo run with force. */
  failedExhausted: number;
  toUndo: number;
}

/** One interface of a source, its ledger, and its open artifacts. */
export interface DeliveryInterfaceUndos {
  interface: string | null;
  flowId: string;
  counts: DeliveryArtifactCounts;
}

/** One record holding open artifacts: the record as the ledger holds it (null fields when it holds none), and its counts. */
export interface DeliveryOpenUndoRecord {
  flowId: string;
  deliveryKey: string;
  sourceKey: string | null;
  label: string | null;
  targetId: string | null;
  status: string | null;
  intent: number;
  pending: number;
  due: number;
  failed: number;
  failedExhausted: number;
  /** When the oldest of its open artifacts was written. */
  oldestUtc: string;
  /** When the sweep next tries a failed one; null when none waits for a retry. */
  nextUndoUtc: string | null;
}

/**
 * A flow's open undos in a partition: counted for the source and per interface, with a page of the records of one interface
 * (`interface`, or the only one); `records` is null for a source of several interfaces asked about none.
 */
export interface DeliveryOpenUndos {
  counts: DeliveryArtifactCounts;
  interfaces: DeliveryInterfaceUndos[];
  interface: string | null;
  flowId: string | null;
  records: PagedResult<DeliveryOpenUndoRecord> | null;
}

/**
 * What an undo took before a removal or the deletion of a ledger, as their results report it: the records it took up, and
 * what became of their artifacts.
 */
export interface UndoneCounts {
  records: number;
  removed: number;
  restored: number;
  gone: number;
  kept: number;
  superseded: number;
  failed: number;
}

/**
 * One enrichment of a record as its ledger row holds it (osdu/docs/reference/flow/enrich.md, What the ledger keeps): what it
 * adds, the enrichment mapping that rendered it, the kind of the records it adds and how they are written, whether it was set
 * aside on its holds on the record's last render, where its rows came from, and every record it added.
 */
export interface DeliveryRecordEnrichment {
  name: string;
  /** What it adds to the record, in this order: paths, records, bulk, workflow. */
  adds: ("paths" | "records" | "bulk" | "workflow")[];
  mapping: string | null;
  mappingFingerprint: string | null;
  /** The kind of the records it adds, and how they are written: `dataset` with their files, or `storage`. */
  recordKind: string | null;
  writtenThrough: "dataset" | "storage" | null;
  setAside: boolean;
  /** Where the rows of the delivered document's enrichment came from; null for one reading no rows, or not delivered yet. */
  rows: DeliveryEnrichmentRows | null;
  records: DeliveryEnrichmentRecord[];
}

/** How many rows the enrichment read, the ingestion files they came from (at most twenty of `filesCounted`), and for one row where it is (`file#row`). */
export interface DeliveryEnrichmentRows {
  count: number;
  files: string[];
  filesCounted: number;
  row: string | null;
}

/**
 * One record an enrichment added: its OSDU id, whether the record links it now (one it no longer links stays in OSDU), the
 * version the ledger last saw land, the hashes of what was written and of its files, the row it came from, and every write.
 */
export interface DeliveryEnrichmentRecord {
  id: string;
  linked: boolean;
  version: number | null;
  hash: string | null;
  filesHash: string | null;
  source: string | null;
  writes: DeliveryEnrichmentWrite[];
}

/**
 * One write a delivery made of a record an enrichment added, as the record's attempt holds it: the attempt, when the write
 * went, the run, how the attempt ended (a write that landed stays in OSDU however the record's own write went), the version it
 * landed as, and the version it replaced (null when it created the record).
 */
export interface DeliveryEnrichmentWrite {
  attemptId: number;
  atUtc: string;
  runId: string | null;
  outcome: string;
  version: number | null;
  priorVersion: number | null;
}

export const artifactApi = {
  /** A record's artifacts, newest first; a record deleted from the ledger keeps its own. */
  recordArtifacts: ({ flowId, deliveryKey }: DeliveryRecordRef, max?: number) =>
    get<DeliveryArtifact[]>(
      `/api/v1/delivery/records/${encodeURIComponent(flowId)}/${encodeURIComponent(deliveryKey)}/artifacts`, max ? { max } : {}),
  /** What the mapping's enrichments added to a record, in the order they render, read from its ledger row. */
  recordEnrichments: ({ flowId, deliveryKey }: DeliveryRecordRef) =>
    get<DeliveryRecordEnrichment[]>(
      `/api/v1/delivery/records/${encodeURIComponent(flowId)}/${encodeURIComponent(deliveryKey)}/enrichments`),
  /** A flow's open undos in the partition the scope names, with a page of the records of its interface. */
  openUndos: (pipelineId: string, scope: DeliveryFlowScope | undefined, page = 1, pageSize = 50) =>
    get<DeliveryOpenUndos>(flowPath(pipelineId, "/undos", scope), { page, pageSize }),
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function count(value: Record<string, unknown>, field: string): number {
  const found = value[field];
  return typeof found === "number" && Number.isFinite(found) ? found : 0;
}

/** The undo counts an object holds, or null when it is not one. */
function countsOf(value: unknown): UndoneCounts | null {
  if (!isRecord(value) || typeof value.records !== "number") {
    return null;
  }

  return {
    records: count(value, "records"),
    removed: count(value, "removed"),
    restored: count(value, "restored"),
    gone: count(value, "gone"),
    kept: count(value, "kept"),
    superseded: count(value, "superseded"),
    failed: count(value, "failed"),
  };
}

/** The undo counts an object carries under `undone`, or null when it carries none. */
function undoneOf(value: unknown): UndoneCounts | null {
  return isRecord(value) ? countsOf(value.undone) : null;
}

/**
 * What a removal's or a ledger deletion's result says was undone first: its own `undone`, or for a run of a source of several
 * interfaces, every interface's added up. Null when the result says nothing of it.
 */
export function undoneFirst(result: unknown): UndoneCounts | null {
  return undoneOf(result) ?? acrossInterfaces(result, undoneOf);
}

/** What an undo run's result says it did: its own counts, or for a run of a source of several interfaces, each one's added up. */
export function undoRunCounts(result: unknown): UndoneCounts | null {
  return countsOf(result) ?? acrossInterfaces(result, countsOf);
}

/** The counts each interface of a source run's result reports, added up; null when none reports any. */
function acrossInterfaces(result: unknown, read: (value: unknown) => UndoneCounts | null): UndoneCounts | null {
  const interfaces = isRecord(result) && Array.isArray(result.interfaces) ? result.interfaces : [];
  const each = interfaces.map((entry) => read(isRecord(entry) ? entry.result : null)).filter((found): found is UndoneCounts => found !== null);
  if (each.length === 0) {
    return null;
  }

  return each.reduce((sum, next) => ({
    records: sum.records + next.records,
    removed: sum.removed + next.removed,
    restored: sum.restored + next.restored,
    gone: sum.gone + next.gone,
    kept: sum.kept + next.kept,
    superseded: sum.superseded + next.superseded,
    failed: sum.failed + next.failed,
  }));
}
