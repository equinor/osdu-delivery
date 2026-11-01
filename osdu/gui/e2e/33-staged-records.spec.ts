import { E2E } from "../playwright.config";
import { LOG_SOURCE } from "./global-setup";
import { expect, test } from "./helpers";

// A mapping's staged records, before anything is delivered. A mapping is found by a search and opens on a page of its own, on
// the rows of a flow that renders with it and the first one's document, any other a click away, and a dry run takes them
// through everything a delivery does short of sending them, holding each document to the template and to the partition's
// Schema service.
//
// Nothing here reaches an OSDU, and nothing is delivered: the flows reach the e2e stand-in, which holds a schema of the kind
// for as long as the spec runs.

const LOG_KIND = "osdu:wks:work-product-component--WellLog:1.4.0";

/** What the stand-in's Schema service holds for the well log's kind: a name that starts with "WellDB ", which no sample log's does. */
const LOG_SCHEMA = {
  $schema: "http://json-schema.org/draft-07/schema#",
  "x-osdu-schema-source": LOG_KIND,
  type: "object",
  required: ["kind", "acl", "legal"],
  properties: {
    kind: { type: "string" },
    data: { type: "object", properties: { Name: { type: "string", pattern: "^WellDB " } } },
  },
};

test.describe.serial("a mapping's staged records and a dry run", () => {
  test.beforeAll(async ({ playwright }) => {
    const standIn = await playwright.request.newContext();
    try {
      expect((await standIn.put(`${E2E.osdu.OSDU_URL}/__e2e/schemas`, { data: { id: LOG_KIND, schema: LOG_SCHEMA } })).ok()).toBe(true);
    } finally {
      await standIn.dispose();
    }
  });

  test.afterAll(async ({ playwright }) => {
    const standIn = await playwright.request.newContext();
    try {
      await standIn.delete(`${E2E.osdu.OSDU_URL}/__e2e/schemas`);
    } finally {
      await standIn.dispose();
    }
  });

  test("the rows are listed as they stand, a pick shows its document, and a dry run says what each comes to", async ({ adminPage }) => {
    test.setTimeout(300_000);
    await adminPage.getByTestId("nav-delivery-documents").click();
    const row = adminPage.getByTestId("delivery-mappings-table").getByTestId("table-row").filter({ hasText: "WellLog@1.4.0" }).first();
    await expect(row).toBeVisible({ timeout: 30_000 });

    // A search finds it by what its row shows, the field has the cursor, and Enter opens the first row: a page, not a sheet.
    await expect(adminPage.getByTestId("delivery-documents-search")).toBeFocused();
    await adminPage.keyboard.type("welllog 1.4");
    await expect(adminPage.getByTestId("delivery-mappings-table").getByTestId("table-row").first()).toContainText("WellLog@1.4.0");
    await adminPage.keyboard.press("Enter");
    await expect(adminPage.getByTestId("page-delivery-mapping")).toBeVisible({ timeout: 30_000 });
    await expect(adminPage).toHaveURL(/\/delivery\/documents\/[^/]+$/);
    await expect(adminPage.getByTestId("delivery-mapping-name")).toHaveText("WellLog@1.4.0");
    await expect(adminPage.getByTestId("nav-delivery-documents")).toHaveAttribute("aria-current", "page");
    const sheet = adminPage.getByTestId("delivery-mapping-detail");

    // The flow's scope needs its log source, which it declares required. The page starts on the value the table holds most,
    // so the tab opens on the rows and the first one's document, with no refusal on the way there.
    await sheet.getByTestId("delivery-mapping-tab-staged").click();
    await expect(sheet.getByTestId("staged-records")).toBeVisible({ timeout: 30_000 });
    await expect(sheet.getByTestId("value-check-parameter-logSource-picked")).toHaveAttribute("data-value", /\S/, { timeout: 60_000 });
    const lines = sheet.getByTestId("staged-line");
    await expect(lines.first()).toBeVisible({ timeout: 60_000 });
    await expect(lines.first()).toHaveAttribute("data-outcome", "none");
    await expect(lines.first()).toHaveAttribute("aria-selected", "true");
    await expect(sheet.getByTestId("preview-document")).toContainText("work-product-component--WellLog", { timeout: 60_000 });
    await expect(sheet.getByTestId("staged-rows-error")).toHaveCount(0);
    await expect(sheet.getByTestId("preview-schema-note")).toBeVisible();

    // A field emptied stays empty: the reader is told why there are no rows, and the mapping as written is still there to read.
    await sheet.getByTestId("value-check-parameter-logSource").press("Backspace");
    await expect(sheet.getByTestId("staged-blocked")).toBeVisible({ timeout: 30_000 });
    await expect(sheet.getByTestId("value-check-all")).toBeDisabled();
    await expect(sheet.getByTestId("staged-rows-error")).toHaveCount(0);
    await sheet.getByTestId("value-check-parameter-logSource").fill(LOG_SOURCE);
    await sheet.getByTestId("value-check-parameter-logSource").press("Escape");
    await expect(lines.first()).toBeVisible({ timeout: 60_000 });

    // The mapping as written and the shape it gives a record are pages of the same section as the record.
    await sheet.getByTestId("delivery-mapping-tab-yaml").click();
    // The editor draws only the lines in view, so the check reads the header comment the sample opens on.
    await expect(sheet.getByTestId("delivery-mapping-yaml")).toContainText("well database logs into the WellLog", { timeout: 30_000 });
    await sheet.getByTestId("delivery-mapping-tab-shape").click();
    await expect(sheet.getByTestId("delivery-mapping-shape-json")).toContainText("delivery key", { timeout: 30_000 });

    // Picking a record shows it, from whichever page the reader was on.
    await lines.nth(1).click();
    await expect(sheet.getByTestId("preview-document")).toContainText("work-product-component--WellLog", { timeout: 60_000 });
    await expect(lines.nth(1)).toHaveAttribute("aria-selected", "true");

    // The handle between the records and the code gives either more room, and the width is kept.
    const handle = sheet.locator("[data-slot=resizable-handle]").first();
    const before = (await handle.boundingBox())!;
    await adminPage.mouse.move(before.x + 1, before.y + before.height / 2);
    await adminPage.mouse.down();
    await adminPage.mouse.move(before.x - 60, before.y + before.height / 2, { steps: 6 });
    await adminPage.mouse.up();
    await expect.poll(async () => Math.round((await handle.boundingBox())!.x)).toBeLessThan(Math.round(before.x) - 30);
    await expect.poll(() => adminPage.evaluate(() => localStorage.getItem("sqlflow.delivery.mapping.records.layout"))).toContain("staged-list");

    await sheet.getByTestId("preview-tab-schema").click();
    await expect(sheet.getByTestId("schema-check")).toBeVisible();

    // A search asks the table, and finds a row that is not on the page.
    await sheet.getByTestId("staged-search").fill("no such log anywhere");
    await expect(sheet.getByTestId("staged-empty")).toContainText("No row holds", { timeout: 30_000 });
    await sheet.getByTestId("staged-search-clear").click();
    await expect(lines.first()).toBeVisible({ timeout: 30_000 });

    // A dry run says what became of every row; nothing is sent.
    await sheet.getByTestId("value-check-all").click();
    await expect(sheet.getByTestId("dry-run-verdict")).toContainText("rows", { timeout: 240_000 });
    await expect(sheet.getByTestId("dry-run-verdict")).toContainText("Nothing was sent");
    await expect(lines.first()).not.toHaveAttribute("data-outcome", "none");

    // The sample's names break the rule only the service's schema states, which the template knows nothing of.
    await expect(sheet.getByTestId("dry-run-schema")).toHaveAttribute("data-state-name", "differs");
    await expect(sheet.getByTestId("dry-run-differences")).toBeVisible();
    await expect(sheet.getByTestId("dry-run-rules")).toContainText("data.Name");

    // Narrowing to what is worth a look lists those rows, and a rule's example opens its record.
    await sheet.getByTestId("dry-run-outcome-attention").click();
    await expect(lines.first()).toHaveAttribute("data-outcome", "attention");
    await sheet.getByTestId("dry-run-outcome-attention").click();
    await sheet.getByTestId("delivery-mapping-tab-findings").click();
    await sheet.getByTestId("dry-run-rule-example").first().click();
    await expect(sheet.getByTestId("preview-result")).toBeVisible({ timeout: 60_000 });

    // Properties reads the same run, attribute by attribute, without running it again.
    await sheet.getByTestId("delivery-mapping-tab-coverage").click();
    await expect(sheet.getByTestId("value-check-summary")).toBeVisible({ timeout: 30_000 });

    // An attribute's SQL names the source table, and after a run it narrows to the rows the run found wrong with it.
    const properties = sheet.getByTestId("delivery-mapping-coverage");
    await properties.getByTestId("templates-view-variable-osdu.data.Name").click();
    await properties.getByTestId("templates-view-properties-tab-sql").click();
    await expect(properties.getByTestId("variable-sql-reads")).toContainText("dataset.", { timeout: 30_000 });
    await expect(properties.getByTestId("variable-sql")).toContainText("SELECT", { timeout: 30_000 });
    await expect(properties.getByTestId("variable-sql")).toContainText("FROM", { timeout: 30_000 });

    // Name fails no check here, so it offers no failing rows. An attribute the run found wrong does.
    await expect(properties.getByTestId("variable-sql-rows-failing")).toHaveCount(0);
    await properties.getByTestId("templates-view-show-failing").click();
    await properties.locator('[data-testid^="templates-view-variable-"]').last().click();
    await properties.getByTestId("templates-view-properties-tab-sql").click();
    await properties.getByTestId("variable-sql-rows-failing").click();
    await expect(properties.getByTestId("variable-sql")).toContainText("OPENJSON", { timeout: 30_000 });
    await properties.getByTestId("variable-sql-rows-all").click();
    await expect(properties.getByTestId("variable-sql")).not.toContainText("OPENJSON", { timeout: 30_000 });
    await sheet.getByTestId("delivery-mapping-tab-staged").click();
    await expect(sheet.getByTestId("dry-run-verdict")).toBeVisible();

    // The page is its address: a reload lands on it again, another mapping is a pick away, and the way back is the list.
    await adminPage.reload();
    await expect(adminPage.getByTestId("delivery-mapping-name")).toHaveText("WellLog@1.4.0", { timeout: 30_000 });
    await adminPage.getByTestId("delivery-mapping-picker").click();
    await expect(adminPage.getByTestId("delivery-mapping-picker-option").first()).toBeVisible({ timeout: 30_000 });
    await adminPage.keyboard.press("Escape");
    await adminPage.getByTestId("delivery-mapping-back").click();
    await expect(adminPage.getByTestId("page-delivery-documents")).toBeVisible({ timeout: 30_000 });
  });
});
