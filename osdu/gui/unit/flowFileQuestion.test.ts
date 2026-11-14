// What Write the flow file asks the assistant: the item as the builder wrote it, under dimensions:, in a YAML block of
// its own; the partition it was built over, when there is one; and the two things the person wants of the file, the
// estate's patterns and the data database, said as requests rather than as the assistant's rules.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { flowFileQuestion, flowFileSubject } from "../src/features/delivery/explorer/dimension/flowFileQuestion.ts";

const ITEM = "  - name: AcmeWellbore\n    kind: acme:wks:master-data--Wellbore:1.0.0\n    path: id\n    label: data.FacilityName\n\n";

describe("flowFileQuestion", () => {
  it("lists the item under dimensions in one YAML block, its trailing blank lines dropped", () => {
    const question = flowFileQuestion(ITEM, "dev");
    assert.ok(question.includes("```yaml\ndimensions:\n  - name: AcmeWellbore\n    kind: acme:wks:master-data--Wellbore:1.0.0\n    path: id\n    label: data.FacilityName\n```"), question);
    assert.ok(question.endsWith("```"));
  });

  it("names the partition the dimension was built over, and says nothing of one when there is none", () => {
    assert.ok(flowFileQuestion(ITEM, "dev").includes("I built the dimension in the explorer over partition dev:"));
    assert.ok(flowFileQuestion(ITEM, null).includes("I built the dimension in the explorer:"));
    assert.ok(flowFileQuestion(ITEM, "").includes("I built the dimension in the explorer:"));
  });

  it("asks for the estate's patterns and the data database, and for the file in one block", () => {
    const question = flowFileQuestion(ITEM, "dev");
    assert.ok(question.startsWith("Write the whole dimension flow file for the dimension below"));
    assert.ok(question.includes("Follow the dimension flows this estate already has"));
    assert.ok(question.includes("not in the catalog's database"));
    assert.ok(question.includes("Show the file in one YAML block."));
  });
});

describe("flowFileSubject", () => {
  it("names the dimension, or says what it is about without a name", () => {
    assert.equal(flowFileSubject(" AcmeWellbore "), "The flow file for AcmeWellbore");
    assert.equal(flowFileSubject(""), "The flow file for the dimension");
  });
});
