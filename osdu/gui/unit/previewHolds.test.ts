// Whether a delivery would hold a previewed record (osdu/docs/reference/concepts/gui.md, A record's page).

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { assertionHoldText, wouldHold } from "../src/features/delivery/previewHolds.ts";

const HOLD = "osdu.data.Curves[].CurveUnit, in 1 of 13 items: dataset.curves.curve_unit is empty, and the entry is required";
const FAILURE = { onFail: "hold", assertion: "unit is known", path: "data.Curves[3].CurveUnit", at: "data.Curves[].CurveUnit" };

describe("whether a delivery would hold a previewed record", () => {
  it("would for a hold of the render, or for a failed assertion that holds", () => {
    assert.equal(wouldHold({ holds: [HOLD] }), true);
    assert.equal(wouldHold({ holds: [], assertions: { held: 1, failures: [FAILURE] } }), true);
  });

  it("would not for a record the render and its assertions let through", () => {
    assert.equal(wouldHold({ holds: [] }), false);
    assert.equal(wouldHold({ holds: [], assertions: { held: 0, failures: [] } }), false);
  });
});

describe("what a failed assertion that holds says", () => {
  it("names the first that holds and where, alone or as one more reason after the render's own", () => {
    assert.equal(
      assertionHoldText({ holds: [], assertions: { held: 1, failures: [FAILURE] } }),
      "The check before sending would hold it, its document kept: \"unit is known\" fails at data.Curves[3].CurveUnit.");
    assert.equal(
      assertionHoldText({ holds: [HOLD], assertions: { held: 3, failures: [FAILURE] } }),
      "An assertion of the mapping holds it as well: \"unit is known\" fails at data.Curves[3].CurveUnit, and 2 more failures hold.");
  });
});
