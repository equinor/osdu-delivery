// How one check of an assertion reads on the dashboard (osdu/docs/reference/flow/assertion-sets.md, An open assertion):
// the engine's texts in plain words, the measure a check's outcome makes, by how much it missed, what its finding adds,
// and the records that fail it with each difference apart. The outcomes are written as the engine writes them.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  asksOf, briefOf, evidenceOf, figure, plain, shareLine, type BriefFacts, type ShareMeasure, type TargetMeasure,
} from "../src/features/delivery/assertions/dashboard/checkBrief.ts";

function check(over: Partial<BriefFacts> & Pick<BriefFacts, "type" | "expected">): BriefFacts {
  return { label: "a check", severity: "error", outcome: "failed", ...over };
}

function target(facts: BriefFacts): TargetMeasure {
  const measure = briefOf(facts).measure;
  assert.equal(measure?.form, "target");
  return measure as TargetMeasure;
}

function share(facts: BriefFacts): ShareMeasure {
  const measure = briefOf(facts).measure;
  assert.equal(measure?.form, "share");
  return measure as ShareMeasure;
}

describe("plain", () => {
  it("names one or many by the number before the noun, and gives one its verb", () => {
    assert.equal(plain("5 of 5 record(s) meet it"), "5 of 5 records meet it");
    assert.equal(plain("1 of 1 record(s) do not meet it."), "1 of 1 record does not meet it.");
    assert.equal(plain("1 row(s) find no record."), "1 row finds no record.");
    assert.equal(plain("1 row(s) with 12 proof value(s): 0 hold their values"), "1 row with 12 proof values: 0 hold their values");
    assert.equal(plain("8 distinct reference(s) in 26 value(s) of 1 record(s); 0 do not resolve"), "8 distinct references in 26 values of 1 record; 0 do not resolve");
    assert.equal(plain("1 record(s) read in 2 search(es), 1 reached through others"), "1 record read in 2 searches, 1 reached through others");
    assert.equal(plain("3 of 3 record(s)' bulk data meet it"), "3 of 3 records' bulk data meet it");
    assert.equal(plain("1 of 1 record(s)' bulk data meet it"), "1 of 1 record's bulk data meet it");
    assert.equal(plain("21 of 21 value(s) are neither numbers nor dates; 1 of 1 value(s) are neither"), "21 of 21 values are neither numbers nor dates; 1 of 1 value is neither");
  });

  it("spells a document's keys out, and leaves a quoted value as it is", () => {
    assert.equal(plain("atLeast 5"), "at least 5");
    assert.equal(plain("includes STAT_COMP atLeast 5; none of UNKNOWN; group count atMost 3"), "includes STAT_COMP at least 5; none of UNKNOWN; group count at most 3");
    assert.equal(plain("MD strictlyIncreasing"), "MD strictly increasing");
    assert.equal(plain("rowCount between 172 and 458"), "row count between 172 and 458");
    assert.equal(plain('notEquals "atLeast 1 row(s)"'), 'not equal to "atLeast 1 row(s)"');
    assert.equal(plain('matches "^\\d+ record\\(s\\)$" for 2 record(s)'), 'matches "^\\d+ record\\(s\\)$" for 2 records');
  });

  it("writes a number as a figure, a very small one by its first digits", () => {
    assert.equal(figure(524142), "524,142");
    assert.equal(figure(4205.478), "4,205.478");
    assert.equal(figure(327.90000000000003), "327.9");
    assert.equal(figure(0.00001), "0.00001");
    assert.equal(figure(0), "0");
  });
});

describe("a number against what it had to be", () => {
  const wellbores = check({
    label: "812 wellbore(s)",
    type: "aggregate",
    expected: "equals 812 (warning within 10%) (stated as of 2026-10-27)",
    actual: "distinct(data.WellboreID) is 640",
    message: "distinct(data.WellboreID) is 640; expected equals 812 (warning within 10%); 172 below 812 (21.18%).",
    value: 640,
  });

  it("reads the range that holds, the band that only warns, what was measured and by how much it missed", () => {
    const brief = briefOf(wellbores);
    const measure = target(wellbores);
    assert.equal(brief.label, "812 wellbores");
    assert.equal(brief.expected, "equals 812");
    assert.deepEqual(brief.qualifiers, ["warns within 10%", "stated as of 2026-10-27"]);
    assert.deepEqual([measure.found, measure.low, measure.high, measure.exact, measure.lead, measure.expected], [640, 812, 812, 812, null, "812"]);
    assert.deepEqual([Math.round(measure.warnLow! * 10) / 10, Math.round(measure.warnHigh! * 10) / 10], [730.8, 893.2]);
    assert.equal(measure.of, "distinct values of data.WellboreID");
    assert.equal(measure.gap, "172 short of 812 (21.2%)");
    assert.equal(brief.gist, "640 found, 812 expected");
    // The message only says the miss again, which the gap already does.
    assert.equal(brief.why, null);
  });

  it("keeps of a miss what the gap does not say: that it is within the warning band", () => {
    const brief = briefOf({
      ...wellbores,
      severity: "warning",
      expected: "equals 5191 (warning within 10%) (stated as of 2026-10-27)",
      actual: "distinct(data.WellboreID) is 5190",
      message: "distinct(data.WellboreID) is 5190; expected equals 5191 (warning within 10%); 1 below 5191 (0.02%), within the warning band of 10%, so it warns.",
      value: 5190,
    });
    assert.equal(brief.result, "warned");
    assert.equal((brief.measure as TargetMeasure).gap, "1 short of 5,191 (0.019%)");
    assert.equal(brief.why, "Within the warning band of 10%, so it warns.");
  });

  it("says nothing was found where a count came to none", () => {
    const none = check({ label: "count atLeast 5", type: "count", expected: "atLeast 5", actual: "0 record(s)", message: "The test matches 0 record(s); it expects atLeast 5.", value: 0, checked: 0, failing: 1 });
    const brief = briefOf(none);
    const measure = target(none);
    assert.deepEqual([measure.low, measure.high, measure.lead, measure.expected, measure.of, measure.gap], [5, null, "at least", "5", "records", "Nothing found"]);
    assert.equal(brief.gist, "0 found, at least 5 expected");
    assert.equal(brief.why, null);
    // A count's failing is a flag, not a number of records.
    assert.equal(brief.failing, null);
  });

  it("reads a range, several terms, a tolerance and a measure that is not a count", () => {
    assert.deepEqual(
      (({ low, high, expected, gap }) => [low, high, expected, gap])(target(check({ type: "count", expected: "between 172 and 458", actual: "500 record(s)", value: 500 }))),
      [172, 458, "172 to 458", "42 over 458 (9.2%)"],
    );
    assert.deepEqual(
      (({ low, high, lead, expected }) => [low, high, lead, expected])(target(check({ type: "count", expected: "atLeast 5 and atMost 10", actual: "7 record(s)", value: 7, outcome: "passed" }))),
      [5, 10, null, "5 to 10"],
    );
    const step = target(check({ type: "aggregate", expected: "equals 0.1524 (tolerance 1E-05)", actual: "avg(data.SamplingInterval) is 0.3048", value: 0.3048 }));
    assert.deepEqual([step.exact, step.trail, step.of, step.gap], [0.1524, "give or take 0.00001", "the average of data.SamplingInterval", "0.1524 above 0.1524 (100%)"]);
  });

  it("makes no measure of a comparison it does not read, and none of a number it cannot draw from", () => {
    assert.equal(briefOf(check({ type: "count", expected: "notEquals 5", actual: "5 record(s)", value: 5 })).measure, null);
    assert.equal(briefOf(check({ type: "aggregate", expected: "equals 26", actual: "count(data.Curves.CurveID) is unknown" })).measure, null);
  });

  it("lists what a finding adds after its number, and not the values left out when none was", () => {
    const curves = briefOf(check({
      type: "aggregate",
      outcome: "passed",
      expected: "equals 21, leaving out \"MD\"",
      actual: "count(data.Curves.CurveID) is 21 (21 of 21 value(s) are neither numbers nor dates and were left out) (5 value(s) left out: \"MD\")",
      value: 21,
    }));
    assert.deepEqual(curves.foundNotes, ["21 of 21 values are neither numbers nor dates and were left out", "5 values left out: \"MD\""]);
    assert.deepEqual(curves.qualifiers, ["leaving out \"MD\""]);
    assert.deepEqual(briefOf(check({ type: "aggregate", expected: "equals 7861", actual: "count(data.Curves.CurveID) is 0 (0 value(s) left out: \"MD\")", value: 0 })).foundNotes, []);
  });
});

describe("a share that holds", () => {
  it("reads the rows of proof values by what became of each, and one row by what it does", () => {
    const rows = check({
      label: "the rows of the sheet",
      type: "knownValues",
      expected: "each of 5 row(s) finds one record and holds its 20 proof value(s)",
      actual: "5 row(s) with 20 proof value(s): 3 hold their values, 1 differ, 1 not found, 0 found more than once; 4 record(s) read in 5 search(es); differing most in DT at 2000.0976 (1)",
      message: "1 row(s) differ from the record they find; 1 row(s) find no record.",
      checked: 5,
      failing: 2,
      value: 60,
    });
    const measure = share(rows);
    assert.deepEqual(measure.parts.map((part) => [part.tone, part.count]), [["passed", 3], ["failed", 1], ["missing", 1], ["failed", 0]]);
    assert.equal(shareLine(measure), "3 of 5 rows hold, 1 differs, 1 is not found");
    const brief = briefOf(rows);
    assert.equal(brief.gist, "3 of 5 rows hold, 1 differs, 1 is not found");
    assert.deepEqual(brief.foundNotes, ["4 records read in 5 searches", "differing most in DT at 2000.0976 (1)"]);
    assert.equal(brief.failing, 2);

    const one = briefOf({
      ...rows,
      actual: "1 row(s) with 12 proof value(s): 0 hold their values, 0 differ, 1 not found, 0 found more than once; 0 record(s) read in 1 search(es)",
      message: "1 row(s) find no record.",
      checked: 1,
      failing: 1,
    });
    assert.equal(one.gist, "The row is not found");
    assert.equal(one.why, null);

    const banded = briefOf({
      ...rows,
      severity: "warning",
      actual: "1 row(s) with 2 proof value(s): 0 hold their values, 1 within the warning band, 0 differ, 0 not found, 0 found more than once; 1 record(s) read in 2 search(es)",
      message: "1 row(s) differ only within the warning band (warning).",
      checked: 1,
      failing: 1,
    });
    assert.equal(banded.result, "warned");
    assert.equal(banded.gist, "The row is within the warning band");
  });

  it("reads a check judged record by record as the records that pass, an aggregate over bulk data among them", () => {
    const bulk = check({
      label: "max(MD) between 2741 and 3107",
      type: "aggregate",
      expected: "between 2741 and 3107",
      actual: "0 of 3 record(s)' bulk data meet it",
      message: "The bulk data of 3 of 3 record(s) does not meet it.",
      checked: 3,
      failing: 3,
      value: 0,
    });
    const brief = briefOf(bulk);
    assert.equal(brief.found, "0 of 3 records' bulk data meet it");
    assert.equal(brief.gist, "0 of 3 records pass");
    assert.equal(brief.why, null);
    assert.deepEqual(brief.foundNotes, []);
    assert.deepEqual(share(bulk).parts.map((part) => [part.tone, part.count]), [["passed", 0], ["failed", 3]]);

    const warned = briefOf(check({ type: "column", severity: "warning", expected: "notEquals -999", actual: "4 of 5 record(s)' bulk data meet it", checked: 5, failing: 1, value: 80 }));
    assert.equal(warned.expected, "not equal to -999");
    assert.deepEqual((warned.measure as ShareMeasure).parts.map((part) => part.tone), ["passed", "warned"]);
    assert.equal(briefOf(check({ type: "conforms", expected: "every record meets the template of its kind", actual: "0 of 1 record(s) meet it", checked: 1, failing: 1 })).gist, "The record does not pass");
  });

  it("counts references and legal tags as what they are, and keeps what a finding says beyond the count", () => {
    const delivered = briefOf(check({
      type: "delivered",
      expected: "exactly the records welllog-03-header-delivery delivered",
      actual: "75 delivered, 74 found; 1 delivered record(s) not found, 0 found and not delivered",
      message: "1 record(s) differ between the ledger of welllog-03-header-delivery and what OSDU holds.",
      checked: 75,
      failing: 1,
      value: 1,
    }));
    assert.equal(delivered.gist, "74 of 75 records pass");
    assert.deepEqual(delivered.foundNotes, ["75 delivered, 74 found", "1 delivered record not found, 0 found and not delivered"]);
    assert.equal(delivered.why, "1 record differs between the ledger of welllog-03-header-delivery and what OSDU holds.");

    const references = check({ type: "field", outcome: "passed", expected: "resolves to a reference-data--UnitOfMeasure", actual: "8 distinct reference(s) in 26 value(s) of 5 record(s); 0 do not resolve", checked: 8, failing: 0, value: 100 });
    assert.equal(briefOf(references).gist, "8 of 8 references pass");
    assert.equal(asksOf(references), "Does every reference lead to a record that is there?");
    assert.equal(briefOf(check({ type: "legal", outcome: "passed", expected: "every legal tag the records carry is valid", actual: "1 legal tag(s) on 5 record(s); 0 invalid", checked: 1, failing: 0 })).gist, "The legal tag passes");
  });

  it("makes no share where nothing was judged, or where the two counts are of different things", () => {
    const empty = briefOf(check({ type: "conforms", expected: "every record meets the template of its kind", actual: "0 of 0 record(s) meet it", message: "No record was there to check: none matched, none met the where conditions, or none had a value.", checked: 0, failing: 0 }));
    assert.equal(empty.measure, null);
    assert.equal(empty.gist, "No record was there to check: none matched, none met the where conditions, or none had a value");
    const set = briefOf(check({ type: "recordSet", expected: "includes 1 row(s)", actual: "5 record(s) read against 1 row(s) expected", message: "1 row(s) differ from the rows expected (includes).", checked: 5, failing: 1 }));
    assert.equal(set.measure, null);
    assert.equal(set.gist, "1 row differs from the rows expected (includes)");
    assert.equal(briefOf(check({ type: "unique", outcome: "passed", expected: "no two records share data.Code", actual: "5 record(s), 5 distinct; 0 value(s) shared by more than one record", checked: 5, failing: 0 })).gist,
      "found 5 records, 5 distinct; 0 values shared by more than one record");
  });
});

describe("a check that was not judged, and one the dashboard does not know", () => {
  it("says why a check could not be made, and that one is disabled", () => {
    const errored = briefOf(check({ type: "groupBy", outcome: "errored", expected: "includes STAT_COMP atLeast 5", actual: null, message: "HTTP 400 BadRequest from POST https://osdu.example.com/api/search/v2/query: Aggregations are not supported (Bad Request)" }));
    assert.deepEqual([errored.result, errored.measure, errored.found], ["errored", null, "-"]);
    assert.equal(errored.gist, "HTTP 400 BadRequest from POST https://osdu.example.com/api/search/v2/query: Aggregations are not supported (Bad Request)");
    const skipped = briefOf(check({ type: "aggregate", outcome: "skipped", expected: "equals 2765 (warning within 10%)", message: "The test is disabled: No flow delivers these logs yet." }));
    assert.deepEqual([skipped.result, skipped.measure], ["disabled", null]);
    assert.equal(skipped.why, "The test is disabled: No flow delivers these logs yet.");
  });

  it("asks nothing of a type it does not know, and shows its texts as they are", () => {
    const unknown = briefOf(check({ type: "somethingNew", expected: "holds water", actual: "3 bucket(s) leak", checked: 3, failing: 3 }));
    assert.deepEqual([unknown.asks, unknown.measure, unknown.gist], ["", null, "found 3 buckets leak"]);
    assert.equal(briefOf(check({ type: "field", expected: "is not empty", description: "Every log names its wellbore." })).description, "Every log names its wellbore.");
  });
});

describe("the records that fail a check", () => {
  it("reads a reason that lists differences into them, one a value, with what the engine adds", () => {
    const differs = evidenceOf({
      id: "dev:work-product-component--WellLog:39d2",
      value: "GB 16/18- 1 / SYSTEM_A / STAT_COMP",
      reason: "differs in 4 proof value(s): MD step is 0.1524 in every one of its 29052 steps, expected each step equals 0.3048 (tolerance 0.0001); GR at 2000.0976 is 53.1875 (row 12128), expected it equals 60 (tolerance 0.0001); GR at 1000 has no row: the index MD holds no 1000 (nearest: 999.8964 in row 5565 and 1000.0488 in row 5566); expected it equals 25 (tolerance 0.0001); DT first is 441.0456 (row 1898), expected it equals 44",
    });
    assert.equal(differs.reason, "differs in 4 proof values");
    assert.deepEqual(differs.differences, [
      { what: "MD step", found: "0.1524 in every one of its 29052 steps", expected: "equals 0.3048 (tolerance 0.0001)", note: null },
      { what: "GR at 2000.0976", found: "53.1875 (row 12128)", expected: "equals 60 (tolerance 0.0001)", note: null },
      { what: "GR at 1000 has no row", found: "the index MD holds no 1000 (nearest: 999.8964 in row 5565 and 1000.0488 in row 5566)", expected: "equals 25 (tolerance 0.0001)", note: null },
      { what: "DT first", found: "441.0456 (row 1898)", expected: "equals 44", note: null },
    ]);

    const banded = evidenceOf({
      reason: "differs within the warning band in 2 proof value(s): GR max is 178.173095703125, expected it equals 180; 1.826904 below 180 (1.01%), within the warning band of 2%, so it warns; GR avg is 64.98302286855925, expected it equals 65.5; 0.516977 below 65.5 (0.79%), within the warning band of 2%, so it warns",
    });
    assert.equal(banded.reason, "differs within the warning band in 2 proof values");
    assert.deepEqual(banded.differences.map((d) => [d.what, d.found, d.expected, d.note]), [
      ["GR max", "178.173095703125", "equals 180", "1.826904 below 180 (1.01%), within the warning band of 2%, so it warns"],
      ["GR avg", "64.98302286855925", "equals 65.5", "0.516977 below 65.5 (0.79%), within the warning band of 2%, so it warns"],
    ]);
  });

  it("shows any other reason as it is, in plain words", () => {
    assert.deepEqual(evidenceOf({ id: "dev:x:1", value: "row 2: -999", reason: "108 of 117 row(s) do not meet it. First: is -999" }), {
      id: "dev:x:1",
      value: "row 2: -999",
      reason: "108 of 117 rows do not meet it. First: is -999",
      differences: [],
    });
    assert.deepEqual(evidenceOf({ reason: "not found: no master-data--Wellbore record holds data.FacilityName \"CA BACCALIEU F-89\"" }), {
      id: null,
      value: null,
      reason: "not found: no master-data--Wellbore record holds data.FacilityName \"CA BACCALIEU F-89\"",
      differences: [],
    });
  });
});
