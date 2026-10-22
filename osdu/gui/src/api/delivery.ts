// The delivery ledger's API: what each flow delivered (records, their history, their submissions), the audit trail,
// the mappings, templates and caches the catalog holds, the mapping builder, and the interventions (release,
// redeliver, verify, read back, delete). Same conventions as endpoints.ts: one function per endpoint, pages compose them
// with TanStack Query.

import { del, get, getText, post, put, type QueryParams } from "@/api/client";
import type { ComputeTaskAccepted, PagedResult, RunStatus } from "@/api/types";
import type { PageQuery } from "@/api/endpoints";
import type { AssertionAction, AssertionFindings, AssertionStage } from "./validation";

/**
 * A record's custody state. `waiting` holds a rendered document that refers to a record another record of the ledger
 * holds and has not delivered; the record goes back to pending when that one lands. `reverted` is a record a reversal put
 * back to the version OSDU held before the run it reversed, blocked until its source changes or it is released.
 */
export type DeliveryRecordStatus = "pending" | "delivering" | "delivered" | "held" | "failed" | "deleted" | "reverted" | "waiting";

export const DELIVERY_RECORD_STATUSES: readonly DeliveryRecordStatus[] = [
  "pending", "waiting", "delivering", "delivered", "held", "failed", "deleted", "reverted",
];

export type DeliverySubmissionStatus = "received" | "planned" | "running" | "completed" | "failed";

/**
 * What a submission read from the ingestion tables: the rows changed in a window after the scope's watermark
 * (`incremental`), every row of the scope (`full`), or the keys a record-scoped or replanned run named (`keys`).
 */
export type DeliverySubmissionKind = "incremental" | "full" | "keys";

export type DeliveryVerifyOutcome = "match" | "drifted" | "missing" | "error";

/** A delivery flow's record counts by state, drift, throughput, and its last submission. */
export interface DeliveryFlowStats {
  pipelineId: string;
  flowName: string;
  flowId: string;
  total: number;
  pending: number;
  delivering: number;
  delivered: number;
  held: number;
  failed: number;
  deleted: number;
  /** Records a reversal put back to the version OSDU held before, blocked until released; absent from an older control plane. */
  reverted?: number;
  /** Records waiting for a record they refer to that has not landed. */
  waiting: number;
  /**
   * Records blocked (held, failed, reverted, or removed by a reversal) until their row moves or someone releases them; a
   * released record waiting for its run is not. Absent from an older control plane.
   */
  blocked?: number;
  /**
   * Held or failed records nothing will move: not blocked, not asked to be planned again, no document to send. A release of
   * every blocked record reaches them too. Absent from an older control plane.
   */
  stranded?: number;
  drifted: number;
  deliveredLast24h: number;
  lastDeliveredUtc: string | null;
  lastVerifiedUtc: string | null;
  submissions: number;
  lastSubmission: DeliverySubmission | null;
  /**
   * The partition the counts are of, for a flow that names its partitions and was counted in one; null when it was counted
   * as a whole, or names none.
   */
  partition?: string | null;
  /** The partitions the counts cover, for a flow that names its partitions; null for one that names none. */
  partitions?: string[] | null;
  /**
   * For a flow whose partition is its data-partition-id header, the partition its ledger is kept under, as the ledger's
   * directory holds it; null until it has run, and for a flow that names or follows partitions. Never sent back as a
   * request's partition: such a flow takes none.
   */
  headerPartition?: string | null;
  /**
   * The parts of a record the flow's route can send again (all, record, and the files, bulk data or workflow run it sends,
   * with metadata and payload as their older names), for a card of one ledger; absent for a card that adds up several.
   */
  redeliverParts?: string[] | null;
}

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
}

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
}

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
}

/** One value of a record a lookup matched: the value as it was read, and what it is (identity, key, label, osdu, file). */
export interface DeliveryRecordMatch {
  value: string;
  kind: string;
}

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
   * What the check before sending found of the document (osdu/docs/reference/concepts/preflight.md, Validation before a
   * record is sent), what the mapping's assertions found among it; read it with `verdictIn`, which answers null for a
   * result that carries none.
   */
  validation?: unknown;
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
  outcome: "delivered" | "skipped" | "failed" | "held" | "deleted" | "historypurged" | "restored" | "undone";
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
}

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

/** A mapping document as the sync found it in a repository. */
export interface DeliveryMapping {
  id: string;
  repoId: string;
  reference: string;
  name: string;
  version: string;
  kind: string;
  relativePath: string;
  contentHash: string;
  status: "valid" | "invalid";
  message: string | null;
  summary: Record<string, unknown>;
  firstSeenUtc: string;
  lastSeenUtc: string;
  /** The template version the mapping fills; empty when it names none the sync could settle. */
  templateVersion: string;
  /** The template folder file the last sync settled the template from; empty when no folder on the mapping's path holds it. */
  templatePath: string;
}

export interface DeliveryMappingDetail {
  mapping: DeliveryMapping;
  yaml: string;
}

/** One path a partition's cache keeps for a type, the name it is cached under, and the cache flows that declare it. */
export interface DeliveryCacheField {
  path: string;
  as: string;
  flows: string[];
}

/** Where a cached type's records come from: searched on OSDU, read from an ingestion table, or held from a dictionary document. */
export type DeliveryCacheOrigin = "osdu" | "table" | "dictionary" | "dimension";

/**
 * One cache flow's declaration of a type: where it takes the records from, for an OSDU type the kind it searches and its
 * query, for a table type the connection reference, table and key column it reads, for a dictionary type the document's
 * file and key, and what a change does in its declaration.
 */
export interface DeliveryCacheTypeSource {
  flow: string;
  origin: DeliveryCacheOrigin;
  kind: string | null;
  query: string | null;
  /** approve or auto, as this flow declares it. */
  onChange: string;
  connection: string | null;
  sourceObject: string | null;
  keyField: string | null;
  dictionaryPath: string | null;
}

/**
 * One type of a partition's cache, as its cache flows together declare it: where its records come from, each flow's
 * declaration, every path any of them keeps, what a change does, and what the current version holds of it.
 */
export interface DeliveryCacheType {
  name: string;
  entityType: string;
  sources: DeliveryCacheTypeSource[];
  fields: DeliveryCacheField[];
  /** approve (a change waits for a decision, when any declaring flow asks for that) or auto (a change goes out on the next run). */
  onChange: string;
  /** How many records the current version holds of the type. */
  items: number;
  origin: DeliveryCacheOrigin;
  /** For a lookup table (a table or a dictionary), the name each row's key is kept under; null for OSDU records. */
  key: string | null;
}

/** A schedule that refreshes a cache flow: its cadence, or that it fires behind other schedules. */
export interface DeliveryCacheSchedule {
  id: string;
  name: string;
  cron: string | null;
  intervalSeconds: number | null;
  chained: boolean;
}

/**
 * A cache flow filling a partition's cache: the repository and file that define it (the file is where what it caches is
 * changed), its pipeline, the OSDU endpoint reference its OSDU types are searched on and the connection reference its table
 * types are read from (each null when it declares none of those), the schedules that refresh it, and the types it declares.
 */
export interface DeliveryCacheFlow {
  name: string;
  repoId: string;
  repoName: string;
  relativePath: string;
  pipelineId: string | null;
  endpoint: string | null;
  schedules: DeliveryCacheSchedule[];
  types: string[];
  connection: string | null;
  /**
   * The partitions the flow builds a cache for, when it names them (`partitions:`): a refresh of it names the one it
   * builds, or builds each in turn. Null for a flow whose partition is its source header's.
   */
  partitions?: string[] | null;
  /** How many days the flow asks the partition's cache to keep a replaced version's records (`retentionDays`, 7 by default). */
  retentionDays?: number;
}

/**
 * The cache of one OSDU partition: every cache flow that fills it, the types it holds as those flows together declare them,
 * its current version with the flow and run that wrote it, and how many versions it has. Every delivery flow delivering to
 * the partition reads it.
 */
export interface DeliveryCache {
  /** The partition: the data-partition-id every flow that fills or reads the cache carries. */
  scope: string;
  flows: DeliveryCacheFlow[];
  types: DeliveryCacheType[];
  current: DeliveryCacheVersion | null;
  versions: number;
  /**
   * How many days a replaced version's records are kept before a refresh prunes them: the longest retention any of the
   * partition's cache flows declares.
   */
  retentionDays?: number;
}

/** One cached record: its OSDU id and the values captured at the declared paths, in whatever shape they came. */
export interface DeliveryCachedItem {
  itemId: number;
  /** The partition whose cache holds the record. */
  scope: string;
  /** The version of the cache this row is the record as of. */
  version: string;
  typeName: string;
  entityType: string;
  recordId: string;
  fields: Record<string, unknown>;
}

/** How one type a cache version holds compares with the version before it, by the type's own content hash. */
export type DeliveryCacheTypeChange = "added" | "changed" | "unchanged";

/**
 * One type a cache version holds, how many records of it, and for a lookup table the name its key is kept under; with the
 * type's own content hash, how it compares with the version before, and the version its content dates from. The version moves
 * whenever anything in the partition's cache does; these move only when the type does.
 */
export interface DeliveryCacheVersionType {
  name: string;
  entityType: string;
  items: number;
  key: string | null;
  /** The type's content hash; null for a version written before types were hashed. */
  hash: string | null;
  /** How the type compares with the version before; null for a version written before types were hashed. */
  change: DeliveryCacheTypeChange | null;
  /** The version that last added or changed the type, whose content this version holds unchanged; null when it cannot be told. */
  since: string | null;
}

/** Whether a platform service reports a system property on for the partition. */
export type DeliveryCacheSystemPropertyState = "Enabled" | "Disabled" | "Unknown";

/**
 * One of the partition's system properties as a cache version holds it: a setting of the platform for the partition, reported
 * by one of its services, which is neither reference nor master data and has no record behind it.
 */
export interface DeliveryCacheSystemProperty {
  /** The service that reports it: indexer or search. */
  service: string;
  /** The property as that service names it, such as featureFlag.keywordLower.enabled. */
  name: string;
  state: DeliveryCacheSystemPropertyState;
  /** Where the service says it took the value from, when it says. */
  source: string | null;
  /** Why the state is unknown, or the last time it could not be read. */
  detail: string | null;
}

/**
 * One version of a partition's cache: when it was written, whether it is the version deliveries render against, the version
 * that was current before it, the cache flow and the run that wrote it and who asked (no run for an import from files),
 * where the content came from, and what it holds.
 */
export interface DeliveryCacheVersion {
  scope: string;
  version: string;
  sequence: number;
  capturedUtc: string;
  current: boolean;
  previousVersion: string | null;
  /** The cache flow whose capture or import wrote the version. */
  flow: string;
  runId: string | null;
  capturedBy: string;
  origin: string;
  items: number;
  types: DeliveryCacheVersionType[];
  /** The partition's system properties the capture found, kept apart from the types. */
  systemProperties: DeliveryCacheSystemProperty[];
  /**
   * When the cache's retention pruned the version's records; absent or null while they are kept. A pruned version is still
   * listed with what it was and changed, and can no longer be read or compared.
   */
  prunedUtc?: string | null;
  /** Who pruned them: the refresh's requester, the import's account, or the admin who purged the history. */
  prunedBy?: string | null;
}

/** A purge of a partition cache's history: the days of replaced versions that keep their records, 0 to 36500. */
export interface DeliveryCachePruneRequest {
  scope: string;
  keepDays: number;
}

/**
 * What a purge of a partition cache's history did, or for a preview would do: the days it keeps, how many versions keep
 * their records, the versions it prunes and the stored rows that go, what it waits for when it cannot prune yet, and one
 * line saying it.
 */
export interface DeliveryCachePruneResult {
  scope: string;
  keepDays: number;
  preview: boolean;
  kept: number;
  pruned: string[];
  rows: number;
  deferred?: string | null;
  summary: string;
}

/** How one cached record differs between two versions of its cache. */
export type DeliveryCacheChange = "changed" | "added" | "removed";

/** A comparison of two versions of one cache: `from` is required, `to` defaults to the current version, `change` narrows the items only. */
export type DeliveryCacheDiffQuery = {
  scope: string;
  from: string;
  to?: string;
  type?: string;
  change?: DeliveryCacheChange;
  search?: string;
  page?: number;
  pageSize?: number;
};

/** One cached record that differs between two versions, with what it held on each side. */
export interface DeliveryCacheDiffItem {
  typeName: string;
  entityType: string;
  recordId: string;
  change: DeliveryCacheChange;
  /** The captured values at the earlier version; null for a record the later version added. */
  before: Record<string, unknown> | null;
  /** The captured values at the later version; null for a record the later version no longer holds. */
  after: Record<string, unknown> | null;
  /** The captured names whose value differs; empty unless the record changed. */
  changedFields: string[];
}

/** How many records of one cached type changed, arrived and left between the two versions. */
export interface DeliveryCacheDiffType {
  typeName: string;
  changed: number;
  added: number;
  removed: number;
}

/**
 * What changed in a cache between two versions. The counts follow the type and search filters but not the change filter, so
 * they describe every kind of change while the items show the one picked.
 */
export interface DeliveryCacheDiff {
  scope: string;
  fromVersion: string;
  toVersion: string;
  changed: number;
  added: number;
  removed: number;
  types: DeliveryCacheDiffType[];
  items: PagedResult<DeliveryCacheDiffItem>;
}

/**
 * One type a version moved: added, changed or removed, with how many of its records changed, arrived and left. Such a version
 * is a version of the type, whose content hash differs from the one the version before held.
 */
export interface DeliveryCacheHistoryType {
  name: string;
  change: "added" | "changed" | "removed";
  changed: number;
  added: number;
  removed: number;
  /** The type's content hash in the version; null when the version removed the type, or was written before types were hashed. */
  hash: string | null;
  /** How many records of the type the version holds; 0 when it removed the type. */
  items: number;
}

/**
 * One version in a cache's history: the version captured before it, how many records it changed, added and removed against
 * that one, and which types it moved. A type that only rode along with another's change is not listed.
 */
export interface DeliveryCacheHistoryEntry {
  version: DeliveryCacheVersion;
  /** The version captured before it; null for the first version, whose records all arrived with it. */
  before: string | null;
  changed: number;
  added: number;
  removed: number;
  /** The types the version added, changed or removed (the type in scope alone, when one is named). */
  types: DeliveryCacheHistoryType[];
  /** True when the cache's retention pruned the version before it: the counts stand, the records that differ are gone. */
  beforePruned?: boolean;
}

/** One cache change and what happens about it: it covers every delivered record built from the value that moved. */
export interface DeliveryUpdateTag {
  tagId: number;
  kind: string;
  /** The partition whose cache the change was found in. */
  scope: string;
  typeName: string;
  itemId: string;
  path: string;
  /** changed, removed, unmatched, or listed (a lookup table now lists a key records looked up and found no row under). */
  change: string;
  oldValue: string | null;
  newValue: string | null;
  fromVersion: string | null;
  toVersion: string;
  /** auto or approve. */
  mode: string;
  /** pending, approved, rolling (records being marked), delivering (all marked, some flows yet to run), rejected or applied. */
  status: string;
  summary: string;
  /** Delivered records built from the old value. */
  affectedRecords: number;
  /** How many of them the rollout has marked for redelivery. */
  processed: number;
  /** How many are still to be marked. */
  remaining: number;
  /** How many, of every flow, are still built from the old value: their flow has not rendered them again yet. */
  waiting: number;
  /** The flows those records belong to, most waiting first. */
  waitingFlows: DeliveryUpdateTagFlow[];
  detectedUtc: string;
  decidedUtc: string | null;
  decidedBy: string | null;
  startedUtc: string | null;
  completedUtc: string | null;
}

/** The records of one flow a cache change still waits for; pipelineId and flowName are null for a ledger no synced flow holds. */
export interface DeliveryUpdateTagFlow {
  flowId: string;
  pipelineId: string | null;
  flowName: string | null;
  records: number;
}

/** One cached value a record was built from. */
export interface DeliveryCacheUse {
  /** The partition whose cache the value was read from. */
  scope: string;
  typeName: string;
  itemId: string;
  path: string;
  /**
   * match (what it resolved by), value (what went into the document), empty (a field read that held nothing), unlisted
   * (a value no cached row answered to: a key a lookup table does not list, a wellbore the cache does not hold yet),
   * unverified (an id written without a record the cache holds) or listed (the rows a $findAll found by one key, their
   * ids in value, empty when none was: an access group a data office has not listed for the field yet).
   */
  kind: string;
  value: string;
}

/**
 * One thing delivered records of a partition were built without, and how many were: a value no cached record answered to
 * (unlisted: a wellbore the cache does not hold yet), a key a $findAll found no row under (listed: a field no access group
 * lists), an id written without its record (unverified), or a path read that held nothing (empty). The refresh that brings
 * it tags the records and redelivers them.
 */
export interface DeliveryCacheGap {
  typeName: string;
  path: string;
  kind: string;
  /** What was looked for: the key, or the record whose path held nothing. */
  key: string;
  /** The value as it was looked up, for a value no record answered to. */
  value: string;
  records: number;
}

export interface DeliveryRunAccepted {
  runId: string;
  status: RunStatus;
}

export interface DeliveryReleaseResult {
  released: number;
  /** The deliver run queued with the release, when one was asked for. */
  runId?: string | null;
}

/**
 * One issue keeping a flow's records blocked: the error its records share with every part that names a record (a value,
 * an id, a moment, a number, a file) replaced by a placeholder such as `<value>`, how many records it keeps blocked, held
 * and failed, when they last changed, and its most recently changed record, with that record's own error.
 */
export interface DeliveryIssue {
  /** The issue's id: sixteen hexadecimal characters, what the records listing's `issue` filter takes. */
  issue: string;
  pattern: string;
  records: number;
  held: number;
  failed: number;
  /** When the record of the issue that changed longest ago last changed. */
  oldestUtc: string;
  /** When its most recently changed record last changed. */
  newestUtc: string;
  example: DeliveryRecord | null;
  /**
   * Where the issue lies: `set` when its records carry the same error (no value of their own, or the same values in
   * each), the mistake made once for the whole set; `rows` when they name values of their own rows, each to be fixed in
   * its row. The listing reads it from the newest and the oldest record; an issue's samples tell it more surely.
   */
  shape: DeliveryIssueShape;
  /** The values the example's error names (what its row held); for a set error, the values every record names. */
  values: string[];
}

export type DeliveryIssueShape = "set" | "rows";

/** A record of an issue looked at closely, with the values its error names. */
export interface DeliveryIssueSample {
  record: DeliveryRecord;
  values: string[];
}

/** A flow's issues, the most records first, with how many there are in all and how many blocked records are not sorted yet. */
export interface DeliveryIssueList {
  issues: DeliveryIssue[];
  totalIssues: number;
  totalRecords: number;
  /** Blocked records held or failed before the ledger kept issues, which the control plane sorts a page at a time. */
  unsorted: number;
}

/** An ingestion file some of an issue's records came from; a null name for the records that name none. */
export interface DeliveryIssueFile {
  fileName: string | null;
  records: number;
}

export interface DeliveryIssueDetail {
  /** The issue, its shape as its samples tell it. */
  issue: DeliveryIssue;
  /** The files its records came from, the most records first. */
  files: DeliveryIssueFile[];
  /** Records spread evenly across the issue: its newest, its oldest and the ones between. */
  samples: DeliveryIssueSample[];
}

/** What a release of an issue's records did, and the deliver run it queued when asked to. */
export interface DeliveryIssueReleaseResult {
  issue: string;
  released: number;
  runId: string | null;
}

/** What adopting one record did: the OSDU id it claimed, the version OSDU held there, and the run queued when asked to. */
export interface DeliveryRecordAdoptResult {
  targetId: string;
  version?: number | null;
  runId?: string | null;
}

/** What adopting an issue's records did: how many it adopted, and the deliver run it queued when asked to. */
export interface DeliveryIssueAdoptResult {
  issue: string;
  adopted: number;
  runId: string | null;
}

export interface DeliveryRedeliverResult {
  marked: number;
  runId: string | null;
}

export interface DeliveryPruneResult {
  /** Delivery tries deleted; the latest try of every record is always kept. */
  attemptsPruned: number;
  /** Activities whose captured run log was cleared. The audit row itself is never deleted. */
  activityLogsCleared: number;
  /** Assertion runs whose every result a later one superseded; the latest result of every test is always kept. */
  assertionRunsPruned: number;
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

/** What the Records page asks the ledger for: a term over every flow, and a custody state and a flow to narrow it to. */
export interface DeliveryRecordLookupQuery extends PageQuery {
  /**
   * A delivery key, or the start of an OSDU id, a source key, a label or an ingestion file name. Left out, the
   * records the ledger last touched are listed instead, newest first.
   */
  search?: string;
  status?: DeliveryRecordStatus;
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

/**
 * How much of a record a removal takes away in OSDU. The four are different calls with different promises, not degrees
 * of one thing: "record" and "previous" can be undone, "history" and "everything" cannot. OSDU has no call that removes
 * only the latest version, so "previous" writes the version before it back as a new version, and the replaced one stays
 * in the record's history.
 */
export type RemovalScope = "record" | "previous" | "history" | "everything";

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
}

/** One interface another waits for, or does not wait for, and where that comes from. */
export interface DeliveryInterfaceWait {
  interface: string;
  /** "after" when the document declares it, "schema" when a mapping's reference implies it. */
  origin: string;
  why: string;
}

/**
 * One interface of a source (osdu/docs/reference/flow/interfaces.md): its ledger, how it is delivered and why, what it
 * renders, the order it runs in, and its record counts. A flow in the single form lists one, with no name.
 */
export interface DeliveryInterface {
  /** The interface's name, or null for a flow in the single form. */
  interface: string | null;
  /** The ledger identity its records are keyed by. */
  flowId: string;
  ledger: string;
  route: string;
  routeReason: string | null;
  mapping: string;
  kind: string | null;
  recordObject: string;
  after: string[];
  stats: DeliveryFlowStats;
  /** The wave it runs in: every interface of a wave runs together, after the waves before it. */
  wave: number;
  waitsFor: DeliveryInterfaceWait[] | null;
  notWaitedFor: DeliveryInterfaceWait[] | null;
  /** Why the order shown is only what `after:` gives (a mapping the catalog does not hold, interfaces that wait for each other). */
  orderProblem: string | null;
  /** The parameters the flow declares, whose values fill its record scope; absent from a control plane that predates them. */
  parameters?: DeliveryParameter[] | null;
  /** The record table's key columns, in the order a key's parts are named. */
  keyColumns?: string[] | null;
  /**
   * The partition the interface is described in, for a flow that names its partitions: each partition keeps a ledger of its
   * own. Null for a flow that names none. Listed without a partition, such a flow lists every interface in every partition.
   */
  partition?: string | null;
}

/** A parameter a flow declares: a value of it fills the record scope's predicate and the work location. */
export interface DeliveryParameter {
  name: string;
  required: boolean;
  default?: string | null;
  description?: string | null;
  /**
   * The record table's column the scope predicate binds the parameter to (`source.record.scope`), whose values a page
   * offers for it; null for a parameter the scope does not read. Absent from a control plane that predates it.
   */
  scopeColumn?: string | null;
}

/** One value a scope parameter can take, and how many rows of the record table not marked deleted hold it. */
export interface DeliveryScopeValue {
  value: string;
  rows: number;
}

/** The values a parameter of a flow's scope can take, read from the column the scope binds it to. */
export interface DeliveryScopeParameterValues {
  parameter: string;
  column: string;
  /** The values, the most rows first. */
  values: DeliveryScopeValue[];
  /** True when the column holds more distinct values than were listed. */
  more: boolean;
}

/** What the `delivery-scope-values` task answers: the values each parameter of an interface's scope can take. */
export interface DeliveryScopeValues {
  flow: string;
  interface?: string | null;
  recordObject: string;
  parameters: DeliveryScopeParameterValues[];
  readUtc: string;
}

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
  values: Record<string, string>;
}

/** The render inputs a preview's document comes from. */
export interface DeliveryPreviewInputs {
  mapping: string;
  kind: string;
  templateVersion: string;
  cachePartition?: string | null;
  cacheVersion?: string | null;
  contextHash: string;
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
  previewedUtc: string;
}

/**
 * One interface of a delivery flow that renders with a mapping: what a mapping's value check reads the rows of. The
 * partition is the one the interface is described in, null for a flow that names none.
 */
export interface DeliveryMappingFlow {
  pipelineId: string;
  flow: string;
  interface: string | null;
  partition: string | null;
  ledgerFlowId: string;
  recordObject: string;
  route: string;
}

/**
 * A value check of a flow's rows: the variables to check (none for every variable of the mapping), the scope's values,
 * the rows to read (0 for the whole scope), the example records each finding names, how many it passes over first (the
 * next page of a finding), and the mapping it is asked of, which the flow must render with.
 */
export interface DeliveryValueCheckRequest {
  targets?: string[];
  values?: Record<string, string>;
  maxRows?: number;
  samples?: number;
  skipSamples?: number;
  mapping?: string | null;
}

/**
 * What a variable came to in a row that did not give it a value the template accepts, that the mapping means no value for,
 * or (`asserted`) whose value fails an assertion of the mapping.
 */
export type ValueCheckOutcome = "held" | "invalid" | "empty" | "notApplicable" | "asserted";

/** A variable's rows (or items) by what they came to. */
export interface DeliveryValueCheckCounts {
  valid: number;
  invalid: number;
  empty: number;
  notApplicable: number;
  held: number;
  /**
   * Rows (or items) whose value failed an assertion of the mapping, counted apart from the others: a value that fails an
   * assertion is still one the template accepts, or not.
   */
  asserted: number;
  total: number;
}

/** One record a check names: its key, where its row came from, and what the variable came to there. */
export interface DeliveryValueCheckSample {
  sourceKey: string;
  label?: string | null;
  deliveryKey?: string | null;
  file?: string | null;
  row?: number | null;
  /** For an item of a repeated array, the child row it was written from, from 1. */
  item?: number | null;
  value?: string | null;
  message?: string | null;
}

/** A value and how many times it was met. */
export interface DeliveryValueCheckValue {
  value: string;
  count: number;
}

/** One reason a variable is not written with a value the template accepts, and every occurrence of it. */
export interface DeliveryValueCheckFinding {
  outcome: ValueCheckOutcome;
  /** The variable the reason is about: the one checked, or a property inside the value written there. */
  at: string;
  /** For an invalid value, the rule of the template it breaks; for an asserted one, the assertion it fails. */
  rule?: string | null;
  /** For an asserted value, what a failure of the assertion does. */
  onFail?: AssertionAction | null;
  message: string;
  /** Occurrences: once per row, or once per item of a repeated array. */
  count: number;
  rows: number;
  values: DeliveryValueCheckValue[];
  otherValues: number;
  samples: DeliveryValueCheckSample[];
  /** How many occurrences were passed over before the examples were taken. */
  samplesFrom: number;
}

/** One variable checked: its rows by outcome, the values it was written with, and its findings. */
export interface DeliveryValueCheckVariable {
  target: string;
  /** Where the mapping writes its entry; absent for a variable of data the schema requires that no entry fills directly. */
  entry?: string | null;
  required: boolean;
  /** For an entry of a repeated item, the array its items are written into. */
  repeater?: string | null;
  rows: DeliveryValueCheckCounts;
  items?: DeliveryValueCheckCounts | null;
  values: DeliveryValueCheckValue[];
  distinctValues: number;
  moreValues: boolean;
  findings: DeliveryValueCheckFinding[];
  unlisted: number;
}

/** The rows a check read, and what became of them as records. */
export interface DeliveryValueCheckRows {
  scopeRecords?: number | null;
  read: number;
  checked: number;
  complete: boolean;
  passedOver: number;
  passedOverWhy: { reason: string; count: number; samples: DeliveryValueCheckSample[] }[];
  keyless: number;
  keylessSamples: DeliveryValueCheckSample[];
  clean: number;
  withHeld: number;
  withInvalid: number;
  withEmpty: number;
  /** Rows whose record fails an assertion of the mapping on a variable checked, whatever the failure does. */
  withFailedAssertion: number;
  /** Of those, the rows whose record a failed assertion would hold at the check before sending. */
  heldByAssertion: number;
}

/**
 * The rows of a flow's scope that will not give the variables of its mapping the values the template expects: the
 * `delivery-check-values` task's result. Counts are exact; what is listed is bounded.
 */
export interface DeliveryValueCheck {
  flow: string;
  interface?: string | null;
  flowId: string;
  inputs: { mapping: string; kind: string; templateVersion: string; cachePartition?: string | null; cacheVersion?: string | null };
  asked: { targets: string[]; maxRows: number; samples: number; skipSamples: number; values: Record<string, string> };
  rows: DeliveryValueCheckRows;
  variables: DeliveryValueCheckVariable[];
  issues: string[];
  notes: string[];
  startedUtc: string;
  checkedUtc: string;
}

/**
 * Whether a flow's target answers under its credentials, as the control plane probed it: the route, the endpoint as the
 * flow writes it, and the service's answer (its status, and the path asked).
 */
export interface DeliveryProbeResult {
  flow: string;
  protocol: string;
  endpoint: string;
  auth: string;
  reachable: boolean;
  status: number;
  detail?: string | null;
  path?: string | null;
  checkedUtc: string;
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

/** The listing a removal is aimed at: the same filter the records list is built from. */
export interface DeliveryRecordFilter {
  status?: DeliveryRecordStatus;
  search?: string;
  mode?: "prefix" | "contains";
  submissionId?: string;
  /** The records a submission delivered: what "remove the batch we ran" acts on. */
  deliveredBy?: string;
  runId?: string;
  drifted?: boolean;
  /** The blocked records one issue keeps blocked, by its id. */
  issue?: string;
}

/**
 * Which records of a flow a timeline sync reads: those `keys` names, every one `filter` matches (resolved when the sync
 * is queued; `expected` is the count the operator was shown, and the sync is refused when the filter no longer resolves to
 * it), or, with neither, every record of the flow's interface.
 */
export interface DeliverySyncRequest {
  keys?: string[];
  filter?: DeliveryRecordFilter;
  expected?: number;
}

/**
 * Which of a flow's records a many-record intervention acts on: those `keys` names, every one `filter` matches (refused when
 * it no longer matches the `expected` count the operator was shown), or with neither every record the flow has delivered.
 * With `run` (the default) a deliver run is queued that acts on them; without it the flow's next run does.
 */
export interface DeliveryManyRecords {
  keys?: string[];
  filter?: DeliveryRecordFilter;
  expected?: number;
  run?: boolean;
}

/** A redelivery of many records: `scope` is the part to send again whatever the hashes say, all when it names none. */
export interface DeliveryFlowRedeliverRequest extends DeliveryManyRecords {
  scope?: string;
}

/** How many records were asked to be brought up to date, and the deliver run that brings them when one was queued. */
export interface DeliveryRerenderResult {
  marked: number;
  runId?: string | null;
}

/**
 * The plan run that says what bringing a selection up to date would send: the run to watch, how many delivered records of
 * the selection it checks, and how many the selection holds (`selectedCapped` when the count stopped at the listing's bound).
 */
export interface DeliveryRerenderPreview {
  runId: string;
  checked: number;
  selected: number;
  selectedCapped: boolean;
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
}

/**
 * Records already removed from OSDU to delete from the ledger: named by `keys` or by `filter` with the `expected` count the
 * operator was shown. A record OSDU may still hold is never deleted; it is counted as left.
 */
export interface DeliveryLedgerPurgeRequest {
  keys?: string[];
  filter?: DeliveryRecordFilter;
  expected?: number;
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

/** The run that deletes the ledger: it removes every record from OSDU reversibly, then deletes everything the ledgers keep. */
export interface DeliveryLedgerDeleteAccepted {
  runId: string;
  status: RunStatus;
  partition: string;
  interfaces: string[];
}

/** A removal was queued on a node. */
export interface DeliveryRemovalAccepted {
  taskId: string;
  status: RunStatus;
  scope: RemovalScope;
  records: number;
  /** Whether the removal deletes the records it removes from the ledger too. */
  purgeLedger: boolean;
}

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

/** The shape of the value a template variable takes. */
export type DeliveryTemplateShape = "Value" | "ValueList" | "Group" | "GroupList" | "Whole";

/** Who writes a template variable: a mapping, OSDU Delivery itself (the id and the kind), or OSDU when it stores the record. */
export type DeliveryTemplateRole = "Mapping" | "Engine" | "Osdu";

/**
 * A file of a synced repository's template folder, as the repository's last sync found it: the template it holds, or why it
 * holds none. The file is the template; the control plane reads it where the repository was synced, and keeps none of it.
 */
export interface DeliveryTemplate {
  repoId: string;
  /** The repository's name. */
  repository: string;
  /** The file's path in the repository, such as welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json. */
  path: string;
  /** The template folder it is in, such as welldb/templates. */
  folder: string;
  /** The kind it holds; null when it holds no template. */
  kind: string | null;
  /** The template version it holds, the hash of its schema; null when it holds none. */
  version: string | null;
  /** Why it holds no template; null when it holds one. */
  problem: string | null;
  /** When the repository's last sync saw it. */
  seenUtc: string;
  /** The kind the template is linked to as its parent (x-osdu-inheriting-from-kind); null for a template linked to none. */
  parent?: string | null;
}

/** The repository file a template laid out was read from. */
export interface DeliveryTemplateFile {
  repoId: string;
  repository: string;
  path: string;
}

/**
 * What a template a document of a synced repository names settles to, from the template folders the repository's last
 * sync recorded on the document's path; `suggestedPath` is where a file of the template goes for the document to read it.
 */
export interface DeliveryTemplateResolution {
  found: boolean;
  kind: string;
  version?: string | null;
  file?: string | null;
  problem?: string | null;
  suggestedPath: string;
}

/** One variable of a template: a property of the OSDU record, with what the schema says about it. */
export interface DeliveryTemplateVariable {
  /** The variable's path, such as osdu.data.Name or osdu.data.Curves[].CurveUnit; `[]` steps into an array of objects. */
  path: string;
  shape: DeliveryTemplateShape;
  type: string;
  itemType: string | null;
  format: string | null;
  required: boolean;
  role: DeliveryTemplateRole;
  relationships: string[];
  pattern: string | null;
  unitContext: string | null;
  title: string | null;
  description: string | null;
  /** For an object with free keys (tags): the type of the value under any key. Entries target `<path>.<name>`. */
  keyValueType: string | null;
  /** A list inside a repeated item: listed for reference, never fillable. */
  nested: boolean;
  /** The repository's cached types this variable can be read from, when a repository was given. */
  cacheTypes: string[];
  /** An object the schema leaves open (data.ExtensionProperties): entries may target any path inside it. */
  open: boolean;
}

/**
 * A template laid out variable by variable, parents before their children in schema order. `file` is the repository file it
 * was read from; null for a schema of the OSDU data definitions being looked at.
 */
export interface DeliveryTemplateDetail {
  kind: string;
  version: string;
  title: string | null;
  description: string | null;
  file: DeliveryTemplateFile | null;
  variables: DeliveryTemplateVariable[];
  /** The kind the template is linked to as its parent, whose records its mappings read with $parent; null for none. */
  parent?: string | null;
}

/**
 * A release of the OSDU data definitions, the Open Group's public repository of OSDU schemas: its version tag, the commit
 * the tag names, when that was committed, and the release's schema folder in the repository.
 */
export interface DeliveryOsduRelease {
  name: string;
  commit: string;
  publishedUtc: string | null;
  webUrl: string;
  /** The release is in the control plane's local copy, so reading it needs no download. */
  local: boolean;
}

/** The releases, newest first, and when the control plane last read the list from the repository. */
export interface DeliveryOsduReleases {
  syncedUtc: string | null;
  releases: DeliveryOsduRelease[];
}

/** What a sync did: the release list as read just now, and the releases it downloaded into the local copy. */
export interface DeliveryOsduSync {
  syncedUtc: string;
  releases: DeliveryOsduRelease[];
  downloaded: string[];
}

/** A record schema a release publishes: its kind, its status in the release, and its file with a link to it. */
export interface DeliveryOsduSchema {
  kind: string;
  entityType: string;
  version: string;
  /** PUBLISHED, DEVELOPMENT or OBSOLETE; null when the release's index gives none. */
  status: string | null;
  /** The file under the release's Generated folder, such as master-data/Wellbore.1.3.0.json. */
  path: string;
  webUrl: string;
}

/** Every record schema one release publishes, by entity type and newest version first. */
export interface DeliveryOsduSchemaIndex {
  release: DeliveryOsduRelease;
  schemas: DeliveryOsduSchema[];
}

/** A kind's schema bundled from a release: the template version its file holds, and where it came from. Nothing is kept. */
export interface DeliveryOsduSchemaFile {
  kind: string;
  version: string;
  release: DeliveryOsduRelease;
  path: string;
  webUrl: string;
  origin: string;
  schema: Record<string, unknown>;
}

/** What a difference between two template versions means for a mapping written for the older one. */
export type DeliveryTemplateChangeImpact = "Breaking" | "Additive" | "Wording";

/** A field of a variable that differs between the versions; `before` is null for a new variable, `after` for a removed one. */
export interface DeliveryTemplateFieldChange {
  /** type, format, pattern, keyValueType, unitContext, role, nested, required, relationships, title or description. */
  field: string;
  before: string | null;
  after: string | null;
  impact: DeliveryTemplateChangeImpact;
}

/** A variable that differs between two versions of a template. */
export interface DeliveryTemplateVariableChange {
  path: string;
  change: "Added" | "Removed" | "Changed";
  impact: DeliveryTemplateChangeImpact;
  role: DeliveryTemplateRole;
  fields: DeliveryTemplateFieldChange[];
}

/** One side of a comparison: a kind's version in a release, its status there, the template version it saves as, and its file as published. */
export interface DeliveryOsduComparisonSide {
  kind: string;
  release: DeliveryOsduRelease;
  path: string;
  webUrl: string;
  status: string | null;
  templateVersion: string;
  fileText: string;
}

/** A shared schema file the two versions refer to whose published text differs; a path, link and text are null where a version does not refer to it. */
export interface DeliveryOsduReferencedFile {
  /** The file's name without its version, such as AbstractFacility. */
  name: string;
  fromPath: string | null;
  toPath: string | null;
  fromWebUrl: string | null;
  toWebUrl: string | null;
  fromText: string | null;
  toText: string | null;
}

/** Two versions of a kind from the OSDU data definitions, compared as files and variable by variable. */
export interface DeliveryOsduComparison {
  from: DeliveryOsduComparisonSide;
  to: DeliveryOsduComparisonSide;
  sameFile: boolean;
  /** The files say the same thing once each one's own kind and file name are set aside. */
  onlyIdentifiersDiffer: boolean;
  sameTemplate: boolean;
  breaking: number;
  additive: number;
  wording: number;
  unchanged: number;
  changes: DeliveryTemplateVariableChange[];
  /** How many of the shared schema files both versions refer to are the same. */
  sameReferencedFiles: number;
  /** The shared schema files that differ, or that only one version refers to. */
  referencedFiles: DeliveryOsduReferencedFile[];
}

/**
 * A type a cache holds: the name mappings read it by, its entity type, and the names its values are cached under. A lookup
 * table (entity type lookup--<name>) names its key too; a type of OSDU records has none.
 */
export interface DeliveryCachedType {
  name: string;
  entityType: string;
  fields: string[];
  key: string | null;
}

/** A delivery flow of a repository: its OSDU connection, the mapping and parameters it renders with, and the partition whose cache it reads. */
export interface DeliveryBuilderFlow {
  pipelineId: string;
  name: string;
  mapping: string;
  /**
   * The values a run of the flow renders with: its own render parameters and the kind's defaults for the ones it leaves
   * out, resolved as a run resolves them. A value whose reference the control plane cannot resolve is absent.
   */
  parameters: Record<string, string>;
  /** The reference each value is read from, by parameter, for every value written as one. */
  parameterReferences: Record<string, string>;
  endpoint: string;
  /** The partition the flow delivers to, whose cache it reads; null when its target names none a cache is kept under. */
  cacheScope: string | null;
  /**
   * For a flow that names its partitions, the one partition this entry delivers to: such a flow is offered once per
   * partition it names. Null for a flow that names none.
   */
  partition?: string | null;
}

/** A repository as the mapping builder offers it: the git source a proposal opens against, and its delivery flows. */
export interface DeliveryBuilderRepo {
  repoId: string;
  name: string;
  sourceId: string | null;
  sourceBranch: string | null;
  flows: DeliveryBuilderFlow[];
}

/** A partition's cache as the mapping builder offers it: the partition, the cache flows filling it, its current version and its types. */
export interface DeliveryBuilderCache {
  scope: string;
  flows: string[];
  currentVersion: string | null;
  types: DeliveryCachedType[];
}

/**
 * Where a draft entry's value comes from: a dataset column, a child dataset's rows, a cached record, a fixed value, the
 * id of a record found by searching the platform, a value an expression computes from the row, the first of several
 * alternatives that gives a value ($coalesce), a field of the record one of the mapping's lookups finds ($lookup), a list
 * (of values whose items are fixed values and value nodes, or of objects whose items are groups and fixed objects), a
 * group: an item of a list of objects, the object its properties give, or a section of the record's parent ($parent), for a
 * template linked to a parent.
 */
export type MappingDraftInput = "Dataset" | "Repeat" | "Cache" | "Static" | "Search" | "Expression" | "Coalesce" | "Lookup" | "List" | "Group" | "Parent";

export type MappingDraftModifierKind = "trim" | "upper" | "lower" | "split" | "replace" | "equals" | "date" | "number" | "id" | "ref";

/** A parameter the mapping declares, which the flow supplies under render.parameters. */
export interface MappingDraftParameter {
  name: string;
  required: boolean;
  default: string | null;
  description: string | null;
}

/** One findBy line: the cached field, and the dataset column (without `dataset.`) or the fixed text it must equal. */
export interface MappingDraftFind {
  field: string;
  column: string | null;
  literal: string | null;
}

/** One pair of a replace: the incoming value, and what it becomes; null is no value, so the entry's required flag decides. */
export interface MappingDraftReplacement {
  from: string;
  to: string | null;
}

/** What a replace does with a value its pairs do not list: keep it (the default), give no value, or give a fixed text. */
export type MappingDraftOtherwiseKind = "keep" | "empty" | "text";

/**
 * One modifier with its settings: split takes a separator and a part, replace its pairs (or the cached table it reads them
 * from) and what an unlisted value becomes, equals its text, date an optional format in text, number its decimal and
 * group separators, and id the template the id is built from, in text.
 */
export interface MappingDraftModifier {
  kind: MappingDraftModifierKind;
  separator: string | null;
  part: number | null;
  replacements: MappingDraftReplacement[] | null;
  text: string | null;
  /** number: the separator before the decimals, "." or ",". */
  decimalSeparator: string | null;
  /** number: the separator between groups of three digits, or null when the value is written without one. */
  groupSeparator: string | null;
  /** replace: what a value the pairs do not list becomes; null on every other modifier. */
  otherwiseKind?: MappingDraftOtherwiseKind | null;
  /** replace: the text an unlisted value becomes when otherwiseKind is text. */
  otherwiseText?: string | null;
  /** replace: the cached type the table is read from (replace: $cache.<table>), instead of pairs; null for pairs. */
  table?: string | null;
  /** replace from the cache: the field a value is matched on; null for the table's key. */
  match?: string | null;
  /** replace from the cache: the field that replaces a value; null for the one field a lookup table holds beside its key. */
  field?: string | null;
}

/** One entry as the builder edits it. */
export interface MappingDraftEntry {
  target: string;
  input: MappingDraftInput;
  /** Dataset: `column`, or `child.column` for an entry inside a repeater. */
  column: string | null;
  /** Repeat: the child dataset whose rows become the items. */
  child: string | null;
  /** Cache: the cached type, such as UnitOfMeasure. Search: the search, by the name the mapping declares it under. */
  cacheType: string | null;
  /** Cache and Lookup: the field of the record to read, usually id. Search: always id, the one thing a search returns. */
  cacheField: string | null;
  /** Lookup: the lookup read, by the name the mapping declares it under. */
  lookup: string | null;
  /** Parent: the path of the section of the record's parent it takes, from the record's root, such as data.DDMSDatasets. */
  parentPath?: string | null;
  findBy: MappingDraftFind[];
  /** Cache: every matching row read ($findAll) in place of findBy lines; null for an entry that finds one record. */
  findAll: MappingDraftFindAll | null;
  modifiers: MappingDraftModifier[];
  /** Expression: the expression the value is computed with, as the record tree writes it in the entry's scope. */
  expression: string | null;
  /** The condition (`$when`) that decides whether the property is written for a row, such as `status = "FINAL"`; null always writes it. */
  when: string | null;
  /** Repeat: the condition (`$where`) a child row must hold to become an item; null keeps every row. */
  where: string | null;
  required: boolean;
  ignoreSeparators: boolean;
  /**
   * An entry that builds an id with id or ref ($unverified): the id is written even when the cache holds records of its
   * entity type and not this one, and recorded as an unverified reference.
   */
  unverified: boolean;
  /**
   * Coalesce: the alternatives in the order they are tried, each an entry of its own input; their target, condition,
   * required flag and description are this entry's.
   */
  alternatives: MappingDraftEntry[];
  /**
   * List: the items in the order they are written, each an entry of its own input. A fixed item of a list of values holds
   * one value, with its own condition, required flag and description; an item of a list of objects is a group or a fixed
   * object. Their target is this entry's, which itself takes no condition.
   */
  items: MappingDraftEntry[];
  /**
   * Group, an item of a list of objects: its properties in the order they are written, each an entry of its own input
   * whose target is the variable inside the list's items it fills (`osdu.data.TechnicalAssurances[].TechnicalAssuranceTypeID`).
   * The group itself takes no condition, required flag or description; its properties do.
   */
  properties: MappingDraftEntry[];
  /** Static: the value as JSON text, such as "[\"a\"]", "\"MD\"", "5" or "true". */
  static: string | null;
  description: string | null;
  /**
   * The entry's assertions ($assert, osdu/docs/reference/flow/mapping-assertions.md), in the order they are written. A
   * coalesce entry's are the entry's, never an alternative's; a fixed value, a list and an item of a list of values take none.
   */
  assertions?: MappingDraftAssertion[];
  /** True when the builder proposed the entry from the cache, until someone edits it. */
  prefilled: boolean;
}

/**
 * One assertion of an entry as the builder edits it: the condition's operator and its operand as the document writes it
 * after the operator (`[0, 250]`, `'GR'`, `{ atLeast: 1 }`), the value it judges, what a record that fails it does, how it
 * reads several values, the conditions it is judged under, its name and its description.
 */
export interface MappingDraftAssertion {
  operator: string;
  /** One line of YAML, as it follows the operator. */
  operand: string;
  stage: AssertionStage;
  onFail: AssertionAction;
  /** For a property holding several values: true when one of them has to meet it, false when every one does. */
  anyValue: boolean;
  ignoreCase: boolean;
  tolerance: number | null;
  where: MappingDraftAssertionFilter[];
  name: string | null;
  description: string | null;
}

/**
 * One condition an assertion is judged under: what it reads (`field`, a path into the record, for the record stage;
 * `column`, a column as the draft names one, for the incoming stage), its operator and its operand.
 */
export interface MappingDraftAssertionFilter {
  reads: "field" | "column";
  path: string;
  operator: string;
  operand: string;
  ignoreCase: boolean;
  tolerance: number | null;
}

/**
 * A cache entry's $findAll: the cached field the rows are found by, compared with exactly one of a dataset column, a fixed
 * text, or a field of a lookup's record written with the lookup's name (`wellbore.GeoContexts.FieldID`); and the fields a
 * row must hold nothing under.
 */
export interface MappingDraftFindAll {
  field: string;
  column: string | null;
  literal: string | null;
  lookup: string | null;
  empty: string[];
}

/**
 * A record the mapping finds once for a row and reads wherever the record needs it (lookups.<name>): the cached type it is
 * found in, the findBy lines comparing columns of the dataset's own row, and the modifiers and separator fold a cache
 * entry takes.
 */
export interface MappingDraftLookup {
  name: string;
  cacheType: string;
  findBy: MappingDraftFind[];
  modifiers: MappingDraftModifier[];
  ignoreSeparators: boolean;
  description: string | null;
}

/**
 * One record set the mapping searches the platform for as a record needs one, rather than capturing it into the cache:
 * the name entries read it by, the kind it looks in, and the template whose schema says how that kind is indexed.
 */
export interface MappingDraftSearch {
  name: string;
  kind: string;
  schemaKind: string;
  schemaVersion: string;
  description: string | null;
}

/** What a mapping makes its records' OSDU ids from: the delivery key (a GUID), or the key's own values (`WELLDB::GAPI`). */
export type MappingIdFrom = "deliveryKey" | "key";

/** A mapping as the builder edits it: the header, the parameters and the entries. */
export interface MappingDraft {
  name: string;
  version: string;
  templateKind: string;
  templateVersion: string;
  description: string | null;
  system: string;
  /** The dataset columns of the key, without `dataset.`. */
  key: string[];
  /** The label as written, with {column} tokens naming columns of the dataset's own row. */
  label: string | null;
  /** The dataset columns an operator finds a record by, without `dataset.`. */
  identity: string[];
  /** What the OSDU id is made from, as `dataset.idFrom` writes it: `key` for the key's own values; `deliveryKey` or absent for the delivery key. */
  idFrom?: MappingIdFrom | null;
  parameters: MappingDraftParameter[];
  /** The record sets the mapping's search entries look in. */
  searches: MappingDraftSearch[];
  /** The records the mapping finds once for a row, which lookup entries and find all lines read. */
  lookups: MappingDraftLookup[];
  entries: MappingDraftEntry[];
}

/** What the builder found about a draft: an error stops the mapping from loading, a warning does not. */
export interface MappingDraftIssue {
  severity: "error" | "warning";
  message: string;
  target: string | null;
}

/** Starts a mapping for the template a synced repository's file holds. */
export interface DeliveryMappingDraftRequest {
  /** The partition whose cache the draft's cache entries are prefilled from; null prefills none. */
  scope: string | null;
  /** The repository holding the template. */
  repoId: string;
  /** The template file's path in the repository. */
  templatePath: string;
  name: string;
  mappingVersion: string;
  system: string;
}

/** A draft written as YAML, what the checks found, and whether the mapping loads and passes the preflight. */
export interface DeliveryMappingComposeResult {
  yaml: string;
  issues: MappingDraftIssue[];
  valid: boolean;
}

/** A mapping document read back into a draft, or the problems that stopped it. */
export interface DeliveryMappingParseResult {
  draft: MappingDraft | null;
  issues: MappingDraftIssue[];
}

/**
 * Whether what a mapping writes reaches a variable on every row (`Always`), on some rows (`Sometimes`: an entry that is
 * `required: false` or only applies to some rows), or never (`Empty`: no entry names it and nothing fills what it holds).
 */
export type CoverageState = "Empty" | "Sometimes" | "Always";

/** How a mapping fills one variable of the template it pins. */
export interface DeliveryMappingCoverageVariable {
  target: string;
  state: CoverageState;
  /** True when an entry targets the variable itself; false for an object filled through what it holds. */
  direct: boolean;
  required: boolean;
  /**
   * For a variable no entry targets that a static value further up writes (the TechnicalAssuranceTypeID of the items of a
   * static TechnicalAssurances list): the target of the entry whose static value it is.
   */
  writtenBy?: string | null;
  /** With writtenBy: the values that static value gives the variable, once each; empty for an object or a list. */
  values?: string[] | null;
}

/**
 * What a mapping fills of the template version it pins: every variable a mapping may fill with how the document reaches
 * it, and what it leaves required and empty. The errors are the ones the delivery gate raises; the warnings are the
 * required variables the gate does not check. `variables` is empty when the document does not load or its template
 * cannot be read, and `issues` says which.
 */
export interface DeliveryMappingCoverage {
  kind: string | null;
  version: string | null;
  variables: DeliveryMappingCoverageVariable[];
  issues: MappingDraftIssue[];
}

/** A parameter a mapping declares, and the value its record shape was drawn with: the one given, else the default. */
export interface DeliveryMappingShapeParameter {
  name: string;
  required: boolean;
  default: string | null;
  description: string | null;
  value: string | null;
}

/**
 * The shape of the records a mapping renders, drawn by the renderer delivery uses: a placeholder naming the template's
 * type and the source wherever a value comes from a row or the cache. `record` is null when an issue stopped it.
 */
export interface DeliveryMappingShapeResult {
  record: Record<string, unknown> | null;
  parameters: DeliveryMappingShapeParameter[];
  notes: string[];
  issues: MappingDraftIssue[];
}

/**
 * Where one record lives: its flow's ledger id and its delivery key. A key alone names one record per flow that reads
 * the row, so every record address carries both, in the API and in the GUI.
 */
export interface DeliveryRecordRef {
  flowId: string;
  deliveryKey: string;
}

const recordApiPath = ({ flowId, deliveryKey }: DeliveryRecordRef) =>
  `/api/v1/delivery/records/${encodeURIComponent(flowId)}/${encodeURIComponent(deliveryKey)}`;

/** The GUI page of one record. */
export const deliveryRecordRoute = ({ flowId, deliveryKey }: DeliveryRecordRef) =>
  `/delivery/records/${encodeURIComponent(flowId)}/${encodeURIComponent(deliveryKey)}`;

/**
 * One OSDU partition: whether the registry holds it and whether it is the default, and what it holds (its cache, the
 * delivery flows that deliver to it). What the title bar's partition switcher and the Partitions page list.
 */
export interface DeliveryPartition {
  name: string;
  /** What it is for, in the words of whoever registered it. */
  description: string | null;
  /** The partition a run that names none runs in. */
  isDefault: boolean;
  /**
   * Whether the registry holds it. An unregistered partition is listed while something is kept under it: a flow that
   * hard-codes it, or a cache or ledger left from before it was removed.
   */
  registered: boolean;
  createdUtc: string | null;
  createdBy: string | null;
  updatedUtc: string | null;
  updatedBy: string | null;
  /** The version of its cache deliveries read, or null while it holds none. */
  currentVersion: string | null;
  capturedUtc: string | null;
  types: number;
  items: number;
  /** The cache flows that fill its cache. */
  cacheFlows: string[];
  /** The delivery flows that deliver to it. */
  deliveryFlows: string[];
  /** Cache changes found in it that wait for a decision. */
  pendingChanges: number;
  /** How many ledgers the ledger's directory keeps under it: one per interface that delivered or retrieved there. */
  ledgers?: number;
  /**
   * Whether the control plane's configuration names it (Osdu:Partitions:<name>): registered from there, and described,
   * made the default and removed only there.
   */
  configured: boolean;
}

/**
 * Which ledger of a flow a request is about: the interface of a source (null for the single form), and the partition of a
 * flow that names its partitions (null for one that names none). A flow keeps a ledger per interface and partition, so its
 * records, submissions, target, previews and interventions are read and acted on one ledger at a time, never summed.
 */
export interface DeliveryFlowScope {
  interfaceName?: string | null;
  partition?: string | null;
}

/** What a ledger is called wherever a page names it: the flow, the interface of a source, and the partition it delivers to. */
export const ledgerLabel = (flowName: string, scope?: DeliveryFlowScope): string =>
  `${flowName}${scope?.interfaceName ? ` / ${scope.interfaceName}` : ""}${scope?.partition ? ` in partition ${scope.partition}` : ""}`;

/**
 * The link to a flow's page showing one ledger: `tab` (records, submissions) in the interface and partition of `scope`,
 * with any further query parameters a view reads.
 */
export const flowLedgerRoute = (pipelineId: string, scope?: DeliveryFlowScope, extra: Record<string, string> = {}): string => {
  const params = new URLSearchParams(extra);
  if (scope?.interfaceName) {
    params.set("interface", scope.interfaceName);
  }

  if (scope?.partition) {
    params.set("partition", scope.partition);
  }

  const query = params.toString();
  return `/pipelines/${pipelineId}${query === "" ? "" : `?${query}`}`;
};

/** The query parameters naming a scope's interface and partition. */
const scopeQuery = (scope?: DeliveryFlowScope): Record<string, string> => ({
  ...(scope?.interfaceName ? { interface: scope.interfaceName } : {}),
  ...(scope?.partition ? { partition: scope.partition } : {}),
});

/**
 * A flow-level path with the ledger the request is about. A source that delivers several interfaces, or a flow that names
 * several partitions, answers nothing without one, because its records, submissions, target and counts are per ledger.
 */
export const flowPath = (pipelineId: string, suffix: string, scope?: DeliveryFlowScope) => {
  const query = new URLSearchParams(scopeQuery(scope)).toString();
  return `/api/v1/delivery/flows/${pipelineId}${suffix}${query === "" ? "" : `?${query}`}`;
};

// ---- Assertion flows ---------------------------------------------------------------------------------------------------

/** How one test came out in one run, or how one assertion of it did (`passed`, `failed`, `errored` or `skipped`). */
export type TestOutcome = "passed" | "failed" | "warned" | "errored" | "skipped";

/** An assertion run's status: running while its tests run, then how they came out, or why it stopped. */
export type AssertionRunStatus = "running" | "passed" | "failed" | "errored" | "cancelled";

export type AssertionSeverity = "error" | "warning" | "info";

/** The formats a run's report downloads in. */
export type AssertionReportFormat = "html" | "md" | "json" | "junit";

/** A check of an assertion as the flow document states it. */
export interface DeliveryAssertionDefinition {
  index: number;
  label: string;
  type: string;
  severity: AssertionSeverity;
  expected: string;
  description: string | null;
  /** Why the check is disabled (its `disabled`); absent or null when it is judged. */
  disabled?: string | null;
  /** The day what it expects was stated (its own `asOf`, else its assertion's). */
  asOf?: string | null;
  /** The last day what it expects holds (`validUntil`, or `validFor` after `asOf`); absent when it holds for good. */
  validUntil?: string | null;
  /** True when that day is past: a run flags the check as needing new proof values instead of judging it. */
  needsNewProofValues?: boolean;
}

/** One run's outcome of a test, as a history strip or a matrix cell shows it. */
export interface DeliveryAssertionPoint {
  assertionRunId: number;
  outcome: TestOutcome;
  failedAssertions: number;
  matched: number | null;
  completedUtc: string;
}

/** A test's latest result, without its detail. */
export interface DeliveryAssertionLatest {
  resultId: number;
  assertionRunId: number;
  outcome: TestOutcome;
  severity: AssertionSeverity | null;
  matched: number | null;
  evaluated: number | null;
  sampled: boolean;
  assertions: number;
  failedAssertions: number;
  durationMs: number;
  error: string | null;
  completedUtc: string;
  definitionHash: string;
}

/**
 * One test on a board: what the flow document says of it, whether it fits the template of its kind (`problems`), its
 * latest result, how it came out over the last runs (newest first), and whether it changed since that result.
 */
export interface DeliveryAssertionTest {
  name: string;
  description: string | null;
  kind: string;
  tags: string[];
  severity: AssertionSeverity;
  query: string | null;
  ids: number;
  bulk: boolean;
  read: "storage" | "index";
  maxRecords: number;
  assertions: DeliveryAssertionDefinition[];
  /** False when the test names partitions and the board's partition is not one of them. */
  runsHere: boolean;
  partitions: string[];
  /** The version of the template of its kind its fields are checked against; null when it reads no field of an exact kind. */
  template: string | null;
  /** The mapping (`Name@version`) whose pinned template it is checked against (`template: WellLog@1.5.0`). */
  templateMapping?: string | null;
  /** True when it names its template version itself; false takes the newest captured, or the mapping's. */
  templatePinned?: boolean;
  /** Why it is disabled (its `disabled`); absent or null when it runs. */
  disabled?: string | null;
  /** The day what it expects was stated (`asOf`). */
  asOf?: string | null;
  /** The last day what it expects holds; absent when it holds for good. */
  validUntil?: string | null;
  /** True when every check it judges is past that day, so a run flags it as needing new proof values. */
  needsNewProofValues?: boolean;
  problems: string[];
  latest: DeliveryAssertionLatest | null;
  history: DeliveryAssertionPoint[];
  changed: boolean;
  definitionHash: string;
}

/** An assertion run as listings show it. */
export interface DeliveryAssertionRun {
  assertionRunId: number;
  flowId: string;
  flowName: string;
  partition: string | null;
  runId: string | null;
  actor: string;
  /** The tests the run was asked for, as JSON; null when it ran every test. */
  selection: string | null;
  status: AssertionRunStatus;
  tests: number;
  passed: number;
  failed: number;
  warned: number;
  errored: number;
  skipped: number;
  startedUtc: string;
  completedUtc: string | null;
  error: string | null;
}

/** One assertion flow on a board, in the board's partition. */
export interface DeliveryAssertionFlow {
  pipelineId: string;
  repoId: string;
  name: string;
  description: string | null;
  batch: string | null;
  partition: string | null;
  /** False when the flow does not test the board's partition (or its document does not parse: see `problem`). */
  testsPartition: boolean;
  /** False when the catalog's copy of the document does not parse, which needs a fix; a flow of another partition parses. */
  parses: boolean;
  partitions: string[];
  failRunOn: "error" | "warning" | "never";
  /** The parameters a run of the flow takes, which its tests use as `{name}` tokens. */
  parameters: DeliveryParameter[];
  problem: string | null;
  lastRun: DeliveryAssertionRun | null;
  tests: DeliveryAssertionTest[];
}

/** What a board adds up to over every test that runs in its partition. */
export interface DeliveryAssertionTotals {
  flows: number;
  tests: number;
  passed: number;
  failed: number;
  warned: number;
  errored: number;
  notRun: number;
  problems: number;
  changed: number;
  passRate: number | null;
}

export interface DeliveryAssertionBoard {
  partition: string | null;
  totals: DeliveryAssertionTotals;
  flows: DeliveryAssertionFlow[];
}

/** A record that failed an assertion: what it held there and why it failed. */
export interface DeliveryAssertionExample {
  id?: string | null;
  value?: string | null;
  reason: string;
}

/** How one assertion of a test came out in one run. */
export interface DeliveryAssertionOutcome {
  index: number;
  label: string;
  description?: string | null;
  type: string;
  severity: AssertionSeverity;
  outcome: TestOutcome;
  expected: string;
  actual?: string | null;
  message?: string | null;
  checked?: number | null;
  failing?: number | null;
  /** The number the assertion measured (a count, an aggregate, the share that held), for a trend. */
  value?: number | null;
  examples?: DeliveryAssertionExample[];
  examplesTrimmed?: boolean;
}

/** One test's whole result in one run: what it read, and every assertion. */
export interface DeliveryTestResult {
  test: string;
  description?: string | null;
  kind: string;
  tags?: string[];
  outcome: TestOutcome;
  severity?: AssertionSeverity | null;
  matched?: number | null;
  evaluated?: number | null;
  sampled?: boolean;
  query?: string | null;
  ids?: number;
  template?: string | null;
  definitionHash: string;
  durationMs?: number;
  error?: string | null;
  problems?: string[];
  notes?: string[];
  assertions?: DeliveryAssertionOutcome[];
  startedUtc?: string;
  completedUtc?: string;
}

/** An assertion run with every test's result. */
export interface DeliveryAssertionRunDetail {
  run: DeliveryAssertionRun;
  pipelineId: string | null;
  results: DeliveryTestResult[];
}

/** A flow's recent runs against its tests: each test's outcome in each run, null where the run did not run it. */
export interface DeliveryAssertionMatrix {
  runs: DeliveryAssertionRun[];
  tests: { test: string; kind: string; cells: (DeliveryAssertionPoint | null)[] }[];
}

// ---- Dimension flows ---------------------------------------------------------------------------------------------------

/** What a dimension build came to: running while it reads, then completed, failed or cancelled. */
export type DimensionBuildStatus = "running" | "completed" | "failed" | "cancelled";

/** Why a key belongs to no value. */
export type DimensionLeftOut = "empty" | "tooLong" | "dropped" | "failed";

/** What a build changed of one key. */
export type DimensionChangeKind = "added" | "removed" | "moved" | "restored";

/** How the index stores a dimension's field, as its builds settled it. */
export interface DeliveryDimensionField {
  /** text, keyword, number, boolean or date. */
  index: string;
  nestedPath: string | null;
  aggregateBy: string;
  /** Whether one record can hold the field more than once, so a value's records can be counted as the sum of its keys'. */
  repeats: boolean;
}

/** What one build changed of a dimension: its values that arrived, left or came back, and its keys that arrived, left, moved or came back. */
export interface DeliveryDimensionChanges {
  valuesAdded: number;
  valuesRemoved: number;
  valuesRestored: number;
  keysAdded: number;
  keysRemoved: number;
  keysMoved: number;
  keysRestored: number;
}

/** A kind a build read, with its records and the template it was read against. */
export interface DeliveryDimensionKind {
  kind: string;
  records: number;
  template: string | null;
}

/** One build of a dimension: who ran it, what came of it, how it read, how complete the keys are, and what it changed. */
export interface DeliveryDimensionBuild {
  buildId: number;
  dimensionId: number;
  runId: string | null;
  actor: string;
  status: DimensionBuildStatus;
  startedUtc: string;
  completedUtc: string | null;
  error: string | null;
  definitionHash: string;
  query: string | null;
  aggregateBy: string | null;
  values: number;
  keys: number;
  leftOut: number;
  unfilterable: number;
  /** The records the query matched; null when they could not be counted. */
  records: number | null;
  /** The records holding a key the index can aggregate; null when not counted. */
  withValue: number | null;
  nulls: number;
  /** The records holding only text longer than the index's exact field keeps; null when not counted. */
  tooLong: number | null;
  unreadable: number;
  aggregations: number;
  slices: number;
  splits: number;
  scannedSlices: number;
  scanPages: number;
  scannedUnits: number;
  countQueries: number;
  /** Keys a label was read for. */
  labelled: number;
  /** Keys of a labelled dimension that have no label, each its own value. */
  unlabelled: number;
  /** Searches the labels were read with. */
  labelQueries: number;
  kinds: DeliveryDimensionKind[];
  notes: string[];
  changes: DeliveryDimensionChanges;
  /** How the build loaded the dimension: every key (`full`), or the keys of what changed in its window (`incremental`). */
  load: DimensionLoad;
  /** Where an incremental load's window began; omitted for a full load. */
  windowFrom?: string | null;
  /** Up to when the build read what changed, which the next incremental load reads on from. */
  windowTo?: string | null;
  /** The records an incremental load found changed in its window; omitted for a full load. */
  changedRecords?: number | null;
  /** The keys an incremental load read again; omitted for a full load. */
  touchedKeys?: number | null;
}

/** How a build loads a dimension. */
export type DimensionLoad = "full" | "incremental";

/** A dimension flow's incremental block: how far behind now a window ends, how old a full load may get, and whether every build loads in full. */
export interface DeliveryDimensionIncremental {
  lagMinutes: number;
  fullLoadAfterHours?: number | null;
  fullLoad: boolean;
}

/**
 * One dimension of a dimension flow in a partition: its declaration, how the index stores its field, what it holds, the
 * build that wrote that (`current`), and the newest build when that is another one (`latest`: failed, cancelled or running).
 */
export interface DeliveryDimension {
  /** Null for a dimension no build has registered yet. */
  dimensionId: number | null;
  name: string;
  description: string | null;
  kind: string;
  /** The query as the flow declares it, its tokens unfilled. */
  query: string | null;
  /** The query as the build that wrote the values ran it. */
  builtQuery: string | null;
  path: string;
  /** Where a key's label is read: the paths through the records a key names; empty when keys are their own values. */
  label: string[];
  /** The value of a key whose label is not read ("Not specified"); null values such a key by its id's code. */
  unlabelled: string | null;
  /** The attributes each key is read with, each by its name and the paths it is read through. */
  attributes: DeliveryDimensionAttributeSpec[];
  /** The clean steps, as the document writes them. */
  clean: string[];
  countRecords: boolean;
  maxValues: number;
  /** The flow still declares it. */
  declared: boolean;
  /** Its declaration builds in the board's partition. */
  buildsHere: boolean;
  /** The declaration differs from the one its values were built with. */
  changed: boolean;
  field: DeliveryDimensionField | null;
  values: number;
  keys: number;
  lastBuiltUtc: string | null;
  current: DeliveryDimensionBuild | null;
  latest: DeliveryDimensionBuild | null;
  /** The dimension's own table in the database (`osdu.dim_<dimension>`); null until a build has written it. */
  table: string | null;
  /**
   * What the dimension calls its key: the property its path ends with (`WellboreID`), with `Key` at its end where its value
   * has that name (`SourceKey`), unless the flow names it. The column of its table that holds each key is named so after
   * its next build.
   */
  keyColumn: string;
  /** What the dimension calls its value: the property its label ends with (`FacilityName`), or its own name with no label, unless the flow names it. */
  valueColumn: string;
  /** When the dimension was last loaded in full; omitted until a full load has completed. */
  lastFullLoadUtc?: string | null;
}

/** One dimension flow in the partition a board is read in. */
export interface DeliveryDimensionFlow {
  pipelineId: string;
  repoId: string;
  name: string;
  description: string | null;
  batch: string | null;
  partition: string | null;
  buildsPartition: boolean;
  partitions: string[];
  ledgerId: string | null;
  parameters: DeliveryParameter[];
  problem: string | null;
  dimensions: DeliveryDimension[];
  /** How the flow's builds load after a first full load; omitted for a flow whose every build loads in full. */
  incremental?: DeliveryDimensionIncremental | null;
}

/** What a board adds up to, over the dimensions of the flows that build in its partition. */
export interface DeliveryDimensionTotals {
  flows: number;
  dimensions: number;
  built: number;
  notBuilt: number;
  failing: number;
  running: number;
  changed: number;
  values: number;
  keys: number;
}

export interface DeliveryDimensionBoard {
  partition: string | null;
  totals: DeliveryDimensionTotals;
  flows: DeliveryDimensionFlow[];
}

/** One dimension with the flow that declares it. */
export interface DeliveryDimensionDetail {
  pipelineId: string | null;
  repoId: string | null;
  flowName: string;
  ledgerId: string;
  partition: string | null;
  parameters: DeliveryParameter[];
  dimension: DeliveryDimension;
}

/**
 * An attribute a dimension reads of its keys: its name, and the paths it is read through from the record a key names, or
 * the path of the dimension's own records whose values it collects (`collect`, with no steps).
 */
export interface DeliveryDimensionAttributeSpec {
  name: string;
  steps: string[];
  collect: string | null;
}

/**
 * A value of an attribute of a key: the value, where it was read (the id of the record it was read from, or the text the
 * key's records hold for a collected attribute), and for a collected attribute how many of the key's records hold it. A key
 * holds one value of an attribute read from the record it names, and any number of a collected one.
 */
export interface DeliveryDimensionAttribute {
  name: string;
  value: string;
  from: string | null;
  records: number | null;
}

/** A value an attribute holds among a value's keys, with how many of them hold it. */
export interface DeliveryDimensionValueAttribute {
  name: string;
  value: string;
  keys: number;
}

/** What removing a dimension took out of the ledger: the dimension, the rows kept of it in each table, and one line saying so. */
export interface DeliveryDimensionRemoved {
  dimensionId: number;
  dimension: string;
  flow: string;
  partition: string | null;
  values: number;
  keys: number;
  builds: number;
  changes: number;
  attributes: number;
  texts: number;
  summary: string;
}

/** A value an attribute holds among a dimension's keys: the keys holding it and their records (summed). */
export interface DeliveryDimensionAttributeValue {
  value: string;
  keys: number;
  records: number;
}

/** A key as a list of values shows it beside its value. */
export interface DeliveryDimensionKeyBrief {
  key: string;
  label: string | null;
  count: number;
}

/** A value of a dimension: the human-friendly value a person picks, its records, the keys it stands for and its search filter. */
export interface DeliveryDimensionValue {
  valueId: number;
  value: string;
  records: number;
  /** Whether `records` counts records; otherwise it is the sum of its keys' counts. */
  recordsExact: boolean;
  keys: number;
  unfilterable: number;
  /** The search filter finding the value's records, when one query holds it. */
  filter: string | null;
  filterParts: number;
  firstSeenBuildId: number;
  firstSeenUtc: string;
  removedBuildId: number | null;
  removedUtc: string | null;
  /** The keys most records hold. */
  top: DeliveryDimensionKeyBrief[];
  /** The values its keys' attributes hold, the most keys first. */
  attributes: DeliveryDimensionValueAttribute[];
}

export interface DeliveryDimensionValuePage {
  items: DeliveryDimensionValue[];
  next: string | null;
}

/**
 * A key of a dimension, exactly as the index holds it (an id for a reference): the label read for it and the record it was
 * read from, its value or why it has none, and the search filter finding exactly its records.
 */
export interface DeliveryDimensionKey {
  keyId: number;
  key: string;
  label: string | null;
  labelFrom: string | null;
  valueId: number | null;
  value: string | null;
  leftOut: DimensionLeftOut | null;
  note: string | null;
  count: number;
  filterable: boolean;
  filter: string | null;
  firstSeenBuildId: number;
  firstSeenUtc: string;
  valueSinceBuildId: number;
  removedBuildId: number | null;
  removedUtc: string | null;
  /** Its attributes, as its last build read them. */
  attributes: DeliveryDimensionAttribute[];
}

export interface DeliveryDimensionKeyPage {
  items: DeliveryDimensionKey[];
  next: string | null;
}

/** A change a build made to one key. */
export interface DeliveryDimensionChange {
  changeId: number;
  buildId: number;
  keyId: number;
  key: string;
  change: DimensionChangeKind;
  fromValueId: number | null;
  fromValue: string | null;
  toValueId: number | null;
  toValue: string | null;
  changedUtc: string;
}

export interface DeliveryDimensionChangePage {
  items: DeliveryDimensionChange[];
  /** The change the next page starts before; null when this is the last. */
  next: number | null;
}

/** The search filter of a set of values, with what it covers and what it leaves out. */
export interface DeliveryDimensionFilter {
  kind: string;
  query: string | null;
  aggregateBy: string;
  filters: string[];
  /** Each filter joined with the dimension's own query: the searches to send. */
  searches: string[];
  values: DeliveryDimensionFilterValue[];
  keys: number;
  unfilterable: number;
  unfilterableNamed: string[];
  removed: string[];
  missing: string[];
}

/** A value with its keys, its filter (or why none can be written) and its history. */
export interface DeliveryDimensionValueDetail {
  value: DeliveryDimensionValue;
  keys: DeliveryDimensionKey[];
  moreKeys: boolean;
  filter: DeliveryDimensionFilter | null;
  filterProblem: string | null;
  history: DeliveryDimensionChange[];
}

/** A build a platform run made, with the dimension it built. */
export interface DeliveryDimensionRunBuild {
  dimension: string;
  build: DeliveryDimensionBuild;
}

// ---- Dimension blueprints ----------------------------------------------------------------------------------------------

/** What a read of a blueprint is for: the dimension's key, its label, an attribute read through a key's record, or one collected from its own records. */
export type BlueprintRole = "key" | "label" | "attribute" | "collect";

/** What a column of a dimension's table holds. */
export type BlueprintColumnRole = "id" | "partition" | "keyId" | "key" | "value" | "records" | "filter" | "attribute";

/** A template a blueprint describes a part of the dimension by. */
export interface BlueprintTemplate {
  kind: string;
  version: string;
}

/** What a read is for: its role, the attribute it reads, its step in that chain from 0, and whether it is the chain's last. */
export interface BlueprintUse {
  role: BlueprintRole;
  attribute: string | null;
  step: number;
  last: boolean;
}

/** A path segment's filter, as the template describes the property it compares. */
export interface SchemaFilterReading {
  property: string;
  compare: "equals" | "contains" | "endsWith";
  text: string;
  /** Whether the objects the segment holds declare the property. */
  known: boolean;
  description: string | null;
}

/**
 * One segment of a path as the template describes it: whether it declares it, its type (and its items'), how the index
 * stores an array of objects, the entity types it names, and the forms of a choice that declare it.
 */
export interface SchemaSegmentReading {
  name: string;
  filter: SchemaFilterReading | null;
  found: boolean;
  type: string | null;
  itemType: string | null;
  format: string | null;
  title: string | null;
  description: string | null;
  indexing: "nested" | "flattened" | string | null;
  references: string[];
  branch: string | null;
}

/** A path as one kind's template describes it, and why the description stops where a segment is not in it. */
export interface SchemaPathReading {
  kind: string;
  segments: SchemaSegmentReading[];
  problem: string | null;
}

/** How a build's search reads a path of the dimension's own records. */
export interface BlueprintIndex {
  index: string;
  nestedPath: string | null;
  aggregateBy: string;
  repeats: boolean;
}

/**
 * One path a build reads, in the records it reads it from: what it is read for, what the template says of it, the entity
 * types its value names, the records read next through it, how the search reads it (the dimension's own records), and what
 * keeps it from being read as the flow means it.
 */
export interface BlueprintRead {
  id: string;
  path: string;
  uses: BlueprintUse[];
  schema: SchemaPathReading | null;
  references: string[];
  leadsTo: string | null;
  index: BlueprintIndex | null;
  problem: string | null;
}

/** Records a build reads by id, one step on: their depth, entity types, the read whose ids name them, their template, and the paths read. */
export interface BlueprintRecords {
  id: string;
  depth: number;
  entityTypes: string[];
  from: string;
  template: BlueprintTemplate | null;
  missing: string | null;
  reads: BlueprintRead[];
}

/** A kind of the dimension's own records: its records where a build counted them, and its template's version. */
export interface BlueprintKind {
  kind: string;
  records: number | null;
  template: string | null;
}

/** The dimension's own records: the pattern and query that choose them, their kinds and template, and the reads: the key, then each collected attribute. */
export interface BlueprintSource {
  kind: string;
  query: string | null;
  /** True when the kinds are those the last build read; false when they are the templates the flow reads that the pattern matches. */
  built: boolean;
  kinds: BlueprintKind[];
  template: BlueprintTemplate | null;
  missing: string | null;
  reads: BlueprintRead[];
}

/** A column of the dimension's table, in the table's order, with the reads it is written from. */
export interface BlueprintColumn {
  name: string;
  role: BlueprintColumnRole;
  attribute: string | null;
  from: string[];
}

/** How a dimension is built: its own records, the records read through each key step by step, and the table's columns. */
export interface DimensionBlueprint {
  source: BlueprintSource;
  records: BlueprintRecords[];
  columns: BlueprintColumn[];
}

/** Where one thing a dimension declares is written in its flow's YAML: lines and columns from 1, the end column after the last character. */
export interface DimensionYamlSpan {
  target: string;
  line: number;
  column: number;
  endLine: number;
  endColumn: number;
}

/** A dimension's item in its flow's YAML, comments included, from `firstLine`, and where each thing it declares is written. */
export interface DeliveryDimensionYaml {
  file: string | null;
  firstLine: number;
  lines: string[];
  spans: DimensionYamlSpan[];
  /** True when the item is longer than a block shows and its last lines are left out. */
  cut: boolean;
}

/** How much of an attribute the last build read: the keys holding a value read for it, and how many values those are. */
export interface DeliveryDimensionCoverage {
  name: string;
  keys: number;
  values: number;
}

/**
 * How a dimension is built: its flow and partition, whether the flow declares it, its number once built, its YAML (or why
 * it is not shown), the blueprint the declaration and the flow's templates make, and how much of each attribute was read.
 */
export interface DeliveryDimensionBlueprint {
  flow: string;
  dimension: string;
  partition: string | null;
  declared: boolean;
  dimensionId: number | null;
  yaml: DeliveryDimensionYaml | null;
  yamlMissing: string | null;
  blueprint: DimensionBlueprint;
  coverage: DeliveryDimensionCoverage[];
}

/** An attribute picked: its name, and the values a key has to hold one of. */
export interface DeliveryDimensionAttributePick {
  name: string;
  values: string[];
}

/** What is picked in one dimension: values by id or as the dimension holds them, and attribute values its keys hold. */
export interface DeliveryDimensionPick {
  dimensionId: number;
  valueIds?: number[];
  values?: string[];
  attributes?: DeliveryDimensionAttributePick[];
}

/** A search to compose from the values picked across dimensions. */
export interface DeliveryDimensionSearchRequest {
  picks: DeliveryDimensionPick[];
  /** The kind to search; left out, the one kind every dimension picked in reads. */
  kind?: string | null;
  /** A query narrowing the search further. */
  within?: string | null;
}

/** A value a filter or a search covers. */
export interface DeliveryDimensionFilterValue {
  valueId: number;
  value: string;
  records: number;
  recordsExact: boolean;
  keys: number;
}

/** One dimension's part of a composed search: the values picked it holds now, the keys they compare, and its filter. */
export interface DeliveryDimensionSearchPart {
  dimensionId: number;
  dimension: string;
  aggregateBy: string;
  values: DeliveryDimensionFilterValue[];
  keys: number;
  unfilterable: number;
  filter: string;
  query: string | null;
  /** The attribute values its keys were picked by. */
  attributes: DeliveryDimensionAttributePick[];
}

/** A composed search: the kind and query to send, the request body the search service takes, and what the picks left out. */
export interface DeliveryDimensionSearch {
  kind: string;
  query: string;
  /** The body of POST /api/search/v2/query, as JSON text. */
  request: string;
  parts: DeliveryDimensionSearchPart[];
  clauses: number;
  removed: string[];
  missing: string[];
  notes: string[];
}

export interface DeliveryDimensionValueQuery {
  search?: string;
  order?: "value" | "records";
  removed?: boolean;
  after?: string | null;
  limit?: number;
  /** Only the values holding a key that holds every attribute value named. */
  attributes?: DimensionAttributeCondition[];
}

export interface DeliveryDimensionKeyQuery {
  search?: string;
  value?: number;
  leftOut?: boolean;
  removed?: boolean;
  order?: "arrival" | "count";
  after?: string | null;
  limit?: number;
  /** Only the keys holding every attribute value named. */
  attributes?: DimensionAttributeCondition[];
}

/** One attribute value a key has to hold: several of one attribute are any of them, several attributes all of them. */
export interface DimensionAttributeCondition {
  name: string;
  value: string;
}

export interface DeliveryDimensionChangeQuery {
  build?: number;
  key?: number;
  value?: number;
  change?: DimensionChangeKind;
  before?: number | null;
  limit?: number;
}

/**
 * One row of a dimension's table: the row's number (what a table of facts joins on), the key's number, the key, its value,
 * its attributes in the order of the table's attribute columns (null where the key has none), the records of the row,
 * and the search finding the key's records.
 */
export interface DeliveryDimensionTableRow {
  id: number;
  keyId: number;
  key: string;
  value: string;
  attributes: (string | null)[];
  records: number;
  filter: string | null;
}

/**
 * A page of a dimension's table; `total` is how many rows the query matches, when the page says. `keyColumn` and
 * `valueColumn` are what the table names the columns holding each row's `key` and `value`, as it has them now.
 */
export interface DeliveryDimensionTable {
  table: string;
  keyColumn: string;
  valueColumn: string;
  attributes: string[];
  rows: DeliveryDimensionTableRow[];
  more: boolean;
  total: number | null;
}

export interface DeliveryDimensionTableQuery {
  search?: string;
  /** The column the rows are ordered by: one of the table by its name, or `value` and `key` for those two whatever the table names them. */
  order?: string;
  dir?: "asc" | "desc";
  offset?: number;
  limit?: number;
  /** Only the rows holding every attribute value named. */
  attributes?: DimensionAttributeCondition[];
}

// ---- Dimension views --------------------------------------------------------------------------------------------------

/** What a build's check of a view came to: read whole, or not readable. */
export type DimensionViewCheckStatus = "passed" | "failed";

/** One join of a view: the alias its columns are read by, the dimension joined, the column joined on, and the joined table. */
export interface DeliveryDimensionViewJoin {
  alias: string;
  to: string;
  on: string;
  table: string;
}

/** One column of a view: its type, the expression it is computed by, the type it is converted to, and what it holds. */
export interface DeliveryDimensionViewColumn {
  name: string;
  type: string;
  expression?: string | null;
  dataType?: string | null;
  description?: string | null;
}

/** What a check found of one join: the rows that found their row, those whose value found none, and some of those values. */
export interface DeliveryDimensionViewJoinCheck {
  alias: string;
  to: string;
  on: string;
  matched: number;
  unmatched: number;
  examples: string[];
}

/** A row whose value a column's conversion could not read: the row's number (the view's `id`) and the value. */
export interface DeliveryDimensionViewExample {
  id: number;
  value: string;
}

/** What a check found of one column: the rows holding a value, and for a converted column the values it could not read. */
export interface DeliveryDimensionViewColumnCheck {
  name: string;
  values: number;
  /** Null for a column the view does not convert. */
  unconverted?: number | null;
  examples: DeliveryDimensionViewExample[];
}

/**
 * What a build's check of a view found in one partition: `passed` with its rows, joins and columns, or `failed` with SQL
 * Server's message; `notes` says what the run's result says of it, a sentence each.
 */
export interface DeliveryDimensionViewCheck {
  checkId: number;
  partition: string;
  runId?: string | null;
  status: DimensionViewCheckStatus;
  rows: number;
  joins: DeliveryDimensionViewJoinCheck[];
  columns: DeliveryDimensionViewColumnCheck[];
  error?: string | null;
  checkedUtc: string;
  durationMs: number;
  notes: string[];
}

/**
 * A view of a dimension flow: as its flow declares it now (`declared`), as a build last wrote it (`recorded`, with
 * `written` false when a build dropped it to write it again, `note` saying why), and whether the two differ (`changed`:
 * the next build writes it again). A view a build wrote that no flow declares any more is listed with `declared` false.
 */
export interface DeliveryDimensionView {
  name: string;
  /** The view's name in the module's schema, without the schema (`v_dim_Curve`). */
  viewName: string;
  flowName: string;
  /** Null when the catalog holds no pipeline of the flow's name. */
  pipelineId?: string | null;
  declared: boolean;
  recorded: boolean;
  written: boolean;
  changed: boolean;
  description?: string | null;
  /** The dimension whose rows the view's rows are. */
  from: string;
  /** The condition a row of `from` is kept by; null when the view keeps every row. */
  where?: string | null;
  joins: DeliveryDimensionViewJoin[];
  /** The view's columns in order, its `partition` and `id` first. */
  columns: DeliveryDimensionViewColumn[];
  /** The dimension tables the view reads (`dim_...`), its `from` table first. */
  tables: string[];
  note?: string | null;
  writtenRunId?: string | null;
  writtenBy?: string | null;
  writtenUtc?: string | null;
  /** The newest check, in whichever partition a build ran. */
  lastCheck?: DeliveryDimensionViewCheck | null;
}

/** What the flow's templates say of a join: it agrees, it joins a dimension of another entity type (and finds nothing), or no template says. */
export type DimensionViewJoinVerdictKind = "agrees" | "differs" | "unchecked";

/**
 * What the flow's templates say of one join a view declares: the entity types the joined column names (its template's
 * `x-osdu-relationship`, or an id's pattern), the entity type the joined dimension's kind names, and the verdict.
 */
export interface DeliveryDimensionViewJoinVerdict {
  alias: string;
  on: string;
  to: string;
  names: string[];
  /** Null for a dimension whose kind names any type, or one not keyed by id. */
  reads?: string | null;
  verdict: DimensionViewJoinVerdictKind;
  note: string;
}

/** A view with its newest checks (newest first), its SQL as last written and as declared now, its item of the flow's YAML, and what the templates say of its joins. */
export interface DeliveryDimensionViewDetail {
  view: DeliveryDimensionView;
  sql?: string | null;
  declaredSql?: string | null;
  checks: DeliveryDimensionViewCheck[];
  yaml?: DeliveryDimensionYaml | null;
  /** One per join the flow declares now; empty for a view no flow declares. */
  joinChecks?: DeliveryDimensionViewJoinVerdict[];
}

/** A join a view could make: the column joined on, the dimension joined, the alias, and why it is offered. */
export interface DeliveryDimensionViewJoinSuggestion {
  on: string;
  to: string;
  as: string;
  note: string;
}

/** The joins a view whose rows are `from`'s could make, and the same as a ready-to-paste `join:` block of a view's YAML. */
export interface DeliveryDimensionViewSuggestion {
  from: string;
  joins: DeliveryDimensionViewJoinSuggestion[];
  yaml: string;
}

/** What removing a view took: the view from the database when it was there, and its record with its checks. */
export interface DeliveryDimensionViewRemoved {
  name: string;
  viewName: string;
  flowName: string;
  dropped: boolean;
  checks: number;
  summary: string;
}

export type DimensionExportSet = "values" | "keys" | "table";
export type DimensionExportFormat = "csv" | "jsonl";

/** The attribute conditions of a page as the path's query carries them: `attr=Name:value`, once each. */
function attributesQuery(conditions: DimensionAttributeCondition[] | undefined): string {
  if (conditions === undefined || conditions.length === 0) {
    return "";
  }

  const params = new URLSearchParams();
  for (const condition of conditions) {
    params.append("attr", `${condition.name}:${condition.value}`);
  }

  return `?${params.toString()}`;
}

/** A query without the parameters it leaves unset, so the request carries only what was asked. */
function defined(query: object): QueryParams {
  return Object.fromEntries(Object.entries(query).filter(([, value]) => value !== undefined && value !== null && value !== "")) as QueryParams;
}


export const deliveryApi = {
  /**
   * Every registered partition, and every partition something is still kept under, with what its cache serves and what
   * waits in it.
   */
  partitions: () => get<DeliveryPartition[]>("/api/v1/delivery/partitions"),
  /** Sets what a registered partition is for; empty clears it. Admin only. */
  describePartition: (name: string, description: string | null) =>
    put<DeliveryPartition>(`/api/v1/delivery/partitions/${encodeURIComponent(name)}`, { description }),
  /** Makes a registered partition the one a run that names none runs in. Admin only. */
  makeDefaultPartition: (name: string) =>
    post<DeliveryPartition>(`/api/v1/delivery/partitions/${encodeURIComponent(name)}/default`),
  /** Takes a partition out of the registry; nothing kept under it is deleted. Admin only. */
  removePartition: (name: string) => del<void>(`/api/v1/delivery/partitions/${encodeURIComponent(name)}`),
  /**
   * The counts of one interface, or of the whole source when the scope names none; of one partition, or of every partition
   * added up when the scope names none.
   */
  stats: (pipelineId: string, scope?: DeliveryFlowScope) =>
    get<DeliveryFlowStats>(`/api/v1/delivery/flows/${pipelineId}/stats`, scopeQuery(scope)),
  /**
   * Every interface of a source, in the order a run takes them, each with its route and counts: in `partition`, or for a
   * flow that names its partitions and no partition given, in every partition, each row naming its own.
   */
  interfaces: (pipelineId: string, partition?: string | null) =>
    get<DeliveryInterface[]>(`/api/v1/delivery/flows/${pipelineId}/interfaces`, partition ? { partition } : {}),
  records: (pipelineId: string, query: DeliveryRecordListQuery = {}) =>
    get<PagedResult<DeliveryRecord>>(`/api/v1/delivery/flows/${pipelineId}/records`, query as QueryParams),
  /**
   * A record by what an operator holds, across every flow: a delivery key lands on the record of every flow reading
   * that row; anything else is a prefix over the OSDU id, the source key, the label and the ingestion file name. With
   * no search term, the records the ledger last took in or sent, newest first. The ledger's indexed reads, so they
   * answer at production volume and count no further than the candidate bound.
   */
  lookupRecords: (query: DeliveryRecordLookupQuery) =>
    get<PagedResult<DeliveryRecordHit>>("/api/v1/delivery/records", query as unknown as QueryParams),
  /** The flows the lookup can be narrowed to, one per interface of a source, ordered by flow: of one partition's ledgers, when named. */
  recordFlows: (partition?: string | null) =>
    get<DeliveryRecordFlow[]>("/api/v1/delivery/records/flows", partition ? { partition } : {}),
  /** A flow's submissions in one ledger, newest first. */
  submissions: (pipelineId: string, max?: number, scope?: DeliveryFlowScope) =>
    get<DeliverySubmission[]>(`/api/v1/delivery/flows/${pipelineId}/submissions`, {
      ...(max ? { max } : {}),
      ...scopeQuery(scope),
    }),
  retrievals: (pipelineId: string, max?: number) =>
    get<DeliveryRetrieval[]>(`/api/v1/delivery/flows/${pipelineId}/retrievals`, max ? { max } : {}),
  /** Every assertion flow's tests in the workbench's partition, each with its latest result and recent history. */
  assertionBoard: () => get<DeliveryAssertionBoard>("/api/v1/delivery/assertions"),
  /** One assertion flow's tests in the workbench's partition. */
  assertionFlowBoard: (pipelineId: string) => get<DeliveryAssertionBoard>(`/api/v1/delivery/flows/${pipelineId}/assertions`),
  /** An assertion flow's runs in the workbench's partition, newest first. */
  assertionRuns: (pipelineId: string, max?: number) =>
    get<DeliveryAssertionRun[]>(`/api/v1/delivery/flows/${pipelineId}/assertion-runs`, max ? { max } : {}),
  /** An assertion flow's recent runs against its tests. */
  assertionMatrix: (pipelineId: string, runs?: number) =>
    get<DeliveryAssertionMatrix>(`/api/v1/delivery/flows/${pipelineId}/assertion-matrix`, runs ? { runs } : {}),
  /** An assertion run with every test's result. */
  assertionRun: (assertionRunId: number) => get<DeliveryAssertionRunDetail>(`/api/v1/delivery/assertion-runs/${assertionRunId}`),
  /** An assertion run's report as the text of the format asked for. */
  assertionReport: (assertionRunId: number, format: AssertionReportFormat) =>
    getText(`/api/v1/delivery/assertion-runs/${assertionRunId}/report?format=${format}`),
  /** Every dimension flow's dimensions in the workbench's partition. */
  dimensionBoard: () => get<DeliveryDimensionBoard>("/api/v1/delivery/dimensions"),
  /** One dimension flow's dimensions in the workbench's partition. */
  dimensionFlowBoard: (pipelineId: string) => get<DeliveryDimensionBoard>(`/api/v1/delivery/flows/${pipelineId}/dimensions`),
  /**
   * How a dimension of a flow is built, in `partition` (the workbench's when none is named): its YAML line by line, the
   * records it reads step by step with the template describing each, and the table's columns with the reads each is
   * written from. Read from the catalog, the template files on the flow's path and the ledger; nothing reaches OSDU.
   */
  dimensionBlueprint: (pipelineId: string, name: string, partition?: string | null) =>
    get<DeliveryDimensionBlueprint>(
      `/api/v1/delivery/flows/${pipelineId}/dimensions/${encodeURIComponent(name)}/blueprint`, partition ? { partition } : {}),
  /** Removes a dimension its flow no longer declares, for good: an admin's alone. */
  removeDimension: (dimensionId: number) => del<DeliveryDimensionRemoved>(`/api/v1/delivery/dimensions/${dimensionId}`),
  /** A dimension with its declaration, the build that wrote what it holds and its newest build. */
  dimension: (dimensionId: number) => get<DeliveryDimensionDetail>(`/api/v1/delivery/dimensions/${dimensionId}`),
  /** A page of a dimension's table, as the database holds it: narrowed, ordered, from a row on. */
  dimensionTable: (dimensionId: number, { attributes, search, ...query }: DeliveryDimensionTableQuery = {}) =>
    get<DeliveryDimensionTable>(
      `/api/v1/delivery/dimensions/${dimensionId}/table${attributesQuery(attributes)}`, defined({ ...query, search: search === "" ? undefined : search })),
  /** A page of a dimension's values, in value order or with the most records first. */
  dimensionValues: (dimensionId: number, { attributes, ...query }: DeliveryDimensionValueQuery = {}) =>
    get<DeliveryDimensionValuePage>(`/api/v1/delivery/dimensions/${dimensionId}/values${attributesQuery(attributes)}`, defined(query)),
  /** A value with its keys, filter and history. */
  dimensionValue: (dimensionId: number, valueId: number) =>
    get<DeliveryDimensionValueDetail>(`/api/v1/delivery/dimensions/${dimensionId}/values/${valueId}`),
  /** A page of a dimension's keys, each with its label and filter. */
  dimensionKeys: (dimensionId: number, { attributes, ...query }: DeliveryDimensionKeyQuery = {}) =>
    get<DeliveryDimensionKeyPage>(`/api/v1/delivery/dimensions/${dimensionId}/keys${attributesQuery(attributes)}`, defined(query)),
  /** The values an attribute of a dimension holds among its keys, the most records first; those containing `search`. */
  dimensionAttributeValues: (dimensionId: number, name: string, search?: string, limit?: number) =>
    get<DeliveryDimensionAttributeValue[]>(
      `/api/v1/delivery/dimensions/${dimensionId}/attributes/${encodeURIComponent(name)}`, defined({ search, limit })),
  /** A dimension's builds, newest first. */
  dimensionBuilds: (dimensionId: number, max?: number) =>
    get<DeliveryDimensionBuild[]>(`/api/v1/delivery/dimensions/${dimensionId}/builds`, max ? { max } : {}),
  /** A page of a dimension's change log, newest first. */
  dimensionChanges: (dimensionId: number, query: DeliveryDimensionChangeQuery = {}) =>
    get<DeliveryDimensionChangePage>(`/api/v1/delivery/dimensions/${dimensionId}/changes`, defined(query)),
  /** The search filter of the values named by id or as the dimension holds them. */
  dimensionFilter: (dimensionId: number, request: { valueIds?: number[]; values?: string[] }) =>
    post<DeliveryDimensionFilter>(`/api/v1/delivery/dimensions/${dimensionId}/filter`, request),
  /**
   * The OSDU search finding the records that hold a value picked in each dimension: OR within a dimension, AND across them,
   * in the kind every dimension reads. Reads the ledger only; nothing is sent to OSDU.
   */
  dimensionSearch: (request: DeliveryDimensionSearchRequest) =>
    post<DeliveryDimensionSearch>("/api/v1/delivery/dimensions/search", request),
  /** The whole of a dimension's values or keys, as the text of the format asked for. */
  dimensionExport: (dimensionId: number, set: DimensionExportSet, format: DimensionExportFormat) =>
    getText(`/api/v1/delivery/dimensions/${dimensionId}/export?set=${set}&format=${format}`),
  /** The builds a platform run made, one per dimension it built. */
  runDimensionBuilds: (runId: string) => get<DeliveryDimensionRunBuild[]>(`/api/v1/delivery/runs/${runId}/dimension-builds`),
  /** Every view the active dimension flows declare, then the views builds wrote that no flow declares any more. */
  dimensionViews: () => get<DeliveryDimensionView[]>("/api/v1/delivery/dimensions/views"),
  /** One dimension flow's views: those it declares, and those a build of it wrote that it no longer declares. */
  dimensionFlowViews: (pipelineId: string) => get<DeliveryDimensionView[]>(`/api/v1/delivery/flows/${pipelineId}/dimensions/views`),
  /**
   * The joins a view whose rows are the dimension `from`'s could make, from the flow's dimensions and its templates,
   * with the `join:` block to paste into the view's YAML. Reads the catalog and the templates; nothing is written.
   */
  suggestDimensionViewJoins: (pipelineId: string, from: string) =>
    get<DeliveryDimensionViewSuggestion>(`/api/v1/delivery/flows/${pipelineId}/dimensions/views/suggest`, { from }),
  /** A view with its newest checks, its SQL as written and as declared, its YAML, and what the templates say of its joins. */
  dimensionView: (name: string) => get<DeliveryDimensionViewDetail>(`/api/v1/delivery/dimensions/views/${encodeURIComponent(name)}`),
  /** Removes a view no flow declares any more, for good: an admin's alone. */
  removeDimensionView: (name: string) => del<DeliveryDimensionViewRemoved>(`/api/v1/delivery/dimensions/views/${encodeURIComponent(name)}`),
  record: (record: DeliveryRecordRef) => get<DeliveryRecordDetail>(recordApiPath(record)),
  attempts: (record: DeliveryRecordRef, max?: number) =>
    get<DeliveryAttempt[]>(`${recordApiPath(record)}/attempts`, max ? { max } : {}),
  recordActivities: (record: DeliveryRecordRef, max?: number) =>
    get<DeliveryActivity[]>(`${recordApiPath(record)}/activities`, max ? { max } : {}),
  /** The record's row through its ingestion table: its arrival and every change, each with the runs that made it. */
  recordChain: (record: DeliveryRecordRef) => get<DeliveryRecordChain>(`${recordApiPath(record)}/chain`),
  submission: (submissionId: string) => get<DeliverySubmissionDetail>(`/api/v1/delivery/submissions/${submissionId}`),
  submissionAttempts: (submissionId: string, max?: number) =>
    get<DeliveryAttempt[]>(`/api/v1/delivery/submissions/${submissionId}/attempts`, max ? { max } : {}),
  submissionBatches: (submissionId: string, query: PageQuery & { status?: DeliveryWorkBatch["status"] } = {}) =>
    get<PagedResult<DeliveryWorkBatch>>(`/api/v1/delivery/submissions/${submissionId}/batches`, query as QueryParams),
  activities: (query: DeliveryActivityListQuery = {}) =>
    get<PagedResult<DeliveryActivity>>("/api/v1/delivery/activities", query as QueryParams),
  activity: (activityId: number) => get<DeliveryActivity>(`/api/v1/delivery/activities/${activityId}`),
  /** The flows with activity on the trail of `partition`, else the workbench's, else every partition. */
  activityFlows: (partition?: string | null) =>
    get<DeliveryActivityFlow[]>("/api/v1/delivery/activities/flows", partition ? { partition } : {}),
  mappings: (repoId?: string, status?: string) =>
    get<DeliveryMapping[]>("/api/v1/delivery/mappings", { repoId, status }),
  mapping: (mappingId: string) => get<DeliveryMappingDetail>(`/api/v1/delivery/mappings/${mappingId}`),
  /** Every file of every synced repository's template folders, as each repository's last sync found it. */
  templates: () => get<DeliveryTemplate[]>("/api/v1/delivery/templates"),
  /**
   * The template a repository's file holds, read from the file and laid out variable by variable; with `scope`, each
   * variable names the types of that partition's cache it can be read from. A 404 for a file the sync did not record, a 422
   * for one that holds no template or no longer holds what the sync recorded.
   */
  templateDetail: (repoId: string, path: string, scope?: string) =>
    get<DeliveryTemplateDetail>("/api/v1/delivery/templates/detail", { repoId, path, scope }),
  /** The template a repository's file holds, read from the file, as the file it is, read as its JSON text. */
  templateSchema: (repoId: string, path: string) =>
    getText(`/api/v1/delivery/templates/schema?${new URLSearchParams({ repoId, path }).toString()}`),
  /**
   * Lays out a schema as a template, keeping nothing; a 400 says why the JSON is not a record schema. A schema that refers
   * to the shared schemas of the OSDU data definitions has them read from `release` (the newest when omitted).
   */
  previewTemplate: (kind: string, schema: Record<string, unknown>, scope?: string | null, release?: string | null) =>
    post<DeliveryTemplateDetail>("/api/v1/delivery/templates/preview", { kind, schema, scope: scope ?? null, release: release ?? null }),
  /** The releases of the OSDU data definitions (the Open Group's public schema repository), newest first; a 502 when it cannot be read. */
  osduReleases: () => get<DeliveryOsduReleases>("/api/v1/delivery/templates/osdu/releases"),
  /** Reads the release list again from the repository and downloads `release` (the newest when omitted) when it is not local; under the operate policy, which every signed-in user holds. */
  osduSync: (release?: string | null) =>
    post<DeliveryOsduSync>("/api/v1/delivery/templates/osdu/sync", { release: release ?? null }),
  /** Every record kind a release of the OSDU data definitions publishes; a 404 for a release it does not have. */
  osduSchemas: (release: string) =>
    get<DeliveryOsduSchemaIndex>("/api/v1/delivery/templates/osdu/schemas", { release }),
  /** One kind's schema from a release, bundled with every schema it refers to. Nothing is kept; a 404 for a kind the release does not publish. */
  osduSchema: (release: string, kind: string) =>
    get<DeliveryOsduSchemaFile>("/api/v1/delivery/templates/osdu/schema", { release, kind }),
  /**
   * One kind's schema from a release, as the file a repository's template folder keeps (bundled, naming its kind), read as
   * its JSON text: what Download saves. Nothing is kept on the control plane.
   */
  osduSchemaFile: (release: string, kind: string) =>
    getText(`/api/v1/delivery/templates/osdu/file?${new URLSearchParams({ release, kind }).toString()}`),
  /** Two versions of one kind, each from a release, compared as published files and variable by variable; a 400 for two different kinds. */
  osduCompare: (from: { release: string; kind: string }, to: { release: string; kind: string }) =>
    get<DeliveryOsduComparison>("/api/v1/delivery/templates/osdu/compare", {
      fromRelease: from.release,
      fromKind: from.kind,
      toRelease: to.release,
      toKind: to.kind,
    }),
  /** The repositories the mapping builder offers, with their git source and delivery flows. */
  builderRepos: () => get<DeliveryBuilderRepo[]>("/api/v1/delivery/mapping-builder/repos"),
  /** The partition caches the mapping builder offers, with the flows filling each, their current version and their types. */
  builderCaches: () => get<DeliveryBuilderCache[]>("/api/v1/delivery/mapping-builder/caches"),
  /** A new draft for the template a repository's file holds, prefilled from the named partition's cache; a 404 when the sync recorded no such file. */
  draftMapping: (request: DeliveryMappingDraftRequest) =>
    post<MappingDraft>("/api/v1/delivery/mapping-builder/draft", request),
  /**
   * Writes a draft as YAML and checks it against its template and the named partition cache's current version, rendering with
   * `parameters`: at `path` in repository `repoId` when named, whose template folders settle its templates as they will once
   * it is there.
   */
  composeMapping: (scope: string | null, draft: MappingDraft, parameters: Record<string, string> | null, repoId?: string | null, path?: string | null) =>
    post<DeliveryMappingComposeResult>("/api/v1/delivery/mapping-builder/compose", { scope, draft, parameters, repoId: repoId ?? null, path: path ?? null }),
  /** What a mapping document fills of the template version it pins, variable by variable; nothing is rendered and no cache is read. */
  /**
   * `repoId` names the synced repository the document is a mapping of, whose template folders settle a template it names
   * by kind alone, as its runs do; without it the document has to name its template's version.
   */
  mappingCoverage: (yaml: string, path: string | null, repoId?: string | null) =>
    post<DeliveryMappingCoverage>("/api/v1/delivery/mapping-builder/coverage", { yaml, path, repoId: repoId ?? null }),
  /** Reads a mapping document back into a draft for the builder; `repoId` as for the coverage. */
  parseMapping: (yaml: string, path: string | null, repoId?: string | null) =>
    post<DeliveryMappingParseResult>("/api/v1/delivery/mapping-builder/parse", { yaml, path, repoId: repoId ?? null }),
  /** The shape of the records a mapping document renders, drawn with `parameters`; nothing is read or stored. */
  mappingShape: (yaml: string, path: string | null, parameters: Record<string, string>, repoId?: string | null) =>
    post<DeliveryMappingShapeResult>("/api/v1/delivery/mapping-builder/shape", { yaml, path, parameters, repoId: repoId ?? null }),
  /** What a template a document of a synced repository names settles to, from the template folders on its path. */
  resolveTemplate: (repoId: string, path: string, kind: string, version?: string | null) =>
    get<DeliveryTemplateResolution>("/api/v1/delivery/templates/resolve", { repoId, path, kind, version: version ?? undefined }),
  /** Every partition's cache: the cache flows filling it, what refreshes them, its types and its current version. */
  caches: (repoId?: string) =>
    get<DeliveryCache[]>("/api/v1/delivery/caches", { repoId }),
  /** The versions of one partition's cache, newest first, each with the flow and run that wrote it: what the version picker offers. */
  cacheVersions: (scope: string) =>
    get<DeliveryCacheVersion[]>("/api/v1/delivery/cache/versions", { scope }),
  /** The records of one partition's cache at one version (the current one when none is named). */
  cachedItems: (query: PageQuery & { scope: string; type?: string; search?: string; version?: string }) =>
    get<PagedResult<DeliveryCachedItem>>("/api/v1/delivery/cache/items", query as unknown as QueryParams),
  /** What changed in one partition's cache between two versions (`to` defaults to the current one), a page of records at a time. */
  cacheDiff: (query: DeliveryCacheDiffQuery) =>
    get<DeliveryCacheDiff>("/api/v1/delivery/cache/diff", query),
  /** Every version of one partition's cache, newest first, with what it changed against the one before it; `type` narrows the counts to one type. */
  cacheHistory: (scope: string, type?: string) =>
    get<DeliveryCacheHistoryEntry[]>("/api/v1/delivery/cache/history", { scope, type }),
  /** What purging the partition cache's history, keeping `keepDays`, would prune; changes nothing. */
  cachePrunePreview: (request: DeliveryCachePruneRequest) =>
    post<DeliveryCachePruneResult>("/api/v1/delivery/cache/prune/preview", request),
  /** Purges the partition cache's history, keeping `keepDays`, as the signed-in admin. */
  cachePrune: (request: DeliveryCachePruneRequest) =>
    post<DeliveryCachePruneResult>("/api/v1/delivery/cache/prune", request),
  /** The cache changes delivered records were built from, by status (pending, approved, rolling, rejected, applied) and partition. */
  updateTags: (query: PageQuery & { status?: string; scope?: string }) =>
    get<PagedResult<DeliveryUpdateTag>>("/api/v1/delivery/cache/tags", query as QueryParams),
  /** Approves or rejects tags; approving lets the next run carry the new document to OSDU. */
  decideTags: (tagIds: number[], approve: boolean) =>
    post<{ decided: number; approved: boolean }>("/api/v1/delivery/cache/tags/decide", { tagIds, approve }),
  /** What one record read out of the cache when it was rendered. */
  recordCacheUses: (record: DeliveryRecordRef) => get<DeliveryCacheUse[]>(`${recordApiPath(record)}/cache`),
  /** What delivered records of a partition were built without, most records first, a page at a time. */
  cacheGaps: (query: PageQuery & { scope: string; type?: string; empty?: boolean }) =>
    get<PagedResult<DeliveryCacheGap>>("/api/v1/delivery/cache/gaps", query as unknown as QueryParams),
  /**
   * Releases the flow's blocked records (held, failed, reverted, or removed by a reversal), and its held or failed ones
   * nothing moves: all of them, or the given keys. A record that kept its rendered document goes back to pending; the others
   * are planned again from their rows by the next run. With `run`, a deliver run of the flow is queued at once.
   */
  releaseFlow: (pipelineId: string, keys?: string[], scope?: DeliveryFlowScope, run = false) =>
    post<DeliveryReleaseResult>(flowPath(pipelineId, "/release", scope), { keys: keys ?? null, run }),
  /** Sends many records again whatever their hashes say: the part `scope` names, of the records the request names. */
  redeliverFlow: (pipelineId: string, request: DeliveryFlowRedeliverRequest, scope?: DeliveryFlowScope) =>
    post<DeliveryRedeliverResult>(flowPath(pipelineId, "/redeliver", scope), request),
  /** Brings many records up to date: renders them again and sends only what renders differently. */
  rerenderFlow: (pipelineId: string, request: DeliveryManyRecords, scope?: DeliveryFlowScope) =>
    post<DeliveryRerenderResult>(flowPath(pipelineId, "/rerender", scope), request),
  /** Queues the plan run that says what bringing the records the request names up to date would send, sending nothing. */
  previewRerender: (pipelineId: string, request: DeliveryManyRecords, scope?: DeliveryFlowScope) =>
    post<DeliveryRerenderPreview>(flowPath(pipelineId, "/rerender/preview", scope), request),
  /** Probes the flow's target: is OSDU reachable with the flow's credentials, in the scope's partition? Answered at once. */
  probe: (pipelineId: string, scope?: DeliveryFlowScope) =>
    post<DeliveryProbeResult>(flowPath(pipelineId, "/probe", scope)),
  /** Releases one record; with `run`, also queues a deliver run scoped to it, which tries it again at once. */
  release: (record: DeliveryRecordRef, run = false) =>
    post<DeliveryReleaseResult>(`${recordApiPath(record)}/release`, run ? { run: true } : undefined),
  /** The issues keeping one ledger's records blocked, the most records first. */
  issues: (pipelineId: string, scope?: DeliveryFlowScope, max?: number) =>
    get<DeliveryIssueList>(`/api/v1/delivery/flows/${pipelineId}/issues`, { ...scopeQuery(scope), ...(max === undefined ? {} : { max }) }),
  /** One issue of a ledger, with the files its records came from. */
  issue: (pipelineId: string, issue: string, scope?: DeliveryFlowScope) =>
    get<DeliveryIssueDetail>(`/api/v1/delivery/flows/${pipelineId}/issues/${encodeURIComponent(issue)}`, scopeQuery(scope)),
  /** Releases every record an issue keeps blocked; with `run`, also queues a deliver run of the flow. */
  releaseIssue: (pipelineId: string, issue: string, run: boolean, scope?: DeliveryFlowScope) =>
    post<DeliveryIssueReleaseResult>(flowPath(pipelineId, `/issues/${encodeURIComponent(issue)}/release`, scope), { run }),
  /**
   * Adopts the record OSDU already holds at the id a held record's key makes: the record claims the id at the version
   * OSDU held, and the next run writes it there. With `run`, a deliver run scoped to the record is queued at once.
   */
  adopt: (record: DeliveryRecordRef, run = false) =>
    post<DeliveryRecordAdoptResult>(`${recordApiPath(record)}/adopt`, run ? { run: true } : undefined),
  /** Adopts what OSDU holds for every record an issue keeps held, the partition typed back as `confirm`. */
  adoptIssue: (pipelineId: string, issue: string, confirm: string, run: boolean, scope?: DeliveryFlowScope) =>
    post<DeliveryIssueAdoptResult>(flowPath(pipelineId, `/issues/${encodeURIComponent(issue)}/adopt`, scope), { confirm, run }),
  /** Queues a verify run scoped to this record. */
  verify: (record: DeliveryRecordRef) => post<DeliveryRunAccepted>(`${recordApiPath(record)}/verify`),
  /**
   * Queues a sync run for this record: its row is read from the ingestion table and the ledger consolidated with it (its
   * arrival recorded, a change it never saw asked to be planned by the next run, a row that is gone put on its history).
   * Nothing is sent to OSDU.
   */
  syncRecord: (record: DeliveryRecordRef) => post<DeliveryRunAccepted>(`${recordApiPath(record)}/sync`),
  /** Queues the same sync for records of the flow's interface: those the request names, or every one without a request. */
  syncFlow: (pipelineId: string, scope?: DeliveryFlowScope, request?: DeliverySyncRequest) =>
    post<DeliveryRunAccepted>(flowPath(pipelineId, "/sync", scope), request ?? {}),
  /** Reads the record back as its flow wrote it to OSDU, at its latest version or at `version`. Answered at once. */
  read: (record: DeliveryRecordRef, version?: number) =>
    post<DeliveryOsduRead>(`${recordApiPath(record)}/read`, version === undefined ? undefined : { version }),
  /**
   * Reads the record's rows as the ingestion tables hold them now, with the flow's own connection: the record row with its
   * system columns, its child datasets, and the origin file and row the ingestion tables record. Answered at once.
   */
  readSource: (record: DeliveryRecordRef) => post<DeliverySourceRows>(`${recordApiPath(record)}/source`),
  /**
   * Previews one record of an interface: the scope's first record, or the one `key` names (a source key, a delivery key, an
   * OSDU id the ledger holds, or a JSON array of the key's parts), rendered as a delivery would render it and sent nowhere.
   * `values` fill the flow's parameters; the declared defaults fill the rest. Answered at once.
   */
  preview: (pipelineId: string, request: { key?: string | null; values?: Record<string, string> }, scope?: DeliveryFlowScope) =>
    post<DeliveryRecordPreview>(flowPath(pipelineId, "/preview", scope), request),
  /** Previews this record, rendered from its current source row as a delivery would render it now. Answered at once. */
  previewRecord: (record: DeliveryRecordRef) => post<DeliveryRecordPreview>(`${recordApiPath(record)}/preview`),
  /**
   * Reads the values each parameter of an interface's scope can take: the distinct values of the column the scope binds it
   * to in the flow's record table, with how many rows hold each. What a page offers for a scope's value rather than having
   * it typed. Answered at once.
   */
  scopeValues: (pipelineId: string, scope?: DeliveryFlowScope) =>
    post<DeliveryScopeValues>(flowPath(pipelineId, "/scope-values", scope)),
  /** The interfaces of the delivery flows that render with a mapping, in every partition: what its values are checked against. */
  mappingFlows: (mappingId: string) => get<DeliveryMappingFlow[]>(`/api/v1/delivery/mappings/${mappingId}/flows`),
  /**
   * Queues a value check of one interface's rows on a node: the rows that will not give the variables named (every
   * variable when none is named) the values the template expects, with the reasons and example records. Nothing is sent.
   */
  checkValues: (pipelineId: string, request: DeliveryValueCheckRequest, scope?: DeliveryFlowScope) =>
    post<ComputeTaskAccepted>(flowPath(pipelineId, "/check-values", scope), request),
  /** Reads any OSDU record by id, through the flow's route and credentials. Answered at once. */
  readOsdu: (pipelineId: string, targetId: string, scope?: DeliveryFlowScope, version?: number) =>
    post<DeliveryOsduRead>(flowPath(pipelineId, "/osdu/read", scope), version === undefined ? { targetId } : { targetId, version }),
  /** Where the flow's records live in the scope's partition, and which call each removal scope makes against them. */
  target: (pipelineId: string, scope?: DeliveryFlowScope) =>
    get<DeliveryTarget>(`/api/v1/delivery/flows/${pipelineId}/target`, scopeQuery(scope)),
  /** What a removal would act on, without removing anything: the confirmation's contents. */
  previewRemoval: (pipelineId: string, request: DeliveryRemovalRequest, scope?: DeliveryFlowScope) =>
    post<DeliveryRemovalPreview>(flowPath(pipelineId, "/records/remove/preview", scope), request),
  /** Queues the removal of the selected records, or of every record the filter matches, on a node. */
  removeRecords: (pipelineId: string, request: DeliveryRemovalRequest, scope?: DeliveryFlowScope) =>
    post<DeliveryRemovalAccepted>(flowPath(pipelineId, "/records/remove", scope), request),
  /**
   * Queues the run that deletes the flow's ledger, every interface's, in the partition the request names (none for a flow
   * whose partition is its header): every record is removed from OSDU reversibly first, then everything the ledgers keep goes.
   */
  deleteLedger: (pipelineId: string, request: DeliveryLedgerDeleteRequest, partition: string | null) =>
    post<DeliveryLedgerDeleteAccepted>(flowPath(pipelineId, "/ledger/delete", { interfaceName: null, partition }), request),
  /** Deletes the selected records already removed from OSDU from the ledger, here and now; OSDU is asked nothing. */
  purgeRecords: (pipelineId: string, request: DeliveryLedgerPurgeRequest, scope?: DeliveryFlowScope) =>
    post<DeliveryLedgerPurgeResult>(flowPath(pipelineId, "/records/purge", scope), request),
  /** What reversing a run or a submission would reach, deciding a sample of its records as the run would; nothing is written. */
  previewReversal: (pipelineId: string, request: DeliveryReversalRequest, scope?: DeliveryFlowScope) =>
    post<DeliveryReversalPreview>(flowPath(pipelineId, "/reverse/preview", scope), request),
  /** Queues the reverse run of a run or a submission, as the caller; asking again resumes a reversal that stopped. */
  reverse: (pipelineId: string, request: DeliveryReversalRequest, scope?: DeliveryFlowScope) =>
    post<DeliveryReversalAccepted>(flowPath(pipelineId, "/reverse", scope), request),
  /** Whether the source has anything to reverse now: what every place that offers a reversal shows the action by. */
  reversible: (pipelineId: string, scope: DeliveryFlowScope | undefined, source: ReversalSource) =>
    get<DeliveryReversible>(flowPath(pipelineId, "/reversible", scope), {
      runId: source.kind === "run" ? source.id : undefined,
      submissionId: source.kind === "submission" ? source.id : undefined,
    }),
  /** The interface's reversals, newest first; with a source, the reversal of that source, counted, when one was asked for. */
  reversals: (pipelineId: string, scope?: DeliveryFlowScope, source?: ReversalSource) =>
    get<DeliveryReversal[]>(flowPath(pipelineId, "/reversals", scope), {
      runId: source?.kind === "run" ? source.id : undefined,
      submissionId: source?.kind === "submission" ? source.id : undefined,
    }),
  reversal: (reversalId: number) => get<DeliveryReversalDetail>(`/api/v1/delivery/reversals/${reversalId}`),
  /** A page of a reversal's records, every one or those of one outcome (`pending` for those not settled yet). */
  reversalRecords: (reversalId: number, query: { outcome?: string; after?: string; limit?: number } = {}) =>
    get<DeliveryReversalItemPage>(`/api/v1/delivery/reversals/${reversalId}/records`, query),
  prune: (olderThanDays: number) => post<DeliveryPruneResult>("/api/v1/delivery/ledger/prune", { olderThanDays }),
};

