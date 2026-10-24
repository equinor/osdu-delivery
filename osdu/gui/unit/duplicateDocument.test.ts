// A record as the duplicate key is picked in (osdu/docs/reference/flow/inventory-duplicates.md, In the GUI): which line adds
// which property to the key, which lines show as the record is folded, searched or narrowed to the key, the values a key's
// property holds, and what opens to show a property's first line.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  FOLDED_AT_FIRST, ITEMS_AT_FIRST, documentLines, keyPathOf, pickOf, pickedPath, revealOf, sectionsOf, valuesAt, type DocumentLine, type DocumentView,
} from "../src/features/delivery/inventories/duplicates/duplicateDocument.ts";
import { documentNode, type RecordNode } from "../src/features/delivery/osduRecordModel.ts";

const RECORD = {
  id: "dev:work-product-component--WellLog:a",
  kind: "osdu:wks:work-product-component--WellLog:1.4.0",
  version: 7,
  acl: { viewers: ["viewers@dev.example.com"], owners: ["owners@dev.example.com"] },
  legal: { legaltags: ["dev-welldb-legal"], otherRelevantDataCountries: ["NO"] },
  tags: { Batch: "2026-10" },
  data: {
    Name: "GR run 1",
    WellboreID: "dev:master-data--Wellbore:w1:",
    Aliases: ["A-1", "a 1"],
    Curves: [
      { Mnemonic: "DEPT", Unit: "m" },
      { Mnemonic: "GR", Unit: "gAPI" },
      { Mnemonic: "RHOB" },
      { Unit: "v/v" },
      { Mnemonic: "NPHI", Flag: true },
    ],
    Empty: [],
    "Odd-Name": "x",
    Spatial: { Point: { X: 1, Y: 2 } },
  },
};

const root = documentNode(RECORD);

/** The node at a path of the record. */
function at(path: string): RecordNode {
  const find = (node: RecordNode): RecordNode | null => (node.path === path ? node : node.children.reduce<RecordNode | null>((found, child) => found ?? find(child), null));
  const found = find(root);
  assert.ok(found !== null, `the record holds ${path}`);
  return found;
}

/** The node holding a path's node. */
function parentOf(path: string): RecordNode {
  return at(at(path).parent);
}

const VIEW: DocumentView = { folded: new Set(FOLDED_AT_FIRST), shown: new Map(), term: "", only: null };

/** The lines as kind and path, for a reader comparing them. */
function shape(lines: DocumentLine[]): string[] {
  return lines.map((line) => `${line.kind} ${line.node.path}${line.comma ? "," : ""}`);
}

describe("keyPathOf", () => {
  it("names the property a path reads, its list places left out", () => {
    assert.equal(keyPathOf("data.Curves[3].Mnemonic"), "data.Curves.Mnemonic");
    assert.equal(keyPathOf("data.Aliases[1]"), "data.Aliases");
    assert.equal(keyPathOf("data.Name"), "data.Name");
  });
});

describe("pickOf", () => {
  it("adds a value's own property, and a value of a list of values adds the list", () => {
    assert.deepEqual(pickOf(at("data.Name"), parentOf("data.Name")), { path: "data.Name" });
    assert.deepEqual(pickOf(at("data.Aliases[1]"), parentOf("data.Aliases[1]")), { path: "data.Aliases" });
    assert.deepEqual(pickOf(at("data.Aliases"), parentOf("data.Aliases")), { path: "data.Aliases" });
  });

  it("adds a value inside a list's items as its property across every item", () => {
    assert.deepEqual(pickOf(at("data.Curves[4].Mnemonic"), parentOf("data.Curves[4].Mnemonic")), { path: "data.Curves.Mnemonic" });
    assert.deepEqual(pickOf(at("data.Spatial.Point.X"), parentOf("data.Spatial.Point.X")), { path: "data.Spatial.Point.X" });
  });

  it("adds nothing for a section: an object, a list of objects, an empty list, the record itself", () => {
    assert.equal(pickOf(at("data.Spatial"), parentOf("data.Spatial")), null);
    assert.equal(pickOf(at("data.Curves"), parentOf("data.Curves")), null);
    assert.equal(pickOf(at("data.Empty"), parentOf("data.Empty")), null);
    assert.equal(pickOf(root, null), null);
  });

  it("says why a line can be no part of a key: every record's own id and version, a name a key cannot write, a root no key starts at", () => {
    const why = (node: RecordNode, parent: RecordNode) => {
      const pick = pickOf(node, parent);
      assert.ok(pick !== null && "problem" in pick, `${node.path} is refused`);
      return pick.problem;
    };
    assert.match(why(at("id"), root), /its own id, so no two share it/);
    assert.match(why(at("version"), root), /its own version/);
    assert.match(why(at("data.Odd-Name"), parentOf("data.Odd-Name")), /letters, digits and underscores/);
    const stray = documentNode({ ...RECORD, extra: 1 });
    const extra = stray.children.find((child) => child.key === "extra")!;
    assert.match(why(extra, stray), /starts at one of the record's roots/);
    assert.equal(pickedPath(pickOf(at("id"), root)), null);
  });
});

describe("documentLines", () => {
  it("opens with the sections the copies of a record rarely tell apart by folded, and a list on its first items", () => {
    const { lines, matches } = documentLines(root, VIEW);
    assert.equal(matches, 0);
    const kinds = new Map(lines.map((line) => [line.node.path, line.kind]));
    assert.equal(kinds.get("acl"), "folded");
    assert.equal(kinds.get("legal"), "folded");
    assert.equal(kinds.get("tags"), "folded");
    assert.equal(kinds.get("data.Empty"), "empty");
    assert.ok(!lines.some((line) => line.node.path === "acl.viewers"), "a folded section shows none of its lines");

    const curves = shape(lines.filter((line) => line.node.path.startsWith("data.Curves") && !line.node.path.includes("].")));
    assert.deepEqual(curves, [
      "open data.Curves",
      "open data.Curves[0]", "close data.Curves[0],",
      "open data.Curves[1]", "close data.Curves[1],",
      "open data.Curves[2]", "close data.Curves[2],",
      "more data.Curves",
      "close data.Curves,",
    ]);
    const more = lines.find((line) => line.kind === "more")!;
    assert.equal(more.hidden, 2);
    assert.equal(more.showing, ITEMS_AT_FIRST);
  });

  it("ends each section's last member without a comma, the record's own lines first and last", () => {
    const { lines } = documentLines(root, VIEW);
    assert.equal(lines[0].id, ":open");
    assert.equal(lines.at(-1)!.kind, "close");
    assert.equal(lines.at(-1)!.comma, false);
    const spatial = lines.find((line) => line.node.path === "data.Spatial" && line.kind === "close")!;
    assert.equal(spatial.comma, false, "data.Spatial is data's last member");
    const name = lines.find((line) => line.node.path === "data.Name")!;
    assert.equal(name.comma, true);
    assert.equal(name.name, "Name");
    assert.deepEqual(name.pick, { path: "data.Name" });
  });

  it("shows more of a list as it was asked to, and nothing of a section the reader folded", () => {
    const { lines } = documentLines(root, { ...VIEW, folded: new Set([...FOLDED_AT_FIRST, "data.Spatial"]), shown: new Map([["data.Curves", 5]]) });
    assert.ok(!lines.some((line) => line.kind === "more"));
    assert.ok(lines.some((line) => line.node.path === "data.Curves[4].Flag"));
    assert.equal(lines.find((line) => line.node.path === "data.Spatial")!.kind, "folded");
    assert.ok(!lines.some((line) => line.node.path === "data.Spatial.Point"));
  });

  it("searches every name and value, folded or not, showing every item that holds it and the sections around it", () => {
    const named = documentLines(root, { ...VIEW, term: "mnemonic" });
    assert.equal(named.matches, 4);
    const values = named.lines.filter((line) => line.kind === "value").map((line) => line.node.path);
    assert.deepEqual(values, ["data.Curves[0].Mnemonic", "data.Curves[1].Mnemonic", "data.Curves[2].Mnemonic", "data.Curves[4].Mnemonic"]);
    assert.ok(!named.lines.some((line) => line.kind === "more"), "a search shows every item holding it");

    const folded = documentLines(root, { ...VIEW, term: "owners@" });
    assert.equal(folded.matches, 1);
    assert.deepEqual(folded.lines.filter((line) => line.kind === "value").map((line) => line.node.path), ["acl.owners[0]"]);

    const section = documentLines(root, { ...VIEW, term: "spatial" });
    assert.deepEqual(section.lines.filter((line) => line.kind === "value").map((line) => line.node.path), ["data.Spatial.Point.X", "data.Spatial.Point.Y"]);

    assert.deepEqual(documentLines(root, { ...VIEW, term: "nowhere" }).lines, []);
  });

  it("narrowed to the key, shows the lines that compare it, a list on its first items holding it", () => {
    const { lines } = documentLines(root, { ...VIEW, only: new Set(["data.Curves.Mnemonic", "data.Aliases"]) });
    assert.deepEqual(lines.filter((line) => line.kind === "value").map((line) => line.node.path), [
      "data.Aliases[0]", "data.Aliases[1]", "data.Curves[0].Mnemonic", "data.Curves[1].Mnemonic", "data.Curves[2].Mnemonic",
    ]);
    const more = lines.find((line) => line.kind === "more")!;
    assert.equal(more.hidden, 1, "the fourth item holds no mnemonic, so one item waits");
    assert.ok(!lines.some((line) => line.node.path === "data.Name"));
  });
});

describe("valuesAt", () => {
  it("reads every value a key's property holds, through the lists on the way, in the record's order", () => {
    assert.deepEqual(valuesAt(RECORD, "data.Curves.Mnemonic"), ["DEPT", "GR", "RHOB", "NPHI"]);
    assert.deepEqual(valuesAt(RECORD, "data.Aliases"), ["A-1", "a 1"]);
    assert.deepEqual(valuesAt(RECORD, "data.Name"), ["GR run 1"]);
    assert.deepEqual(valuesAt(RECORD, "data.Spatial.Point.X"), [1]);
    assert.deepEqual(valuesAt(RECORD, "acl.owners"), ["owners@dev.example.com"]);
    assert.deepEqual(valuesAt(RECORD, "data.Curves.Flag"), [true]);
    assert.deepEqual(valuesAt(RECORD, "data.Missing"), []);
    assert.deepEqual(valuesAt(RECORD, "data.Empty"), []);
  });
});

describe("revealOf", () => {
  it("opens the sections and list items a property's first line sits in", () => {
    const mnemonic = revealOf(root, "data.Curves.Mnemonic")!;
    assert.equal(mnemonic.line, "data.Curves[0].Mnemonic");
    assert.deepEqual(mnemonic.sections, ["data", "data.Curves", "data.Curves[0]"]);
    assert.deepEqual([...mnemonic.items], [["data.Curves", 1]]);

    const flag = revealOf(root, "data.Curves.Flag")!;
    assert.equal(flag.line, "data.Curves[4].Flag");
    assert.deepEqual([...flag.items], [["data.Curves", 5]], "the fifth item has to show");

    const aliases = revealOf(root, "data.Aliases")!;
    assert.equal(aliases.line, "data.Aliases", "a list of values is shown by its own line");
    assert.deepEqual(aliases.sections, ["data"]);

    assert.equal(revealOf(root, "data.Missing"), null);
  });
});

describe("sectionsOf", () => {
  it("lists every section below the record, which Fold all folds", () => {
    const sections = sectionsOf(root);
    for (const path of ["acl", "acl.owners", "data", "data.Curves", "data.Curves[0]", "data.Spatial.Point", "data.Empty"]) {
      assert.ok(sections.has(path), path);
    }

    assert.ok(!sections.has(""));
    assert.ok(!sections.has("data.Name"));
  });
});
