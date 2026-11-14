// What Write the flow file asks the assistant: the item as the builder wrote it, under dimensions: or types:, in a YAML
// block of its own; the partition it was built over, when there is one; and what the person wants of the file (the
// estate's patterns, the data database for a dimension, the flow already caching the type for a cached type), said as
// requests rather than as the assistant's rules.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { flowFileQuestion, flowFileSubject } from "../src/features/delivery/explorer/dimension/flowFileQuestion.ts";

const ITEM = "  - name: AcmeWellbore\n    kind: acme:wks:master-data--Wellbore:1.0.0\n    path: id\n    label: data.FacilityName\n\n";
const TYPE = "  - kind: 'acme:wks:master-data--Wellbore:*'\n    fields:\n      - data.FacilityName\n";

describe("flowFileQuestion", () => {
  it("lists the item under dimensions in one YAML block, its trailing blank lines dropped", () => {
    const question = flowFileQuestion("dimension", ITEM, "dev");
    assert.ok(question.includes("```yaml\ndimensions:\n  - name: AcmeWellbore\n    kind: acme:wks:master-data--Wellbore:1.0.0\n    path: id\n    label: data.FacilityName\n```"), question);
    assert.ok(question.endsWith("```"));
  });

  it("names the partition the dimension was built over, and says nothing of one when there is none", () => {
    assert.ok(flowFileQuestion("dimension", ITEM, "dev").includes("I built the dimension in the explorer over partition dev:"));
    assert.ok(flowFileQuestion("dimension", ITEM, null).includes("I built the dimension in the explorer:"));
    assert.ok(flowFileQuestion("dimension", ITEM, "").includes("I built the dimension in the explorer:"));
  });

  it("asks for the estate's patterns and the data database, and for the file in one block", () => {
    const question = flowFileQuestion("dimension", ITEM, "dev");
    assert.ok(question.startsWith("Write the whole dimension flow file for the dimension below"));
    assert.ok(question.includes("Follow the dimension flows this estate already has"));
    assert.ok(question.includes("not in the catalog's database"));
    assert.ok(question.includes("Show the file in one YAML block."));
  });

  it("asks for a cache flow file with the type under types, and for the flow that caches the type already", () => {
    const question = flowFileQuestion("cache", TYPE, "dev");
    assert.ok(question.startsWith("Write the whole cache flow file for the cached type below"));
    assert.ok(question.includes("Follow the cache flows this estate already has"));
    assert.ok(question.includes("caches a type of this name already"));
    assert.ok(question.includes("I built the cached type in the explorer over partition dev:"));
    assert.ok(question.includes("```yaml\ntypes:\n  - kind: 'acme:wks:master-data--Wellbore:*'\n    fields:\n      - data.FacilityName\n```"), question);
    // A cached type lives in the partition's cache, so nothing is said of where a table is kept.
    assert.ok(!question.includes("catalog's database"));
  });
});

describe("flowFileSubject", () => {
  it("names what was built, or says what it is about without a name", () => {
    assert.equal(flowFileSubject("dimension", " AcmeWellbore "), "The flow file for AcmeWellbore");
    assert.equal(flowFileSubject("dimension", ""), "The flow file for the dimension");
    assert.equal(flowFileSubject("cache", "Wellbore"), "The flow file for Wellbore");
    assert.equal(flowFileSubject("cache", " "), "The flow file for the cached type");
  });
});
