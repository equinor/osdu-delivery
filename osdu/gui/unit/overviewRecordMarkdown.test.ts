// The rendered record as the overview shows it (osdu/docs/reference/concepts/mapping-overview.md, Looking at the rendered
// record): its kind and id under its name, a section per part of the record, nested properties by path, and lists as tables.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { MAX_LIST_ROWS, recordMarkdown, recordSections } from "../src/features/delivery/overview/recordMarkdown.ts";

const RECORD = {
  id: "dev:work-product-component--WellLog:9f2c",
  kind: "osdu:wks:work-product-component--WellLog:1.4.0",
  version: 3,
  acl: { owners: ["data.default.owners@dev.example.com"], viewers: ["data.default.viewers@dev.example.com", "data.logs.viewers@dev.example.com"] },
  legal: { legaltags: ["dev-public-usa"], otherRelevantDataCountries: ["US"] },
  data: {
    Name: "COMPOSITE",
    SamplingStart: 2127.504,
    IsRegular: true,
    Comment: null,
    VerticalMeasurement: { VerticalMeasurement: 42.8, VerticalMeasurementPathID: "dev:reference-data--VerticalMeasurementPath:MD:" },
    Curves: [
      { CurveID: "MD", CurveUnit: "dev:reference-data--UnitOfMeasure:M:", NumberOfColumns: 1 },
      { CurveID: "GR", CurveUnit: "dev:reference-data--UnitOfMeasure:gAPI:", Description: "Gamma | ray\nlog" },
    ],
  },
};

describe("a rendered record as markdown", () => {
  it("names the record, then says what it holds at its top, its kind and id first", () => {
    const lines = recordMarkdown(RECORD, "Wellbore A-1 / COMPOSITE / LOG-0001/1").split("\n");
    assert.deepEqual(lines.slice(0, 7), [
      "# Wellbore A-1 / COMPOSITE / LOG-0001/1",
      "",
      "| Property | Value |",
      "| --- | --- |",
      "| kind | osdu:wks:work-product-component--WellLog:1.4.0 |",
      "| id | dev:work-product-component--WellLog:9f2c |",
      "| version | 3 |",
    ]);
  });

  it("outlines the headings it writes, with how many properties or items each holds", () => {
    assert.deepEqual(recordSections(RECORD), [
      { heading: "acl", level: 2, count: 2 },
      { heading: "legal", level: 2, count: 2 },
      { heading: "data", level: 2, count: 7 },
      { heading: "Curves (2)", level: 3, count: 2 },
    ]);
    const text = recordMarkdown(RECORD, "WellLog");
    assert.ok(recordSections(RECORD).every((section) => text.includes(`${"#".repeat(section.level)} ${section.heading}\n`)));
  });

  it("gives each part of the record a section in the record's order, a list of values on one line", () => {
    const text = recordMarkdown(RECORD, "WellLog");
    assert.deepEqual([...text.matchAll(/^## (.+)$/gm)].map((match) => match[1]), ["acl", "legal", "data"]);
    assert.ok(text.includes("| viewers | data.default.viewers@dev.example.com, data.logs.viewers@dev.example.com |"));
  });

  it("says a nested property by its path, a missing value as null, and escapes what would end a cell", () => {
    const text = recordMarkdown(RECORD, "WellLog");
    assert.ok(text.includes("| VerticalMeasurement.VerticalMeasurement | 42.8 |"));
    assert.ok(text.includes("| Comment | _null_ |"));
    assert.ok(text.includes("| IsRegular | true |"));
    assert.ok(text.includes("Gamma \\| ray log"));
  });

  it("sets a list of objects apart as a table, a column for every property an item holds", () => {
    const text = recordMarkdown(RECORD, "WellLog");
    assert.ok(text.includes("### Curves (2)"));
    assert.ok(text.includes("| # | CurveID | CurveUnit | NumberOfColumns | Description |"));
    assert.ok(text.includes("| 1 | MD | dev:reference-data--UnitOfMeasure:M: | 1 |  |"));
    // The list leaves the part's own table: no line of data names it.
    assert.ok(!text.includes("| Curves |"));
  });

  it("lists at most the first items of a long list and says how many more there are", () => {
    const many = { kind: "k", data: { Items: Array.from({ length: MAX_LIST_ROWS + 5 }, (_, i) => ({ N: i })) } };
    const text = recordMarkdown(many, "Many");
    assert.ok(text.includes(`| ${MAX_LIST_ROWS} | ${MAX_LIST_ROWS - 1} |`));
    assert.ok(!text.includes(`| ${MAX_LIST_ROWS + 1} |`));
    assert.ok(text.includes("_And 5 more._"));
  });

  it("escapes a heading's words Markdown would read as emphasis", () => {
    assert.ok(recordMarkdown({ kind: "k" }, "log_*_1").startsWith("# log\\_\\*\\_1\n"));
  });
});
