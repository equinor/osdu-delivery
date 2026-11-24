// A flow's submissions as the delivery API answers them: each submission, its work batches and what sent the records it
// delivered, what a run id leads to, and a retrieval flow's retrievals.

import type { DeliveryTriggerKind } from "./attempts";

export type DeliverySubmissionStatus = "received" | "planned" | "running" | "completed" | "failed";

/**
 * What a submission read from the ingestion tables: the rows changed in a window after the scope's watermark
 * (`incremental`), every row of the scope (`full`), or the keys a record-scoped or replanned run named (`keys`).
 */
export type DeliverySubmissionKind = "incremental" | "full" | "keys";

/** One submission as the ledger received it and what became of it. */
export interface DeliverySubmission {
  submissionId: string;
  flowId: string;
  flowName: string;
  mappingReference: string;
  renderContext: string;
  parametersJson: string;
  recordCount: number;
  status: DeliverySubmissionStatus;
  receivedUtc: string;
  startedUtc: string | null;
  completedUtc: string | null;
  planned: number;
  skippedUnchanged: number;
  /** Records a cache change waiting for approval holds back: rendered and ready, not sent until it is decided. */
  awaitingApproval: number;
  /** Records the submission carried in a version older than the one delivered or queued: skipped, never sent. */
  skippedStale: number;
  /** Records whose queued document OSDU already held when the worker came to send it: nothing was sent. */
  unchangedAtPush: number;
  blocked: number;
  delivered: number;
  held: number;
  failed: number;
  /** Records of the submission still waiting, when it closed, for a record they refer to; they go out once it lands. */
  waiting: number;
  error: string | null;
  /** Where the intake wrote the work batches (the rendered documents the drains read). */
  workLocation: string | null;
  batchCount: number;
  /** How many key slices the intake cut the submission into for its fan-out members; 0 when it fanned out none. */
  slices: number;
  /** Which selection the plan read from the ingestion tables. */
  kind: DeliverySubmissionKind;
  /** Rows the plan read that carried no complete record key, so nothing could be delivered under them. */
  untracked: number;
  /** The connection reference of the ingestion database as the flow declares it; never a resolved secret. */
  sourceConnection: string;
  /** The three-part name of the ingestion table the records were read from. */
  sourceObject: string;
  /** The change window the plan covered, on the ingestion tables' updated column; null for a plan with no window. */
  windowFromUtc: string | null;
  windowToUtc: string | null;
  /** What else bounded the read: the child dataset objects, the overlap, the scope values, the slice bounds, the key count. */
  sourceWindow: Record<string, unknown> | null;
  /** The run that coordinated the submission. */
  runId: string | null;
  /** The partition the submission's ledger is kept under. */
  partition?: string | null;
}

/** One work batch of a submission: a file of rendered documents and how far its drain got. */
export interface DeliveryWorkBatch {
  submissionId: string;
  index: number;
  location: string;
  recordCount: number;
  status: "queued" | "running" | "done" | "failed";
  leaseOwner: string | null;
  leaseExpiresUtc: string | null;
  runId: string | null;
  createdUtc: string;
  startedUtc: string | null;
  completedUtc: string | null;
  delivered: number;
  held: number;
  failed: number;
  retrying: number;
  /** Records the batch's claim found waiting for a record they refer to, and did not send. */
  waiting: number;
  error: string | null;
}

export interface DeliverySubmissionDetail {
  submission: DeliverySubmission;
  pipelineId: string | null;
  /** The runs that carried or re-ran this submission, newest first. */
  runIds: string[];
  /** The interface of a source the submission belongs to; null for a flow in the single form. */
  interface?: string | null;
  /** The partition the submission delivered to, for a flow that names its partitions; null for one that names none. */
  partition?: string | null;
  /**
   * The records the submission delivered, counted by what sent them: under each trigger kind their reasons name (a record
   * under every kind its reason names), and those delivered before the ledger kept reasons.
   */
  deliveredBy?: DeliverySubmissionTriggers | null;
}

/**
 * What a run id or a correlation id leads to, as far as the GUI reads it: the records the run's execution (the run, and the
 * parts it split its delivery into) delivered, counted by what sent them.
 */
export interface DeliveryRunLookup {
  id: string;
  deliveredBy?: DeliverySubmissionTriggers | null;
}

/** A submission's delivered records counted by trigger kind. */
export interface DeliverySubmissionTriggers {
  delivered: number;
  notRecorded: number;
  triggers: { kind: DeliveryTriggerKind; records: number }[];
}

/** One run of an execution, as a lookup by run id or correlation id answers it (DeliveryRunLookupRunDto). */
export interface DeliveryRunLookupRun {
  runId: string;
  pipelineId: string;
  flowName: string;
  flowKind: string;
  status: string;
  operation: string | null;
  requestedBy: string | null;
  triggerSource: string | null;
  enqueuedUtc: string | null;
  startUtc: string | null;
  endUtc: string | null;
  durationSeconds: number | null;
  error: string | null;
  /** What it did, in a few words; null when nothing is known. */
  outcome: string | null;
  /** The run that coordinates it, when it is a part of a split delivery. */
  fanOutRoot: string | null;
  /** Which part it is, from 1. */
  fanOutSlot: number | null;
  /** How many parts the delivery was split into. */
  fanOutCount: number | null;
}

/** What a run hit of the search carries: the run, or why no run has the id (DeliveryRunHit). */
export interface DeliveryRunHit {
  run: DeliveryRunLookupRun | null;
  /** Whether this is the run the search named, rather than another run of its execution. */
  asked: boolean;
  reason: string | null;
}

/** One retrieval run of a retrieval flow: the window it covered, where its files went, and its outcome. */
export interface DeliveryRetrieval {
  retrievalId: number;
  flowId: string;
  flowName: string;
  runId: string | null;
  actor: string;
  kinds: string;
  query: string | null;
  windowField: string | null;
  windowFrom: string | null;
  windowTo: string | null;
  location: string;
  manifestLocation: string | null;
  status: "running" | "done" | "failed" | "cancelled";
  records: number;
  files: number;
  bytes: number;
  startedUtc: string;
  completedUtc: string | null;
  error: string | null;
}
