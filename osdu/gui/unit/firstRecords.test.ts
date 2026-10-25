// A run of the first records of its scope (osdu/docs/reference/cli/run.md, A delivery flow's payload, maxRecords): what a
// run's payload is read back as, and the words the run's page names it by.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { firstRecordsOf, firstRecordsText } from "../src/features/delivery/firstRecords.ts";

describe("a run's payload", () => {
  it("names how many records it asked for, 0 for the whole scope, and nothing when it asked for none", () => {
    assert.equal(firstRecordsOf({ maxRecords: 100, force: true }), 100);
    assert.equal(firstRecordsOf({ maxRecords: 0 }), 0);
    assert.equal(firstRecordsOf({ force: true }), null);
    assert.equal(firstRecordsOf({ maxRecords: "100" }), null);
    assert.equal(firstRecordsOf({ maxRecords: -1 }), null);
    assert.equal(firstRecordsOf({ maxRecords: 2.5 }), null);
    assert.equal(firstRecordsOf(null), null);
    assert.equal(firstRecordsOf(undefined), null);
  });

  it("is named on the run's page by how many records of the scope it read", () => {
    assert.equal(firstRecordsText(1), "first 1 record of the scope");
    assert.equal(firstRecordsText(1000), "first 1,000 records of the scope");
    assert.equal(firstRecordsText(0), "the whole scope (maxRecords 0)");
  });
});
