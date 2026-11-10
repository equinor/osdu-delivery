// Where a mapping's YAML names a column or a table of the overview (osdu/docs/reference/concepts/mapping-overview.md, The YAML
// beside the canvas): what is lit, what is marked, and the line brought into view when it is picked.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { MappingOutline, MappingOutlineFlow, MappingOutlineNode } from "../src/api/mappingOutline.ts";
import { linesOfPart, markRuns, namedOn } from "../src/features/delivery/overview/partLines.ts";

const LINES = [
  "documentType: mapping",
  "dataset:",
  "  system: welldb",
  "  # The key is the log_id within a project.",
  "  key: [source_project, log_id]",
  "  label: \"{wellbore_uwi} / {log_id}\"",
  "  identity: [wellbore_uwi, log_id]",
  "  assert:",
  "    - name: one-log",
  "      unique: [log_id]",
  "parameters:",
  "  dataPartition:",
  "    required: true",
  "record:",
  "  tags:",
  "    Partition: \"{$param.dataPartition}\"",
  "  data:",
  "    Name: { $from: log_name }",
  "    LogId:",
  "      $from: log_id",
  "      $modifiers:",
  "        - split: { separator: \",\", part: 1 }",
  "    WellboreLogId: { $from: wellbore_log_id }",
  "    Curves:",
  "      $forEach: curves",
  "      Unit:",
  "        $coalesce:",
  "          - $from: curve_unit",
  "            $modifiers: [ref]",
  "          - $cache: UnitOfMeasure.id",
  "            findBy: { Code: CURVE_UNIT }",
];

/** The line, from 1, the text first stands on. */
function at(text: string): number {
  const index = LINES.findIndex((line) => line.includes(text));
  assert.ok(index >= 0, `no line holds ${text}`);
  return index + 1;
}

function flow(reads: [string, string, "value" | "match"][], lookups: { source: string; field: string; matches: { field: string; value: string }[] }[] = []): MappingOutlineFlow {
  return {
    origin: lookups.length > 0 ? "find" : "column",
    summary: "",
    reads: reads.map(([source, field, role]) => ({ source, field, role })),
    steps: lookups.length === 0 ? [] : [{ kind: "find", label: "find", detail: "", text: "", lookups }],
    unverified: false,
  };
}

function node(path: string, lines: [number, number] | null, extra: Partial<MappingOutlineNode> = {}): MappingOutlineNode {
  return {
    path,
    name: path.slice(path.lastIndexOf(".") + 1),
    shape: "value",
    how: "copied",
    required: true,
    line: lines?.[0] ?? null,
    endLine: lines?.[1] ?? null,
    assertions: [],
    children: [],
    ...extra,
  };
}

const OUTLINE: MappingOutline = {
  reference: "WellLog@1.0.0",
  kind: "osdu:wks:work-product-component--WellLog:1.4.0",
  enrichment: false,
  dataset: {
    system: "welldb",
    key: ["source_project", "log_id"],
    label: "{wellbore_uwi} / {log_id}",
    identity: ["wellbore_uwi", "log_id"],
    idFrom: "deliveryKey",
    rules: [{
      label: "one-log", expected: "unique", onFail: "hold", per: "run", source: "dataset",
      reads: [{ source: "dataset", field: "log_id", role: "value" }], line: at("- name: one-log"), endLine: at("unique: [log_id]"),
    }],
  },
  sources: [
    { id: "dataset", kind: "record", name: "welldb", fields: [], line: at("dataset:"), endLine: at("unique: [log_id]") },
    { id: "parameters", kind: "parameters", name: "Parameters", fields: [], line: at("parameters:"), endLine: at("required: true") },
    { id: "dataset.curves", kind: "child", name: "curves", fields: [] },
    { id: "cache.UnitOfMeasure", kind: "cache", name: "UnitOfMeasure", fields: [] },
  ],
  record: [
    node("id", null, { how: "key", flow: flow([["dataset", "source_project", "value"], ["dataset", "log_id", "value"]]) }),
    node("tags", [at("tags:"), at("Partition: \"")], {
      shape: "group",
      how: "",
      children: [node("tags.Partition", [at("Partition: \""), at("Partition: \"")], { how: "fixed", flow: flow([["parameters", "dataPartition", "value"]]) })],
    }),
    node("data", [at("data:"), LINES.length], {
      shape: "group",
      how: "",
      children: [
        node("data.Name", [at("Name:"), at("Name:")], { flow: flow([["dataset", "log_name", "value"]]) }),
        node("data.LogId", [at("LogId:"), at("- split:")], { how: "changed", flow: flow([["dataset", "log_id", "value"]]) }),
        node("data.WellboreLogId", [at("WellboreLogId:"), at("WellboreLogId:")], { flow: flow([["dataset", "wellbore_log_id", "value"]]) }),
        node("data.Curves[]", [at("Curves:"), LINES.length], {
          shape: "repeat",
          how: "",
          dataset: "curves",
          children: [node("data.Curves[].Unit", [at("Unit:"), LINES.length], {
            shape: "choice",
            how: "lookedUp",
            children: [
              node("data.Curves[].Unit|1", [at("- $from: curve_unit"), at("$modifiers: [ref]")], { how: "changed", flow: flow([["dataset.curves", "curve_unit", "value"]]) }),
              node("data.Curves[].Unit|2", [at("- $cache: UnitOfMeasure.id"), LINES.length], {
                how: "lookedUp",
                flow: flow([["dataset.curves", "curve_unit", "match"]], [{ source: "cache.UnitOfMeasure", field: "id", matches: [{ field: "Code", value: "dataset.curves.curve_unit" }] }]),
              }),
            ],
          })],
        }),
      ],
    }),
  ],
  enrichments: [],
};

/** The lines a part lights, in order. */
function litOf(part: { source: string; field: string | null }): number[] {
  return [...linesOfPart(OUTLINE, LINES, part).lit].sort((a, b) => a - b);
}

/** The lines from `first` to `last`. */
function span(first: number, last: number): number[] {
  return Array.from({ length: last - first + 1 }, (_, index) => first + index);
}

describe("where the document names a column or a table", () => {
  it("lights what reads a column, and of the block declaring its table only the lines naming it, marked where they do", () => {
    const found = linesOfPart(OUTLINE, LINES, { source: "dataset", field: "log_id" });

    // The key, the label and the identity name it; the comment above them does not count, nor does a longer name holding it.
    assert.deepEqual(
      [...found.lit].sort((a, b) => a - b),
      [at("key: [source_project"), at("label:"), at("identity:"), ...span(at("- name: one-log"), at("unique: [log_id]")), ...span(at("LogId:"), at("- split:"))],
    );
    assert.equal(found.lit.has(at("# The key is the log_id")), false);
    assert.equal(found.lit.has(at("WellboreLogId:")), false);
    assert.equal(found.lit.has(at("system: welldb")), false);
    assert.equal(found.first, at("key: [source_project"));

    const key = LINES[at("key: [source_project") - 1];
    assert.deepEqual(found.marks.get(at("key: [source_project")), [{ start: key.indexOf("log_id"), end: key.indexOf("log_id") + "log_id".length }]);
    assert.equal(found.marks.has(at("LogId:")), false, "the property's own key is LogId, not the column");
    assert.equal(found.marks.has(at("$from: log_id")), true);
  });

  it("lights each alternative that reads a column, not the whole coalesce, and marks a column a lookup writes in another case", () => {
    const found = linesOfPart(OUTLINE, LINES, { source: "dataset.curves", field: "curve_unit" });
    assert.deepEqual(litOf({ source: "dataset.curves", field: "curve_unit" }), span(at("- $from: curve_unit"), LINES.length));
    assert.equal(found.lit.has(at("$coalesce:")), false);
    assert.equal(found.first, at("- $from: curve_unit"));
    const upper = LINES[at("CURVE_UNIT") - 1];
    assert.deepEqual(found.marks.get(at("CURVE_UNIT")), [{ start: upper.indexOf("CURVE_UNIT"), end: upper.indexOf("CURVE_UNIT") + "CURVE_UNIT".length }]);
  });

  it("lights a child table's whole $forEach, marked where it names the dataset, and a cached type where its lookups are", () => {
    const curves = linesOfPart(OUTLINE, LINES, { source: "dataset.curves", field: null });
    assert.deepEqual(litOf({ source: "dataset.curves", field: null }), span(at("Curves:"), LINES.length));
    assert.equal(curves.first, at("$forEach: curves"));

    const units = linesOfPart(OUTLINE, LINES, { source: "cache.UnitOfMeasure", field: null });
    assert.deepEqual([...units.lit].sort((a, b) => a - b), span(at("- $cache: UnitOfMeasure.id"), LINES.length));
    assert.equal(units.first, at("- $cache: UnitOfMeasure.id"));
  });

  it("lights the record table's declaring block whole when the table itself is picked, and finds a parameter where it is declared", () => {
    const table = linesOfPart(OUTLINE, LINES, { source: "dataset", field: null });
    assert.equal(table.first, at("dataset:"));
    for (const line of span(at("dataset:"), at("unique: [log_id]"))) {
      assert.equal(table.lit.has(line), true, `line ${line}`);
    }

    assert.equal(table.marks.size, 0, "a table of the record is named by no word of its own");

    const partition = linesOfPart(OUTLINE, LINES, { source: "parameters", field: "dataPartition" });
    assert.deepEqual([...partition.lit].sort((a, b) => a - b), [at("  dataPartition:"), at("Partition: \"")]);
    assert.equal(partition.first, at("  dataPartition:"));
  });

  it("finds nothing for a column the document never names", () => {
    const found = linesOfPart(OUTLINE, LINES, { source: "dataset", field: "log_source" });
    assert.equal(found.lit.size, 0);
    assert.equal(found.first, null);
  });
});

describe("a name on a line", () => {
  it("is a whole name outside a comment, as written or ignoring case", () => {
    assert.deepEqual(namedOn("  key: [log_id, wellbore_log_id] # log_id", "log_id", false), [{ start: 8, end: 14 }]);
    assert.deepEqual(namedOn("    findBy: { Code: CURVE_UNIT }", "curve_unit", false), []);
    assert.deepEqual(namedOn("    findBy: { Code: CURVE_UNIT }", "curve_unit", true), [{ start: 20, end: 30 }]);
    assert.deepEqual(namedOn("{$param.dataPartition}", "dataPartition", false), [{ start: 8, end: 21 }]);
    assert.deepEqual(namedOn("anything", "", false), []);
  });
});

describe("the runs of a line with a mark", () => {
  it("are cut where the mark starts and ends, each piece keeping its token", () => {
    const runs = [{ text: "  key", token: "key" }, { text: ": ", token: "mark" }, { text: "[a, log_id]", token: "value" }];
    assert.deepEqual(markRuns(runs, [{ start: 11, end: 17 }]), [
      { text: "  key", token: "key", marked: false },
      { text: ": ", token: "mark", marked: false },
      { text: "[a, ", token: "value", marked: false },
      { text: "log_id", token: "value", marked: true },
      { text: "]", token: "value", marked: false },
    ]);
    assert.deepEqual(markRuns(runs, []), runs.map((run) => ({ ...run, marked: false })));
  });
});
