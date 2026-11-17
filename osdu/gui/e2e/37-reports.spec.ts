import type { Page } from "@playwright/test";
import { E2E } from "../playwright.config";
import { LOG_SOURCE, REPORT, REPORT_TITLE } from "./global-setup";
import { adminSession, expect, FIRST_PAGE_MS, test } from "./helpers";

// Reports (sqlflow/docs/reference/concepts/reports.md): the estate's report over the well log ingestion table, a
// fixture document the seed synced with the repository, opened from the Reports page. Its queries run on the control
// plane's own worker against the table the seed's chain loaded, so what a tile shows is what the database holds. The
// suite's database outlives a run and keeps the well logs earlier samples staged, so the tests compare the tiles with
// each other rather than with a count written here. Nothing is run as a flow and nothing reaches an OSDU.

/** How long a tile may take to have its answer: a query is queued, claimed by the worker and read back. */
const ANSWER_MS = 60_000;

/** The queries of the report that are tiles; `sources` only lists the filter's choices. */
const TILES = ["totals", "by_source", "logs"] as const;

/** A figure as a number: it is printed with its thousands separated. */
const numberOf = (text: string | null) => Number((text ?? "").replace(/[^\d]/g, ""));

/** One of the staging area's figures, by its column in the answer: well logs, log sources, wellbores, last load. */
const figure = (page: Page, column: number) => page.getByTestId(`report-tile-totals-figures-${column}-value`);

const bars = (page: Page) => page.getByTestId("report-tile-by_source-bars-row");

/** The bar of the log source the sample stages, which a click turns into the report's filter. */
const sampleBar = (page: Page) => bars(page).filter({ hasText: LOG_SOURCE });

async function expectEveryTileAnswered(page: Page): Promise<void> {
  for (const tile of TILES) {
    await expect(page.getByTestId(`report-tile-${tile}`)).toHaveAttribute("data-status", "answered", { timeout: ANSWER_MS });
  }
}

/** Opens the report from the menu and the Reports page's list, and waits for every tile's answer. */
async function openReport(page: Page): Promise<void> {
  await page.getByTestId("nav-reports").click();
  await expect(page.getByTestId("page-reports")).toBeVisible({ timeout: FIRST_PAGE_MS });
  await page.getByTestId("reports-search").fill(REPORT_TITLE);
  const listed = page.getByTestId("reports-table").getByText(REPORT_TITLE, { exact: true });
  await expect(listed).toBeVisible({ timeout: 30_000 });
  await listed.click();
  await expect(page.getByTestId("report-title")).toHaveText(REPORT_TITLE, { timeout: FIRST_PAGE_MS });
  await expectEveryTileAnswered(page);
}

/** Records every query the page asks the control plane to run, with the body it sent. */
function recordRuns(page: Page): { query: string; body: string | null }[] {
  const runs: { query: string; body: string | null }[] = [];
  page.on("request", (request) => {
    const asked = /\/reports\/[^/]+\/queries\/([^/]+)\/run$/.exec(new URL(request.url()).pathname);
    if (asked !== null && request.method() === "POST") {
      runs.push({ query: asked[1], body: request.postData() });
    }
  });
  return runs;
}

test.describe("reports", () => {
  test("the Reports page lists the estate's report, and its page shows what the staging area holds", async ({ adminPage }) => {
    const runs = recordRuns(adminPage);
    await openReport(adminPage);

    // The figures are numbers the database answered, and the tiles agree with each other: the bars are the log sources,
    // and their well logs add up to the staging area's.
    const wellLogs = numberOf(await figure(adminPage, 0).textContent());
    expect(wellLogs).toBeGreaterThan(0);
    await expect(sampleBar(adminPage)).toHaveCount(1);
    expect(numberOf(await figure(adminPage, 1).textContent())).toBe(await bars(adminPage).count());
    let staged = 0;
    for (const bar of await bars(adminPage).all()) {
      staged += numberOf(await bar.locator("span").last().textContent());
    }
    expect(staged).toBe(wellLogs);

    // The table lists the well logs themselves, each with the file it was read from.
    const rows = adminPage.getByTestId("report-tile-logs-table").getByTestId("table-row");
    await expect(rows.first()).toBeVisible();
    await expect(rows.filter({ hasText: LOG_SOURCE }).first()).toContainText(".csv");

    // Every query was asked for on its own and sent no statement: the page gives only the report's values. The query
    // that lists the filter's choices ran too, and is not a tile. (How often each was asked is not held here: the dev
    // server this suite runs on mounts a page twice, so it asks twice where the built GUI asks once.)
    expect([...new Set(runs.map((run) => run.query))].sort()).toEqual(["by_source", "logs", "sources", "totals"]);
    expect(runs.every((run) => run.body === JSON.stringify({ values: {} }))).toBe(true);
    await expect(adminPage.getByTestId("report-tile-sources")).toHaveCount(0);
  });

  test("a click on a bar narrows the report to that log source, and Reset widens it again", async ({ adminPage }) => {
    const runs = recordRuns(adminPage);
    await openReport(adminPage);
    const whole = numberOf(await figure(adminPage, 0).textContent());
    const ofTheSample = numberOf(await sampleBar(adminPage).locator("span").last().textContent());
    const before = runs.length;

    // The address carries the value, so the view can be linked; the filter shows it; and only the queries that bind it
    // run again, with that value and still no statement.
    await sampleBar(adminPage).click();
    await expect(adminPage).toHaveURL(new RegExp(`[?&]p\\.source=${LOG_SOURCE}(&|$)`));
    await expect(sampleBar(adminPage)).toHaveAttribute("aria-pressed", "true");
    await expect(adminPage.getByTestId("report-parameter-source")).toContainText(LOG_SOURCE);
    await expect.poll(() => runs.slice(before).map((run) => run.query).sort(), { timeout: ANSWER_MS }).toEqual(["logs", "totals"]);
    expect(runs.slice(before).every((run) => run.body === JSON.stringify({ values: { source: LOG_SOURCE } }))).toBe(true);
    await expectEveryTileAnswered(adminPage);

    // The staging area is now that log source's: its well logs, one log source, and a table of nothing else.
    await expect(figure(adminPage, 0)).toHaveText(ofTheSample.toLocaleString("en-US"), { timeout: ANSWER_MS });
    await expect(figure(adminPage, 1)).toHaveText("1");
    const rows = adminPage.getByTestId("report-tile-logs-table").getByTestId("table-row");
    await expect(rows.first()).toBeVisible();
    await expect(rows.filter({ hasNotText: LOG_SOURCE })).toHaveCount(0, { timeout: ANSWER_MS });

    // How the tile was computed: the statement as the document declares it, and the value it was bound with.
    await adminPage.getByTestId("report-tile-totals-menu").click();
    await adminPage.getByTestId("report-tile-totals-details").click();
    const sheet = adminPage.getByTestId("report-query-sheet");
    await expect(sheet).toBeVisible();
    await expect(adminPage.getByTestId("report-query-sql")).toContainText("FROM silver.WellLog");
    await expect(adminPage.getByTestId("report-query-values")).toContainText("@source");
    await expect(adminPage.getByTestId("report-query-values")).toContainText(LOG_SOURCE);
    await adminPage.keyboard.press("Escape");
    await expect(sheet).toBeHidden();

    // Reset takes the value out of the address, and the staging area is whole again.
    await adminPage.getByTestId("report-parameters-reset").click();
    await expect(adminPage).not.toHaveURL(/p\.source=/);
    await expect(figure(adminPage, 0)).toHaveText(whole.toLocaleString("en-US"), { timeout: ANSWER_MS });
    await expect(sampleBar(adminPage)).toHaveAttribute("aria-pressed", "false");
  });

  test("a report is a document that is never run as a flow", async ({ adminPage, request }) => {
    await openReport(adminPage);

    // Its document's page says so: it offers the report, and no run.
    await adminPage.getByTestId("report-menu").click();
    await adminPage.getByTestId("report-open-document").click();
    await expect(adminPage.getByTestId("pipeline-open-report")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await expect(adminPage.getByTestId("pipeline-never-run")).toHaveText("run from its page");
    await expect(adminPage.getByTestId("open-trigger-run")).toHaveCount(0);
    await adminPage.getByTestId("pipeline-open-report").click();
    await expect(adminPage.getByTestId("report-title")).toHaveText(REPORT_TITLE, { timeout: FIRST_PAGE_MS });

    // And so does the control plane: the report is listed as a report, not among the flows, and a trigger refuses it.
    const session = await adminSession(request);
    const headers = { Authorization: `Bearer ${session.token}` };
    const listed = await request.get(`${E2E.apiBaseUrl}/api/v1/reports`, { headers });
    expect(listed.ok(), await listed.text()).toBe(true);
    const report = ((await listed.json()) as { name: string; repoId: string; problem: string | null }[]).find((r) => r.name === REPORT);
    expect(report, `the seed syncs ${REPORT}`).toBeDefined();
    expect(report!.problem).toBeNull();

    const flows = await request.get(`${E2E.apiBaseUrl}/api/v1/pipelines?name=${REPORT}&pageSize=10`, { headers });
    expect(flows.ok(), await flows.text()).toBe(true);
    expect(((await flows.json()) as { items: { name: string }[] }).items.filter((p) => p.name === REPORT)).toEqual([]);

    const refused = await request.post(`${E2E.apiBaseUrl}/api/v1/runs`, { headers, data: { repoId: report!.repoId, flowName: REPORT } });
    expect(refused.status(), await refused.text()).toBe(400);
    expect(await refused.text()).toContain("not a flow");
  });
});
