// How a check and a set's flows read on the Assertions pages (osdu/docs/reference/flow/assertion-sets.md): each fact of a
// check once (what it expected, what qualifies that, what it found, and why), the start a set's flow names share, and the
// staged rows a check read, which a query of them names (osdu/docs/reference/flow/staged-accounting.md).

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { checkReading, scopeText, sharedPrefix, stagedRowsOf } from "../src/features/delivery/assertions/assertionReading.ts";

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

describe("stagedRowsOf", () => {
  it("names the rows a staged check read: the check, its flow and scope, the rows it lists by their keys and how many failed", () => {
    const rows = stagedRowsOf({
      index: 2,
      label: "every staged log is in OSDU",
      failing: 345,
      examples: [{ key: ["NORTH", "7"] }, { key: ["NORTH", "8"] }, { key: null }, {}],
      staged: { flow: "welldb-welllog-03-delivery", interface: "welllogs", values: { logSource: "COMPOSITE" } },
    });

    assert.deepEqual(rows, {
      index: 2,
      label: "every staged log is in OSDU",
      flow: "welldb-welllog-03-delivery",
      interface: "welllogs",
      values: { logSource: "COMPOSITE" },
      listed: 2,
      failing: 345,
    });
  });

  it("is null for a check that reads OSDU alone, and for one whose result names no position", () => {
    assert.equal(stagedRowsOf({ index: 0, label: "count", staged: null }), null);
    assert.equal(stagedRowsOf({ index: 0, label: "count" }), null);
    assert.equal(stagedRowsOf({ label: "staged", staged: { flow: "f", values: {} } }), null);
  });

  it("lists no row when the check names none by its key, and takes no interface for a flow without them", () => {
    const rows = stagedRowsOf({ index: 0, label: "staged", examples: [{ key: [] }], staged: { flow: "f", values: {} } });
    assert.equal(rows?.listed, 0);
    assert.equal(rows?.interface, null);
    assert.equal(rows?.failing, null);
  });
});

describe("scopeText", () => {
  it("names each value the scope was read under, in order, and nothing for a scope with none", () => {
    assert.equal(scopeText({ logSource: "COMPOSITE", project: "NORTH" }), "logSource = COMPOSITE, project = NORTH");
    assert.equal(scopeText({}), "");
  });
});
