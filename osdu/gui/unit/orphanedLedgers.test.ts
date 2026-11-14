// What the Orphaned ledgers page says of a missing document (osdu/docs/reference/concepts/renaming-a-pipeline.md, Missing
// documents): what it left behind in words, where each ledger goes under the flow picked, which documents a search and a
// kind keep, the totals the page heads with, and what a deletion would do and did.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  deletedText, deleteEffect, deleteLabel, deletePartition, deleteTitle, deletionTotals, destinationOf, documentKey, heldCount, heldText, ledgerLabel, leftRecently, matches,
  RECENT_MS, recordsHref, SHARES, suggestedOf, totals, trailHref, type MissingDocument,
} from "../src/features/delivery/orphanedLedgers.ts";

const SOURCE: MissingDocument = {
  kind: "delivery",
  flowName: "petrel",
  relativePath: "flows/petrel.yaml",
  rows: 5431,
  claimed: 5430,
  ledgers: [
    { ledger: "w", partition: "dev", interface: "wellbores", rows: 5000, claimed: 5000 },
    { ledger: "l", partition: "dev", interface: "logs", rows: 431, claimed: 430 },
  ],
  candidates: [
    {
      flowName: "petrel-v2",
      relativePath: "flows/petrel-v2.yaml",
      shares: ["mapping", "folder"],
      takes: 1,
      suggested: true,
      plan: [{ ledger: "w", toInterface: "wellbores", toLedgerName: "petrel-v2/wellbores" }, { ledger: "l", toInterface: null, toLedgerName: null }],
    },
    { flowName: "other", relativePath: "flows/other.yaml", shares: [], takes: 2, suggested: false, plan: [] },
  ],
};

const DIMENSIONS: MissingDocument = {
  kind: "dimension",
  flowName: "welldb-dimensions",
  relativePath: null,
  rows: 1,
  claimed: 0,
  ledgers: [{ ledger: "d", partition: "test", interface: "", rows: 1, claimed: 0 }],
  candidates: [{ flowName: "welldb-dimensions-v2", relativePath: "flows/d.yaml", shares: [], takes: 1, suggested: false, plan: [{ ledger: "d", toInterface: "", toLedgerName: "welldb-dimensions-v2@test" }] }],
};

describe("what a missing document left behind", () => {
  it("counts a delivery flow's records and the OSDU ids they claimed", () => {
    assert.equal(heldText("delivery", 5431, 5430), "5,431 records, 5,430 with an OSDU id");
    assert.equal(heldText("delivery", 2, 2), "2 records, each with its OSDU id");
    assert.equal(heldText("delivery", 1, 1), "1 record, each with its OSDU id");
  });

  it("counts another kind's history by what it is", () => {
    assert.equal(heldText("assertion", 1, 0), "1 assertion run");
    assert.equal(heldCount("inventory", 3), "3 inventories");
    assert.equal(heldCount("download", 1), "1 download");
    assert.equal(heldCount("download", 2), "2 downloads");
    assert.equal(heldText("dimension", 12, 0), "12 dimensions");
  });

  it("names a ledger by its interface and partition, or its partition alone", () => {
    assert.equal(ledgerLabel(SOURCE.ledgers[0]), "wellbores in dev");
    assert.equal(ledgerLabel(DIMENSIONS.ledgers[0]), "test");
  });
});

describe("where each ledger goes", () => {
  it("goes to the interface of the same name, and stays orphaned without one", () => {
    const flow = suggestedOf(SOURCE)!;
    assert.equal(flow.flowName, "petrel-v2");
    assert.equal(destinationOf("delivery", flow, SOURCE.ledgers[0]), "petrel-v2/wellbores");
    assert.equal(destinationOf("delivery", flow, SOURCE.ledgers[1]), null);
  });

  it("goes to another kind's ledger by its name, and is suggested only when one flow alone ranks first", () => {
    assert.equal(destinationOf("dimension", DIMENSIONS.candidates[0], DIMENSIONS.ledgers[0]), "welldb-dimensions-v2@test");
    assert.equal(suggestedOf(DIMENSIONS), undefined);
  });
});

describe("the list", () => {
  it("keeps the documents a search of their name or file and a kind keep", () => {
    assert.equal(matches(SOURCE, "PETREL", null), true);
    assert.equal(matches(SOURCE, "flows/petrel", "delivery"), true);
    assert.equal(matches(SOURCE, "", "dimension"), false);
    assert.equal(matches(DIMENSIONS, "flows", null), false);
  });

  it("heads with the delivery records and claimed ids, and how many have a suggestion", () => {
    assert.deepEqual(totals([SOURCE, DIMENSIONS]), { documents: 2, records: 5431, claimed: 5430, history: 1, suggested: 1 });
  });

  it("links a ledger's records and trail in its partition", () => {
    assert.equal(recordsHref(SOURCE.ledgers[0]), "/delivery/records?flow=w&partition=dev");
    assert.equal(trailHref("d", "test"), "/delivery/activity?flow=d&partition=test");
  });
});

describe("deleting what a missing document left behind", () => {
  const NOW = Date.parse("2026-11-14T12:00:00Z");

  it("tells one document from another by its kind and flow, and names the file a renamed flow shares", () => {
    assert.equal(documentKey(SOURCE), "delivery:petrel");
    assert.equal(documentKey(DIMENSIONS), "dimension:welldb-dimensions");
    assert.equal(SHARES.file.label, "same file");
  });

  it("acts in the workbench's partition, else the one partition every ledger is kept in, else in none", () => {
    assert.equal(deletePartition([SOURCE, DIMENSIONS], "prod"), "prod");
    assert.equal(deletePartition([SOURCE], null), "dev");
    assert.equal(deletePartition([SOURCE, DIMENSIONS], null), null);
    assert.equal(deletePartition([], null), null);
  });

  it("says a document left only just now within a day, and never without a departure on record", () => {
    assert.equal(leftRecently(Date.parse("2026-11-13T16:00:00Z"), NOW), true);
    assert.equal(leftRecently(NOW - RECENT_MS, NOW), false);
    assert.equal(leftRecently(Date.parse("2026-11-04T12:00:00Z"), NOW), false);
    assert.equal(leftRecently(null, NOW), false);
    assert.equal(leftRecently(Number.NaN, NOW), false);
  });

  it("adds up what can go by kind, and counts the ledgers that stay", () => {
    const totalled = deletionTotals([
      { kind: "assertion", ledgers: [{ ledger: "a", rows: 1 }] },
      { kind: "assertion", ledgers: [{ ledger: "b", rows: 20, refusal: null }] },
      { kind: "delivery", ledgers: [{ ledger: "w", rows: 5000, refusal: "OSDU may still hold them" }, { ledger: "l", rows: 431 }] },
      { kind: "inventory", ledgers: [{ ledger: "i", rows: 2, refusal: "holds a removal" }] },
    ]);
    assert.deepEqual(totalled, { ledgers: 3, staying: 2, documents: 3, held: ["431 records", "21 assertion runs"] });
    assert.deepEqual(deletionTotals([]), { ledgers: 0, staying: 0, documents: 0, held: [] });
  });

  it("heads with the one document's ledgers, or the flows", () => {
    assert.equal(deleteTitle([DIMENSIONS]), "Delete the orphaned ledger");
    assert.equal(deleteTitle([SOURCE]), "Delete 2 orphaned ledgers");
    assert.equal(deleteTitle([SOURCE, DIMENSIONS]), "Delete the orphaned ledgers of 2 flows");
  });

  it("says what goes of the kinds in the dialog only, in the page's order, and that the trail stays", () => {
    assert.equal(deleteEffect(["assertion"]), "Assertion runs go with their results. Each ledger's audit trail stays, with this deletion in it as yours.");
    const both = deleteEffect(["inventory", "delivery", "inventory"]);
    assert.ok(both.startsWith("A delivery ledger goes only once OSDU holds none of its records"));
    assert.ok(both.includes("Inventories go with"));
    assert.equal(both.split("Inventories").length, 2);
  });

  it("puts what goes on the button: one kind's rows, else the ledgers", () => {
    assert.equal(deleteLabel({ ledgers: 21, held: ["21 assertion runs"] }), "Delete 21 assertion runs");
    assert.equal(deleteLabel({ ledgers: 1, held: ["1 assertion run"] }), "Delete 1 assertion run");
    assert.equal(deleteLabel({ ledgers: 1234, held: ["431 records", "21 assertion runs"] }), "Delete 1,234 ledgers");
    assert.equal(deleteLabel({ ledgers: 0, held: [] }), "Delete");
  });

  it("says how a ledger's deletion ended: what went, that it waits, or why it stays", () => {
    assert.equal(deletedText("assertion", { outcome: "completed", rows: 2, summary: "deleted" }), "2 assertion runs deleted, with all they held.");
    assert.equal(deletedText("assertion", { outcome: "completed", rows: 1, summary: "deleted" }), "1 assertion run deleted, with all it held.");
    assert.equal(deletedText("delivery", { outcome: "completed", rows: 1, summary: "deleted" }), "1 record deleted, a line kept of it.");
    assert.equal(deletedText("delivery", { outcome: "completed", rows: 3, summary: "deleted" }), "3 records deleted, a line kept of each.");
    assert.equal(
      deletedText("delivery", { outcome: "running", rows: 1, summary: "a run is still working on 'petrel'" }),
      "It waits (a run is still working on 'petrel'); the control plane finishes it.");
    assert.equal(deletedText("inventory", { outcome: "failed", rows: 2, summary: "holds 1 removal(s)" }), "holds 1 removal(s)");
  });
});
