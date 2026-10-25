import { E2E } from "../playwright.config";
import { DELIVERY_FLOW, LOG_SOURCE } from "./global-setup";
import { stageWellLogRecords } from "./delivery-cli";
import { expect, test } from "./helpers";

// A record before it is sent, and a record as OSDU holds it. The Preview tab of a delivery flow renders one record on a
// node exactly as a delivery would and sends nothing: the scope's first record, or the one a key names. A record's page
// reads the record from OSDU through its flow's route, follows what it refers to, checks it against the schema of its
// kind, and compares what OSDU holds with what a delivery would send now.
//
// Nothing here reaches an OSDU: the flows reach the e2e stand-in, which answers the wellbore searches a render makes and
// the record reads, and holds the one well log this spec gives it, and a schema of its kind, for as long as the spec runs.
// The record page's test stages the flow's records itself (an intake, which sends nothing), so it reads them whatever ran
// before it; nothing is delivered.

/** A well log of the sample well database: its source key as the Records page shows it, and the wellbore it names. */
const LOG_KEY = "welldb:PROJECT_A/LOG-0004/1";
const LOG_WELLBORE = "Wellbore-B-2-B";

/** The kind of the well log the stand-in holds. */
const LOG_KIND = "osdu:wks:work-product-component--WellLog:1.4.0";

/** What the stand-in's Schema service holds for the well log's kind while the spec runs: a name that starts with "WellDB ". */
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

test.describe.serial("record preview and OSDU read", () => {
  test.afterAll(async ({ playwright }) => {
    // The stand-in forgets what this spec gave it, so a later spec meets the platform as the suite starts it.
    const standIn = await playwright.request.newContext();
    try {
      await standIn.delete(`${E2E.osdu.OSDU_URL}/__e2e/records`);
      await standIn.delete(`${E2E.osdu.OSDU_URL}/__e2e/schemas`);
    } finally {
      await standIn.dispose();
    }
  });

  test("the Preview tab renders the scope's first record and a named one, and says when a key names none", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-pipelines").click();
    await adminPage.getByTestId("filter-name").fill(DELIVERY_FLOW);
    await adminPage.getByTestId("repo-pipeline").filter({ hasText: DELIVERY_FLOW }).first().click();
    await expect(adminPage.getByTestId("page-pipeline-detail")).toBeVisible();
    await adminPage.getByTestId("pipeline-tabs").getByRole("tab", { name: /preview/i }).click();
    await expect(adminPage.getByTestId("delivery-panel-preview")).toBeVisible();

    // The flow's scope needs its log source, which it declares required with no default: without it there is no preview.
    const run = adminPage.getByTestId("preview-run");
    await expect(adminPage.getByTestId("preview-parameter-logSource")).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("preview-parameter-logSource-problem")).toContainText("parameter 'logSource' is required");
    await expect(run).toBeDisabled();
    await adminPage.getByTestId("preview-parameter-logSource").fill(LOG_SOURCE);
    await adminPage.getByTestId("preview-parameter-logSource").press("Escape");
    await expect(run).toBeEnabled();

    // No key: the scope's first record, rendered as a delivery would render it.
    await run.click();
    const result = adminPage.getByTestId("preview-result");
    await expect(result).toBeVisible({ timeout: 60_000 });
    await expect(adminPage.getByTestId("preview-asked")).toContainText("the first record of the scope");
    await expect(adminPage.getByTestId("preview-action")).toBeVisible();
    await expect(adminPage.getByTestId("preview-target")).toHaveAttribute("data-value", /work-product-component--WellLog/);
    await expect(adminPage.getByTestId("preview-document")).toContainText("work-product-component--WellLog", { timeout: 30_000 });

    // The route's requests in order, and the parquet chunk the well log's bulk data is, measured by its footer.
    await adminPage.getByTestId("preview-tab-steps").click();
    await expect(adminPage.getByTestId("preview-step").first()).toContainText("DDMS");
    await adminPage.getByTestId("preview-tab-payload").click();
    await expect(adminPage.getByTestId("preview-payload-files")).toContainText(".parquet");
    await expect(adminPage.getByTestId("preview-payload-files")).toContainText(/rows x \d+ columns/);

    // What the document refers to: the wellbore the render found by searching the platform, and the reference data the
    // cache gave it, none of them records of this ledger.
    await adminPage.getByTestId("preview-tab-references").click();
    // The reference is named by its type and unique part; the whole id is on hover and on copy.
    await expect(adminPage.getByTestId("preview-references")).toContainText(/Wellbore\s?Wellbore-[0-9A-Z-]+\s?data\.WellboreID/);
    await expect(adminPage.getByTestId("preview-references")).toContainText("not a record of the ledger");

    // A key as the Records page shows it names that record, though its log id holds a slash.
    await adminPage.getByTestId("preview-key").fill(LOG_KEY);
    await run.click();
    await expect(adminPage.getByTestId("preview-asked")).toContainText("read as a source key", { timeout: 60_000 });
    await expect(adminPage.getByTestId("preview-header")).toContainText("LOG-0004/1");

    // A key the table holds no row for is an answer, not a failure.
    await adminPage.getByTestId("preview-key").fill("PROJECT_A/no-such-log");
    await run.click();
    await expect(adminPage.getByTestId("preview-not-found")).toBeVisible({ timeout: 60_000 });
    await expect(adminPage.getByTestId("preview-reason")).toContainText("holds no row");
  });

  test("a record's page reads it from OSDU, follows what it refers to, and compares it with what a delivery would send", async ({ adminPage, request }) => {
    test.setTimeout(600_000);
    expect(stageWellLogRecords()).toContain("record(s)");

    // The lookup finds the record by the source key an operator holds; its row goes straight to what OSDU holds.
    await adminPage.getByTestId("nav-delivery-records").click();
    await expect(adminPage.getByTestId("page-delivery-records")).toBeVisible();
    await adminPage.getByTestId("delivery-lookup-search").fill(LOG_KEY);
    // The lookup writes its term into the address once typing settles; a click before that is taken back to the lookup.
    await expect(adminPage).toHaveURL(/q=welldb%3APROJECT_A%2FLOG-0004%2F1/);
    const row =adminPage.getByTestId("delivery-lookup-table").getByTestId("table-row").filter({ hasText: "LOG-0004/1" }).first();
    await expect(row).toBeVisible({ timeout: 30_000 });
    await row.getByTestId("open-in-osdu").click();
    await expect(adminPage.getByTestId("page-delivery-record")).toBeVisible();

    // The page opened on its OSDU tab reads the record at once. Nothing was delivered, so OSDU holds none.
    await expect(adminPage.getByTestId("record-tab-osdu")).toHaveAttribute("data-state", "active");
    await expect(adminPage.getByTestId("osdu-not-found")).toBeVisible({ timeout: 60_000 });

    // Once OSDU holds the record, the read shows it: its version, its access, and the wellbore it refers to.
    // The chip shows the id's type and unique part; the whole id rides on it as data, for the copy and for this.
    const chip = await adminPage.getByTestId("record-target").getAttribute("data-value");
    const targetId = /(\S+:work-product-component--WellLog:[0-9a-f]{32})/.exec(chip ?? "")?.[1];
    expect(targetId, `the record page names the OSDU id: ${chip}`).toBeDefined();
    const partition = targetId!.split(":")[0];
    const hold = await request.put(`${E2E.osdu.OSDU_URL}/__e2e/records`, {
      data: {
        id: targetId,
        kind: LOG_KIND,
        acl: { viewers: [E2E.osdu.OSDU_ACL_VIEWER], owners: [E2E.osdu.OSDU_ACL_OWNER] },
        legal: { legaltags: [E2E.osdu.OSDU_LEGAL_TAG], otherRelevantDataCountries: ["US"], status: "compliant" },
        data: { Name: "held by the stand-in", WellboreID: `${partition}:master-data--Wellbore:${LOG_WELLBORE}:` },
        createUser: "e2e-stand-in",
        createTime: "2026-09-25T00:00:00.000Z",
      },
    });
    expect(hold.ok()).toBe(true);
    const heldVersion = String(((await hold.json()) as { version: number }).version);

    await adminPage.getByTestId("record-osdu-read").click();
    await expect(adminPage.getByTestId("osdu-record")).toBeVisible({ timeout: 60_000 });
    // The inspector opens on the record's data; who may see it is the Access & legal view of the outline.
    await expect(adminPage.getByTestId("osdu-record-json")).toContainText("held by the stand-in", { timeout: 30_000 });
    await adminPage.getByTestId("osdu-outline-access").click();
    await expect(adminPage.getByTestId("osdu-record-viewers")).toContainText(E2E.osdu.OSDU_ACL_VIEWER);
    // The full document is the whole record as OSDU returned it, envelope and all, and a key in it steps to its branch.
    await adminPage.getByTestId("osdu-outline-document").click();
    await expect(adminPage.getByTestId("osdu-json")).toContainText("e2e-stand-in");
    // The record's content is where its name leads, so the location ends on the record rather than naming `data`.
    // Exactly the "data" key: a text filter is a substring, any case, which legal's otherRelevantDataCountries matches first.
    await adminPage.getByTestId("osdu-json").getByTestId("osdu-json-key").filter({ hasText: /^"data"$/ }).click();
    await expect(adminPage.getByTestId("osdu-crumb-record")).toHaveAttribute("aria-current", "location");
    await expect(adminPage.getByTestId("osdu-trail")).not.toContainText("Full document");
    await adminPage.getByTestId("osdu-mode-fields").click();

    // The explorer opens in a window of its own: the record named, the inspector filling the window and read as it
    // opens, and nothing of the workbench around it.
    const [explorer] = await Promise.all([adminPage.context().waitForEvent("page"), adminPage.getByTestId("record-osdu-popout").click()]);
    await expect(explorer.getByTestId("page-osdu-window")).toBeVisible({ timeout: 30_000 });
    await expect(explorer.getByTestId("osdu-record-json")).toContainText("held by the stand-in", { timeout: 60_000 });
    await expect(explorer.getByTestId("record-tabs")).toHaveCount(0);
    await explorer.close();

    // The well logs travel by the Wellbore DDMS route, and a Wellbore DDMS record is a storage record whose versions name
    // its bulk data, so its history is read through the storage service: the location bar offers the one version OSDU
    // holds of it, marked as the latest, and says how many there are.
    const version = adminPage.getByTestId("osdu-record-versions").getByTestId("osdu-record-version");
    await expect(version).toContainText(heldVersion);
    await expect(version).toContainText("latest");
    await expect(adminPage.getByTestId("osdu-no-history")).toHaveCount(0);
    await version.click();
    const versions = adminPage.getByTestId("osdu-version-menu");
    await expect(versions).toContainText("1 version in OSDU, newest first");
    await expect(versions.getByTestId("osdu-version")).toHaveCount(1);
    await expect(versions.getByTestId("osdu-version")).toContainText(heldVersion);
    await adminPage.keyboard.press("Escape");
    await expect(versions).toHaveCount(0);

    // The wellbore it refers to is a link where it stands in the record, read in turn through the same flow's route,
    // and takes the inspector's place after it on the trail; closing it steps back to the log.
    // The link is the record's name itself: a click opens it here, and the location bar then names the wellbore alone,
    // with the way back to the log, which says on hover where the log named it.
    const link = adminPage.getByTestId("osdu-record-link").first();
    await expect(link).toHaveAttribute("data-value", new RegExp(`master-data--Wellbore:${LOG_WELLBORE}`));
    await link.getByTestId("osdu-link-read").click();
    const linked = adminPage.getByTestId("osdu-linked");
    await expect(linked).toBeVisible();
    await expect(linked.getByTestId("osdu-record-json")).toContainText("FacilityName", { timeout: 60_000 });
    await expect(linked.getByTestId("osdu-trail")).toContainText(LOG_WELLBORE);
    await expect(linked.getByTestId("osdu-trail")).not.toContainText("WellboreID");
    await expect(linked.getByTestId("osdu-linked-close")).toHaveText("WellLog");
    await expect(linked.getByTestId("osdu-linked-close")).toHaveAttribute("title", /data\.WellboreID names this record/);
    await linked.getByTestId("osdu-linked-close").click();
    await expect(linked).toHaveCount(0);
    await expect(adminPage.getByTestId("osdu-record-json")).toContainText("held by the stand-in");

    // The record is checked against the schema of its kind as the explorer checks one, read through the same flow: the
    // Schema service holds one whose name pattern the stand-in's copy breaks, so the check finds that, and the name
    // carries the mark of it.
    const held = await request.put(`${E2E.osdu.OSDU_URL}/__e2e/schemas`, { data: { id: LOG_KIND, schema: LOG_SCHEMA } });
    expect(held.ok()).toBe(true);
    await adminPage.getByTestId("osdu-outline-validation").click();
    const verdict = adminPage.getByTestId("validation-verdict");
    await expect(verdict).toHaveAttribute("data-outcome", "invalid", { timeout: 60_000 });
    await expect(verdict).toContainText(LOG_KIND);
    const problem = verdict.getByTestId("validation-problem");
    await expect(problem).toHaveCount(1);
    await expect(problem).toContainText("data.Name");
    await expect(problem.getByTestId("validation-problem-found")).toHaveText("'held by the stand-in'");
    await expect(problem.getByTestId("validation-problem-expected")).toHaveText("text matching ^WellDB");
    await problem.getByTestId("validation-problem-open").click();
    const mark = adminPage.getByTestId("explorer-validation-mark").first();
    await expect(mark).toBeVisible();
    await mark.hover();
    await expect(adminPage.getByRole("tooltip")).toContainText("data.Name (pattern)");
    await adminPage.mouse.move(0, 0);

    // Render: the record's manifest built afresh from its source row, beside what OSDU holds (the stand-in's copy differs
    // in its name), with the mapping it was built with beside the one that built OSDU's copy, and what the next run would
    // do; the whole document is one toggle away.
    await adminPage.getByTestId("record-tab-render").click();
    await expect(adminPage.getByTestId("record-render-scope")).toContainText("manifest");
    await adminPage.getByTestId("record-render-run").click();
    await expect(adminPage.getByTestId("record-render-diff")).toBeVisible({ timeout: 60_000 });
    await expect(adminPage.getByTestId("record-render-counts")).toContainText(/changed|would be added|only in OSDU/);
    await expect(adminPage.getByTestId("record-render-now-mapping")).toContainText("@");
    await expect(adminPage.getByTestId("record-render-next")).toBeVisible();
    await adminPage.getByTestId("record-render-view-document").click();
    await expect(adminPage.getByTestId("record-render-document")).toBeVisible();
    await expect(adminPage.getByTestId("record-render-diff")).toBeHidden();
  });
});
