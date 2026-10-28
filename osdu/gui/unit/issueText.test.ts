// How an issue reads on the Issues tab (osdu/docs/reference/concepts/record-lifecycle.md, Issues).

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  issueHeadline, issueHolds, issueMatches, issueParts, issuesFootText, issueStatesText, issueSteps, samplesText,
} from "../src/features/delivery/issueText.ts";

describe("an issue's text", () => {
  it("is what its records all say, with a placeholder where each says its own", () => {
    assert.deepEqual(issueParts("osdu.data.WellboreID: '<value>' matches no cached Wellbore in version <time>"), [
      { kind: "text", text: "osdu.data.WellboreID: '" },
      { kind: "placeholder", name: "value", meaning: "a value the record read" },
      { kind: "text", text: "' matches no cached Wellbore in version " },
      { kind: "placeholder", name: "time", meaning: "a moment" },
    ]);
    assert.deepEqual(issueParts("<n><osdu id>"), [
      { kind: "placeholder", name: "n", meaning: "a number" },
      { kind: "placeholder", name: "osdu id", meaning: "an OSDU record id" },
    ]);
  });

  it("keeps angle brackets that are no placeholder as words", () => {
    assert.deepEqual(issueParts("expected <unit> here"), [{ kind: "text", text: "expected <unit> here" }]);
    assert.deepEqual(issueParts(""), []);
  });
});

describe("what heads an issue", () => {
  it("is the property a render's hold names, as the record's path, and what it says of it", () => {
    assert.deepEqual(issueHeadline("osdu.data.WellboreID: no Wellbore matches 'NO 34/10-32 A'"), {
      property: "data.WellboreID", scope: null, reason: "no Wellbore matches 'NO 34/10-32 A'",
    });
  });

  it("says how far through a list a reason of its items reaches", () => {
    assert.deepEqual(issueHeadline("osdu.data.Curves[].CurveUnit, in <n> of 12 items: none of the 3 alternatives of $coalesce gives a value: 1. a; 2. b"), {
      property: "data.Curves[].CurveUnit", scope: "in <n> of 12 items", reason: "none of the 3 alternatives of $coalesce gives a value: 1. a; 2. b",
    });
  });

  it("is the whole error when it names no property first", () => {
    const refused = "HTTP 403 Forbidden from PUT https://osdu.example.com/api/storage/v2/records (correlation-id <id>)";
    assert.deepEqual(issueHeadline(refused), { property: null, scope: null, reason: refused });
    assert.deepEqual(issueHeadline("schema-required property data.Depth rendered empty"), {
      property: null, scope: null, reason: "schema-required property data.Depth rendered empty",
    });
  });
});

describe("the reasons an issue's error joins", () => {
  it("are told apart where a property heads the next one", () => {
    assert.deepEqual(
      issueHolds("osdu.data.WellboreID: no Wellbore matches '<value>'; osdu.data.Curves[].CurveUnit, in <n> of 12 items: dataset.curves.curve_unit is empty"),
      ["osdu.data.WellboreID: no Wellbore matches '<value>'", "osdu.data.Curves[].CurveUnit, in <n> of 12 items: dataset.curves.curve_unit is empty"]);
  });

  it("keep a reason's own list together", () => {
    const one = "osdu.data.Unit: none of the 2 alternatives of $coalesce gives a value: 1. dataset.unit is empty; 2. no UnitOfMeasure matches '<value>'";
    assert.deepEqual(issueHolds(one), [one]);
    assert.deepEqual(issueHolds("HTTP <n> Forbidden; retried"), ["HTTP <n> Forbidden; retried"]);
  });

  it("list what each alternative said, numbered as the error numbers them", () => {
    assert.deepEqual(
      issueSteps("none of the 3 alternatives of $coalesce gives a value, and the entry is required: 1. a is empty; 2. b is empty, so there is nothing to find in the cache; 3. c is empty"),
      { lead: "none of the 3 alternatives of $coalesce gives a value, and the entry is required", steps: ["a is empty", "b is empty, so there is nothing to find in the cache", "c is empty"] });
    assert.deepEqual(issueSteps("none of the <n> alternatives gives a value: <n>. a; <n>. b"), { lead: "none of the <n> alternatives gives a value", steps: ["a", "b"] });
  });

  it("list nothing for a reason that ends with no list of two or more", () => {
    assert.equal(issueSteps("dataset.unit is empty, and the entry is required"), null);
    assert.equal(issueSteps("required: 1. only one"), null);
  });
});

describe("finding an issue", () => {
  const issue = { issue: "41e2687b3a09debf", shared: "osdu.data.Curves[].CurveUnit: dataset.curves.curve_unit is empty" };

  it("matches its text or the start of its id, ignoring case", () => {
    assert.equal(issueMatches(issue, "curveunit"), true);
    assert.equal(issueMatches(issue, " 41E2 "), true);
    assert.equal(issueMatches(issue, "debf"), false);
    assert.equal(issueMatches(issue, "WellboreID"), false);
    assert.equal(issueMatches(issue, "  "), true);
  });
});

describe("the words around an issue", () => {
  it("say where its records stand", () => {
    assert.equal(issueStatesText(21, 0), "all held");
    assert.equal(issueStatesText(0, 3), "all failed");
    assert.equal(issueStatesText(1800, 3), "1,800 held · 3 failed");
  });

  it("say how many issues are listed, of how many, and how many a filter leaves", () => {
    assert.equal(issuesFootText(4, 4, 4, false), "4 issues");
    assert.equal(issuesFootText(1, 1, 1, false), "1 issue");
    assert.equal(issuesFootText(100, 230, 100, false), "the 100 largest of 230 issues");
    assert.equal(issuesFootText(4, 4, 1, true), "1 of 4 issues match");
  });

  it("say what the samples show of the issue's records", () => {
    assert.equal(samplesText(5, 30, "set"), "the same error in all 5, newest first");
    assert.equal(samplesText(3, 1200, "rows"), "3 records naming values of their own, newest first");
    assert.equal(samplesText(1, 1, "rows"), "its one record");
    assert.equal(samplesText(1, 5, "set"), "1 of its 5 records");
    assert.equal(samplesText(0, 5, "rows"), "none left: its records moved on");
  });
});
