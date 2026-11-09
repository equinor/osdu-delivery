// The findBy lines of a mapping's cache nodes (osdu/docs/reference/flow/mapping-lookups.md), as the mapping builder edits
// them and as the cache pages name what a line of several comparisons looked up.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  bareColumn, comparisonsOf, compositeLookedUp, findByPhrase, findLinesOf, findsOf, newFindLine, type DraftFind,
} from "../src/features/delivery/findByLines.ts";

const operand = (find: DraftFind): string => (find.literal !== null && find.literal !== "" ? `"${find.literal}"` : `dataset.${find.column ?? ""}`);

describe("a findBy line as the builder edits it", () => {
  it("keeps the comparisons after the first, and writes a line of one without them", () => {
    const finds: DraftFind[] = [
      { field: "well", column: "well", literal: null, and: [{ field: "mnemonic", column: "mnemonic", literal: null }] },
      { field: "mnemonic", column: "mnemonic", literal: null, and: null },
    ];

    const lines = findLinesOf(finds);
    assert.deepEqual(lines, [
      { field: "well", mode: "column", value: "well", and: [{ field: "mnemonic", mode: "column", value: "mnemonic" }] },
      { field: "mnemonic", mode: "column", value: "mnemonic", and: [] },
    ]);
    assert.deepEqual(findsOf(lines), [
      { field: "well", column: "well", literal: null, and: [{ field: "mnemonic", column: "mnemonic", literal: null }] },
      { field: "mnemonic", column: "mnemonic", literal: null, and: null },
    ]);
  });

  it("trims a field, drops the dataset prefix of a column, and keeps a fixed text as it is", () => {
    const [line] = findsOf([{ field: " well ", mode: "column", value: " dataset.well ", and: [{ field: "kind", mode: "text", value: " LWD " }] }]);
    assert.deepEqual(line, { field: "well", column: "well", literal: null, and: [{ field: "kind", column: null, literal: " LWD " }] });
    assert.equal(bareColumn("dataset.curves.unit"), "curves.unit");
    assert.deepEqual(newFindLine("Code"), { field: "Code", mode: "column", value: "", and: [] });
  });

  it("reads a draft written before lines took and as lines of one", () => {
    assert.deepEqual(findLinesOf([{ field: "Code", column: "unit", literal: null }]), [{ field: "Code", mode: "column", value: "unit", and: [] }]);
    assert.deepEqual(comparisonsOf({ field: "Code", column: "unit", literal: null }).map((c) => c.field), ["Code"]);
  });
});

describe("the lines as one phrase", () => {
  it("runs lines of one comparing the same value together, and brackets a line of several beside others", () => {
    const unit: DraftFind = { field: "Code", column: "unit", literal: null };
    const name: DraftFind = { field: "Name", column: "unit", literal: null };
    const together: DraftFind = { field: "well", column: "well", literal: null, and: [{ field: "mnemonic", column: "mnemonic", literal: null }] };

    assert.equal(findByPhrase([unit, name], operand), "Code/Name = dataset.unit");
    assert.equal(findByPhrase([together], operand), "well = dataset.well and mnemonic = dataset.mnemonic");
    assert.equal(findByPhrase([together, unit, name], operand), "(well = dataset.well and mnemonic = dataset.mnemonic) or Code/Name = dataset.unit");
    assert.equal(findByPhrase([unit, together, name], operand), "Code = dataset.unit or (well = dataset.well and mnemonic = dataset.mnemonic) or Name = dataset.unit");
  });
});

describe("what a line of several comparisons looked up", () => {
  it("pairs each field with its value, and leaves anything else to be shown as it is kept", () => {
    assert.equal(compositeLookedUp("well and mnemonic", "[\"A-12\",\"GR\"]"), "well = A-12 and mnemonic = GR");
    assert.equal(compositeLookedUp("mnemonic", "GR"), null);
    assert.equal(compositeLookedUp("well and mnemonic", "[\"A-12\",\"G"), null, "a value the ledger cut short");
    assert.equal(compositeLookedUp("well and mnemonic", "[\"A-12\"]"), null, "a count that does not match the fields");
    assert.equal(compositeLookedUp("well and mnemonic", "[\"A-12\", 4]"), null);
    assert.equal(compositeLookedUp("well and mnemonic", null), null);
  });
});
