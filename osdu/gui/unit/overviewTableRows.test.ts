// A look at a table's rows from the mapping overview (osdu/docs/reference/concepts/mapping-overview.md): which tables hold rows
// to look at and where they are read, the columns in the order the canvas lists the fields, and which rows are the record shown.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { DeliverySourceTable, MappingOutlineSource } from "../src/api/mappingOutline.ts";
import type { CardField, OutlineCard } from "../src/features/delivery/overview/outlineModel.ts";
import { cardPreviewLabel, previewColumns, previewLabel, recordColumnsOf, rowsOfRecord, rowsSourceOf } from "../src/features/delivery/overview/tableRows.ts";

function field(name: string, key = false): CardField {
  return { id: `dataset\u0000${name}`, name, key, displayOnly: false, match: false, read: true, type: null };
}

function card(source: Partial<MappingOutlineSource> & Pick<MappingOutlineSource, "id" | "kind">, fields: CardField[] = []): OutlineCard {
  return {
    id: source.id,
    source: { name: source.id, fields: [], ...source },
    title: source.id,
    subtitle: null,
    role: "",
    fields,
    folded: false,
    unread: [],
    repeater: null,
    readers: 0,
  };
}

const RECORD: DeliverySourceTable = { dataset: null, table: "[OsduData].[silver].[WellLog]", objectKey: "${env:osdu_data_db}|osdudata|silver|welllog", key: ["log_id", "source"], join: null, columns: [] };
const CURVES: DeliverySourceTable = { dataset: "curves", table: "[OsduData].[silver].[Curve]", objectKey: "${env:osdu_data_db}|osdudata|silver|curve", key: ["curve_log", "curve_source"], join: { curve_log: "log_id", curve_source: "source" }, columns: [] };

describe("which tables hold rows to look at", () => {
  it("reads a flow's tables in its scope, the cache's types by name, and a search's kind in OSDU", () => {
    assert.deepEqual(rowsSourceOf(card({ id: "dataset", kind: "record" })), { from: "table", dataset: null });
    assert.deepEqual(rowsSourceOf(card({ id: "dataset.curves", kind: "child" })), { from: "table", dataset: "curves" });
    assert.deepEqual(rowsSourceOf(card({ id: "cache.UnitOfMeasure", kind: "cache", name: "UnitOfMeasure" })), { from: "cache", type: "UnitOfMeasure" });
    assert.deepEqual(rowsSourceOf(card({ id: "search.Wellbore", kind: "search", name: "Wellbore", detail: "osdu:wks:master-data--Wellbore:1.0.0" })), {
      from: "search", kind: "osdu:wks:master-data--Wellbore:1.0.0",
    });
  });

  it("offers none for what holds no rows: the flow's parameters, another record, a search naming no kind", () => {
    assert.equal(rowsSourceOf(card({ id: "parameters", kind: "parameters" })), null);
    assert.equal(rowsSourceOf(card({ id: "parent", kind: "parent" })), null);
    assert.equal(rowsSourceOf(card({ id: "search.Wellbore", kind: "search", detail: null })), null);
  });

  it("offers the flow's parameters a look at their values, and another record nothing", () => {
    assert.equal(cardPreviewLabel(card({ id: "parameters", kind: "parameters" })), "Preview the values");
    assert.equal(cardPreviewLabel(card({ id: "dataset", kind: "record" })), "Preview the first 100 rows");
    assert.equal(cardPreviewLabel(card({ id: "parent", kind: "parent" })), null);
  });

  it("says what each reads in the menu's words", () => {
    assert.equal(previewLabel({ from: "table", dataset: null }), "Preview the first 100 rows");
    assert.equal(previewLabel({ from: "cache", type: "UnitOfMeasure" }), "Preview 100 cached records");
    assert.equal(previewLabel({ from: "search", kind: "osdu:wks:master-data--Wellbore:1.0.0" }), "Preview 100 records in OSDU");
  });
});

describe("the columns of a look at a table", () => {
  it("puts the fields first in the order the card lists them, whatever their case, then the rest as the rows hold them", () => {
    const table = card({ id: "dataset", kind: "record" }, [field("log_id", true), field("name"), field("depth")]);
    const columns = previewColumns(table, ["Depth", "comment", "LOG_ID", "Name", "loaded"]);
    assert.deepEqual(columns.map((column) => [column.name, column.index, column.field?.name ?? null]), [
      ["LOG_ID", 2, "log_id"],
      ["Name", 3, "name"],
      ["Depth", 0, "depth"],
      ["comment", 1, null],
      ["loaded", 4, null],
    ]);
  });

  it("keeps names that differ only in case apart when the rows hold both, as a cached type can", () => {
    const type = card({ id: "cache.UnitOfMeasure", kind: "cache", name: "UnitOfMeasure" }, [field("ID"), field("Code"), field("id")]);
    const columns = previewColumns(type, ["id", "ID", "Code", "Name"]);
    assert.deepEqual(columns.map((column) => [column.name, column.field?.name ?? null]), [["ID", "ID"], ["Code", "Code"], ["id", "id"], ["Name", null]]);
  });

  it("leaves out a field the rows do not hold, rather than show an empty column for it", () => {
    const table = card({ id: "dataset", kind: "record" }, [field("log_id", true), field("gone")]);
    assert.deepEqual(previewColumns(table, ["log_id"]).map((column) => column.name), ["log_id"]);
  });
});

describe("the rows of the record shown", () => {
  it("names a record by the key in the record table, and by what its join matches to the key in a child table", () => {
    assert.deepEqual(recordColumnsOf(RECORD, RECORD.key), ["log_id", "source"]);
    assert.deepEqual(recordColumnsOf(CURVES, RECORD.key), ["curve_log", "curve_source"]);
  });

  it("names none when the join leaves a part of the key out, or there is no key", () => {
    assert.equal(recordColumnsOf({ ...CURVES, join: { curve_log: "log_id" } }, RECORD.key), null);
    assert.equal(recordColumnsOf(RECORD, []), null);
  });

  it("marks the rows holding the record's key, part by part, and none when a column naming it is not in the rows", () => {
    const names = ["curve_source", "mnemonic", "curve_log"];
    const rows = [["las", "GR", "L-1"], ["las", "DT", "L-1"], ["las", "GR", "L-2"], ["dlis", "GR", "L-1"]];
    assert.deepEqual([...rowsOfRecord(names, rows, ["curve_log", "curve_source"], ["L-1", "las"])], [0, 1]);
    assert.equal(rowsOfRecord(names, rows, ["curve_log", "nowhere"], ["L-1", "las"]).size, 0);
    assert.equal(rowsOfRecord(names, rows, null, ["L-1", "las"]).size, 0);
    assert.equal(rowsOfRecord(names, rows, ["curve_log", "curve_source"], null).size, 0);
  });
});
