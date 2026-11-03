// What a proof of a value check's finding is asked with, and how its explanation's code is drawn
// (osdu/docs/reference/concepts/value-proof.md).

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { codeParts, proofRequest, type ProvableFinding } from "../src/features/delivery/valueProof.ts";

const cause = {
  kind: "idNotHeld" as const,
  columns: ["dataset.index_type"],
  types: ["WellLogSamplingDomainType"],
  entityType: "reference-data--WellLogSamplingDomainType",
  fields: ["id"],
  ignoreSeparators: false,
  candidates: [],
};

type Cause = typeof cause | { kind: "empty"; columns: string[] };

function finding(values: string[], sourceValues: string[], causes: Cause[] = [cause]): ProvableFinding<Cause> {
  return {
    values: values.map((value) => ({ value, count: 1 })),
    causes,
    sourceValues: sourceValues.map((value) => ({ column: "dataset.index_type", value, count: 1 })),
  };
}

const check = { values: { logSource: "COMPOSITE" }, cacheVersion: "20260908T212727Z" };

describe("the request of a finding's proof", () => {
  it("carries the finding as the check answered it: its first cause, its values, the scope and the cache it read", () => {
    const ask = proofRequest(finding(["dev:reference-data--WellLogSamplingDomainType:TIME2"], ["TIME2"]), "osdu.data.SamplingDomainTypeID", check, "WellLog@1.4.0", "records");
    assert.ok(ask);
    assert.deepEqual(ask.request, {
      target: "osdu.data.SamplingDomainTypeID",
      values: { logSource: "COMPOSITE" },
      cause,
      sourceValues: [{ column: "dataset.index_type", value: "TIME2" }],
      looked: ["dev:reference-data--WellLogSamplingDomainType:TIME2"],
      cacheVersion: "20260908T212727Z",
      mapping: "WellLog@1.4.0",
      run: "records",
    });
    assert.deepEqual(ask.sourceValues, { sent: 1, listed: 1 });
  });

  it("leaves out an empty list rather than sending one", () => {
    const ask = proofRequest(finding([], [], [{ kind: "empty", columns: ["dataset.log_run"] }]), "osdu.data.LogRun", check, null);
    assert.ok(ask);
    assert.equal(ask.request.sourceValues, undefined);
    assert.equal(ask.request.looked, undefined);
    assert.equal(ask.request.mapping, undefined);
  });

  it("stops each list at what a request can carry, the most frequent first, and says how many it kept", () => {
    const many = Array.from({ length: 300 }, (_, i) => `VALUE-${String(i).padStart(6, "0")}-${"x".repeat(20)}`);
    const ask = proofRequest(finding(many, many), "osdu.data.SamplingDomainTypeID", check, null);
    assert.ok(ask);
    assert.ok(ask.sourceValues.sent < 300 && ask.sourceValues.sent > 0);
    assert.equal(ask.sourceValues.listed, 300);
    assert.ok(JSON.stringify(ask.request.sourceValues).length <= 3900);
    assert.ok(JSON.stringify(ask.request.looked).length <= 3900);
    assert.equal(ask.request.looked?.[0], many[0]);
  });

  it("is none for a finding an older node answered without its cause", () => {
    assert.equal(proofRequest({ ...finding(["x"], ["x"]), causes: undefined }, "osdu.data.X", check, null), null);
  });
});

describe("an explanation's code", () => {
  it("is the text the server set in backticks, apart from the words around it", () => {
    assert.deepEqual(codeParts("The cache holds no `UnitOfMeasure` record `ft`"), [
      { text: "The cache holds no ", code: false },
      { text: "UnitOfMeasure", code: true },
      { text: " record ", code: false },
      { text: "ft", code: true },
    ]);
  });

  it("keeps text a backtick left open, and plain text whole", () => {
    assert.deepEqual(codeParts("a `b"), [{ text: "a ", code: false }, { text: "b", code: true }]);
    assert.deepEqual(codeParts("no code here"), [{ text: "no code here", code: false }]);
  });
});
