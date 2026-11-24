// Reversals of a run or a submission through the delivery API: what one would reach, the reversal with its records, and
// whether a source has anything to reverse.

import type { RunStatus } from "@/api/types";
import type { DeliveryTarget } from "./removal";

/** What a reversal reverses: one run, or one submission. */
export type ReversalSourceKind = "run" | "submission";

/** A reversal's source, as a page names it. */
export interface ReversalSource {
  kind: ReversalSourceKind;
  id: string;
}

/**
 * A reversal of one run or one submission (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a run or a
 * submission). `expected` is how many records the preview said the source delivered: the API refuses the request when the
 * source reaches another number by then.
 */
export interface DeliveryReversalRequest {
  runId?: string;
  submissionId?: string;
  expected?: number;
}

/** What a reversal can do on the flow's route: the calls a restore and a removal make, or why the route cannot, in brackets. */
export interface DeliveryReversalRoute {
  protocol: string;
  restores: boolean;
  restore: string;
  removes: boolean;
  remove: string;
}

/**
 * A reversal's state (`capturing`, `reversing`, `completed`, `failed`, `cancelled`), who asked, its latest run, and its
 * records by state and outcome (absent in a listing, which does not count them).
 */
export interface DeliveryReversal {
  reversalId: number;
  flowId: string;
  flowName: string;
  partition: string | null;
  source: ReversalSourceKind;
  sourceId: string;
  submissions: number;
  status: "capturing" | "reversing" | "completed" | "failed" | "cancelled";
  requestedBy: string;
  requestedUtc: string;
  capturedUtc: string | null;
  startedUtc: string | null;
  completedUtc: string | null;
  lastRunId: string | null;
  error: string | null;
  records?: number | null;
  states?: Record<string, number> | null;
  outcomes?: Record<string, number> | null;
}

/**
 * What reversing a source would reach, nothing written: how many records it delivered under how many submissions, and for
 * a sample of them (all of them when `sampleIsAll`) what the reversal would do, decided as the run decides each record.
 */
export interface DeliveryReversalPreview {
  source: ReversalSourceKind;
  sourceId: string;
  submissions: number;
  records: number;
  sampled: number;
  sampleIsAll: boolean;
  restore: number;
  remove: number;
  resolvedFromOsdu: number;
  passedOver: Record<string, number>;
  route: DeliveryReversalRoute;
  target: DeliveryTarget;
  existing?: DeliveryReversal | null;
}

/** A reversal with its pipeline and interface, the submissions it covers, and the runs that worked on it. */
export interface DeliveryReversalDetail {
  reversal: DeliveryReversal;
  pipelineId: string | null;
  interface: string | null;
  submissionIds: string[];
  runIds: string[];
}

/** One record of a reversal: what its source left, what OSDU held before, and what came of it. */
export interface DeliveryReversalItem {
  deliveryKey: string;
  targetId: string | null;
  state: "pending" | "sending" | "done" | "skipped" | "failed";
  outcome: string | null;
  detail: string | null;
  runVersion: number | null;
  prior: "version" | "none" | "unknown";
  priorVersion: number | null;
  restoredVersion: number | null;
  newVersion: number | null;
  runId: string | null;
  updatedUtc: string;
  sourceKey: string | null;
  label: string | null;
}

/** A page of a reversal's records in key order, and the key the next page starts after. */
export interface DeliveryReversalItemPage {
  items: DeliveryReversalItem[];
  next?: string | null;
}

/**
 * Whether a run or a submission has anything to reverse now, by the answer a request and the reverse run go by: the action is
 * offered exactly when `reversible`. `resumes` says the request would resume the reversal that exists; `reason` says why there
 * is nothing (it delivered nothing, its reversal settled every record, or a reverse run of it is queued or running).
 */
export interface DeliveryReversible {
  source: ReversalSourceKind;
  sourceId: string;
  reversible: boolean;
  resumes: boolean;
  reason?: string | null;
  reversal?: DeliveryReversal | null;
}

/** The reverse run a request queued, with how many records its source delivered. */
export interface DeliveryReversalAccepted {
  runId: string;
  status: RunStatus;
  source: ReversalSourceKind;
  sourceId: string;
  records: number;
}
