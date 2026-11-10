import type { APIRequestContext } from "@playwright/test";
import { E2E } from "../playwright.config";
import { adminSession, expect, test } from "./helpers";

// The Schedules page's two ways into a schedule, and the Pipelines page's Schedule and Wave filters, over the estate the
// seed synced (sqlflow/docs/reference/flow/schedule.md#reading-a-schedules-plan). Each test makes a schedule of its own
// through the API, disabled so it never fires. One, joined by the wellbore and the well log deliveries: its name opens
// the run board on those two flows, and its flow count opens the Pipelines page on exactly them, whatever an earlier
// visit left in the page's search. The other, joined by the wellbore ingestion and the delivery reading its table: the
// Pipelines page numbers its flows by the waves of its fire and narrows to one wave. Nothing is started and nothing
// reaches an OSDU; each schedule is deleted when its test ends.

const INGEST = "wells-wellbore-02-header-ing";
const WELLBORES = "wells-wellbore-03-header-delivery";
const LOGS = "welldb-welllog-03-header-delivery";
const SCHEDULE = "e2e-schedule-flows";
const WAVES_SCHEDULE = "e2e-schedule-waves";

/** Makes a disabled schedule of the given flows in their repo, after removing one of the name an earlier run stopped
 * before deleting (a second schedule of a name is refused). */
async function makeSchedule(request: APIRequestContext, name: string, members: string[]) {
  const session = await adminSession(request);
  const headers = { Authorization: `Bearer ${session.token}` };
  const listed = await request.get(`${E2E.apiBaseUrl}/api/v1/pipelines?name=${members[0]}&pageSize=10`, { headers });
  expect(listed.ok(), await listed.text()).toBe(true);
  const pipeline = ((await listed.json()) as { items: { name: string; repoId: string }[] }).items.find((p) => p.name === members[0]);
  expect(pipeline, `the seed syncs ${members[0]}`).toBeDefined();

  const earlier = await request.get(`${E2E.apiBaseUrl}/api/v1/schedules?search=${name}&pageSize=50`, { headers });
  expect(earlier.ok(), await earlier.text()).toBe(true);
  for (const stale of ((await earlier.json()) as { items: { id: string; name: string }[] }).items.filter((s) => s.name === name)) {
    const removed = await request.delete(`${E2E.apiBaseUrl}/api/v1/schedules/${stale.id}`, { headers });
    expect(removed.ok(), await removed.text()).toBe(true);
  }

  const created = await request.post(`${E2E.apiBaseUrl}/api/v1/schedules`, {
    headers,
    data: { repoId: pipeline!.repoId, members, cron: "0 3 * * *", timezone: "UTC", enabled: false, name },
  });
  expect(created.ok(), await created.text()).toBe(true);
  return { headers, repoId: pipeline!.repoId, scheduleId: ((await created.json()) as { id: string }).id };
}

test.describe("Schedule flows", () => {
  test("a schedule's name opens its run board and its flow count opens its flows on the Pipelines page", async ({ adminPage, request }) => {
    // Four pages, one of them reached for the first time on a dev server that compiles it then.
    test.setTimeout(180_000);
    const { headers, scheduleId } = await makeSchedule(request, SCHEDULE, [WELLBORES, LOGS]);

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

  test("with a schedule picked, the Pipelines page numbers its flows by the waves of its fire and narrows to one", async ({ adminPage, request }) => {
    test.setTimeout(180_000);
    const { headers, repoId, scheduleId } = await makeSchedule(request, WAVES_SCHEDULE, [INGEST, WELLBORES]);

    try {
      // The fire runs the ingestion before the delivery that reads its table, so the plan has them in two waves.
      const plan = await request.get(`${E2E.apiBaseUrl}/api/v1/schedules/${scheduleId}/plan`, { headers });
      expect(plan.ok(), await plan.text()).toBe(true);
      const waveOf = new Map(((await plan.json()) as { members: { flowName: string; wave: number }[] }).members
        .map((member) => [member.flowName, member.wave]));
      expect(waveOf.get(INGEST), "lineage orders the ingestion before the delivery reading its table")
        .toBeLessThan(waveOf.get(WELLBORES) as number);

      await adminPage.goto(`/pipelines?repo=${repoId}&schedule=${scheduleId}`);
      await expect(adminPage.getByTestId("page-pipelines")).toBeVisible({ timeout: 30_000 });
      const flows = adminPage.getByTestId("repo-pipeline");
      await expect(flows).toHaveCount(2, { timeout: 30_000 });
      // Each row says the wave its flow runs in as the run board numbers them, not its lineage wave in the repository.
      await expect(flows.filter({ hasText: INGEST }).getByTestId("repo-pipeline-wave")).toHaveText("wave 1");
      await expect(flows.filter({ hasText: WELLBORES }).getByTestId("repo-pipeline-wave")).toHaveText("wave 2");

      await adminPage.getByTestId("filter-wave").click();
      await adminPage.getByRole("option", { name: "wave 2 · 1 flow" }).click();
      await expect(flows).toHaveCount(1);
      await expect(flows).toContainText(WELLBORES);
      await expect(adminPage).toHaveURL(/[?&]wave=2(&|$)/);

      // The wave belongs to its schedule: clearing the schedule drops it, and with no schedule there is no wave to pick.
      await adminPage.getByTestId("filter-schedule").click();
      await adminPage.getByRole("option", { name: "Clear filter" }).click();
      await expect(adminPage.getByTestId("filter-wave")).toHaveCount(0);
      await expect(adminPage).not.toHaveURL(/[?&]wave=/);
    } finally {
      const removed = await request.delete(`${E2E.apiBaseUrl}/api/v1/schedules/${scheduleId}`, { headers });
      expect(removed.ok(), await removed.text()).toBe(true);
    }
  });
});
