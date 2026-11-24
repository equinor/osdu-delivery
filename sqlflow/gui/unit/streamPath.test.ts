// A stream's path page: what each flow's run did in a fire, how the canvas inks the hand-overs, and the words its cards
// say it in. The path here is a source's chain in small: files landed by a pre flow into a table, keyed by an ingestion
// flow into the archive, exported by a flow of a kind that counts in its result.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { RunSummary, StreamPath, StreamPathFire, StreamPathNode, StreamPathRun } from "../src/api/types.ts";
import {
  edgeOutcome, filesFigures, fireTallyText, firesOldestFirst, flowsInRunningOrder, formatCount, landedFigures, nodeLabel,
  omissionText, pathEnds, readOutcome, runFigures, runOutcome, runsByFlow,
} from "../src/features/datastreams/streamPathModel.ts";

function run(overrides: Partial<RunSummary>): RunSummary {
  return {
    runId: "r", pipelineId: "p", repoId: null, flowName: "flow", flowKind: "ing", batch: "default", wave: 1,
    status: "succeeded", success: true, targetPool: null, commitSha: null, writtenUtc: "2026-11-20T04:00:30Z",
    enqueuedUtc: null, durationSeconds: 30, rowsLoaded: null, rowsInserted: null, rowsUpdated: null, rowsDeleted: null,
    fileCount: 0, groupId: null, lastAction: null, lastActionUtc: null, error: null, triggerSource: null, fullLoad: false,
    backfillFrom: null, backfillTo: null, incrementalMode: null, incrementalFilter: null, incrementalWatermark: null,
    operation: null, requestedBy: null, startUtc: null, endUtc: null, triggerScheduleId: null, triggerRunId: null,
    startedBy: null, resultCounts: null,
    ...overrides,
  };
}

function node(overrides: Partial<StreamPathNode> & { id: string }): StreamPathNode {
  return {
    type: "object", role: "downstream", name: overrides.id, kind: "table", pipelineId: null, wave: null,
    objectKey: null, qualifiedName: null, external: false, omitted: [], ...overrides,
  };
}

const files = node({ id: "object:files", name: "shop/orders", kind: "file", role: "upstream", external: true });
const pre = node({ id: "flow:pre", type: "flow", name: "orders_pre", kind: "file", pipelineId: "pre", wave: 1, role: "upstream" });
const preTable = node({ id: "object:pre", name: "Orders", qualifiedName: "Shop.pre.Orders", role: "upstream" });
const ing = node({ id: "flow:ing", type: "flow", name: "orders_ing", kind: "ing", pipelineId: "ing", wave: 2, role: "selected" });
const arcTable = node({ id: "object:arc", name: "Orders", qualifiedName: "Shop.arc.Orders" });
const exp = node({ id: "flow:exp", type: "flow", name: "orders_exp", kind: "exp", pipelineId: "exp", wave: 3 });
const exported = node({ id: "object:export", name: "exports/orders", kind: "file" });
const nodes = [ing, pre, preTable, files, arcTable, exp, exported];
const byId = new Map(nodes.map((n) => [n.id, n]));

const path: StreamPath = {
  pipelineId: "ing", flowName: "orders_ing", windowDays: 60, fromUtc: "2026-09-26T00:00:00", asOfUtc: "2026-11-24T10:00:00Z",
  includeBackfills: false, lineageKnown: true, nodes,
  edges: [
    { from: files.id, to: pre.id }, { from: pre.id, to: preTable.id }, { from: preTable.id, to: ing.id },
    { from: ing.id, to: arcTable.id }, { from: arcTable.id, to: exp.id }, { from: exp.id, to: exported.id },
  ],
  streams: [], fires: [], totalFires: 0, olderThanUtc: null,
};

function entry(summary: RunSummary, extra: Partial<StreamPathRun> = {}): StreamPathRun {
  return { run: summary, parts: 0, files: null, ...extra };
}

describe("runOutcome", () => {
  it("says a run moved data when it changed rows, and moved nothing when it read rows and changed none", () => {
    assert.equal(runOutcome(run({ rowsLoaded: 15, rowsInserted: 3, rowsUpdated: 0, rowsDeleted: 0 })), "moved");
    assert.equal(runOutcome(run({ rowsLoaded: 15, rowsInserted: 0, rowsUpdated: 0, rowsDeleted: 0 })), "empty");
  });

  it("goes by the row count for a kind that reports no breakdown, and by the result for a kind that counts there", () => {
    assert.equal(runOutcome(run({ rowsLoaded: 4 })), "moved");
    assert.equal(runOutcome(run({ rowsLoaded: 0 })), "empty");
    assert.equal(runOutcome(run({ rowsLoaded: 0, resultCounts: { delivered: 12, failed: 0 } })), "moved");
    assert.equal(runOutcome(run({ rowsLoaded: 0, resultCounts: { delivered: 0, failed: 0 } })), "empty");
  });

  it("puts the run's lifecycle before what it counted, and a flow with no run in the fire is absent", () => {
    assert.equal(runOutcome(run({ status: "failed", rowsLoaded: 10, rowsInserted: 10 })), "failed");
    assert.equal(runOutcome(run({ status: "running" })), "running");
    assert.equal(runOutcome(run({ status: "queued" })), "queued");
    assert.equal(runOutcome(run({ status: "cancelled" })), "cancelled");
    assert.equal(runOutcome(undefined), "absent");
  });
});

describe("readOutcome", () => {
  it("shows data reaching a run that read rows or files, though it wrote nothing", () => {
    assert.equal(readOutcome(entry(run({ rowsLoaded: 5, rowsInserted: 0, rowsUpdated: 0, rowsDeleted: 0 }))), "moved");
    assert.equal(readOutcome(entry(run({ rowsLoaded: 0 }), { files: { files: 1, rows: 0, bytes: 10, newestModified: null } })), "moved");
    assert.equal(readOutcome(entry(run({ rowsLoaded: 0, rowsInserted: 0 }))), "empty");
    assert.equal(readOutcome(undefined), "absent");
  });
});

describe("edgeOutcome", () => {
  const fire: StreamPathFire = {
    key: "g", groupId: "g", startedUtc: "2026-11-20T04:00:00",
    runs: [
      entry(run({ runId: "a", pipelineId: "pre", rowsLoaded: 5, rowsInserted: 5 }), { files: { files: 1, rows: 5, bytes: 100, newestModified: null } }),
      entry(run({ runId: "b", pipelineId: "ing", rowsLoaded: 5, rowsInserted: 0, rowsUpdated: 0, rowsDeleted: 0 })),
    ],
  };
  const runs = runsByFlow(fire);

  it("follows the data as far as it went: into the ingestion, which changed nothing, and no further", () => {
    const outcome = (from: StreamPathNode, to: StreamPathNode) => edgeOutcome({ from: from.id, to: to.id }, byId, runs);
    assert.equal(outcome(files, pre), "moved");
    assert.equal(outcome(pre, preTable), "moved");
    assert.equal(outcome(preTable, ing), "moved");
    assert.equal(outcome(ing, arcTable), "empty");
    assert.equal(outcome(arcTable, exp), "absent");
  });
});

describe("runsByFlow", () => {
  it("keeps a flow's later run when the fire ran it twice", () => {
    const fire: StreamPathFire = {
      key: "g", groupId: "g", startedUtc: "2026-11-20T04:00:00",
      runs: [
        entry(run({ runId: "first", pipelineId: "ing", writtenUtc: "2026-11-20T04:01:00Z", status: "failed" })),
        entry(run({ runId: "retry", pipelineId: "ing", writtenUtc: "2026-11-20T04:05:00Z" })),
      ],
    };
    assert.equal(runsByFlow(fire).get("ing")?.run.runId, "retry");
    assert.equal(runsByFlow(null).size, 0);
  });
});

describe("the words on a card", () => {
  it("says what a run took in, then what it did to its table", () => {
    assert.equal(runFigures(run({ rowsLoaded: 15, rowsInserted: 3, rowsUpdated: 2, rowsDeleted: 0 })), "15 in · 3 new, 2 changed");
    assert.equal(runFigures(run({ rowsLoaded: 5, rowsInserted: 0, rowsUpdated: 0, rowsDeleted: 0 })), "5 in · nothing changed");
    assert.equal(runFigures(run({ rowsLoaded: null, rowsDeleted: 4 })), "4 removed");
    assert.equal(runFigures(run({ rowsLoaded: 1 })), "1 row");
    assert.equal(runFigures(run({})), "no row counts");
  });

  it("says what landed in a table as signed counts, and nothing for a writer without a breakdown", () => {
    assert.equal(landedFigures(run({ rowsInserted: 3, rowsUpdated: 2, rowsDeleted: 1 })), "+3 ~2 -1");
    assert.equal(landedFigures(run({ rowsInserted: 0, rowsUpdated: 0, rowsDeleted: 0 })), "no change");
    assert.equal(landedFigures(run({ rowsLoaded: 9 })), null);
  });

  it("totals the files a run took in", () => {
    assert.equal(filesFigures({ files: 2, rows: 15, bytes: 150, newestModified: null }), "2 files · 15 rows · 150 B");
    assert.equal(filesFigures({ files: 1, rows: 1, bytes: 2048, newestModified: null }), "1 file · 1 row · 2.0 KiB");
  });

  it("keeps a large count to one short word", () => {
    assert.equal(formatCount(250_000), "250k");
    assert.equal(formatCount(1_500_000), "1.5M");
    assert.equal(formatCount(42), "42");
  });

  it("names what a node stands for without drawing it", () => {
    assert.equal(omissionText("readers", 24), "24 more readers");
    assert.equal(omissionText("inputs", 1), "1 more input");
    assert.equal(omissionText("downstream", 2), "2 flows further on");
  });
});

describe("a fire", () => {
  const ok = (id: string, at: string): StreamPathFire => ({
    key: id, groupId: id, startedUtc: at, runs: [entry(run({ runId: `${id}1` })), entry(run({ runId: `${id}2` }))],
  });

  it("reads oldest first along the strip", () => {
    const fires = [ok("c", "2026-11-22T04:00:00"), ok("a", "2026-11-20T04:00:00"), ok("b", "2026-11-21T04:00:00")];
    assert.deepEqual(firesOldestFirst(fires).map((f) => f.key), ["a", "b", "c"]);
  });

  it("is told by its size and its bad news", () => {
    assert.equal(fireTallyText(ok("a", "2026-11-20T04:00:00")), "2 runs, all succeeded");
    const bad: StreamPathFire = {
      key: "b", groupId: null, startedUtc: "2026-11-20T04:00:00",
      runs: [entry(run({ status: "failed" })), entry(run({ status: "running" })), entry(run({}))],
    };
    assert.equal(fireTallyText(bad), "3 runs, 1 failed, 1 under way");
  });
});

describe("the path", () => {
  it("runs its flows by wave, top to bottom", () => {
    assert.deepEqual(flowsInRunningOrder(path).map((n) => n.name), ["orders_pre", "orders_ing", "orders_exp"]);
  });

  it("begins where data enters and ends where nothing reads on", () => {
    assert.deepEqual(pathEnds(path), { from: ["shop/orders"], to: ["exports/orders"] });
  });

  it("names a table by its schema, so the pre table and the archive table of one name read apart", () => {
    assert.equal(nodeLabel(preTable), "pre.Orders");
    assert.equal(nodeLabel(arcTable), "arc.Orders");
    assert.equal(nodeLabel(files), "shop/orders");
    assert.equal(nodeLabel(ing), "orders_ing");
    const dataset = node({ id: "object:type", name: "acme:wks:order:1.4.0", kind: "record type", qualifiedName: "*.orders.acme:wks:order:1.4.0" });
    assert.equal(nodeLabel(dataset), "acme:wks:order:1.4.0");
  });
});
