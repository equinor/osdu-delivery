// What a mapping's page starts a flow's scope on, and how its search finds a mapping (osdu/docs/reference/concepts/gui-build.md, Mappings).

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { mappingRoute, mappingSearchText, matchingMappings, summaryText } from "../src/features/delivery/mappingSearch.ts";
import { wantsFirstOption, withFirstOptions } from "../src/features/delivery/scopeDefaults.ts";

const REQUIRED = { name: "logSource", required: true, default: null };
const DEFAULTED = { name: "batch", required: true, default: "nightly" };
const OPTIONAL = { name: "project", required: false, default: null };

describe("which parameters start on their first option", () => {
  it("is a required one with no default, which nothing else would fill", () => {
    assert.equal(wantsFirstOption(REQUIRED), true);
    assert.equal(wantsFirstOption({ required: true, default: undefined }), true);
  });

  it("is not one with a default, or one left empty on purpose", () => {
    assert.equal(wantsFirstOption(DEFAULTED), false);
    assert.equal(wantsFirstOption(OPTIONAL), false);
  });
});

describe("the fields a page reads the rows with", () => {
  const parameters = [REQUIRED, DEFAULTED, OPTIONAL];
  const first = { logSource: "COMPOSITE", batch: "ignored", project: "ignored" };

  it("gives a parameter left alone its first option, and leaves the others to their document", () => {
    assert.deepEqual(withFirstOptions(parameters, {}, first), { logSource: "COMPOSITE" });
  });

  it("keeps what the reader picked", () => {
    assert.deepEqual(withFirstOptions(parameters, { logSource: "WIRELINE" }, first), { logSource: "WIRELINE" });
  });

  it("leaves a field the reader emptied empty, rather than filling it back under their hands", () => {
    assert.deepEqual(withFirstOptions(parameters, { logSource: undefined }, first), { logSource: undefined });
  });

  it("gives nothing while the first option is not known", () => {
    assert.deepEqual(withFirstOptions(parameters, {}, {}), {});
  });
});

describe("the search of the mappings", () => {
  const mapping = (reference: string, kind: string, relativePath: string, system: string, key: string[]) => ({
    reference, kind, relativePath, templateVersion: "26a3c3441882db4f", summary: { system, key, entries: 34 },
  });
  const log = mapping("WellLog@1.4.0", "osdu:wks:work-product-component--WellLog:1.4.0", "welldb/mappings/WellLog@1.4.0.yaml", "welldb", ["source_project", "log_id"]);
  const bore = mapping("Wellbore@1.2.0", "osdu:wks:master-data--Wellbore:1.2.0", "petrel/mappings/Wellbore@1.2.0.yaml", "petrel", ["uwi"]);
  const found = (search: string) => matchingMappings([log, bore], search).map((row) => row.reference);

  it("finds a mapping by anything its row shows, ignoring case", () => {
    assert.deepEqual(found("WELLBORE"), ["Wellbore@1.2.0"]);
    assert.deepEqual(found("work-product"), ["WellLog@1.4.0"]);
    assert.deepEqual(found("petrel"), ["Wellbore@1.2.0"]);
    assert.deepEqual(found("log_id"), ["WellLog@1.4.0"]);
    assert.deepEqual(found("26a3c344"), ["WellLog@1.4.0", "Wellbore@1.2.0"]);
  });

  it("takes every word typed, in any order", () => {
    assert.deepEqual(found("1.4 welllog"), ["WellLog@1.4.0"]);
    assert.deepEqual(found("welllog petrel"), []);
  });

  it("lists every mapping for an empty search", () => {
    assert.deepEqual(found("   "), ["WellLog@1.4.0", "Wellbore@1.2.0"]);
  });

  it("reads a list of the summary as its values, and leaves out what the sync did not give", () => {
    assert.equal(summaryText(log.summary, "key"), "source_project, log_id");
    assert.equal(summaryText(log.summary, "entries"), "34");
    assert.equal(summaryText({}, "system"), "");
    assert.match(mappingSearchText({ ...log, summary: {} }), /WellLog@1\.4\.0/);
  });

  it("names a mapping's page by its id", () => {
    assert.equal(mappingRoute("a/b"), "/delivery/documents/a%2Fb");
  });
});
