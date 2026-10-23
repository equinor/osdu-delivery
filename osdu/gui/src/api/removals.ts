// Removal flows (osdu/docs/reference/flow/removal.md): a removal flow reads the keys a source holds now, and a source's list of
// removed keys, compares them with the ledgers of the delivery flows it names, and takes out of OSDU the records the source no
// longer holds. What it finds is the delivery ledger's deletions, one per key, worked through incrementally: a deletion taken out
// is never asked of OSDU again. Same conventions as delivery.ts: one function per endpoint, pages compose them with TanStack
// Query. An answer of the removal endpoints leaves out what holds no value, so every field that may hold none is optional
// here, and a page tests it with `=== undefined` or `??`, never `=== null`.

import { get, post } from "@/api/client";
import { flowPath, type DeliveryFlowScope } from "./delivery";

/** Where a deletion stands: waiting for an operator's approval, waiting to be taken out, or done. */
export type RemovedKeyState = "pending" | "awaiting-approval" | "removed";

/** Every state, in the order a page lists them. */
export const REMOVED_KEY_STATES: readonly RemovedKeyState[] = ["pending", "awaiting-approval", "removed"];

/** Why a key counts as removed: it is not in the source's current keys, or the source lists it as removed. */
export type RemovalReason = "absent" | "listed";

/**
 * What the last try to take a deletion out came to. The first four finish it (its state is then `removed`); the others leave
 * it pending for the next run.
 */
export type RemovalOutcome = "removed" | "already-gone" | "settled" | "kept-out" | "referenced" | "shared" | "busy" | "failed";

/** Every outcome, in the order a page lists them. */
export const REMOVAL_OUTCOMES: readonly RemovalOutcome[] = ["removed", "already-gone", "settled", "kept-out", "referenced", "shared", "busy", "failed"];

/** The outcomes that finish a deletion. */
export const FINISHING_OUTCOMES: readonly RemovalOutcome[] = ["removed", "already-gone", "settled", "kept-out"];

/** How a removal run ended: still running, completed, its guard held what it found for approval, or it stopped on an error. */
export type RemovalRunStatus = "running" | "completed" | "guarded" | "failed";

/** What one removal run read, found and came to. */
export interface RemovalTally {
  rowsRead: number;
  sourceKeys: number;
  listedKeys: number;
  ingestionKeys: number;
  compared: number;
  detected: number;
  awaitingApproval: number;
  approved: number;
  removed: number;
  alreadyGone: number;
  settled: number;
  keptOut: number;
  referenced: number;
  shared: number;
  busy: number;
  failed: number;
  cameBack: number;
  lifted: number;
}

/** One run of one removal of a removal flow, as the ledger of the delivery flow it wrote to keeps it. */
export interface RemovalRun {
  partition: string;
  /** Its number in the partition. */
  removalRunId: number;
  flowName: string;
  entry: string;
  ledgerFlowId: string;
  deliveryFlow: string;
  /** The platform run that carried it. */
  runId?: string;
  /** `remove` or `plan`. */
  operation: string;
  actor: string;
  status: RemovalRunStatus | string;
  startedUtc: string;
  completedUtc?: string;
  /** When the read of the source's current keys began: the moment the keys it lacked count as removed as of. */
  sourceReadUtc?: string;
  tally: RemovalTally;
  guardPercent: number;
  guardRecords?: number;
  approvedBy?: string;
  activityId?: number;
  error?: string;
}

/** One of a delivery ledger's deletions: a key its source removed, and how far taking it out has come. */
export interface RemovedKey {
  deliveryKey: string;
  /** The key tuple, a JSON array of the key columns' values in the record key's order. */
  sourceKeyJson?: string;
  reason: RemovalReason | string;
  /** The moment the source removed the key: when the read that lacked it began, or the moment its list gives. */
  removedUtc: string;
  /** The file (or table) and row that said so. */
  fileName?: string;
  rowNumber?: number;
  state: RemovedKeyState | string;
  outcome?: RemovalOutcome | string;
  /** What the last try was told, or why it left the deletion pending. */
  detail?: string;
  tries: number;
  removalFlow: string;
  entry: string;
  /** The removal run that found it. */
  detectedRunId: number;
  /** The removal run that finished it. */
  doneRunId?: number;
  runId?: string;
  approvedBy?: string;
  approvedUtc?: string;
  createdUtc: string;
  doneUtc?: string;
  /** The status of the ledger's record of the key; absent when the ledger holds none (a key only the record table held). */
  recordStatus?: string;
}

/** A page of a ledger's deletions in key order, the key to ask for the next page with, and how many there are in each state. */
export interface RemovedKeysPage {
  keys: RemovedKey[];
  next?: string;
  counts: Partial<Record<RemovedKeyState, number>>;
}

/** One column a removal reads a key column from: the source column, the key column it fills, and the expression over it. */
export interface RemovalColumn {
  name: string;
  as?: string;
  expr?: string;
}

/** Where a removal reads keys from, as its document declares it. */
export interface RemovalSource {
  /** `files` read by a SQLFlow reader, or a `table` of a source database. */
  reads: "files" | "table" | string;
  /** The reader (`csv`, `parquet`, ...) or the source system (`mssql`, `postgresql`, ...). */
  type: string;
  /** The files' location, or the table's name. */
  where?: string;
  /** The rows of a table read, as a predicate with its leading AND. */
  filter?: string;
  /** The columns the key is read from; none reads each key column by its name. */
  columns: RemovalColumn[];
  /** For a source of removed keys: the column holding the moment each key was removed. */
  removedAt?: RemovalColumn;
}

/** One removal of a removal flow as its page shows it. */
export interface RemovalEntry {
  name: string;
  /** The delivery flow whose ledger it compares and writes to. */
  flow: string;
  interface?: string;
  /** The delivery flow's parameter values it compares under. */
  values: Record<string, string>;
  keepReferenced: boolean;
  /** The source's current, complete key set. */
  keys?: RemovalSource;
  /** The source's list of removed keys. */
  removed?: RemovalSource;
  /** The delivery flow's pipeline, when the catalog knows it in the removal flow's repository. */
  deliveryPipelineId?: string;
  /** The ledger the removal writes to in the partition; absent while the delivery flow keeps none there. */
  ledgerFlowId?: string;
  /** The deletions this removal found that wait for an operator's approval. */
  awaitingApproval: number;
  /** The ledger's deletions by state (every removal's that writes to it). */
  deletions: Partial<Record<RemovedKeyState, number>>;
  lastRun?: RemovalRun;
}

/** A removal flow in the partition: its guard and every removal it declares, or why it cannot be read there. */
export interface RemovalFlow {
  pipelineId: string;
  repoId: string;
  name: string;
  description?: string;
  /** The partition read; absent when it cannot be settled, and `problem` says why. */
  partition?: string;
  /** The partitions the flow names; empty for one that removes in every registered partition. */
  partitions: string[];
  guardPercent: number;
  guardRecords?: number;
  /** The parameters the removals' values and locations take as `{name}` tokens. */
  parameters: RemovalParameter[];
  removals: RemovalEntry[];
  problem?: string;
}

/** A parameter a removal flow declares, which a run's values fill. */
export interface RemovalParameter {
  name: string;
  required: boolean;
  default?: string;
  description?: string;
}

/** An approval of the deletions a removal's guard held: the count the operator was shown, and the partition typed back. */
export interface RemovalApprovalRequest {
  entry: string;
  expected: number;
  confirm: string;
  pool?: string;
}

/** The run an approval queued, which makes the held deletions pending and takes them out. */
export interface RemovalApprovalAccepted {
  runId: string;
  status: string;
  partition: string;
  entry: string;
  expected: number;
}

/** How many records' removals a lift lifted, and the deliver run it queued when asked to. */
export interface RemovalLiftResult {
  lifted: number;
  runId?: string | null;
}

/** The most records one lift names. */
export const MAX_LIFT = 1000;

/**
 * A deletion as a record's detail carries it, whose empty fields arrive as null rather than left out: the same deletion with
 * them left out, so a page reads it as it reads every other answer here.
 */
export function removedKeyOf(raw: object | null | undefined): RemovedKey | undefined {
  if (raw === null || raw === undefined) {
    return undefined;
  }

  return Object.fromEntries(Object.entries(raw).filter(([, value]) => value !== null)) as unknown as RemovedKey;
}

const removalsPath = "/api/v1/delivery/removals";

export const removalApi = {
  /** Every removal flow that removes in the workbench's partition, each as `flow` answers it. */
  list: () => get<RemovalFlow[]>(removalsPath),
  /** A removal flow in the workbench's partition: each removal, its ledger's deletions by state, and its last run. */
  flow: (pipelineId: string) => get<RemovalFlow>(`${removalsPath}/flows/${pipelineId}`),
  /** A removal flow's runs in a partition, newest first, of one removal when `entry` names it. */
  runs: (pipelineId: string, query: { partition?: string; entry?: string; limit?: number } = {}) =>
    get<RemovalRun[]>(`${removalsPath}/flows/${pipelineId}/runs`, query),
  /** One removal run, by its number in a partition. */
  run: (partition: string, removalRunId: number) => get<RemovalRun>(`${removalsPath}/${encodeURIComponent(partition)}/runs/${removalRunId}`),
  /** A page of a delivery ledger's deletions in key order after `after`, of one state or every one, with the counts by state. */
  keys: (ledgerFlowId: string, query: { state?: RemovedKeyState; run?: number; after?: string; limit?: number } = {}) =>
    get<RemovedKeysPage>(`${removalsPath}/ledgers/${ledgerFlowId}/keys`, query),
  /** The removal runs that wrote to a delivery ledger, newest first, whichever removal flow ran them. */
  ledgerRuns: (ledgerFlowId: string, limit?: number) =>
    get<RemovalRun[]>(`${removalsPath}/ledgers/${ledgerFlowId}/runs`, limit === undefined ? {} : { limit }),
  /** Queues the run that approves what a removal's guard held, as the caller. */
  approve: (pipelineId: string, request: RemovalApprovalRequest) =>
    post<RemovalApprovalAccepted>(`${removalsPath}/flows/${pipelineId}/approve`, request),
  /** Lifts the removals of the records named, as the caller; `run` also queues a deliver run that delivers them again at once. */
  lift: (pipelineId: string, keys: string[], scope?: DeliveryFlowScope, run = false) =>
    post<RemovalLiftResult>(flowPath(pipelineId, "/lift-removal", scope), { keys, run }),
  /** Lifts the removal of one record, as the caller; `run` also queues a deliver run scoped to it. */
  liftRecord: (flowId: string, deliveryKey: string, run = false) =>
    post<RemovalLiftResult>(`/api/v1/delivery/records/${encodeURIComponent(flowId)}/${encodeURIComponent(deliveryKey)}/lift-removal`, { run }),
};
