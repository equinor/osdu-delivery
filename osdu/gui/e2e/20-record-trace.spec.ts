import { E2E } from "../playwright.config";
import { DELIVERY_FLOW } from "./global-setup";
import { stageWellLogRecords } from "./delivery-cli";
import { expect, expectRecordsWorkbook, test } from "./helpers";

// Traceability from the product's front door (CLAUDE.md: traceability is the product). An operator holding a source key,
// an OSDU id or a file name finds the record without knowing which flow delivered it, and the record's page tells its
// journey: when the row was received, when it was planned, every dispatch and what OSDU answered, and where it stands.
// A batch is undoable from its submission's page, aimed at what that submission delivered.
//
// The spec stages the well database's well logs of the COMPOSITE scope itself (an intake), so the ledger holds pending
// records with a queued document and no dispatch yet whatever ran before it; nothing here reaches an OSDU, and no
// removal is run.

test.describe.serial("record trace", () => {
  test("a record is found from what an operator holds, and its page tells its journey", async ({ adminPage }) => {
    test.setTimeout(600_000);
    expect(stageWellLogRecords()).toContain("record(s)");

    // The home page's field opens the Records page looked up for the term.
    await adminPage.goto("/");
    await expect(adminPage.getByTestId("home-delivery")).toBeVisible({ timeout: 60_000 });
    // A source key is the source system and the row's key columns (welldb:PROJECT_A/LOG-0004/1: the well database's
    // project and the log id); the lookup is a prefix.
    await adminPage.getByTestId("home-find-record-term").fill("welldb:PROJECT_A");
    await adminPage.getByTestId("home-find-record-go").click();
    await expect(adminPage.getByTestId("page-delivery-records")).toBeVisible();
    await expect(adminPage).toHaveURL(/q=welldb%3APROJECT_A/);
    await expect(adminPage.getByTestId("delivery-lookup-search")).toHaveValue("welldb:PROJECT_A");

    // The lookup lists every record whose source key starts with the term, across flows, each naming its flow.
    const table = adminPage.getByTestId("delivery-lookup-table");
    await expect(table).toBeVisible();
    const rows = table.getByTestId("table-row");
    await expect(rows.first()).toBeVisible({ timeout: 30_000 });
    await expect(rows.first()).toContainText("PROJECT_A");
    await expect(rows.first()).toContainText(DELIVERY_FLOW);

    // Every row says which of its values answered, so a hit explains itself.
    await expect(rows.first().getByTestId("lookup-matched")).toBeVisible();

    // The wellbore name the mapping declares as an identity finds it, which is what an operator actually holds. The well
    // database's log id does too, and neither is the start of the source key. The address carries each term encoded.
    for (const [term, url] of [["Wellbore B-2 B", /q=Wellbore(\+|%20)B-2(\+|%20)B/], ["LOG-0004/1", /q=LOG-0004%2F1/]] as const) {
      await adminPage.getByTestId("delivery-lookup-search").fill(term);
      await expect(adminPage).toHaveURL(url);
      await expect(rows.first()).toBeVisible({ timeout: 30_000 });
      await expect(rows.first().getByTestId("lookup-matched")).toContainText(term);
    }

    await adminPage.getByTestId("delivery-lookup-search").fill("welldb:PROJECT_A");
    await expect(adminPage).toHaveURL(/q=welldb%3APROJECT_A/);
    await expect(rows.first()).toContainText("PROJECT_A", { timeout: 30_000 });

    // A state narrows the same lookup; a state nothing is in leaves it empty rather than wrong.
    await adminPage.getByTestId("delivery-lookup-status").click();
    await adminPage.getByRole("option", { name: "deleted" }).click();
    await expect(adminPage).toHaveURL(/status=deleted/);
    await expect(table.getByText(/No record the filters leave starts with that/)).toBeVisible({ timeout: 30_000 });
    await adminPage.getByTestId("delivery-lookup-status").click();
    await adminPage.getByRole("option", { name: "pending" }).click();
    // The empty answer to the last state stays on screen until this one arrives, so the row is waited for by what it holds.
    await expect(adminPage).toHaveURL(/status=pending/);
    await expect(rows.first()).toContainText("PROJECT_A", { timeout: 30_000 });

    // What the lookup lists downloads as an Excel workbook, each record with the flow that delivers it.
    const [workbook] = await Promise.all([
      adminPage.waitForEvent("download"),
      adminPage.getByTestId("delivery-lookup-export").click(),
    ]);
    expect(workbook.suggestedFilename()).toMatch(/^records-.*pending\.xlsx$/);
    expectRecordsWorkbook(await workbook.path());

    // A row opens the record, whose timeline tells the chapters it has lived, newest first: the first began when its row
    // arrived, the ledger picked it up and reserved its OSDU id, and the one it is in now waits to be sent. A fresh estate
    // has lived that one chapter only; the suite's estate lives on between runs, and a record an operator held and
    // released since has a chapter more. Nothing was sent, and no row of the timeline stands for its current state, which
    // the header and the milestones say.
    await rows.first().click();
    await expect(adminPage.getByTestId("page-delivery-record")).toBeVisible();
    await expect(adminPage.getByTestId("record-journey")).toBeVisible();
    await expect(adminPage.getByTestId("record-milestones")).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("milestone-landed")).toContainText("not yet");
    await expect(adminPage.getByTestId("journey-now")).toHaveCount(0);
    await expect(adminPage.getByTestId("journey-planned")).toBeVisible();
    await expect(adminPage.getByTestId("journey-planned")).toContainText("Picked up for delivery");
    await expect(adminPage.getByTestId("journey-planned")).toContainText("OSDU id reserved for this flow");
    const chapters = adminPage.getByTestId("journey-chapter");
    await expect(chapters.first()).toBeVisible();
    await expect(chapters.last().getByTestId("journey-chapter-trigger")).toContainText("Row arrived in silver.WellLog");
    await expect(chapters.first().getByTestId("journey-chapter-outcome")).toContainText("Queued to send");

    // The row's arrival, before the ledger: the file it came in from and when it reached the ingestion table. They are
    // the record's own facts; the runs that made them are their evidence, found by the file the landing processed and
    // by the table the ingestion run was writing when it inserted the row.
    await expect(adminPage.getByTestId("milestone-file")).toContainText("welllog_", { timeout: 30_000 });
    await expect(adminPage.getByTestId("milestone-file")).not.toContainText("not recorded");
    await expect(adminPage.getByTestId("milestone-loaded")).toContainText("silver.WellLog");
    await expect(adminPage.getByTestId("milestone-changed")).toContainText("unchanged since it was loaded");

    // The timeline holds the changes of this record's row, not the pipeline's runs: the landing that brought its file
    // in, and the load that inserted the row, which names the ingestion run that wrote it.
    const journey = adminPage.getByTestId("record-journey");
    await expect(journey.getByTestId("journey-source-landing")).toContainText("File landed by welldb-welllog-01-header-pre");
    await expect(journey.getByTestId("journey-source-loaded")).toContainText("Row loaded into silver.WellLog");
    await expect(journey.getByTestId("journey-source-loaded")).toContainText("written by");
    await expect(journey.getByTestId("journey-source-changed")).toHaveCount(0);

    // With every run named there is nothing missing to explain, so the note is not rendered at all.
    await expect(adminPage.getByTestId("record-chain-note")).toHaveCount(0);

    // An entry opens to the rest of what is known about it: the ingestion run that wrote the row, and the landing.
    await journey.getByTestId("journey-source-loaded").getByTestId("journey-event-toggle").click();
    const detail = journey.getByTestId("journey-source-loaded").getByTestId("journey-event-detail");
    await expect(detail).toContainText("welldb-welllog-02-header-ing");
    await expect(detail).toContainText("welldb-welllog-01-header-pre");

    // The Source tab names the ingestion file and row the record was staged from. Every long value it shows is clipped
    // to its own column and copyable: a value wider than its cell used to run under the value beside it.
    await adminPage.getByTestId("record-tab-source").click();
    await expect(adminPage.getByTestId("record-origin")).toContainText("welllog_");
    await expect(adminPage.getByTestId("copy-record-origin")).toBeVisible();
    const fits = await adminPage.evaluate(() => Array.from(document.querySelectorAll("span[style*='max-width']"))
      .every((span) => {
        const parent = span.parentElement;
        return parent === null || span.getBoundingClientRect().width <= parent.getBoundingClientRect().width + 1;
      }));
    expect(fits).toBe(true);

    // The Render tab sets the mapping and cache that rendered the delivered document beside those a render uses now;
    // this record was never delivered, so it says so, and offers the render.
    await adminPage.getByTestId("record-tab-render").click();
    await expect(adminPage.getByTestId("record-render-delivered-mapping")).toContainText("not delivered");
    await expect(adminPage.getByTestId("record-render-run")).toBeEnabled();

    // Removing the one record opens the removal surface, which says the record was never delivered; nothing is sent.
    await adminPage.getByTestId("record-delete").click();
    await expect(adminPage.getByTestId("removal-dialog")).toBeVisible();
    await expect(adminPage.getByTestId("removal-never-delivered")).toBeVisible({ timeout: 30_000 });
    await adminPage.getByTestId("removal-cancel").click();
    await expect(adminPage.getByTestId("removal-dialog")).toHaveCount(0);

    // The record's submission is the batch: what it delivered is its own to reverse, and this one delivered nothing, so it
    // offers no reversal.
    await adminPage.getByTestId("record-submission-link").click();
    await expect(adminPage.getByTestId("page-delivery-submission")).toBeVisible();
    await expect(adminPage.getByTestId("submission-delivered-records")).toContainText("(0)", { timeout: 30_000 });
    await expect(adminPage.getByTestId("submission-reverse")).toHaveCount(0);

    // On its flow's Submissions tab a row opens the submission and carries no id of its own; its icon opens the run that
    // coordinated it. The tab lists a flow's newest submissions and every run adds one, so on an estate kept from run to run
    // this submission can have left the list: the first row's icon is the one followed, not this submission's run.
    const submissionPage = adminPage.url();
    await expect(adminPage.getByTestId(/^submission-run-[0-9a-f-]{36}$/).first()).toBeVisible();
    await adminPage.getByTestId("submission-pipeline-link").click();
    await adminPage.getByTestId("pipeline-tabs").getByRole("tab", { name: /submissions/i }).click();
    const runIcon = adminPage.getByTestId("delivery-submissions-table").locator('a[href^="/runs/"]').first();
    await expect(runIcon).toBeVisible({ timeout: 30_000 });
    const runPath = await runIcon.getAttribute("href");
    await runIcon.click();
    await expect(adminPage.getByTestId("page-run-detail")).toBeVisible();
    expect(new URL(adminPage.url()).pathname).toBe(runPath);
    await adminPage.goto(submissionPage);
    await expect(adminPage.getByTestId("page-delivery-submission")).toBeVisible();

    // Its two record sets are two links: what it delivered (nothing yet), and what it last planned (every record).
    await adminPage.getByTestId("submission-delivered-records").click();
    await expect(adminPage.getByTestId("delivery-records-clear-delivered")).toBeVisible();
    await expect(adminPage.getByTestId("delivery-records-table").getByText(/No records match/)).toBeVisible({ timeout: 30_000 });
    await adminPage.getByTestId("delivery-records-clear-delivered").click();
    await expect(adminPage.getByTestId("delivery-records-clear-delivered")).toHaveCount(0);
    await adminPage.goBack();
    await adminPage.goBack();
    await expect(adminPage.getByTestId("page-delivery-submission")).toBeVisible();
    await adminPage.getByTestId("submission-records").click();
    await expect(adminPage.getByTestId("delivery-records-clear-submission")).toBeVisible();
    await expect(adminPage.getByTestId("delivery-records-table").getByTestId("table-row").first()).toBeVisible({ timeout: 30_000 });

    // Records has a place in the navigation of its own, and opens on what the delivery system last took in rather
    // than on an empty page: the newest records of every flow, each one a click from its own journey.
    await adminPage.getByTestId("nav-delivery-records").click();
    await expect(adminPage.getByTestId("page-delivery-records")).toBeVisible();
    await expect(adminPage.getByTestId("delivery-lookup-search")).toHaveValue("");
    await expect(adminPage.getByTestId("delivery-lookup-caption")).toBeVisible();
    const latest = adminPage.getByTestId("delivery-lookup-table").getByTestId("table-row");
    await expect(latest.first()).toBeVisible({ timeout: 30_000 });

    // Nothing was typed, so no row claims a value matched it.
    await expect(latest.first().getByTestId("lookup-matched")).toHaveCount(0);
    await latest.first().click();
    await expect(adminPage.getByTestId("page-delivery-record")).toBeVisible();
    await expect(adminPage.getByTestId("record-journey")).toBeVisible();
    await expect(adminPage.getByTestId("record-milestones")).toBeVisible({ timeout: 30_000 });
  });

  test("a delivered try says why it was sent, and one delivered before the ledger kept reasons says it was not recorded", async ({ adminPage }) => {
    // The e2e estate sends nothing to an OSDU, so the record's tries are given the two deliveries the ledger writes: the one
    // a mapping edit of acl.viewers sent with the source unchanged, and an older one delivered before the ledger kept reasons.
    const delivered = (deliveryKey: string, attemptId: number, at: string, targetVersion: number, reason: object) => ({
      attemptId, deliveryKey, submissionId: null, runId: null, worker: "node-1", startedUtc: at, completedUtc: at, outcome: "delivered",
      phase: "metadata", metadataHash: null, payloadHash: null, targetVersion, error: null, result: { steps: [] }, workBatch: 0,
      sourceFileName: null, sourceRowNumber: null, sourceUpdatedUtc: null, reason,
    });
    await adminPage.route(`${E2E.apiBaseUrl}/api/v1/delivery/records/*/*/attempts**`, async (route) => {
      const response = await route.fetch();
      const attempts = await response.json() as { deliveryKey: string }[];
      const key = new URL(route.request().url()).pathname.split("/").at(-2) ?? "";
      await route.fulfill({
        response,
        json: [
          delivered(key, 900002, "2026-11-21T08:00:00Z", 3, {
            recorded: true,
            action: "update-metadata",
            summary: "mapping WellLog@1.4.0 changed (852db64); acl.viewers changed",
            triggers: [{
              kind: "mapping-changed", from: "WellLog@1.4.0", to: "WellLog@1.4.0",
              fromFingerprint: "1f0c8b2e44d9a7c6b5e3f2a1908d7c6b5a4f3e2d1c0b9a8f7e6d5c4b3a291807",
              toFingerprint: "9a8b7c6d5e4f30211f0e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a6978",
              fromCommit: "268b2527d1c04e2fa3b9c8d7e6f5a4b3c2d1e0f9", toCommit: "852db64a9c0e4f7b8d1e2a3b4c5d6e7f80912345",
            }],
            paths: { compared: true, added: 0, removed: 0, changed: 1, listed: [{ path: "acl.viewers", change: "changed" }] },
          }),
          delivered(key, 900001, "2026-11-17T08:00:00Z", 1, { recorded: false, summary: "reason not recorded" }),
          {
            ...delivered(key, 900003, "2026-11-22T08:00:00Z", 3, {
              recorded: true, action: "none", summary: "cache moved to 20261122T020000Z; renders what OSDU holds",
              triggers: [{ kind: "cache-changed", from: "20261121T020000Z", to: "20261122T020000Z" }],
            }),
            outcome: "skipped", phase: "identical", worker: "intake", targetVersion: null,
          },
          ...attempts,
        ],
      });
    });

    await adminPage.getByTestId("nav-delivery-records").click();
    await expect(adminPage.getByTestId("page-delivery-records")).toBeVisible();
    const latest = adminPage.getByTestId("delivery-lookup-table").getByTestId("table-row");
    await expect(latest.first()).toBeVisible({ timeout: 30_000 });
    await latest.first().click();
    const journey = adminPage.getByTestId("record-journey");
    await expect(journey).toBeVisible();

    // The delivery is titled with what sent it; the older one says no more than the ledger knows.
    const tries = journey.getByTestId("journey-attempt-delivered");
    await expect(tries).toHaveCount(2, { timeout: 30_000 });
    const sent = tries.filter({ hasText: "acl.viewers changed" });
    await expect(sent).toContainText("Delivered: mapping WellLog@1.4.0 changed (852db64); acl.viewers changed");
    const older = tries.filter({ hasNotText: "acl.viewers" });
    await expect(older.getByTestId("journey-reason-missing")).toHaveText("reason not recorded");

    // Opened, it names what moved, the mapping's fingerprint and estate commit on each side, and the path that changed.
    await sent.getByTestId("journey-event-toggle").click();
    const reason = sent.getByTestId("journey-reason");
    await expect(reason.getByTestId("journey-reason-trigger")).toHaveAttribute("data-kind", "mapping-changed");
    await expect(reason.getByTestId("journey-reason-trigger")).toContainText("Mapping changed");
    await expect(reason.getByTestId("journey-reason-was")).toHaveText("WellLog@1.4.0 · 1f0c8b2e44d9 · commit 268b252");
    await expect(reason.getByTestId("journey-reason-now")).toHaveText("WellLog@1.4.0 · 9a8b7c6d5e4f · commit 852db64");
    await expect(reason.getByTestId("journey-reason-paths")).toContainText("Paths: 1 changed");
    await expect(reason.getByTestId("journey-reason-path")).toHaveAttribute("data-change", "changed");
    await expect(reason.getByTestId("journey-reason-path")).toContainText("acl.viewers");

    await older.getByTestId("journey-event-toggle").click();
    await expect(older.getByTestId("journey-reason-not-recorded")).toHaveText("Why it was sent: reason not recorded");
    // A render that sent nothing says what moved to render it: the cache reached the record and changed nothing.
    const unchanged = journey.getByTestId("journey-attempt-skipped").filter({ hasText: "renders what OSDU holds" });
    await expect(unchanged).toContainText("Nothing to send: cache moved to 20261122T020000Z; renders what OSDU holds");
    await unchanged.getByTestId("journey-event-toggle").click();
    await expect(unchanged.getByTestId("journey-reason")).toContainText("What moved");
    await expect(unchanged.getByTestId("journey-reason-trigger")).toHaveAttribute("data-kind", "cache-changed");
    await adminPage.unroute(`${E2E.apiBaseUrl}/api/v1/delivery/records/*/*/attempts**`);
  });
});
