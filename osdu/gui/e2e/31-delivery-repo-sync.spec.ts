import { REPO_NAME } from "./global-setup";
import { expect, FIRST_PAGE_MS, test } from "./helpers";

// Syncing a repository from the Delivery page (osdu/docs/reference/concepts/gui.md, Delivery). The first test syncs the
// estate's repository for real; the sync reads the commit the estate already holds, so the specs after this one see it
// unchanged. The others answer the list themselves, for the standings the estate does not have.

const SOURCES = "**/api/v1/repos/sources?*";

/** A source as the control plane lists it, synced and healthy unless `fields` say otherwise. */
function source(name: string, fields: Record<string, unknown> = {}) {
  return {
    id: `00000000-0000-0000-0000-00000000000${name.length % 10}`,
    name,
    remoteUrl: "https://git.example.org/estate.git",
    localPath: null,
    branch: "main",
    enabled: true,
    syncIntervalSeconds: 300,
    nextSyncUtc: null,
    lastSyncUtc: "2026-10-30T10:00:00Z",
    lastSyncedSha: "abcdef0123456789",
    lastError: null,
    credentialReference: null,
    credentialUsername: null,
    excludedFlowPaths: [],
    createdUtc: "2026-10-01T10:00:00Z",
    updatedUtc: "2026-10-30T10:00:00Z",
    syncRequestedUtc: null,
    syncStartedUtc: null,
    syncPending: false,
    ...fields,
  };
}

const listing = (items: unknown[]) => ({ json: { items, page: 1, pageSize: 200, total: items.length } });

test.describe("Syncing a repository from the Delivery page", () => {
  test("syncs the repository picked, shows its trace, reads the flows again and keeps the pick", async ({ adminPage }) => {
    await adminPage.goto("/delivery");
    await expect(adminPage.getByTestId("page-delivery")).toBeVisible({ timeout: FIRST_PAGE_MS });

    await adminPage.getByTestId("delivery-repo-sync-pick").click();
    await adminPage.getByTestId(`delivery-repo-sync-option-${REPO_NAME}`).click();
    await expect(adminPage.getByTestId("delivery-repo-sync-pick")).toHaveText(REPO_NAME);
    await expect(adminPage.getByTestId("delivery-flows")).toBeVisible();

    const flowsReadAgain = adminPage.waitForResponse(
      (response) => response.url().includes("/api/v1/pipelines") && response.url().includes("kind=delivery"),
      { timeout: 120_000 },
    );
    await adminPage.getByTestId("delivery-repo-sync-now").click();
    await expect(adminPage.getByText(`${REPO_NAME} synced to`)).toBeVisible({ timeout: 120_000 });
    await expect(adminPage.getByTestId("trace-copy")).toBeVisible();
    await flowsReadAgain;
    await expect(adminPage.getByTestId("delivery-repo-sync-now")).toBeEnabled();

    await adminPage.reload();
    await expect(adminPage.getByTestId("delivery-repo-sync-pick")).toHaveText(REPO_NAME, { timeout: FIRST_PAGE_MS });
  });

  test("lists a paused repository without offering it, and leads to a failed sync's trace", async ({ adminPage }) => {
    await adminPage.route(SOURCES, (route) => route.fulfill(listing([
      source("a-paused", { enabled: false }),
      source("broken", { lastError: "The remote refused the credential." }),
      source("healthy"),
    ])));
    await adminPage.goto("/delivery");
    await expect(adminPage.getByTestId("delivery-repo-sync-pick")).toHaveText("broken", { timeout: FIRST_PAGE_MS });

    const error = adminPage.getByTestId("delivery-repo-sync-error");
    await error.hover();
    await expect(adminPage.getByRole("tooltip")).toContainText("The remote refused the credential.");
    await error.click();
    await expect(adminPage.getByTestId("trace-copy")).toBeVisible();

    await adminPage.getByTestId("delivery-repo-sync-pick").click();
    await expect(adminPage.getByTestId("delivery-repo-sync-option-a-paused")).toHaveAttribute("data-disabled", "");
    await expect(adminPage.getByTestId("delivery-repo-sync-option-a-paused")).toContainText("paused");
    await expect(adminPage.getByTestId("delivery-repo-sync-option-broken")).toContainText("the last sync failed");
    await adminPage.getByTestId("delivery-repo-sync-option-healthy").click();
    await expect(adminPage.getByTestId("delivery-repo-sync-error")).toHaveCount(0);
  });

  test("reads the flows again when a sync that outlasted the server's wait ends", async ({ adminPage }) => {
    let items = [source("slow")];
    await adminPage.route(SOURCES, (route) => route.fulfill(listing(items)));
    await adminPage.route("**/api/v1/repos/sources/*/sync", (route) => {
      items = [source("slow", { syncPending: true })];
      return route.fulfill({ status: 202, json: items[0] });
    });
    await adminPage.goto("/delivery");
    await expect(adminPage.getByTestId("delivery-repo-sync-pick")).toHaveText("slow", { timeout: FIRST_PAGE_MS });

    await adminPage.getByTestId("delivery-repo-sync-now").click();
    await expect(adminPage.getByText("The sync of slow is still running")).toBeVisible();
    await expect(adminPage.getByTestId("delivery-repo-sync-now")).toHaveText("Syncing");
    await expect(adminPage.getByTestId("delivery-repo-sync-now")).toBeDisabled();

    const flowsReadAgain = adminPage.waitForResponse(
      (response) => response.url().includes("/api/v1/pipelines") && response.url().includes("kind=delivery"),
    );
    items = [source("slow", { lastSyncUtc: "2026-10-30T10:05:00Z", lastSyncedSha: "fedcba9876543210" })];
    await flowsReadAgain;
    await expect(adminPage.getByTestId("delivery-repo-sync-now")).toBeEnabled();
  });

  test("keeps the record search first in the page's header", async ({ adminPage }) => {
    await adminPage.route(SOURCES, (route) => route.fulfill(listing([source("healthy")])));
    await adminPage.goto("/delivery");
    await expect(adminPage.getByTestId("delivery-repo-sync")).toBeVisible({ timeout: FIRST_PAGE_MS });

    const syncFollows = await adminPage.getByTestId("delivery-find-record").evaluate((search) => {
      const sync = document.querySelector("[data-testid=delivery-repo-sync]");
      return sync !== null && (search.compareDocumentPosition(sync) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;
    });
    expect(syncFollows).toBe(true);
  });

  test("asks nothing of a catalog with no repository, whose empty state says how to add one", async ({ adminPage }) => {
    await adminPage.route(SOURCES, (route) => route.fulfill(listing([])));
    await adminPage.goto("/delivery");
    await expect(adminPage.getByTestId("page-delivery")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await expect(adminPage.getByTestId("delivery-find-record")).toBeVisible();
    await expect(adminPage.getByTestId("delivery-repo-sync")).toHaveCount(0);
  });
});
