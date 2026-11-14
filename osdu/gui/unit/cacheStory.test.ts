// How the cache builder tells a drafted cached type as a story: the draft an address holds, read defensively; where each
// step stands and which asks for a pick next; which step each of the builder's findings is about; the names a type and
// its values take when the draft gives none; how a sampled value reads in a cell; and the lookup a mapping reads it by.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  cacheAttributeOfTarget, cacheDraftFromParam, cacheDraftParam, cacheStepOfTarget, cacheStepStates, cellText, defaultTypeName, emptyCacheDraft, fieldName,
  lookupExample, nextCacheStep, pathName, sampleWords, suggestedAs, typeNameOf, type CacheStoryDraft,
} from "../src/features/delivery/explorer/cache/cacheStory.ts";

const WELLBORES = "acme:wks:master-data--Wellbore:*";

const wellbore = (): CacheStoryDraft => ({
  ...emptyCacheDraft(WELLBORES),
  fields: [{ path: "data.FacilityName", as: null }, { path: "data.NameAliases.AliasName", as: "Alias" }],
  attributes: [{ name: "WellName", steps: ["data.WellID", "data.FacilityName"] }],
});

describe("the draft in the address", () => {
  it("is written with the parts it has, and read back as the same draft", () => {
    const draft = { ...wellbore(), name: "AcmeWellbore", fetchRecords: true, onChange: "approve" as const };
    const param = cacheDraftParam(draft);
    assert.deepEqual(JSON.parse(param), {
      name: "AcmeWellbore",
      kind: WELLBORES,
      fields: [{ path: "data.FacilityName" }, { path: "data.NameAliases.AliasName", as: "Alias" }],
      attributes: [{ name: "WellName", steps: ["data.WellID", "data.FacilityName"] }],
      fetchRecords: true,
      onChange: "approve",
    });
    assert.deepEqual(cacheDraftFromParam(param), draft);
  });

  it("passes over whatever is not a draft's part", () => {
    assert.deepEqual(cacheDraftFromParam("not json"), emptyCacheDraft(""));
    assert.deepEqual(cacheDraftFromParam("[1,2]"), emptyCacheDraft(""));
    const read = cacheDraftFromParam(JSON.stringify({
      kind: WELLBORES, fields: [{ path: 7 }, "data.Code", { path: "data.Code", as: 3 }], attributes: [{ name: "Empty", steps: [] }, { steps: ["data.A", 4] }],
      fetchRecords: "yes", onChange: "later",
    }));
    assert.deepEqual(read, { ...emptyCacheDraft(WELLBORES), fields: [{ path: "data.Code", as: null }], attributes: [{ name: "", steps: ["data.A"] }] });
  });
});

describe("the names a type and its values take", () => {
  it("names the type after its kind until the draft names it", () => {
    assert.equal(defaultTypeName(WELLBORES), "Wellbore");
    assert.equal(defaultTypeName(""), "");
    assert.equal(typeNameOf({ name: " ", kind: WELLBORES }), "Wellbore");
    assert.equal(typeNameOf({ name: " AcmeWellbore ", kind: WELLBORES }), "AcmeWellbore");
  });

  it("keeps a path under its own name without data, or under the name given", () => {
    assert.equal(pathName("data.FacilityName"), "FacilityName");
    assert.equal(pathName("$.data.NameAliases.AliasName"), "NameAliases.AliasName");
    assert.equal(pathName("kind"), "kind");
    assert.equal(fieldName({ path: "data.FacilityName", as: null }), "FacilityName");
    assert.equal(fieldName({ path: "data.FacilityName", as: " Name " }), "Name");
  });

  it("suggests a name only where the path's own would be awkward or taken", () => {
    assert.equal(suggestedAs("data.FacilityName", []), null);
    assert.equal(suggestedAs("data.NameAliases.AliasName", []), "AliasName");
    assert.equal(suggestedAs("data.NameAliases.AliasName", ["aliasname"]), "AliasName2");
    assert.equal(suggestedAs("data.FacilityName", ["FacilityName"]), "FacilityName2");
    // A record's id is kept already, under that name.
    assert.equal(suggestedAs("data.id", []), "id2");
  });
});

describe("where each step stands", () => {
  it("asks for a type first, then for something to keep", () => {
    const none = cacheStepStates(emptyCacheDraft(""));
    assert.deepEqual(none, { from: "next", keeps: "waiting", with: "waiting", read: "waiting", makes: "waiting" });
    assert.equal(nextCacheStep(none), "from");

    const kind = cacheStepStates(emptyCacheDraft(WELLBORES));
    assert.deepEqual(kind, { from: "done", keeps: "next", with: "open", read: "open", makes: "waiting" });
    assert.equal(nextCacheStep(kind), "keeps");
  });

  it("asks for nothing once the type keeps something, a value or an attribute", () => {
    const full = cacheStepStates({ ...wellbore(), fetchRecords: true });
    assert.deepEqual(full, { from: "done", keeps: "done", with: "done", read: "done", makes: "done" });
    assert.equal(nextCacheStep(full), null);

    const attributes = cacheStepStates({ ...wellbore(), fields: [] });
    assert.deepEqual(attributes, { from: "done", keeps: "open", with: "done", read: "open", makes: "done" });
  });
});

describe("the step a finding is about", () => {
  it("is where the part it names is picked", () => {
    assert.equal(cacheStepOfTarget("name"), "name");
    assert.equal(cacheStepOfTarget("kind"), "from");
    assert.equal(cacheStepOfTarget("query"), "from");
    assert.equal(cacheStepOfTarget("fields"), "keeps");
    assert.equal(cacheStepOfTarget("attributes.WellName"), "with");
    assert.equal(cacheStepOfTarget("lookupKinds"), "with");
    assert.equal(cacheStepOfTarget("fetchRecords"), "read");
    assert.equal(cacheStepOfTarget("onChange"), "read");
    assert.equal(cacheStepOfTarget(null), "makes");
    assert.equal(cacheAttributeOfTarget("attributes.WellName"), "WellName");
    assert.equal(cacheAttributeOfTarget("fields"), null);
  });
});

describe("a sampled value in a cell", () => {
  it("reads as its text, a list joined, an object as JSON, and nothing where the record gave none", () => {
    assert.equal(cellText("WB B/2-9 S"), "WB B/2-9 S");
    assert.equal(cellText(12.5), "12.5");
    assert.equal(cellText(false), "false");
    assert.equal(cellText(["F2", "Fac-2", null]), "F2; Fac-2");
    assert.equal(cellText({ Region: "North" }), '{"Region":"North"}');
    assert.equal(cellText([]), null);
    assert.equal(cellText(undefined), null);
  });

  it("says how many of the type's records are shown", () => {
    assert.equal(sampleWords(25, 4210), "The first 25 of the 4,210 records the type would hold");
    assert.equal(sampleWords(3, 3), "All 3 records the type would hold");
    assert.equal(sampleWords(1, 1), "The one record the type would hold");
    assert.equal(sampleWords(0, 0), "No record matches, so the type would be empty");
  });
});

describe("the lookup a mapping reads the type by", () => {
  it("names the type and finds a record by the first value it keeps", () => {
    assert.equal(
      lookupExample("Wellbore", "FacilityName"),
      "lookups:\n  wellbore:\n    $cache: Wellbore\n    $findBy:\n      - FacilityName = <a source column>",
    );
    assert.equal(lookupExample("Wellbore", null), null);
    assert.equal(lookupExample("", "FacilityName"), null);
  });
});
