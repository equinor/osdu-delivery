// The assertion dashboard's model (osdu/docs/reference/flow/assertion-sets.md, The dashboard): where each assertion stands
// now, what is left out until asked for, the axes and squares of the map, the verdict, the groups, and what each row
// reads.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  axisValues, byMiss, checksOf, defaultAxes, fromBoard, groupOf, inScope, readingLine, readingOf, setsOf, square, standingOf, tally, verdictOf,
  type Board, type BoardLatest, type BoardTest, type ResultFacts,
} from "../src/features/delivery/assertions/dashboard/dashboardModel.ts";

let nextResult = 100;

function latest(outcome: BoardLatest["outcome"], over: Partial<BoardLatest> = {}): BoardLatest {
  return {
    resultId: nextResult++,
    assertionRunId: 7,
    outcome,
    matched: 12,
    failedAssertions: outcome === "passed" ? 0 : 1,
    error: null,
    completedUtc: "2026-11-03T10:00:00",
    ...over,
  };
}

function testOf(name: string, over: Partial<BoardTest> = {}): BoardTest {
  return {
    name,
    title: null,
    description: null,
    kind: "osdu:wks:work-product-component--WellLog:1.4.0",
    query: null,
    template: null,
    runsHere: true,
    problems: [],
    latest: null,
    changed: false,
    assertions: [{ label: "count", expected: "at least 1" }],
    dimensions: [],
    ...over,
  };
}

function dims(set: string, check: string, region?: string) {
  return [
    { name: "Set", value: set },
    { name: "Type", value: "WellLog 1.4.0" },
    { name: "Check", value: check },
    ...(region === undefined ? [] : [{ name: "Region", value: region }]),
  ];
}

function boardOf(...flows: { set: string | null; tests: BoardTest[]; problem?: string }[]): Board {
  return {
    partition: "p1",
    flows: flows.map((flow, i) => ({
      pipelineId: `flow-${i}`,
      repoId: "repo",
      name: `flow-${i}`,
      title: null,
      description: null,
      set: flow.set,
      partitions: [],
      problem: flow.problem ?? null,
      lastRun: null,
      tests: flow.tests,
    })),
  };
}

describe("standingOf", () => {
  it("counts a disabled assertion as disabled whatever it found before, with the reason its document gives", () => {
    assert.deepEqual(standingOf(testOf("a", { disabled: "No flow delivers these yet.", latest: latest("failed") })), {
      standing: "disabled",
      reason: "No flow delivers these yet.",
    });
    assert.equal(standingOf(testOf("b", { assertions: [{ label: "count", expected: "at least 1", disabled: "Waiting on the source." }] })).reason,
      "Every check is disabled: Waiting on the source.");
  });

  it("counts an assertion changed since its latest result as not yet checked, naming the report that checked the earlier version", () => {
    const { standing, reason } = standingOf(testOf("a", { latest: latest("passed", { assertionRunId: 41 }), changed: true }));
    assert.equal(standing, "notRun");
    assert.match(reason ?? "", /^Changed since report 41,/);
  });

  it("reads one that does not fit its schema and has no result as not checkable, and a skip as not yet checked", () => {
    assert.equal(standingOf(testOf("a", { problems: ["data.Nope is not in WellLog 1.4.0."] })).standing, "errored");
    assert.deepEqual(standingOf(testOf("b", { latest: latest("skipped", { assertionRunId: 9, error: "Its query names no record." }) })), {
      standing: "notRun",
      reason: "Its latest run, report 9, skipped it: Its query names no record.",
    });
  });

  it("warns on a pass whose proof values are past their day, and otherwise takes the latest outcome", () => {
    assert.equal(standingOf(testOf("a", { latest: latest("passed"), needsNewProofValues: true, validUntil: "2026-10-01" })).standing, "warned");
    assert.deepEqual(standingOf(testOf("b", { latest: latest("errored", { error: "OSDU did not answer." }) })), { standing: "errored", reason: "OSDU did not answer." });
    assert.deepEqual(standingOf(testOf("c", { latest: latest("passed") })), { standing: "passed", reason: null });
  });
});

describe("fromBoard and the scope", () => {
  const board = boardOf(
    {
      set: "logs",
      tests: [
        testOf("counts", { title: "The log counts", latest: latest("failed"), dimensions: dims("logs", "Counts", "North") }),
        testOf("names", { latest: latest("passed"), dimensions: dims("logs", "Values", "South") }),
        testOf("archive", { disabled: "Not delivered yet.", dimensions: dims("logs", "Counts", "North") }),
        testOf("elsewhere", { runsHere: false, latest: latest("passed") }),
      ],
    },
    { set: "wells", tests: [testOf("wells", { latest: latest("passed", { resultId: 5 }), changed: true, dimensions: dims("wells", "Counts") })] },
  );
  const data = fromBoard(board, "2026-11-04T08:00:00Z");

  it("keeps the assertions that check the partition, reads a title in place of the name, and stands each at a result only when it is judged", () => {
    assert.deepEqual(data.tests.map((t) => [t.title, t.standing, t.resultId !== null]), [
      ["The log counts", "failed", true],
      ["names", "passed", true],
      ["archive", "disabled", false],
      ["wells", "notRun", false],
    ]);
  });

  it("leaves disabled assertions out until they are included, for the set picked or for every set", () => {
    assert.deepEqual(inScope(data, undefined, false).map((t) => t.name), ["counts", "names", "wells"]);
    assert.deepEqual(inScope(data, "logs", true).map((t) => t.name), ["counts", "names", "archive"]);
    assert.deepEqual(setsOf(data, false).map((s) => [s.label, s.n.total, s.n.worst]), [["logs", 2, "failed"], ["wells", 1, "notRun"]]);
  });
});

describe("the map", () => {
  const board = boardOf({
    set: "logs",
    tests: [
      testOf("n1", { latest: latest("failed"), dimensions: dims("logs", "Counts", "North") }),
      testOf("n2", { latest: latest("failed"), dimensions: dims("logs", "Counts", "North") }),
      testOf("n3", { latest: latest("passed"), dimensions: dims("logs", "Values", "North") }),
      testOf("s1", { latest: latest("passed"), dimensions: dims("logs", "Counts", "South") }),
      testOf("w1", { latest: latest("warned"), dimensions: dims("logs", "Values", "West") }),
      testOf("x1", { dimensions: dims("logs", "Values") }),
    ],
  });
  const tests = fromBoard(board, "2026-11-04T08:00:00Z").tests;

  it("draws the rows along a dimension the documents name and the columns along Check", () => {
    assert.deepEqual(defaultAxes(tests), { row: "Region", col: "Check" });
  });

  it("lays a dimension's values out worst first, those without one after the others that stand alike, and gathers what is past the limit as Other", () => {
    assert.deepEqual(axisValues(tests, "Region", 12).map((a) => a.label), ["North", "West", "No region", "South"]);
    const gathered = axisValues(tests, "Region", 2);
    assert.deepEqual(gathered.map((a) => a.label), ["North", "Other (3)"]);
    assert.deepEqual(gathered[1]!.values, ["West", null, "South"]);
  });

  it("reads a square as passing of judged, with the worst that is there", () => {
    const north = tests.filter((t) => t.dimensions.some((d) => d.value === "North"));
    assert.deepEqual(square(north).main, "1/3");
    assert.equal(square(north).sub, "2 failing");
    assert.equal(square(tests.filter((t) => t.name === "x1")).main, "-");
  });

  it("says where most of what fails is", () => {
    const rows = axisValues(tests, "Region", 12);
    const cols = axisValues(tests, "Check", 7);
    const verdict = verdictOf(tests, rows, "Region", cols, "Check");
    assert.equal(verdict.standing, "failed");
    assert.equal(verdict.text, "Needs attention: 2 assertions fail, all of them in North · Counts.");
  });

  it("says where only when most of what fails is there", () => {
    const spread = fromBoard(boardOf({
      set: "logs",
      tests: [
        testOf("a", { latest: latest("failed"), dimensions: dims("logs", "Counts", "North") }),
        testOf("b", { latest: latest("failed"), dimensions: dims("logs", "Values", "South") }),
        testOf("c", { latest: latest("failed"), dimensions: dims("logs", "Counts", "West") }),
        testOf("d", { latest: latest("passed"), dimensions: dims("logs", "Values", "West") }),
      ],
    }), "2026-11-04T08:00:00Z").tests;
    assert.equal(verdictOf(spread, axisValues(spread, "Region", 12), "Region", axisValues(spread, "Check", 7), "Check").text, "Needs attention: 3 assertions fail.");
    const one = spread.filter((t) => t.name === "a" || t.name === "d");
    assert.equal(verdictOf(one, axisValues(one, "Region", 12), "Region", axisValues(one, "Check", 7), "Check").text, "Needs attention: 1 assertion fails, in North · Counts.");
  });

  it("says a set holds when everything checked passes", () => {
    const passing = tests.filter((t) => t.standing === "passed");
    assert.equal(verdictOf(passing, [], null, [], null).text, "Holds: all 2 assertions checked pass.");
    assert.equal(tally(passing).worst, "passed");
  });
});

describe("the list", () => {
  it("groups what is not yet checked with each reason once, counted", () => {
    const tests = fromBoard(boardOf({
      set: null,
      tests: [
        testOf("a"),
        testOf("b"),
        testOf("c", { latest: latest("passed", { assertionRunId: 3 }), changed: true }),
        testOf("d", { latest: latest("passed", { assertionRunId: 4 }), changed: true }),
      ],
    }), "2026-11-04T08:00:00Z").tests;
    assert.deepEqual(groupOf(tests, "notRun").reasons, [
      { reason: "No run has checked it in this partition yet.", count: 2 },
      { reason: "Changed since its last result, which checked an earlier version of it; no run has checked this version yet.", count: 2 },
    ]);
  });

  it("reads the check with the most that does not hold as an assertion's claim, what does not hold of it first, and the measure of it", () => {
    const [test] = fromBoard(boardOf({ set: null, tests: [testOf("a", { latest: latest("failed") })] }), "2026-11-04T08:00:00Z").tests;
    const result = {
      test: "a",
      assertions: [
        { label: "names", type: "field", severity: "error", outcome: "passed", expected: "is not empty" },
        { label: "a warning first", type: "field", severity: "warning", outcome: "failed", expected: "exists", actual: "4 of 5 record(s) meet it", checked: 5, failing: 1 },
        { label: "count atLeast 5", type: "count", severity: "error", outcome: "failed", expected: "atLeast 5", actual: "3 record(s)", value: 3, examples: [{ id: "p1:x:1", reason: "missing" }] },
      ],
    };
    const reading = readingOf(test!, result);
    assert.equal(reading.claim, "count at least 5");
    assert.equal(reading.says, "2 short of 5 (40%)");
    assert.equal(reading.measure?.form, "target");
    assert.equal(reading.more, 1);
    assert.equal(reading.miss, 0.4);
    assert.equal(readingLine(reading), "count at least 5: 2 short of 5 (40%); 1 more check does not hold");

    // Of two checks that fail, the row names the one with more that does not hold, whichever the document states first.
    const two = readingOf(test!, {
      test: "a",
      assertions: [
        { label: "names", type: "field", severity: "error", outcome: "failed", expected: "is not empty", actual: "4 of 5 record(s) meet it", checked: 5, failing: 1 },
        { label: "curves", type: "columns", severity: "error", outcome: "failed", expected: "exactly MD, GR", actual: "0 of 5 record(s)' bulk data meet it", checked: 5, failing: 5 },
      ],
    });
    assert.deepEqual([two.claim, two.says, two.more, two.miss], ["curves", "None of the 5 records pass", 1, 1]);
    assert.deepEqual(checksOf(result).map((c) => [c.label, c.result, c.found, c.examples.length]),
      [["names", "passed", "-", 0], ["a warning first", "warned", "4 of 5 records meet it", 0], ["count at least 5", "failed", "3", 1]]);
  });

  it("says how many checks do not hold until the result is at hand, and why an assertion has none", () => {
    const tests = fromBoard(boardOf({
      set: null,
      tests: [
        testOf("failing", { latest: latest("failed") }),
        testOf("passing", { latest: latest("passed"), assertions: [{ label: "a", expected: "exists" }, { label: "b", expected: "exists" }] }),
        testOf("off", { disabled: "Not delivered yet." }),
        testOf("unread", { latest: latest("errored", { error: "OSDU did not answer." }) }),
      ],
    }), "2026-11-04T08:00:00Z").tests;
    assert.deepEqual(tests.map((test) => readingLine(readingOf(test, undefined))), [
      "1 of 1 check does not hold",
      "All 2 checks hold",
      "Not delivered yet.",
      "OSDU did not answer.",
    ]);
    assert.equal(readingOf(tests[3]!, { test: "unread", error: "The search answered 503." }).says, "The search answered 503.");
    // A service's answer is said in plain words on the row, and whole on a hover.
    const answered = readingOf(tests[3]!, { test: "unread", error: "HTTP 503 ServiceUnavailable from POST https://osdu.example.com/api/search/v2/query (correlation-id IG-1)" });
    assert.equal(answered.says, "The search service answered 503 Service Unavailable.");
    assert.equal(readingLine(answered), "HTTP 503 ServiceUnavailable from POST https://osdu.example.com/api/search/v2/query (correlation-id IG-1)");
  });

  it("lists what needs attention by where it stands, then the most that does not hold first, and ranks nothing it has not read", () => {
    const failing = (name: string, title: string) => testOf(name, { title, latest: latest("failed") });
    const tests = fromBoard(boardOf({
      set: null,
      tests: [
        failing("near", "A near miss"),
        failing("unread", "B not read yet"),
        failing("none", "C nothing found"),
        failing("some", "D some of it"),
        testOf("warns", { title: "E warns", latest: latest("warned") }),
        testOf("down", { title: "F could not be checked", latest: latest("errored", { error: "OSDU did not answer." }) }),
      ],
    }), "2026-11-04T08:00:00Z").tests;
    const count = (found: number, severity = "error"): ResultFacts => ({
      test: "x",
      assertions: [{ label: "count", type: "count", severity, outcome: "failed", expected: "equals 100", actual: `${found} record(s)`, value: found }],
    });
    const results: Record<string, ResultFacts> = {
      [String(tests[0]!.resultId)]: count(99),
      [String(tests[2]!.resultId)]: count(0),
      [String(tests[3]!.resultId)]: count(60),
      [String(tests[4]!.resultId)]: count(1, "warning"),
    };
    assert.deepEqual(byMiss(tests, results).map((test) => test.name), ["none", "some", "near", "unread", "down", "warns"]);
    // With no result at hand they keep the order they stand in, by title.
    assert.deepEqual(byMiss(tests, {}).map((test) => test.name), ["near", "unread", "none", "some", "down", "warns"]);
  });

  it("counts a passing assertion's check of info severity that does not hold as noted, beside the ones that hold", () => {
    const [test] = fromBoard(boardOf({
      set: null,
      tests: [testOf("noted", {
        latest: latest("passed", { failedAssertions: 1 }),
        assertions: [{ label: "a", expected: "exists" }, { label: "b", expected: "exists" }, { label: "c", expected: "atLeast 5" }],
      })],
    }), "2026-11-04T08:00:00Z").tests;
    assert.equal(readingOf(test!, undefined).says, "2 of 3 checks hold, 1 noted");
  });
});
