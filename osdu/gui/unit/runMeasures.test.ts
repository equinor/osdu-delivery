// What a run of each of the module's flow kinds moved, as the lineage graph's data view shows it
// (sqlflow/docs/reference/guides/lineage-graph.md, What moved): read from the counts a result reports, one run's or a
// day's summed.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  assertionMeasure, cacheMeasure, deliveryMeasure, dimensionMeasure, inventoryMeasure,
} from "../src/features/delivery/runMeasures.ts";

describe("a delivery flow's measure", () => {
  it("is the records it delivered, of those it planned, with what failed as its problem", () => {
    const measure = deliveryMeasure({ planned: 103, delivered: 98, failed: 5, held: 3, skippedUnchanged: 20, unchangedAtPush: 5 });
    assert.equal(measure.moved, 98);
    assert.equal(measure.label, "records delivered");
    assert.equal(measure.taken, 103);
    assert.equal(measure.takenLabel, "planned");
    assert.equal(measure.problems, 5);
    assert.deepEqual(measure.details?.find((detail) => detail.label === "unchanged"), { label: "unchanged", value: 25 });
    assert.deepEqual(measure.details?.find((detail) => detail.label === "held"), { label: "held", value: 3 });
  });

  it("says nothing of what it planned when it delivered all of it, and counts one record as one", () => {
    const all = deliveryMeasure({ planned: 1, delivered: 1 });
    assert.equal(all.taken, undefined);
    assert.equal(all.label, "record delivered");
  });

  it("delivered nothing on a day it only verified or planned", () => {
    const verified = deliveryMeasure({ checked: 30, matched: 30, drifted: 0 });
    assert.equal(verified.moved, 0);
    assert.equal(verified.problems, 0);
  });
});

describe("the other kinds' measures", () => {
  it("a dimension flow built the rows it loaded, and a dimension or a view that failed is its problem", () => {
    const measure = dimensionMeasure({ built: 5, failed: 1, skipped: 0, viewsFailed: 1, rowsLoaded: 139_790 });
    assert.equal(measure.moved, 139_790);
    assert.equal(measure.label, "rows built");
    assert.equal(measure.problems, 2);
    assert.deepEqual(measure.details?.[0], { label: "dimensions built", value: 5 });
  });

  it("a cache flow cached items", () => {
    const measure = cacheMeasure({ items: 575, affectedRecords: 10 });
    assert.equal(measure.moved, 575);
    assert.equal(measure.label, "items cached");
    assert.deepEqual(measure.details, [{ label: "records affected", value: 10 }]);
    assert.equal(cacheMeasure({ items: 1 }).label, "item cached");
  });

  it("an assertion flow passed checks, of those it ran, and the failed and errored are its problem", () => {
    const measure = assertionMeasure({ assertionRunId: 76, tests: 39, passed: 32, failed: 5, warned: 1, errored: 1, skipped: 0 });
    assert.equal(measure.moved, 32);
    assert.equal(measure.label, "checks passed");
    assert.equal(measure.taken, 39);
    assert.equal(measure.takenLabel, "run");
    assert.equal(measure.problems, 6);
    // An identifier among the counts is never read as an amount.
    assert.ok(!JSON.stringify(measure).includes("76"));
    assert.equal(assertionMeasure({ tests: 4, passed: 4 }).taken, undefined);
  });

  it("an inventory flow completed inventories", () => {
    const measure = inventoryMeasure({ completed: 2, failed: 1 });
    assert.equal(measure.moved, 2);
    assert.equal(measure.label, "inventories completed");
    assert.equal(measure.problems, 1);
  });

  it("every measure of no counts is of nothing", () => {
    for (const measure of [deliveryMeasure, dimensionMeasure, cacheMeasure, assertionMeasure, inventoryMeasure]) {
      assert.equal(measure({}).moved, 0);
    }
  });

  it("a check and an inventory move no data on, so what moved through a drawing leaves them out", () => {
    assert.equal(assertionMeasure({}).movesData, false);
    assert.equal(inventoryMeasure({}).movesData, false);
    for (const measure of [deliveryMeasure, dimensionMeasure, cacheMeasure]) {
      assert.notEqual(measure({}).movesData, false);
    }
  });
});
