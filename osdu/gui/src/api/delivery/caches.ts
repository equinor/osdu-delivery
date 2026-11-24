// Partition caches as the delivery API answers them: each cache with its flows and types, its versions and what each
// changed, its records, pruning and retention, the cache changes awaiting a decision, and what records read from it or
// went without.

import type { PagedResult } from "@/api/types";

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
  /** For an OSDU type: this flow reads each record its search finds whole from storage (types[].fetchRecords). */
  fetchRecords: boolean;
  /**
   * For a table type: the most rows its capture takes (types[].maxRows, or the default when the flow says nothing); a table
   * holding more refuses the capture. Null for every other origin.
   */
  maxRows: number | null;
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
  /**
   * For an OSDU type: every capture reads its records whole from storage, so paths the search index does not hold are kept;
   * set when any declaring flow asks for that.
   */
  fetchRecords: boolean;
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
  /**
   * How many versions keep their records at most, the current one counted (3 until an admin sets another number); every
   * refresh prunes the records of the older ones, except a version a delivery flow pins. Absent from an older control plane.
   */
  versionsKept?: number;
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

/** The T-SQL a person runs in SSMS on the catalog database to read what the overview lists of one cached type, at one version. */
export interface DeliveryCachedItemsSql {
  scope: string;
  version: string;
  typeName: string;
  sql: string;
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

/**
 * A purge of a partition cache's history: the days of replaced versions that keep their records, 0 to 36500, and the most
 * versions that keep them, 2 to 1000; each left out, the partition's own.
 */
export interface DeliveryCachePruneRequest {
  scope: string;
  keepDays?: number;
  versionsKept?: number;
}

/** How many versions of a partition's cache keep their records, the current one counted, as an admin sets it: 2 to 1000. */
export interface DeliveryCacheRetentionRequest {
  scope: string;
  versionsKept: number;
}

/**
 * What a purge of a partition cache's history did, or for a preview would do: the days it keeps, how many versions keep
 * their records, the versions it prunes and the stored rows that go, what it waits for when it cannot prune yet, and one
 * line saying it.
 */
export interface DeliveryCachePruneResult {
  scope: string;
  keepDays: number;
  /** The most versions that keep their records, the current one counted. Absent from an older control plane. */
  versionsKept?: number;
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
