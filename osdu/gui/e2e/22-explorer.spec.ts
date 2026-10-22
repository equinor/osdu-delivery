import type { Locator, Page } from "@playwright/test";
import { E2E } from "../playwright.config";
import { expect, test } from "./helpers";

// The explorer: what the partition of OSDU holds, read live through the partition's own connection, and nothing
// the delivery system keeps. It browses by type, searches by name and by id, reads a record under the place it sits in,
// follows the records that mention it, compares two of its versions side by side, and offers the ids near one OSDU holds
// nothing under.
//
// Nothing here reaches an OSDU: the partition's connection reaches the e2e stand-in, which holds the platform's wellbores
// and the well log this spec gives it, in two versions, for as long as the spec runs. Nothing is delivered and nothing is
// written.

/** The well log this spec holds in the stand-in: written twice, so OSDU keeps two versions of it. */
const LOG = `${E2E.osdu.OSDU_DATA_PARTITION}:work-product-component--WellLog:e2e-explorer-log`;

/** The wellbore the log names, one of the wellbores the stand-in holds. */
const WELLBORE = `${E2E.osdu.OSDU_DATA_PARTITION}:master-data--Wellbore:Wellbore-B-2-B`;

/**
 * What the list in view is read by, as sent to the search service: its popover is opened, `check` runs on it, and it is
 * closed again. A list is read before its query is asked, so a caller waits for its rows first.
 */
async function sent(page: Page, check: (panel: Locator) => Promise<void>) {
  await page.getByTestId("explorer-sent-toggle").click();
  await check(page.getByTestId("explorer-sent"));
  await page.keyboard.press("Escape");
  await expect(page.getByTestId("explorer-sent")).toHaveCount(0);
}

test.describe.serial("explorer", () => {
  test.beforeAll(async ({ playwright }) => {
    const standIn = await playwright.request.newContext();
    try {
      for (const [name, source] of [["e2e explorer log", "COMP"], ["e2e explorer log", "COMPOSITE"]]) {
        const held = await standIn.put(`${E2E.osdu.OSDU_URL}/__e2e/records`, {
          data: {
            id: LOG,
            kind: "osdu:wks:work-product-component--WellLog:1.4.0",
            acl: { viewers: [E2E.osdu.OSDU_ACL_VIEWER], owners: [E2E.osdu.OSDU_ACL_OWNER] },
            legal: { legaltags: [E2E.osdu.OSDU_LEGAL_TAG], otherRelevantDataCountries: ["US"], status: "compliant" },
            data: { Name: name, LogSource: source, WellboreID: `${WELLBORE}:` },
            createUser: "e2e-stand-in",
            createTime: "2026-09-25T00:00:00.000Z",
          },
        });
        expect(held.ok()).toBe(true);
      }
    } finally {
      await standIn.dispose();
    }
  });

  test.afterAll(async ({ playwright }) => {
    // The stand-in forgets the record this spec gave it, so a later spec meets the platform as the suite starts it.
    const standIn = await playwright.request.newContext();
    try {
      await standIn.delete(`${E2E.osdu.OSDU_URL}/__e2e/records`);
    } finally {
      await standIn.dispose();
    }
  });

  test("opens on a welcome, then browses the partition by type and searches it by name", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-explorer").click();
    await expect(adminPage.getByTestId("page-delivery-explorer")).toBeVisible();
    await expect(adminPage.getByTestId("explorer-live")).toContainText(E2E.osdu.OSDU_DATA_PARTITION, { timeout: 30_000 });

    // Nothing is read from OSDU until the reader asks: the welcome, and no list of types or records behind it.
    await expect(adminPage.getByTestId("explorer-welcome")).toBeVisible();
    await expect(adminPage.getByTestId("explorer-types")).toHaveCount(0);
    await adminPage.getByTestId("explorer-browse-types").click();
    await expect(adminPage.getByTestId("explorer-pick-type")).toBeVisible();

    // The kinds the partition holds, counted by one aggregation: its wellbores and the log this spec holds. The groups start
    // folded, and a group unfolded lists its types.
    const types = adminPage.getByTestId("explorer-types");
    await expect(types.getByTestId("explorer-type-all")).toContainText("8", { timeout: 60_000 });
    await expect(types.getByTestId("explorer-type")).toHaveCount(0);
    await types.getByTestId("explorer-type-group").filter({ hasText: "master-data" }).getByRole("button", { name: "Unfold master-data" }).click();
    const wellbores = types.getByTestId("explorer-type").filter({ hasText: "Wellbore" });
    await expect(wellbores).toContainText("7");

    // A type narrows the grid to its records, and the place says where they are.
    await wellbores.getByRole("button").last().click();
    const grid = adminPage.getByTestId("explorer-grid");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(7, { timeout: 60_000 });
    await expect(adminPage.getByTestId("explorer-place")).toContainText("Wellbore");
    await expect(adminPage.getByTestId("explorer-count")).toContainText("7 records");

    // What the list is read by, as sent to the search service, one click away: the type's kind, and no query.
    await sent(adminPage, async (panel) => {
      await expect(panel.getByTestId("explorer-sent-kind")).toHaveText("*:*:master-data--Wellbore:*");
      await expect(panel.getByTestId("explorer-sent-query")).toContainText("every record of the kind");
    });

    // The one search field searches the place in view: a name typed finds the records of the type holding it.
    const field = adminPage.getByTestId("explorer-search-input");
    await expect(field).toHaveAttribute("placeholder", "Search Wellbore by id, name or any text");
    await field.fill("Wellbore B-2");
    await expect(adminPage.getByTestId("explorer-search-hint")).toContainText("search");
    await field.press("Enter");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(2, { timeout: 60_000 });
    await expect(grid).toContainText("Wellbore B-2 A");
    await expect(grid).toContainText("Wellbore B-2 B");
    await expect(field).toHaveValue("Wellbore B-2");
    await sent(adminPage, async (panel) => {
      await expect(panel.getByTestId("explorer-sent-kind")).toHaveText("*:*:master-data--Wellbore:*");
      await expect(panel.getByTestId("explorer-sent-query")).toContainText("Wellbore B-2");
    });

    // A property's values group the records, and a value narrows them to it.
    await adminPage.getByTestId("explorer-group-by").click();
    await adminPage.locator('[data-testid="explorer-group-by-attributes-field"][data-path="data.FacilityName"]').click({ timeout: 60_000 });
    await adminPage.getByTestId("explorer-group-by-value").filter({ hasText: "Wellbore B-2 B" }).click({ timeout: 60_000 });
    await expect(adminPage.getByTestId("explorer-filter")).toContainText("Wellbore B-2 B");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(1, { timeout: 60_000 });

    // The value narrowed to is a clause of the query sent, which Edit takes into the search field as Lucene, the cursor at
    // its end, so it is edited at once.
    await adminPage.getByTestId("explorer-sent-toggle").click();
    await expect(adminPage.getByTestId("explorer-sent-query")).toContainText('data.FacilityName.keyword:"Wellbore B-2 B"');
    await adminPage.getByTestId("explorer-sent-edit").click();
    await expect(field).toHaveValue(/data\.FacilityName\.keyword:"Wellbore B-2 B"/);
    await expect(field).toBeFocused();
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(1, { timeout: 60_000 });

    // The cross clears the search, not only the field: every record of the type again.
    await adminPage.getByTestId("explorer-search-clear").click();
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(7, { timeout: 60_000 });
    await expect(field).toHaveValue("");
    await sent(adminPage, async (panel) => {
      await expect(panel.getByTestId("explorer-sent-query")).toContainText("every record of the kind");
    });

    // Escape clears a search the same way.
    await field.fill("Wellbore B-2");
    await field.press("Enter");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(2, { timeout: 60_000 });
    await field.press("Escape");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(7, { timeout: 60_000 });

    // All types is a place like any other: the field then searches every type.
    await types.getByTestId("explorer-type-all").getByRole("button").click();
    await expect(field).toHaveAttribute("placeholder", "Search every type by id, name or any text");
    await field.fill("Wellbore B-2 B");
    await field.press("Enter");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(1, { timeout: 60_000 });
    await sent(adminPage, async (panel) => {
      await expect(panel.getByTestId("explorer-sent-kind")).toHaveText("*:*:*:*");
    });
    await expect(field).toHaveValue("Wellbore B-2 B");

    // Cleared, it lists every record of every type, rather than going back to the welcome.
    await adminPage.getByTestId("explorer-search-clear").click();
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(8, { timeout: 60_000 });
    await sent(adminPage, async (panel) => {
      await expect(panel.getByTestId("explorer-sent-query")).toContainText("every record of the kind");
      await expect(panel.getByTestId("explorer-sent-kind")).toHaveText("*:*:*:*");
    });
    await expect(adminPage.getByTestId("explorer-welcome")).toHaveCount(0);
  });

  test("searches text in one property, and narrows by conditions changed in place, each property a column", async ({ adminPage }) => {
    await adminPage.goto("/delivery/explorer?kind=*:*:master-data--Wellbore:*");
    const grid = adminPage.getByTestId("explorer-grid");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(7, { timeout: 60_000 });

    // Text typed is searched in every property on Enter; under the field, it is offered in one property instead.
    const field = adminPage.getByTestId("explorer-search-input");
    await field.fill("b-2");
    const options = adminPage.getByTestId("explorer-search-in");
    await expect(options.getByTestId("explorer-search-in-everywhere")).toContainText("Search every property for b-2");
    await expect(options.getByTestId("explorer-search-in-choose")).toContainText("Pick another property", { timeout: 60_000 });
    await options.getByTestId("explorer-search-in-field").filter({ hasText: "FacilityName" }).click();

    // The text is a condition now, its words in any case, and no longer the field's search.
    const chips = adminPage.getByTestId("explorer-filter");
    await expect(chips).toHaveCount(1);
    await expect(chips.first()).toContainText("FacilityName contains b-2");
    await expect(field).toHaveValue("");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(2, { timeout: 60_000 });
    await sent(adminPage, async (panel) => {
      await expect(panel.getByTestId("explorer-sent-query")).toHaveText('data.FacilityName:"b-2"');
    });

    // A chip opens the editor to change it: every wellbore but one, picked from the values the wellbores hold. A query that
    // only excludes starts from every record, since the search service refuses one that does not.
    await chips.first().getByTestId("explorer-filter-edit").click();
    const editor = adminPage.getByTestId("explorer-filter-editor");
    await editor.getByTestId("explorer-filter-condition").click();
    await adminPage.getByTestId("explorer-filter-condition-option").filter({ hasText: /^is not$/ }).click();
    await editor.getByTestId("explorer-filter-held-value").filter({ hasText: "Wellbore B-2 B" }).click({ timeout: 60_000 });
    await expect(editor.getByTestId("explorer-filter-value")).toHaveValue("Wellbore B-2 B");
    await editor.getByTestId("explorer-filter-apply").click();
    await expect(chips.first()).toContainText("FacilityName is not Wellbore B-2 B");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(6, { timeout: 60_000 });
    await sent(adminPage, async (panel) => {
      await expect(panel.getByTestId("explorer-sent-query")).toHaveText('_exists_:id AND NOT (data.FacilityName.keyword:"Wellbore B-2 B")');
    });

    // Filter adds a condition, the property found by part of its name: the wellbores whose name starts with Wellbore B.
    await adminPage.getByTestId("explorer-add-filter").click();
    await adminPage.getByTestId("explorer-filter-attributes-find").fill("facility");
    await adminPage.locator('[data-testid="explorer-filter-attributes-field"][data-path="data.FacilityName"]').click({ timeout: 60_000 });
    await editor.getByTestId("explorer-filter-condition").click();
    await adminPage.getByTestId("explorer-filter-condition-option").filter({ hasText: "starts with" }).click();
    await editor.getByTestId("explorer-filter-value").fill("Wellbore B");
    await editor.getByTestId("explorer-filter-value").press("Enter");
    await expect(chips).toHaveCount(2);
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(2, { timeout: 60_000 });
    await expect(grid).toContainText("Wellbore B-1");
    await expect(grid).toContainText("Wellbore B-2 A");

    // The conditions are the page's address, so a link brings them back; Clear all drops them.
    await adminPage.reload();
    await expect(chips).toHaveCount(2, { timeout: 60_000 });
    await adminPage.getByTestId("explorer-filters-clear").click();
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(7, { timeout: 60_000 });

    // The field searches one property when one is picked at its start: the values it holds are listed as a value is
    // typed, one picked is asked whole, and a value searched again replaces it.
    await adminPage.getByTestId("explorer-search-scope").click();
    await adminPage.getByTestId("explorer-search-scope-attributes-find").fill("facility");
    await adminPage.locator('[data-testid="explorer-search-scope-attributes-field"][data-path="data.FacilityName"]').click({ timeout: 60_000 });
    await expect(adminPage.getByTestId("explorer-search-scope-name")).toHaveText("FacilityName");
    await expect(field).toBeFocused();
    await expect(field).toHaveAttribute("placeholder", "Type a value of FacilityName");
    await field.fill("Wellbore B-2");
    await expect(options.getByTestId("explorer-search-in-scoped")).toContainText("FacilityName contains Wellbore B-2");
    await options.getByTestId("explorer-search-in-held").filter({ hasText: "Wellbore B-2 B" }).click({ timeout: 60_000 });
    await expect(chips).toHaveCount(1);
    await expect(chips.first()).toContainText("FacilityName is Wellbore B-2 B");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(1, { timeout: 60_000 });
    await field.fill("Wellbore B-2 A");
    await field.press("Enter");
    await expect(chips).toHaveCount(1);
    await expect(chips.first()).toContainText("FacilityName contains Wellbore B-2 A");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(1, { timeout: 60_000 });
    await expect(grid).toContainText("Wellbore B-2 A");

    // Every property again: the field searches as it always does.
    await adminPage.getByTestId("explorer-search-scope").click();
    await adminPage.getByTestId("explorer-search-scope-every").click();
    await expect(field).toHaveAttribute("placeholder", "Search Wellbore by id, name or any text");
    await chips.first().getByTestId("explorer-filter-drop").click();
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(7, { timeout: 60_000 });

    // A property a condition asks is a column, so the grid shows why each record is there: the log's source.
    await adminPage.goto("/delivery/explorer?kind=*:*:work-product-component--WellLog:*");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(1, { timeout: 60_000 });
    await adminPage.getByTestId("explorer-add-filter").click();
    await adminPage.locator('[data-testid="explorer-filter-attributes-field"][data-path="data.LogSource"]').click({ timeout: 60_000 });
    await editor.getByTestId("explorer-filter-condition").click();
    await adminPage.getByTestId("explorer-filter-condition-option").filter({ hasText: /^is$/ }).click();
    await editor.getByTestId("explorer-filter-held-value").filter({ hasText: "COMPOSITE" }).click({ timeout: 60_000 });
    await editor.getByTestId("explorer-filter-apply").click();
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(1, { timeout: 60_000 });
    await expect(grid).toContainText("LogSource");
    await expect(grid.getByTestId("explorer-grid-row").first()).toContainText("COMPOSITE");
    await chips.first().getByTestId("explorer-filter-drop").click();
    await expect(chips).toHaveCount(0);
  });

  test("reads a record under its place, follows what mentions it, and compares two versions of that", async ({ adminPage }) => {
    // A whole id typed opens its record straight away.
    await adminPage.goto("/delivery/explorer");
    await adminPage.getByTestId("explorer-search-input").fill(`${WELLBORE}:`);
    await expect(adminPage.getByTestId("explorer-search-hint")).toContainText("open");
    await adminPage.getByTestId("explorer-search-input").press("Enter");
    const record = adminPage.getByTestId("explorer-record");
    await expect(record.getByTestId("osdu-record-name")).toHaveText("Wellbore B-2 B", { timeout: 60_000 });
    await expect(record.getByTestId("explorer-record-place")).toContainText("master-data");
    await expect(record.getByTestId("explorer-record-place")).toContainText("Wellbore");

    // The records that name it: the log, opened on the trail after it.
    await record.getByTestId("osdu-outline-mentions").click();
    await expect(record.getByTestId("explorer-mentions")).toContainText("1 record", { timeout: 60_000 });
    await record.getByTestId("explorer-mention-item").filter({ hasText: "e2e explorer log" }).click();
    const linked = record.getByTestId("osdu-linked");
    await expect(linked.getByTestId("osdu-record-json")).toContainText("e2e explorer log", { timeout: 60_000 });

    // OSDU keeps two versions of the log: Compare puts them side by side, with the one value that moved.
    await linked.getByTestId("osdu-version-compare-toggle").click();
    const compare = adminPage.getByTestId("osdu-version-compare-dialog");
    await expect(compare.getByTestId("osdu-version-compare-counts")).toContainText("1 changed", { timeout: 60_000 });
    await expect(compare.getByTestId("osdu-version-differences")).toContainText("data.LogSource");
    await expect(compare.getByTestId("osdu-version-differences")).toContainText("COMPOSITE");
    await adminPage.keyboard.press("Escape");

    // Back where the record was opened from: the welcome, which now lists it among the records opened lately.
    await linked.getByTestId("osdu-linked-close").click();
    await record.getByTestId("explorer-record-back").click();
    await expect(adminPage.getByTestId("explorer-welcome-record").filter({ hasText: "Wellbore B-2 B" })).toBeVisible();
  });

  test("shows how to search for a value of a record, and searches with it", async ({ adminPage }) => {
    await adminPage.goto(`/delivery/explorer?id=${encodeURIComponent(`${WELLBORE}:`)}`);
    const record = adminPage.getByTestId("explorer-record");
    await expect(record.getByTestId("osdu-record-name")).toHaveText("Wellbore B-2 B", { timeout: 60_000 });

    // The wellbore's name: exactly its value by its keyword, with its words and whether a record holds one beside it.
    await record.locator('[data-testid="explorer-element-query"][data-path="data.FacilityName"]').click();
    const panel = adminPage.getByTestId("explorer-element-queries");
    const exact = panel.locator('[data-testid="explorer-element-query-item"][data-purpose="exact"]');
    await expect(exact.getByTestId("explorer-element-lucene")).toHaveText('data.FacilityName.keyword:"Wellbore B-2 B"', { timeout: 30_000 });
    await expect(panel.locator('[data-testid="explorer-element-query-item"][data-purpose="exists"]').getByTestId("explorer-element-lucene")).toHaveText("_exists_:data.FacilityName");

    // How it was written is a tooltip away: the stand-in's wellbores are of a version no synced repository holds a template
    // of, so the newest of the type the repository's template folder holds reads them.
    await panel.getByTestId("explorer-element-about").hover();
    await expect(adminPage.getByRole("tooltip")).toContainText("osdu:wks:master-data--Wellbore:1.3.0");

    // Searching with it finds the wellbore, by a Lucene query in the explorer's own search.
    await exact.getByTestId("explorer-element-search").click();
    const grid = adminPage.getByTestId("explorer-grid");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(1, { timeout: 60_000 });
    await expect(grid).toContainText("Wellbore B-2 B");
  });

  test("offers the ids near one OSDU holds nothing under", async ({ adminPage }) => {
    await adminPage.goto(`/delivery/explorer?id=${encodeURIComponent(`${E2E.osdu.OSDU_DATA_PARTITION}:master-data--Wellbore:Wellbore-B-2`)}`);
    await expect(adminPage.getByTestId("osdu-not-found")).toBeVisible({ timeout: 60_000 });
    const near = adminPage.getByTestId("explorer-near");
    await expect(near.getByTestId("explorer-near-item")).toHaveCount(2, { timeout: 60_000 });
    await near.getByTestId("explorer-near-item").filter({ hasText: "Wellbore B-2 B" }).click();
    await expect(adminPage.getByTestId("osdu-record-name")).toHaveText("Wellbore B-2 B", { timeout: 60_000 });
  });
});
