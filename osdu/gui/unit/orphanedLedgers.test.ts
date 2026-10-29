// What the Orphaned ledgers page says of a missing document (osdu/docs/reference/concepts/renaming-a-pipeline.md, Missing
// documents): what it left behind in words, where each ledger goes under the flow picked, which documents a search and a
// kind keep, and the totals the page heads with.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  destinationOf, heldCount, heldText, ledgerLabel, matches, recordsHref, suggestedOf, totals, trailHref, type MissingDocument,
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
