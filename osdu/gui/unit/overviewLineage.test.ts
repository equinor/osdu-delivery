// Where a mapping overview's parts stand in lineage (osdu/docs/reference/concepts/mapping-overview.md, Opening a part in
// lineage): a table is the node the flow's read of it lands on, a column opens its table's, and a property each node its value
// comes from; what lineage holds no node of opens nothing.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { DeliverySourceTable, MappingOutline, MappingOutlineFlow, MappingOutlineNode } from "../src/api/mappingOutline.ts";
import { cardsOf } from "../src/features/delivery/overview/outlineModel.ts";
import { lineageOfPart, nodeOfCard, sourcesOfNode, type LineageScope } from "../src/features/delivery/overview/outlineLineage.ts";

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
