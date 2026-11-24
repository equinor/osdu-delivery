// Which inputs the mapping builder offers an entry (osdu/docs/reference/flow/mapping.md).

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { inputsFor, inputsOffered, type EntryDeclared, type EntryPlace, type EntryVariable } from "../src/features/delivery/entryInputs.ts";

const value: EntryVariable = { path: "osdu.data.FacilityName", shape: "Value" };
const values: EntryVariable = { path: "osdu.data.Tags", shape: "ValueList" };
const objects: EntryVariable = { path: "osdu.data.Curves", shape: "GroupList" };

const OWN: EntryPlace = { nested: null, outside: false, inRepeater: false };
const EVERYTHING: EntryDeclared = { searches: 1, lookups: 1, parent: true, children: true };
const NOTHING: EntryDeclared = { searches: 0, lookups: 0, parent: false, children: false };

describe("what a variable takes", () => {
  it("is fixed for the legal lists, and fixed or a list for the access lists", () => {
    assert.deepEqual(inputsFor({ path: "osdu.legal.legaltags", shape: "ValueList" }), ["Static"]);
    assert.deepEqual(inputsFor({ path: "osdu.acl.viewers", shape: "ValueList" }), ["Static", "List"]);
  });

  it("is a value from anywhere for a value, a list besides for a list of values, and a repeat for a list of objects", () => {
    assert.deepEqual(inputsFor(value), ["Dataset", "Expression", "Cache", "Lookup", "Search", "Parent", "Child", "Static", "Coalesce"]);
    assert.deepEqual(inputsFor(values), ["Dataset", "Expression", "Cache", "Lookup", "Search", "Parent", "Child", "Static", "Coalesce", "List"]);
    assert.deepEqual(inputsFor(objects), ["Repeat", "List", "Parent", "Static"]);
    assert.deepEqual(inputsFor({ path: "osdu.data.SpatialLocation", shape: "Group" }), ["Parent", "Static"]);
  });
});

describe("what an entry is offered", () => {
  it("is all its variable takes when the mapping and the template declare everything an input reads", () => {
    assert.deepEqual(inputsOffered(value, OWN, EVERYTHING, null), inputsFor(value));
  });

  it("leaves out a search, a lookup, the parent and a child the mapping or the template does not declare", () => {
    assert.deepEqual(inputsOffered(value, OWN, NOTHING, null), ["Dataset", "Expression", "Cache", "Static", "Coalesce"]);
  });

  it("reads no relative of the record inside a repeater's items", () => {
    assert.deepEqual(
      inputsOffered(value, { ...OWN, inRepeater: true }, EVERYTHING, null),
      ["Dataset", "Expression", "Cache", "Lookup", "Search", "Static", "Coalesce"]);
  });

  it("gives an item of a list of values one value of its own, and an item of a list of objects a group or a fixed object", () => {
    const item: EntryPlace = { ...OWN, nested: "item" };
    assert.deepEqual(inputsOffered(values, item, EVERYTHING, null), ["Static", "Dataset", "Expression", "Cache", "Lookup", "Search", "Child"]);
    assert.deepEqual(inputsOffered(objects, item, EVERYTHING, null), ["Group", "Static"]);
  });

  it("gives an alternative of a coalesce one value, never a child link, a repeat, a coalesce or a list", () => {
    assert.deepEqual(
      inputsOffered(values, { ...OWN, nested: "alternative" }, EVERYTHING, null),
      ["Dataset", "Expression", "Cache", "Lookup", "Search", "Parent", "Static"]);
  });

  it("fills a property of an item as its variable anywhere, but with no repeat and a list of objects written whole", () => {
    const property: EntryPlace = { ...OWN, nested: "property" };
    assert.deepEqual(inputsOffered(objects, property, EVERYTHING, null), ["Parent", "Static"]);
    assert.deepEqual(inputsOffered(values, property, EVERYTHING, null), inputsFor(values));
  });

  it("reads a child dataset's rows for a property of the items those rows fill", () => {
    assert.deepEqual(
      inputsOffered(value, { ...OWN, outside: true }, EVERYTHING, null),
      ["Dataset", "Expression", "Repeat", "Cache", "Lookup", "Search", "Static"]);
  });

  it("keeps the input an entry has, even one no longer offered, so opening it never drops what it is", () => {
    assert.deepEqual(inputsOffered(value, OWN, NOTHING, "Search"), ["Dataset", "Expression", "Cache", "Static", "Coalesce", "Search"]);
    assert.deepEqual(inputsOffered(value, OWN, NOTHING, "Static"), ["Dataset", "Expression", "Cache", "Static", "Coalesce"]);
  });
});
