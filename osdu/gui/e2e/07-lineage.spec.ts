import { E2E } from "../playwright.config";
import { DELIVERY_FLOW, SOURCE } from "./global-setup";
import { adminSession, expect, test } from "./helpers";

// Lineage over the synced estate: every OSDU flow shows with its data on both sides. The delivery flows write the OSDU
// types their mappings fill and read their mappings, each a node of its own that reads the partition cache types and the
// kinds it resolves against; the cache flow reads OSDU types and writes those cache types. The catalog explorer lists
// them as datasets, a dataset's page names the pipelines on either side of it, and the lineage graph draws it captioned
// by its system.

interface DatasetNode {
  key: string;
  system: string;
  namespace: string;
  group: string;
  name: string;
  readers: number;
  writers: number;
}

const WELL_LOG = "osdu:wks:work-product-component--WellLog:1.4.0";

const WELLBORE = "osdu:wks:master-data--Wellbore:1.3.0";

/**
 * The system a partition's cache types belong to (`OsduLineage.CacheSystem`). It is the cache of the partition, not of
 * the flow: a partition has one cache whichever flow fills it, so the system never carries a flow's name.
 */
const CACHE_SYSTEM = "osdu-cache";

/** The system a mapping's node belongs to (`OsduLineage.MappingSystem`). */
const MAPPING_SYSTEM = "osdu-mapping";

/** The mapping the sample's delivery flow and the fixture source's welllogs interface both pin. */
const WELL_LOG_MAPPING = "WellLog@1.4.0";

/** The partition the estate's flows name, as lineage keeps it: the name they write under partitions, lower-cased as every namespace is. */
const PARTITION = "dev";

test.describe.serial("lineage", () => {
  test("the synced estate lists the OSDU types and cache types its flows write and read", async ({ request }) => {
    const session = await adminSession(request);
    const response = await request.get(`${E2E.apiBaseUrl}/api/v1/lineage/datasets`, {
      headers: { Authorization: `Bearer ${session.token}` },
    });
    expect(response.ok(), `GET /lineage/datasets answered ${response.status()}`).toBeTruthy();
    const datasets = (await response.json()) as DatasetNode[];
    const named = (system: string, name: string) =>
      datasets.find((d) => d.system === system && d.name.toLowerCase() === name.toLowerCase());

    const wellLog = named("osdu-type", WELL_LOG);
    expect(wellLog, `no ${WELL_LOG} among ${datasets.map((d) => d.name).join(", ")}`).toBeDefined();
    // Two flows deliver well logs: the sample's delivery flow in the single form, and the welllogs interface of the
    // fixture source that delivers several kinds as interfaces.
    // Every flow of the estate names the same partition, and lineage keeps a partition as it is written, so every flow
    // delivering there meets on the same node.
    expect([wellLog!.namespace, wellLog!.group, wellLog!.writers]).toEqual([PARTITION, "work-product-component", 2]);

    const wellbore = named("osdu-type", WELLBORE);
    expect(wellbore).toBeDefined();
    // The fixture wellbore flow and the wellbores interface of the fixture source write it; the well log mapping's search and
    // the download read it back through their wildcard kinds.
    expect(wellbore!.writers).toBe(2);
    expect(wellbore!.readers).toBeGreaterThanOrEqual(2);

    // Two cache flows capture the partition's units: the fixture reference flow and the sample's reference flow, whose
    // declarations of the type agree, so the cache holds one UnitOfMeasure type both write.
    const units = named(CACHE_SYSTEM, "UnitOfMeasure");
    expect(units).toBeDefined();
    expect([units!.namespace, units!.group, units!.writers]).toEqual([PARTITION, "cache", 2]);
    const families = named(CACHE_SYSTEM, "LogCurveFamily");
    expect([families?.namespace, families?.group, families?.writers]).toEqual([PARTITION, "cache", 1]);

    // The lookup tables sit in the same partition's cache, written by the lookups flow and read by the well log mapping,
    // which translates through them.
    const unitAlias = named(CACHE_SYSTEM, "UnitAlias");
    expect([unitAlias?.namespace, unitAlias?.group, unitAlias?.writers]).toEqual([PARTITION, "cache", 1]);
    expect(unitAlias!.readers).toBeGreaterThanOrEqual(1);
    expect(units!.readers).toBeGreaterThanOrEqual(1);

    // A mapping is a node of its own, under the folder it is filed in. The sample's delivery flow and the welllogs
    // interface of the fixture source render with the same mapping for the same partition, so both read the one node;
    // no flow writes it.
    const mapping = named(MAPPING_SYSTEM, WELL_LOG_MAPPING);
    expect(mapping, `no ${WELL_LOG_MAPPING} among ${datasets.map((d) => d.name).join(", ")}`).toBeDefined();
    expect([mapping!.namespace, mapping!.group, mapping!.readers, mapping!.writers]).toEqual([PARTITION, `${SOURCE}/mappings`, 2, 0]);

    // The mapping never names these two types: the ids it builds are checked against them, LogCurveFamily's through an
    // id whose entity type the mapping writes, LogType's through a ref whose entity type its template tells. Both are
    // read through the mapping by the flows that render with it, so neither is a cache type nothing reads.
    expect(families!.readers).toBe(2);
    const logTypes = named(CACHE_SYSTEM, "LogType");
    expect([logTypes?.namespace, logTypes?.writers, logTypes?.readers]).toEqual([PARTITION, 1, 2]);
  });

  test("an OSDU type opens with the pipeline that writes it, and jumps to the graph", async ({ adminPage }) => {
    await adminPage.goto("/catalog");
    await adminPage.getByTestId("catalog-filter").fill("WellLog:1.4.0");
    const matches = adminPage.getByTestId("catalog-object-matches");
    await matches.getByRole("button", { name: /work-product-component--WellLog:1\.4\.0/ }).first().click();

    const details = adminPage.getByTestId("catalog-object-details");
    await expect(details.getByText("Dataset", { exact: true })).toBeVisible();
    await expect(details.getByText("osdu type", { exact: true })).toBeVisible();
    await details.getByTestId("catalog-tab-pipelines").click();
    const provenance = adminPage.getByTestId("catalog-file-provenance");
    await expect(provenance.getByText(DELIVERY_FLOW, { exact: true })).toBeVisible({ timeout: 30_000 });

    await details.getByTestId("search-open-graph").click();
    await adminPage.getByTestId("lineage-jump-option").first().click();
    const graph = adminPage.getByTestId("page-lineage-graph");
    await expect(graph).toBeVisible();
    await expect(graph.getByText(WELL_LOG, { exact: true }).first()).toBeVisible({ timeout: 30_000 });
    await expect(graph.getByText(`osdu type · ${PARTITION}.work-product-component`).first()).toBeVisible();
    await expect(graph.getByText(DELIVERY_FLOW, { exact: true }).first()).toBeVisible();

    // The jump names the repository, so the graph opens on the type's trace through it, as flows alone: the flows that
    // lead to the type, and nothing else of the repository.
    await expect(graph.getByTestId("graph-status-trace")).toContainText("trace of osdu:wks:work-product-component--WellLog:1.4.0");
    await expect(graph.getByTestId("graph-status-line")).toContainText("flows only");
    await expect(graph.getByText(WELL_LOG_MAPPING, { exact: true })).toHaveCount(0);

    // Everything draws the data between them. The flow's mapping is drawn between what it reads and the flow, captioned
    // by its system and where it is filed, with a cache type it only checks its ids against feeding it. The cache types
    // one mapping reads look alike (one kind, the same flow before them and the same mapping after), so they are one
    // stack until stacks are turned off.
    // The two clicks come one straight after the other, and both must hold: the address is a render behind, and a
    // control that started from it would drop the click before.
    await graph.getByTestId("graph-level-all").click();
    await graph.getByTestId("graph-stack-toggle").click();
    await expect(graph.getByTestId("graph-status-line")).toContainText("everything");
    await expect.poll(() => new URL(adminPage.url()).searchParams.get("stacks")).toBe("off");
    await expect(graph.getByText(WELL_LOG_MAPPING, { exact: true }).first()).toBeVisible();
    await expect(graph.getByText(`osdu mapping · ${PARTITION}.${SOURCE}/mappings`).first()).toBeVisible();
    await expect(graph.getByText("LogType", { exact: true }).first()).toBeVisible();
  });

  test("the graph opens a repository as its flows, and each control says what it changed", async ({ adminPage }) => {
    await adminPage.goto("/lineage");
    await adminPage.getByTestId("graph-project-select").click();
    await adminPage.getByTestId("graph-scope-repo").first().click();

    const graph = adminPage.getByTestId("page-lineage-graph");
    const status = graph.getByTestId("graph-status-line");
    const nodes = graph.getByTestId("graph-node");
    const named = (name: string) => nodes.filter({ hasText: name });
    const data = graph.locator('[data-testid="graph-node"][data-node-type="object"]');

    // A repository opens as its flows alone: no table, file, mapping or OSDU type is drawn until it is asked for.
    await expect(status).toContainText("flows only", { timeout: 30_000 });
    await expect(nodes.first()).toBeVisible({ timeout: 30_000 });
    await expect(data).toHaveCount(0);
    const repoId = new URL(adminPage.url()).searchParams.get("repoId");
    expect(repoId).not.toBeNull();

    // Every flow one by one, whatever the estate's size would have folded or stacked.
    await adminPage.goto(`/lineage?repoId=${repoId}&group=off&stacks=off`);
    await expect(named(DELIVERY_FLOW)).toHaveCount(1, { timeout: 30_000 });
    await expect(graph.getByTestId("graph-reset")).toBeVisible();

    // Everything draws the data beside the flows, and the level is in the address.
    await graph.getByTestId("graph-level-all").click();
    await expect(status).toContainText("everything");
    await expect(data.first()).toBeVisible();
    expect(new URL(adminPage.url()).searchParams.get("show")).toBe("all");
    await graph.getByTestId("graph-level-flows").click();
    await expect(data).toHaveCount(0);

    // A flow's card has no room for what it reads and writes; its panel lists both, whatever the level draws.
    await named(DELIVERY_FLOW).click();
    const panel = graph.getByTestId("graph-focus-panel");
    await expect(panel).toContainText("upstream");
    await expect(panel.getByTestId("graph-detail-reads")).toContainText(WELL_LOG_MAPPING);
    await expect(panel.getByTestId("graph-detail-writes")).toContainText(WELL_LOG);

    // Trace draws only what is upstream and downstream of it; the bar gains the trace's reach and side, and the line
    // under it says what is traced.
    await panel.getByTestId("graph-trace-selected").click();
    await expect(graph.getByTestId("graph-status-trace")).toContainText(`trace of ${DELIVERY_FLOW}`);
    await expect(named("welldb-welllog-02-header-ing")).toHaveCount(1);
    await graph.getByTestId("graph-trace-hops-1").click();
    await graph.getByTestId("graph-trace-upstream").click();
    await expect(graph.getByTestId("graph-status-trace")).toContainText("1 hop, upstream only");
    const traced = new URL(adminPage.url()).searchParams;
    expect([traced.get("hops"), traced.get("dir")]).toEqual(["1", "up"]);

    // An entry of the panel's lists draws its node, even one the level folds away: the mapping the flow reads.
    await panel.getByTestId("graph-detail-reads").getByTestId("graph-detail-node").filter({ hasText: WELL_LOG_MAPPING }).first().click();
    await expect(named(WELL_LOG_MAPPING)).toHaveCount(1);

    // Taking the trace back from the line draws the repository again.
    await graph.getByTestId("graph-status-remove-trace").click();
    await expect(graph.getByTestId("graph-status-trace")).toHaveCount(0);
    await expect(graph.getByTestId("graph-trace-control")).toHaveCount(0);

    // A hidden kind leaves the drawing, and the line names it.
    await graph.getByTestId("graph-clear-focus").click();
    await graph.getByTestId("graph-kind-filter").click();
    await adminPage.locator('[data-testid="graph-kind-option"][data-kind="flow:ing"]').click();
    await adminPage.keyboard.press("Escape");
    await expect(graph.getByTestId("graph-status-kinds")).toContainText("hidden: Ingestion");
    await expect(named("welldb-welllog-02-header-ing")).toHaveCount(0);

    // The wave range trims the drawing from either end, by the keyboard as by dragging.
    await graph.getByRole("slider", { name: "First wave drawn" }).press("ArrowRight");
    await expect(graph.getByTestId("graph-status-waves")).toContainText("wave");
    expect(new URL(adminPage.url()).searchParams.get("waves")).not.toBeNull();

    // Reset takes every choice back.
    await graph.getByTestId("graph-reset").click();
    await expect(graph.getByTestId("graph-status-kinds")).toHaveCount(0);
    await expect(graph.getByTestId("graph-status-waves")).toHaveCount(0);
    const reset = new URL(adminPage.url()).searchParams;
    expect([reset.get("hide"), reset.get("waves"), reset.get("group"), reset.get("stacks")]).toEqual([null, null, null, null]);

    // Data runs top to bottom unless the reader turns the drawing, and the choice is in the address.
    expect(reset.get("layout")).toBeNull();
    await graph.getByTestId("graph-orientation-right").click();
    await expect.poll(() => new URL(adminPage.url()).searchParams.get("layout")).toBe("right");
    await expect(nodes.first()).toBeVisible();
    await graph.getByTestId("graph-orientation-down").click();
    await expect.poll(() => new URL(adminPage.url()).searchParams.get("layout")).toBeNull();
  });

  test("a mapping's row jumps to its node in the graph", async ({ adminPage }) => {
    await adminPage.goto("/delivery/documents");
    const row = adminPage.getByTestId("delivery-mappings-table").getByTestId("table-row").filter({ hasText: WELL_LOG_MAPPING }).first();
    await row.getByTestId("search-open-graph").click();

    // The picker names the mapping and the file it is read from, and offers the node the partition's flows read. The
    // jump opens the picker, not the mapping's page the row itself opens.
    const menu = adminPage.getByTestId("lineage-jump-menu");
    await expect(menu).toContainText(WELL_LOG_MAPPING);
    await expect(menu).toContainText(`${SOURCE}/mappings/`);
    await expect(adminPage.getByTestId("delivery-mapping-detail")).toHaveCount(0);
    const option = adminPage.getByTestId("lineage-jump-option");
    await expect(option).toHaveCount(1, { timeout: 30_000 });
    await expect(option).toContainText(`${PARTITION}.${SOURCE}/mappings`);
    await expect(option).toContainText("Written by 0 flows, read by 2 flows");
    await option.click();

    const graph = adminPage.getByTestId("page-lineage-graph");
    await expect(graph).toBeVisible();
    await expect(graph.getByText(WELL_LOG_MAPPING, { exact: true }).first()).toBeVisible({ timeout: 30_000 });
    await expect(graph.getByText(`osdu mapping · ${PARTITION}.${SOURCE}/mappings`).first()).toBeVisible();
    await expect(graph.getByText(DELIVERY_FLOW, { exact: true }).first()).toBeVisible();
  });

  test("a cache type jumps to its node in the graph, from its own table and from its section", async ({ adminPage }) => {
    // From the type's own table: the node the two reference flows write and the mappings resolving units read.
    await adminPage.goto("/delivery/cache?type=UnitOfMeasure");
    const header = adminPage.getByTestId("delivery-cache-type-header");
    await expect(header.getByTestId("delivery-cache-type-name")).toHaveText("UnitOfMeasure", { timeout: 30_000 });
    await header.getByTestId("search-open-graph").click();
    const option = adminPage.getByTestId("lineage-jump-option");
    await expect(option).toHaveCount(1, { timeout: 30_000 });
    await expect(option).toContainText(`${PARTITION}.cache`);
    await expect(option).toContainText("Written by 2 flows");
    await option.click();

    const graph = adminPage.getByTestId("page-lineage-graph");
    await expect(graph).toBeVisible();
    await expect(graph.getByText("UnitOfMeasure", { exact: true }).first()).toBeVisible({ timeout: 30_000 });
    await expect(graph.getByText(`osdu cache · ${PARTITION}.cache`).first()).toBeVisible();

    // From the type's section among every type's records: the same node, and the section is not opened by the jump.
    await adminPage.goto("/delivery/cache");
    const section = adminPage.locator('[data-testid="delivery-cache-section"][data-type="LogCurveFamily"]');
    await section.getByTestId("search-open-graph").click();
    await expect(adminPage.getByTestId("lineage-jump-menu")).toContainText("LogCurveFamily");
    await expect(adminPage.getByTestId("lineage-jump-option")).toContainText(`${PARTITION}.cache`, { timeout: 30_000 });
    await adminPage.getByTestId("lineage-jump-option").click();
    await expect(graph.getByText("LogCurveFamily", { exact: true }).first()).toBeVisible({ timeout: 30_000 });
  });

  test("a mapping's overview opens what it reads in lineage, from a part's menu and from its panel", async ({ adminPage }) => {
    await adminPage.goto("/delivery/documents");
    const row = adminPage.getByTestId("delivery-mappings-table").getByTestId("table-row").filter({ hasText: WELL_LOG_MAPPING }).first();
    await row.click();
    const overview = adminPage.getByTestId("mapping-overview");

    // The record table is named once the flow's tables are read, which is when the canvas knows the node it is.
    const table = overview.getByTestId("mapping-overview-card-dataset");
    await expect(table.locator("text.title")).toHaveText("silver.WellLog", { timeout: 30_000 });

    // A right click on it offers Open in lineage, holding the picker for the node the flow reads, in the repository whose
    // ingestion flow populates it; the graph opens on it with that flow.
    await table.locator("g.head").click({ button: "right" });
    await adminPage.getByTestId("mapping-overview-menu-lineage").click();
    await expect(adminPage.getByTestId("lineage-jump-menu")).toContainText("silver.WellLog");
    const populated = adminPage.getByTestId("lineage-jump-option").filter({ hasText: "populates" });
    await expect(populated).toHaveCount(1, { timeout: 30_000 });
    await populated.click();
    const graph = adminPage.getByTestId("page-lineage-graph");
    await expect(graph).toBeVisible();
    await expect(graph.getByText("welldb-welllog-02-header-ing", { exact: true }).first()).toBeVisible({ timeout: 30_000 });
    await expect(graph.getByText(DELIVERY_FLOW, { exact: true }).first()).toBeVisible();

    // A cache table's panel carries the jump: the partition's cache type, which the cache flows write.
    await adminPage.goBack();
    await overview.locator('[data-testid="mapping-overview-card-cache.UnitOfMeasure"] g.head').dblclick();
    const inspector = overview.getByTestId("mapping-overview-inspector");
    await inspector.getByTestId("mapping-overview-inspector-lineage").getByTestId("search-open-graph").click();
    await expect(adminPage.getByTestId("lineage-jump-menu")).toContainText("UnitOfMeasure");
    await expect(adminPage.getByTestId("lineage-jump-option")).toContainText(`${PARTITION}.cache`, { timeout: 30_000 });
    // Escape closes the picker first, and leaves the panel it was opened from.
    await adminPage.keyboard.press("Escape");
    await expect(adminPage.getByTestId("lineage-jump-menu")).toHaveCount(0);
    await expect(inspector.getByTestId("mapping-overview-inspector-close")).toBeVisible();

    // The curves come from several nodes, each a picker of its own: the child table the section repeats, and the cache.
    await overview.getByTestId("mapping-overview-row-data.Curves").click({ button: "right" });
    await adminPage.getByTestId("mapping-overview-menu-lineage").click();
    await expect(adminPage.getByTestId("mapping-overview-menu-lineage-cache.UnitOfMeasure")).toBeVisible();
    await adminPage.getByTestId("mapping-overview-menu-lineage-dataset.curves").click();
    await expect(adminPage.getByTestId("lineage-jump-menu")).toContainText("silver.WellLogCurve");
    await expect(adminPage.getByTestId("lineage-jump-option").filter({ hasText: "populates" })).toHaveCount(1, { timeout: 30_000 });
  });

  test("the catalog tree groups datasets by system, partition and group", async ({ adminPage }) => {
    await adminPage.goto("/catalog");
    const tree = adminPage.getByRole("tree", { name: "Catalog tree" });
    await tree.getByRole("treeitem", { name: /^Datasets/ }).click();
    await tree.getByRole("treeitem", { name: /^osdu cache/ }).click();
    await tree.getByRole("treeitem", { name: new RegExp(`^${PARTITION.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}`) }).first().click();
    await tree.getByRole("treeitem", { name: /^cache/ }).click();
    await expect(tree.getByRole("treeitem", { name: /^UnitOfMeasure/ })).toBeVisible();
  });
});
