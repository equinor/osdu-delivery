// Dimension blueprints as the delivery API answers them: how a dimension is built, read by read from the records its
// templates describe to the columns of its table, with its YAML and how much of each attribute its last build read.

/** What a read of a blueprint is for: the dimension's key, its label, an attribute read through a key's record, or one collected from its own records. */
export type BlueprintRole = "key" | "label" | "attribute" | "collect";

/** What a column of a dimension's table holds. */
export type BlueprintColumnRole = "id" | "partition" | "keyId" | "key" | "value" | "records" | "filter" | "inserted" | "updated" | "attribute";

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
