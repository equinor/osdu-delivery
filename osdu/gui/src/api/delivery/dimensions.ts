// Dimension flows as the delivery API answers them: the board of every flow's dimensions, a dimension with its builds,
// its values, keys and attributes, its change log and the search filter of its values.

import type { DeliveryDimensionFilterValue } from "./dimensionQueries";
import type { DeliveryParameter } from "./flows";

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
  /**
   * The dimension's own table where its flow's target keeps it (`osdu.dim_<dimension>`, or
   * `<database>.<schema>.dim_<dimension>`); null until a build has written it.
   */
  table: string | null;
  /**
   * Where the table is kept, or will be, as a person types it before its name: `osdu`, or `<database>.<schema>` when the
   * flow's target names a database. Absent from an older control plane.
   */
  kept?: string;
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
