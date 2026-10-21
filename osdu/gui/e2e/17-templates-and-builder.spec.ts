import { readFileSync } from "node:fs";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import { REPO_NAME, SOURCE } from "./global-setup";
import { expect, test } from "./helpers";

// Templates and the mapping builder (osdu/docs/reference/concepts/templates.md; the builder:
// osdu/docs/reference/concepts/gui.md, Mapping builder). Runs after the seed (03), which syncs the fixture repository: its
// sync records the templates the source's template folder holds, which the Templates page lists and reads from the
// repository's files, and its cache is in the catalog. Browsing OSDU reads the Open Group's public data definitions
// repository through the control plane, which the suite cannot reach, so the browse tests stand in for those answers and
// let everything after them (the preview, the download) run against the real control plane.

const WELLBORE_KIND = "osdu:wks:master-data--Wellbore:1.3.0";
const WELLBORE_VERSION = "58d6bdbd9d066a06";
const WELLBORE_FILE = "osdu_wks_master-data--Wellbore_1.3.0.json";
/** The file of the source's template folder that holds the wellbore template, as the sync records it. */
const WELLBORE_PATH = `${SOURCE}/templates/${WELLBORE_FILE}`;
/** The sample's own copy of that file, which the fixture repository's template folder was copied from. */
const WELLBORE_SAMPLE = join(import.meta.dirname, "..", "..", "samples", "welldb", "templates", WELLBORE_FILE);

const DATA_DEFINITIONS = "https://community.opengroup.org/osdu/data/data-definitions";
const RELEASE = {
  name: "v0.30.0",
  commit: "99f8fc88d8ad838b5738ac5ad92ac643538b5766",
  publishedUtc: "2026-07-17T06:55:57Z",
  webUrl: `${DATA_DEFINITIONS}/-/tree/v0.30.0/Generated`,
  local: true,
};
const WELLBORE_URL = `${DATA_DEFINITIONS}/-/blob/v0.30.0/Generated/master-data/Wellbore.1.3.0.json`;

/** A record schema as the control plane lists it for a release. */
function osduSchema(kind: string, entityType: string, version: string, status: string, path: string) {
  return { kind, entityType, version, status, path, webUrl: `${DATA_DEFINITIONS}/-/blob/v0.30.0/Generated/${path}` };
}
const WELLLOG_KIND = "osdu:wks:work-product-component--WellLog:1.4.0";
const WELLLOG_VERSION = "26a3c3441882db4f";

/** The row of a data table holding the element with the given test id. */
function rowWith(page: Page, table: string, testId: string): Locator {
  return page.getByTestId(table).getByTestId("table-row").filter({ has: page.getByTestId(testId) });
}

/**
 * How long a page may take to appear on its first visit of a run: the GUI's dev server compiles a lazily loaded page's
 * modules when it is first asked for, which takes longer than the default wait on a busy machine.
 */
const FIRST_VISIT_MS = 30_000;

/**
 * Turns one of the variable tree's view options (required, minted, nested) on or off. The options wait in a popover the
 * page renders outside the sheet, so the switch is found on the page, and the popover is closed again after.
 */
async function toggleViewOption(page: Page, scope: Locator, option: "required" | "minted" | "nested"): Promise<void> {
  await scope.getByTestId("templates-view-options").click();
  await page.getByTestId(`templates-view-show-${option}`).click();
  await page.keyboard.press("Escape");
  await expect(page.getByTestId("templates-view-options-panel")).toHaveCount(0);
}

async function openTemplates(page: Page): Promise<void> {
  await page.getByTestId("nav-delivery-templates").click();
  await expect(page.getByTestId("page-delivery-templates")).toBeVisible({ timeout: FIRST_VISIT_MS });
}

test.describe.serial("templates and the mapping builder", () => {
  test("a repository's template is listed by its file, laid out from it, and downloads as that file", async ({ adminPage }) => {
    await openTemplates(adminPage);

    // Each row is a file of a synced repository's template folder: the kind and version it holds, the repository, the file.
    const rows = adminPage.getByTestId("templates-table").getByTestId("table-row");
    const ours = rows.filter({ hasText: REPO_NAME });
    const wellbore = ours.filter({ hasText: WELLBORE_PATH });
    await expect(wellbore).toHaveCount(1, { timeout: 30_000 });
    await expect(wellbore).toContainText(WELLBORE_KIND);
    await expect(wellbore).toContainText(WELLBORE_VERSION);
    await expect(ours.filter({ hasText: WELLLOG_KIND }).first()).toContainText(WELLLOG_VERSION);

    // The filter finds a file by any of what its row says.
    await adminPage.getByTestId("templates-search").fill(WELLBORE_FILE);
    await expect(ours).toHaveCount(1);
    await adminPage.getByTestId("templates-search").fill("");

    await wellbore.click();
    const sheet = adminPage.getByTestId("templates-sheet");
    await expect(sheet.getByTestId("templates-view-kind")).toHaveText(WELLBORE_KIND, { timeout: 15_000 });
    await expect(sheet.getByTestId("templates-view-version")).toHaveText(WELLBORE_VERSION);
    // It says where it is read from: the repository, and the file of its template folder.
    await expect(sheet.getByTestId("templates-view-repository")).toContainText(REPO_NAME);
    await expect(sheet.getByTestId("templates-view-file")).toContainText(WELLBORE_PATH);

    // The variables a mapping fills are listed; what OSDU Delivery and OSDU write themselves shows only on request.
    // The tree opens fully folded; opening the data section shows what it holds.
    await expect(sheet.getByTestId("templates-view-variable-osdu.data")).toBeVisible();
    await expect(sheet.getByTestId("templates-view-variable-osdu.data.FacilityName")).toHaveCount(0);
    await sheet.getByTestId("templates-view-variable-osdu.data").click();
    await expect(sheet.getByTestId("templates-view-variable-osdu.data.FacilityName")).toBeVisible();
    await expect(sheet.getByTestId("templates-view-variable-osdu.id")).toHaveCount(0);
    await toggleViewOption(adminPage, sheet, "minted");
    await expect(sheet.getByTestId("templates-view-variable-osdu.id")).toBeVisible();

    // Each writer is marked apart from what a mapping fills: a glyph named for the writer, and no marker on a mapping's variable.
    await expect(sheet.getByTestId("templates-view-role-osdu.createTime")).toHaveAttribute("aria-label", "OSDU sets it");
    await expect(sheet.getByTestId("templates-view-role-osdu.createTime").locator("svg")).toHaveCount(1);
    await expect(sheet.getByTestId("templates-view-role-osdu.id")).toHaveAttribute("aria-label", "OSDU Delivery writes it");
    await expect(sheet.getByTestId("templates-view-role-osdu.data.FacilityName")).toHaveCount(0);

    // Selecting a variable in the tree lays out its properties beside it.
    await sheet.getByTestId("templates-view-variable-osdu.id").click();
    await expect(sheet.getByTestId("templates-view-properties-path")).toHaveText("osdu.id");
    await expect(sheet.getByTestId("templates-view-properties-role")).toHaveText("OSDU Delivery writes it");
    await sheet.getByTestId("templates-view-variable-osdu.data.FacilityName").click();
    await expect(sheet.getByTestId("templates-view-properties-path")).toHaveText("osdu.data.FacilityName");
    await expect(sheet.getByTestId("templates-view-properties-role")).toHaveCount(0);

    // Required narrows the tree to what the schema demands, keeping the holders on the way to them.
    await toggleViewOption(adminPage, sheet, "required");
    await expect(sheet.getByTestId("templates-view-variable-osdu.acl")).toBeVisible();
    await expect(sheet.getByTestId("templates-view-variable-osdu.data.FacilityName")).toHaveCount(0);
    await toggleViewOption(adminPage, sheet, "required");
    await expect(sheet.getByTestId("templates-view-variable-osdu.data.FacilityName")).toBeVisible();

    await sheet.getByTestId("templates-view-tab-schema").click();
    await expect(sheet.getByTestId("templates-view-schema-json")).toBeVisible({ timeout: 15_000 });

    // Download saves the file as the repository holds it, under its own name: the sample's file, which the fixture copied.
    // A template is the repository's file, so there is nothing to delete here: the repository removes it, and its sync.
    await expect(sheet.getByTestId("templates-view-actions").getByTestId("templates-delete")).toHaveCount(0);
    const [download] = await Promise.all([
      adminPage.waitForEvent("download"),
      sheet.getByTestId("templates-view-actions").getByTestId("templates-download").click(),
    ]);
    expect(download.suggestedFilename()).toBe(WELLBORE_FILE);
    // A checkout may write the file's line endings its own way, so the two are compared as the JSON they hold.
    expect(JSON.parse(readFileSync(await download.path(), "utf8"))).toEqual(JSON.parse(readFileSync(WELLBORE_SAMPLE, "utf8")));
  });

  test("a link to a kind and version opens the file holding it, and one no repository holds says so", async ({ adminPage }) => {
    // The links the mapping, dimension and order pages make name a kind and version; the page opens the file holding it.
    await adminPage.goto(`/delivery/templates?${new URLSearchParams({ kind: WELLBORE_KIND, version: WELLBORE_VERSION }).toString()}`);
    const sheet = adminPage.getByTestId("templates-sheet");
    await expect(sheet.getByTestId("templates-view-version")).toHaveText(WELLBORE_VERSION, { timeout: FIRST_VISIT_MS });
    await expect(sheet.getByTestId("templates-view-file")).toContainText(`templates/${WELLBORE_FILE}`);
    await adminPage.keyboard.press("Escape");
    await expect(sheet).toHaveCount(0);
    // Closing the sheet drops the link from the address, so a reload does not open it again.
    expect(new URL(adminPage.url()).searchParams.has("kind")).toBe(false);

    await adminPage.goto(`/delivery/templates?${new URLSearchParams({ kind: WELLBORE_KIND, version: "0".repeat(16) }).toString()}`);
    await expect(adminPage.getByTestId("templates-link-unheld")).toContainText(`${WELLBORE_KIND} version ${"0".repeat(16)}`, { timeout: FIRST_VISIT_MS });
    await expect(adminPage.getByTestId("templates-sheet")).toHaveCount(0);

    // A link to a file the sync did not record says so in the sheet rather than showing nothing.
    await adminPage.goto(`/delivery/templates?${new URLSearchParams({ repoId: "00000000-0000-0000-0000-000000000000", path: "nowhere/templates/none.json" }).toString()}`);
    await expect(adminPage.getByTestId("templates-detail-error")).toBeVisible({ timeout: FIRST_VISIT_MS });
  });

  test("the OSDU data definitions are browsed by release, and a kind opens as a template and downloads as its file", async ({ adminPage }) => {
    const releases = /\/api\/v1\/delivery\/templates\/osdu\/releases$/;
    const schemas = /\/api\/v1\/delivery\/templates\/osdu\/schemas(\?|$)/;
    const schema = /\/api\/v1\/delivery\/templates\/osdu\/schema(\?|$)/;
    const file = /\/api\/v1\/delivery\/templates\/osdu\/file(\?|$)/;
    const wellboreText = readFileSync(WELLBORE_SAMPLE, "utf8");
    const wellboreSchema: unknown = JSON.parse(wellboreText);
    const asked: string[] = [];
    const sync = /\/api\/v1\/delivery\/templates\/osdu\/sync$/;
    await adminPage.route(sync, (route) => route.fulfill({ json: { syncedUtc: "2026-09-13T11:00:00Z", releases: [RELEASE], downloaded: ["v0.30.0"] } }));
    await adminPage.route(releases, (route) => route.fulfill({ json: { syncedUtc: "2026-09-13T10:00:00Z", releases: [RELEASE, { ...RELEASE, name: "v0.29.1", commit: "0".repeat(40), local: false }] } }));
    await adminPage.route(schemas, (route) => {
      asked.push(route.request().url());
      return route.fulfill({
        json: {
          release: RELEASE,
          schemas: [
            osduSchema("osdu:wks:master-data--Well:1.2.0", "master-data--Well", "1.2.0", "DEVELOPMENT", "master-data/Well.1.2.0.json"),
            osduSchema(WELLBORE_KIND, "master-data--Wellbore", "1.3.0", "PUBLISHED", "master-data/Wellbore.1.3.0.json"),
            osduSchema("osdu:wks:master-data--Wellbore:1.0.0", "master-data--Wellbore", "1.0.0", "PUBLISHED", "master-data/Wellbore.1.0.0.json"),
          ],
        },
      });
    });
    await adminPage.route(schema, (route) => {
      asked.push(route.request().url());
      return route.fulfill({
        json: {
          kind: WELLBORE_KIND,
          version: WELLBORE_VERSION,
          release: RELEASE,
          path: "master-data/Wellbore.1.3.0.json",
          webUrl: WELLBORE_URL,
          origin: "OSDU data definitions v0.30.0 (99f8fc88d8ad) Generated/master-data/Wellbore.1.3.0.json",
          schema: wellboreSchema,
        },
      });
    });
    // The file a template folder keeps: the bundled schema, as the control plane writes it for the kind of the release.
    await adminPage.route(file, (route) => {
      asked.push(route.request().url());
      return route.fulfill({ contentType: "application/json", body: wellboreText });
    });

    try {
      await openTemplates(adminPage);
      await adminPage.getByTestId("templates-tab-browse").click();

      // The newest release is browsed straight away: there is no repository or connection to pick first.
      await expect(adminPage.getByTestId("templates-browse-release")).toContainText("v0.30.0", { timeout: 15_000 });
      await expect(adminPage.getByTestId("templates-browse-summary")).toContainText("2 record types");
      await expect(adminPage.getByTestId("templates-browse-repository-link")).toHaveAttribute("href", RELEASE.webUrl);

      // The local copy says when it last read the release list, and syncing reads it again and says what it downloaded.
      await expect(adminPage.getByTestId("templates-browse-synced")).toContainText("Synced");
      await adminPage.getByTestId("templates-browse-sync").click();
      await expect(adminPage.getByText(/Synced with the OSDU data definitions: 1 releases; downloaded v0\.30\.0/).first()).toBeVisible({ timeout: 15_000 });
      const rows = adminPage.getByTestId("templates-browse-results").getByTestId("table-row");
      await expect(rows).toHaveCount(2);
      await expect(adminPage.getByTestId("templates-browse-status-osdu:wks:master-data--Well:1.2.0")).toHaveText("In development");

      // One search box finds a kind by any part of it; each entity type shows its newest version unless every version is asked for.
      await adminPage.getByTestId("templates-browse-search").fill("wellbore");
      await expect(rows).toHaveCount(1);
      await expect(rows.first()).toContainText("2 versions");
      await expect(adminPage.getByTestId(`templates-browse-link-${WELLBORE_KIND}`)).toHaveAttribute("href", WELLBORE_URL);
      await adminPage.getByTestId("templates-browse-all-versions").click();
      await expect(rows).toHaveCount(2);

      // A kind downloads straight from its row as the file a template folder keeps, named after its kind.
      const [fromRow] = await Promise.all([
        adminPage.waitForEvent("download"),
        adminPage.getByTestId(`templates-browse-download-${WELLBORE_KIND}`).click(),
      ]);
      expect(fromRow.suggestedFilename()).toBe(WELLBORE_FILE);
      expect(readFileSync(await fromRow.path(), "utf8")).toBe(wellboreText);
      await expect(adminPage.getByText(/Put it in a folder named templates beside the documents that fill it/).first()).toBeVisible();

      // A kind opens laid out as the template its file holds, with the same download beside it. Nothing is kept: the
      // template is the repository's once the file is committed to a template folder and synced.
      await adminPage.getByTestId(`templates-browse-view-${WELLBORE_KIND}`).click();
      const sheet = adminPage.getByTestId("templates-browse-sheet");
      await expect(sheet.getByTestId("templates-view-version")).toHaveText(WELLBORE_VERSION, { timeout: 15_000 });
      await expect(sheet.getByTestId("templates-browse-source-link")).toHaveAttribute("href", WELLBORE_URL);
      await expect(sheet.getByTestId("templates-view-repository")).toHaveCount(0);
      const [fromSheet] = await Promise.all([
        adminPage.waitForEvent("download"),
        sheet.getByTestId("templates-view-actions").getByTestId("templates-browse-download").click(),
      ]);
      expect(fromSheet.suggestedFilename()).toBe(WELLBORE_FILE);
      expect(JSON.parse(readFileSync(await fromSheet.path(), "utf8"))).toEqual(wellboreSchema);

      expect(asked.length).toBeGreaterThan(1);
      expect(asked.every((url) => new URL(url).searchParams.get("release") === "v0.30.0")).toBe(true);
      expect(asked.some((url) => new URL(url).pathname.endsWith("/osdu/file") && new URL(url).searchParams.get("kind") === WELLBORE_KIND)).toBe(true);
    } finally {
      await adminPage.unroute(sync);
      await adminPage.unroute(releases);
      await adminPage.unroute(schemas);
      await adminPage.unroute(schema);
      await adminPage.unroute(file);
    }
  });

  test("two versions of a kind compare with a verdict, every variable change and the files side by side", async ({ adminPage }) => {
    const releases = /\/api\/v1\/delivery\/templates\/osdu\/releases$/;
    const schemas = /\/api\/v1\/delivery\/templates\/osdu\/schemas(\?|$)/;
    const compare = /\/api\/v1\/delivery\/templates\/osdu\/compare(\?|$)/;
    const OLDER_KIND = "osdu:wks:master-data--Wellbore:1.0.0";
    const side = (kind: string, path: string, templateVersion: string, fileText: string) => ({
      kind, release: RELEASE, path, webUrl: `${DATA_DEFINITIONS}/-/blob/v0.30.0/Generated/${path}`, status: "PUBLISHED", templateVersion, fileText,
    });
    const comparison = {
      from: side(OLDER_KIND, "master-data/Wellbore.1.0.0.json", "5694d203047e3aae", `{\n  "x-osdu-schema-source": "${OLDER_KIND}",\n  "Name": "string"\n}\n`),
      to: side(WELLBORE_KIND, "master-data/Wellbore.1.3.0.json", WELLBORE_VERSION, `{\n  "x-osdu-schema-source": "${WELLBORE_KIND}",\n  "FacilityName": "string"\n}\n`),
      sameFile: false,
      onlyIdentifiersDiffer: false,
      sameTemplate: false,
      breaking: 1,
      additive: 1,
      wording: 1,
      unchanged: 40,
      changes: [
        { path: "osdu.data.Name", change: "Removed", impact: "Breaking", role: "Mapping", fields: [{ field: "type", before: "string", after: null, impact: "Breaking" }] },
        { path: "osdu.data.FacilityName", change: "Added", impact: "Additive", role: "Mapping", fields: [{ field: "type", before: null, after: "string", impact: "Additive" }] },
        { path: "osdu.data.Depth", change: "Changed", impact: "Wording", role: "Mapping", fields: [{ field: "description", before: "Depth.", after: "Measured depth.", impact: "Wording" }] },
      ],
      sameReferencedFiles: 3,
      referencedFiles: [
        {
          name: "AbstractFacility",
          fromPath: "abstract/AbstractFacility.1.0.0.json",
          toPath: "abstract/AbstractFacility.1.1.0.json",
          fromWebUrl: `${DATA_DEFINITIONS}/-/blob/v0.30.0/Generated/abstract/AbstractFacility.1.0.0.json`,
          toWebUrl: `${DATA_DEFINITIONS}/-/blob/v0.30.0/Generated/abstract/AbstractFacility.1.1.0.json`,
          fromText: "{\n  \"FacilityName\": \"string\"\n}\n",
          toText: "{\n  \"FacilityName\": \"string\",\n  \"FacilityOperators\": \"array\"\n}\n",
        },
      ],
    };
    const compared: URL[] = [];
    await adminPage.route(releases, (route) => route.fulfill({ json: { syncedUtc: "2026-09-13T10:00:00Z", releases: [RELEASE, { ...RELEASE, name: "v0.29.1", commit: "0".repeat(40), local: false }] } }));
    await adminPage.route(schemas, (route) => route.fulfill({
      json: {
        release: RELEASE,
        schemas: [
          osduSchema(WELLBORE_KIND, "master-data--Wellbore", "1.3.0", "PUBLISHED", "master-data/Wellbore.1.3.0.json"),
          osduSchema(OLDER_KIND, "master-data--Wellbore", "1.0.0", "PUBLISHED", "master-data/Wellbore.1.0.0.json"),
        ],
      },
    }));
    await adminPage.route(compare, (route) => {
      compared.push(new URL(route.request().url()));
      return route.fulfill({ json: comparison });
    });

    try {
      await openTemplates(adminPage);
      await adminPage.getByTestId("templates-tab-browse").click();
      await expect(adminPage.getByTestId("templates-browse-summary")).toContainText("1 record type", { timeout: 15_000 });

      // Compare opens on the version before this one in the same release.
      await adminPage.getByTestId(`templates-browse-compare-${WELLBORE_KIND}`).click();
      const sheet = adminPage.getByTestId("templates-compare-sheet");
      await expect(sheet.getByTestId("templates-compare-from-version")).toContainText("1.0.0", { timeout: 15_000 });
      await expect(sheet.getByTestId("templates-compare-to-version")).toContainText("1.3.0");

      // Each picker says whether it is the release or the schema version, and a line says what the two sides are, so
      // two versions of one release are never read as the same schema.
      await expect(sheet.getByText("From release", { exact: true })).toBeVisible();
      await expect(sheet.getByText("From schema version", { exact: true })).toBeVisible();
      // The release the compare opened on is whichever the data definitions publish newest, so only its shape is asserted.
      await expect(sheet.getByTestId("templates-compare-comparing"))
        .toHaveText(/^Comparing schema versions 1\.0\.0 and 1\.3\.0, both from release v[\d.]+\.$/);

      // The verdict first, then every variable that differs, with what it means for a mapping of the older version.
      await expect(sheet.getByTestId("templates-compare-count-breaking")).toHaveText("1 breaking", { timeout: 15_000 });
      await expect(sheet.getByTestId("templates-compare-count-additive")).toHaveText("1 additive");
      await expect(sheet.getByTestId("templates-compare-count-wording")).toHaveText("1 wording");
      // The list opens with the impact headings open and every variable folded; a variable opens onto its fields.
      const changeRows = sheet.getByTestId("templates-compare-change-row");
      await expect(changeRows).toHaveCount(3);
      await expect(sheet.getByTestId("templates-compare-group-breaking").getByTestId("templates-compare-change-osdu.data.Name")).toHaveText("Removed");
      await expect(sheet.getByTestId("templates-compare-field-osdu.data.Name-type")).toHaveCount(0);
      await sheet.getByTestId("templates-compare-change-toggle-osdu.data.Name").click();
      await expect(sheet.getByTestId("templates-compare-field-osdu.data.Name-type")).toBeVisible();
      await sheet.getByTestId("templates-compare-group-toggle-breaking").click();
      await expect(changeRows).toHaveCount(2);
      await sheet.getByTestId("templates-compare-expand-all").click();
      await expect(sheet.getByTestId("templates-compare-expand-all")).toHaveText("Collapse all");
      await expect(sheet.getByTestId("templates-compare-group-additive").getByTestId("templates-compare-change-osdu.data.FacilityName")).toHaveText("Added");

      // What changed is marked in place: the words a description lost, and the words it gained.
      const depthDescription = sheet.getByTestId("templates-compare-field-osdu.data.Depth-description");
      await expect(depthDescription.locator("del")).toHaveText("Depth");
      await expect(depthDescription.locator("ins")).toHaveText("Measured depth");

      // The impact filter narrows the list to one impact, counting what each holds.
      await expect(changeRows).toHaveCount(3);
      await expect(sheet.getByTestId("templates-compare-impact-filter-wording")).toContainText("1");
      await sheet.getByTestId("templates-compare-impact-filter-breaking").click();
      await expect(changeRows).toHaveCount(1);
      await expect(sheet.getByTestId("templates-compare-group-wording")).toHaveCount(0);
      await sheet.getByTestId("templates-compare-impact-filter-all").click();
      await expect(changeRows).toHaveCount(3);

      // The files as the releases publish them, side by side.
      await sheet.getByTestId("templates-compare-tab-files").click();
      await expect(sheet.getByTestId("templates-compare-files")).toBeVisible();
      await expect(sheet.getByTestId("templates-compare-files-original")).toContainText(OLDER_KIND);
      await expect(sheet.getByTestId("templates-compare-to-link")).toHaveAttribute("href", `${DATA_DEFINITIONS}/-/blob/v0.30.0/Generated/master-data/Wellbore.1.3.0.json`);

      // The shared schemas the versions refer to that differ are there to compare too.
      await expect(sheet.getByTestId("templates-compare-referenced-summary")).toHaveText("1 shared schema it refers to differs; 3 are the same.");
      await sheet.getByTestId("templates-compare-file-picker").click();
      await adminPage.getByRole("option").filter({ hasText: "AbstractFacility" }).click();
      await expect(sheet.getByTestId("templates-compare-files-original")).toContainText("abstract/AbstractFacility.1.0.0.json");
      await expect(sheet.getByTestId("templates-compare-to-link")).toHaveAttribute("href", `${DATA_DEFINITIONS}/-/blob/v0.30.0/Generated/abstract/AbstractFacility.1.1.0.json`);

      const first = compared[0];
      expect([first.searchParams.get("fromKind"), first.searchParams.get("toKind")]).toEqual([OLDER_KIND, WELLBORE_KIND]);
      expect([first.searchParams.get("fromRelease"), first.searchParams.get("toRelease")]).toEqual(["v0.30.0", "v0.30.0"]);

      // Swapping compares the other way round.
      await sheet.getByTestId("templates-compare-swap").click();
      await expect.poll(() => compared.some((url) => url.searchParams.get("fromKind") === WELLBORE_KIND && url.searchParams.get("toKind") === OLDER_KIND)).toBe(true);
    } finally {
      await adminPage.unroute(releases);
      await adminPage.unroute(schemas);
      await adminPage.unroute(compare);
    }
  });

  test("the builder starts a mapping from a template of the repository, with what the cache holds prefilled", async ({ adminPage }) => {
    // The builder is no entry of the menu: Mappings opens it.
    await adminPage.getByTestId("nav-delivery-documents").click();
    await adminPage.getByTestId("delivery-documents-new-mapping").click();
    await expect(adminPage.getByTestId("page-delivery-mapping-builder")).toBeVisible({ timeout: FIRST_VISIT_MS });
    await expect(adminPage.getByTestId("mapping-builder-empty")).toBeVisible();

    await adminPage.getByTestId("mapping-builder-repo").click();
    await adminPage.getByRole("option").filter({ hasText: "e2e-repo" }).first().click();
    // The repository's delivery flow delivers to partition dev, so the builder reads that partition's cache without being told.
    await expect(adminPage.getByTestId("mapping-builder-cache")).toContainText("dev", { timeout: 15_000 });
    await expect(adminPage.getByTestId("mapping-builder-cache-note")).toContainText(/holds \d+ types? at version /, { timeout: 15_000 });

    // The templates offered are the files the repository's template folders hold, each named by its kind and its file.
    await adminPage.getByTestId("mapping-builder-template").click();
    await expect(adminPage.getByRole("option").filter({ hasText: WELLBORE_PATH })).toBeVisible();
    await adminPage.getByRole("option").filter({ hasText: WELLLOG_KIND }).first().click();
    // The name starts as the template's entity; this draft takes its own, so it is never taken for the synced mapping.
    await expect(adminPage.getByTestId("mapping-builder-name")).toHaveValue("WellLog");
    await adminPage.getByTestId("mapping-builder-name").fill("WellLogDraft");
    await adminPage.getByTestId("mapping-builder-system").fill("welldb");
    await adminPage.getByTestId("mapping-builder-start").click();

    // A well log points at the unit of its vertical measurement, reference data the cache holds (the partition's units),
    // so that entry starts filled from the cache. Its wellbore is searched for on the platform, so nothing prefills that one.
    const measurement = rowWith(adminPage, "mapping-builder-variables", "mapping-builder-variable-osdu.data.VerticalMeasurement.VerticalMeasurementUnitOfMeasureID");
    await expect(measurement).toBeVisible({ timeout: 15_000 });
    await expect(measurement.getByTestId("mapping-builder-prefilled")).toBeVisible();
    const wellbore = rowWith(adminPage, "mapping-builder-variables", "mapping-builder-variable-osdu.data.WellboreID");
    await expect(wellbore.getByTestId("mapping-builder-prefilled")).toHaveCount(0);

    // The check writes the YAML and refuses the draft as it stands: it has no key, and the cache entry finds by no column.
    await expect(adminPage.getByTestId("mapping-builder-invalid")).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("mapping-builder-issue").first()).toBeVisible();
    await expect(adminPage.getByTestId("mapping-builder-yaml")).toContainText(WELLLOG_VERSION);
    await expect(adminPage.getByTestId("mapping-builder-propose-blocked")).toBeVisible();

    await adminPage.getByTestId("mapping-builder-key-input").fill("log_id");
    await adminPage.getByTestId("mapping-builder-key-add").click();
    await expect(adminPage.getByTestId("mapping-builder-key-chip-log_id")).toBeVisible();
  });

  test("a synced mapping opens in the builder and passes the check against the cache its flow reads", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-documents").click();
    await expect(adminPage.getByTestId("page-delivery-documents")).toBeVisible({ timeout: FIRST_VISIT_MS });
    const row = adminPage.getByTestId("delivery-mappings-table").getByTestId("table-row").filter({ hasText: "WellLog@1.4.0" }).first();
    await expect(row).toContainText(WELLLOG_VERSION, { timeout: 30_000 });
    await row.click();

    const detail = adminPage.getByTestId("delivery-mapping-detail");
    await expect(detail.getByTestId("delivery-mapping-template")).toContainText(WELLLOG_VERSION, { timeout: 15_000 });

    // The properties open first: the template as the record's tree, with what this mapping fills of it. Every row says
    // how the mapping reaches its variable (drawn only where it asks for attention), and the tree opens on the Filled
    // view: what is filled and what a check names.
    const properties = detail.getByTestId("delivery-mapping-coverage");
    await expect(properties.getByTestId("templates-view-coverage-osdu.acl")).toHaveAttribute("data-coverage", "Always", { timeout: 30_000 });
    await expect(properties.getByTestId("templates-view-coverage-osdu.data.Name")).toHaveAttribute("data-coverage", "Always");
    // A curve's business value is left optional, so a log whose curves have none still delivers.
    await expect(properties.getByTestId("templates-view-coverage-osdu.data.Curves[].LogCurveBusinessValueID"))
      .toHaveAttribute("data-coverage", "Sometimes");
    // An entry filling a free key of an object that takes them is a row under that object.
    await expect(properties.getByTestId("templates-view-coverage-osdu.tags.DeliveredBy")).toHaveAttribute("data-coverage", "Always");

    // What fills a variable is read beside the tree, whole: the origin, every line a lookup tries, and the modifiers in
    // order. The wellbore is searched for on the platform, by its name and then by its aliases.
    await properties.getByTestId("templates-view-variable-osdu.data.WellboreID").click();
    const entry = properties.getByTestId("templates-view-properties-entry");
    await expect(entry.getByTestId("delivery-mapping-property-detail-source")).toContainText("search.Wellbore.id");
    await expect(entry.getByTestId("delivery-mapping-property-detail-source")).toContainText("Record found by searching the platform");
    await expect(entry.getByTestId("delivery-mapping-property-detail-lookup"))
      .toContainText("search.Wellbore.data.FacilityName = dataset.wellbore_uwi");
    await expect(entry.getByTestId("delivery-mapping-property-detail-lookup"))
      .toContainText("search.Wellbore.data.NameAliases.AliasName = dataset.wellbore_uwi");
    // The unit of the vertical measurement is the first of three alternatives that gives one: its column translated
    // through the depth unit map the partition's cache holds (not a list in the mapping) as a unit the partition holds,
    // else the partition's own units, else the translation written unverified.
    await properties.getByTestId("templates-view-variable-osdu.data.VerticalMeasurement.VerticalMeasurementUnitOfMeasureID").click();
    await expect(entry.getByTestId("delivery-mapping-property-detail-source")).toContainText("The first of these that gives a value");
    await expect(entry.getByTestId("delivery-mapping-property-detail-source")).toContainText("3 alternatives, tried in order");
    const alternatives = entry.getByTestId("delivery-mapping-property-detail-alternatives").getByRole("listitem");
    await expect(alternatives).toHaveCount(3);
    await expect(alternatives.nth(0)).toContainText("dataset.elev_meas_ref | split on ' ', part 2 | replace from $cache.DepthUnitAlias | ref");
    await expect(alternatives.nth(1)).toContainText("cache.UnitOfMeasure.id by ID/Code/Name = dataset.elev_meas_ref");
    await expect(alternatives.nth(2)).toContainText("(unverified)");

    // A property no entry of its own fills says what does: the technical assurance's type is in the static list written
    // to TechnicalAssurances, and the value it gives is shown, not only that something fills it.
    await properties.getByTestId("templates-view-variable-osdu.data.TechnicalAssurances[].TechnicalAssuranceTypeID").click();
    await expect(properties.getByTestId("templates-view-properties-written-by")).toContainText("The static value of osdu.data.TechnicalAssurances, which gives it");
    await expect(properties.getByTestId("templates-view-properties-written-value"))
      .toHaveText("{$param.dataPartition}:reference-data--TechnicalAssuranceType:Unevaluated:");

    // A reference that may go out without its record says so where it is written.
    await properties.getByTestId("templates-view-variable-osdu.data.Curves[].LogCurveTypeID").click();
    await expect(entry.getByTestId("delivery-mapping-property-detail-unverified")).toBeVisible();

    // The search reads what fills a variable too, so a source column answers with every variable it reaches: the
    // vertical measurement, and the unit that measurement is found by.
    await properties.getByTestId("templates-view-variables-filter").fill("elev_meas_ref");
    await expect(properties.getByTestId("templates-view-variable-osdu.data.VerticalMeasurement.VerticalMeasurement")).toBeVisible();
    await expect(properties.getByTestId("templates-view-variables-count")).toContainText("2 of");
    await expect(properties.getByTestId("templates-view-variable-osdu.data.Name")).toHaveCount(0);
    await properties.getByTestId("templates-view-variables-filter-clear").click();

    // The Filled view leaves out what the mapping does not fill and the schema does not require; All adds it back, and
    // picking the view in view again returns to Filled.
    await expect(properties.getByTestId("templates-view-variable-osdu.data.ResourceHomeRegionID")).toHaveCount(0);
    await properties.getByTestId("templates-view-show-everything").click();
    await expect(properties.getByTestId("templates-view-variable-osdu.data")).toBeVisible();
    await properties.getByTestId("templates-view-show-everything").click();
    await expect(properties.getByTestId("templates-view-lens-filled")).toHaveAttribute("data-state", "on");

    // Missing answers whether the mapping satisfies the schema: the sample fills everything this record requires, which
    // the view's own count says before it is picked.
    await expect(properties.getByTestId("templates-view-show-missing")).toHaveText(/^Missing\s*0$/);
    await properties.getByTestId("templates-view-show-missing").click();
    await expect(properties.getByTestId("templates-view-variables-empty")).toContainText("Nothing the schema requires");
    await properties.getByTestId("templates-view-show-missing").click();

    // Narrowing to what nothing fills keeps the holders on the way, and drops what the mapping fills, an entry that
    // may leave its value out included: the mapping fills that variable either way.
    await properties.getByTestId("templates-view-show-gaps").click();
    await expect(properties.getByTestId("templates-view-variable-osdu.data.ResourceHomeRegionID")).toBeVisible();
    await expect(properties.getByTestId("templates-view-coverage-osdu.acl")).toHaveCount(0);
    await expect(properties.getByTestId("templates-view-coverage-osdu.data.Curves[].LogCurveBusinessValueID")).toHaveCount(0);
    await properties.getByTestId("templates-view-show-gaps").click();
    await expect(properties.getByTestId("templates-view-coverage-osdu.acl")).toHaveAttribute("data-coverage", "Always");

    // The mapping's YAML is analysed as the flows are: a key's documentation on hover, and nothing flagged in the sample.
    // The sample opens on its header comment and the editor draws only the lines in view, so it is scrolled until the
    // template key sits inside the window: the editor runs past the window's bottom edge, and a line drawn down there
    // takes no hover.
    await detail.getByTestId("delivery-mapping-tab-yaml").click();
    const yaml = detail.getByTestId("delivery-mapping-yaml");
    const templateKey = yaml.locator(".view-line").filter({ hasText: /^template:$/ });
    const windowBottom = (adminPage.viewportSize()?.height ?? 720) - 48;
    await expect(async () => {
      const drawn = await templateKey.count() === 0 ? null : await templateKey.boundingBox();
      if (drawn === null || drawn.y + drawn.height > windowBottom) {
        await yaml.hover();
        await adminPage.mouse.wheel(0, 100);
      }
      const box = await templateKey.boundingBox({ timeout: 1_000 });
      expect(box !== null && box.y + box.height <= windowBottom).toBe(true);
    }).toPass({ timeout: 30_000 });
    // The loop ends as soon as the key is inside the window, while the editor may still be scrolling smoothly from the
    // last wheel. The editor closes a hover that opens while it moves, and a mouse that stays put never opens one again,
    // so on a slow machine the first hover is lost: it is made again from off the editor until it holds.
    const hoverCard = adminPage.locator(".monaco-hover:not(.hidden)");
    await expect(async () => {
      await adminPage.mouse.move(0, 0);
      await templateKey.getByText("template", { exact: true }).hover();
      await expect(hoverCard).toContainText("The template the mapping fills, read from the template folders", { timeout: 3_000 });
    }).toPass({ timeout: 30_000 });
    await expect(yaml.locator(".squiggly-error, .squiggly-warning")).toHaveCount(0);

    // The record shape: the renderer's layout with placeholders, and the partition filled into the id once it is given.
    await detail.getByTestId("delivery-mapping-tab-shape").click();
    const shape = detail.getByTestId("delivery-mapping-shape-json");
    // The editor draws only the lines in view, so the check reads the id on the first lines rather than a deeper field.
    await expect(shape).toContainText("<delivery key from welldb", { timeout: 15_000 });
    await detail.getByTestId("delivery-mapping-shape-parameter-dataPartition").fill("dev");
    await expect(shape).toContainText("dev:work-product-component--WellLog:", { timeout: 15_000 });

    await detail.getByTestId("delivery-mapping-open-builder").click();

    await expect(adminPage.getByTestId("page-delivery-mapping-builder")).toBeVisible();
    await expect(adminPage.getByTestId("mapping-builder-opened-from")).toContainText("mappings/WellLog@1.4.0.yaml", { timeout: 30_000 });
    // The flow names its partitions, so the check renders in the one the builder offers it for, and says which.
    await expect(adminPage.getByTestId("mapping-builder-check-dataPartition")).toHaveValue("dev");
    await expect(adminPage.getByTestId("mapping-builder-check-source-dataPartition")).toContainText("one of the partitions the flow names");
    // The sample mapping checks against the imported cache, so it loads, passes and can be proposed.
    await expect(adminPage.getByTestId("mapping-builder-valid")).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("mapping-builder-propose")).toBeEnabled();

    // A property a static value above writes is no gap in the builder either: the list says where it is written and what
    // it is given, offers no cache entry for it, and opening it opens the entry that writes it.
    await adminPage.getByTestId("mapping-builder-variables-filter").fill("TechnicalAssuranceTypeID");
    const assurance = adminPage.getByTestId("mapping-builder-entry-summary-osdu.data.TechnicalAssurances[].TechnicalAssuranceTypeID");
    await expect(assurance.getByTestId("mapping-builder-inherited")).toContainText("In the static value of");
    await expect(assurance).toContainText("reference-data--TechnicalAssuranceType:Unevaluated:");
    await expect(adminPage.getByTestId("mapping-builder-use-cache-osdu.data.TechnicalAssurances[].TechnicalAssuranceTypeID")).toHaveCount(0);
    await rowWith(adminPage, "mapping-builder-variables", "mapping-builder-variable-osdu.data.TechnicalAssurances[].TechnicalAssuranceTypeID").click();
    await expect(adminPage.getByTestId("mapping-builder-entry-target")).toHaveText("osdu.data.TechnicalAssurances");
    await adminPage.getByTestId("mapping-builder-entry-cancel").click();
    await adminPage.getByTestId("mapping-builder-variables-filter").fill("");

    // A curve's unit is a coalesce: it opens as the first value of its alternatives, listed in the order they are tried.
    await rowWith(adminPage, "mapping-builder-variables", "mapping-builder-variable-osdu.data.Curves[].CurveUnit").click();
    await expect(adminPage.getByTestId("mapping-builder-entry-target")).toHaveText("osdu.data.Curves[].CurveUnit");
    await expect(adminPage.getByTestId("mapping-builder-entry-input-coalesce")).toHaveAttribute("data-state", "on");
    await expect(adminPage.getByTestId("mapping-builder-entry-alternatives").getByTestId(/^mapping-builder-entry-alternative-\d+$/)).toHaveCount(3);
    await expect(adminPage.getByTestId("mapping-builder-entry-alternative-2")).toContainText("(unverified)");

    // Each alternative is edited as an input of its own. A replace reading a cached table opens as one: the table, and the
    // key it matches on and the field it replaces by, both of which the table settles, so the draft leaves them out. A
    // spelling the table does not list is left as it is.
    await adminPage.getByTestId("mapping-builder-entry-alternative-edit-0").click();
    const alternative = adminPage.getByTestId("mapping-builder-alternative-editor");
    await expect(alternative.getByTestId("mapping-builder-entry-target")).toHaveText("osdu.data.Curves[].CurveUnit, alternative 1");
    await expect(alternative.getByTestId("mapping-builder-entry-input-coalesce")).toHaveCount(0);
    await expect(alternative.getByTestId("mapping-builder-entry-modifier-source-cache-0")).toHaveAttribute("data-state", "on");
    // UnitAlias itself: DepthUnitAlias, the partition's other unit table, ends in the same name.
    await expect(alternative.getByTestId("mapping-builder-entry-modifier-table-0")).toContainText(/(?<!Depth)UnitAlias/);
    await expect(alternative.getByTestId("mapping-builder-entry-modifier-match-0")).toContainText("the table's key");
    await expect(alternative.getByTestId("mapping-builder-entry-modifier-field-0")).toContainText("the table's only field");
    await expect(alternative.getByTestId("mapping-builder-entry-modifier-field-0")).toContainText("osdu_unit");
    await expect(alternative.getByTestId("mapping-builder-entry-modifier-otherwise-0")).toContainText("is left as it is");

    // Switching to values listed in the mapping leaves the table behind; switching back offers the partition's lookup tables.
    await alternative.getByTestId("mapping-builder-entry-modifier-source-pairs-0").click();
    await expect(alternative.getByTestId("mapping-builder-entry-modifier-from-0-0")).toBeVisible();
    await alternative.getByTestId("mapping-builder-entry-modifier-source-cache-0").click();
    await alternative.getByTestId("mapping-builder-entry-modifier-table-0").click();
    await expect(adminPage.getByRole("option").filter({ hasText: /(?<!Depth)UnitAlias/ })).toBeVisible();
    await expect(adminPage.getByRole("option").filter({ hasText: "key mnemonic" })).toBeVisible();
    await adminPage.keyboard.press("Escape");

    // Cancel leaves the alternative as it was and returns to the entry, which Cancel leaves as it was too.
    await alternative.getByTestId("mapping-builder-entry-cancel").click();
    await expect(alternative).toHaveCount(0);
    await expect(adminPage.getByTestId("mapping-builder-entry-alternative-0")).toContainText("replace from $cache.UnitAlias");
    await adminPage.getByTestId("mapping-builder-entry-cancel").click();
    await expect(adminPage.getByTestId("mapping-builder-entry-editor")).toHaveCount(0);
  });
});
