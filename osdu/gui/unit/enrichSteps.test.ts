// What each enrichment of a mapping did to a record (osdu/docs/reference/flow/enrich.md), as the record's Render tab and the
// flow's Preview tab say it.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { effectValueText, enrichOutcomeText, suppliesText } from "../src/features/delivery/enrichSteps.ts";

describe("a step's outcome", () => {
  it("says how many rows the record had and how many changes the step made", () => {
    assert.equal(enrichOutcomeText({ rows: 1, effects: [{}], holds: [] }), "1 row, 1 change");
    assert.equal(enrichOutcomeText({ rows: 2, effects: [{}, {}, {}], holds: [] }), "2 rows, 3 changes");
    assert.equal(enrichOutcomeText({ effects: [{}], holds: [] }), "1 change");
  });

  it("says when a step adds nothing, a record without its rows included", () => {
    assert.equal(enrichOutcomeText({ rows: 0, effects: [], holds: [] }), "0 rows, adds nothing");
    assert.equal(enrichOutcomeText({ effects: [], holds: [] }), "adds nothing");
  });

  it("says a step holds the record, or did not render because one before it did", () => {
    assert.equal(enrichOutcomeText({ rows: 2, effects: [], holds: ["enrich.a: two rows"] }), "holds the record");
    assert.equal(enrichOutcomeText({ effects: [], holds: ["a", "b"] }), "holds the record (2 reasons)");
    assert.equal(enrichOutcomeText({ effects: [], holds: [], notRendered: true }), "not rendered: an enrichment before it holds the record or waits for the platform");
  });

  it("says a step set aside on its hold keeps what it last added, and does not hold the record", () => {
    assert.equal(enrichOutcomeText({ rows: 1, effects: [{}], holds: ["enrich.a: no file"], setAside: true }), "held, set aside: keeps what it last added");
  });
});

describe("a value a step wrote", () => {
  it("is text as it is and anything else as its JSON", () => {
    assert.equal(effectValueText("a.las"), "a.las");
    assert.equal(effectValueText(["x", "y"]), "[\"x\",\"y\"]");
    assert.equal(effectValueText({ Grade: "A" }), "{\"Grade\":\"A\"}");
    assert.equal(effectValueText(3), "3");
  });

  it("is cut to a line, and nothing when the preview left the values out", () => {
    assert.equal(effectValueText("x".repeat(200), 10), `${"x".repeat(9)}…`);
    assert.equal(effectValueText(undefined), null);
  });
});

describe("what an enrichment does beside adding paths", () => {
  it("names the records it adds, the record's bulk data and its workflow, and nothing for paths alone", () => {
    assert.equal(suppliesText(["records", "workflow"]), "records, workflow");
    assert.equal(suppliesText(["bulk"]), "bulk data");
    assert.equal(suppliesText([]), null);
    assert.equal(suppliesText(undefined), null);
  });
});
