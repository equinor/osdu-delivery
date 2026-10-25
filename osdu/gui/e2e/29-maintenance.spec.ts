import type { APIRequestContext, Page } from "@playwright/test";
import { E2E } from "../playwright.config";
import { adminSession, apiLogin, expect, FIRST_PAGE_MS, seedSession, test } from "./helpers";

// Settings > Maintenance (sqlflow/docs/reference/concepts/maintenance.md): the database in storage areas, and the safe
// purge, Clean up. Its counts are previewed for anyone signed in; only an admin cleans up. Nothing here removes anything,
// since every spec shares the estate's database: the cleanup itself is covered by the API suites.

/** A count the panel shows for an area: how many of its unit would go, or that nothing is that old. */
const COUNTED = /^(\d[\d\s,.\u202f]*\s+(statements|events|attempts|logs|runs)|nothing older than .+)$/;

const OPERATOR = { username: "e2e-maintenance-operator", password: "E2e-maintenance-operator-1!" };

/** An operator account (no admin role), made once through the admin's API; a run that finds it made goes on. */
async function operatorSession(request: APIRequestContext) {
  const admin = await adminSession(request);
  const created = await request.post(`${E2E.apiBaseUrl}/api/v1/users`, {
    headers: { Authorization: `Bearer ${admin.token}` },
    data: { ...OPERATOR, role: "operator", email: null, displayName: "E2E maintenance operator" },
  });
  expect([201, 409]).toContain(created.status());
  return apiLogin(request, OPERATOR.username, OPERATOR.password);
}

async function openCleanUp(page: Page) {
  await page.goto("/settings/maintenance");
  await expect(page.getByTestId("maintenance-page")).toBeVisible({ timeout: FIRST_PAGE_MS });
  await expect(page.getByTestId("maintenance-summary")).toBeVisible({ timeout: 60_000 });
  await page.getByTestId("maintenance-cleanup").click();
  await expect(page.getByTestId("cleanup-sheet")).toBeVisible();
}

/** Every area the panel lists answers with a count, without anyone pressing anything. */
async function expectEveryAreaCounted(page: Page) {
  const counts = page.getByTestId("cleanup-sheet").locator("[data-testid^='cleanup-count-']");
  await expect(counts.first()).toBeVisible();
  const total = await counts.count();
  expect(total).toBeGreaterThanOrEqual(5);
  for (let i = 0; i < total; i++) {
    await expect(counts.nth(i)).toHaveText(COUNTED, { timeout: 30_000 });
  }
}

test.describe("Maintenance", () => {
  test("Clean up counts what each area would remove, safe areas ticked and optional ones not, and an admin may run it", async ({ adminPage }) => {
    await openCleanUp(adminPage);
    await expectEveryAreaCounted(adminPage);

    await expect(adminPage.getByTestId("cleanup-row-sqlflow.run-statements").getByRole("checkbox")).toBeChecked();
    await expect(adminPage.getByTestId("cleanup-row-osdu.attempts").getByRole("checkbox")).not.toBeChecked();
    await expect(adminPage.getByTestId("cleanup-admin-only")).toHaveCount(0);
    await expect(adminPage.getByTestId("cleanup-summary")).not.toHaveText("");

    // Another age counts again at once.
    await adminPage.getByTestId("cleanup-age-365").click();
    await expectEveryAreaCounted(adminPage);
  });

  test("someone without the admin role sees the same counts and is told only an admin cleans up", async ({ page, request }) => {
    await seedSession(page, await operatorSession(request));
    await openCleanUp(page);
    await expectEveryAreaCounted(page);
    await expect(page.getByTestId("cleanup-admin-only")).toBeVisible();
    await expect(page.getByTestId("cleanup-start")).toBeDisabled();
  });
});
