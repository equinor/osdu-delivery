import type { Locator, Page } from "@playwright/test";
import { E2E } from "../playwright.config";
import { expect, test } from "./helpers";

// Building a dimension in the explorer: a build mode of the explorer itself, with the builder docked beside the records.
// The records are browsed and drilled into as ever, and each value is picked where it is: the key in a record of the
// dimension's kind (the likeliest one the kind's template suggests is picked as the build starts), the value and the
// attributes in the records reached from it, link after link. A step through one item of a list asks which items are
// meant and suggests the filter keeping it. The YAML is checked as a flow's own dimension is, and an example row made by
// the build's own labelling, cleaning and counting.
//
// Nothing here reaches an OSDU: the flows reach the e2e stand-in, which holds, for as long as the spec runs, five well
// logs, the two wellbores four of them name (the fifth names one OSDU holds nothing under), the political entities those
// wellbores name, and the service company one log names. Nothing is delivered and nothing is written: the builder makes
// no flow and no dimension.

const PARTITION = E2E.osdu.OSDU_DATA_PARTITION;
const WELLLOG_KIND = "osdu:wks:work-product-component--WellLog:1.4.0";
const WELLBORE_1 = `${PARTITION}:master-data--Wellbore:e2e-builder-1`;
const WELLBORE_2 = `${PARTITION}:master-data--Wellbore:e2e-builder-2`;
/** The wellbore the fifth log names, which the stand-in does not hold. */
const WELLBORE_GONE = `${PARTITION}:master-data--Wellbore:e2e-builder-gone`;
const UNITED_STATES = `${PARTITION}:master-data--GeoPoliticalEntity:e2e-builder-united-states`;
const COUNTY_A = `${PARTITION}:master-data--GeoPoliticalEntity:e2e-builder-county-a`;
const COMPANY = `${PARTITION}:master-data--Organisation:e2e-builder-services`;
const COUNTRY = `${PARTITION}:reference-data--GeoPoliticalEntityType:Country:`;
const COUNTY = `${PARTITION}:reference-data--GeoPoliticalEntityType:County:`;
/** The name each test gives the dimension, which no flow of the e2e estate declares. */
const NAME = "E2eBuilderWellbore";
/** The issues the builder lists against the YAML, by how much they matter. */
const ERRORS = '[data-testid="builder-issue"][data-severity="error"]';
const WARNINGS = '[data-testid="builder-issue"][data-severity="warning"]';

/** A record as the stand-in holds it, readable by the e2e flows' ACL and legal tag. */
function record(id: string, kind: string, data: Record<string, unknown>) {
  return {
    id,
    kind,
    acl: { viewers: [E2E.osdu.OSDU_ACL_VIEWER], owners: [E2E.osdu.OSDU_ACL_OWNER] },
    legal: { legaltags: [E2E.osdu.OSDU_LEGAL_TAG], otherRelevantDataCountries: ["US"], status: "compliant" },
    data,
    createUser: "e2e-stand-in",
    createTime: "2026-10-03T00:00:00.000Z",
  };
}

const RECORDS = [
  record(`${PARTITION}:work-product-component--WellLog:e2e-builder-log-1`, WELLLOG_KIND, {
    Name: "e2e builder log 1", WellboreID: `${WELLBORE_1}:`, Source: "CPI", ServiceCompanyID: `${COMPANY}:`,
  }),
  record(`${PARTITION}:work-product-component--WellLog:e2e-builder-log-2`, WELLLOG_KIND, { Name: "e2e builder log 2", WellboreID: `${WELLBORE_1}:`, Source: "CPI" }),
  record(`${PARTITION}:work-product-component--WellLog:e2e-builder-log-3`, WELLLOG_KIND, { Name: "e2e builder log 3", WellboreID: `${WELLBORE_1}:`, Source: "COMP" }),
  record(`${PARTITION}:work-product-component--WellLog:e2e-builder-log-4`, WELLLOG_KIND, { Name: "e2e builder log 4", WellboreID: `${WELLBORE_2}:`, Source: "CPI" }),
  record(`${PARTITION}:work-product-component--WellLog:e2e-builder-log-5`, WELLLOG_KIND, { Name: "e2e builder log 5", WellboreID: `${WELLBORE_GONE}:`, Source: "CPI" }),
  record(WELLBORE_1, "osdu:wks:master-data--Wellbore:1.3.0", {
    FacilityName: "E2E Builder 1",
    GeoContexts: [
      { GeoPoliticalEntityID: `${UNITED_STATES}:`, GeoTypeID: COUNTRY },
      { GeoPoliticalEntityID: `${COUNTY_A}:`, GeoTypeID: COUNTY },
    ],
  }),
  record(WELLBORE_2, "osdu:wks:master-data--Wellbore:1.3.0", {
    FacilityName: "E2E Builder 2",
    GeoContexts: [{ GeoPoliticalEntityID: `${UNITED_STATES}:`, GeoTypeID: COUNTRY }],
  }),
  record(UNITED_STATES, "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", { GeoPoliticalEntityName: "United States", GeoPoliticalEntityTypeID: COUNTRY }),
  record(COUNTY_A, "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", { GeoPoliticalEntityName: "County A", GeoPoliticalEntityTypeID: COUNTY }),
  record(COMPANY, "osdu:wks:master-data--Organisation:1.0.0", { OrganisationName: "E2E Services" }),
];

/** The builder's control beside a value of the record in view, by the value's path (its list places left out). */
function field(page: Page, path: string): Locator {
  return page.locator(`[data-testid="builder-field"][data-path="${path}"]:visible`).first();
}

/** Picks `action` from the builder's menu beside a value of the record in view. */
async function pick(page: Page, path: string, action: string) {
  const control = field(page, path);
  await control.scrollIntoViewIfNeeded();
  await control.getByTestId("builder-field-menu").click();
  await page.getByTestId(`builder-pick-${action}`).click();
}

/** Opens, after the record in view, the record one of its values names. */
async function follow(page: Page, id: string) {
  await page.locator(`[data-testid="osdu-record-link"][data-value^="${id}"]:visible`).first().getByTestId("osdu-link-read").click();
}

/** The explorer on the well logs, the build started there: its kind is theirs, and its key the template's likeliest. */
async function buildOnWellLogs(page: Page) {
  await page.goto("/delivery/explorer");
  await expect(page.getByTestId("explorer-live")).toContainText(PARTITION, { timeout: 30_000 });
  await page.getByTestId("explorer-browse-types").click();
  // The groups start folded: the well logs are a type of work-product-component.
  await page.getByTestId("explorer-types").getByRole("button", { name: "Unfold work-product-component" }).click({ timeout: 60_000 });
  const logs = page.getByTestId("explorer-types").getByTestId("explorer-type").filter({ hasText: "WellLog" });
  await expect(logs).toContainText("5", { timeout: 60_000 });
  await logs.getByRole("button").last().click();
  await expect(page.getByTestId("explorer-grid").getByTestId("explorer-grid-row")).toHaveCount(5, { timeout: 60_000 });
  await expect(page.getByTestId("nav-delivery-explorer")).toBeVisible();
  await page.getByTestId("explorer-build-dimension").click();
  await expect(page.getByTestId("builder-panel")).toBeVisible();
  // The workbench's side bar folds while the builder is docked, so the records and the builder share the width.
  await expect(page.getByTestId("nav-delivery-explorer")).toBeHidden();
  await expect(page.getByTestId("builder-kind")).toContainText("WellLog");
  await expect(page.locator('[data-testid="builder-suggestion"][data-path="data.WellboreID"]')).toHaveAttribute("aria-pressed", "true", { timeout: 60_000 });
}

/** The first log opened, its wellbore drilled into, and the wellbore's name read as the value. */
async function readTheWellboresName(page: Page) {
  await page.getByTestId("explorer-grid-row").filter({ hasText: "e2e builder log 1" }).click();
  await expect(field(page, "data.WellboreID")).toBeVisible({ timeout: 60_000 });
  await follow(page, WELLBORE_1);
  await expect(field(page, "data.FacilityName")).toBeVisible({ timeout: 60_000 });
  await pick(page, "data.FacilityName", "value");
}

test.describe.serial("dimension builder", () => {
  test.beforeAll(async ({ playwright }) => {
    const standIn = await playwright.request.newContext();
    try {
      for (const held of RECORDS) {
        const answer = await standIn.put(`${E2E.osdu.OSDU_URL}/__e2e/records`, { data: held });
        expect(answer.ok()).toBe(true);
      }
    } finally {
      await standIn.dispose();
    }
  });

  test.afterAll(async ({ playwright }) => {
    // The stand-in forgets the records this spec gave it, so a later spec meets the platform as the suite starts it.
    const standIn = await playwright.request.newContext();
    try {
      await standIn.delete(`${E2E.osdu.OSDU_URL}/__e2e/records`);
    } finally {
      await standIn.dispose();
    }
  });

  test("builds a dimension while browsing: the key from the template, the value and a country drilled into, a value collected", async ({ adminPage }) => {
    // The build starts on the well logs, its key the wellbore each names, which the template says names a wellbore.
    await buildOnWellLogs(adminPage);
    await expect(adminPage.getByTestId("builder-name")).toHaveValue("Wellbore");
    await adminPage.getByTestId("builder-name").fill(NAME);

    // A log opened: its wellbore's id is marked as one a key can be, and as the key; the example is the log's own key.
    await adminPage.getByTestId("explorer-grid-row").filter({ hasText: "e2e builder log 1" }).click();
    const key = field(adminPage, "data.WellboreID");
    await expect(key.getByTestId("builder-keyable")).toBeVisible({ timeout: 60_000 });
    await expect(key.locator('[data-testid="builder-mark"][data-role="key"]')).toBeVisible();
    await expect(field(adminPage, "data.ServiceCompanyID").getByTestId("builder-keyable")).toBeVisible();
    await expect(adminPage.getByTestId("builder-example")).toContainText("this record's", { timeout: 60_000 });

    // The wellbore drilled into from the log's link, and its name read as each key's value.
    await follow(adminPage, WELLBORE_1);
    await expect(field(adminPage, "data.FacilityName")).toBeVisible({ timeout: 60_000 });
    await pick(adminPage, "data.FacilityName", "value");
    await expect(field(adminPage, "data.FacilityName").locator('[data-testid="builder-mark"][data-role="value"]')).toBeVisible();
    await expect(adminPage.getByTestId("builder-column-value-example")).toHaveText("E2E Builder 1", { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-example-label")).toHaveText("E2E Builder 1");

    // Into the wellbore's GeoContexts, and from its first item to the country it names; its name read as an attribute.
    // The step passes through one item of two that name records, so the builder asks which, and suggests the country's.
    await adminPage.locator('[data-testid="osdu-drill"]:visible').filter({ hasText: "2 items" }).click();
    await follow(adminPage, UNITED_STATES);
    await expect(field(adminPage, "data.GeoPoliticalEntityName")).toBeVisible({ timeout: 60_000 });
    await pick(adminPage, "data.GeoPoliticalEntityName", "attribute");
    const which = adminPage.getByTestId("builder-filter-dialog");
    await expect(which).toContainText("Which items of GeoContexts to follow?");
    await expect(which.getByTestId("builder-filter-objects").locator("li")).toHaveCount(2);
    await expect(which.getByTestId("builder-filter-value")).toHaveValue("GeoPoliticalEntityType:Country:");
    await which.getByTestId("builder-filter-apply").click();
    await expect(which).toHaveCount(0);
    const country = adminPage.getByTestId("builder-column-attribute").nth(0);
    await expect(country.getByTestId("builder-column-attribute-name")).toHaveValue("Country");
    await expect(country.getByTestId("builder-column-attribute-example")).toHaveText("United States", { timeout: 60_000 });
    await expect(field(adminPage, "data.GeoPoliticalEntityName").locator('[data-testid="builder-mark"][data-role="attribute"]')).toBeVisible();

    // Back along the trail to the log, and the source of every log of a wellbore collected, the commonest first.
    while (await adminPage.locator('[data-testid="osdu-linked-close"]:visible').count() > 0) {
      await adminPage.locator('[data-testid="osdu-linked-close"]:visible').first().click();
    }

    await pick(adminPage, "data.Source", "collect");
    const source = adminPage.getByTestId("builder-column-attribute").nth(1);
    await expect(source.getByTestId("builder-column-attribute-name")).toHaveValue("Source");
    await expect(source.getByTestId("builder-column-attribute-example")).toHaveText("CPI (2), COMP (1)", { timeout: 60_000 });

    // The YAML a dimension flow lists, checked as the flow's own would be: it loads, and the one thing it cannot check (no
    // synced repository holds a template of the political entities) is said, with where to download one.
    const expression = adminPage.getByTestId("builder-expression");
    await expect(adminPage.getByTestId("builder-status")).toHaveAttribute("data-valid", "true", { timeout: 60_000 });
    for (const line of [
      `- name: ${NAME}`,
      `kind: '*:*:work-product-component--WellLog:*'`,
      "path: data.WellboreID",
      "label: data.FacilityName",
      "- 'data.GeoContexts[GeoTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityID'",
      "- data.GeoPoliticalEntityName",
      "Source: { collect: data.Source }",
    ]) {
      await expect(expression).toContainText(line);
    }

    await expect(adminPage.locator(WARNINGS).filter({ hasText: "GeoPoliticalEntity" }).first().getByTestId("builder-issue-templates")).toBeVisible();
    await expect(adminPage.locator(ERRORS)).toHaveCount(0);
    await expect(adminPage.getByTestId("builder-table-name")).toHaveText(`osdu.dim_${NAME}`);

    // How a row is built, drawn as a flow's own dimension is and filled with the example key's row.
    await adminPage.getByTestId("builder-show-row").click();
    const row = adminPage.getByTestId("builder-row-dialog");
    await expect(row.getByTestId("builder-row-preview")).toBeVisible({ timeout: 30_000 });
    await expect(row).toContainText("E2E Builder 1");
    await adminPage.keyboard.press("Escape");
    await expect(row).toHaveCount(0);

    // The build lives in the explorer's address: a reload brings it back beside the records.
    await adminPage.reload();
    await expect(adminPage.getByTestId("builder-name")).toHaveValue(NAME, { timeout: 30_000 });
    await expect(expression).toContainText("Source: { collect: data.Source }", { timeout: 60_000 });
  });

  test("steps through the example keys, the record in view's first, and values one naming no record as the dimension says", async ({ adminPage }) => {
    await buildOnWellLogs(adminPage);
    await readTheWellboresName(adminPage);
    await expect(adminPage.getByTestId("builder-example-label")).toHaveText("E2E Builder 1", { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-example-count")).toHaveText("1/3", { timeout: 60_000 });

    // The next commonest keys, one log each: the second wellbore, then the one OSDU holds nothing under, valued by the
    // code its id ends with; given a value for what is not read, it takes that.
    await adminPage.getByTestId("builder-example-next").click();
    await expect(adminPage.getByTestId("builder-example-label")).toHaveText("E2E Builder 2", { timeout: 60_000 });
    await adminPage.getByTestId("builder-example-next").click();
    await expect(adminPage.getByTestId("builder-example-count")).toHaveText("3/3");
    await expect(adminPage.getByTestId("builder-column-value-example")).toHaveText("e2e-builder-gone", { timeout: 60_000 });
    await adminPage.getByTestId("builder-unlabelled").fill("Unknown wellbore");
    await expect(adminPage.getByTestId("builder-column-value-example")).toHaveText("Unknown wellbore", { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-expression")).toContainText("unlabelled: Unknown wellbore", { timeout: 60_000 });

    // Back round to the first key, cleaned: each value lower-cased, as a build would hold it.
    await adminPage.getByTestId("builder-example-next").click();
    await expect(adminPage.getByTestId("builder-example-count")).toHaveText("1/3");
    await adminPage.getByTestId("builder-clean-lower").click();
    await expect(adminPage.getByTestId("builder-clean-lower")).toHaveAttribute("aria-pressed", "true");
    await expect(adminPage.getByTestId("builder-column-value-example")).toHaveText("e2e builder 1", { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-expression")).toContainText("- lower", { timeout: 60_000 });
  });

  test("offers the link a record is reached through as the key, where it is not the key", async ({ adminPage }) => {
    await buildOnWellLogs(adminPage);
    await adminPage.getByTestId("explorer-grid-row").filter({ hasText: "e2e builder log 1" }).click();
    await expect(field(adminPage, "data.ServiceCompanyID")).toBeVisible({ timeout: 60_000 });

    // The service company is reached through another link than the key's: its name cannot be the value, and its link is
    // offered as the key instead.
    await follow(adminPage, COMPANY);
    await expect(field(adminPage, "data.OrganisationName")).toBeVisible({ timeout: 60_000 });
    await field(adminPage, "data.OrganisationName").getByTestId("builder-field-menu").click();
    await expect(adminPage.getByTestId("builder-pick-value")).toHaveAttribute("data-disabled", "");
    await expect(adminPage.getByTestId("builder-pick-value")).toContainText("reached through data.ServiceCompanyID");
    await adminPage.getByTestId("builder-pick-rekey").click();
    await expect(adminPage.locator('[data-testid="builder-suggestion"][data-path="data.ServiceCompanyID"]')).toHaveAttribute("aria-pressed", "true");

    await pick(adminPage, "data.OrganisationName", "value");
    await expect(adminPage.getByTestId("builder-expression")).toContainText("path: data.ServiceCompanyID", { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-expression")).toContainText("label: data.OrganisationName");
  });

  test("points a mistake at its column, narrows the keys by a query, starts from the welcome, and asks before closing", async ({ adminPage }) => {
    // Started where no type is picked, the build waits for one, and takes the kind of the type picked then.
    await adminPage.goto("/delivery/explorer");
    await expect(adminPage.getByTestId("explorer-live")).toContainText(PARTITION, { timeout: 30_000 });
    await adminPage.getByTestId("explorer-build-dimension").click();
    await expect(adminPage.getByTestId("builder-next")).toContainText("Pick a type");
    await adminPage.getByTestId("explorer-browse-types").click();
    await adminPage.getByTestId("explorer-types").getByRole("button", { name: "Unfold work-product-component" }).click({ timeout: 60_000 });
    const logs = adminPage.getByTestId("explorer-types").getByTestId("explorer-type").filter({ hasText: "WellLog" });
    await logs.getByRole("button").last().click({ timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-kind")).toContainText("WellLog", { timeout: 60_000 });
    const status = adminPage.getByTestId("builder-status");
    await expect(status).toHaveAttribute("data-valid", "true", { timeout: 60_000 });

    // A column name the table already holds is refused at the column, and the YAML is not ready until it is put right.
    await adminPage.getByTestId("builder-column-key-name").fill("id");
    await expect(status).toHaveAttribute("data-valid", "false", { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-column-key-problem")).toBeVisible();
    await expect(adminPage.locator(ERRORS).first()).toHaveAttribute("data-target", "columns.key");
    await adminPage.getByTestId("builder-column-key-name").fill("WellboreKey");
    await expect(status).toHaveAttribute("data-valid", "true", { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-expression")).toContainText("key: WellboreKey");

    // A query narrows the records the keys are read from: only the log whose source is COMP, so one key holds.
    await adminPage.getByTestId("builder-more").click();
    await adminPage.getByTestId("builder-query").fill('data.Source:"COMP"');
    await expect(adminPage.getByTestId("builder-example-count")).toHaveText("1/1", { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-expression")).toContainText(`query: 'data.Source:"COMP"'`, { timeout: 60_000 });

    // Closing a build with a key asks first, since nothing keeps the draft; the explorer is left as it was.
    await adminPage.getByTestId("builder-close").click();
    await expect(adminPage.getByTestId("confirm-dialog")).toContainText("Close the builder?");
    await adminPage.getByTestId("confirm-dialog-confirm").click();
    await expect(adminPage.getByTestId("builder-panel")).toHaveCount(0);
    await expect(adminPage.getByTestId("explorer-build-dimension")).toBeVisible();
    await expect(adminPage.getByTestId("nav-delivery-explorer")).toBeVisible();
    await expect(adminPage.getByTestId("explorer-grid").getByTestId("explorer-grid-row")).toHaveCount(5);
  });
});
