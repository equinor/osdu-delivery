// A record's delivery attempts as the delivery API answers them: each try with its steps, its result and what an undo
// took, and the reason a run sent the record, by the inputs that moved.

/** One step of a delivery try: what it did, how long it took, what the target answered. */
export interface DeliveryAttemptStep {
  name: string;
  startedUtc?: string;
  ms?: number;
  status?: number;
  resumed?: boolean;
  error?: string;
  returned?: Record<string, string>;
}

export interface DeliveryAttemptResult {
  /** The correlation id every OSDU request of the try carried, for finding it in the services' own logs. */
  correlationId?: string;
  /** Absent on attempts recorded before every attempt carried a steps array. */
  steps?: DeliveryAttemptStep[];
  returned?: Record<string, string>;
  /** What an attempt that did not fail has to say (chunks sent, why nothing was sent, what a removal took). */
  detail?: string;
  /**
   * A plan delivered a held, failed or reverted record again with nothing sent: OSDU already held the version its row gives.
   * Absent on every other attempt.
   */
  settled?: boolean;
  /** For an undo (outcome `undone`, phase `undo`): why it ran and what became of each artifact it took. */
  undo?: DeliveryUndoResult;
  /**
   * For an adoption (phase `adopt`): the OSDU id the record claimed, where OSDU already held a record no ledger had
   * claimed, and the version OSDU held there.
   */
  adopted?: { targetId: string; version?: number };
  /**
   * For a restriction of the record's access (phase `restrict`) or its lift (phase `lift-restriction`): the registered
   * restriction, the ACL OSDU holds after it (`acl`, null when OSDU was asked nothing) and the ACL it replaced (`replaced`).
   */
  restriction?: DeliveryAttemptRestriction;
  /**
   * What the check before sending found of the document (osdu/docs/reference/concepts/preflight.md, Validation before a
   * record is sent), what the mapping's assertions found among it; read it with `verdictIn`, which answers null for a
   * result that carries none.
   */
  validation?: unknown;
}

/** An ACL as an attempt's result names it: the groups that may read the record and those that may change it. */
export interface DeliveryAttemptAcl {
  viewers: string[];
  owners: string[];
  /** The records beside the record a restriction reached (the records its mapping's enrichments add), each group with the ACL it had when it was not the record's. */
  beside?: { ids: string[]; viewers?: string[]; owners?: string[] }[];
}

/** What a restriction, or its lift, did to a record's access, as its attempt's result names it. */
export interface DeliveryAttemptRestriction {
  id: number;
  name: string;
  acl?: DeliveryAttemptAcl | null;
  replaced?: DeliveryAttemptAcl | null;
}

/**
 * What one undo of an unfinished delivery did (osdu/docs/reference/concepts/removal-and-reversal.md, Unfinished deliveries
 * and their undo): why it ran (`held`, `failed`, `abandoned`, `removed`), whether it left the record itself to newer work,
 * the units it took, its outcome counts in a line, and each artifact with what became of it.
 */
export interface DeliveryUndoResult {
  reason?: string;
  keptRecord?: boolean;
  units?: string[];
  summary?: string;
  artifacts?: DeliveryUndoArtifact[];
}

/** One artifact an undo took: its slot and role, its id or what finds it, its versions, and how the undo settled it. */
export interface DeliveryUndoArtifact {
  artifactId: number;
  slot: string;
  role: string;
  /** removed, restored, gone, kept, superseded (left to newer work) or failed. */
  outcome: string;
  targetId?: string;
  locator?: string;
  version?: number;
  priorVersion?: number;
  note?: string;
}

/**
 * What moved between the version the ledger holds for a record and the render a run sent: the kinds a delivered attempt's
 * reason names (osdu/docs/reference/concepts/delivery-reasons.md).
 */
export type DeliveryTriggerKind =
  | "first-delivery"
  | "source-changed"
  | "child-or-lookup-changed"
  | "mapping-changed"
  | "template-changed"
  | "parameters-changed"
  | "enrichment-changed"
  | "cache-changed"
  | "requested"
  | "drift"
  | "forced"
  | "payload-changed"
  | "other-input-changed";

/** One input that moved, with what it was and is now, and what else names it. Never a value from the source. */
export interface DeliveryReasonTrigger {
  kind: DeliveryTriggerKind;
  /** What it was: a mapping reference, a template or cache version, a row's update moment, a payload hash. */
  from?: string;
  to?: string;
  /** For a mapping: the fingerprints of the documents the two versions were rendered with, and the estate commits. */
  fromFingerprint?: string;
  toFingerprint?: string;
  fromCommit?: string;
  toCommit?: string;
  /** The parameters or enrichments that moved, the cache changes that reached the record, the change keys that force it. */
  names?: string[];
  more?: number;
  /** What happened, in a sentence, where the fields alone do not say it. */
  detail?: string;
  /** When a plan was asked for, or when verification found the difference. */
  at?: string;
  /** For a request an intervention made: the audit trail's entry, what it was, and who asked. */
  activityId?: number;
  activity?: string;
  actor?: string;
}

/** The paths of a delivered document that changed since the version OSDU held, or why they were not compared. */
export interface DeliveryReasonPaths {
  compared: boolean;
  why?: string;
  added?: number;
  removed?: number;
  changed?: number;
  /** The paths that changed, in path order, at most 50; `more` counts the rest. */
  listed?: { path: string; change: "added" | "removed" | "changed" }[];
  more?: number;
}

/**
 * Why a delivered try was sent: what it sent, its one line, every input that moved, and the paths of the document that
 * changed. A delivery made before the ledger kept reasons says only that it was not recorded. A render found identical to
 * what OSDU holds (action `none`) says what moved to render it, with nothing sent.
 */
export type DeliveryAttemptReason =
  | { recorded: true; action: "create" | "update-metadata" | "update-payload" | "update-both" | "none"; summary: string; triggers: DeliveryReasonTrigger[]; paths?: DeliveryReasonPaths }
  | { recorded: false; summary: string };

/** One delivery try, as the append-only history holds it. */
export interface DeliveryAttempt {
  attemptId: number;
  deliveryKey: string;
  submissionId: string | null;
  runId: string | null;
  worker: string;
  startedUtc: string;
  completedUtc: string;
  /** `undone` is an undo of what an unfinished delivery left (phase `undo`); a skipped or held try of phase `undo-wait` waited for one. */
  outcome: "delivered" | "skipped" | "failed" | "held" | "deleted" | "historypurged" | "restored" | "undone" | "restricted" | "unrestricted";
  /** What the try was: a held try of phase `document-missing` set the record aside, unblocked, because the document queued for it was not there to read. */
  phase: string;
  metadataHash: string | null;
  payloadHash: string | null;
  targetVersion: number | null;
  error: string | null;
  /** The steps the try took and what the target returned, null for tries that recorded none. */
  result: DeliveryAttemptResult | null;
  workBatch: number | null;
  /** The ingestion file and row the document this try sent was built from, and when that row last changed. */
  sourceFileName: string | null;
  sourceRowNumber: number | null;
  sourceUpdatedUtc: string | null;
  /** When the ingestion table marked that row deleted, for the hold of a deleted row (phase source-deleted). */
  sourceDeletedUtc?: string | null;
  /** For a record reading its parent: the version of the parent the document this try sent was rendered from. */
  parentVersion?: number | null;
  /** For a delivered try: why it was sent. Absent for every other try. */
  reason?: DeliveryAttemptReason | null;
}
