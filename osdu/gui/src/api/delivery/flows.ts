// A delivery flow as the delivery API answers it: its record counts, its interfaces and the parameters they declare,
// the checks of its target and of the access it can restrict, the partitions it delivers to, and the scope naming one
// of its ledgers, with the label and the page link of that ledger.

import type { DeliverySubmission } from "./submissions";

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
  /**
   * Held records an adoption may be asked of: held because OSDU already holds a record at the id each one's key makes, and
   * claiming none. The Delivery tab's Adopt asks every one. Absent from an older control plane.
   */
  adoptable?: number;
  drifted: number;
  /**
   * Records whose last render set an enrichment aside on its hold, each going ahead with what the enrichment added to its last
   * delivery. Absent from an older control plane.
   */
  stepsHeld?: number;
  /**
   * Records whose access in OSDU is restricted under a registered access restriction, or asked to be, whatever their status:
   * no run delivers them until it is lifted. Absent from an older control plane.
   */
  restricted?: number;
  /**
   * Records OSDU holds restricted until complete, whatever their status: delivered under a registered access restriction's
   * groups because values their mapping marks `$restrictWhenMissing` were missing, each going back to its own access once a
   * delivery finds them all (osdu/docs/reference/flow/mapping-restricted-access.md). Absent from an older control plane.
   */
  restrictedUntilComplete?: number;
  /**
   * Records whose access in OSDU is restricted either way, until complete or by an operator, each record once: the flow's
   * Restricted figure. Each is counted under its status too, so it is never part of the total. Absent from an older control plane.
   */
  accessRestricted?: number;
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

/** Why a flow's counts could not be read: what its own stats call answers with (the document does not parse, say). */
export interface DeliveryFlowProblem {
  status: number;
  title: string;
  detail?: string | null;
}

/**
 * One delivery flow's counts as the pages that show every flow read them, all in one call: the flow as a whole, its counts
 * in the partition asked for when it is counted there apart from its whole, and why, when they could not be read.
 */
export interface DeliveryFlowCounts {
  pipelineId: string;
  /** The flow as a whole; null when its counts could not be read. */
  stats: DeliveryFlowStats | null;
  /** Its counts in the partition asked for, when it names that partition among others; absent otherwise. */
  inPartition?: DeliveryFlowStats | null;
  problem?: DeliveryFlowProblem | null;
}

/** One registered restriction as a check of a flow answers it: whether the flow can restrict under it and lift it again, and why not. */
export interface DeliveryAccessRestrictionUse {
  restrictionId: number;
  name?: string | null;
  usable: boolean;
  refusal?: string | null;
}

/**
 * Whether a flow can restrict its records under each access restriction of its partition, asked of OSDU under the flow's
 * own credentials before a restriction is queued: the flow's identity as Entitlements named it, why the flow restricts
 * nothing at all, and each restriction with why a restriction under it would be refused.
 */
export interface DeliveryAccessCheck {
  flow: string;
  identity?: string | null;
  refusal?: string | null;
  restrictions: DeliveryAccessRestrictionUse[];
  checkedUtc?: string;
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
