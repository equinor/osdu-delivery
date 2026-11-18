// The access OSDU holds a record under, as every page says it (osdu/docs/reference/flow/mapping-restricted-access.md):
// "restricted" alone is either kind, each record once; each kind carries its qualifier. The Records tab, the Records page
// and a flow's card read their filter, its address and their Restricted count from one module, so they cannot disagree.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  ACCESS_FILTERS, ACCESS_PARAMS, accessAddress, accessChoiceLabel, accessFilterOf, accessQuery, restrictedExplanation, restrictedFigure,
  type AccessFilter, type AccessParam,
} from "../src/features/delivery/recordAccess.ts";

function address(params: Partial<Record<AccessParam, string>>): (name: AccessParam) => string | null {
  return (name) => params[name] ?? null;
}

describe("the Access filter's words", () => {
  it("lists any access, then either kind, then each kind named with its qualifier", () => {
    assert.deepEqual(ACCESS_FILTERS.map((choice) => choice.label), [
      "Any access", "restricted", "restricted until complete", "restricted by an operator",
    ]);
    assert.deepEqual(ACCESS_FILTERS.map((choice) => choice.param), [null, "accessRestricted", "incomplete", "restricted"]);
  });

  it("counts each choice from the flow's figure, and says no count it does not know", () => {
    const figure = { total: 1205, untilComplete: 1200, byOperator: 7 };
    assert.deepEqual(ACCESS_FILTERS.map((choice) => accessChoiceLabel(choice, figure)), [
      "Any access", `restricted (${(1205).toLocaleString()})`, `restricted until complete (${(1200).toLocaleString()})`, "restricted by an operator (7)",
    ]);
    assert.deepEqual(ACCESS_FILTERS.map((choice) => accessChoiceLabel(choice, null)), ACCESS_FILTERS.map((choice) => choice.label));
  });
});

describe("accessFilterOf", () => {
  it("reads the address each link writes", () => {
    assert.equal(accessFilterOf(address({})), "any");
    assert.equal(accessFilterOf(address({ accessRestricted: "true" })), "restricted");
    assert.equal(accessFilterOf(address({ incomplete: "true" })), "until-complete");
    assert.equal(accessFilterOf(address({ restricted: "true" })), "by-operator");
    assert.equal(accessFilterOf(address({ restricted: "false", incomplete: "no" })), "any");
  });

  it("takes the broadest when an address names more than one, so nothing asked for is left out", () => {
    assert.equal(accessFilterOf(address({ incomplete: "true", restricted: "true" })), "restricted");
    assert.equal(accessFilterOf(address({ accessRestricted: "true", restricted: "true" })), "restricted");
  });
});

describe("accessAddress and accessQuery", () => {
  it("round-trip every choice through the address, clearing the other parameters", () => {
    for (const choice of ACCESS_FILTERS) {
      const written = accessAddress(choice.value);
      assert.deepEqual(Object.keys(written).sort(), [...ACCESS_PARAMS].sort());
      assert.equal(ACCESS_PARAMS.filter((name) => written[name] !== null).length, choice.param === null ? 0 : 1);
      assert.equal(accessFilterOf((name) => written[name]), choice.value);
    }
  });

  it("narrows the API by the one parameter each choice names", () => {
    const asked: Record<AccessFilter, object> = {
      any: {}, restricted: { accessRestricted: true }, "until-complete": { incomplete: true }, "by-operator": { restricted: true },
    };
    for (const choice of ACCESS_FILTERS) {
      assert.deepEqual(accessQuery(choice.value), asked[choice.value]);
    }
  });
});

describe("restrictedFigure", () => {
  it("counts a record restricted both ways once, as the server counts it", () => {
    assert.deepEqual(restrictedFigure({ restricted: 3, restrictedUntilComplete: 10, accessRestricted: 12 }), { total: 12, untilComplete: 10, byOperator: 3 });
  });

  it("reads a server without the count as the two kinds added, and none as none", () => {
    assert.deepEqual(restrictedFigure({ restricted: 3, restrictedUntilComplete: 10 }), { total: 13, untilComplete: 10, byOperator: 3 });
    assert.deepEqual(restrictedFigure({}), { total: 0, untilComplete: 0, byOperator: 0 });
  });

  it("is never less than either kind, whatever a count says", () => {
    assert.equal(restrictedFigure({ restricted: 5, restrictedUntilComplete: 2, accessRestricted: 1 }).total, 5);
    assert.equal(restrictedFigure({ restricted: -1, restrictedUntilComplete: 0 }).total, 0);
  });

  it("says each kind on hover, and that the records are counted under their status too", () => {
    assert.equal(
      restrictedExplanation({ total: 12, untilComplete: 10, byOperator: 3 }),
      "Records whose access in OSDU is restricted, each counted once: 10 restricted until complete, 3 restricted by an operator. They are counted under their status too, so this is not part of the total.",
    );
    assert.match(restrictedExplanation({ total: 0, untilComplete: 0, byOperator: 0 }), /once: None is restricted until complete or by an operator\. They/);
  });
});
