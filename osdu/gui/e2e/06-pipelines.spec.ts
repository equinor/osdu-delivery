import { DELIVERY_FLOW } from "./global-setup";
import { stageWellLogRecords } from "./delivery-cli";
import { expect, expectRecordsWorkbook, test } from "./helpers";

// The pipeline catalog for a delivery flow: the module's Delivery tab and the platform's own read-only tabs beside
// it. A delivery flow opens on its Delivery tab, where the stats strip and the flow-level actions live. What kind of
// flow it is reads everywhere the flow is shown: its row in the tree, its page's header, and its workbench tab.

test.describe.serial("pipelines", () => {
  test("pipeline detail names its kind and shows the delivery, records, YAML, runs and schedules tabs", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-pipelines").click();
    // The folder tree starts collapsed; a search expands it and surfaces the flow row.
    await adminPage.getByTestId("filter-name").fill(DELIVERY_FLOW);
    const row = adminPage.getByTestId("repo-pipeline").filter({ hasText: DELIVERY_FLOW }).first();
    // The row names its kind as the module describes it, not by the flowType value alone.
    await expect(row.getByTestId("repo-pipeline-kind")).toHaveText("Delivery");
    await expect(row.getByTestId("repo-pipeline-kind")).toHaveAttribute("data-kind", "delivery");
    await row.click();
    // The suite's first visit to a pipeline's page, which the GUI's dev server compiles when it is first asked for.
    await expect(adminPage.getByTestId("page-pipeline-detail")).toBeVisible({ timeout: 30_000 });

    // The kind leads the header: its tile before the flow's name and its name under it. The workbench tab wears the same
    // icon, so pipelines of several kinds open side by side read apart.
    await expect(adminPage.getByTestId("pipeline-kind")).toHaveAttribute("data-kind", "delivery");
    await expect(adminPage.getByTestId("pipeline-kind-label")).toHaveText("Delivery flow");
    await expect(adminPage.getByRole("tablist", { name: "Open pages" }).getByRole("tab", { selected: true })
      .locator("svg[data-kind='delivery']")).toBeVisible();

    const tabs = adminPage.getByTestId("pipeline-tabs");
    // A delivery flow opens on its Delivery tab: the stats strip and the flow-level actions.
    await expect(adminPage.getByTestId("delivery-stats")).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("delivery-probe")).toBeVisible();

    await tabs.getByRole("tab", { name: /records/i }).click();
    await expect(adminPage.getByTestId("delivery-records-search")).toBeVisible();

    await tabs.getByRole("tab", { name: /yaml/i }).click();
    await expect(adminPage.getByTestId("pipeline-yaml")).toBeVisible();
    // Monaco renders the synced document: the flow name from the fixture YAML is on screen.
    await expect(adminPage.getByTestId("pipeline-yaml").getByText(DELIVERY_FLOW).first())
      .toBeVisible({ timeout: 30_000 });

    // The Definition tab would show the YAML tab's document again as JSON, so the kind hides it.
    await expect(tabs.getByRole("tab", { name: /definition/i })).toHaveCount(0);

    await tabs.getByRole("tab", { name: /runs/i }).click();
    await expect(adminPage.getByTestId("table-row").first()).toBeVisible({ timeout: 30_000 });

    await tabs.getByRole("tab", { name: /schedules/i }).click();
    await expect(adminPage.getByTestId("paged-table").first()).toBeVisible();
  });

  test("records are redelivered from the Records tab through one dialog, which asks first what bringing them up to date sends", async ({ adminPage }) => {
    // The flow's records are what the dialog acts on, and the earlier specs only plan, which stages none: the spec stages
    // them itself (an intake renders and stages every record of the scope, and sends nothing), so it holds on a first
    // run of the flow as on any later one.
    test.setTimeout(600_000);
    expect(stageWellLogRecords()).toContain("record(s)");

    await adminPage.getByTestId("nav-pipelines").click();
    await adminPage.getByTestId("filter-name").fill(DELIVERY_FLOW);
    await adminPage.getByTestId("repo-pipeline").filter({ hasText: DELIVERY_FLOW }).first().click();
    await expect(adminPage.getByTestId("page-pipeline-detail")).toBeVisible();

    // The overview offers it for every delivered record.
    await expect(adminPage.getByTestId("delivery-redeliver-all")).toBeVisible({ timeout: 30_000 });

    // A ticked record: the dialog opens over the Records tab and names it.
    await adminPage.getByTestId("pipeline-tabs").getByRole("tab", { name: /records/i }).click();
    await adminPage.getByTestId("delivery-records-table").getByTestId("row-select").first().check({ timeout: 30_000 });
    await adminPage.getByTestId("delivery-redeliver-selected").click();
    const dialog = adminPage.getByTestId("redeliver-dialog");
    await expect(dialog).toBeVisible();
    await expect(adminPage.getByTestId("redeliver-selection")).toContainText("One record");

    // Bringing it up to date is offered first, and its check says why there is nothing to check: OSDU holds none of it.
    await expect(adminPage.getByTestId("redeliver-mode-uptodate")).toHaveAttribute("aria-checked", "true");
    await expect(adminPage.getByTestId("redeliver-mode-again")).toHaveAttribute("aria-checked", "false");
    await expect(adminPage.getByTestId("redeliver-preview-error")).toContainText("nothing to bring up to date", { timeout: 30_000 });

    // Sending again offers the parts the flow's route sends: a ddms route sends the record and its bulk data.
    await adminPage.getByTestId("redeliver-mode-again").click();
    await expect(adminPage.getByTestId("redeliver-part-record")).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("redeliver-part-bulk")).toBeVisible();
    await expect(adminPage.getByTestId("redeliver-part-files")).toHaveCount(0);
    await expect(adminPage.getByTestId("redeliver-confirm")).toHaveText(/Send again/);
    await expect(adminPage.getByTestId("redeliver-mode-again")).toHaveAttribute("aria-checked", "true");
    await expect(adminPage.getByTestId("redeliver-again-warning")).toContainText("whether it changed or not");

    await adminPage.getByTestId("redeliver-cancel").click();
    await expect(dialog).toHaveCount(0);
  });

  test("the Records tab downloads every record its filter matches as an Excel workbook, and says why when there is none", async ({ adminPage }) => {
    test.setTimeout(600_000);
    expect(stageWellLogRecords()).toContain("record(s)");

    await adminPage.getByTestId("nav-pipelines").click();
    await adminPage.getByTestId("filter-name").fill(DELIVERY_FLOW);
    await adminPage.getByTestId("repo-pipeline").filter({ hasText: DELIVERY_FLOW }).first().click();
    await expect(adminPage.getByTestId("page-pipeline-detail")).toBeVisible();
    await adminPage.getByTestId("pipeline-tabs").getByRole("tab", { name: /records/i }).click();
    const table = adminPage.getByTestId("delivery-records-table");
    await expect(table.getByTestId("table-row").first()).toBeVisible({ timeout: 30_000 });

    // Every record the filter matches, not the page in view, named after the flow.
    const exportButton = adminPage.getByTestId("delivery-records-export");
    await expect(exportButton).toBeEnabled();
    const [workbook] = await Promise.all([adminPage.waitForEvent("download"), exportButton.click()]);
    expect(workbook.suggestedFilename().startsWith(`${DELIVERY_FLOW}-`)).toBe(true);
    expect(workbook.suggestedFilename()).toMatch(/-records\.xlsx$/);
    expectRecordsWorkbook(await workbook.path());

    // A status no record is in leaves nothing to export, and the button says so on hover rather than offering an empty file.
    await adminPage.getByTestId("delivery-records-status").click();
    await adminPage.getByRole("option", { name: "deleted" }).click();
    await expect(table.getByText(/No records match/)).toBeVisible({ timeout: 30_000 });
    await expect(exportButton).toBeDisabled();
    await expect(adminPage.getByTestId("delivery-records-export-hint")).toHaveAttribute("title", /nothing to export/);
  });
});
