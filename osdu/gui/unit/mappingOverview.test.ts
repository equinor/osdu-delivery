// A mapping's overview (osdu/docs/reference/concepts/mapping-overview.md): which rows stand open, how a value's marks fall
// around the tables it passes through, why the lines run side by side, and what a reader's pointing lights.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { DeliverySourceTable, MappingOutline, MappingOutlineFlow, MappingOutlineNode } from "../src/api/mappingOutline.ts";
import {
  cardsOf, countsOf, defaultOpen, dotId, everyFolding, fieldId, lightOfCard, lightOfField, lightOfRows, marksFor, nodeAtLine, opensTo, pathsOfHow, pathsOfSearch, propertiesOf, rowShowing, rowsOf,
  tableName, valueAt, wayOf,
} from "../src/features/delivery/overview/outlineModel.ts";
import { fitText, fitValue, layoutOf, MEASURE } from "../src/features/delivery/overview/outlineLayout.ts";

function flow(origin: string, reads: [string, string][], steps: { kind: string; lookups?: { source: string; field: string; matches?: { field: string; value: string }[] }[] }[] = []): MappingOutlineFlow {
  return {
    origin,
    summary: reads.map(([source, field]) => `${source}.${field}`).join(", "),
    literal: origin === "literal" ? "\"MD\"" : null,
    reads: reads.map(([source, field]) => ({ source, field, role: "value" })),
    steps: steps.map((step) => ({ kind: step.kind, label: step.kind, detail: "", text: step.kind, lookups: (step.lookups ?? []).map((lookup) => ({ matches: [], ...lookup })) })),
    unverified: false,
  };
}

function node(path: string, shape: MappingOutlineNode["shape"], how: MappingOutlineNode["how"], extra: Partial<MappingOutlineNode> = {}): MappingOutlineNode {
  return { path, name: path.split(".").pop()!, shape, how, required: true, assertions: [], children: [], ...extra };
}

/** A well log: an id, the access list, a copied name, a value split then looked up, and repeated curves. */
const OUTLINE: MappingOutline = {
  reference: "WellLog@1.4.0",
  kind: "osdu:wks:work-product-component--WellLog:1.4.0",
  enrichment: false,
  dataset: { system: "welldb", key: ["log_id"], identity: [], idFrom: "deliveryKey" },
  enrichments: [],
  sources: [
    { id: "dataset", kind: "record", name: "welldb", fields: [
      { name: "log_id", key: true, display: false, match: false, reads: 1 },
      { name: "elev", key: false, display: false, match: false, reads: 1 },
      { name: "label_only", key: false, display: true, match: false, reads: 0 },
      { name: "log_name", key: false, display: false, match: false, reads: 1 },
    ] },
    { id: "dataset.curves", kind: "child", name: "curves", fields: [{ name: "curve_id", key: false, display: false, match: false, reads: 1 }] },
    { id: "parameters", kind: "parameters", name: "Parameters", fields: [{ name: "aclOwner", key: false, display: false, match: false, reads: 1 }] },
    { id: "cache.Units", kind: "cache", name: "Units", fields: [] },
  ],
  record: [
    node("id", "value", "key", { flow: flow("key", [["dataset", "log_id"]], [{ kind: "key" }]) }),
    node("acl", "group", "", { children: [node("acl.owners", "value", "fixed", { flow: flow("parameter", [["parameters", "aclOwner"]]) })] }),
    node("data", "group", "", { children: [
      node("data.Name", "value", "copied", { line: 10, endLine: 10, flow: flow("column", [["dataset", "log_name"]]) }),
      node("data.Unit", "value", "lookedUp", { line: 11, endLine: 14, flow: flow("column", [["dataset", "elev"]], [{ kind: "split" }, { kind: "replace", lookups: [{ source: "cache.Units", field: "code" }] }, { kind: "ref" }]) }),
      node("data.Curves", "repeat", "", { dataset: "curves", line: 15, endLine: 20, children: [
        node("data.Curves[].CurveID", "value", "copied", { line: 17, endLine: 17, flow: flow("column", [["dataset.curves", "curve_id"]]) }),
        node("data.Curves[].Family", "choice", "lookedUp", { children: [
          node("data.Curves[].Family|1", "value", "copied", { flow: flow("column", [["dataset.curves", "curve_id"]]) }),
          node("data.Curves[].Family|2", "value", "fixed", { flow: flow("literal", []) }),
        ] }),
      ] }),
    ] }),
  ],
};

const TABLES: DeliverySourceTable[] = [
  { dataset: null, table: "OsduData.silver.WellLog", key: ["log_id"], join: null, columns: ["log_id", "elev", "log_name", "unused"].map((name) => ({ name, dataType: "nvarchar(50)", nullable: true })) },
  { dataset: "curves", table: "OsduData.silver.WellLogCurve", key: ["log_id"], join: { log_id: "log_id" }, columns: [] },
];

describe("what opens", () => {
  it("starts on the record's data, one level down, each thing it nests one line", () => {
    const rows = rowsOf(OUTLINE, defaultOpen(OUTLINE));
    assert.deepEqual(rows.map((row) => row.path), ["id", "acl", "data", "data.Name", "data.Unit", "data.Curves"]);
    const curves = rows.find((row) => row.path === "data.Curves")!;
    assert.equal(curves.open, false);
    assert.equal(curves.folded, 2);
  });

  it("gives a closed row everything under it: what enters it, the tables, and the marks, counted", () => {
    const rows = rowsOf(OUTLINE, new Set());
    const data = rows.find((row) => row.path === "data")!;
    assert.deepEqual(data.reads.map((read) => read.field).sort(), ["curve_id", "elev", "log_name"]);
    assert.deepEqual(data.lookups.map((lookup) => lookup.source), ["cache.Units"]);
    assert.deepEqual(data.after.map((pill) => pill.label), ["split", "ref"]);
  });

  it("opens every level of what holds others", () => {
    assert.deepEqual([...everyFolding(OUTLINE)].sort(), ["acl", "data", "data.Curves", "data.Curves[].Family"]);
  });
});

describe("a value's marks", () => {
  const row = (path: string) => rowsOf(OUTLINE, everyFolding(OUTLINE)).find((candidate) => candidate.path === path)!;

  it("stand where a render does them: what comes before the table it passes through, then what comes after", () => {
    const unit = row("data.Unit");
    assert.deepEqual(unit.before.map((pill) => pill.label), ["split"]);
    assert.deepEqual(unit.after.map((pill) => pill.label), ["ref"]);
  });

  it("leave a table a step does the whole of to its dot", () => {
    assert.deepEqual(row("data.Unit").lookups.map((lookup) => lookup.source), ["cache.Units"]);
    assert.equal([...row("data.Unit").before, ...row("data.Unit").after].some((pill) => pill.label === "replace"), false);
  });

  it("say a closed $coalesce is the first of its alternatives", () => {
    const family = rowsOf(OUTLINE, new Set(["data", "data.Curves"])).find((candidate) => candidate.path === "data.Curves[].Family")!;
    assert.deepEqual(family.after.map((pill) => pill.label), ["first of 2"]);
    assert.equal(family.folded, 1);
  });

  it("put a condition on the line, marked as one", () => {
    const outline = { ...OUTLINE, record: [node("data.When", "value", "copied", { when: "flag = \"Y\"", flow: flow("column", [["dataset", "flag"]]) })] };
    const [when] = rowsOf(outline, new Set());
    assert.equal(when.after[0].label, "when");
    assert.equal(when.after[0].gate, true);
  });
});

describe("the tables", () => {
  const cards = (open: ReadonlySet<string>) => cardsOf(OUTLINE, open, TABLES);

  it("name a table as a flow does, and say what a row of it is", () => {
    const [record, curves] = cards(new Set());
    assert.deepEqual([record.title, record.subtitle, record.role], ["silver.WellLog", "OsduData", "One row, one record"]);
    assert.deepEqual([curves.title, curves.role], ["silver.WellLogCurve", "One row, one item of Curves"]);
  });

  it("read a table's name however a flow quotes it", () => {
    assert.deepEqual(tableName("[OsduData].[silver].[WellLog]"), { title: "silver.WellLog", subtitle: "OsduData" });
    assert.deepEqual(tableName("OsduData.silver.WellLog"), { title: "silver.WellLog", subtitle: "OsduData" });
    assert.deepEqual(tableName("[Osdu.Data].[silver].[Well Log]"), { title: "silver.Well Log", subtitle: "Osdu.Data" });
    assert.deepEqual(tableName("silver.WellLog"), { title: "silver.WellLog", subtitle: null });
  });

  it("fall back on what the document calls them when no flow tells", () => {
    assert.equal(cardsOf(OUTLINE, new Set(), null)[0].title, "The record table");
  });

  it("keep the key first and the rest in the order the record reads them, so the lines run side by side", () => {
    const names = cards(new Set())[0].fields.map((field) => field.name);
    assert.deepEqual(names, ["log_id", "log_name", "elev", "label_only"]);
  });

  it("hold that order whatever is open", () => {
    const names = (open: ReadonlySet<string>) => cards(open)[0].fields.map((field) => field.name);
    assert.deepEqual(names(new Set()), names(everyFolding(OUTLINE)));
  });

  it("mark what only names a record to a person, and count what the catalog holds that nothing reads", () => {
    const [record] = cards(new Set());
    assert.equal(record.fields.find((field) => field.name === "label_only")!.displayOnly, true);
    assert.deepEqual(record.unread.map((column) => column.name), ["unused"]);
  });

  it("say how many properties they reach, a $coalesce's alternatives counted as the one property", () => {
    const [record, curves, parameters, units] = cards(new Set());
    assert.equal(record.readers, 3);
    assert.equal(curves.readers, 2);
    assert.equal(parameters.readers, 1);
    assert.equal(units.readers, 1);
  });

  it("fold a child table with its repeater", () => {
    assert.equal(cards(new Set())[1].folded, true);
    assert.equal(cards(new Set(["data", "data.Curves"]))[1].folded, false);
  });

  it("give a table of pairs, which the document names no field of, what the cache knows of it", () => {
    const named = cardsOf(OUTLINE, new Set(), TABLES, [{ name: "Units", fields: ["partition_code"], key: "source_unit" }]);
    assert.deepEqual(named[3].fields.map((field) => [field.name, field.match]), [["source_unit", true], ["partition_code", false]]);
    assert.deepEqual(cards(new Set())[3].fields.map((field) => field.name), ["key", "value"]);
  });
});

describe("the counts", () => {
  it("count a property once, however many alternatives it has, and leave out the id", () => {
    assert.deepEqual(countsOf(OUTLINE), { properties: 5, copied: 2, changed: 0, lookedUp: 2, fixed: 1, linked: 0 });
    assert.equal(propertiesOf(OUTLINE.record).some((property) => property.path.includes("|")), false);
  });
});

describe("what lights", () => {
  const rows = rowsOf(OUTLINE, everyFolding(OUTLINE));

  it("a property lights what enters it, the tables on its line, and the rows it opens under", () => {
    const light = lightOfRows(rows, new Set(["data.Unit"]));
    assert.equal(light.rows.has("data"), true);
    assert.equal(light.fields.has(fieldId("dataset", "elev")), true);
    assert.equal(light.cards.has("cache.Units"), true);
    assert.equal(light.dots.has(dotId("data.Unit", "cache.Units")), true);
    assert.equal(light.rows.has("data.Name"), false);
  });

  it("a column lights where it goes, with the tables it is looked up in on the way", () => {
    const light = lightOfField(rows, "dataset", "elev");
    assert.equal(light.rows.has("data.Unit"), true);
    assert.equal(light.dots.has(dotId("data.Unit", "cache.Units")), true);
    assert.equal(light.cards.has("cache.Units"), true);
    assert.equal(light.rows.has("data.Name"), false);
  });

  it("a table on the shelf lights the whole way: the columns looked up in it, the dot, and the property", () => {
    const light = lightOfCard(rows, "cache.Units");
    assert.equal(light.rows.has("data.Unit"), true);
    assert.equal(light.dots.has(dotId("data.Unit", "cache.Units")), true);
    assert.equal(light.fields.has(fieldId("dataset", "elev")), true);
    assert.equal(light.cards.has("dataset"), true);
    assert.equal(light.rows.has("data.Name"), false);
  });

  it("a field of a table on the shelf lights the columns looked up for it", () => {
    const light = lightOfField(rows, "cache.Units", "code");
    assert.equal(light.rows.has("data.Unit"), true);
    assert.equal(light.fields.has(fieldId("dataset", "elev")), true);
  });

  it("a table on the left lights the tables its values are looked up in", () => {
    const light = lightOfCard(rows, "dataset");
    assert.equal(light.rows.has("data.Name"), true);
    assert.equal(light.cards.has("cache.Units"), true);
    assert.equal(light.dots.has(dotId("data.Unit", "cache.Units")), true);
  });

  it("a way of filling lights its properties and opens what holds them", () => {
    const { paths, opens } = pathsOfHow(OUTLINE, "lookedUp");
    assert.deepEqual([...paths].sort(), ["data.Curves[].Family", "data.Unit"]);
    assert.deepEqual([...opens].sort(), ["data", "data.Curves"]);
  });

  it("a search finds a property by its name or by what it reads", () => {
    assert.deepEqual([...pathsOfSearch(OUTLINE, "curveid").paths], ["data.Curves[].CurveID"]);
    assert.deepEqual([...pathsOfSearch(OUTLINE, "log_name").paths], ["data.Name"]);
  });
});

describe("what is wrong", () => {
  it("shows at the row that holds it, so it is seen with everything closed", () => {
    const rows = rowsOf(OUTLINE, new Set(["data"]));
    const marks = marksFor(rows, [{ path: "data.Curves[].CurveID", tone: "warning", text: "A rule" }, { path: "data.Curves[].Family", tone: "error", text: "Another" }]);
    assert.deepEqual([...marks.keys()], ["data.Curves"]);
    assert.equal(marks.get("data.Curves")!.tone, "error");
    assert.equal(marks.get("data.Curves")!.text, "A rule\nAnother");
  });

  it("takes the row's own place once that row is shown", () => {
    const rows = rowsOf(OUTLINE, everyFolding(OUTLINE));
    assert.deepEqual([...marksFor(rows, [{ path: "data.Curves[].CurveID", tone: "error", text: "A rule" }]).keys()], ["data.Curves[].CurveID"]);
  });
});

describe("what is folded", () => {
  it("is stood for by the row folding it", () => {
    const rows = rowsOf(OUTLINE, new Set(["data"]));
    assert.equal(rowShowing(rows, "data.Curves[].Family|2")?.path, "data.Curves");
    assert.equal(rowShowing(rows, "data.Name")?.path, "data.Name");
    assert.equal(rowShowing(rows, "nowhere"), null);
  });

  it("opens along the way to what is picked", () => {
    assert.deepEqual([...opensTo(OUTLINE, "data.Curves[].Family|2")], ["data", "data.Curves", "data.Curves[].Family"]);
    assert.deepEqual([...opensTo(OUTLINE, "id")], []);
  });
});

describe("the document and a record", () => {
  it("find the narrowest property a line writes", () => {
    assert.equal(nodeAtLine(OUTLINE, 17)?.path, "data.Curves[].CurveID");
    assert.equal(nodeAtLine(OUTLINE, 16)?.path, "data.Curves");
    assert.equal(nodeAtLine(OUTLINE, 3), null);
  });

  it("read a repeated value from the first item, and an alternative's from the property", () => {
    const record = { data: { Name: "A", Curves: [{ CurveID: "GR" }, { CurveID: "MD" }] } };
    assert.equal(valueAt(record, "data.Name"), "A");
    assert.equal(valueAt(record, "data.Curves[].CurveID"), "GR");
    assert.equal(valueAt(record, "data.Curves[].CurveID|2"), "GR");
    assert.equal(valueAt(record, "data.Missing.Deep"), undefined);
    assert.equal(valueAt(null, "data.Name"), undefined);
  });
});

describe("a value's way", () => {
  /** A wellbore found by its UWI, else by an alias, its id fetched and made a reference. */
  const found: MappingOutlineFlow = {
    origin: "find",
    summary: "lookup.wellbore.id",
    reads: [
      { source: "dataset", field: "wellbore_uwi", role: "match" },
      { source: "dataset", field: "status", role: "condition" },
    ],
    steps: [
      { kind: "find", label: "cache", detail: "Each line is tried in turn.", text: "lookup.wellbore.id", lookups: [{ source: "cache.Wellbore", field: "id", matches: [
        { field: "FacilityName", value: "dataset.wellbore_uwi" },
        { field: "NameAliases.AliasName", value: "dataset.wellbore_uwi" },
        { field: "Kind", value: "'wellbore'" },
      ] }] },
      { kind: "ref", label: "ref", detail: "", text: "ref", lookups: [] },
    ],
    unverified: false,
  };

  it("goes from what enters, through what a lookup compares and fetches, to the property", () => {
    const stages = wayOf(found);
    assert.deepEqual(stages.map((stage) => stage.kind), ["input", "compared", "fetched", "changed", "written"]);
    const input = stages[0];
    assert.ok(input.kind === "input");
    assert.deepEqual(input.reads.map((read) => read.field), ["wellbore_uwi"], "a condition decides whether the value is written, and is no input");
  });

  it("pairs each line's field with the column it compares, or the text the document writes", () => {
    const compared = wayOf(found)[1];
    assert.ok(compared.kind === "compared");
    assert.equal(compared.lookup.source, "cache.Wellbore");
    assert.deepEqual(compared.lines.map((line) => [line.field, line.operand.text, line.operand.read?.source ?? null]), [
      ["FacilityName", "wellbore_uwi", "dataset"],
      ["NameAliases.AliasName", "wellbore_uwi", "dataset"],
      ["Kind", "'wellbore'", null],
    ]);
    assert.equal(compared.changed, false, "nothing changed the column first, so its value in the row is what is compared");
  });

  it("says a value a step changed is compared as that step leaves it, and finds a child table's column", () => {
    const stages = wayOf(flow("column", [["dataset.curves", "unit"]], [
      { kind: "split" },
      { kind: "trim" },
      { kind: "replace", lookups: [{ source: "cache.UnitAlias", field: "osdu_unit", matches: [{ field: "source_unit", value: "the value" }] }] },
    ]));
    assert.deepEqual(stages.map((stage) => stage.kind), ["input", "changed", "compared", "fetched", "written"]);
    const changed = stages[1];
    assert.ok(changed.kind === "changed");
    assert.deepEqual(changed.steps.map((step) => step.kind), ["split", "trim"], "steps in a row are one stage");
    const compared = stages[2];
    assert.ok(compared.kind === "compared");
    assert.equal(compared.changed, true);
    assert.deepEqual(compared.lines.map((line) => [line.operand.text, line.operand.read]), [["the value", null]]);

    const child = wayOf(flow("find", [["dataset.curves", "mnemonic"]], [{ kind: "find", lookups: [{ source: "cache.Curves", field: "id", matches: [{ field: "Code", value: "dataset.curves.mnemonic" }] }] }]))[1];
    assert.ok(child.kind === "compared");
    assert.deepEqual(child.lines[0].operand.read, { source: "dataset.curves", field: "mnemonic", role: "value" });
  });

  it("keeps a step that reads a table without comparing anything, an id's template, a change", () => {
    const stages = wayOf(flow("column", [["dataset", "log_id"]], [{ kind: "id", lookups: [{ source: "cache.Units", field: "code" }] }]));
    assert.deepEqual(stages.map((stage) => stage.kind), ["input", "changed", "written"]);
  });

  it("starts a literal at what it writes, and a value with nothing to read at its lookup", () => {
    assert.deepEqual(wayOf(flow("literal", [])).map((stage) => stage.kind), ["input", "written"]);
    const fixed = wayOf(flow("find", [], [{ kind: "find", lookups: [{ source: "cache.Units", field: "id", matches: [{ field: "Code", value: "'m'" }] }] }]));
    assert.deepEqual(fixed.map((stage) => stage.kind), ["compared", "fetched", "written"]);
  });
});

describe("the layout", () => {
  const layout = (open: ReadonlySet<string>) => layoutOf(rowsOf(OUTLINE, open), cardsOf(OUTLINE, open, TABLES), rowsOf(OUTLINE, everyFolding(OUTLINE)));

  it("runs every row's line level, one pitch apart", () => {
    const { rows } = layout(everyFolding(OUTLINE));
    rows.slice(1).forEach((row, index) => assert.equal(row.y - rows[index].y, MEASURE.row));
  });

  it("moves things down and never sideways when a row opens", () => {
    const closed = layout(new Set());
    const opened = layout(everyFolding(OUTLINE));
    assert.equal(closed.record.x, opened.record.x);
    assert.equal(closed.width, opened.width);
    assert.ok(opened.height > closed.height);
  });

  it("gives each table on the shelf a column, and a dot where a line crosses one it reads", () => {
    const placed = layout(everyFolding(OUTLINE));
    const units = placed.cards.find((card) => card.card.id === "cache.Units")!;
    assert.equal(units.shelf, true);
    const dot = placed.dots.find((candidate) => candidate.id === dotId("data.Unit", "cache.Units"))!;
    assert.equal(dot.x, units.column);
    assert.equal(dot.y, placed.rows.find((row) => row.row.path === "data.Unit")!.y);
  });

  it("orders the shelf with the parameters first, then by the first row reading each", () => {
    const shelf = layout(new Set()).cards.filter((card) => card.shelf).map((card) => card.card.id);
    assert.deepEqual(shelf, ["parameters", "cache.Units"]);
  });

  it("draws a parameter as a dot on its row, with no line from the left", () => {
    const placed = layout(everyFolding(OUTLINE));
    assert.equal(placed.dots.some((dot) => dot.id === dotId("acl.owners", "parameters")), true);
    assert.equal(placed.wires.some((wire) => wire.row === "acl.owners"), false);
  });

  it("reaches a closed repeater with one heavy line, whatever the table holds", () => {
    const wires = layout(new Set(["data"])).wires.filter((wire) => wire.source === "dataset.curves");
    assert.equal(wires.length, 1);
    assert.equal(wires[0].heavy, true);
  });

  it("starts a child table level with what it feeds, or under the table above it where that is further down", () => {
    const placed = layout(new Set(["data"]));
    const record = placed.cards.find((card) => card.card.id === "dataset")!;
    const curves = placed.cards.find((card) => card.card.id === "dataset.curves")!;
    const repeater = placed.rows.find((row) => row.row.path === "data.Curves")!;
    assert.equal(curves.y, Math.max(record.y + record.height + MEASURE.cardGap, repeater.y - MEASURE.head / 2));
  });

  it("gives a row every mark it has room for, before counting the rest", () => {
    const unit = layout(everyFolding(OUTLINE)).rows.find((row) => row.row.path === "data.Unit")!;
    assert.deepEqual(unit.pills.map((pill) => pill.pill.label).sort(), ["ref", "split"]);
  });
});

describe("cutting text", () => {
  it("leaves what fits alone", () => {
    assert.equal(fitText("Name", 100, 12), "Name");
  });

  it("ends what does not in an ellipsis", () => {
    assert.equal(fitText("VerticalMeasurement", 50, 10), "Vertica…");
  });

  it("keeps the end of a reference, which is the part that tells two apart", () => {
    assert.equal(fitValue("dev:reference-data--UnitOfMeasure:M:", 60, 10), "…easure:M:");
  });
});
