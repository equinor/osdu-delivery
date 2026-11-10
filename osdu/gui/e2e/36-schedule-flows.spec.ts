import { E2E } from "../playwright.config";
import { adminSession, expect, test } from "./helpers";

// The Schedules page's two ways into a schedule, and the Pipelines page's Schedule filter, over the estate the seed
// synced (sqlflow/docs/reference/flow/schedule.md#reading-a-schedules-plan). The spec makes one schedule of its own
// through the API, disabled so it never fires, joined by the wellbore and the well log deliveries: its name opens the
// run board on those two flows, and its flow count opens the Pipelines page on exactly them, whatever an earlier visit
// left in the page's search. Nothing is started and nothing reaches an OSDU; the schedule is deleted when the spec ends.

const WELLBORES = "wells-wellbore-03-header-delivery";
const LOGS = "welldb-welllog-03-header-delivery";
const SCHEDULE = "e2e-schedule-flows";

test.describe("Schedule flows", () => {
  test("a schedule's name opens its run board and its flow count opens its flows on the Pipelines page", async ({ adminPage, request }) => {
    // Four pages, one of them reached for the first time on a dev server that compiles it then.
    test.setTimeout(180_000);
    const session = await adminSession(request);
    const headers = { Authorization: `Bearer ${session.token}` };
    const listed = await request.get(`${E2E.apiBaseUrl}/api/v1/pipelines?name=${WELLBORES}&pageSize=10`, { headers });
    expect(listed.ok(), await listed.text()).toBe(true);
    const pipeline = ((await listed.json()) as { items: { name: string; repoId: string }[] }).items.find((p) => p.name === WELLBORES);
    expect(pipeline, `the seed syncs ${WELLBORES}`).toBeDefined();

    // A run stopped before its cleanup leaves its schedule behind, and a second one of the name is refused: remove it.
    const earlier = await request.get(`${E2E.apiBaseUrl}/api/v1/schedules?search=${SCHEDULE}&pageSize=50`, { headers });
    expect(earlier.ok(), await earlier.text()).toBe(true);
    for (const stale of ((await earlier.json()) as { items: { id: string; name: string }[] }).items.filter((s) => s.name === SCHEDULE)) {
      const removed = await request.delete(`${E2E.apiBaseUrl}/api/v1/schedules/${stale.id}`, { headers });
      expect(removed.ok(), await removed.text()).toBe(true);
    }

    const created = await request.post(`${E2E.apiBaseUrl}/api/v1/schedules`, {
      headers,
      data: { repoId: pipeline!.repoId, members: [WELLBORES, LOGS], cron: "0 3 * * *", timezone: "UTC", enabled: false, name: SCHEDULE },
    });
    expect(created.ok(), await created.text()).toBe(true);
    const scheduleId = ((await created.json()) as { id: string }).id;

    try {
      // The API answers the same members as pipelines.
      const members = await request.get(`${E2E.apiBaseUrl}/api/v1/pipelines?scheduleId=${scheduleId}&pageSize=10`, { headers });
      expect(members.ok(), await members.text()).toBe(true);
      const names = ((await members.json()) as { items: { name: string }[] }).items.map((p) => p.name).sort();
      expect(names).toEqual([LOGS, WELLBORES].sort());

      // A search an earlier visit left on the Pipelines page, which would hide both flows: the link lifts it.
      await adminPage.getByTestId("nav-pipelines").click();
      await expect(adminPage.getByTestId("page-pipelines")).toBeVisible({ timeout: 30_000 });
      await adminPage.getByTestId("filter-name").fill("no-such-flow");

      await adminPage.getByTestId("nav-schedules").click();
      await expect(adminPage.getByTestId("page-schedules")).toBeVisible({ timeout: 30_000 });
      const search = adminPage.getByTestId("filter-schedule-name");
      await search.fill(SCHEDULE);
      const row = adminPage.getByTestId("table-row").filter({ hasText: SCHEDULE }).first();
      await expect(row).toBeVisible({ timeout: 30_000 });

      // The name opens the run board on what one fire runs; nothing starts until Start, which the spec never presses.
      await row.getByTestId("schedule-name-run").click();
      const board = adminPage.getByTestId("run-schedule-dialog");
      await expect(board).toBeVisible();
      await expect(board.getByTestId("run-schedule-name")).toHaveText(SCHEDULE);
      await expect(board.getByTestId("run-schedule-summary")).toHaveText("2 flows", { timeout: 30_000 });
      await expect(board.getByTestId("run-schedule-member").filter({ hasText: WELLBORES })).toBeVisible();
      await expect(board.getByTestId("run-schedule-member").filter({ hasText: LOGS })).toBeVisible();
      await adminPage.keyboard.press("Escape");
      await expect(board).toBeHidden();

      // The count opens the Pipelines page as a tab beside the Schedules page, on the schedule's flows and no others.
      await row.getByTestId("schedule-flows-link").click();
      await expect(adminPage.getByTestId("page-pipelines")).toBeVisible({ timeout: 30_000 });
      await expect(adminPage.getByTestId("filter-schedule")).toHaveText(SCHEDULE);
      await expect(adminPage.getByTestId("filter-name")).toHaveValue("");
      const flows = adminPage.getByTestId("repo-pipeline");
      await expect(flows).toHaveCount(2, { timeout: 30_000 });
      await expect(flows.filter({ hasText: WELLBORES })).toHaveCount(1);
      await expect(flows.filter({ hasText: LOGS })).toHaveCount(1);
      const tabs = adminPage.getByRole("tablist", { name: "Open pages" });
      await expect(tabs.getByRole("tab", { name: /Schedules/ })).toBeVisible();
      await expect(tabs.getByRole("tab", { name: /Pipelines/, selected: true })).toBeVisible();

      // The filter is the page's own: cleared, the tree is the whole estate again; picked, it narrows to the schedule.
      await adminPage.getByTestId("filter-schedule").click();
      await adminPage.getByRole("option", { name: "Clear filter" }).click();
      await expect(adminPage.getByTestId("filter-schedule")).toHaveText("all schedules");
      await adminPage.getByTestId("filter-schedule").click();
      await adminPage.getByPlaceholder("Search schedules").fill(SCHEDULE);
      await adminPage.getByRole("option", { name: new RegExp(SCHEDULE) }).click();
      await expect(adminPage.getByTestId("filter-schedule")).toHaveText(SCHEDULE);
      await expect(flows).toHaveCount(2, { timeout: 30_000 });

      // Leave no remembered filter behind for a later spec.
      await adminPage.getByTestId("filter-schedule").click();
      await adminPage.getByRole("option", { name: "Clear filter" }).click();
      await adminPage.getByTestId("nav-schedules").click();
      await adminPage.getByTestId("filter-schedule-name").fill("");
    } finally {
      const removed = await request.delete(`${E2E.apiBaseUrl}/api/v1/schedules/${scheduleId}`, { headers });
      expect(removed.ok(), await removed.text()).toBe(true);
    }
  });
});
