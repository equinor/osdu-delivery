// How a column the catalog read differently reads in the Schema changes page's history: an added column by its type, a
// dropped one by what it was, and a changed one by what changed about it, with its place named only when nothing else
// moved.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { SchemaChangeColumn } from "../src/api/types.ts";
import { describeColumnChange, movesColumnMatching } from "../src/features/catalog/schemaChangeColumns.ts";

const column = (overrides: Partial<SchemaChangeColumn>): SchemaChangeColumn => ({
  name: "Amount",
  changeType: "Changed",
  fromType: "decimal(18,2)",
  toType: "decimal(18,2)",
  fromNullable: true,
  toNullable: true,
  fromOrdinal: 3,
  toOrdinal: 3,
  ...overrides,
});

describe("describeColumnChange", () => {
  it("gives an added column its type and a dropped one what it was", () => {
    assert.equal(
      describeColumnChange(column({ changeType: "Added", fromType: null, fromNullable: null, fromOrdinal: null, toNullable: false })),
      "decimal(18,2) NOT NULL",
    );
    assert.equal(
      describeColumnChange(column({ changeType: "Deleted", toType: null, toNullable: null, toOrdinal: null })),
      "was decimal(18,2) NULL",
    );
  });

  it("names a type and a nullability that changed, and not the place they shifted to", () => {
    assert.equal(
      describeColumnChange(column({ fromType: "nvarchar(50)", toType: "nvarchar(100)", toNullable: false, toOrdinal: 4 })),
      "nvarchar(50) to nvarchar(100), now NOT NULL",
    );
  });

  it("reads a type spelled differently as the same type", () => {
    assert.equal(describeColumnChange(column({ fromType: "DECIMAL(18, 2)", fromOrdinal: 1 })), "moved from position 1 to 3");
  });

  it("says when a read did not give the type", () => {
    assert.equal(describeColumnChange(column({ changeType: "Added", toType: null, toNullable: null })), "(type not read)");
  });
});

describe("movesColumnMatching", () => {
  it("finds a change by any part of a column's name", () => {
    assert.equal(movesColumnMatching([column({ name: "TripDuration" })], "duration"), true);
    assert.equal(movesColumnMatching([column({ name: "TripDuration" })], "region"), false);
    assert.equal(movesColumnMatching([], "x"), false);
  });
});
