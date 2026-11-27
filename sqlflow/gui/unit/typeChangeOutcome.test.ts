// How a run's warnings and its changes to existing columns' types read on a page: the word and tone of each outcome, the
// changes an operator has to act on, and a run that succeeded with warnings told apart from a clean one.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { ColumnTypeChange, TypeChangeStatus } from "../src/api/types.ts";
import {
  needingAttention, succeededWithWarnings, typeChangeOutcome, typeChangeTierLabel, warningsLabel,
} from "../src/features/runs/typeChangeOutcome.ts";

function change(overrides: Partial<ColumnTypeChange>): ColumnTypeChange {
  return {
    id: 1,
    runId: "r1",
    pipelineId: "p1",
    objectKey: "${env:dwh}|warehouse|arc|welllog",
    table: "[warehouse].[arc].[WellLog]",
    layer: "target",
    column: "Depth",
    fromType: "int",
    toType: "decimal(18,2)",
    tier: "CheckedConversion",
    status: "Applied",
    needsAttention: false,
    rewritesTable: true,
    isKey: false,
    requiresOptIn: null,
    rows: 10,
    failingValues: null,
    sample: [],
    message: "converted",
    manualSteps: [],
    occurredUtc: "2026-11-27T10:00:00Z",
    requestedBy: null,
    ...overrides,
  };
}

describe("a type change's outcome", () => {
  it("wears the warning tone for each outcome an operator has to act on, and the destructive one for a refusal", () => {
    const tones = (["Pending", "Manual", "RolledBack", "Refused"] as TypeChangeStatus[]).map((s) => typeChangeOutcome(s).tone);
    assert.deepEqual(tones, ["warning", "warning", "warning", "destructive"]);
  });

  it("wears the success tone for a change made, and stays quiet for one that changed nothing", () => {
    assert.deepEqual(typeChangeOutcome("Applied"), { label: "applied", tone: "success" });
    assert.deepEqual(typeChangeOutcome("Resumed"), { label: "finished", tone: "success" });
    assert.equal(typeChangeOutcome("KeptWider").tone, "muted");
    assert.equal(typeChangeOutcome("Planned").tone, "muted");
  });

  it("names each tier in words", () => {
    assert.deepEqual(
      (["Widening", "CheckedConversion", "Manual"] as const).map(typeChangeTierLabel),
      ["widening", "checked conversion", "manual"]);
  });
});

describe("the changes that need attention", () => {
  it("keeps only the ones the API marks, in the order the run reported them", () => {
    const changes = [
      change({ id: 1, status: "Applied" }),
      change({ id: 2, status: "Refused", needsAttention: true }),
      change({ id: 3, status: "KeptWider" }),
      change({ id: 4, status: "Pending", needsAttention: true }),
    ];
    assert.deepEqual(needingAttention(changes).map((c) => c.id), [2, 4]);
    assert.deepEqual(needingAttention([change({})]), []);
  });
});

describe("a run with warnings", () => {
  it("counts its warnings in words", () => {
    assert.equal(warningsLabel(1), "1 warning");
    assert.equal(warningsLabel(3), "3 warnings");
    assert.equal(warningsLabel(1200), "1,200 warnings");
  });

  it("is told apart only when it succeeded and raised a warning", () => {
    assert.equal(succeededWithWarnings("succeeded", 2), true);
    assert.equal(succeededWithWarnings("succeeded", 0), false);
    assert.equal(succeededWithWarnings("succeeded", undefined), false);
    // A failed run is failed whatever it warned of, and a running one has no count yet.
    assert.equal(succeededWithWarnings("failed", 2), false);
    assert.equal(succeededWithWarnings("running", 2), false);
  });
});
