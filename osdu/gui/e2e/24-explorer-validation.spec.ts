import { E2E } from "../playwright.config";
import { expect, test } from "./helpers";

// The explorer's checks of what OSDU holds against what OSDU expects: a record against the schema the partition's Schema
// service holds for its kind, each problem opening its element and marking its field, and the records a search finds,
// counted by the rules they break.
//
// Nothing here reaches an OSDU: the stand-in holds, for as long as the spec runs, the schema of its wellbores' kind, the
// schema that one refers to by id (so the check reads and bundles both), one wellbore of the spec's own, whose name breaks
// the pattern, whose well is held nowhere and whose meta is null as a record with none can read, and the example record
// the data definitions publish for the kind, which the guidance quotes. Nothing is delivered and nothing is written.

const PARTITION = E2E.osdu.OSDU_DATA_PARTITION;

/** The kind the stand-in's wellbores are of; no synced repository holds a template of it, so the Schema service's is the only schema. */
const KIND = "osdu:wks:master-data--Wellbore:1.1.0";

/** The schema the wellbore's refers to by id for its facility: a name that starts with "Wellbore ", as the platform's wellbores do but two. */
const FACILITY = "osdu:wks:AbstractE2eFacility:1.0.0";

/** The wellbore this spec holds: its name breaks the facility's pattern, and the well it names is held nowhere. */
const OWN = `${PARTITION}:master-data--Wellbore:E2E-VALIDATE-1`;

/** The well the spec's wellbore names, which the stand-in does not hold. */
const MISSING_WELL = `${PARTITION}:master-data--Well:E2E-NO-SUCH-WELL`;

/** The example record the data definitions publish for the kind, where the guidance finds how OSDU writes each value. */
const EXAMPLE = {
  path: "Examples/master-data/Wellbore.1.1.0.json",
  record: { kind: KIND, data: { FacilityName: "Wellbore A/1-F-1 A", WellID: "namespace:master-data--Well:Well-A-1-F-1:" } },
};

/** The wellbore's schema as the Schema service answers it: the facility's by reference, and the well a wellbore names. */
const WELLBORE_SCHEMA = {
  $schema: "http://json-schema.org/draft-07/schema#",
  "x-osdu-schema-source": KIND,
  type: "object",
  required: ["kind", "acl", "legal"],
  properties: {
    kind: { type: "string" },
    meta: { type: "array", items: { type: "object" } },
    data: {
      allOf: [
        { $ref: FACILITY },
        {
          type: "object",
          properties: {
            WellID: {
              type: "string",
              pattern: "^[A-Za-z0-9_.-]+:master-data--Well:[A-Za-z0-9_.:%-]+:[0-9]*$",
              "x-osdu-relationship": [{ GroupType: "master-data", EntityType: "Well" }],
            },
          },
        },
      ],
    },
  },
};

const FACILITY_SCHEMA = {
  $schema: "http://json-schema.org/draft-07/schema#",
  "x-osdu-schema-source": FACILITY,
  type: "object",
  required: ["FacilityName"],
  properties: { FacilityName: { type: "string", pattern: "^Wellbore " } },
};

test.describe.serial("explorer validation", () => {
  test.beforeAll(async ({ playwright }) => {
    const standIn = await playwright.request.newContext();
    try {
      for (const [id, schema] of [[KIND, WELLBORE_SCHEMA], [FACILITY, FACILITY_SCHEMA]] as const) {
        const held = await standIn.put(`${E2E.osdu.OSDU_URL}/__e2e/schemas`, { data: { id, schema } });
        expect(held.ok()).toBe(true);
      }

      expect((await standIn.put(`${E2E.osdu.OSDU_URL}/__e2e/examples`, { data: EXAMPLE })).ok()).toBe(true);

      const record = await standIn.put(`${E2E.osdu.OSDU_URL}/__e2e/records`, {
        data: {
          id: OWN,
          kind: KIND,
          acl: { viewers: [E2E.osdu.OSDU_ACL_VIEWER], owners: [E2E.osdu.OSDU_ACL_OWNER] },
          legal: { legaltags: [E2E.osdu.OSDU_LEGAL_TAG], otherRelevantDataCountries: ["US"], status: "compliant" },
          meta: null,
          data: { FacilityName: "E2E validate", WellID: `${MISSING_WELL}:` },
          createUser: "e2e-stand-in",
          createTime: "2026-10-04T00:00:00.000Z",
        },
      });
      expect(record.ok()).toBe(true);
    } finally {
      await standIn.dispose();
    }
  });

  test.afterAll(async ({ playwright }) => {
    // The stand-in forgets what this spec gave it, so a later spec meets the platform as the suite starts it.
    const standIn = await playwright.request.newContext();
    try {
      await standIn.delete(`${E2E.osdu.OSDU_URL}/__e2e/records`);
      await standIn.delete(`${E2E.osdu.OSDU_URL}/__e2e/schemas`);
      await standIn.delete(`${E2E.osdu.OSDU_URL}/__e2e/examples`);
    } finally {
      await standIn.dispose();
    }
  });

  test("checks a record against the Schema service's schema of its kind, and opens what is wrong", async ({ adminPage }) => {
    await adminPage.goto(`/delivery/explorer?id=${encodeURIComponent(OWN)}`);
    const record = adminPage.getByTestId("explorer-record");
    await expect(record.getByTestId("osdu-record-name")).toHaveText("E2E validate", { timeout: 60_000 });

    // The check runs when its view opens, against what the Schema service holds for the record's kind.
    await record.getByTestId("osdu-outline-validation").click();
    const verdict = record.getByTestId("validation-verdict");
    await expect(verdict).toHaveAttribute("data-outcome", "invalid", { timeout: 60_000 });
    await expect(verdict).toContainText(KIND);

    // Two problems: the name breaks the pattern of the schema the kind's refers to, which the check read and bundled; and
    // the well the record names, looked up in storage, is held nowhere.
    const problems = verdict.getByTestId("validation-problem");
    await expect(problems).toHaveCount(2);
    const name = problems.filter({ hasText: "data.FacilityName" });
    await expect(name).toContainText("pattern");
    await expect(name).toContainText("E2E validate");
    await expect(verdict.getByTestId("validation-references")).toHaveText("1 reference: 1 missing");
    await expect(verdict.getByTestId("validation-missing-id")).toContainText("E2E-NO-SUCH-WELL");
    await expect(verdict.getByTestId("validation-missing-id")).toContainText("data.WellID");

    // Each problem says what was found, what the schema takes there, and how to fix it, with OSDU's own example quoted.
    await expect(name.getByTestId("validation-problem-found")).toHaveText("'E2E validate'");
    await expect(name.getByTestId("validation-problem-expected")).toHaveText("text matching ^Wellbore");
    await expect(name.getByTestId("validation-problem-advice")).toContainText("Change FacilityName so it matches ^Wellbore . For example: Wellbore A/1-F-1 A");
    await expect(problems.filter({ hasText: "data.WellID" }).getByTestId("validation-problem-advice")).toContainText("Deliver the record");
    await name.getByTestId("validation-problem-details-toggle").click();
    await expect(name.getByTestId("validation-problem-details-osdu-example")).toHaveText("Wellbore A/1-F-1 A");
    await expect(name.getByTestId("validation-problem-details-pattern-0")).toHaveText("^Wellbore ");
    await expect(verdict.getByTestId("validation-example-source")).toContainText("release v0.30.0");

    // Its meta is null, which is how a stored record with no meta can read: it is read as absent, and the check says so.
    await verdict.getByTestId("validation-explained").hover();
    await expect(adminPage.getByRole("tooltip")).toContainText("meta is null");
    await adminPage.mouse.move(0, 0);

    // A problem opens its element as fields, where each field a problem sits in carries a mark that says what is wrong.
    await name.getByTestId("validation-problem-open").click();
    await expect(record.locator('[data-testid="explorer-element-query"][data-path="data.FacilityName"]')).toBeVisible();
    const marks = record.getByTestId("explorer-validation-mark");
    await expect(marks).toHaveCount(2);
    await marks.first().hover();
    await expect(adminPage.getByRole("tooltip")).toContainText("data.FacilityName (pattern)");
    await expect(adminPage.getByRole("tooltip")).toContainText("Fix: Change FacilityName so it matches ^Wellbore");

    // No synced repository holds a template of the kind, so a check against a repository's template says so rather than
    // passing the record.
    await record.getByTestId("osdu-outline-validation").click();
    await record.getByTestId("explorer-validate-template").click();
    await expect(record.getByTestId("explorer-validate-problem")).toContainText(`No synced repository holds a template of ${KIND}`, { timeout: 60_000 });
    await expect(record.getByTestId("validation-verdict")).toHaveCount(0);

    // Back on the Schema service's schema, the verdict read before is shown again.
    await record.getByTestId("explorer-validate-osdu").click();
    await expect(record.getByTestId("validation-verdict")).toHaveAttribute("data-outcome", "invalid", { timeout: 60_000 });
  });

  test("checks the records a search finds and counts them by the rules they break", async ({ adminPage }) => {
    await adminPage.goto(`/delivery/explorer?kind=${encodeURIComponent("*:*:master-data--Wellbore:*")}`);
    const grid = adminPage.getByTestId("explorer-grid");
    // The platform's seven wellbores and the spec's own.
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(8, { timeout: 60_000 });

    await adminPage.getByTestId("explorer-validate-records").click();
    const list = adminPage.getByTestId("explorer-validate-list");
    const counts = list.getByTestId("explorer-validate-list-counts");
    await expect(counts).toContainText("8 of 8 records checked", { timeout: 60_000 });
    // The five named "Wellbore ..." are valid; the two development wellbores and the spec's own break the name's pattern.
    await expect(counts.locator('[data-outcome="valid"]')).toContainText("5");
    await expect(counts.locator('[data-outcome="invalid"]')).toContainText("3");
    await expect(list.getByTestId("explorer-validate-list-cut")).toHaveCount(0);

    // The rule broken most often first: the name's pattern, by three records, with one of them to open.
    const rules = list.getByTestId("explorer-validate-list-rule");
    await expect(rules.first()).toContainText("data.FacilityName");
    await expect(rules.first()).toContainText("pattern");
    await expect(rules.first()).toContainText("3 records");
    await expect(rules.first().getByTestId("explorer-validate-list-expected")).toContainText("text matching ^Wellbore");
    await expect(rules.first().getByTestId("explorer-validate-list-advice")).toContainText("Change FacilityName so it matches ^Wellbore . For example: Wellbore A/1-F-1 A");

    // The spec's own wellbore holds meta null: counted as read as absent, not as a problem.
    await expect(list.getByTestId("explorer-validate-list-note").filter({ hasText: "meta null or empty" })).toContainText("1 record(s)");

    // Each record with its outcome; the spec's own opens in the explorer.
    const records = list.getByTestId("explorer-validate-list-record");
    await expect(records).toHaveCount(8);
    await records.filter({ hasText: "E2E-VALIDATE-1" }).click();
    await expect(list).toHaveCount(0);
    await expect(adminPage.getByTestId("explorer-record").getByTestId("osdu-record-name")).toHaveText("E2E validate", { timeout: 60_000 });
  });
});
