// How a check and a set's flows read on the Assertions pages (osdu/docs/reference/flow/assertion-sets.md): each fact of a
// check once (what it expected, what qualifies that, what it found, and why), and the start a set's flow names share.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { checkReading, sharedPrefix } from "../src/features/delivery/assertions/assertionReading.ts";

describe("checkReading", () => {
  it("reads a count that warns as its comparison, its qualifiers, the number found and by how much it missed", () => {
    const reading = checkReading({
      type: "aggregate",
      outcome: "failed",
      expected: 'equals 104305 (warning within 10%), leaving out "MD" (stated as of 2026-10-27)',
      actual: 'count(data.Curves.CurveID) is 103908 (11358 value(s) left out: "MD")',
      message: 'count(data.Curves.CurveID) is 103908 (11358 value(s) left out: "MD"); expected equals 104305 (warning within 10%), leaving out "MD"; 397 below 104305 (0.38%), within the warning band of 10%, so it warns.',
      value: 103908,
    });

    assert.deepEqual(reading, {
      expected: "equals 104305",
      qualifiers: ["warns within 10%", 'leaving out "MD"', "stated as of 2026-10-27"],
      found: "103908",
      why: "397 below 104305 (0.38%), within the warning band of 10%, so it warns.",
    });
  });

  it("keeps what a check found in its own words when it measured no number, and says nothing more of one that passed", () => {
    const reading = checkReading({
      type: "knownValues",
      outcome: "passed",
      expected: "5 row(s) find one record each and hold their proof values",
      actual: "5 hold",
      message: "5 hold",
      value: null,
    });

    assert.equal(reading.expected, "5 row(s) find one record each and hold their proof values");
    assert.deepEqual(reading.qualifiers, []);
    assert.equal(reading.found, "5 hold");
    assert.equal(reading.why, null);
  });

  it("says why a check failed when its message adds to what it expected and found, and nothing when it only repeats them", () => {
    assert.equal(checkReading({
      type: "field", outcome: "failed", expected: "in [MD, DEPT]", actual: "2 of 5 record(s) hold", message: "3 record(s) have no depth curve", value: 0.4,
    }).why, "3 record(s) have no depth curve.");
    assert.equal(checkReading({
      type: "count", outcome: "failed", expected: "atLeast 1", actual: "0 record(s) match", message: "0 record(s) match; expected atLeast 1", value: 0,
    }).why, null);
  });
});

describe("sharedPrefix", () => {
  it("is the start every name shares, up to a hyphen", () => {
    assert.equal(sharedPrefix(["welldb-welllog-assertion-well-a", "welldb-welllog-assertion-key-numbers"]), "welldb-welllog-assertion-");
  });

  it("is empty for one flow, for names that share no start, and when it would leave a name nothing", () => {
    assert.equal(sharedPrefix(["welldb-welllog-assertion-well-a"]), "");
    assert.equal(sharedPrefix(["welldb-logs", "archive-logs"]), "");
    assert.equal(sharedPrefix(["welldb-", "welldb-logs"]), "");
  });
});
