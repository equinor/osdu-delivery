// The explorer's calls (osdu/docs/reference/concepts/explorer.md): how a partition of OSDU is reached, and the reads
// made of it there, the kinds its records are of, a page of the records a search finds, the properties a kind's records
// hold, and one record. Every read is answered by the control plane at once, through a connection it keeps open between
// reads.

import { get, post } from "@/api/client";
import { flowPath, type DeliveryDimension, type DeliveryDimensionAttribute, type DeliveryDimensionYaml, type DeliveryFlowScope, type DeliveryOsduRead, type DimensionBlueprint } from "./delivery";
import { guidanceOf, type AssertionAction, type AssertionStage, type ValidationGuidance, type ValidationVerdict } from "./validation";

/**
 * How the explorer reaches a partition: through the partition's own connection (the platform and client its configuration
 * names), and where that reaches, or why it cannot be made.
 */
export interface ExplorerConnection {
  partition: string | null;
  available: boolean;
  /** The platform's base URL, without credentials or query; a configured reference as written. */
  endpoint: string | null;
  reason: string | null;
}

/** How the platform indexes a property, which decides how a value of it is asked for. */
export type ExplorerIndex = "text" | "keyword" | "number" | "boolean" | "date";

export type ExplorerSort = "relevance" | "modified" | "created";

/**
 * What a condition asks of a property: its whole value is (or is not) one value or one of several, its text contains
 * words (any case), its whole value starts with a text (exact case), a value in a range, or whether it holds a value.
 */
export type ExplorerCondition = "is" | "isNot" | "anyOf" | "noneOf" | "contains" | "startsWith" | "range" | "exists" | "missing";

/** A condition a page narrows to: a property, how the platform indexes it, and what it must hold. */
export interface ExplorerFilter {
  path: string;
  index: ExplorerIndex;
  /** The nested list the property sits in, which the query reaches it through. */
  nested?: string;
  /** `is` when left out. */
  condition?: ExplorerCondition;
  /** The whole value, the words, the start, or a range's lower bound. */
  value?: string;
  /** The values `anyOf` compares. */
  values?: string[];
  /** A range's upper bound, left out of it. */
  to?: string;
  /**
   * A search term (osdu/docs/reference/concepts/search-terms.md) the condition names in place of the property: its
   * values are the source's own, and the control plane turns it into the condition on the property `path` names, the
   * one the term's route fills.
   */
  term?: string;
}

/** What the explorer asks of OSDU. */
export interface ExplorerSearchRequest {
  text?: string;
  lucene?: boolean;
  mentions?: string;
  kind?: string;
  filters?: ExplorerFilter[];
  sort?: ExplorerSort;
  offset?: number;
  limit?: number;
  facet?: { path: string; index: ExplorerIndex; nested?: string };
  /** The properties whose values each record carries, as columns beside it; at most 8. */
  columns?: string[];
}

/** How the explorer read a search: everything, a Lucene query, an id, the start of one, text, or a record's mentions. */
export type ExplorerReading = "everything" | "lucene" | "id" | "idPrefix" | "text" | "mentions";

export interface ExplorerHit {
  id: string;
  kind: string | null;
  version: number | null;
  name: string | null;
  nameField: string | null;
  createTime: string | null;
  createUser: string | null;
  modifyTime: string | null;
  modifyUser: string | null;
  /** What the record holds at each column asked, its first values; a column it holds nothing at is left out, as the whole is without columns. */
  values?: Record<string, string[]>;
}

export interface ExplorerBucket {
  value: string | null;
  count: number;
}

export interface ExplorerPage {
  reading: ExplorerReading;
  query: string | null;
  kind: string;
  total: number;
  offset: number;
  hits: ExplorerHit[];
  facetPath: string | null;
  facet: ExplorerBucket[] | null;
  notes: string[];
  /** The search service's words when it refused the query; left out of the answer when it did not, as every null is. */
  refusal?: string | null;
}

export interface ExplorerTypes {
  reading: ExplorerReading;
  query: string | null;
  total: number;
  kinds: { kind: string; count: number }[];
  /** What the kinds add up to: less than the total when the service named fewer groups than there are. */
  listed: number;
  notes: string[];
  /** The search service's words when it refused the query; left out of the answer when it did not, as every null is. */
  refusal?: string | null;
}

/**
 * Where a property was found: `record`, a property of every record; `schema`, declared by the kind's schema; `records`,
 * held by the records read and declared by no schema read (an index augmentation, such as Augmented.WellboreName).
 */
export type ExplorerFieldOrigin = "record" | "schema" | "records";

export interface ExplorerFieldInfo {
  path: string;
  index: ExplorerIndex;
  /** The nested list a query reaches it through; left out for none, as every null of the answer is. */
  nested?: string | null;
  origin?: ExplorerFieldOrigin | null;
  title?: string | null;
  description?: string | null;
}

/** The properties a kind's records hold: the record's own, those its schema declares, and those its records hold beyond them. */
export interface ExplorerFields {
  kind: string;
  sampleId?: string | null;
  fields: ExplorerFieldInfo[];
  /** The kind whose schema was read from the Schema service; left out when none was. */
  schemaKind?: string | null;
  /** How many records were read for the properties they hold. */
  sampled?: number;
  notes?: string[] | null;
}

/** One of the explorer's answers, with the connection and partition it was read through. */
export interface ExplorerAnswer<T> {
  connection: string;
  partition: string | null;
  correlationId: string;
  answeredUtc: string;
  answer: T;
}

/** The furthest a page of the search service reaches (Elasticsearch's result window). */
export const EXPLORER_WINDOW = 10_000;

/** The rows one page reads. */
export const EXPLORER_PAGE = 100;

/** The largest page the API takes. */
export const EXPLORER_MAX_PAGE = 200;

const partitionQuery = (partition: string | null) => (partition === null ? "" : `?partition=${encodeURIComponent(partition)}`);

export const explorerApi = {
  /** How the partition is reached; the workbench's when none is named. */
  connection: (partition: string | null) =>
    get<ExplorerConnection>("/api/v1/delivery/explorer/connection", partition === null ? {} : { partition }),
  /** The count of the records a search finds, kind by kind. */
  types: (partition: string | null, request: ExplorerSearchRequest) =>
    post<ExplorerAnswer<ExplorerTypes>>(`/api/v1/delivery/explorer/types${partitionQuery(partition)}`, request),
  /** One page of the records a search finds. */
  search: (partition: string | null, request: ExplorerSearchRequest) =>
    post<ExplorerAnswer<ExplorerPage>>(`/api/v1/delivery/explorer/search${partitionQuery(partition)}`, request),
  /** The properties a kind's records hold, read from one of them. */
  fields: (partition: string | null, kind: string) =>
    post<ExplorerAnswer<ExplorerFields>>(`/api/v1/delivery/explorer/fields${partitionQuery(partition)}`, { kind }),
  /** One record from the storage service, at its latest or at one version. */
  read: (partition: string | null, targetId: string, version?: number) =>
    post<DeliveryOsduRead>(`/api/v1/delivery/explorer/read${partitionQuery(partition)}`, version === undefined ? { targetId } : { targetId, version }),
  /** One record checked against the schema of its kind: the Schema service's, or a template a synced repository holds. */
  validate: async (partition: string | null, request: ExplorerValidateRequest) => {
    const answered = await post<ExplorerAnswer<ExplorerValidation>>(`/api/v1/delivery/explorer/validate${partitionQuery(partition)}`, request);
    return { ...answered, answer: validationOf(answered.answer) };
  },
  /** One record checked as `validate` checks it, read through a flow's own route and credentials: what a record page shows. */
  validateThroughFlow: async (pipelineId: string, scope: DeliveryFlowScope, request: ExplorerValidateRequest) => {
    const answered = await post<ExplorerAnswer<ExplorerValidation>>(flowPath(pipelineId, "/osdu/validate", scope), request);
    return { ...answered, answer: validationOf(answered.answer) };
  },
  /** The records a search finds, up to 1,000, checked against their schemas and counted by the rules they break. */
  validateList: async (partition: string | null, request: ExplorerValidateListRequest) => {
    const answered = await post<ExplorerAnswer<ExplorerListValidation>>(`/api/v1/delivery/explorer/validate-list${partitionQuery(partition)}`, request);
    return { ...answered, answer: listValidationOf(answered.answer) };
  },
  /**
   * The types whose schemas name records of a type, from the partition's schemas, read once and kept by the control
   * plane; `refresh` reads them again. While they are read, the answer says where the reading stands.
   */
  referencedBy: async (partition: string | null, request: ExplorerReferencesRequest) => {
    const answered = await post<ExplorerAnswer<ExplorerReferences>>(`/api/v1/delivery/explorer/referenced-by${partitionQuery(partition)}`, request);
    return { ...answered, answer: referencesOf(answered.answer) };
  },
};

/** The type whose referring types are asked for: `group--Type`, or a kind naming one. */
export interface ExplorerReferencesRequest {
  type: string;
  /** Reads the partition's schemas again first: those added since, and those in development. */
  refresh?: boolean;
}

/** A place a kind's records name the type asked about. */
export interface ExplorerReferencePlace {
  /** The property path, a list's items by `[]`: `data.VerticalMeasurements[].VerticalMeasurementUnitOfMeasureID`. */
  at: string;
  /** The schema marks no `x-osdu-relationship` there; the type is read from the pattern an id there must match. */
  byPattern: boolean;
}

/** One kind whose schema names the type asked about. */
export interface ExplorerReferringKind {
  kind: string;
  version: string;
  /** PUBLISHED, DEVELOPMENT or OBSOLETE. */
  status: string;
  places: ExplorerReferencePlace[];
  morePlaces: number;
  /** Its schema holds a reference that was not followed, behind which another place may be. */
  partial: boolean;
}

/** A kind of a referring type whose schema names the type asked about nowhere. */
export interface ExplorerKindVersion {
  kind: string;
  version: string;
  status: string;
  partial: boolean;
}

/** A type with a kind whose records name the type asked about: the kinds that do, and those that do not. */
export interface ExplorerReferringType {
  entityType: string;
  kinds: ExplorerReferringKind[];
  without: ExplorerKindVersion[];
}

/** Where the reading of the partition's schemas stands while it runs. */
export interface ExplorerReferencesProgress {
  startedUtc: string;
  /** Still listing the schemas, before any is read. */
  listing: boolean;
  listed: number;
  toRead: number;
  read: number;
  failed: number;
}

/** The types whose records name records of a type, as the partition's schemas declare them. */
export interface ExplorerReferences {
  entityType: string;
  /** reading: the schemas are being read (what was read before answers meanwhile); ready; failed: see `problem`. */
  state: "reading" | "ready" | "failed";
  /** When the reading answered from was made; null before the first one is. */
  readUtc: string | null;
  /** How many schemas the service listed, and how many kinds were read, in that reading. */
  listed: number;
  kinds: number;
  progress: ExplorerReferencesProgress | null;
  problem: string | null;
  types: ExplorerReferringType[];
  /** Types with a place naming any record of the type's group, which may be one of the type. */
  anyOfGroup: ExplorerReferringType[];
  unread: { kind: string; why: string }[];
  unreadCount: number;
  notes: string[];
}

function referencesOf(answer: ExplorerReferences): ExplorerReferences {
  return {
    ...answer,
    readUtc: answer.readUtc ?? null,
    listed: answer.listed ?? 0,
    kinds: answer.kinds ?? 0,
    progress: answer.progress ?? null,
    problem: answer.problem ?? null,
    types: answer.types ?? [],
    anyOfGroup: answer.anyOfGroup ?? [],
    unread: answer.unread ?? [],
    unreadCount: answer.unreadCount ?? 0,
    notes: answer.notes ?? [],
  };
}

// A task's answer leaves a property out when it is null, so the parts of a check that can be null are read as null when
// they are absent, and the lists as empty: every view of a check then reads one shape.

function validationOf(answer: ExplorerValidation): ExplorerValidation {
  return {
    ...answer,
    version: answer.version ?? null,
    kind: answer.kind ?? null,
    schema: answer.schema ?? null,
    verdict: answer.verdict ?? null,
    problem: answer.problem ?? null,
    templateVersions: answer.templateVersions ?? [],
    guidance: guidanceOf(answer.guidance),
  };
}

function listValidationOf(answer: ExplorerListValidation): ExplorerListValidation {
  return {
    ...answer,
    query: answer.query ?? null,
    notFound: answer.notFound ?? [],
    rules: (answer.rules ?? []).map((rule) => ({ ...rule, expected: rule.expected ?? null, advice: rule.advice ?? null })),
    records: (answer.records ?? []).map((record) => ({ ...record, kind: record.kind ?? null, first: record.first ?? null })),
    schemas: answer.schemas ?? [],
    unavailable: answer.unavailable ?? [],
    notes: answer.notes ?? [],
  };
}

// ---- Validation (osdu/docs/reference/concepts/explorer.md, Validate) ----

/** Which schema a record is checked against: what the partition's Schema service holds, or a template a synced repository holds. */
export type ExplorerSchemaChoice = "osdu" | "template";

export interface ExplorerValidateRequest {
  targetId: string;
  version?: number;
  schema?: ExplorerSchemaChoice;
  /** With `template`, the template version, among those the synced repositories hold; the one the repository synced last holds when left out. */
  templateVersion?: string;
  /** The synced mapping (its id) whose assertions are judged on the record; none when left out. */
  mapping?: string;
}

/** The schema a record was checked against, where it came from, and what reading it could not resolve. */
export interface ExplorerSchema {
  kind: string;
  version: string;
  source: "template" | "schema-service";
  read: string[];
  unresolved: string[];
  notes: string[];
}

/** A record checked against a schema: the schema and the verdict, or why nothing was checked. */
export interface ExplorerValidation {
  targetId: string;
  version: number | null;
  found: boolean;
  kind: string | null;
  schema: ExplorerSchema | null;
  verdict: ValidationVerdict | null;
  problem: string | null;
  /** The template versions the synced repositories hold for the kind, the one synced last first, to check against instead. */
  templateVersions: string[];
  /** What each finding of the verdict comes to for the person fixing it. */
  guidance: ValidationGuidance | null;
}

export interface ExplorerValidateListRequest {
  search: ExplorerSearchRequest;
  /** The most records read and checked, 1 to 1,000. */
  max?: number;
  schema?: ExplorerSchemaChoice;
  /** The synced mapping (its id) whose assertions are judged on the records of the entity type it renders; none when left out. */
  mapping?: string;
}

/** A rule the records of a list break: how many records, how many times, and one example. */
export interface ExplorerRuleCount {
  at: string;
  rule: string;
  records: number;
  problems: number;
  exampleId: string;
  examplePath: string;
  exampleMessage: string;
  exampleValue: string;
  /** What the schema expects where the rule is, in one line. */
  expected: string | null;
  /** How to make the example meet the rule. */
  advice: string | null;
}

export interface ExplorerRecordVerdict {
  id: string;
  kind: string | null;
  outcome: "valid" | "invalid" | "unverified" | "notValidated";
  problems: number;
  unverified: number;
  /** The first problem (or part not checked), where and why. */
  first: string | null;
  /** How many judgements of the mapping's assertions failed on the record; absent when none were judged. */
  assertionFailures?: number | null;
}

/** An assertion of a mapping the records of a list fail: how many records, how many times, and one example. */
export interface ExplorerAssertionCount {
  at: string;
  assertion: string;
  stage: AssertionStage;
  onFail: AssertionAction;
  records: number;
  failures: number;
  exampleId: string;
  examplePath: string;
  exampleMessage: string;
  exampleValue: string;
}

/** The records a search finds checked against their schemas, up to a bound, counted by outcome and rule. */
export interface ExplorerListValidation {
  reading: ExplorerReading;
  query: string | null;
  kind: string;
  matched: number;
  asked: number;
  read: number;
  notFound: string[];
  valid: number;
  invalid: number;
  unverified: number;
  notChecked: number;
  rules: ExplorerRuleCount[];
  records: ExplorerRecordVerdict[];
  schemas: ExplorerSchema[];
  unavailable: { kind: string; why: string; records: number }[];
  cut: boolean;
  notes: string[];
  /** The mapping whose assertions were judged, as `Name@version`; absent when none was asked for or could be read. */
  mapping?: string | null;
  /** The records the mapping's assertions were judged on: those of the entity type it renders. */
  asserted?: number;
  /** Of those, the records that failed one or more of them. */
  failingAssertions?: number;
  /** The assertions the records fail, the most records first. */
  assertions?: ExplorerAssertionCount[];
}

/** The most records one check of a search reads. */
export const EXPLORER_MAX_CHECKED = 1000;

/** A record read by the explorer: what a record page's read-back answers, read from the storage service. */
export type ExplorerRead = DeliveryOsduRead;

// ---- The query of an element (osdu/docs/reference/concepts/explorer.md, A record) ----

/** A query that finds records by an element of a record, and what it finds, in words. */
export interface ExplorerElementQuery {
  /** `exact` (exactly this value, or every value a section holds), `words` (the words of a text, their case aside) or `exists` (any value there). */
  purpose: "exact" | "words" | "exists";
  /** The Lucene query, as the search service's `query` takes it. */
  query: string;
  says: string;
}

/** How the index holds an element: its path, how it is kept (`text`, `keyword`, `number`, `boolean`, `date`, or `section`), and the nested or flattened list it sits in. */
export interface ExplorerElementField {
  path: string;
  index: string;
  nestedPath: string | null;
  flattenedPath: string | null;
}

/** The queries that find records by an element, the exact one first; how the index holds it, in words; the template read; why a query is a guess; and why none can be written, where none can. */
export interface ExplorerElementAnswer {
  kind: string;
  path: string;
  template: { kind: string; version: string } | null;
  field: ExplorerElementField | null;
  reading: string | null;
  queries: ExplorerElementQuery[];
  /** Why the queries are written from the values, where the template could not say how they are indexed; null otherwise. */
  guess: string | null;
  problem: string | null;
  notes: string[];
}

/** A value inside a section, by its path in the record, and what the record holds there. */
export interface ExplorerElementLeaf {
  path: string;
  value: string | number | boolean | null;
}

export interface ExplorerElementRequest {
  kind: string;
  /** The element's path as the record inspector names it: `data.GeoContexts[1].GeoTypeID`. */
  path: string;
  section: boolean;
  value?: string | number | boolean | null;
  /** For a section, the values it holds at any depth, at most 48. */
  values?: ExplorerElementLeaf[];
}

/** The Lucene queries that find records by an element of a record, read from the template of its kind a synced repository holds. */
export function elementQueries(request: ExplorerElementRequest) {
  return post<ExplorerElementAnswer>("/api/v1/delivery/explorer/element-queries", request);
}

// ---- The dimension builder (osdu/docs/reference/concepts/explorer.md, Building a dimension) ----

/** An attribute of a drafted dimension: read through the record a key names (`steps`), or collected from its own records (`collect`). */
export interface DimensionDraftAttribute {
  name: string;
  steps: string[] | null;
  collect: string | null;
}

/** A clean step of a drafted dimension: its name, and a `replace` step's pattern and replacement. */
export interface DimensionDraftCleanStep {
  step: string;
  pattern: string | null;
  with: string | null;
}

/** A dimension as the builder holds it while a person picks it: every part of a `dimensions` item. */
export interface DimensionDraft {
  name: string;
  description: string | null;
  kind: string;
  query: string | null;
  path: string | null;
  label: string[];
  unlabelled: string | null;
  attributes: DimensionDraftAttribute[];
  clean: DimensionDraftCleanStep[];
  keyColumn: string | null;
  valueColumn: string | null;
  countRecords: boolean;
  maxValues: number | null;
}

/** What the builder found about a draft: an error keeps it from loading or building, a warning does not. */
export interface DimensionDraftIssue {
  severity: "error" | "warning";
  message: string;
  /** The part it is about, as the YAML's spans name them (`path`, `attributes.Country`); null for the whole. */
  target: string | null;
  /** `template` (no synced repository holds a template) or `name` (another flow's dimension writes the table); null otherwise. */
  code: string | null;
}

/** How the platform indexes a path. */
export interface DimensionFieldWire {
  path: string;
  index: string;
  nestedPath: string | null;
}

/** A key of the dimension with how many of its records hold it, as the search counts them. */
export interface DimensionKeyCount {
  key: string;
  count: number;
}

/** The commonest keys a drafted dimension reads, which its example steps through. */
export interface DimensionKeys {
  /** The records the kind (and the query) holds. */
  total: number;
  keys: DimensionKeyCount[];
  moreKeys: boolean;
  keyField: DimensionFieldWire | null;
  keyFieldGuessed: boolean;
  notes: string[];
  /** The search service's own words when it refused the query; left out of the answer when it did not. */
  refusal?: string | null;
}

export interface DimensionKeysRequest {
  kind: string;
  query?: string | null;
  path: string;
}

/** A property a kind's template suggests as a dimension's key: a value naming another record. */
export interface DimensionKeyCandidate {
  /** The path as a dimension writes it, a list stepped into by its name. */
  path: string;
  /** The entity types the value names (`master-data--Wellbore`), or a group alone. */
  names: string[];
  /** Whether a record holds several values there: a list of ids, or a property of the items of a list. */
  repeated: boolean;
  title: string | null;
  description: string | null;
}

/** The keys a kind's template suggests, the likeliest first, or why there are none. */
export interface DimensionKeySuggestions {
  kind: string;
  template: { kind: string; version: string } | null;
  keys: DimensionKeyCandidate[];
  missing: string | null;
}

/** One key of a draft made into its row as a build makes it. */
export interface DimensionExample {
  key: string;
  label: string | null;
  labelFrom: string | null;
  problem: string | null;
  value: string | null;
  leftOut: string | null;
  note: string | null;
  attributes: DeliveryDimensionAttribute[];
  records: number | null;
  filter: string | null;
  notes: string[];
}

/** A drafted dimension written as YAML and checked, with its example row. */
export interface DimensionCompose {
  /** The item a dimension flow lists under `dimensions`, as it is copied. */
  yaml: string;
  item: DeliveryDimensionYaml;
  dimension: DeliveryDimension | null;
  blueprint: DimensionBlueprint | null;
  table: string | null;
  issues: DimensionDraftIssue[];
  valid: boolean;
  example: DimensionExample | null;
  exampleProblem: string | null;
}

/** The keys the template of `kind` a synced repository holds suggests for a dimension, the likeliest first; read from the templates alone. */
export function dimensionKeyCandidates(kind: string) {
  return get<DimensionKeySuggestions>(`/api/v1/delivery/explorer/dimension/candidates?kind=${encodeURIComponent(kind)}`);
}

/** The commonest keys of a drafted dimension's path, read through the partition's connection. */
export function dimensionKeys(partition: string | null, request: DimensionKeysRequest) {
  return post<ExplorerAnswer<DimensionKeys>>(`/api/v1/delivery/explorer/dimension/keys${partitionQuery(partition)}`, request);
}

/** A drafted dimension written as YAML and checked, with `example` made into its row. */
export function composeDimension(partition: string | null, draft: DimensionDraft, example: string | null) {
  return post<DimensionCompose>(`/api/v1/delivery/explorer/dimension/compose${partitionQuery(partition)}`, { draft, example });
}
