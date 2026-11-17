// How a report's page reads an answer: what each view draws, how a value is written, which values a query runs with,
// and how the page is laid out.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { ReportColumn, ReportParameter, ReportQuery, ReportView } from "../src/api/types.ts";
import {
  assignSlots, choicesFrom, clearValues, defaultFormat, effectiveValue, formatBytes, formatCompact, formatDuration,
  formatNumber, formatTick, formatValue, humanize, isSet, layOut, missingRequired, notNarrowedBy, parameterLabel,
  placeParameters, planView, queryTitle, readValues, selectValue, seriesSpread, shownColumns, tileSpan, toCsv, valueLabel,
  valuesFor, valuesKey, writeValue,
  type BarPlan, type LinePlan, type MatrixPlan, type SharePlan,
} from "../src/features/reports/reportModel.ts";
import { isReport, reportPath } from "../src/modules/reportKind.ts";

const EN = "en-US";

function view(type: ReportView["type"], more: Partial<ReportView> = {}): ReportView {
  return {
    type, title: null, description: null, width: null, by: null, values: [], series: null, across: null, stacked: null,
    top: null, selects: null, ...more,
  };
}

function cols(spec: Record<string, string>, presentations: ReportColumn[] = []) {
  return shownColumns(Object.entries(spec).map(([name, dataType]) => ({ name, dataType })), presentations);
}

function parameter(name: string, more: Partial<ReportParameter> = {}): ReportParameter {
  return {
    name, label: name, description: null, type: "text", default: null, required: false, choices: [], choicesFrom: null,
    usedBy: [], ...more,
  };
}

function query(name: string, more: Partial<ReportQuery> = {}): ReportQuery {
  return {
    name, title: name, description: null, section: null, connection: "${env:DW}", database: null, timeoutSeconds: 60,
    maxRows: 1000, sql: "SELECT 1 AS One", parameters: [], views: [view("auto")], columns: [], problem: null, ...more,
  };
}

describe("names", () => {
  it("reads a name as words", () => {
    assert.equal(humanize("by_source"), "By source");
    assert.equal(humanize("per-day"), "Per day");
    assert.equal(humanize("LastLoaded"), "Last loaded");
    assert.equal(humanize("rows_per_UWI"), "Rows per UWI");
    assert.equal(humanize("totals"), "Totals");
  });

  it("uses a title or a label the document gave, and the name made readable when it gave none", () => {
    assert.equal(queryTitle({ name: "by_source", title: "by_source" }), "By source");
    assert.equal(queryTitle({ name: "by_source", title: "Rows by source" }), "Rows by source");
    assert.equal(parameterLabel({ name: "days_back", label: "days_back" }), "Days back");
    assert.equal(parameterLabel({ name: "days", label: "Days back" }), "Days back");
  });

  it("knows a report's kind and where its page is", () => {
    assert.equal(isReport("rpt"), true);
    assert.equal(isReport("lin"), false);
    assert.equal(isReport(null), false);
    assert.equal(reportPath("a b"), "/reports/a%20b");
  });
});

describe("how a value is written", () => {
  it("takes a format from the column's type when the document names none", () => {
    assert.equal(defaultFormat("int"), "integer");
    assert.equal(defaultFormat("bigint"), "integer");
    assert.equal(defaultFormat("decimal(18,2)"), "number");
    assert.equal(defaultFormat("float"), "number");
    assert.equal(defaultFormat("bit"), "flag");
    assert.equal(defaultFormat("date"), "date");
    assert.equal(defaultFormat("datetime2(7)"), "datetime");
    assert.equal(defaultFormat("nvarchar(50)"), "text");
    assert.equal(defaultFormat("time"), "text");
  });

  it("lays the document's presentation over the answer's columns, whatever case names them", () => {
    const [staged, loaded] = cols(
      { Staged: "int", LastLoaded: "datetime2" },
      [{ name: "STAGED", label: "Rows staged", format: null, decimals: null, unit: "rows" }]);
    assert.deepEqual(
      [staged.label, staged.format, staged.unit, staged.kind], ["Rows staged", "integer", "rows", "numeric"]);
    assert.deepEqual([loaded.label, loaded.format, loaded.kind], ["LastLoaded", "datetime", "date"]);
    assert.equal(cols({ "": "int" })[0].label, "(column 1)");
  });

  it("writes each format its own way", () => {
    const as = (format: ReportColumn["format"], decimals: number | null = null, unit: string | null = null) =>
      ({ format: format ?? "text", decimals, unit });
    assert.equal(formatValue("1284332", as("integer"), EN), "1,284,332");
    assert.equal(formatValue("1284332", as("integer", null, "rows"), EN), "1,284,332 rows");
    assert.equal(formatValue("148300.5000", as("number"), EN), "148,300.5");
    assert.equal(formatValue("148300.5", as("number", 2), EN), "148,300.50");
    assert.equal(formatValue("0.256", as("percent"), EN), "25.6%");
    assert.equal(formatValue("0.25", as("percent", 0), EN), "25%");
    assert.equal(formatValue("1536", as("bytes"), EN), "1.5 KB");
    assert.equal(formatValue("3725", as("duration"), EN), "1 h 02 min");
    assert.equal(formatValue("2026-01-31T00:00:00.0000000", as("date"), EN), "2026-01-31");
    assert.equal(formatValue("2026-01-31T06:05:09.1230000", as("datetime"), EN), "2026-01-31 06:05:09");
    assert.equal(formatValue("2026-01-31", as("datetime"), EN), "2026-01-31");
    assert.equal(formatValue("True", as("flag"), EN), "Yes");
    assert.equal(formatValue("0", as("flag"), EN), "No");
    assert.equal(formatValue("abc-123", as("code"), EN), "abc-123");
    assert.equal(formatValue("12", as("text", null, "m"), EN), "12 m");
  });

  it("answers nothing for NULL, and shows a value its format cannot read as it is", () => {
    assert.equal(formatValue(null, { format: "integer", decimals: null, unit: null }, EN), null);
    assert.equal(formatValue("n/a", { format: "integer", decimals: null, unit: null }, EN), "n/a");
    assert.equal(formatValue("not a date", { format: "date", decimals: null, unit: null }, EN), "not a date");
    assert.equal(formatValue("maybe", { format: "flag", decimals: null, unit: null }, EN), "maybe");
  });

  it("writes durations and bytes in the largest unit that fits", () => {
    assert.equal(formatDuration(0.85, EN), "850 ms");
    assert.equal(formatDuration(12.34, EN), "12.3 s");
    assert.equal(formatDuration(245, EN), "4 min 05 s");
    assert.equal(formatDuration(90061, EN), "1 d 01 h");
    assert.equal(formatBytes(512, null, EN), "512 B");
    assert.equal(formatBytes(5 * 1024 ** 3, null, EN), "5 GB");
    assert.equal(formatBytes(1536, 2, EN), "1.50 KB");
  });

  it("keeps an axis short and a mark exact", () => {
    const count = { format: "integer" as const, decimals: null, unit: null };
    assert.equal(formatCompact(12_900, EN), "12.9K");
    assert.equal(formatTick(2000, count, 8000, EN), "2,000");
    assert.equal(formatTick(20_000, count, 80_000, EN), "20K");
    assert.equal(formatTick(0.5, { format: "percent", decimals: 1, unit: null }, 1, EN), "50%");
    assert.equal(formatNumber(1284332, count, EN), "1,284,332");
    assert.equal(formatNumber(3.5, { format: "text", decimals: null, unit: "m" }, EN), "3.5 m");
  });
});

describe("planView", () => {
  const bySource = cols({ Source: "nvarchar", Staged: "int" });
  const sourceRows = [["ERP", "120"], ["CRM", "80"], ["Files", "40"]];

  it("is empty for an answer without rows, and a problem for one without columns", () => {
    assert.deepEqual(planView(view("bar"), bySource, []), { kind: "empty" });
    assert.equal(planView(view("auto"), [], [["1"]]).kind, "problem");
  });

  it("chooses figures, a line, bars or a table from the answer's shape when the document says nothing", () => {
    const totals = planView(view("auto"), cols({ Staged: "int", LastLoaded: "datetime2" }), [["5", "2026-01-31T06:00:00"]]);
    assert.equal(totals.kind, "figure");
    assert.equal(planView(view("auto"), cols({ Day: "date", Staged: "int" }), [["2026-01-01", "1"], ["2026-01-02", "2"]]).kind, "line");
    assert.equal(planView(view("auto"), bySource, sourceRows).kind, "bar");
    // Two names and a number lose a name as bars, and seven values are too many figures: a table keeps everything.
    assert.equal(planView(view("auto"), cols({ A: "nvarchar", B: "nvarchar", N: "int" }), [["a", "b", "1"], ["c", "d", "2"]]).kind, "table");
    assert.equal(planView(view("auto"), cols({ A: "int", B: "int", C: "int", D: "int", E: "int", F: "int", G: "int" }), [["1", "2", "3", "4", "5", "6", "7"]]).kind, "table");
  });

  it("draws figures from the first row, and says so when there are more", () => {
    const plan = planView(view("figure", { values: ["staged"] }), bySource, sourceRows);
    assert.equal(plan.kind, "figure");
    if (plan.kind !== "figure") return;
    assert.deepEqual(plan.items.map((item) => [item.column.name, item.value]), [["Staged", "120"]]);
    assert.match(plan.notes[0], /first of the 3 rows/);
  });

  it("draws bars by the first name and every number, in the order the query answered", () => {
    const plan = planView(view("bar"), bySource, sourceRows, EN) as BarPlan;
    assert.equal(plan.kind, "bar");
    assert.equal(plan.by.name, "Source");
    assert.deepEqual(plan.series.map((s) => s.key), ["Staged"]);
    assert.deepEqual(plan.rows.map((row) => [row.label, row.raw, row.values[0]]), [["ERP", "ERP", 120], ["CRM", "CRM", 80], ["Files", "Files", 40]]);
    assert.deepEqual([plan.min, plan.max, plan.stacked, plan.top], [0, 120, false, 12]);
  });

  it("sets several value columns side by side, and stacks them when asked", () => {
    const columns = cols({ Source: "nvarchar", Loaded: "int", Failed: "int" });
    const rows = [["ERP", "100", "20"], ["CRM", "70", "10"]];
    const side = planView(view("bar"), columns, rows) as BarPlan;
    assert.deepEqual([side.series.map((s) => s.key), side.stacked, side.max], [["Loaded", "Failed"], false, 100]);
    const stacked = planView(view("column", { stacked: true }), columns, rows) as BarPlan;
    assert.deepEqual([stacked.kind, stacked.stacked, stacked.max], ["column", true, 120]);
  });

  it("gathers long rows into a series per value of the series column, adding what is named twice", () => {
    const columns = cols({ Day: "date", State: "nvarchar", Staged: "int" });
    const rows = [
      ["2026-01-01T00:00:00", "open", "5"], ["2026-01-01T00:00:00", "closed", "3"],
      ["2026-01-02T00:00:00", "open", "4"], ["2026-01-02T00:00:00", "open", "1"],
    ];
    const plan = planView(view("column", { by: "Day", series: "State" }), columns, rows) as BarPlan;
    assert.deepEqual(plan.series.map((s) => s.key), ["open", "closed"]);
    assert.deepEqual(plan.rows.map((row) => [row.label, ...row.values]), [["2026-01-01", 5, 3], ["2026-01-02", 5, null]]);
    assert.equal(plan.stacked, true);
    assert.equal(plan.max, 8);
  });

  it("folds series past the eighth into one, drawn apart from the series colours", () => {
    const columns = cols({ Day: "nvarchar", Kind: "nvarchar", N: "int" });
    const rows = Array.from({ length: 10 }, (_, i) => ["d1", `k${i}`, "1"]);
    const plan = planView(view("bar", { series: "Kind" }), columns, rows) as BarPlan;
    assert.equal(plan.series.length, 8);
    assert.deepEqual([plan.series[7].key, plan.series[7].other], ["Other", true]);
    assert.equal(plan.rows[0].values[7], 3);
    assert.match(plan.notes[0], /3 of 10 series are added together/);
  });

  it("does not stack what goes below zero", () => {
    const plan = planView(
      view("bar", { stacked: true }), cols({ S: "nvarchar", A: "int", B: "int" }), [["x", "5", "-2"], ["y", "1", "1"]]) as BarPlan;
    assert.deepEqual([plan.stacked, plan.min, plan.max], [false, -2, 5]);
    assert.match(plan.notes[0], /side by side/);
  });

  it("puts a line's dates in order and labels them as short as the run allows", () => {
    const plan = planView(
      view("line"), cols({ Day: "date", Staged: "int" }),
      [["2026-01-03T00:00:00.0000000", "3"], ["2026-01-01T00:00:00.0000000", "1"], ["2026-01-02T00:00:00.0000000", null]]) as LinePlan;
    assert.deepEqual(plan.labels, ["2026-01-01", "2026-01-02", "2026-01-03"]);
    assert.deepEqual(plan.points, [[1], [null], [3]]);
  });

  it("divides a whole into its parts, folds the tail, and leaves out what is not above zero", () => {
    const columns = cols({ State: "nvarchar", N: "int" });
    const rows = [["a", "50"], ["b", "30"], ["c", "10"], ["d", "5"], ["e", "3"], ["f", "2"], ["g", "0"], ["h", null]];
    const plan = planView(view("share", { top: 3 }), columns, rows) as SharePlan;
    assert.equal(plan.total, 100);
    assert.deepEqual(plan.parts.map((part) => [part.label, part.value, part.share]), [
      ["a", 50, 0.5], ["b", 30, 0.3], ["c", 10, 0.1], ["Other (3)", 10, 0.1],
    ]);
    assert.equal(plan.parts[3].other, true);
    assert.match(plan.notes[0], /2 rows without a value above zero are not part of the whole/);
    // One part more than the top is shown as it is: an "Other" of one part says less than the part.
    assert.equal((planView(view("share", { top: 3 }), columns, rows.slice(0, 4)) as SharePlan).parts.length, 4);
  });

  it("lays a matrix out by rows and across, adding what falls in one cell", () => {
    const columns = cols({ Source: "nvarchar", State: "nvarchar", N: "int" });
    const rows = [["ERP", "open", "5"], ["ERP", "closed", "2"], ["CRM", "open", "1"], ["ERP", "open", "1"]];
    const plan = planView(view("matrix", { across: "State" }), columns, rows) as MatrixPlan;
    assert.deepEqual(plan.rows.map((row) => row.label), ["ERP", "CRM"]);
    assert.deepEqual(plan.columns, ["open", "closed"]);
    assert.deepEqual(plan.cells, [[6, 2], [1, null]]);
    assert.equal(plan.max, 6);
  });

  it("lists a table's columns as the document names them, or all of them", () => {
    const all = planView(view("table"), bySource, sourceRows);
    assert.equal(all.kind, "table");
    if (all.kind !== "table") return;
    assert.deepEqual(all.columns.map((c) => c.name), ["Source", "Staged"]);
    const chosen = planView(view("table", { values: ["staged"], by: "source" }), bySource, sourceRows);
    assert.equal(chosen.kind === "table" && chosen.columns.map((c) => c.name).join(), "Staged");
    assert.equal(chosen.kind === "table" && chosen.by?.name, "Source");
  });

  it("says in words what a view names that the answer does not hold", () => {
    const missing = planView(view("bar", { by: "Origin" }), bySource, sourceRows);
    assert.equal(missing.kind, "problem");
    assert.match(missing.kind === "problem" ? missing.message : "", /"Origin", which the query does not answer\. Its columns are: Source, Staged\./);
    const noNumbers = planView(view("bar"), cols({ A: "nvarchar", B: "nvarchar" }), [["a", "b"]]);
    assert.match(noNumbers.kind === "problem" ? noNumbers.message : "", /draws a column of numbers, and the query answers none/);
    const sameColumn = planView(view("matrix", { by: "Source", across: "source" }), bySource, sourceRows);
    assert.match(sameColumn.kind === "problem" ? sameColumn.message : "", /name two different columns/);
  });

  it("measures how far apart the series are", () => {
    assert.equal(seriesSpread([[10, 1000], [20, 500]], 2), 50);
    assert.equal(seriesSpread([[10, 20]], 2), 2);
    assert.equal(seriesSpread([[10, null]], 2), 1);
  });
});

describe("assignSlots", () => {
  it("keeps a series' colour when the series around it change", () => {
    const first = assignSlots(new Map(), ["open", "closed", "held"]);
    assert.deepEqual([...first], [["open", 0], ["closed", 1], ["held", 2]]);
    // "closed" is filtered away and a new series arrives: the survivors keep their slots, the newcomer takes a free one.
    const second = assignSlots(first, ["open", "held", "failed"]);
    assert.deepEqual([second.get("open"), second.get("held"), second.get("failed")], [0, 2, 3]);
    assert.equal(assignSlots(second, ["held", "open"]), second);
  });

  it("gives up the slots of series no longer drawn once every slot is taken", () => {
    const full = assignSlots(new Map(), ["a", "b", "c"], 3);
    const next = assignSlots(full, ["c", "d"], 3);
    assert.equal(next.get("c"), 2);
    assert.equal(next.get("d"), 0);
    assert.equal(next.has("a"), false);
  });
});

describe("parameters and the address", () => {
  const source = parameter("source");
  const days = parameter("days", { type: "integer", default: "30" });
  const day = parameter("day", { type: "date", required: true });
  const all = [source, days, day];

  it("reads what the address holds, a parameter written with nothing after it being a cleared one", () => {
    const given = readValues(new URLSearchParams("p.source=ERP&p.days=&tab=x&p.other=1"), all);
    assert.deepEqual(given, { source: "ERP", days: null });
    assert.equal(effectiveValue(source, given), "ERP");
    assert.equal(effectiveValue(days, given), null);
    assert.equal(effectiveValue(days, {}), "30");
    assert.equal(isSet(days, given), true);
    assert.equal(isSet(days, {}), false);
    assert.equal(isSet(days, { days: "30" }), false);
  });

  it("writes a value into the address, and takes a default back out", () => {
    const start = new URLSearchParams("tab=x");
    assert.equal(writeValue(start, source, "ERP").toString(), "tab=x&p.source=ERP");
    assert.equal(writeValue(new URLSearchParams("p.source=ERP"), source, null).toString(), "");
    assert.equal(writeValue(start, days, "7").toString(), "tab=x&p.days=7");
    assert.equal(writeValue(new URLSearchParams("p.days=7"), days, "30").toString(), "");
    // Clearing a parameter that has a default is written down, since leaving it out would mean the default.
    assert.equal(writeValue(start, days, "").toString(), "tab=x&p.days=");
    assert.equal(start.toString(), "tab=x");
    assert.equal(clearValues(new URLSearchParams("p.source=ERP&p.days=7&tab=x"), all).toString(), "tab=x");
  });

  it("runs a query with the values of the parameters it binds, and no others", () => {
    const given = { source: "ERP", days: "7" };
    assert.deepEqual(valuesFor({ parameters: ["Source"] }, given), { Source: "ERP" });
    assert.deepEqual(valuesFor({ parameters: [] }, given), {});
    assert.equal(valuesKey({ b: "2", a: null }), valuesKey({ a: null, b: "2" }));
    assert.notEqual(valuesKey({ a: null }), valuesKey({}));
  });

  it("names the required parameters a query still waits for", () => {
    assert.deepEqual(missingRequired({ parameters: ["day", "source"] }, all, {}), ["Day"]);
    assert.deepEqual(missingRequired({ parameters: ["day"] }, all, { day: "2026-01-31" }), []);
    assert.deepEqual(missingRequired({ parameters: ["source"] }, all, {}), []);
  });

  it("gives a clicked mark's value to a parameter the way the parameter's type is written", () => {
    assert.equal(selectValue("2026-01-31T00:00:00.0000000", "date"), "2026-01-31");
    assert.equal(selectValue("2026-01-31T06:05:09.1230000", "datetime"), "2026-01-31T06:05:09");
    assert.equal(selectValue("True", "flag"), "true");
    assert.equal(selectValue("ERP", "text"), "ERP");
    assert.equal(selectValue("42", "integer"), "42");
    assert.equal(selectValue(null, "text"), null);
  });

  it("reads a parameter's choices from a query's rows", () => {
    assert.deepEqual(choicesFrom([["ERP"], ["CRM"], ["ERP"], [null]]), [{ value: "ERP", label: "ERP" }, { value: "CRM", label: "CRM" }]);
    assert.deepEqual(choicesFrom([["1", "One"], ["2", null]]), [{ value: "1", label: "One" }, { value: "2", label: "2" }]);
  });
});

describe("which filter belongs to what", () => {
  // A staging report in small: the page's two filters, a tile whose bars set one of them, a tile only its own
  // parameter narrows, a tile nothing narrows, and the hidden query a filter's choices are read from.
  const source = parameter("source", { label: "Source", choicesFrom: "sources" });
  const project = parameter("project", { label: "Project" });
  const days = parameter("days", { label: "Days back", type: "integer", default: "30", required: true });
  const report = {
    parameters: [source, project, days],
    queries: [
      query("sources", { views: [] }),
      query("totals", { parameters: ["Source", "project"] }),
      query("by_source", { parameters: ["project"], views: [view("bar", { selects: "SOURCE" })] }),
      query("per_day", { parameters: ["source", "project", "days"] }),
      query("tables"),
    ],
  };
  const places = placeParameters(report);

  it("draws a parameter one shown query reads on that query's tile, and every other in the page's header", () => {
    assert.equal(places.get("source"), null);
    assert.equal(places.get("project"), null);
    assert.equal(places.get("days"), "per_day");
  });

  it("keeps in the header a parameter only a hidden query reads, and one nothing reads", () => {
    const hidden = placeParameters({
      parameters: [parameter("region"), parameter("unused")],
      queries: [query("regions_of", { parameters: ["region"], views: [] }), query("totals")],
    });
    assert.equal(hidden.get("region"), null);
    assert.equal(hidden.get("unused"), null);
  });

  it("names the page's filters in effect that a query does not read", () => {
    const given = { source: "ERP" };
    const names = (name: string) => notNarrowedBy(report.queries.find((q) => q.name === name)!, report.parameters, given, places)
      .map((p) => p.name);
    assert.deepEqual(names("totals"), []);
    assert.deepEqual(names("tables"), ["source"]);
    // The tile whose bars set the filter is not narrowed by it either, and is not named: that is how it is chosen.
    assert.deepEqual(names("by_source"), []);
  });

  it("names no filter that is at its default, cleared, or drawn on another tile", () => {
    const tables = report.queries[4];
    assert.deepEqual(notNarrowedBy(tables, report.parameters, {}, places), []);
    assert.deepEqual(notNarrowedBy(tables, report.parameters, { source: null }, places), []);
    // Days back is the per-day tile's own: moving it says nothing about the other tiles.
    assert.deepEqual(notNarrowedBy(tables, report.parameters, { days: "90" }, places), []);
    assert.deepEqual(
      notNarrowedBy(tables, report.parameters, { source: "ERP", project: "North" }, places).map((p) => p.name),
      ["source", "project"]);
  });

  it("writes a value as its choice's label, from the document or from the rows its choices are read from", () => {
    const listed = parameter("region", { choices: [{ value: "eu", label: "Europe" }, { value: "us", label: null }] });
    assert.equal(valueLabel(listed, "eu", null), "Europe");
    assert.equal(valueLabel(listed, "us", null), "us");
    assert.equal(valueLabel(listed, "apac", null), "apac");
    assert.equal(valueLabel(source, "ERP", [["ERP", "The ERP system"], ["CRM", null]]), "The ERP system");
    assert.equal(valueLabel(source, "CRM", [["ERP", "The ERP system"], ["CRM", null]]), "CRM");
    assert.equal(valueLabel(source, "ERP", null), "ERP");
  });
});

describe("the page's layout", () => {
  it("puts tiles under their headings in the document's order, and leaves hidden queries out", () => {
    const sections = layOut({
      queries: [
        query("feeder", { views: [] }),
        query("totals", { section: "Overview", views: [view("figure")] }),
        query("by_source", { views: [view("bar"), view("table")] }),
        query("per_day", { section: "Arrivals" }),
        query("more", { section: "Arrivals" }),
      ],
    });
    assert.deepEqual(sections.map((s) => [s.title, s.tiles.map((t) => t.id)]), [
      ["Overview", ["totals:0", "by_source:0", "by_source:1"]],
      ["Arrivals", ["per_day:0", "more:0"]],
    ]);
    assert.deepEqual(layOut({ queries: [query("a"), query("b")] }).map((s) => s.title), [null]);
  });

  it("gives a tile the width the document says, or the one its form reads best at", () => {
    assert.equal(tileSpan("third", "table"), 4);
    assert.equal(tileSpan("two-thirds", "bar"), 8);
    assert.equal(tileSpan(null, "bar"), 6);
    assert.equal(tileSpan(null, "share"), 6);
    assert.equal(tileSpan(null, "table"), 12);
    assert.equal(tileSpan(null, "figure"), 12);
    assert.equal(tileSpan(null, null), 12);
  });
});

describe("toCsv", () => {
  it("writes the answer as the query answered it, quoting only what needs it", () => {
    const csv = toCsv([{ name: "Source" }, { name: "Note, long" }, { name: "N" }], [
      ["ERP", 'say "hi"', "1200.50"],
      ["line\nbreak", null, "-3"],
    ]);
    assert.equal(csv, 'Source,"Note, long",N\r\nERP,"say ""hi""",1200.50\r\n"line\nbreak",,-3\r\n');
  });
});
