// The audit trail as the delivery API answers it: each operator action, the flows it can be narrowed to, and its query.

import type { PageQuery } from "@/api/endpoints";

/**
 * A flow the audit trail can be narrowed to: a ledger with activity on the trail, named as its ledger's directory names it.
 * `kind` is the ledger's (`delivery`, `dimension`); `interface` is null for a flow in the single form.
 */
export interface DeliveryActivityFlow {
  flowId: string;
  flowName: string;
  kind: string;
  interface: string | null;
  partition: string | null;
}

/** One entry of the audit trail: who did what, when, with which inputs, and how it ended. */
export interface DeliveryActivity {
  activityId: number;
  flowId: string;
  flowName: string;
  kind: string;
  actor: string;
  startedUtc: string;
  completedUtc: string | null;
  outcome: "running" | "completed" | "failed" | "cancelled";
  parametersJson: string | null;
  submissionId: string | null;
  deliveryKey: string | null;
  runId: string | null;
  summary: string | null;
  /** The captured log; only the detail endpoint fills it. */
  log: string | null;
  /** The partition the activity's ledger is kept under. */
  partition?: string | null;
  /** A run that completed having changed nothing: it planned, held and sent no record. An intervention never is. */
  idle: boolean;
  /** Read on its own (not in a listing): the pipeline of the activity's ledger, so what it did can be acted on from it. */
  pipelineId?: string | null;
  /** Read on its own: the interface of the source the ledger is, null for a flow in the single form. */
  interface?: string | null;
  /** Read on its own: the partition a request about the flow names, null for a flow whose partition is its header's. */
  namedPartition?: string | null;
}

export interface DeliveryActivityListQuery extends PageQuery {
  /** One flow, by its pipeline; or by `flowId`, never both. */
  pipelineId?: string;
  /** One flow, by the ledger identity a choice of `activityFlows` carries. */
  flowId?: string;
  /** Which interface of that source; required when it delivers more than one. */
  interface?: string;
  /**
   * Which partition of that flow, required when it names its partitions; with no flow, the partition whose trail is read,
   * the workbench's when left out.
   */
  partition?: string;
  submissionId?: string;
  runId?: string;
  kind?: string;
  actor?: string;
  outcome?: string;
  /** False leaves out the runs that changed nothing, true lists only them (its total is how many were left out). */
  idle?: boolean;
  since?: string;
  until?: string;
}
