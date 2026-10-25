// A run of the first records of its scope (osdu/docs/reference/cli/run.md, A delivery flow's payload, maxRecords): what the
// run dialog's Records at most field takes, what a run's payload is read back as, and the words the run's page names it by.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { MAX_FIRST_RECORDS, firstRecordsField, firstRecordsOf, firstRecordsText } from "../src/features/delivery/firstRecords.ts";

describe("the Records at most field", () => {
  it("reads empty as every record in scope", () => {
    assert.deepEqual(firstRecordsField(""), { count: null, error: null });
    assert.deepEqual(firstRecordsField("   "), { count: null, error: null });
  });

  it("takes a whole number from 1 to 1,000", () => {
    assert.deepEqual(firstRecordsField("1"), { count: 1, error: null });
    assert.deepEqual(firstRecordsField(" 100 "), { count: 100, error: null });
    assert.deepEqual(firstRecordsField(String(MAX_FIRST_RECORDS)), { count: 1000, error: null });
  });

  it("refuses anything else, saying what it takes", () => {
    for (const text of ["0", "1001", "-5", "2.5", "1e2", "ten", "0x10"]) {
      const field = firstRecordsField(text);
      assert.equal(field.count, null, text);
      assert.equal(field.error, "Records at most is a whole number from 1 to 1,000, or empty for every record in scope.", text);
    }
  });
});

describe("a run's payload", () => {
  it("names the cap it asked for, and nothing when it asked for none", () => {
    assert.equal(firstRecordsOf({ maxRecords: 100, force: true }), 100);
    assert.equal(firstRecordsOf({ force: true }), null);
    assert.equal(firstRecordsOf({ maxRecords: "100" }), null);
    assert.equal(firstRecordsOf({ maxRecords: 0 }), null);
    assert.equal(firstRecordsOf(null), null);
    assert.equal(firstRecordsOf(undefined), null);
  });

  it("is named on the run's page by how many records of the scope it read", () => {
    assert.equal(firstRecordsText(1), "first 1 record of the scope");
    assert.equal(firstRecordsText(1000), "first 1,000 records of the scope");
  });
});
