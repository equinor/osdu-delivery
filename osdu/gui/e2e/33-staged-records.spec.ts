import { E2E } from "../playwright.config";
import { LOG_SOURCE } from "./global-setup";
import { expect, test } from "./helpers";

// A mapping's staged records, before anything is delivered. The sheet opens on the rows of a flow that renders with the
// mapping, any one of them a click from its document, and a dry run takes them through everything a delivery does short of
// sending them, holding each document to the template and to the partition's Schema service.
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
    await row.click();
    const sheet = adminPage.getByTestId("delivery-mapping-detail");

    // The flow's scope needs its log source, which it declares required: without it there are no rows to list.
    await expect(sheet.getByTestId("staged-records")).toBeVisible({ timeout: 30_000 });
    await expect(sheet.getByTestId("staged-blocked")).toBeVisible({ timeout: 30_000 });
    await expect(sheet.getByTestId("value-check-all")).toBeDisabled();
    await sheet.getByTestId("value-check-parameter-logSource").fill(LOG_SOURCE);
    await sheet.getByTestId("value-check-parameter-logSource").press("Escape");

    // The rows, with nothing rendered yet.
    const lines = sheet.getByTestId("staged-line");
    await expect(lines.first()).toBeVisible({ timeout: 60_000 });
    await expect(sheet.getByTestId("staged-intro")).toBeVisible();
    await expect(lines.first()).toHaveAttribute("data-outcome", "none");

    // A row is a click from its document, with what the Schema service says of it.
    await lines.first().click();
    await expect(sheet.getByTestId("preview-document")).toContainText("work-product-component--WellLog", { timeout: 60_000 });
    await expect(sheet.getByTestId("preview-schema-note")).toBeVisible();
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
    await sheet.getByTestId("staged-record-back").click();
    await expect(sheet.getByTestId("dry-run-schema")).toHaveAttribute("data-state-name", "differs");
    await expect(sheet.getByTestId("dry-run-differences")).toBeVisible();
    await expect(sheet.getByTestId("dry-run-rules")).toContainText("data.Name");

    // Narrowing to what is worth a look lists those rows, and a rule's example opens its record.
    await sheet.getByTestId("dry-run-outcome-attention").click();
    await expect(lines.first()).toHaveAttribute("data-outcome", "attention");
    await sheet.getByTestId("dry-run-outcome-attention").click();
    await sheet.getByTestId("dry-run-rule-example").first().click();
    await expect(sheet.getByTestId("preview-result")).toBeVisible({ timeout: 60_000 });

    // Properties reads the same run, attribute by attribute, without running it again.
    await sheet.getByTestId("delivery-mapping-tab-coverage").click();
    await expect(sheet.getByTestId("value-check-summary")).toBeVisible({ timeout: 30_000 });
    await sheet.getByTestId("delivery-mapping-tab-staged").click();
    await expect(sheet.getByTestId("dry-run-verdict")).toBeVisible();
  });
});
