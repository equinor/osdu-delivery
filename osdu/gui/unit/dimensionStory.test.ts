// How the dimension builder tells a draft as a story: where each step stands and which asks for a pick next, which step
// each of the builder's findings is about, the words a clean step is said in, where a record holds a key's value, so the
// record the key names can be opened from it, and how the table's preview lays out the keys it made as a build lays out
// the table.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  attributeOfTarget, chainWords, cleanWords, exampleCount, firstLocationAt, keyEntityType, kindTypeName, nextStep, previewRows, previewWords, stepOfTarget,
  stepStates, type PreviewKey, type StoryDraft,
} from "../src/features/delivery/explorer/dimension/dimensionStory.ts";

const empty: StoryDraft = { kind: "", path: null, label: [], attributes: [], clean: [] };
const keyed: StoryDraft = { ...empty, kind: "osdu:wks:work-product-component--WellLog:*", path: "data.WellboreID" };

describe("stepStates", () => {
  it("asks for the kind first, every other step waiting", () => {
    const states = stepStates(empty, false);
    assert.equal(states.from, "next");
    assert.deepEqual([states.rows, states.shown, states.with, states.cleaned, states.makes], ["waiting", "waiting", "waiting", "waiting", "waiting"]);
    assert.equal(nextStep(states), "from");
  });

  it("asks for the key once the kind is read", () => {
    const states = stepStates({ ...empty, kind: keyed.kind }, true);
    assert.equal(states.from, "done");
    assert.equal(states.rows, "next");
    assert.equal(states.shown, "waiting");
    assert.equal(nextStep(states), "rows");
  });

  it("asks what a row is shown as when the key names records", () => {
    const states = stepStates(keyed, true);
    assert.equal(states.shown, "next");
    assert.equal(states.makes, "done");
    assert.equal(nextStep(states), "shown");
  });

  it("leaves the value open when the key names no record, and asks for nothing more", () => {
    const states = stepStates({ ...keyed, path: "data.Source" }, false);
    assert.equal(states.shown, "open");
    assert.equal(nextStep(states), null);
  });

  it("marks what the draft holds as done", () => {
    const states = stepStates({
      ...keyed,
      label: ["data.FacilityName"],
      attributes: [{ name: "Source", steps: null, collect: "data.Source" }],
      clean: [{ step: "lower", pattern: null, with: null }],
    }, true);
    assert.deepEqual([states.shown, states.with, states.cleaned], ["done", "done", "done"]);
    assert.equal(nextStep(states), null);
  });

  it("takes a blank key for none", () => {
    assert.equal(stepStates({ ...keyed, path: "  " }, true).rows, "next");
  });
});

describe("stepOfTarget", () => {
  it("places each part the builder names at its step", () => {
    const placed = Object.fromEntries(
      ["name", "description", "kind", "query", "path", "maxValues", "countRecords", "label", "label[1]", "unlabelled", "attributes.Country", "clean", "clean[0]", "columns.key", "columns.value"]
        .map((target) => [target, stepOfTarget(target)]),
    );
    assert.deepEqual(placed, {
      name: "name",
      description: "name",
      kind: "from",
      query: "from",
      path: "rows",
      maxValues: "rows",
      countRecords: "rows",
      label: "shown",
      "label[1]": "shown",
      unlabelled: "shown",
      "attributes.Country": "with",
      clean: "cleaned",
      "clean[0]": "cleaned",
      "columns.key": "makes",
      "columns.value": "makes",
    });
  });

  it("places a finding about the whole, or a part no step shows, at none", () => {
    assert.equal(stepOfTarget(null), null);
    assert.equal(stepOfTarget("somethingElse"), null);
  });

  it("names the attribute a finding is about", () => {
    assert.equal(attributeOfTarget("attributes.Country"), "Country");
    assert.equal(attributeOfTarget("label"), null);
    assert.equal(attributeOfTarget(null), null);
  });
});

describe("the words of a story", () => {
  it("says a clean step by its effect", () => {
    assert.equal(cleanWords({ step: "lower", pattern: null, with: null }), "lower case");
    assert.equal(cleanWords({ step: "trim", pattern: null, with: null }), "trimmed");
    assert.equal(cleanWords({ step: "replace", pattern: "^GR_", with: "" }), "/^GR_/ replaced");
    assert.equal(cleanWords({ step: "replace", pattern: "", with: "" }), "a replacement");
    assert.equal(cleanWords({ step: "unheardOf", pattern: null, with: null }), "unheardOf");
  });

  it("names a kind's records by their type", () => {
    assert.equal(kindTypeName("osdu:wks:work-product-component--WellLog:1.4.0"), "WellLog");
    assert.equal(kindTypeName("*:*:reference-data--LogCurveType:*"), "LogCurveType");
    assert.equal(kindTypeName("not a kind"), "not a kind");
  });

  it("says a chain by the property each step reads, its filters aside", () => {
    assert.equal(chainWords(["data.GeoContexts[GeoTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityID", "data.GeoPoliticalEntityName"]), "GeoPoliticalEntityID › GeoPoliticalEntityName");
    assert.equal(chainWords(["data.FacilityName"]), "FacilityName");
  });

  it("counts an example's place", () => {
    assert.equal(exampleCount(0, 3), "1 of 3");
    assert.equal(exampleCount(5, 3), "3 of 3");
    assert.equal(exampleCount(0, 0), "none");
  });
});

describe("keyEntityType", () => {
  it("takes the type the template says the key names", () => {
    assert.equal(keyEntityType(["master-data--Wellbore"], null), "master-data--Wellbore");
    assert.equal(keyEntityType(["master-data", "master-data--Wellbore"], "dev:master-data--Well:x:"), "master-data--Wellbore");
  });

  it("falls back to the type the example key is an id of", () => {
    assert.equal(keyEntityType([], "dev:master-data--Wellbore:e2e-builder-1:"), "master-data--Wellbore");
    assert.equal(keyEntityType(["master-data"], "dev:reference-data--UnitQuantity:length:"), "reference-data--UnitQuantity");
  });

  it("says none for a key that names no record", () => {
    assert.equal(keyEntityType([], "CPI"), null);
    assert.equal(keyEntityType([], null), null);
  });
});

describe("firstLocationAt", () => {
  const wellbore = {
    id: "dev:master-data--Wellbore:w1",
    data: {
      FacilityName: "W1",
      GeoContexts: [
        { GeoTypeID: "dev:reference-data--GeoPoliticalEntityType:County:", GeoPoliticalEntityID: "dev:master-data--GeoPoliticalEntity:county-a:" },
        { GeoTypeID: "dev:reference-data--GeoPoliticalEntityType:Country:", GeoPoliticalEntityID: "dev:master-data--GeoPoliticalEntity:us:" },
      ],
      Blank: "  ",
      Ids: ["", "dev:master-data--Well:w:"],
      Depth: 120,
    },
  };

  it("names a property as the record views do", () => {
    assert.equal(firstLocationAt(wellbore, "data.FacilityName", null), "data.FacilityName");
    assert.equal(firstLocationAt(wellbore, "data.Depth", "120"), "data.Depth");
  });

  it("steps into a list to the item holding the value wanted", () => {
    assert.equal(firstLocationAt(wellbore, "data.GeoContexts.GeoPoliticalEntityID", null), "data.GeoContexts[0].GeoPoliticalEntityID");
    assert.equal(firstLocationAt(wellbore, "data.GeoContexts.GeoPoliticalEntityID", "dev:master-data--GeoPoliticalEntity:us:"), "data.GeoContexts[1].GeoPoliticalEntityID");
  });

  it("passes over blank values, in a list of values too", () => {
    assert.equal(firstLocationAt(wellbore, "data.Blank", null), null);
    assert.equal(firstLocationAt(wellbore, "data.Ids", null), "data.Ids[1]");
  });

  it("says none where the record holds nothing, or not the value wanted", () => {
    assert.equal(firstLocationAt(wellbore, "data.Missing", null), null);
    assert.equal(firstLocationAt(wellbore, "data.FacilityName", "W2"), null);
    assert.equal(firstLocationAt(wellbore, "", null), null);
    assert.equal(firstLocationAt(null, "data.FacilityName", null), null);
  });
});

describe("previewRows", () => {
  const wellbore = (key: string, value: string | null, records: number, extra: Partial<PreviewKey> = {}): PreviewKey => ({
    key, value, records, attributes: [{ name: "UUID", value: `${key}-uuid` }], ...extra,
  });

  it("lays out a row a key, with each attribute's value and the key's records", () => {
    const { rows, leftOut } = previewRows(
      [wellbore("W1", "WELLBORE A", 3), wellbore("W2", "WELLBORE B", 1)],
      [{ name: "UUID", collect: null }, { name: "Field", collect: null }],
    );
    assert.deepEqual(rows, [
      { id: "W1", key: "W1", value: "WELLBORE A", attributes: { UUID: "W1-uuid" }, records: 3 },
      { id: "W2", key: "W2", value: "WELLBORE B", attributes: { UUID: "W2-uuid" }, records: 1 },
    ]);
    assert.deepEqual(leftOut, []);
  });

  it("makes a row of each value a key collects, with the records holding that value", () => {
    const made = wellbore("W1", "WELLBORE A", 3, {
      attributes: [{ name: "UUID", value: "u1" }, { name: "Source", value: "WellDB", records: 2 }, { name: "Source", value: "Facade API", records: 1 }],
    });
    const { rows } = previewRows([made], [{ name: "UUID", collect: null }, { name: "Source", collect: "data.Source" }]);
    assert.deepEqual(rows.map((row) => [row.value, row.attributes, row.records]), [
      ["WELLBORE A", { UUID: "u1", Source: "WellDB" }, 2],
      ["WELLBORE A", { UUID: "u1", Source: "Facade API" }, 1],
    ]);
    assert.equal(new Set(rows.map((row) => row.id)).size, 2);
  });

  it("keeps one row, with every record of the key, for a key collecting no value", () => {
    const { rows } = previewRows([wellbore("W2", "W2", 4)], [{ name: "Source", collect: "data.Source" }]);
    assert.deepEqual(rows.map((row) => [row.attributes, row.records]), [[{}, 4]]);
  });

  it("joins several values of one attribute as a build does", () => {
    const made = wellbore("W1", "A", 1, { attributes: [{ name: "Alias", value: "a" }, { name: "Alias", value: "b" }] });
    assert.equal(previewRows([made], [{ name: "Alias", collect: null }]).rows[0].attributes.Alias, "a; b");
  });

  it("lists a key under no name, which is no row, with why", () => {
    const { rows, leftOut } = previewRows(
      [wellbore("W1", null, 3, { leftOut: "cleaning left nothing" }), { key: "W9", attributes: [] }],
      [],
    );
    assert.deepEqual(rows, []);
    assert.deepEqual(leftOut, [{ key: "W1", why: "cleaning left nothing" }, { key: "W9", why: "it has no name" }]);
  });
});

describe("previewWords", () => {
  it("says the whole table when every key is made", () => {
    assert.equal(previewWords(12, 10, 10, true), "12 rows, the whole table: 10 keys");
    assert.equal(previewWords(1, 1, 1, true), "1 row, the whole table: 1 key");
  });

  it("says how many keys the rows are of, and whether the search named them all", () => {
    assert.equal(previewWords(100, 100, 2400, true), "100 rows from the 100 keys most records hold, of 2,400");
    assert.equal(previewWords(140, 100, 1000, false), "140 rows from the 100 keys most records hold, of 1,000 or more");
  });
});
