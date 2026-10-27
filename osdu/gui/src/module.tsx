import {
  Activity, Blocks, BookCheck, ClipboardList, CloudDownload, Compass, DatabaseZap, FileCode2, Layers, LayoutTemplate, ListChecks, ListX,
  Network, PackageCheck, PackageSearch, ScrollText, Settings, Shapes, ShieldCheck, Telescope, TextSearch, Unlink,
} from "lucide-react";
import type { RunSummary } from "@/api/types";
import type { Column } from "@/components/DataTable";
import { TruncatedText } from "@/components/TruncatedText";
import { lazyRoute } from "@/lib/lazyRoute";
import type { FlowKindContribution, GuiModule, RunDetailContribution } from "@/modules/registry";
import { Deferred } from "./features/delivery/Deferred";
import { AssertionRunActions, AssertionRunCounts } from "./features/delivery/assertions/AssertionRunHeader";
import { AssertionTriggerFields } from "./features/delivery/assertions/AssertionTriggerFields";
import { CacheRunActions, DeliveryRunActions, DeliveryRunCounts, DeliveryRunMeta } from "./features/delivery/DeliveryRunHeader";
import { DimensionRunActions, DimensionRunCounts } from "./features/delivery/dimensions/DimensionRunHeader";
import { DimensionTriggerFields } from "./features/delivery/dimensions/DimensionTriggerFields";
import { InventoryRunActions, InventoryRunCounts } from "./features/delivery/inventories/InventoryRunHeader";
import { InventoryTriggerFields } from "./features/delivery/inventories/InventoryTriggerFields";
import { RemovalRunActions, RemovalRunCounts } from "./features/delivery/removals/RemovalRunHeader";
import { RemovalTriggerFields } from "./features/delivery/removals/RemovalTriggerFields";
import { DeliveryTriggerFields } from "./features/delivery/DeliveryTriggerFields";
import { activePartition } from "./features/delivery/activePartition";
import { PartitionSwitcher } from "./features/delivery/PartitionSwitcher";
import { shortId } from "./features/delivery/idTail";

// The OSDU Delivery module: its pages, its navigation, the panels of the delivery, retrieval, cache, assertion, dimension, inventory and removal kinds on SQLFlow's
// pipeline, run and trigger pages, the delivery records in search, and the product's branding. Everything heavy (the
// pages, the panels, anything with the code editor) loads with the surface that shows it.

const DeliveryOverviewPage = lazyRoute("DeliveryOverviewPage", () => import("./features/delivery/DeliveryOverviewPage"));
const DeliveryHomeSection = lazyRoute(
  "DeliveryHomeSection",
  () => import("./features/delivery/DeliveryHomeSection").then((loaded) => ({ default: loaded.DeliveryHomeSection })),
);
const DeliveryRecordsPage = lazyRoute("DeliveryRecordsPage", () => import("./features/delivery/DeliveryRecordsPage"));
const DeliveryRecordPage = lazyRoute("DeliveryRecordPage", () => import("./features/delivery/DeliveryRecordPage"));
const OsduRecordWindowPage = lazyRoute("OsduRecordWindowPage", () => import("./features/delivery/OsduRecordWindowPage"));
const DeliverySubmissionPage = lazyRoute("DeliverySubmissionPage", () => import("./features/delivery/DeliverySubmissionPage"));
const DeliveryActivityPage = lazyRoute("DeliveryActivityPage", () => import("./features/delivery/DeliveryActivityPage"));
const OrphanedLedgersPage = lazyRoute("OrphanedLedgersPage", () => import("./features/delivery/OrphanedLedgersPage"));
const DeliveryDocumentsPage = lazyRoute("DeliveryDocumentsPage", () => import("./features/delivery/DeliveryDocumentsPage"));
const TemplatesPage = lazyRoute("TemplatesPage", () => import("./features/delivery/TemplatesPage"));
const OrderPage = lazyRoute("OrderPage", () => import("./features/delivery/order/OrderPage"));
const MappingBuilderPage = lazyRoute("MappingBuilderPage", () => import("./features/delivery/MappingBuilderPage"));
const DeliveryCachePage = lazyRoute("DeliveryCachePage", () => import("./features/delivery/DeliveryCachePage"));
const DeliveryPartitionsPage = lazyRoute("DeliveryPartitionsPage", () => import("./features/delivery/DeliveryPartitionsPage"));
const DeliveryAssertionsPage = lazyRoute("DeliveryAssertionsPage", () => import("./features/delivery/assertions/DeliveryAssertionsPage"));
const AssertionReportPage = lazyRoute("AssertionReportPage", () => import("./features/delivery/assertions/AssertionReportPage"));
const DeliveryDimensionsPage = lazyRoute("DeliveryDimensionsPage", () => import("./features/delivery/dimensions/DeliveryDimensionsPage"));
const DeliveryInventoriesPage = lazyRoute("DeliveryInventoriesPage", () => import("./features/delivery/inventories/DeliveryInventoriesPage"));
const DeliveryRemovalsPage = lazyRoute("DeliveryRemovalsPage", () => import("./features/delivery/removals/DeliveryRemovalsPage"));
const ExplorerPage = lazyRoute("ExplorerPage", () => import("./features/delivery/explorer/ExplorerPage"));
const SearchTermsPage = lazyRoute("SearchTermsPage", () => import("./features/delivery/searchTerms/SearchTermsPage"));

const DeliveryFlowPanel = lazyRoute(
  "DeliveryFlowPanel",
  () => import("./features/delivery/DeliveryFlowPanel").then((loaded) => ({ default: loaded.DeliveryFlowPanel })),
);
const DeliveryIssuesPanel = lazyRoute(
  "DeliveryIssuesPanel",
  () => import("./features/delivery/DeliveryIssuesPanel").then((loaded) => ({ default: loaded.DeliveryIssuesPanel })),
);
const DeliveryTargetFacts = lazyRoute(
  "DeliveryTargetFacts",
  () => import("./features/delivery/DeliveryTargetFacts").then((loaded) => ({ default: loaded.DeliveryTargetFacts })),
);
const DeliveryPreviewPanel = lazyRoute(
  "DeliveryPreviewPanel",
  () => import("./features/delivery/DeliveryPreviewPanel").then((loaded) => ({ default: loaded.DeliveryPreviewPanel })),
);
const RetrievalFlowPanel = lazyRoute(
  "RetrievalFlowPanel",
  () => import("./features/delivery/RetrievalFlowPanel").then((loaded) => ({ default: loaded.RetrievalFlowPanel })),
);
const DeliveryCacheFlowVersions = lazyRoute(
  "DeliveryCacheFlowVersions",
  () => import("./features/delivery/DeliveryCacheFlowVersions").then((loaded) => ({ default: loaded.DeliveryCacheFlowVersions })),
);
const AssertionTestsPanel = lazyRoute(
  "AssertionTestsPanel",
  () => import("./features/delivery/assertions/AssertionFlowPanels").then((loaded) => ({ default: loaded.AssertionTestsPanel })),
);
const AssertionHistoryPanel = lazyRoute(
  "AssertionHistoryPanel",
  () => import("./features/delivery/assertions/AssertionFlowPanels").then((loaded) => ({ default: loaded.AssertionHistoryPanel })),
);
const AssertionReportsPanel = lazyRoute(
  "AssertionReportsPanel",
  () => import("./features/delivery/assertions/AssertionFlowPanels").then((loaded) => ({ default: loaded.AssertionReportsPanel })),
);
const DimensionsPanel = lazyRoute(
  "DimensionsPanel",
  () => import("./features/delivery/dimensions/DimensionFlowPanels").then((loaded) => ({ default: loaded.DimensionsPanel })),
);
const InventoriesPanel = lazyRoute(
  "InventoriesPanel",
  () => import("./features/delivery/inventories/InventoryFlowPanels").then((loaded) => ({ default: loaded.InventoriesPanel })),
);
const RemovalsPanel = lazyRoute(
  "RemovalsPanel",
  () => import("./features/delivery/removals/RemovalFlowPanels").then((loaded) => ({ default: loaded.RemovalsPanel })),
);
const DeliveryDeletionsPanel = lazyRoute(
  "DeliveryDeletionsPanel",
  () => import("./features/delivery/removals/DeliveryDeletionsPanel").then((loaded) => ({ default: loaded.DeliveryDeletionsPanel })),
);
const DeliveryRunCard = lazyRoute("DeliveryRunCard", () => import("./features/delivery/DeliveryRunCard"));
const RecordSearchHits = lazyRoute(
  "RecordSearchHits",
  () => import("./features/delivery/RecordSearchHits").then((loaded) => ({ default: loaded.RecordSearchHits })),
);

/**
 * The key census of every document this module adds, so the YAML editor documents, colours and checks them as it does
 * SQLFlow's own flows: the delivery, retrieval, cache, assertion, dimension, inventory and removal flows by their flowType, the mapping and the dictionary
 * by their documentType. Each file loads when the editor first starts, not with the page.
 */
const census: GuiModule["census"] = [
  { flowType: "delivery", load: () => import("../../docs/census/keys.delivery.json?raw").then((file) => file.default) },
  { flowType: "retrieval", load: () => import("../../docs/census/keys.retrieval.json?raw").then((file) => file.default) },
  { flowType: "cache", load: () => import("../../docs/census/keys.cache.json?raw").then((file) => file.default) },
  { flowType: "assertion", load: () => import("../../docs/census/keys.assertion.json?raw").then((file) => file.default) },
  { flowType: "dimension", load: () => import("../../docs/census/keys.dimension.json?raw").then((file) => file.default) },
  { flowType: "inventory", load: () => import("../../docs/census/keys.inventory.json?raw").then((file) => file.default) },
  { flowType: "removal", load: () => import("../../docs/census/keys.removal.json?raw").then((file) => file.default) },
  { documentType: "mapping", load: () => import("../../docs/census/keys.mapping.json?raw").then((file) => file.default) },
  { documentType: "dictionary", load: () => import("../../docs/census/keys.dictionary.json?raw").then((file) => file.default) },
];

/**
 * SQLFlow's pipeline tabs that describe its own flows' data (view columns, processed files), not these kinds, and the
 * definition, which for these kinds is the YAML tab's document again as JSON.
 */
const HIDDEN_PIPELINE_TABS = ["transforms", "files", "definition"];

/** SQLFlow's run tabs that describe its own runs' work (files, SQL statements, keys, assertions, health metrics). */
const HIDDEN_RUN_TABS = ["files", "statements", "surrogate-keys", "assertions", "health-metrics"];

const runColumns: Column<RunSummary>[] = [
  { id: "operation", header: "Operation", render: (row) => <span className="font-mono text-[12px]">{row.operation ?? "-"}</span> },
  { id: "requested-by", header: "Requested by", render: (row) => <TruncatedText text={row.requestedBy} mono maxWidth={200} /> },
];

function runPanels(extra: Omit<RunDetailContribution, "card" | "hiddenTabs">): RunDetailContribution {
  return {
    ...extra,
    card: (run) => <Deferred placeholder={false}><DeliveryRunCard run={run} /></Deferred>,
    hiddenTabs: HIDDEN_RUN_TABS,
  };
}

// The module's kinds wear the icons of the pages they feed (Delivery, Cache, Assertions), in colours of their own: a delivery
// flow in the one tone SQLFlow leaves to modules, a cache flow in orange, so a pre, ingestion, cache and delivery flow of
// one source each read differently; a retrieval flow brings records in, as SQLFlow's blue kinds do, an assertion flow
// checks, as SQLFlow's neutral kinds do, and a dimension flow shapes what OSDU holds into tables of its own, as SQLFlow's
// violet kinds shape theirs.
const deliveryKind: FlowKindContribution = {
  kind: "delivery",
  identity: { label: "Delivery", icon: PackageCheck, tone: "magenta" },
  pipelineTabs: [
    {
      value: "delivery",
      label: "Delivery",
      testId: "pipeline-tab-delivery",
      render: (pipeline) => (
        <Deferred><DeliveryFlowPanel pipelineId={pipeline.id} flowName={pipeline.name} section="overview" /></Deferred>
      ),
    },
    {
      value: "records",
      label: "Records",
      testId: "pipeline-tab-records",
      render: (pipeline) => (
        <Deferred><DeliveryFlowPanel pipelineId={pipeline.id} flowName={pipeline.name} section="records" /></Deferred>
      ),
    },
    {
      // The blocked records grouped by the issue that keeps them blocked, each issue checked and released as one.
      value: "issues",
      label: "Issues",
      testId: "pipeline-tab-issues",
      render: (pipeline) => (
        <Deferred><DeliveryIssuesPanel pipelineId={pipeline.id} flowName={pipeline.name} /></Deferred>
      ),
    },
    {
      value: "submissions",
      label: "Submissions",
      testId: "pipeline-tab-submissions",
      render: (pipeline) => (
        <Deferred><DeliveryFlowPanel pipelineId={pipeline.id} flowName={pipeline.name} section="submissions" /></Deferred>
      ),
    },
    {
      // One record rendered as a delivery would render it, and sent nowhere.
      value: "preview",
      label: "Preview",
      testId: "pipeline-tab-preview",
      render: (pipeline) => (
        <Deferred><DeliveryPreviewPanel pipelineId={pipeline.id} flowName={pipeline.name} /></Deferred>
      ),
    },
    {
      // The keys removal flows found the flow's source no longer holds: the ledger's deletions, whichever removal flow found them.
      value: "deletions",
      label: "Deletions",
      testId: "pipeline-tab-deletions",
      render: (pipeline) => (
        <Deferred><DeliveryDeletionsPanel pipelineId={pipeline.id} flowName={pipeline.name} /></Deferred>
      ),
    },
  ],
  defaultPipelineTab: "delivery",
  hiddenPipelineTabs: HIDDEN_PIPELINE_TABS,
  // A delivery source's target is an endpoint, not a server the catalog knows, which it names only as "file".
  hiddenPipelineFacts: ["targetServer"],
  pipelineFacts: (pipeline) => <Deferred placeholder={false}><DeliveryTargetFacts pipelineId={pipeline.id} /></Deferred>,
  runColumns,
  run: runPanels({
    headerActions: (run) => <DeliveryRunActions run={run} />,
    headerMeta: (run) => <DeliveryRunMeta run={run} />,
    headerDetails: (run) => <DeliveryRunCounts run={run} />,
  }),
  trigger: { Fields: DeliveryTriggerFields },
};

const retrievalKind: FlowKindContribution = {
  kind: "retrieval",
  identity: { label: "Retrieval", icon: CloudDownload, tone: "blue" },
  pipelineTabs: [
    {
      value: "retrievals",
      label: "Retrievals",
      testId: "pipeline-tab-retrievals",
      render: (pipeline) => <Deferred><RetrievalFlowPanel pipelineId={pipeline.id} /></Deferred>,
    },
  ],
  defaultPipelineTab: "retrievals",
  hiddenPipelineTabs: HIDDEN_PIPELINE_TABS,
  runColumns,
  run: runPanels({}),
  trigger: { Fields: DeliveryTriggerFields },
};

const cacheKind: FlowKindContribution = {
  kind: "cache",
  identity: { label: "Cache", icon: DatabaseZap, tone: "orange" },
  pipelineTabs: [
    {
      value: "versions",
      label: "Cache versions",
      testId: "pipeline-tab-versions",
      render: (pipeline) => <Deferred><DeliveryCacheFlowVersions flowName={pipeline.name} /></Deferred>,
    },
  ],
  defaultPipelineTab: "versions",
  hiddenPipelineTabs: HIDDEN_PIPELINE_TABS,
  runColumns,
  run: runPanels({
    headerActions: (run) => <CacheRunActions run={run} />,
  }),
  trigger: { Fields: DeliveryTriggerFields },
};

const assertionKind: FlowKindContribution = {
  kind: "assertion",
  identity: { label: "Assertion", icon: ListChecks, tone: "neutral" },
  pipelineTabs: [
    {
      value: "tests",
      label: "Assertions",
      testId: "pipeline-tab-tests",
      render: (pipeline) => <Deferred><AssertionTestsPanel pipelineId={pipeline.id} /></Deferred>,
    },
    {
      // The flow's tests against its recent runs.
      value: "history",
      label: "History",
      testId: "pipeline-tab-history",
      render: (pipeline) => <Deferred><AssertionHistoryPanel pipelineId={pipeline.id} /></Deferred>,
    },
    {
      value: "reports",
      label: "Reports",
      testId: "pipeline-tab-reports",
      render: (pipeline) => <Deferred><AssertionReportsPanel pipelineId={pipeline.id} /></Deferred>,
    },
  ],
  defaultPipelineTab: "tests",
  hiddenPipelineTabs: HIDDEN_PIPELINE_TABS,
  runColumns,
  run: runPanels({
    headerActions: (run) => <AssertionRunActions run={run} />,
    headerDetails: (run) => <AssertionRunCounts run={run} />,
  }),
  trigger: { Fields: AssertionTriggerFields },
};

const dimensionKind: FlowKindContribution = {
  kind: "dimension",
  identity: { label: "Dimension", icon: Shapes, tone: "violet" },
  pipelineTabs: [
    {
      value: "dimensions",
      label: "Dimensions",
      testId: "pipeline-tab-dimensions",
      render: (pipeline) => <Deferred><DimensionsPanel pipelineId={pipeline.id} /></Deferred>,
    },
  ],
  defaultPipelineTab: "dimensions",
  hiddenPipelineTabs: HIDDEN_PIPELINE_TABS,
  runColumns,
  run: runPanels({
    headerActions: () => <DimensionRunActions />,
    headerDetails: (run) => <DimensionRunCounts run={run} />,
  }),
  trigger: { Fields: DimensionTriggerFields },
};

// An inventory flow only reads OSDU and sets what it holds against the ledgers, so it wears the neutral tone the checking
// kinds wear, with the icon of the page its inventories are read on.
const inventoryKind: FlowKindContribution = {
  kind: "inventory",
  identity: { label: "Inventory", icon: ClipboardList, tone: "neutral" },
  pipelineTabs: [
    {
      value: "inventories",
      label: "Inventories",
      testId: "pipeline-tab-inventories",
      render: (pipeline) => <Deferred><InventoriesPanel pipelineId={pipeline.id} /></Deferred>,
    },
  ],
  defaultPipelineTab: "inventories",
  hiddenPipelineTabs: HIDDEN_PIPELINE_TABS,
  runColumns,
  run: runPanels({
    headerActions: () => <InventoryRunActions />,
    headerDetails: (run) => <InventoryRunCounts run={run} />,
  }),
  trigger: { Fields: InventoryTriggerFields },
};

// A removal flow takes records out of OSDU through the delivery flow's own removal path and into its ledger, so it wears the
// delivery flow's tone, with the glyph of keys no longer listed.
const removalKind: FlowKindContribution = {
  kind: "removal",
  identity: { label: "Removal", icon: ListX, tone: "magenta" },
  pipelineTabs: [
    {
      value: "removals",
      label: "Removals",
      testId: "pipeline-tab-removals",
      render: (pipeline) => <Deferred><RemovalsPanel pipelineId={pipeline.id} /></Deferred>,
    },
  ],
  defaultPipelineTab: "removals",
  hiddenPipelineTabs: HIDDEN_PIPELINE_TABS,
  runColumns,
  run: runPanels({
    headerActions: (run) => <RemovalRunActions run={run} />,
    headerDetails: (run) => <RemovalRunCounts run={run} />,
  }),
  trigger: { Fields: RemovalTriggerFields },
};

export const osduDeliveryModule: GuiModule = {
  id: "osdu-delivery",
  routes: [
    { path: "/delivery", component: DeliveryOverviewPage },
    { path: "/delivery/records", component: DeliveryRecordsPage },
    { path: "/delivery/records/:flowId/:key", component: DeliveryRecordPage },
    // A record's OSDU explorer alone, in a window of its own that the record page's OSDU tab opens.
    { path: "/delivery/records/:flowId/:key/osdu", component: OsduRecordWindowPage, frame: "window" },
    { path: "/delivery/submissions/:submissionId", component: DeliverySubmissionPage },
    { path: "/delivery/activity", component: DeliveryActivityPage },
    { path: "/delivery/ledgers/orphaned", component: OrphanedLedgersPage },
    { path: "/delivery/documents", component: DeliveryDocumentsPage },
    { path: "/delivery/templates", component: TemplatesPage },
    { path: "/delivery/order", component: OrderPage },
    { path: "/delivery/mappings/build", component: MappingBuilderPage },
    { path: "/delivery/cache", component: DeliveryCachePage },
    { path: "/delivery/partitions", component: DeliveryPartitionsPage },
    { path: "/delivery/assertions", component: DeliveryAssertionsPage },
    { path: "/delivery/assertions/runs/:assertionRunId", component: AssertionReportPage },
    { path: "/delivery/dimensions", component: DeliveryDimensionsPage },
    { path: "/delivery/inventories", component: DeliveryInventoriesPage },
    { path: "/delivery/removals", component: DeliveryRemovalsPage },
    // What OSDU holds, read live from it through a flow's credentials, under the operate policy, which every signed-in
    // user holds (only admin checks a scope).
    { path: "/delivery/explorer", component: ExplorerPage, requiredScope: "operate" },
    { path: "/delivery/search-terms", component: SearchTermsPage },
  ],
  // The whole menu, SQLFlow's pages and this product's side by side, grouped by what a person does there rather than by
  // who added the page (decision 0034). Operate is the day's work: asking, the runs, and the health of what runs them.
  // Ledger is what was delivered, flow by flow and record by record, and who did what. OSDU is what OSDU holds, read
  // live, the checks of it, and its partitions. Build is what is delivered, how and from where, all of it kept in the
  // repositories: the flows, what a delivered record is built from, and the sources they read. Explore is the data the
  // flows read and write and how it connects. Admin is who and what may act, and Settings is the person's own. A group of
  // more than one sort of page lists each sort under a section. The mapping builder is reached from Mappings, which owns
  // its path.
  menu: {
    groups: [
      {
        id: "operate",
        label: "Operate",
        icon: Activity,
        entries: ["/", "/chat", "/runs", "/schedules/timeline"],
        sections: [{ id: "health", label: "Health", entries: ["/datastreams", "/insights", "/nodes", "/settings/maintenance"] }],
      },
      {
        id: "ledger",
        label: "Ledger",
        icon: BookCheck,
        entries: ["/delivery", "/delivery/records", "/delivery/removals", "/delivery/activity", "/delivery/ledgers/orphaned"],
      },
      {
        id: "osdu",
        label: "OSDU",
        icon: Layers,
        entries: [
          "/delivery/explorer",
          "/delivery/search-terms",
          "/delivery/assertions",
          "/delivery/inventories",
          "/delivery/dimensions",
          "/delivery/partitions",
        ],
      },
      {
        id: "build",
        label: "Build",
        icon: Blocks,
        sections: [
          { id: "flows", label: "Flows", entries: ["/repos", "/pipelines", "/schedules"] },
          { id: "delivery", label: "Delivery", entries: ["/delivery/documents", "/delivery/templates", "/delivery/cache", "/delivery/order"] },
          { id: "sources", label: "Sources", entries: ["/datasources", "/integrations", "/discover", "/key-detection"] },
        ],
      },
      { id: "explore", label: "Explore", icon: Compass, entries: ["/catalog", "/lineage", "/subscribers", "/schema-changes", "/search"] },
      { id: "admin", label: "Admin", icon: ShieldCheck, requiresScope: "admin", entries: ["/users", "/external-triggers", "/semantic-layer"] },
      { id: "settings", label: "Settings", icon: Settings, bottom: true, entries: ["/settings/tokens", "/settings/notifications"] },
    ],
  },
  navItems: [
    { label: "Delivery", to: "/delivery", icon: PackageCheck, testId: "nav-delivery",
        description: "Every delivery flow as a card with its counts in the picked partition: delivered, pending, held and failed. Open a card for that flow, or look up a record by its key." },
    { label: "Records", to: "/delivery/records", icon: PackageSearch, testId: "nav-delivery-records",
        description: "The records the ledger took in or sent, newest first. Find one by well name, key, OSDU id or source file across every flow; open it for its timeline, render and OSDU copy." },
    { label: "Removals", to: "/delivery/removals", icon: ListX, testId: "nav-delivery-removals",
        description: "Every removal of the partition: the ledger it writes to, its deletions by state and its last run. Open one to see its deletions, approve what its guard held, or run it." },
    { label: "Audit trail", to: "/delivery/activity", icon: ScrollText, testId: "nav-delivery-activity",
        description: "Every run and intervention on the delivery flows, newest first: who did what, when, and how it ended. Filter by actor, flow, action or outcome; open one for its log." },
    { label: "Orphaned ledgers", to: "/delivery/ledgers/orphaned", icon: Unlink, testId: "nav-delivery-orphaned-ledgers",
        description: "Ledgers whose flow no repository declares any more but which still keep records and history; see what each left behind and hand it to the flow that replaced it." },
    { label: "Explorer", to: "/delivery/explorer", icon: Telescope, testId: "nav-delivery-explorer",
        description: "What OSDU holds in the picked partition, read live: browse by type, search by id, text or query, and open a record with its versions and links. Nothing here is the ledger's." },
    { label: "Assertions", to: "/delivery/assertions", icon: ListChecks, testId: "nav-delivery-assertions",
        description: "The assertion flows' checks of what OSDU holds after delivery, what needs a look first; filter by outcome, type or tag, run all or the ones picked, and open their reports." },
    { label: "Inventories", to: "/delivery/inventories", icon: ClipboardList, testId: "nav-delivery-inventories",
        description: "Every id an OSDU kind holds, set against the partition's ledgers, each with a finding such as orphan or stale. Open one to filter its ids, find duplicates, export or remove them." },
    { label: "Dimensions", to: "/delivery/dimensions", icon: Shapes, testId: "nav-delivery-dimensions",
        description: "The distinct keys the dimension flows find in OSDU records, each with the readable value it stands for and the search that finds its records; open one, or build a search." },
    {
      label: "Mappings",
      to: "/delivery/documents",
      icon: FileCode2,
      testId: "nav-delivery-documents",
      description: "The mappings that turn source rows into OSDU records, as the last sync found them. Open one for its properties, YAML and record shape, or write a new one in the builder.",
      owns: ["/delivery/mappings/build"],
    },
    { label: "Templates", to: "/delivery/templates", icon: LayoutTemplate, testId: "nav-delivery-templates",
        description: "The record schemas the repositories keep in their template folders, which the mappings fill; Browse OSDU reads the published schemas and downloads a kind as a template." },
    { label: "Cache", to: "/delivery/cache", icon: DatabaseZap, testId: "nav-delivery-cache",
        description: "The partition's cache of reference and master data that delivered records are built from: browse its records and versions, and approve or reject a change before records carry it." },
    { label: "Order", to: "/delivery/order", icon: Network, testId: "nav-delivery-order",
        description: "The order records reach OSDU in, from what each kind refers to: the delivery schedules drawn against it, a partition's types level by level, and OSDU's own lineage by release." },
    { label: "Partitions", to: "/delivery/partitions", icon: Layers, testId: "nav-delivery-partitions",
        description: "The OSDU partitions the flows run in, as the control plane's configuration names them, each with its cache and the flows serving it; an admin can describe or remove the others." },
    { label: "Search terms", to: "/delivery/search-terms", icon: TextSearch, testId: "nav-delivery-search-terms",
        description: "The source columns the mappings read, which the Explorer searches by: rename one to what people call it, pick the route it searches through, add a note, or delete and restore it." },
  ],
  detailTitles: [
    { pattern: /^\/delivery\/mappings\/build/, title: () => "Mapping builder" },
    { pattern: /^\/delivery\/records\/[^/]+/, title: () => "Record" },
    { pattern: /^\/delivery\/submissions\/([^/]+)/, title: (match) => `Submission ${shortId(match[1])}` },
    { pattern: /^\/delivery\/assertions\/runs\/(\d+)/, title: (match) => `Report #${match[1]}` },
  ],
  kinds: [deliveryKind, retrievalKind, cacheKind, assertionKind, dimensionKind, inventoryKind, removalKind],
  searchCategories: [
    {
      key: "records",
      render: ({ category }) => <Deferred placeholder={false}><RecordSearchHits category={category} /></Deferred>,
    },
  ],
  census,
  // The OSDU partition every OSDU page is read in, picked once for the whole workbench, and carried by every call so each
  // read across flows answers in it.
  titleBar: [{ id: "osdu-partition", component: PartitionSwitcher }],
  requestHeaders: [{ name: "X-Osdu-Partition", value: activePartition }],
  // The workbench opens on the page, with the activity bar beside it: the side bar starts closed until the person opens
  // it, its menu folded to the headers of its groups and sections, and from then on both start as they last left them.
  sideBar: { closedOnFirstVisit: true, menuCollapsedOnFirstVisit: true },
  // The home page: questions for the assistant that its delivery tools answer, and what the partition's flows hold.
  home: {
    prompts: [
      "Which delivery flows have held or failed records, and why?",
      "What changed in this partition's cache since yesterday?",
      "Which mappings deliver well logs, and what do they read from the source?",
    ],
    sections: [{ id: "osdu-delivery", component: DeliveryHomeSection }],
  },
  branding: {
    productName: "OSDU Delivery",
    attribution: "powered by SQLFlow",
    documentTitle: "OSDU Delivery",
    // The README's pitch. The streams are kinds the delivery engine delivers (docs/osdu-coverage-plan.md): any kind
    // through storage, and the bulk data of the wellbore, seismic, production and rock and fluid sample DDMS.
    login: {
      headline: "Get any data into OSDU.",
      headlineAccent: "Configured, not coded.",
      tagline: "Wells, logs, seismic, production data and more: delivered, kept in sync, and traceable record by record.",
      streams: [
        "Wells",
        "Seismic",
        "Well logs",
        "Documents",
        "Trajectories",
        "Production",
        "Wellbores",
        "Reference data",
        "Markers",
        "Pressure tests",
        "Rock and fluid samples",
      ],
    },
  },
};
