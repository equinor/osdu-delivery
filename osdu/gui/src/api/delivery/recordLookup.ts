// Finding records through the delivery API: a flow's records listing, the lookup across every flow, what each hit
// carries, and the flows a lookup can be narrowed to.

import type { PageQuery } from "@/api/endpoints";
import type { DeliveryTriggerKind } from "./attempts";
import type { DeliveryRecordStatus } from "./records";

/**
 * A delivery record the combined search found: the `data` of each hit in the `records` category the module's search
 * contributor adds to GET /api/v1/search/all. It matches an exact delivery key, or a prefix of the record's OSDU id,
 * source key or label, across every flow.
 */
export interface DeliveryRecordHit {
  deliveryKey: string;
  flowId: string;
  flowName: string | null;
  pipelineId: string | null;
  sourceKey: string;
  label: string | null;
  targetId: string | null;
  status: string;
  lastDeliveredUtc: string | null;
  updatedUtc: string;
  /** The interface of a source the record belongs to; null for a flow in the single form. */
  interface?: string | null;
  /** The record's own values the term matched, and what each is; absent when the term was a delivery key. */
  matched?: DeliveryRecordMatch[] | null;
  /** The ingestion file and row the record's newest version came from: the queued version's while work waits. */
  sourceFileName?: string | null;
  sourceRowNumber?: number | null;
  /** The partition the record's ledger delivers to, for a flow that names its partitions. */
  partition?: string | null;
  /** Whether an operator restricted the record's access in OSDU. Absent from an older control plane. */
  restricted?: boolean;
  /** Whether OSDU holds the record restricted until complete. Absent from an older control plane. */
  restrictedUntilComplete?: boolean;
  /** What a record restricted until complete goes without, one value to an item. */
  incompleteMissing?: string[] | null;
}

/** One value of a record a lookup matched: the value as it was read, and what it is (identity, key, label, osdu, file). */
export interface DeliveryRecordMatch {
  value: string;
  kind: string;
}

export interface DeliveryRecordListQuery extends PageQuery {
  /** Which interface of the source the records are of. Required when the source delivers more than one. */
  interface?: string;
  /** Which partition's ledger the records are of. Required when the flow names its partitions. */
  partition?: string;
  /** A delivery key (exact), or a prefix over label, source key and target id. */
  search?: string;
  /** "contains" for the slower substring match; prefix by default. */
  mode?: "prefix" | "contains";
  status?: DeliveryRecordStatus;
  /** Only records this submission last planned, delivered or found unchanged; a later submission moves them on. */
  submissionId?: string;
  /** Only records this submission delivered, which stay its own however many submissions touch them afterwards. */
  deliveredBy?: string;
  /** Only records the given platform run touched, resolved through that run's attempts. */
  runId?: string;
  /** Only delivered records whose last verify found drift or a missing record. */
  drifted?: boolean;
  /** Only the blocked records one issue keeps blocked, by its id. */
  issue?: string;
  /** Only records whose last render set an enrichment aside on its hold. */
  stepHeld?: boolean;
  /** Only records an operator restricted in OSDU, or asked to: restricted by an operator. */
  restricted?: boolean;
  /** Only records OSDU holds restricted until complete. */
  incomplete?: boolean;
  /** Only records restricted until complete for one set of missing values, by its group. */
  missing?: string;
  /** Only records whose access in OSDU is restricted either way, until complete or by an operator. */
  accessRestricted?: boolean;
  /**
   * With `deliveredBy` or `runId`: only the records it delivered for one kind of trigger, as its count of that cause stands
   * for them (osdu/docs/reference/concepts/delivery-reasons.md).
   */
  trigger?: DeliveryTriggerKind;
}

/**
 * What the Records page asks the ledger for: a term over every flow, and the filters a flow's Records tab has (a custody
 * state, drift, a held step, matching anywhere) with a flow to narrow it to.
 */
export interface DeliveryRecordLookupQuery extends PageQuery {
  /**
   * A delivery key, or the start of an OSDU id, a source key, a label or an ingestion file name. Left out, the
   * records the ledger last touched are listed instead, newest first.
   */
  search?: string;
  /**
   * `contains` matches the term anywhere in the source key, the label, the OSDU id or the ingestion file. It has no
   * index, so the answer is a 400 naming what to narrow when the other filters leave more than 100,000 records.
   */
  mode?: "prefix" | "contains";
  status?: DeliveryRecordStatus;
  /** Only the records whose last verify found drift or a missing record. */
  drifted?: boolean;
  /** Only the records whose last render set an enrichment aside on its hold. */
  stepHeld?: boolean;
  /** Only the records an operator restricted in OSDU, or asked to: restricted by an operator. */
  restricted?: boolean;
  /** Only the records OSDU holds restricted until complete. */
  incomplete?: boolean;
  /** Only the records restricted until complete for one set of missing values, by its group. */
  missing?: string;
  /** Only the records whose access in OSDU is restricted either way, until complete or by an operator. */
  accessRestricted?: boolean;
  /** The ledger identity of one flow (a {@link DeliveryRecordFlow}'s `flowId`); left out, every flow. */
  flowId?: string;
  /** The partition whose ledgers are read; left out, the workbench's, which every call carries. */
  partition?: string;
}

/**
 * A flow the Records page can be narrowed to: a ledger identity that holds records, and the pipeline and interface the
 * Flow column names it by.
 */
export interface DeliveryRecordFlow {
  flowId: string;
  pipelineId: string;
  flowName: string;
  /** The interface of a source that delivers several; null for a flow in the single form. */
  interface: string | null;
  /** The partition of a flow that names its partitions; null for a flow that names none. */
  partition?: string | null;
}
