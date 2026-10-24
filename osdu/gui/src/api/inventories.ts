// The inventories of inventory flows (osdu/docs/reference/flow/inventory.md): every id an OSDU kind holds in a partition,
// set against every ledger of the partition, each id with its finding. Same conventions as delivery.ts: one function per
// endpoint, pages compose them with TanStack Query. An answer leaves out what holds no value, so every field that may hold
// none is optional here, and a page tests it with `=== undefined` or `??`, never `=== null`.

import { get, getText, post } from "@/api/client";
import type { ExplorerFilter } from "./explorer";

/**
 * What an inventory finds of an id: what OSDU serves of it set against what the ledgers hold of it. The first eight are the
 * findings a report raises; the rest say the two agree, or why an id needs no look.
 */
export type InventoryFinding =
  | "orphan" | "missing" | "undoing" | "forgotten" | "stale" | "unconfirmed" | "drifted" | "unlisted"
  | "foreign" | "superseded" | "tracked" | "gone" | "unreconciled";

/** Every finding, in the order a report lists them. */
export const INVENTORY_FINDINGS: readonly InventoryFinding[] = [
  "orphan", "missing", "undoing", "forgotten", "stale", "unconfirmed", "drifted", "unlisted", "foreign", "superseded", "tracked", "gone", "unreconciled",
];

/** The findings a report raises: where the ledgers and OSDU disagree. */
export const RAISED_FINDINGS: readonly InventoryFinding[] = ["orphan", "missing", "undoing", "forgotten", "stale", "unconfirmed", "drifted", "unlisted"];

/** The findings whose ids an inventory flow may remove from OSDU, when its document allows it. */
export const REMOVABLE_FINDINGS: readonly InventoryFinding[] = ["orphan", "stale", "forgotten"];

/**
 * The finding whose ids are removed from the inventory alone, whatever the flow declares: OSDU no longer serves them, so
 * nothing is asked of it.
 */
export const GONE_FINDING: InventoryFinding = "gone";

/**
 * How much of each record a removal takes: a soft delete (`record`, reversible) or a purge (`everything`) of an id OSDU
 * serves; the inventory's own row (`inventory`) of a gone id, and only of one.
 */
export type InventoryRemovalScope = "record" | "everything" | "inventory";

/** The finding whose ids a repair delivers again: a record a ledger delivered that OSDU no longer holds. */
export const MISSING_FINDING: InventoryFinding = "missing";

/**
 * A repair of an inventory's missing ids (osdu/docs/reference/flow/inventory-removals.md, Repairing missing ids): the count the
 * operator was shown, the ids picked, and whether the records are delivered under new OSDU ids (which takes the partition
 * typed back).
 */
export interface InventoryRepairRequest {
  expected: number;
  ids?: string[];
  newIds?: boolean;
  confirm?: string;
}

/** What a repair did in one delivery ledger: the flow, how many records it asked for and marked, and the run it queued. */
export interface InventoryRepairFlow {
  ledgerId: string;
  flow: string;
  pipelineId: string;
  asked: number;
  marked: number;
  runId?: string;
}

/** Missing ids a repair left as they were, for one reason, with the first of them it can name. */
export interface InventoryRepairSkip {
  reason: string;
  count: number;
  ids: string[];
}

/** What a repair did: how many ids it was asked for, how many records it marked and how, per flow, and what it left. */
export interface InventoryRepairResult {
  partition: string;
  inventory: string;
  asked: number;
  marked: number;
  newIds: boolean;
  flows: InventoryRepairFlow[];
  skipped: InventoryRepairSkip[];
  activityId: number;
}

/** What a removal came to for one id. */
export type InventoryRemovalOutcome = "removed" | "gone" | "skipped" | "failed";

/** Every outcome, in the order a page lists them. */
export const INVENTORY_REMOVAL_OUTCOMES: readonly InventoryRemovalOutcome[] = ["removed", "gone", "skipped", "failed"];

/**
 * What a flow lets an operator remove of what its inventory found, the platform the removal goes to as the flow writes it, and
 * the storage paths a soft delete (500 ids a request) and a purge (one id at a time) call there.
 */
export interface InventoryRemovalPolicy {
  findings: string[];
  purge: boolean;
  endpoint: string;
  bulkDeletePath: string;
  purgePath: string;
}

/**
 * A removal an operator asks for: the finding, how much of each record, the count shown, the ids picked or the selection they
 * were taken from, and the partition typed back.
 */
export interface InventoryRemovalRequest {
  finding: string;
  scope: InventoryRemovalScope;
  expected: number;
  ids?: string[];
  /** The selection whose ids of the finding are removed: those that held what its conditions asked in OSDU. */
  selection?: number;
  confirm: string;
}

/** A removal queued as a run of the flow. */
export interface InventoryRemovalAccepted {
  runId: string;
  status: string;
  partition: string;
  inventory: string;
  finding: string;
  scope: InventoryRemovalScope;
  expected: number;
  selection?: number;
}

/** One removal of an inventory's ids: its run, who asked, what and how much, where it stands, and what it came to. */
export interface InventoryRemoval {
  inventoryRemovalId: number;
  inventoryId: number;
  runId?: string;
  actor: string;
  finding: string;
  scope: InventoryRemovalScope | string;
  namesIds: boolean;
  requested: number;
  status: InventoryRunStatus | string;
  startedUtc: string;
  completedUtc?: string;
  removed: number;
  gone: number;
  skipped: number;
  failed: number;
  error?: string;
  activityId?: number;
  /** The selection the removal took its ids from, with the conditions that made it; left out for one that took none. */
  selection?: InventorySelectionSummary;
}

/** What one removal did to one id, and why. */
export interface InventoryRemovalItem {
  inventoryRemovalItemId: number;
  inventoryRemovalId: number;
  inventoryRecordId: number;
  targetId: string;
  version?: number;
  finding: string;
  outcome: InventoryRemovalOutcome | string;
  reason?: string;
  ledgerFlowId?: string;
  deliveryKey?: string;
  recordedUtc: string;
}

/** A page of what a removal did to its ids, with the id the next page starts after; left out on the last page. */
export interface InventoryRemovalItemPage {
  items: InventoryRemovalItem[];
  next?: number;
}

/** A finding with how many ids have it, and whether a report raises it. */
export interface InventoryCount {
  finding: string;
  count: number;
  raised: boolean;
}

/** An identity that created ids of an inventory, with how many of the ids a ledger claims it created. */
export interface InventoryOwner {
  identity: string;
  records: number;
}

/** The owners a reconcile used, and how it knew them: `declared` by the flow, `inferred` from what a ledger claims, or `none`. */
export interface InventoryOwners {
  source?: string;
  identities: InventoryOwner[];
}

/** A build or reconcile: running while it reads, then completed or failed. */
export type InventoryRunStatus = "running" | "completed" | "failed";

/** One build or reconcile of an inventory: how it read, what it changed, and what it found when it reconciled. */
export interface InventoryRun {
  inventoryRunId: number;
  /** The platform run it was part of. */
  runId?: string;
  operation: "build" | "reconcile" | string;
  actor: string;
  status: InventoryRunStatus | string;
  startedUtc: string;
  completedUtc?: string;
  /** How it read OSDU: `search` or `storage`. */
  read: string;
  listed: number;
  pages: number;
  requests: number;
  added: number;
  changed: number;
  gone: number;
  returned: number;
  /** The ids a ledger expects that it read from storage, to tell missing from unlisted. */
  missingChecked: number;
  /** The findings it counted, those it found any of (on a run's own answers; a listing's newest run leaves them out). */
  findings?: InventoryCount[];
  raised?: number;
  owners?: InventoryOwners;
  error?: string;
}

/** An inventory as listings show it. */
export interface Inventory {
  inventoryId: number;
  partition: string;
  /** The inventory flow's ledger identity in the partition. */
  ledgerId: string;
  flowName: string;
  name: string;
  kind: string;
  query?: string;
  read: string;
  /** `latest` or `all`. */
  versions: string;
  createdUtc: string;
  updatedUtc: string;
  lastBuildRunId?: number;
  lastBuiltUtc?: string;
  lastReconcileRunId?: number;
  lastReconciledUtc?: string;
  /** The counts by finding the last reconcile wrote, every finding with its zeros. */
  reconciled?: InventoryCount[];
  raised?: number;
  /** The newest run: one under way, or one that failed after the last that completed. */
  latest?: InventoryRun;
}

/** The inventories of the workbench's partition, or of every partition with none picked. */
export interface InventoryList {
  partition?: string;
  inventories: Inventory[];
}

/** One inventory with its report: its counts by finding as its rows hold them now, its owners and its last runs. */
export interface InventoryDetail {
  inventory: Inventory;
  /** The pipeline of the flow declaring it and its repository, for its link and a run of it. */
  pipelineId?: string;
  repoId?: string;
  description?: string;
  /** Whether the flow still declares it; left out when the flow cannot be read now. */
  declared?: boolean;
  counts: InventoryCount[];
  ids: number;
  raised: number;
  owners?: InventoryOwners;
  lastBuild?: InventoryRun;
  lastReconcile?: InventoryRun;
  /** What the flow lets an operator remove of what the inventory found; left out when it allows none. */
  removal?: InventoryRemovalPolicy;
}

/** One id of an inventory: what OSDU serves of it, its finding and why, and what the ledgers hold of it. */
export interface InventoryRecord {
  inventoryRecordId: number;
  inventoryId: number;
  targetId: string;
  kind?: string;
  version?: number;
  createUser?: string;
  createTime?: string;
  modifyUser?: string;
  modifyTime?: string;
  firstSeenUtc?: string;
  changedUtc?: string;
  goneUtc?: string;
  finding: string;
  findingUtc: string;
  /** The ledger that holds the id, by its identity and its name. */
  ledgerFlowId?: string;
  ledger?: string;
  deliveryKey?: string;
  ledgerStatus?: string;
  ledgerVersion?: number;
  artifactId?: number;
  artifactState?: string;
  /** Why the id has its finding. */
  detail?: string;
  /** For an id of a selection: the version the search index held when the match read it. */
  matchedVersion?: number;
  /** For an id of a selection: what it held at each property the selection's conditions ask, property to values. */
  values?: Record<string, string[]>;
}

/** A page of ids, with the id the next page starts after; left out on the last page. */
export interface InventoryRecordPage {
  items: InventoryRecord[];
  next?: number;
}

/** What every inventory of a partition holds of one OSDU id, and what removals did to it, the newest first. */
export interface InventoryLookup {
  partition: string;
  targetId: string;
  hits: { inventory: Inventory; record: InventoryRecord }[];
  removals: InventoryRemovalItem[];
}

/** A parameter a run of an inventory flow takes. */
export interface InventoryParameter {
  name: string;
  required: boolean;
  default?: string;
  description?: string;
}

/** An inventory a flow declares or keeps, with the inventory as the partition keeps it once a build registered it. */
export interface InventoryEntry {
  name: string;
  description?: string;
  kind: string;
  query?: string;
  versions: string;
  declared: boolean;
  inventory?: Inventory;
}

/** One inventory flow in the workbench's partition: whether it reads it, how, and its inventories. */
export interface InventoryFlow {
  pipelineId: string;
  repoId: string;
  name: string;
  description?: string;
  batch?: string;
  partition?: string;
  readsPartition: boolean;
  partitions: string[];
  ledgerId?: string;
  read?: string;
  owners: string[];
  parameters: InventoryParameter[];
  problem?: string;
  inventories: InventoryEntry[];
}

/**
 * Where a selection's match stands: queued as a run of the flow, reading OSDU, completed, failed (it holds no id), or expired
 * (older than a week, kept for the removal that took its ids).
 */
export type InventorySelectionStatus = "queued" | "running" | "completed" | "failed" | "expired";

/** How a match read OSDU: every record the conditions find, joined with the inventory's ids; or the inventory's ids a chunk at a time. */
export type InventorySelectionDirection = "conditions" | "ids";

/**
 * The ids of an inventory that hold what an operator's conditions ask in OSDU (osdu/docs/reference/flow/inventory-selections.md,
 * Selecting ids by what OSDU holds): the conditions as written, where the match stands and whether the inventory was built again
 * since (stale), the query and how it read OSDU, how far it got and what it found, and its ids counted by finding once it completed.
 */
export interface InventorySelection {
  selectionId: number;
  inventoryId: number;
  partition: string;
  status: InventorySelectionStatus | string;
  stale: boolean;
  conditions: ExplorerFilter[];
  query?: string;
  direction?: InventorySelectionDirection | string;
  notes: string[];
  /** The properties each id's row carries the values of: those the conditions ask. */
  columns: string[];
  /** The ids the inventory served that the match judged. */
  candidates: number;
  /** How much the match has to read, and has read: the records the conditions find, or the inventory's ids. */
  toCheck: number;
  checked: number;
  matched: number;
  /** Records the conditions found that the inventory holds no row of: written since its build, or outside its query. */
  outside: number;
  /** Ids matched at another version than the inventory listed, which a removal leaves for the next build. */
  changed: number;
  requests: number;
  /** Its ids by the finding the inventory gives each now, every finding with its zeros; once the match completed. */
  counts?: InventoryCount[];
  runId?: string;
  actor: string;
  createdUtc: string;
  startedUtc?: string;
  completedUtc?: string;
  error?: string;
  /** For a selection a duplicate check made of its picks: the check, and the key it compared the records by. It holds no conditions. */
  duplicateCheckId?: number;
  duplicateKey?: DuplicateKeyProperty[];
}

/** The selection a removal took its ids from, as the removal names it: by its conditions, or by the duplicate check that made it. */
export interface InventorySelectionSummary {
  selectionId: number;
  status: string;
  conditions: ExplorerFilter[];
  query?: string;
  matched: number;
  matchedUtc?: string;
  duplicateCheckId?: number;
  duplicateKey?: DuplicateKeyProperty[];
}

/** The most properties a key compares, as the control plane takes them. */
export const MAX_DUPLICATE_KEY = 100;

/**
 * One property of a duplicate check's key (osdu/docs/reference/flow/inventory-duplicates.md, The key): its path from the record's
 * root, whose values the copies of a record hold equal.
 */
export interface DuplicateKeyProperty {
  path: string;
}

/**
 * Where a check stands: queued as a run of the flow, downloading or picking (running), completed (its download read whole; a pick
 * that stopped leaves it completed with its error), failed (its download stopped; it holds no group), or expired.
 */
export type DuplicateCheckStatus = "queued" | "running" | "completed" | "failed" | "expired";

/** How a group of copies came out: left for review, its copy to keep picked or chosen, settled, or not read yet. */
export type DuplicateOutcome = "review" | "picked" | "chosen" | "settled" | "pending";

/** Every outcome, in the order a page lists them: what needs a look first. */
export const DUPLICATE_OUTCOMES: readonly DuplicateOutcome[] = ["review", "picked", "chosen", "settled", "pending"];

/** A copy's role in its group: kept, picked to remove, left (the inventory may not remove it), for review, or gone from storage. */
export type DuplicateRole = "keep" | "remove" | "leave" | "review" | "gone";

/** How many of a check's groups came out one way. */
export interface DuplicateOutcomeCount {
  outcome: DuplicateOutcome | string;
  groups: number;
}

/** How many of a check's groups hold from `from` to `to` copies (no end for the last size), and the copies they hold. */
export interface DuplicateSize {
  from: number;
  to?: number;
  groups: number;
  copies: number;
}

/**
 * A check of an inventory's ids for duplicate records: the key its groups are formed by, where it stands and what it does
 * (downloading or picking), what it read and found, its groups by outcome and by size, the selection last made of its picks and
 * whether a choice since leaves it behind, its run, who asked and when, and why its download failed or its pick stopped; then
 * the properties it downloaded (any key of which groups it again without reading OSDU), its table, who set its key, when it was
 * last picked, and the groups waiting to be picked with the copies a pick reads.
 */
export interface InventoryDuplicateCheck {
  checkId: number;
  inventoryId: number;
  partition: string;
  status: DuplicateCheckStatus | string;
  stale: boolean;
  /** While it runs or waits to: `keys` (downloading every id's properties) or `copies` (picking: reading each group's copies). */
  phase?: "keys" | "copies" | string;
  key: DuplicateKeyProperty[];
  notes: string[];
  /** The ids the inventory served when it started. */
  candidates: number;
  /** The records the search returned while it downloaded. */
  checked: number;
  keyed: number;
  unkeyed: number;
  outside: number;
  changed: number;
  groups: number;
  copies: number;
  groupsJudged: number;
  decided: number;
  review: number;
  toRemove: number;
  requests: number;
  outcomes?: DuplicateOutcomeCount[];
  selectionId?: number;
  /** Whether the selection last made of its picks still says what it picks: no choice was written since it was made. */
  selectionCurrent: boolean;
  chosenUtc?: string;
  runId?: string;
  actor: string;
  createdUtc: string;
  startedUtc?: string;
  completedUtc?: string;
  error?: string;
  /** The properties it downloaded, by path. */
  attributes: string[];
  /** Its table in the module's database (`osdu.dup_<check>`), which holds what it downloaded; none before its download. */
  table?: string;
  keyedBy?: string;
  keyedUtc?: string;
  /** When a pick last read every group's copies; none while groups wait to be picked. */
  pickedUtc?: string;
  sizes?: DuplicateSize[];
  pendingGroups: number;
  pendingCopies: number;
}

/** One copy of a group: the inventory's row of it, what the check read of it, and its role with why. */
export interface DuplicateCopy {
  record: InventoryRecord;
  indexedVersion?: number;
  /** What it holds at each property of the key, property to values, as it holds them. */
  values?: Record<string, string[]>;
  /** What storage answered when its group was read: `held`, `missing` or `unanswered`. */
  storageState?: string;
  storedVersion?: number;
  /** How many values it holds under data: how complete the copy is. */
  attributes?: number;
  /** How many records name it. */
  mentionedBy?: number;
  role?: DuplicateRole | string;
  reason?: string;
}

/** One group of ids sharing a key: its copies, how it came out and why, the copy it keeps and who chose it. */
export interface DuplicateGroup {
  groupNumber: number;
  copies: number;
  values?: Record<string, string[]>;
  outcome: DuplicateOutcome | string;
  reason?: string;
  keptInventoryRecordId?: number;
  chosenBy?: string;
  chosenUtc?: string;
  /** Its copies, left out for a group too large to list with its page (read them with `copies`). */
  members?: DuplicateCopy[];
}

/** A page of groups, with the group the next page starts after; left out on the last page. */
export interface DuplicateGroupPage {
  items: DuplicateGroup[];
  next?: number;
}

/** A page of one group's copies, with the row the next page starts after; left out on the last page. */
export interface DuplicateCopyPage {
  items: DuplicateCopy[];
  next?: number;
}

const inventoryPath = (partition: string, inventoryId: number) =>
  `/api/v1/delivery/inventories/${encodeURIComponent(partition)}/${inventoryId}`;

export const inventoryApi = {
  /** The inventories of the workbench's partition, each with what its last reconcile raised and its newest run. */
  list: () => get<InventoryList>("/api/v1/delivery/inventories"),
  /** An inventory flow's inventories in the workbench's partition. */
  flow: (pipelineId: string) => get<InventoryFlow>(`/api/v1/delivery/flows/${pipelineId}/inventories`),
  /** One inventory with its counts by finding, owners and last runs. */
  inventory: (partition: string, inventoryId: number) => get<InventoryDetail>(inventoryPath(partition, inventoryId)),
  /** A page of an inventory's ids, of one finding or every one (only those a selection holds, when one is named), after the id `after` names. */
  records: (partition: string, inventoryId: number, query: { finding?: string; after?: number; limit?: number; selection?: number } = {}) =>
    get<InventoryRecordPage>(`${inventoryPath(partition, inventoryId)}/records`, query),
  /** An inventory's builds and reconciles, newest first. */
  runs: (partition: string, inventoryId: number, limit?: number) =>
    get<InventoryRun[]>(`${inventoryPath(partition, inventoryId)}/runs`, limit === undefined ? {} : { limit }),
  /** What every inventory of the workbench's partition holds of one OSDU id. */
  lookup: (id: string) => get<InventoryLookup>("/api/v1/delivery/inventories/lookup", { id }),
  /** An inventory's ids of one finding (every id with none) as CSV text; only those a selection holds, when one is named. */
  exportCsv: (partition: string, inventoryId: number, finding?: string, selection?: number) => {
    const query = new URLSearchParams();
    if (finding !== undefined) {
      query.set("finding", finding);
    }

    if (selection !== undefined) {
      query.set("selection", String(selection));
    }

    const text = query.toString();
    return getText(`${inventoryPath(partition, inventoryId)}/export${text === "" ? "" : `?${text}`}`);
  },
  /**
   * The ids of the inventory that hold what the conditions ask in OSDU: the match made of the same conditions against the same
   * build when there is one, else a new match queued as a run of the flow (`refresh` always queues one).
   */
  select: (partition: string, inventoryId: number, request: { filters: ExplorerFilter[]; refresh?: boolean }) =>
    post<InventorySelection>(`${inventoryPath(partition, inventoryId)}/selections`, request),
  /** One selection of the inventory: where its match stands, and its ids counted by finding once it completed. */
  selection: (partition: string, inventoryId: number, selectionId: number) =>
    get<InventorySelection>(`${inventoryPath(partition, inventoryId)}/selections/${selectionId}`),
  /**
   * The inventory's ids that are copies of one another by the key: the current check grouped by it at once when it downloaded
   * every property of the key (200), else a new check downloading them, queued as a run of the flow (202; `refresh` always
   * downloads again).
   */
  checkDuplicates: (partition: string, inventoryId: number, request: { key: DuplicateKeyProperty[]; refresh?: boolean }) =>
    post<InventoryDuplicateCheck>(`${inventoryPath(partition, inventoryId)}/duplicates`, request),
  /** Asks for a pick of a completed check: its groups' copies read from storage, and the copy each group keeps picked. */
  pickDuplicates: (partition: string, inventoryId: number, checkId: number) =>
    post<InventoryDuplicateCheck>(`${inventoryPath(partition, inventoryId)}/duplicates/${checkId}/pick`, {}),
  /** The inventory's duplicate checks, newest first. */
  duplicateChecks: (partition: string, inventoryId: number, limit?: number) =>
    get<InventoryDuplicateCheck[]>(`${inventoryPath(partition, inventoryId)}/duplicates`, limit === undefined ? {} : { limit }),
  /** One duplicate check: where it stands, and its groups counted by outcome once it completed. */
  duplicateCheck: (partition: string, inventoryId: number, checkId: number) =>
    get<InventoryDuplicateCheck>(`${inventoryPath(partition, inventoryId)}/duplicates/${checkId}`),
  /** A page of a check's groups, of one outcome or every one, after the group `after` names, each with its copies. */
  duplicateGroups: (partition: string, inventoryId: number, checkId: number, query: { outcome?: string; after?: number; limit?: number } = {}) =>
    get<DuplicateGroupPage>(`${inventoryPath(partition, inventoryId)}/duplicates/${checkId}/groups`, query),
  /** A page of one group's copies, for a group too large to list with its page. */
  duplicateCopies: (partition: string, inventoryId: number, checkId: number, groupNumber: number, query: { after?: number; limit?: number } = {}) =>
    get<DuplicateCopyPage>(`${inventoryPath(partition, inventoryId)}/duplicates/${checkId}/groups/${groupNumber}/copies`, query),
  /** Keeps a copy of a group, as the caller; null takes a choice back, leaving the group to the check's own pick. */
  keepDuplicate: (partition: string, inventoryId: number, checkId: number, groupNumber: number, inventoryRecordId: number | null) =>
    post<DuplicateGroup>(`${inventoryPath(partition, inventoryId)}/duplicates/${checkId}/groups/${groupNumber}/keep`, { inventoryRecordId }),
  /** The copies a completed check picked to remove, made a selection a removal takes. */
  selectDuplicates: (partition: string, inventoryId: number, checkId: number) =>
    post<InventorySelection>(`${inventoryPath(partition, inventoryId)}/duplicates/${checkId}/selection`, {}),
  /** Every copy of every group of a check as CSV text. */
  exportDuplicates: (partition: string, inventoryId: number, checkId: number) =>
    getText(`${inventoryPath(partition, inventoryId)}/duplicates/${checkId}/export`),
  /** Queues the run that removes from OSDU the ids of one finding of the inventory, as the caller. */
  remove: (partition: string, inventoryId: number, request: InventoryRemovalRequest) =>
    post<InventoryRemovalAccepted>(`${inventoryPath(partition, inventoryId)}/removals`, request),
  /** Delivers again, through their flows, the records behind missing ids of the inventory, as the caller. */
  repair: (partition: string, inventoryId: number, request: InventoryRepairRequest) =>
    post<InventoryRepairResult>(`${inventoryPath(partition, inventoryId)}/repairs`, request),
  /** An inventory's removals, newest first. */
  removals: (partition: string, inventoryId: number, limit?: number) =>
    get<InventoryRemoval[]>(`${inventoryPath(partition, inventoryId)}/removals`, limit === undefined ? {} : { limit }),
  /** A page of what one removal did to its ids, of one outcome or every one, after the item `after` names. */
  removalItems: (partition: string, removalId: number, query: { outcome?: string; after?: number; limit?: number } = {}) =>
    get<InventoryRemovalItemPage>(`/api/v1/delivery/inventories/${encodeURIComponent(partition)}/removals/${removalId}/items`, query),
};
