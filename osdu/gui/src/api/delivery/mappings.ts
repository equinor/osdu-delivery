// Mappings as the delivery API answers them: the mappings the catalog holds and the flows rendering with each, what the
// mapping builder offers, and what it answers of a document: composed, read back, covered, shaped and outlined.

import type { MappingOutline } from "../mappingOutline";
import type { MappingDraft } from "./mappingDraft";

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

/** A mapping's outline, how many of its template's properties it writes nothing to, and what is wrong with it. */
export interface DeliveryMappingOutlineResult {
  /** Null when the document does not load. */
  outline: MappingOutline | null;
  /** Null when its template cannot be read. */
  unfilled: number | null;
  issues: MappingDraftIssue[];
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
