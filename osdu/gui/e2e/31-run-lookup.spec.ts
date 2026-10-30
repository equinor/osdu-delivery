import { E2E } from "../playwright.config";
import { adminSession, expect, FIRST_PAGE_MS, test } from "./helpers";

// A run found from the id a platform's log shows (osdu/docs/reference/concepts/request-correlation.md, From a log line to
// the run): the value pasted into the search lists the run and opens it, the Runs page's box lists that run alone whatever
// its filters, and an id no run has says what that means. The seed's runs are the runs it looks for.

test.describe("run lookup", () => {
  test("a correlation id from a log finds its run in the search and on the Runs page", async ({ adminPage, request }) => {
    const session = await adminSession(request);
    const listed = await request.get(`${E2E.apiBaseUrl}/api/v1/runs?pageSize=1`, {
      headers: { Authorization: `Bearer ${session.token}` },
    });
    expect(listed.ok()).toBe(true);
    const { items } = (await listed.json()) as { items: { runId: string; flowName: string }[] };
    const run = items[0];
    const value = `osdu-delivery-wl-dlv-${run.runId}`;

    await adminPage.goto(`/search?q=${encodeURIComponent(value)}`);
    const hits = adminPage.getByTestId("search-runs-table");
    await expect(hits).toBeVisible({ timeout: FIRST_PAGE_MS });
    await hits.getByRole("row").nth(1).click();
    await expect(adminPage).toHaveURL(/\/runs\/[0-9a-f-]{36}$/);

    await adminPage.goto("/runs");
    await expect(adminPage.getByTestId("page-runs")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await adminPage.getByTestId("filter-flow-name").fill(value);
    const board = adminPage.getByTestId("runs-table");
    await expect(board.getByRole("row").filter({ hasText: run.flowName })).toHaveCount(1);
    await adminPage.getByTestId("filter-flow-name").clear();
  });

  test("an explorer read's id says it is no run", async ({ adminPage }) => {
    await adminPage.goto("/search?q=osdu-delivery-explore-e227648c-0913-404d-88d6-176b8eef73e0");
    await expect(adminPage.getByTestId("search-runs-none")).toContainText("No run has this id", { timeout: FIRST_PAGE_MS });
  });
});
