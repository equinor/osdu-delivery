// One record of the ledger as the delivery API answers it: its custody state, its detail with the records it waits for
// and the access it is held under, its row's chain through the ingestion table, its rows and its OSDU document read
// back, its address, and the links to its page and to the records one run touched.

/**
 * A record's custody state. `waiting` holds a rendered document that refers to a record another record of the ledger
 * holds and has not delivered; the record goes back to pending when that one lands. `reverted` is a record a reversal put
 * back to the version OSDU held before the run it reversed, blocked until its source changes or it is released.
 */
export type DeliveryRecordStatus = "pending" | "delivering" | "delivered" | "held" | "failed" | "deleted" | "reverted" | "waiting";

export const DELIVERY_RECORD_STATUSES: readonly DeliveryRecordStatus[] = [
  "pending", "waiting", "delivering", "delivered", "held", "failed", "deleted", "reverted",
];

/**
 * What a verify found of a record in OSDU: of a delivered record, `match`, `drifted`, `missing` or `error`; of a record
 * removed from OSDU, `gone` (the removal stands) or `served` (OSDU still holds a record at its id), or `error`.
 */
export type DeliveryVerifyOutcome = "match" | "drifted" | "missing" | "error" | "gone" | "served";

/** The current state of one deliverable: what OSDU holds for it, what is pending, and why it is where it is. */
export interface DeliveryRecord {
  deliveryKey: string;
  flowId: string;
  sourceKey: string;
  label: string | null;
  mappingName: string;
  renderContext: string | null;
  sourceFingerprint: string | null;
  /** When the source row the delivered document was built from last changed (the flow's source.lastModified). */
  sourceModifiedUtc: string | null;
  metadataHash: string | null;
  payloadHash: string | null;
  /** The newest modified time among the chunk files the delivered payload was sent from. */
  payloadModifiedUtc: string | null;
  targetId: string | null;
  targetVersion: number | null;
  status: DeliveryRecordStatus;
  lastDeliveredUtc: string | null;
  lastVerifiedUtc: string | null;
  lastVerifyOutcome: DeliveryVerifyOutcome | null;
  leaseOwner: string | null;
  leaseExpiresUtc: string | null;
  lastSubmissionId: string | null;
  attemptCount: number;
  nextAttemptUtc: string | null;
  lastError: string | null;
  hasPendingDocument: boolean;
  pendingMetadata: boolean;
  pendingPayload: boolean;
  pendingPayloadLocation: string | null;
  blocked: boolean;
  createdUtc: string;
  updatedUtc: string;
  /** Where the pending document sits in the submission's work batches (batch:offset:length), when one is waiting. */
  pendingDocumentRef: string | null;
  workBatch: number | null;
  /** The identifiers the target returned for what it holds now (record id and version, dataset ids, a workflow run). */
  targetState: Record<string, unknown> | null;
  /** The steps of the pending delivery an earlier try completed, with what they returned. */
  pendingSteps: Record<string, unknown> | null;
  /** The ingestion file the delivered document was built from, and the row of it. */
  sourceFileName: string | null;
  sourceRowNumber: number | null;
  /** When the ingestion table last updated that row. */
  sourceUpdatedUtc: string | null;
  /** The same three for work that is waiting: where the pending document will be built from. */
  pendingSourceFileName: string | null;
  pendingSourceRowNumber: number | null;
  pendingSourceUpdatedUtc: string | null;
  /**
   * When the ingestion table first inserted the record's row, which later changes never move: its arrival. Null when the
   * table does not carry it, or no plan has read the row since the ledger began keeping it.
   */
  sourceInsertedUtc?: string | null;
  /** The key columns that find the record's row in the ingestion tables, as JSON. */
  sourceKeyJson: string | null;
  /** When a plan last asked for this record, for a record waiting on one. */
  planRequestedUtc: string | null;
  /** While the record is waiting: the OSDU id of the record it waits for. */
  waitingFor: string | null;
  /** The partition the record's ledger is kept under, whichever way its flow names it. */
  partition?: string | null;
  /** The OSDU ids the pending document refers to, each with the property that holds it. */
  references: DeliveryRecordReference[] | null;
  /**
   * While the record is blocked, held or failed: the issue keeping it so, which every record refused for the same reason
   * shares (sixteen hexadecimal characters). Null otherwise, and until the ledger has sorted a record blocked before it kept
   * issues.
   */
  issue?: string | null;
  /**
   * For a record of a linked template that reads its parent ($parent): the parent's OSDU id, the record of the canonical
   * kind with the same minted id in that kind's namespace.
   */
  parentId?: string | null;
  /** The version of the parent the delivered document was rendered from. */
  parentVersion?: number | null;
  /** The version of the parent the queued document was rendered from. */
  pendingParentVersion?: number | null;
  /**
   * While the record is held because OSDU already holds a record at the OSDU id its key makes, which no ledger claimed
   * (another system wrote it, or a ledger this database does not hold): that id, which an adoption makes the record's.
   */
  occupiedTargetId?: string | null;
  /** The version OSDU held at `occupiedTargetId` when the record was held for it. */
  occupiedTargetVersion?: number | null;
  /**
   * Whether an adoption may be asked of the record: held because OSDU already holds a record at the id its key makes (the
   * id the hold kept, or a hold made before holds kept it), claiming none, and not asked already.
   */
  adoptable?: boolean;
  /** When an adoption was asked of the record; the flow's next run takes it over. Null otherwise. */
  adoptRequestedUtc?: string | null;
  /**
   * Whether the record claimed its OSDU id for its flow: it queued a document once, or took over the record OSDU held
   * there. A record that was only ever held names the id its key makes, but its flow wrote nothing there to read back.
   * Absent from an older control plane.
   */
  claimed?: boolean;
  /**
   * The registered access restriction whose groups replaced the record's ACL in OSDU, while it does; null or absent for a
   * record whose access is its own. No run delivers the record until it is lifted.
   */
  restrictionId?: number | null;
  /** When OSDU confirmed the restriction; null while it is asked and OSDU's answer is not known. */
  restrictedUtc?: string | null;
  /**
   * The registered access restriction OSDU holds the record under, restricted until complete, because values its mapping marks
   * `$restrictWhenMissing` were missing from the document OSDU holds; null or absent while OSDU holds it with its own access.
   * Every run plans it as any other, and the delivery that finds every such value gives it its own access back.
   */
  incompleteRestrictionId?: number | null;
  /** When OSDU first held the record restricted until complete; null while it is not. */
  incompleteSinceUtc?: string | null;
  /** What the document OSDU holds goes without, one value to an item, as a hold would say each; null while not restricted until complete. */
  incompleteMissing?: string[] | null;
  /** What every record restricted for the same missing values shares: the `missing` filter of a listing. */
  incompleteGroup?: string | null;
}

/**
 * The access OSDU holds a record restricted until complete under (osdu/docs/reference/flow/mapping-restricted-access.md): the
 * registered restriction its mapping named and its groups (null when the registry no longer names it), since when, what the
 * document OSDU holds goes without, and the group every record missing the same shares.
 */
export interface DeliveryRecordIncomplete {
  restrictionId: number;
  name: string | null;
  description: string | null;
  viewers: string[] | null;
  owners: string[] | null;
  sinceUtc: string | null;
  missing: string[];
  group: string | null;
}

/**
 * The access restriction a record carries (osdu/docs/reference/concepts/access-restriction.md): the registered restriction
 * and its groups (null when the registry no longer names it), when OSDU confirmed it (null while asked and unanswered), the
 * ACL the record had before, which lifting it gives back, and the records beside it the restriction reached with it (the
 * records the mapping's enrichments add), which lifting gives the ACL each had.
 */
export interface DeliveryRecordRestriction {
  restrictionId: number;
  name: string | null;
  description: string | null;
  viewers: string[] | null;
  owners: string[] | null;
  restrictedUtc: string | null;
  formerViewers: string[] | null;
  formerOwners: string[] | null;
  beside?: string[] | null;
}

/** An OSDU id a record's pending document refers to, and the property of the record holding it. */
export interface DeliveryRecordReference {
  id: string;
  property: string;
}

/** A record of the ledger another record waits for, or that waits for it: where it is and how it stands. */
export interface DeliveryRecordLink {
  flowId: string;
  deliveryKey: string;
  pipelineId: string | null;
  flowName: string | null;
  interface: string | null;
  sourceKey: string;
  label: string | null;
  targetId: string | null;
  status: DeliveryRecordStatus;
  /** The partition its ledger delivers to, for a flow that names its partitions. */
  partition?: string | null;
}

export interface DeliveryRecordDetail {
  record: DeliveryRecord;
  pipelineId: string | null;
  repoId: string | null;
  flowName: string | null;
  interface?: string | null;
  /** The record this one waits for, while it waits. */
  waitsOn?: DeliveryRecordLink | null;
  /** The records waiting for this one (the first 50). */
  waitedOnBy?: DeliveryRecordLink[] | null;
  /** The record of the ledger holding this record's parent id, when one of the partition's flows delivers it. */
  parent?: DeliveryRecordLink | null;
  /** The records reading this one as their parent (the first 50): the same minted id in another namespace. */
  readBy?: DeliveryRecordLink[] | null;
  /**
   * The columns the record's key tuple (`sourceKeyJson`) holds the values of, in its order: the interface's
   * `source.record.key` as the catalog's copy of the flow declares it now. Null when the catalog holds no readable copy.
   */
  keyColumns?: string[] | null;
  /**
   * The partition the record's ledger delivers to, for a flow that names its partitions (each partition keeps a ledger of
   * its own); null for a flow that names none.
   */
  partition?: string | null;
  /**
   * The ledger's deletion of the record's key, when a removal flow found its source no longer holds it; null when it holds
   * none. Its empty fields arrive as null: read it through `removedKeyOf`.
   */
  removedKey?: object | null;
  /** The access restriction the record carries while its access in OSDU is restricted; null or absent otherwise. */
  restriction?: DeliveryRecordRestriction | null;
  /** The access OSDU holds the record under while it is restricted until complete; null or absent otherwise. */
  restrictedUntilComplete?: DeliveryRecordIncomplete | null;
}

/**
 * One platform run a change of a record's row is evidence of: the ingestion run that was writing the record's table when
 * the row was stamped, or the landing that brought the change's file in. The stage is the estate's own vocabulary
 * (pre-ingestion, ingestion), derived from the flow's kind.
 */
export interface DeliveryChainRun {
  stage: string;
  runId: string;
  pipelineId: string;
  flowName: string;
  flowKind: string;
  wave: number;
  status: string;
  success: boolean;
  startedUtc: string | null;
  ranUtc: string;
  durationSeconds: number | null;
  /** Rows the run loaded (an ingestion run) or read from the file (a landing). */
  rows: number;
  error: string | null;
}

/** The landing that brought a change's file into the estate: its run, and the file as that run processed it. */
export interface DeliveryChainLanding {
  run: DeliveryChainRun;
  fileName: string;
  filePath: string | null;
  rows: number;
  sizeBytes: number;
  fileModifiedUtc: string | null;
}

/**
 * What one change of a record's row was: its first arrival in the ingestion table (loaded), an arrival after earlier
 * versions (reloaded: the row was deleted and inserted anew), the earliest version the ledger holds when it does not
 * know the arrival (earliest), a change of the row (changed), or its deletion (deleted).
 */
export type DeliverySourceChangeKind = "loaded" | "reloaded" | "earliest" | "changed" | "deleted";

/**
 * One change of a record's row in its ingestion table: the moment the table stamped it, the file and row it came from,
 * the ingestion run that wrote it and the landing that brought its file in. A run is named only when the catalog proves
 * it; either is null otherwise.
 */
export interface DeliverySourceChange {
  kind: DeliverySourceChangeKind;
  atUtc: string;
  fileName: string | null;
  rowNumber: number | null;
  loading: DeliveryChainRun | null;
  landing: DeliveryChainLanding | null;
}

/**
 * A record's row through its ingestion table: the table, when the row first reached it, and every change of it the
 * ledger recorded, newest first. A run that reloaded the row without changing it is not a change and is not here.
 * `truncated` says only the newest changes (and the arrival) are listed; `note` says what the chain could not name.
 */
export interface DeliveryRecordChain {
  sourceTable: string | null;
  insertedUtc: string | null;
  changes: DeliverySourceChange[];
  truncated: boolean;
  note: string | null;
}

/** A record's rows as the ingestion tables hold them now: the record row with its system columns, its datasets, its origin. */
export type DeliverySourceRows = Record<string, unknown>;

/** A record as OSDU holds it, read through a flow's route and answered by the control plane. */
export interface DeliveryOsduRead {
  flow: string;
  deliveryKey?: string | null;
  targetId: string;
  correlationId?: string | null;
  found: boolean;
  /** The version of the document read (its own `version` field). */
  version?: number | null;
  /** The version the read was asked for; absent for a read of the latest. */
  readVersion?: number | null;
  /** Every version the target keeps of the record, newest first; null for a target that keeps no version list. */
  versions?: number[] | null;
  /** Why the version list could not be read, when the record itself could. */
  historyError?: string | null;
  record?: Record<string, unknown> | null;
  readUtc: string;
}

/**
 * Where one record lives: its flow's ledger id and its delivery key. A key alone names one record per flow that reads
 * the row, so every record address carries both, in the API and in the GUI.
 */
export interface DeliveryRecordRef {
  flowId: string;
  deliveryKey: string;
}

/** The GUI page of one record. */
export const deliveryRecordRoute = ({ flowId, deliveryKey }: DeliveryRecordRef) =>
  `/delivery/records/${encodeURIComponent(flowId)}/${encodeURIComponent(deliveryKey)}`;

/**
 * A delivery flow's Records tab narrowed to the records one run touched: those its attempts name, and its parts' when it
 * split its delivery over nodes. A run that names the interface or the partition it ran in opens that ledger; one that names
 * neither opens the ledger in view, whose records it touched like every other interface's of its source.
 */
export function deliveryRunRecordsRoute(
  pipelineId: string, runId: string, scope: { interfaceName: string | null; partition: string | null } = { interfaceName: null, partition: null },
  trigger?: string,
): string {
  const params = new URLSearchParams({ tab: "records", run: runId });
  if (trigger !== undefined) {
    params.set("trigger", trigger);
  }

  if (scope.interfaceName !== null) {
    params.set("interface", scope.interfaceName);
  }

  if (scope.partition !== null) {
    params.set("partition", scope.partition);
  }

  return `/pipelines/${encodeURIComponent(pipelineId)}?${params.toString()}`;
}
