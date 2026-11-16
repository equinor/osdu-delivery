// Where a mapping overview's parts stand in lineage (osdu/docs/reference/concepts/mapping-overview.md, Opening a part in
// lineage): a table is the node the flow's read of it lands on, a column opens its table's, and a property each node its value
// comes from; what lineage holds no node of opens nothing. And the column the assistant is asked to trace: a field of a
// table of the flow picked, by its name and its table's node, and the table columns some lines of the document read.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { DeliverySourceTable, MappingOutline, MappingOutlineFlow, MappingOutlineNode } from "../src/api/mappingOutline.ts";
import { cardsOf } from "../src/features/delivery/overview/outlineModel.ts";
import {
  columnOfField, columnsOfLines, lineageOfPart, nodeOfCard, sourcesOfNode, type LineageScope,
} from "../src/features/delivery/overview/outlineLineage.ts";

function flow(reads: [string, string][], lookups: string[] = []): MappingOutlineFlow {
  return {
    origin: lookups.length > 0 ? "find" : "column",
    summary: "",
    reads: reads.map(([source, field]) => ({ source, field, role: "value" })),
    steps: lookups.map((source) => ({ kind: "find", label: "cache", detail: "", text: "", lookups: [{ source, field: "id", matches: [] }] })),
    unverified: false,
  };
}

function node(path: string, extra: Partial<MappingOutlineNode> = {}): MappingOutlineNode {
  return { path, name: path.split(".").pop()!, shape: "value", how: "copied", required: true, assertions: [], children: [], ...extra };
}

const LOG_KIND = "osdu:wks:work-product-component--WellLog:1.4.0";
const WELLBORE_KIND = "osdu:wks:master-data--Wellbore:*";

/** A well log reading its table, its curves, the flow's parameters, the cache, a search, its parent, a child and an enrichment. */
const OUTLINE: MappingOutline = {
  reference: "WellLog@1.4.0",
  kind: LOG_KIND,
  enrichment: false,
  dataset: { system: "welldb", key: ["log_id"], identity: [], idFrom: "deliveryKey" },
  enrichments: [{ name: "las", mapping: "WellLogLas@1.0.0", bulk: false }, { name: "files", mapping: null, bulk: true }],
  sources: [
    { id: "dataset", kind: "record", name: "welldb", fields: [{ name: "log_id", key: true, display: false, match: false, reads: 1 }] },
    { id: "dataset.curves", kind: "child", name: "curves", fields: [{ name: "mnemonic", key: false, display: false, match: false, reads: 1 }] },
    { id: "parameters", kind: "parameters", name: "Parameters", fields: [{ name: "owner", key: false, display: false, match: false, reads: 1 }] },
    { id: "cache.Units", kind: "cache", name: "Units", fields: [] },
    { id: "search.wellbore", kind: "search", name: "wellbore", detail: WELLBORE_KIND, fields: [] },
    { id: "search.nameless", kind: "search", name: "nameless", detail: null, fields: [] },
    { id: "parent", kind: "parent", name: "Parent record", detail: "osdu:wks:master-data--Wellbore:1.3.0", fields: [] },
    { id: "child.osdu:wks:work-product-component--WellLog:1.4.0", kind: "childRecord", name: "WellLog", detail: LOG_KIND, fields: [] },
    { id: "link.las", kind: "link", name: "las", fields: [] },
    { id: "link.files", kind: "link", name: "files", fields: [] },
    { id: "base", kind: "base", name: "Base record", fields: [] },
  ],
  record: [
    node("data", { shape: "group", how: "", children: [
      node("data.Name", { flow: flow([["dataset", "log_id"]]) }),
      node("data.Owner", { how: "fixed", flow: flow([["parameters", "owner"]]) }),
      node("data.Unit", { how: "lookedUp", flow: flow([["dataset", "log_id"]], ["cache.Units", "search.wellbore"]) }),
      node("data.Fixed", { how: "fixed", flow: { ...flow([]), origin: "literal", literal: "\"MD\"" } }),
      node("data.Curves", { shape: "repeat", how: "", dataset: "curves", children: [
        node("data.Curves[].Mnemonic", { flow: flow([["dataset.curves", "mnemonic"]]) }),
      ] }),
    ] }),
  ],
};

const TABLES: DeliverySourceTable[] = [
  { dataset: null, table: "OsduData.silver.WellLog", objectKey: "${env:osdu_data_db}|osdudata|silver|welllog", key: ["log_id"], join: null, columns: [], lookups: [] },
  { dataset: "curves", table: "OsduData.silver.WellLogCurve", objectKey: "${env:osdu_data_db}|osdudata|silver|welllogcurve", key: ["log_id"], join: { log_id: "log_id" }, columns: [], lookups: [] },
];

const PICKED: LineageScope = { tables: TABLES, flow: true };
const CARDS = cardsOf(OUTLINE, new Set(), TABLES);
const card = (id: string) => CARDS.find((candidate) => candidate.id === id)!;

describe("the node a table on the canvas is", () => {
  it("is the node the flow's read of a table lands on, the record table's and a child table's", () => {
    assert.deepEqual(nodeOfCard(card("dataset"), OUTLINE, PICKED), { node: "table", objectKey: "${env:osdu_data_db}|osdudata|silver|welllog" });
    assert.deepEqual(nodeOfCard(card("dataset.curves"), OUTLINE, PICKED), { node: "table", objectKey: "${env:osdu_data_db}|osdudata|silver|welllogcurve" });
  });

  it("is no node for a table while no flow says which table it is", () => {
    assert.equal(nodeOfCard(card("dataset"), OUTLINE, { tables: null, flow: false }), null);
    assert.equal(nodeOfCard(card("dataset.curves"), OUTLINE, { tables: TABLES.slice(0, 1), flow: true }), null);
  });

  it("is the flow for its parameters, once one is picked", () => {
    assert.deepEqual(nodeOfCard(card("parameters"), OUTLINE, PICKED), { node: "flow" });
    assert.equal(nodeOfCard(card("parameters"), OUTLINE, { tables: null, flow: false }), null);
  });

  it("is the cache type for a cache table, whatever the flow", () => {
    assert.deepEqual(nodeOfCard(card("cache.Units"), OUTLINE, { tables: null, flow: false }), { node: "cache", type: "Units" });
  });

  it("is the kind for a search, a parent and a child record, a wildcard kind as it is written", () => {
    assert.deepEqual(nodeOfCard(card("search.wellbore"), OUTLINE, PICKED), { node: "type", kind: WELLBORE_KIND });
    assert.deepEqual(nodeOfCard(card("parent"), OUTLINE, PICKED), { node: "type", kind: "osdu:wks:master-data--Wellbore:1.3.0" });
    assert.deepEqual(nodeOfCard(card(`child.${LOG_KIND}`), OUTLINE, PICKED), { node: "type", kind: LOG_KIND });
    assert.equal(nodeOfCard(card("search.nameless"), OUTLINE, PICKED), null);
  });

  it("is the enrichment's mapping for the records it registers, and nothing for the record it adds to", () => {
    assert.deepEqual(nodeOfCard(card("link.las"), OUTLINE, PICKED), { node: "mapping", reference: "WellLogLas@1.0.0" });
    assert.equal(nodeOfCard(card("link.files"), OUTLINE, PICKED), null);
    assert.equal(nodeOfCard(card("base"), OUTLINE, PICKED), null);
  });
});

describe("what a part opens in lineage", () => {
  const opened = (part: Parameters<typeof lineageOfPart>[0], scope: LineageScope = PICKED) => lineageOfPart(part, OUTLINE, CARDS, scope).map((source) => source.card.id);

  it("opens a table's node from the table and from each of its columns", () => {
    assert.deepEqual(opened({ type: "card", source: "dataset.curves" }), ["dataset.curves"]);
    assert.deepEqual(opened({ type: "field", source: "dataset.curves", field: "mnemonic" }), ["dataset.curves"]);
  });

  it("opens, for a property, each node its value comes from, in the order the canvas lists the tables", () => {
    assert.deepEqual(opened({ type: "row", path: "data.Unit" }), ["dataset", "cache.Units", "search.wellbore"]);
    assert.deepEqual(opened({ type: "row", path: "data.Owner" }), ["parameters"]);
  });

  it("opens, for a section, everything its properties come from, the child table it repeats included", () => {
    assert.deepEqual(opened({ type: "row", path: "data.Curves" }), ["dataset.curves"]);
    assert.deepEqual(opened({ type: "row", path: "data" }), ["dataset", "dataset.curves", "parameters", "cache.Units", "search.wellbore"]);
  });

  it("opens nothing for a literal, a path the outline does not hold, or a table with no node", () => {
    assert.deepEqual(opened({ type: "row", path: "data.Fixed" }), []);
    assert.deepEqual(opened({ type: "row", path: "data.Missing" }), []);
    assert.deepEqual(opened({ type: "card", source: "base" }), []);
    assert.deepEqual(opened({ type: "row", path: "data.Owner" }, { tables: null, flow: false }), []);
  });

  it("reads a repeater's sources from its children and the dataset it repeats", () => {
    const curves = OUTLINE.record[0].children.find((child) => child.path === "data.Curves")!;
    assert.deepEqual([...sourcesOfNode(curves)].sort(), ["dataset.curves"]);
  });
});

const LOG_KEY = "${env:osdu_data_db}|osdudata|silver|welllog";
const CURVE_KEY = "${env:osdu_data_db}|osdudata|silver|welllogcurve";
const ALIAS_KEY = "${env:osdu_data_db}|osdudata|ref|curvealias";

describe("the column a field is, as the assistant traces it", () => {
  it("is a column of the record table, by the table's name on the canvas and its node", () => {
    assert.deepEqual(columnOfField(card("dataset"), "log_id", PICKED), { column: "log_id", object: "silver.WellLog", objectKey: LOG_KEY });
  });

  it("is a column of a child table, by that table's name and node", () => {
    assert.deepEqual(columnOfField(card("dataset.curves"), "mnemonic", PICKED), { column: "mnemonic", object: "silver.WellLogCurve", objectKey: CURVE_KEY });
  });

  it("is none for a field of anything but a table: the cache, the parameters, a search, another record", () => {
    for (const id of ["cache.Units", "parameters", "search.wellbore", "parent", "link.las", "base"]) {
      assert.equal(columnOfField(card(id), "id", PICKED), null, id);
    }
  });

  it("is none while no flow says which table it is, or the flow picked does not read the table", () => {
    assert.equal(columnOfField(card("dataset"), "log_id", { tables: null, flow: false }), null);
    assert.equal(columnOfField(card("dataset.curves"), "mnemonic", { tables: TABLES.slice(0, 1), flow: true }), null);
  });

  it("is none for a table with no node in lineage, and for a field with no name", () => {
    const unplaced: LineageScope = { tables: [{ ...TABLES[0], objectKey: "  " }], flow: true };
    assert.equal(columnOfField(card("dataset"), "log_id", unplaced), null);
    assert.equal(columnOfField(card("dataset"), " ", PICKED), null);
  });

  it("is the lookup table's column for one a lookup gives the row, under the name it has there", () => {
    const alias = { name: "CurveAlias", table: "[OsduData].[ref].[CurveAlias]", objectKey: ALIAS_KEY, join: { mnemonic: "mnemonic" }, columns: [{ name: "CurveAlias_curve_type", dataType: "nvarchar", nullable: true }] };
    const joined: LineageScope = { tables: [TABLES[0], { ...TABLES[1], lookups: [alias] }], flow: true };
    assert.deepEqual(columnOfField(card("dataset.curves"), "curvealias_curve_type", joined), { column: "curve_type", object: "ref.CurveAlias", objectKey: ALIAS_KEY });
    assert.deepEqual(columnOfField(card("dataset.curves"), "mnemonic", joined), { column: "mnemonic", object: "silver.WellLogCurve", objectKey: CURVE_KEY });

    const unplaced: LineageScope = { tables: [TABLES[0], { ...TABLES[1], lookups: [{ ...alias, objectKey: "" }] }], flow: true };
    assert.equal(columnOfField(card("dataset.curves"), "CurveAlias_curve_type", unplaced), null);
  });
});

/** The same sources under a record the document writes on lines: a rule on the rows, then `data` with its curves and an alternative. */
const WRITTEN: MappingOutline = {
  ...OUTLINE,
  dataset: {
    ...OUTLINE.dataset,
    rules: [{ label: "one-log", expected: "unique", onFail: "hold", per: "run", source: "dataset", reads: [{ source: "dataset", field: "log_id", role: "value" }], line: 4, endLine: 5 }],
  },
  record: [
    node("id", { how: "key", flow: flow([["dataset", "source_project"]]) }),
    node("data", { shape: "group", how: "", line: 10, endLine: 24, children: [
      node("data.Name", { line: 11, endLine: 11, flow: flow([["dataset", "log_name"]]) }),
      node("data.Owner", { how: "fixed", line: 12, endLine: 12, flow: flow([["parameters", "owner"]]) }),
      node("data.Unit", { how: "lookedUp", line: 13, endLine: 15, flow: flow([["dataset", "LOG_NAME"], ["dataset", "unit"]], ["cache.Units"]) }),
      node("data.Curves", { shape: "repeat", how: "", dataset: "curves", line: 16, endLine: 24, children: [
        node("data.Curves[].Mnemonic", { line: 18, endLine: 18, flow: flow([["dataset.curves", "mnemonic"]]) }),
        node("data.Curves[].Type", { shape: "choice", how: "lookedUp", line: 19, endLine: 24, children: [
          node("data.Curves[].Type|1", { line: 21, endLine: 22, flow: flow([["dataset.curves", "curve_type"]]) }),
          node("data.Curves[].Type|2", { line: 23, endLine: 24, flow: flow([["dataset.curves", "mnemonic"], ["dataset", "log_id"]], ["cache.Units"]) }),
        ] }),
      ] }),
    ] }),
  ],
};

describe("the table columns lines of the document read", () => {
  const cards = cardsOf(WRITTEN, new Set(), TABLES);
  const read = (start: number, end: number, scope: LineageScope = PICKED) => columnsOfLines(WRITTEN, cards, scope, { start, end })
    .map((column) => `${column.object}.${column.column}`);

  it("are the columns the property on those lines reads, with their table and its node", () => {
    assert.deepEqual(columnsOfLines(WRITTEN, cards, PICKED, { start: 11, end: 11 }), [{ column: "log_name", object: "silver.WellLog", objectKey: LOG_KEY }]);
  });

  it("are every column a section's lines read, each once however its case is written, in the order the lines read them", () => {
    assert.deepEqual(read(10, 24), ["silver.WellLog.log_name", "silver.WellLog.unit", "silver.WellLogCurve.mnemonic", "silver.WellLogCurve.curve_type", "silver.WellLog.log_id"]);
  });

  it("are those of the alternative the lines are in, not of the whole choice, and none for a line that reads nothing", () => {
    assert.deepEqual(read(21, 22), ["silver.WellLogCurve.curve_type"]);
    assert.deepEqual(read(22, 21), ["silver.WellLogCurve.curve_type"]);
    assert.deepEqual(read(19, 20), []);
    assert.deepEqual(read(17, 17), []);
  });

  it("leave out what is no column of a table: a parameter, a field of the cache", () => {
    assert.deepEqual(read(12, 12), []);
    assert.deepEqual(read(13, 15), ["silver.WellLog.LOG_NAME", "silver.WellLog.unit"]);
  });

  it("include what a rule on the rows stated there reads, and never what the document writes on no line", () => {
    assert.deepEqual(read(4, 4), ["silver.WellLog.log_id"]);
    assert.deepEqual(read(1, 30), ["silver.WellLog.log_id", "silver.WellLog.log_name", "silver.WellLog.unit", "silver.WellLogCurve.mnemonic", "silver.WellLogCurve.curve_type"]);
  });

  it("give a property the document writes no lines for to the section holding it", () => {
    const held: MappingOutline = { ...WRITTEN, record: [node("data", { shape: "group", how: "", line: 10, endLine: 12, children: [node("data.Name", { flow: flow([["dataset", "log_name"]]) })] })] };
    assert.deepEqual(columnsOfLines(held, cards, PICKED, { start: 12, end: 12 }).map((column) => column.column), ["log_name"]);
    assert.deepEqual(columnsOfLines(held, cards, PICKED, { start: 13, end: 13 }), []);
  });

  it("are none for the whole mapping, and none while no flow says which tables they are", () => {
    assert.deepEqual(columnsOfLines(WRITTEN, cards, PICKED, null), []);
    assert.deepEqual(read(10, 24, { tables: null, flow: false }), []);
  });
});
