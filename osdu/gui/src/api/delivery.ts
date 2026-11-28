// The delivery ledger's API: what each flow delivered (records, their history, their submissions), the audit trail,
// the mappings, templates and caches the catalog holds, the mapping builder, and the interventions (release,
// redeliver, verify, read back, delete). Same conventions as endpoints.ts: one function per endpoint, pages compose them
// with TanStack Query.

import { del, get, getBlob, getText, post, put, type QueryParams } from "@/api/client";
import type { ComputeTaskAccepted, PagedResult, RunValue } from "@/api/types";
import type { PageQuery } from "@/api/endpoints";
import type { DeliverySourceTable, DeliveryTableRows } from "./mappingOutline";
import type { DeliveryActivity, DeliveryActivityFlow, DeliveryActivityListQuery } from "./delivery/activities";
import type {
  AssertionReportFormat, AssertionSourceRows, DeliveryAssertionBoard, DeliveryAssertionMatrix, DeliveryAssertionResult,
  DeliveryAssertionRun, DeliveryAssertionRunDetail,
} from "./delivery/assertions";
import type { DeliveryAttempt } from "./delivery/attempts";
import type {
  DeliveryCache, DeliveryCacheDiff, DeliveryCacheDiffQuery, DeliveryCachedItem, DeliveryCachedItemsSql,
  DeliveryCacheGap, DeliveryCacheHistoryEntry, DeliveryCachePruneRequest, DeliveryCachePruneResult,
  DeliveryCacheRetentionRequest, DeliveryCacheUse, DeliveryCacheVersion, DeliveryUpdateTag,
} from "./delivery/caches";
import type { DeliveryDimensionBlueprint } from "./delivery/dimensionBlueprints";
import type {
  DeliveryDimensionChangeQuery, DeliveryDimensionKeyQuery, DeliveryDimensionSearch, DeliveryDimensionSearchRequest,
  DeliveryDimensionTable, DeliveryDimensionTableQuery, DeliveryDimensionValueQuery, DimensionAttributeCondition,
  DimensionExportFormat, DimensionExportSet,
} from "./delivery/dimensionQueries";
import type {
  DeliveryDimensionView, DeliveryDimensionViewDetail, DeliveryDimensionViewRemoved, DeliveryDimensionViewSuggestion,
} from "./delivery/dimensionViews";
import type {
  DeliveryDimensionAttributeValue, DeliveryDimensionBoard, DeliveryDimensionBuild, DeliveryDimensionChangePage,
  DeliveryDimensionDetail, DeliveryDimensionFilter, DeliveryDimensionKeyPage, DeliveryDimensionRemoved,
  DeliveryDimensionRunBuild, DeliveryDimensionValueDetail, DeliveryDimensionValuePage,
} from "./delivery/dimensions";
import type {
  DeliveryAccessCheck, DeliveryFlowCounts, DeliveryFlowScope, DeliveryFlowStats, DeliveryInterface, DeliveryPartition,
  DeliveryProbeResult,
} from "./delivery/flows";
import type {
  DeliveryAdoptRequest, DeliveryAdoptResult, DeliveryFlowRedeliverRequest, DeliveryIssueAdoptResult,
  DeliveryIssueDetail, DeliveryIssueList, DeliveryIssueReleaseResult, DeliveryManyRecords, DeliveryRedeliverResult,
  DeliveryReleaseResult, DeliveryRerenderPreview, DeliveryRerenderResult, DeliveryRunAccepted, DeliverySyncRequest,
} from "./delivery/interventions";
import type { MappingDraft } from "./delivery/mappingDraft";
import type {
  DeliveryBuilderCache, DeliveryBuilderRepo, DeliveryMapping, DeliveryMappingComposeResult, DeliveryMappingCoverage,
  DeliveryMappingDetail, DeliveryMappingDraftRequest, DeliveryMappingFlow, DeliveryMappingOutlineResult,
  DeliveryMappingParseResult, DeliveryMappingShapeResult,
} from "./delivery/mappings";
import type {
  DeliveryTakeoverAnswer, DeliveryTakeoverRequest, OrphanDeleteAnswer, OrphanDeletePreview, OrphanDeleteRequest,
  OrphanedDocument,
} from "./delivery/orphanedDocuments";
import type { DeliveryRecordPreview, DeliveryStagedRows } from "./delivery/preview";
import type {
  DeliveryRecordFlow, DeliveryRecordHit, DeliveryRecordListQuery, DeliveryRecordLookupQuery,
} from "./delivery/recordLookup";
import type {
  DeliveryOsduRead, DeliveryRecord, DeliveryRecordChain, DeliveryRecordDetail, DeliveryRecordRef, DeliverySourceRows,
} from "./delivery/records";
import type {
  DeliveryLedgerDeleteAccepted, DeliveryLedgerDeleteRequest, DeliveryLedgerPurgeRequest, DeliveryLedgerPurgeResult,
  DeliveryRemovalAccepted, DeliveryRemovalPreview, DeliveryRemovalRequest, DeliveryTarget,
} from "./delivery/removal";
import type {
  DeliveryReversal, DeliveryReversalAccepted, DeliveryReversalDetail, DeliveryReversalItemPage, DeliveryReversalPreview,
  DeliveryReversalRequest, DeliveryReversible, ReversalSource,
} from "./delivery/reversals";
import type {
  DeliveryRetrieval, DeliveryRunLookup, DeliverySubmission, DeliverySubmissionDetail, DeliveryWorkBatch,
} from "./delivery/submissions";
import type {
  DeliveryOsduComparison, DeliveryOsduReleases, DeliveryOsduSchemaFile, DeliveryOsduSchemaIndex, DeliveryOsduSync,
  DeliveryTemplate, DeliveryTemplateDetail, DeliveryTemplateResolution, TemplateMoveReportFormat,
} from "./delivery/templates";
import type {
  DeliverySourceSql, DeliveryValueCheckRequest, DeliveryValueProof, DeliveryValueProofRequest,
} from "./delivery/valueChecks";

export type * from "./mappingOutline";

// The API's types and page links, a module per concern in delivery/, which callers import from here.
export * from "./delivery/activities";
export * from "./delivery/assertions";
export * from "./delivery/attempts";
export * from "./delivery/caches";
export * from "./delivery/dimensionBlueprints";
export * from "./delivery/dimensionQueries";
export * from "./delivery/dimensionViews";
export * from "./delivery/dimensions";
export * from "./delivery/flows";
export * from "./delivery/interventions";
export * from "./delivery/mappingDraft";
export * from "./delivery/mappings";
export * from "./delivery/orphanedDocuments";
export * from "./delivery/preview";
export * from "./delivery/recordLookup";
export * from "./delivery/records";
export * from "./delivery/removal";
export * from "./delivery/reversals";
export * from "./delivery/submissions";
export * from "./delivery/templates";
export * from "./delivery/valueChecks";

const recordApiPath = ({ flowId, deliveryKey }: DeliveryRecordRef) =>
  `/api/v1/delivery/records/${encodeURIComponent(flowId)}/${encodeURIComponent(deliveryKey)}`;

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
   * Every delivery flow's counts in one call: each as a whole, and in `partition` where the flow names it among others, with
   * why for a flow that cannot be counted. What a page showing every flow polls, in place of a call or two per flow.
   */
  flowCounts: (partition: string | null) =>
    get<DeliveryFlowCounts[]>("/api/v1/delivery/flows/stats", partition ? { partition } : {}),
  /**
   * Every interface of a source, in the order a run takes them, each with its route and counts: in `partition`, or for a
   * flow that names its partitions and no partition given, in every partition, each row naming its own.
   */
  interfaces: (pipelineId: string, partition?: string | null) =>
    get<DeliveryInterface[]>(`/api/v1/delivery/flows/${pipelineId}/interfaces`, partition ? { partition } : {}),
  records: (pipelineId: string, query: DeliveryRecordListQuery = {}) =>
    get<PagedResult<DeliveryRecord>>(`/api/v1/delivery/flows/${pipelineId}/records`, query as QueryParams),
  /** What a run id leads to: the run's execution, and what the records it delivered were sent for. */
  runLookup: (id: string) => get<DeliveryRunLookup>("/api/v1/delivery/runs/lookup", { id }),
  /**
   * Every record the listing's filter matches (paging aside) as an Excel workbook: at most the listing's count limit, a
   * broader filter refused as too broad.
   */
  exportRecords: (pipelineId: string, query: Omit<DeliveryRecordListQuery, keyof PageQuery>) =>
    getBlob(`/api/v1/delivery/flows/${pipelineId}/records/export`, query as QueryParams),
  /** Every record the lookup reaches (paging aside) as an Excel workbook, each with the flow that delivers it. */
  exportLookup: (query: Omit<DeliveryRecordLookupQuery, keyof PageQuery>) =>
    getBlob("/api/v1/delivery/records/export", query as QueryParams),
  /**
   * A record by what an operator holds, across every flow: a delivery key lands on the record of every flow reading
   * that row; anything else is a prefix over the OSDU id, the source key, the label and the ingestion file name, or
   * with `mode: "contains"` the same values matched anywhere. With no search term, the records the ledger last took in
   * or sent, newest first. A prefix and the listing are the ledger's indexed reads, so they answer at production volume
   * and count no further than the candidate bound; a contains term reads what the other filters leave.
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
  /** Tests' results whole, by the ids the board gave them (at most 100 a read). */
  assertionResults: (resultIds: readonly number[]) =>
    get<DeliveryAssertionResult[]>("/api/v1/delivery/assertion-results", { ids: resultIds.join(",") }),
  /**
   * The staged rows one check of a run read, as T-SQL over its delivery flow's tables: the rows it lists, or every row of its
   * scope. Written by the control plane from what the result kept; no row is read, and nothing is run.
   */
  assertionSourceSql: (assertionRunId: number, request: { test: string; assertion: number; rows: AssertionSourceRows }) =>
    post<DeliverySourceSql>(`/api/v1/delivery/assertion-runs/${assertionRunId}/source-sql`, request),
  /** An assertion set's report as the text of the format asked for: Markdown, JSON or JUnit XML. */
  assertionSetReport: (set: string, format: Exclude<AssertionReportFormat, "html">) =>
    getText(`/api/v1/delivery/assertion-sets/${encodeURIComponent(set)}/report?format=${format}`),
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
  /**
   * The report on moving from one version of a kind to the other, as the text of the format asked for, read against what each
   * synced mapping of the kind writes: which changes are significant, what each mapping has to change, and what no mapping
   * uses. Nothing is kept on the control plane.
   */
  osduCompareReport: (from: { release: string; kind: string }, to: { release: string; kind: string }, format: TemplateMoveReportFormat) =>
    getText(`/api/v1/delivery/templates/osdu/compare/report?${new URLSearchParams({
      fromRelease: from.release,
      fromKind: from.kind,
      toRelease: to.release,
      toKind: to.kind,
      format,
    }).toString()}`),
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
  /** What a mapping document does, laid out for a person to read, and what is wrong with it; nothing is rendered and no data is read. */
  mappingOutline: (yaml: string, path: string | null, repoId?: string | null) =>
    post<DeliveryMappingOutlineResult>("/api/v1/delivery/mapping-builder/outline", { yaml, path, repoId: repoId ?? null }),
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
  /** The T-SQL that reads in SSMS the first page `cachedItems` lists of one type, with the same search and page size. Nothing is run. */
  cachedItemsSql: (query: { scope: string; type: string; search?: string; version?: string; pageSize?: number }) =>
    get<DeliveryCachedItemsSql>("/api/v1/delivery/cache/items/sql", query as unknown as QueryParams),
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
  /**
   * Sets how many versions of the partition's cache keep their records, as the signed-in admin, and applies it at once: what
   * it pruned comes back. `cachePrunePreview` with `versionsKept` says what it would prune first.
   */
  cacheKeepVersions: (request: DeliveryCacheRetentionRequest) =>
    put<DeliveryCachePruneResult>("/api/v1/delivery/cache/retention", request),
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
   * nothing moves: the given keys, or every one with `all`, which has to be said. A record that kept its rendered document
   * goes back to pending; the others are planned again from their rows by the next run. With `run`, a deliver run of the
   * flow is queued at once.
   */
  releaseFlow: (pipelineId: string, selection: { keys: string[] } | { all: true }, scope?: DeliveryFlowScope, run = false) =>
    post<DeliveryReleaseResult>(flowPath(pipelineId, "/release", scope), { ...selection, run }),
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
  /** Whether the flow can restrict its records under each access restriction of its partition, and lift it again. */
  checkAccess: (pipelineId: string, scope?: DeliveryFlowScope) =>
    post<DeliveryAccessCheck>(flowPath(pipelineId, "/access-check", scope)),
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
   * Asks one record held because OSDU already holds a record at the id its key makes to adopt it: the next run takes it
   * over. With `run`, a deliver run scoped to the record is queued at once.
   */
  adopt: (record: DeliveryRecordRef, run = false) =>
    post<DeliveryAdoptResult>(`${recordApiPath(record)}/adopt`, run ? { run: true } : undefined),
  /** Asks every record an issue keeps held for an id OSDU holds to adopt it, the partition typed back as `confirm`. */
  adoptIssue: (pipelineId: string, issue: string, confirm: string, run: boolean, scope?: DeliveryFlowScope) =>
    post<DeliveryIssueAdoptResult>(flowPath(pipelineId, `/issues/${encodeURIComponent(issue)}/adopt`, scope), { confirm, run }),
  /**
   * Asks a flow's records held for an id OSDU holds to adopt it: the ones the request names or matches, or every one, of
   * every interface when the scope names none. The partition is typed back as `confirm`; with `run`, a deliver run is queued.
   */
  adoptFlow: (pipelineId: string, request: DeliveryAdoptRequest, scope?: DeliveryFlowScope) =>
    post<DeliveryAdoptResult>(flowPath(pipelineId, "/adopt", scope), request),
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
  preview: (pipelineId: string, request: { key?: string | null; values?: Record<string, RunValue>; schemaCheck?: boolean }, scope?: DeliveryFlowScope) =>
    post<DeliveryRecordPreview>(flowPath(pipelineId, "/preview", scope), request),
  /**
   * A page of the rows staged in an interface's scope, nothing of them rendered, resuming after `after`, narrowed to the rows
   * whose key, label or file hold `search`. Answered at once.
   */
  /** The T-SQL that shows what an attribute reads, as the flow's source tables hold it now. No row is read, and nothing is run. */
  sourceSql: (
    pipelineId: string,
    request: { target?: string; values?: Record<string, RunValue>; maxRows?: number; keys?: string[] },
    scope?: DeliveryFlowScope,
  ) => post<DeliverySourceSql>(flowPath(pipelineId, "/source-sql", scope), request),
  /**
   * The proof of one finding of a value check: the statements that show it from the flow's source and the partition's cache,
   * each read-only and written by the control plane, with the step `run` names run. Answered at once.
   */
  valueProof: (pipelineId: string, request: DeliveryValueProofRequest, scope?: DeliveryFlowScope) =>
    post<DeliveryValueProof>(flowPath(pipelineId, "/value-proof", scope), request),
  stagedRows: (
    pipelineId: string,
    request: { values?: Record<string, RunValue>; search?: string; after?: string | null; limit?: number },
    scope?: DeliveryFlowScope,
  ) => post<DeliveryStagedRows>(flowPath(pipelineId, "/staged-rows", scope), request),
  /**
   * The first rows of the record table, or of the child dataset named, every column of it, in the scope `values` name; with a
   * `search`, the first of those one of whose values holds it, as the rows show it, and their count. Answered at once.
   */
  tableRows: (
    pipelineId: string,
    request: { values?: Record<string, RunValue>; dataset?: string; limit?: number; search?: string },
    scope?: DeliveryFlowScope,
  ) => post<DeliveryTableRows>(flowPath(pipelineId, "/table-rows", scope), request),
  /** Previews this record, rendered from its current source row as a delivery would render it now. Answered at once. */
  previewRecord: (record: DeliveryRecordRef) => post<DeliveryRecordPreview>(`${recordApiPath(record)}/preview`),
  /** The tables an interface of a delivery flow reads, record table first, with the columns the catalog holds of each. */
  sourceTables: (pipelineId: string, scope?: DeliveryFlowScope) =>
    get<DeliverySourceTable[]>(flowPath(pipelineId, "/source-tables", scope)),
  /** The interfaces of the delivery flows that render with a mapping, in every partition: what its values are checked against. */
  mappingFlows: (mappingId: string) => get<DeliveryMappingFlow[]>(`/api/v1/delivery/mappings/${mappingId}/flows`),
  /**
   * Queues a value check of one interface's rows on a node: the rows that will not give the variables named (every
   * variable when none is named) the values the template expects, with the reasons and example records. Nothing is sent.
   */
  checkValues: (pipelineId: string, request: DeliveryValueCheckRequest, scope?: DeliveryFlowScope) =>
    post<ComputeTaskAccepted>(flowPath(pipelineId, "/check-values", scope), request),
  /** Queues the same check on a node to write every issue it finds to an Excel workbook, which the task's result carries. */
  exportValueIssues: (pipelineId: string, request: DeliveryValueCheckRequest, scope?: DeliveryFlowScope) =>
    post<ComputeTaskAccepted>(flowPath(pipelineId, "/check-values/export", scope), request),
  /** Reads any OSDU record by id, through the flow's route and credentials. Answered at once. */
  readOsdu: (pipelineId: string, targetId: string, scope?: DeliveryFlowScope, version?: number) =>
    post<DeliveryOsduRead>(flowPath(pipelineId, "/osdu/read", scope), version === undefined ? { targetId } : { targetId, version }),
  /** Where the flow's records live in the scope's partition, and which call each removal scope makes against them. */
  target: (pipelineId: string, scope?: DeliveryFlowScope) =>
    get<DeliveryTarget>(`/api/v1/delivery/flows/${pipelineId}/target`, scopeQuery(scope)),
  /** What a removal would act on, without removing anything: the confirmation's contents. */
  previewRemoval: (pipelineId: string, request: DeliveryRemovalRequest, scope?: DeliveryFlowScope) =>
    post<DeliveryRemovalPreview>(flowPath(pipelineId, "/records/remove/preview", scope), request),
  /** Queues the remove run of the selected records, or of every record the filter matches: a run of the flow, listed with its runs. */
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
  /** The missing documents with a ledger in `partition`, else the workbench's, else in any partition, each with who can take it over. */
  orphanedDocuments: (partition?: string | null) =>
    get<OrphanedDocument[]>("/api/v1/delivery/ledgers/orphaned", partition ? { partition } : {}),
  /** Hands a missing document's ledgers to a declared flow, as the caller: each ledger moves at once, and the answer says how. */
  takeOverLedgers: (request: DeliveryTakeoverRequest) =>
    post<DeliveryTakeoverAnswer>("/api/v1/delivery/ledgers/orphaned/takeover", request),
  /** What deleting a missing document's ledgers in a partition would do: each ledger, and why it stays when it cannot go. Nothing is written. */
  previewOrphanedDelete: (request: OrphanDeleteRequest) =>
    post<OrphanDeletePreview>("/api/v1/delivery/ledgers/orphaned/delete/preview", request),
  /** Deletes what a missing document left behind in a partition, as the caller, once `confirm` names that partition. Nothing is asked of OSDU. */
  deleteOrphanedLedgers: (request: OrphanDeleteRequest) =>
    post<OrphanDeleteAnswer>("/api/v1/delivery/ledgers/orphaned/delete", request),
};
