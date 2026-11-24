// Removing a flow's records through the delivery API: where its records live and the call each removal scope makes, a
// removal's preview and run, purging records from the ledger, and deleting a flow's ledger.

import type { RunStatus } from "@/api/types";
import type { DeliveryRecordFilter } from "./interventions";

/**
 * How much of a record a removal takes away in OSDU. The four are different calls with different promises, not degrees
 * of one thing: "record" and "previous" can be undone, "history" and "everything" cannot. OSDU has no call that removes
 * only the latest version, so "previous" writes the version before it back as a new version, and the replaced one stays
 * in the record's history. "restrict" removes nothing: the record's ACL in OSDU is replaced with a registered access
 * restriction's groups, and "unrestrict" gives it back the ACL it had (osdu/docs/reference/concepts/access-restriction.md).
 */
export type RemovalScope = "record" | "previous" | "history" | "everything" | "restrict" | "unrestrict";

/** Where a flow's records live, and the exact call each removal scope would make against them. */
export interface DeliveryTarget {
  pipelineId: string;
  flowName: string;
  /** The endpoint as the flow declares it, secret references included: that reference names the environment. */
  endpoint: string;
  dataPartition: string | null;
  protocol: string;
  authType: string;
  recordPath: string;
  /** The method the record scope calls recordPath with: POST for storage and the dataset service, DELETE for a DDMS's own removal. */
  recordMethod: string;
  historyPath: string;
  everythingPath: string;
  /** The interface of the source the target is for, or null for a flow in the single form. */
  interface: string | null;
  /** On the ddms route: which collection of which DDMS the records go to, as a sentence; null on the other routes. */
  ddms: string | null;
  /** The calls the previous scope makes: the version before the latest read, then written back as a new version. */
  previousPath: string;
  /** Why the route cannot write an earlier version back; absent or null when it can. */
  previousRefusal?: string | null;
  /** The call a restriction of the records' access, and a lift, makes; absent from an older control plane. */
  restrictPath?: string;
  /** Why the route cannot restrict the records' access; absent or null when it can. */
  restrictRefusal?: string | null;
}

/**
 * A removal of one or many records. The records are named by `keys` or by `filter` (every record it matches),
 * never both. `expected` is the count the operator was shown: the API refuses the removal when the filter no
 * longer resolves to it, rather than running against a set that changed underneath them.
 */
export interface DeliveryRemovalRequest {
  scope: RemovalScope;
  keys?: string[];
  filter?: DeliveryRecordFilter;
  expected?: number;
  /**
   * With "record" or "everything": also delete each record OSDU answered for from the ledger, keeping one line of it (who,
   * when, its OSDU id and last version). Refused with the other scopes.
   */
  purgeLedger?: boolean;
  /** The pool the remove run is queued on; any node when absent. */
  pool?: string | null;
  /** With "restrict": the registered access restriction, by name, whose groups the records' ACL becomes. Refused with the other scopes. */
  restriction?: string;
}

/** What a removal would act on, and from where: the contents of the confirmation. */
export interface DeliveryRemovalPreview {
  scope: RemovalScope;
  records: number;
  inOsdu: number;
  neverDelivered: number;
  capped: boolean;
  target: DeliveryTarget;
  /** Of the selection, the records removed from OSDU already: those deleting from the ledger alone reaches. */
  removed: number;
  /** Of the selection, the records whose access is restricted: those a lift reaches. Absent from an older control plane. */
  restricted?: number;
}

/**
 * Records already removed from OSDU to delete from the ledger: named by `keys`, by `filter` with the `expected` count the
 * operator was shown, or with `all` every record the ledger marks deleted; exactly one of them. A record OSDU may still hold
 * is never deleted; it is counted as left.
 */
export interface DeliveryLedgerPurgeRequest {
  keys?: string[];
  filter?: DeliveryRecordFilter;
  expected?: number;
  all?: boolean;
}

/** What deleting from the ledger did: records asked about, deleted, and left as they were. */
export interface DeliveryLedgerPurgeResult {
  selected: number;
  purged: number;
  left: number;
  summary: string;
}

/**
 * Deleting a flow's whole ledger, every interface's: `confirm` names the partition the ledger is kept in, as the operator
 * typed it; nothing is queued unless it is that partition.
 */
export interface DeliveryLedgerDeleteRequest {
  confirm: string;
  pool?: string | null;
}

export interface DeliveryLedgerDeleteAccepted {
  runId: string;
  status: RunStatus;
  partition: string;
  interfaces: string[];
}

/**
 * A removal was queued as a remove run of the flow, listed with its runs: the run to watch, how many records it acts on, and
 * whether it deletes them from the ledger too.
 */
export interface DeliveryRemovalAccepted {
  runId: string;
  status: RunStatus;
  scope: RemovalScope;
  records: number;
  /** Whether the removal deletes the records it removes from the ledger too. */
  purgeLedger: boolean;
  /** The access restriction a restriction restricts the records under; null for any other scope. */
  restriction?: string | null;
}
