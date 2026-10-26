import type { APIRequestContext } from "@playwright/test";
import { E2E } from "../playwright.config";
import { REPO_NAME } from "./global-setup";
import { adminSession, apiLogin, expect, FIRST_PAGE_MS, seedSession, seedSideBarOpen, test } from "./helpers";

// Admin > External triggers (sqlflow/docs/reference/concepts/external-triggers.md): a GitHub trigger is created with what
// it trusts and the page writes the workflow that connects it; a key trigger's key is shown once, exchanged for the
// trigger's credential and used to sync the estate's repository, which the Requests tab follows to its end and whose
// trace opens in the bottom panel. The sync reads the commit the estate already holds, so the specs after this one see
// it unchanged; both triggers are removed before the spec ends.

const runTag = Date.now().toString(36);
const GITHUB_TRIGGER = `e2e-gh-${runTag}`;
const KEY_TRIGGER = `e2e-key-${runTag}`;
/** A schedule the estate does not have, as a merge would add it: the trigger may name it, and the page warns. */
const MERGE_SCHEDULE = "e2e-merge-nightly";
const OPERATOR = { username: "e2e-triggers-operator", password: "E2e-triggers-operator-1!" };

let triggerKey = "";

/** An operator account (no admin role), made once through the admin's API; a run that finds it made goes on. */
async function operatorSession(request: APIRequestContext) {
  const admin = await adminSession(request);
  const created = await request.post(`${E2E.apiBaseUrl}/api/v1/users`, {
    headers: { Authorization: `Bearer ${admin.token}` },
    data: { ...OPERATOR, role: "operator", email: null, displayName: "E2E triggers operator" },
  });
  expect([201, 409]).toContain(created.status());
  return apiLogin(request, OPERATOR.username, OPERATOR.password);
}

test.describe.serial("External triggers", () => {
  test("a GitHub trigger is created with what it trusts, and the page writes the workflow that connects it", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-external-triggers").click();
    await expect(adminPage.getByTestId("page-external-triggers")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await adminPage.getByTestId("open-create-trigger").click();
    const sheet = adminPage.getByTestId("trigger-sheet");
    await expect(sheet.getByTestId("trigger-kind-github")).toHaveAttribute("aria-pressed", "true");

    // Nothing is saved until the form is whole, and it says what is missing.
    await sheet.getByTestId("trigger-submit").click();
    await expect(sheet.getByText("Pick the repository the trigger syncs.")).toBeVisible();
    await expect(sheet.getByText("The GitHub repository whose workflows it trusts, written owner/name.")).toBeVisible();

    await sheet.getByTestId("trigger-name").fill(GITHUB_TRIGGER);
    await sheet.getByTestId("trigger-repo").click();
    await adminPage.getByRole("option", { name: REPO_NAME, exact: true }).click();
    await sheet.getByTestId("trigger-extra-schedule").fill(MERGE_SCHEDULE);
    await sheet.getByTestId("trigger-extra-schedule").press("Enter");
    await sheet.getByTestId("trigger-gh-repository").fill("acme/e2e-estate");
    await sheet.getByTestId("trigger-gh-workflow").fill(".github/workflows/sqlflow.yml");
    await sheet.getByTestId("trigger-submit").click();

    const connect = adminPage.getByTestId("connect-sheet");
    await expect(connect).toBeVisible({ timeout: 15_000 });
    await expect(connect.getByTestId("connect-warnings")).toContainText(`'${MERGE_SCHEDULE}'`);
    await expect(connect.getByTestId(`connect-merge-${MERGE_SCHEDULE}`)).toBeVisible();
    const workflow = connect.getByTestId("connect-workflow");
    await expect(workflow).toContainText(GITHUB_TRIGGER, { timeout: 15_000 });
    await expect(workflow).toContainText("id-token");
    await connect.getByTestId("connect-done").click();

    const row = adminPage.getByTestId("triggers-table").getByTestId("table-row").filter({ hasText: GITHUB_TRIGGER });
    await expect(row).toBeVisible();
    await expect(row.getByTestId("trigger-kind")).toHaveAttribute("data-kind", "github");
    await expect(row.getByTestId("trigger-state")).toHaveText("enabled");
  });

  test("a key trigger's key is shown once, and the credential it is exchanged for syncs the repository, followed to its end", async ({ adminPage, request }) => {
    await adminPage.goto("/external-triggers");
    await expect(adminPage.getByTestId("page-external-triggers")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await adminPage.getByTestId("open-create-trigger").click();
    const sheet = adminPage.getByTestId("trigger-sheet");
    await sheet.getByTestId("trigger-kind-key").click();
    await sheet.getByTestId("trigger-name").fill(KEY_TRIGGER);
    await sheet.getByTestId("trigger-repo").click();
    await adminPage.getByRole("option", { name: REPO_NAME, exact: true }).click();
    await sheet.getByTestId("trigger-submit").click();

    const connect = adminPage.getByTestId("connect-sheet");
    await expect(connect.getByTestId("connect-key")).toHaveText(/^sqlft_\S{32,}$/, { timeout: 15_000 });
    triggerKey = (await connect.getByTestId("connect-key").textContent())!.trim();
    await connect.getByTestId("connect-done").click();

    // The calling system's two calls: the key for an hour's credential, then a request that only syncs.
    const exchange = await request.post(`${E2E.apiBaseUrl}/api/v1/auth/trigger/key`, { data: { key: triggerKey } });
    expect(exchange.status()).toBe(200);
    const { accessToken } = (await exchange.json()) as { accessToken: string };
    const headers = { Authorization: `Bearer ${accessToken}` };
    const made = await request.post(`${E2E.apiBaseUrl}/api/v1/trigger-requests`, { headers, data: { requestKey: `e2e-${runTag}` } });
    expect(made.status()).toBe(202);
    const asked = (await made.json()) as { id: string; repoName: string; requestedBy: string };
    expect(asked.repoName).toBe(REPO_NAME);
    expect(asked.requestedBy).toBe(`trigger:${KEY_TRIGGER}`);

    // Asking again with the same key answers with the request already made; the credential reaches nothing else.
    const again = await request.post(`${E2E.apiBaseUrl}/api/v1/trigger-requests`, { headers, data: { requestKey: `e2e-${runTag}` } });
    expect(again.status()).toBe(200);
    expect(((await again.json()) as { id: string }).id).toBe(asked.id);
    expect((await request.get(`${E2E.apiBaseUrl}/api/v1/runs`, { headers })).status()).toBe(403);

    await adminPage.getByTestId("external-triggers-tab-requests").click();
    const row = adminPage.getByTestId("requests-table").getByTestId("table-row").filter({ hasText: KEY_TRIGGER });
    await expect(row.getByTestId("request-state")).toHaveText("succeeded", { timeout: 120_000 });
    await expect(row.getByTestId("request-sync")).toHaveAttribute("data-outcome", /^(succeeded|current)$/);

    await row.click();
    await expect(adminPage.getByText(`Asked for a sync of '${REPO_NAME}'`).first()).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("trace-copy")).toBeVisible();
  });

  test("someone without the admin role is not offered the page", async ({ page, request }) => {
    await seedSession(page, await operatorSession(request));
    await seedSideBarOpen(page);
    await page.goto("/");
    await expect(page.getByTestId("page-dashboard")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await expect(page.locator("[data-testid^='nav-']").first()).toBeVisible();
    await expect(page.getByTestId("nav-external-triggers")).toHaveCount(0);
    await page.goto("/external-triggers");
    await expect(page.getByTestId("page-dashboard")).toBeVisible();
  });

  test("a disabled trigger's key is refused, and removed triggers are gone", async ({ adminPage, request }) => {
    expect(triggerKey, "the key trigger's test ran first").not.toBe("");
    await adminPage.goto("/external-triggers");
    await expect(adminPage.getByTestId("page-external-triggers")).toBeVisible({ timeout: FIRST_PAGE_MS });
    const table = adminPage.getByTestId("triggers-table");

    const keyRow = table.getByTestId("table-row").filter({ hasText: KEY_TRIGGER });
    await keyRow.getByTestId("trigger-actions").click();
    await adminPage.getByTestId("trigger-action-toggle").click();
    await expect(keyRow.getByTestId("trigger-state")).toHaveText("disabled", { timeout: 15_000 });
    const refused = await request.post(`${E2E.apiBaseUrl}/api/v1/auth/trigger/key`, { data: { key: triggerKey } });
    expect(refused.status()).toBe(401);

    for (const name of [KEY_TRIGGER, GITHUB_TRIGGER]) {
      const row = table.getByTestId("table-row").filter({ hasText: name });
      await row.getByTestId("trigger-actions").click();
      await adminPage.getByTestId("trigger-action-remove").click();
      await adminPage.getByTestId("confirm-dialog-confirm").click();
      await expect(row).toHaveCount(0, { timeout: 15_000 });
    }
  });
});
