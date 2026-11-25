// The lineage graph's data view (sqlflow/docs/reference/guides/lineage-graph.md, What moved): what a flow did in a
// day, what that says on the cards of the flows and objects around it, how the lines between them are inked and
// weighed, a node's own days, which days anything ran, and the day the address names.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { FlowActivity, FlowActivityDay, FlowFire, FlowFireRun, RunSummary } from "../src/api/types.ts";
import {
  changesText, dayIndex, dayKey, dayLabel, dayTallies, edgeFigure, edgeWidth, figureLine, figureOf, figuresByDay, firesOf,
  formatCount, latestDay, neighbourDay, NO_RUN, nodeBars, nodeDays, nodeFigure, startsWeek, totalsOfDay, totalsOfRuns, type Figure,
  type LensNodeSpec,
} from "../src/features/lineage/dataLens.ts";
import { followDataPath, readGraphParams, writeGraphParams } from "../src/features/lineage/graphUrl.ts";
import type { RunMeasureOf } from "../src/modules/runMeasure.ts";

function day(over: Partial<FlowActivityDay>): FlowActivityDay {
  return {
    day: 0, runs: 1, failed: 0, rowsRead: 0, breakdown: false, rowsInserted: 0, rowsUpdated: 0, rowsDeleted: 0,
    files: 0, fileRows: 0, fileBytes: 0, seconds: 0, counts: null, ...over,
  };
}

function run(over: Partial<RunSummary>): RunSummary {
  return {
    runId: "r", pipelineId: "p", flowName: "orders_ing", flowKind: "ing", status: "succeeded", rowsLoaded: null,
    rowsInserted: null, rowsUpdated: null, rowsDeleted: null, durationSeconds: null, resultCounts: null,
    writtenUtc: "2026-01-05T04:00:30Z", startUtc: "2026-01-05T04:00:00Z", enqueuedUtc: null, ...over,
  } as RunSummary;
}

function entry(over: Partial<RunSummary>, files: FlowFireRun["files"] = null): FlowFireRun {
  return { run: run(over), parts: 0, files };
}

const figure = (over: Partial<FlowActivityDay>, measure?: RunMeasureOf): Figure => figureOf(totalsOfDay(day(over)), measure);
const flow = (id: string): LensNodeSpec => ({ id, type: "flow", flows: [id], writers: [], readers: [] });
const table = (id: string, writers: string[], readers: string[] = []): LensNodeSpec => ({ id, type: "object", flows: [], writers, readers });
const from = (figures: Record<string, Figure>) => (flowId: string) => figures[flowId] ?? NO_RUN;

const delivered: RunMeasureOf = (counts) => ({
  moved: counts.delivered ?? 0,
  label: "records delivered",
  taken: counts.planned,
  takenLabel: "planned",
  problems: counts.failed ?? 0,
  problemsLabel: "failed",
  details: [{ label: "held", value: counts.held ?? 0 }, { label: "unchanged", value: counts.unchanged ?? 0 }],
});

describe("what a flow did", () => {
  it("moved what it inserted, updated and deleted, of what it read", () => {
    const loaded = figure({ rowsRead: 15, breakdown: true, rowsInserted: 15 });
    assert.equal(loaded.outcome, "moved");
    assert.equal(loaded.moved, 15);
    assert.equal(loaded.label, "new rows");
    assert.equal(loaded.taken, 15);

    const keyed = figure({ rowsRead: 495_000, breakdown: true, rowsInserted: 9000, rowsUpdated: 3000, rowsDeleted: 400 });
    assert.equal(keyed.moved, 12_400);
    assert.equal(keyed.label, "rows changed");
    assert.deepEqual(keyed.changes, { inserted: 9000, updated: 3000, deleted: 400 });
    assert.equal(figure({ rowsRead: 1, breakdown: true, rowsInserted: 1 }).label, "new row");
  });

  it("ran and moved nothing when it read rows and changed none", () => {
    const unchanged = figure({ rowsRead: 5, breakdown: true });
    assert.equal(unchanged.outcome, "empty");
    assert.equal(unchanged.moved, 0);
    assert.equal(unchanged.taken, 5);
  });

  it("moved what it read when its runs report only a row count", () => {
    const exported = figure({ rowsRead: 40 });
    assert.equal(exported.outcome, "moved");
    assert.equal(exported.moved, 40);
    assert.equal(exported.label, "rows");
    assert.equal(exported.taken, null);
    assert.equal(exported.changes, null);
  });

  it("failed only when every run failed, and says when some did", () => {
    assert.equal(figure({ runs: 2, failed: 2 }).outcome, "failed");
    const partly = figure({ runs: 2, failed: 1, rowsRead: 5, breakdown: true, rowsInserted: 5 });
    assert.equal(partly.outcome, "moved");
    assert.equal(partly.failed, 1);
  });

  it("did not run without totals, and is running while nothing has finished", () => {
    assert.equal(figureOf(null).outcome, "absent");
    assert.equal(NO_RUN.outcome, "absent");
    assert.equal(figureOf(totalsOfRuns([entry({ status: "running" })])).outcome, "running");
    // A cancelled run is neither a finished run nor one under way.
    assert.equal(figureOf(totalsOfRuns([entry({ status: "cancelled" })])).outcome, "absent");
  });

  it("is measured by its kind's own counts when the kind has a measure", () => {
    const sent = figure({ counts: { delivered: 98, planned: 103, failed: 5, held: 3, unchanged: 0 } }, delivered);
    assert.equal(sent.outcome, "moved");
    assert.equal(sent.moved, 98);
    assert.equal(sent.label, "records delivered");
    assert.equal(sent.taken, 103);
    assert.equal(sent.problems, 5);
    // A count of nothing is left out of what is listed.
    assert.deepEqual(sent.details, [{ label: "held", value: 3 }]);
    // A run of the kind that reported no counts moved nothing, whatever rows it loaded.
    assert.equal(figure({ rowsRead: 9 }, delivered).outcome, "empty");
  });

  it("totals a fire's runs the way a day is totalled", () => {
    const totals = totalsOfRuns([
      entry({ rowsLoaded: 10, rowsInserted: 4, rowsUpdated: 1, rowsDeleted: 0, durationSeconds: 2, resultCounts: { delivered: 2 } },
        { files: 2, rows: 10, bytes: 100, newestModified: null }),
      entry({ status: "failed", rowsLoaded: 3, durationSeconds: 1, resultCounts: { delivered: 1, failed: 1 } }),
      entry({ status: "queued" }),
    ]);
    assert.ok(totals !== null);
    assert.equal(totals.runs, 2);
    assert.equal(totals.failed, 1);
    assert.equal(totals.active, 1);
    assert.equal(totals.rowsRead, 13);
    assert.equal(totals.breakdown, true);
    assert.equal(totals.rowsInserted, 4);
    assert.equal(totals.files, 2);
    assert.equal(totals.fileBytes, 100);
    assert.equal(totals.seconds, 3);
    assert.deepEqual(totals.counts, { delivered: 3, failed: 1 });
    assert.equal(totalsOfRuns([]), null);
  });
});

describe("what a card says", () => {
  it("leads a flow's card with what it moved, and says what it read when that differs", () => {
    const loaded = nodeFigure(flow("pre"), from({ pre: figure({ rowsRead: 15, breakdown: true, rowsInserted: 15 }) }));
    assert.deepEqual([loaded.value, loaded.label, loaded.note], ["15", "new rows", ""]);
    const keyed = nodeFigure(flow("ing"), from({ ing: figure({ rowsRead: 15, breakdown: true, rowsInserted: 3, rowsUpdated: 2 }) }));
    assert.deepEqual([keyed.value, keyed.label, keyed.note], ["5", "rows changed", "of 15 read"]);
    assert.equal(figureLine(keyed), "5 rows changed · of 15 read");
  });

  it("says in words when a flow changed nothing, failed, or did not run", () => {
    const unchanged = nodeFigure(flow("ing"), from({ ing: figure({ rowsRead: 5, breakdown: true }) }));
    assert.deepEqual([unchanged.outcome, unchanged.value, unchanged.label, unchanged.note], ["empty", "", "No change", "5 read"]);
    const failed = nodeFigure(flow("ing"), from({ ing: figure({ runs: 3, failed: 3 }) }));
    assert.deepEqual([failed.outcome, failed.label, failed.note], ["failed", "Failed", "all 3 runs"]);
    assert.equal(nodeFigure(flow("ing"), from({})).label, "No run");
    assert.equal(figureLine(nodeFigure(flow("ing"), from({}))), "No run");
  });

  it("marks a failed run among others without calling the flow failed", () => {
    const partly = nodeFigure(flow("ing"), from({ ing: figure({ runs: 2, failed: 1, rowsRead: 5, breakdown: true, rowsInserted: 5 }) }));
    assert.equal(partly.outcome, "moved");
    assert.equal(partly.someFailed, true);
    assert.equal(figureLine(partly), "5 new rows · a run failed");
  });

  it("puts a measured kind's problem beside what it moved", () => {
    const sent = nodeFigure(flow("del"), from({ del: figure({ counts: { delivered: 98, planned: 103, failed: 5 } }, delivered) }));
    assert.deepEqual([sent.value, sent.label, sent.note], ["98", "records delivered", "5 failed"]);
    const clean = nodeFigure(flow("del"), from({ del: figure({ counts: { delivered: 98, planned: 103 } }, delivered) }));
    assert.equal(clean.note, "of 103 planned");
  });

  it("counts the flows that ran for a node standing for several", () => {
    const folder: LensNodeSpec = { id: "g", type: "flow", flows: ["a", "b", "c"], writers: [], readers: [] };
    const figures = from({
      a: figure({ rowsRead: 5, breakdown: true, rowsInserted: 5 }),
      b: figure({ runs: 1, failed: 1 }),
    });
    const said = nodeFigure(folder, figures);
    assert.deepEqual([said.outcome, said.value, said.label, said.note], ["moved", "2", "of 3 flows ran", "1 failed"]);
    assert.equal(nodeFigure(folder, from({})).label, "No run");
  });

  it("says what landed in an object its flows write", () => {
    const pre = figure({ rowsRead: 15, breakdown: true, rowsInserted: 15 });
    const landed = nodeFigure(table("pre.orders", ["pre"]), from({ pre }));
    assert.deepEqual([landed.outcome, landed.value, landed.label, landed.note], ["moved", "+15", "rows", ""]);

    const ing = figure({ rowsRead: 15, breakdown: true, rowsInserted: 3, rowsUpdated: 2, rowsDeleted: 1 });
    const keyed = nodeFigure(table("arc.orders", ["ing"]), from({ ing }));
    assert.deepEqual([keyed.value, keyed.label, keyed.note], ["6", "rows changed", "3 new, 2 updated, 1 removed"]);

    const sent = nodeFigure(table("kind", ["del"]), from({ del: figure({ counts: { delivered: 98 } }, delivered) }));
    assert.deepEqual([sent.value, sent.label], ["98", "records delivered"]);
  });

  it("says when an object was not written, not changed, or its write failed", () => {
    assert.equal(nodeFigure(table("t", ["ing"]), from({})).label, "Not written");
    assert.equal(nodeFigure(table("t", ["ing"]), from({ ing: figure({ rowsRead: 5, breakdown: true }) })).label, "No change");
    const failed = nodeFigure(table("t", ["ing"]), from({ ing: figure({ runs: 1, failed: 1 }) }));
    assert.deepEqual([failed.outcome, failed.label], ["failed", "Write failed"]);
    // A writer's failed run among good ones is the writer's news, not the table's.
    const partly = nodeFigure(table("t", ["ing"]), from({ ing: figure({ runs: 2, failed: 1, rowsRead: 5, breakdown: true, rowsInserted: 5 }) }));
    assert.equal(partly.someFailed, false);
  });

  it("says what arrived where nothing in scope writes: the files its readers took in, or the rows they read", () => {
    const pre = figure({ rowsRead: 15, breakdown: true, rowsInserted: 15, files: 2, fileRows: 15, fileBytes: 2048 });
    const arrived = nodeFigure(table("files", [], ["pre"]), from({ pre }));
    assert.deepEqual([arrived.outcome, arrived.value, arrived.label, arrived.note], ["moved", "2", "files", "15 rows · 2.0 KiB"]);

    const read = nodeFigure(table("source", [], ["ing"]), from({ ing: figure({ rowsRead: 7, breakdown: true }) }));
    assert.deepEqual([read.value, read.label], ["7", "rows read"]);
    assert.equal(nodeFigure(table("source", [], ["ing"]), from({})).label, "Not read");
    assert.equal(nodeFigure(table("source", [], ["ing"]), from({ ing: figure({ runs: 1, failed: 1 }) })).label, "Read failed");
  });
});

describe("how a line is inked and weighed", () => {
  const pre = figure({ rowsRead: 15, breakdown: true, rowsInserted: 15, files: 1, fileRows: 15 });
  const ing = figure({ rowsRead: 15, breakdown: true });
  const figures = from({ pre, ing });
  const end = (spec: LensNodeSpec) => ({ type: spec.type, figure: nodeFigure(spec, figures) });

  it("carries what a flow moved out of it, and what reached a flow into it", () => {
    assert.deepEqual(edgeFigure(end(table("files", [], ["pre"])), end(flow("pre"))), { outcome: "moved", volume: 15 });
    assert.deepEqual(edgeFigure(end(flow("pre")), end(table("pre.orders", ["pre"], ["ing"]))), { outcome: "moved", volume: 15 });
    // The ingestion read fifteen rows and changed none: data reached it, and nothing went on.
    assert.deepEqual(edgeFigure(end(table("pre.orders", ["pre"], ["ing"])), end(flow("ing"))), { outcome: "moved", volume: 15 });
    assert.deepEqual(edgeFigure(end(flow("ing")), end(table("arc.orders", ["ing"]))), { outcome: "empty", volume: 0 });
    assert.deepEqual(edgeFigure(end(flow("exp")), end(table("out", ["exp"]))), { outcome: "absent", volume: 0 });
  });

  it("carries what landed in the second of two objects, in the drawing of data alone", () => {
    assert.deepEqual(edgeFigure(end(table("pre.orders", ["pre"])), end(table("arc.orders", ["ing"]))), { outcome: "empty", volume: 0 });
    assert.deepEqual(edgeFigure(end(table("files", [], ["pre"])), end(table("pre.orders", ["pre"]))), { outcome: "moved", volume: 15 });
    assert.deepEqual(edgeFigure(undefined, undefined), { outcome: "absent", volume: 0 });
  });

  it("widens with the square root of what it carries, between a least and a greatest width", () => {
    assert.equal(edgeWidth(0, 100), 1.5);
    assert.equal(edgeWidth(100, 100), 5);
    assert.equal(edgeWidth(25, 100), 1.5 + 3.5 * 0.5);
    assert.equal(edgeWidth(500, 100), 5);
    assert.equal(edgeWidth(10, 0), 1.5);
  });
});

describe("the window, day by day", () => {
  const activity: FlowActivity = {
    windowDays: 5,
    fromUtc: "2026-01-05T00:00:00Z",
    asOfUtc: "2026-01-09T10:00:00Z",
    flows: [
      { pipelineId: "pre", days: [day({ day: 1, rowsRead: 9, breakdown: true, rowsInserted: 9 }), day({ day: 3, rowsRead: 4, breakdown: true, rowsInserted: 4 })] },
      { pipelineId: "ing", days: [day({ day: 1, rowsRead: 9, breakdown: true }), day({ day: 3, runs: 2, failed: 1, rowsRead: 4, breakdown: true, rowsInserted: 1 })] },
      { pipelineId: "del", days: [day({ day: 3, counts: { delivered: 2 } }), day({ day: 9 })] },
    ],
  };
  const figures = figuresByDay(activity, (flowId) => (flowId === "del" ? delivered : undefined));
  const on = (flowId: string, at: number) => figures.get(flowId)?.[at] ?? NO_RUN;

  it("gives each flow a figure for the days it ran, and none for the others", () => {
    assert.equal(figures.get("pre")?.length, 5);
    assert.equal(figures.get("pre")?.[0], undefined);
    assert.equal(figures.get("pre")?.[1]?.moved, 9);
    assert.equal(figures.get("del")?.[3]?.label, "records delivered");
    // A day outside the window is left out, not written past the end.
    assert.equal(figures.get("del")?.length, 5);
  });

  it("charts a node with the measure its card leads with", () => {
    assert.deepEqual(nodeBars(flow("pre"), 5, on).map((bar) => bar.value), [0, 9, 0, 4, 0]);
    assert.deepEqual(nodeBars(flow("ing"), 5, on).map((bar) => bar.outcome), ["absent", "empty", "absent", "moved", "absent"]);
    assert.equal(nodeBars(flow("ing"), 5, on)[3].someFailed, true);
    assert.deepEqual(nodeBars(table("pre.orders", ["pre"]), 5, on).map((bar) => bar.value), [0, 9, 0, 4, 0]);
    // A node standing for several flows charts how many of them ran.
    const folder: LensNodeSpec = { id: "g", type: "flow", flows: ["pre", "ing", "del"], writers: [], readers: [] };
    assert.deepEqual(nodeBars(folder, 5, on).map((bar) => bar.value), [0, 2, 0, 3, 0]);
  });

  it("tallies each day's flows by how they went, a failed run counting as failed", () => {
    const tallies = dayTallies(["pre", "ing", "del"], 5, figures);
    assert.deepEqual(tallies[1], { moved: 1, empty: 1, failed: 0 });
    assert.deepEqual(tallies[3], { moved: 2, empty: 0, failed: 1 });
    assert.deepEqual(tallies[0], { moved: 0, empty: 0, failed: 0 });
    assert.equal(latestDay(tallies), 3);
    assert.equal(latestDay(dayTallies(["other"], 5, figures)), null);
    assert.equal(neighbourDay(tallies, 3, -1), 1);
    assert.equal(neighbourDay(tallies, 1, -1), null);
    assert.equal(neighbourDay(tallies, 1, 1), 3);
    assert.equal(neighbourDay(tallies, 3, 1), null);
  });

  it("lists the days a node ran, the latest first, and steps between them", () => {
    const bars = nodeBars(flow("ing"), 5, on);
    assert.deepEqual(nodeDays(bars, 3, 5), { ran: [1, 3], earlier: 1, later: null, listed: [3, 1] });
    // A day it did not run on is stepped away from in both directions, and is not a row of the list.
    assert.deepEqual(nodeDays(bars, 2, 1), { ran: [1, 3], earlier: 1, later: 3, listed: [3] });
    // The day looked at stays in the short list however old it is.
    assert.deepEqual(nodeDays(bars, 1, 1).listed, [3, 1]);
    assert.deepEqual(nodeDays(nodeBars(flow("other"), 5, on), 2, 5), { ran: [], earlier: null, later: null, listed: [] });
  });

  it("names a day by its UTC date, and finds it again", () => {
    assert.equal(dayKey(activity.fromUtc, 0), "2026-01-05");
    assert.equal(dayKey(activity.fromUtc, 3), "2026-01-08");
    assert.equal(dayIndex(activity.fromUtc, "2026-01-08", 5), 3);
    assert.equal(dayIndex(activity.fromUtc, "2026-01-10", 5), null);
    assert.equal(dayIndex(activity.fromUtc, "2026-01-04", 5), null);
    assert.equal(dayIndex(activity.fromUtc, "yesterday", 5), null);
    assert.equal(dayLabel(activity.fromUtc, 0), "Mon 5 Jan");
    assert.equal(dayLabel(activity.fromUtc, 3, false), "8 Jan");
    assert.equal(startsWeek(activity.fromUtc, 0), true);
    assert.equal(startsWeek(activity.fromUtc, 1), false);
  });
});

describe("a day's fires", () => {
  const morning: FlowFire = {
    key: "g1", groupId: "g1", startedUtc: "2026-01-05T04:00:00Z",
    runs: [
      entry({ runId: "a", pipelineId: "pre", flowName: "orders_pre", rowsLoaded: 9, rowsInserted: 9, rowsUpdated: 0, rowsDeleted: 0 }),
      entry({ runId: "b", pipelineId: "ing", flowName: "orders_ing", status: "failed" }),
      entry({ runId: "c", pipelineId: "other", flowName: "other_ing", rowsLoaded: 1 }),
    ],
  };
  const alone: FlowFire = {
    key: "d", groupId: null, startedUtc: "2026-01-05T04:00:40Z",
    runs: [entry({ runId: "d", pipelineId: "ing", flowName: "orders_ing", rowsLoaded: 9, rowsInserted: 2, rowsUpdated: 0, rowsDeleted: 0 })],
  };

  it("are cut down to the flows drawn, oldest first, and dropped when they ran none of them", () => {
    const drawn = firesOf([alone, morning], new Set(["pre", "ing"]));
    assert.deepEqual(drawn.map((fire) => fire.key), ["g1", "d"]);
    assert.deepEqual(drawn[0].runs.map((item) => item.run.runId), ["a", "b"]);
    assert.deepEqual(firesOf([alone, morning], new Set(["other"])).map((fire) => fire.key), ["g1"]);
    assert.deepEqual(firesOf([alone], new Set(["pre"])), []);
  });
});

describe("the words and numbers", () => {
  it("write a count whole to a hundred thousand and compact past it", () => {
    assert.equal(formatCount(0), "0");
    assert.equal(formatCount(99_999), "99,999");
    assert.equal(formatCount(495_000), "495k");
    assert.equal(formatCount(3_287_655), "3.3M");
    assert.equal(formatCount(12_000_000), "12M");
  });

  it("say what a flow did to its target, leaving out what is zero", () => {
    assert.equal(changesText({ inserted: 3, updated: 2, deleted: 1 }), "3 new, 2 updated, 1 removed");
    assert.equal(changesText({ inserted: 0, updated: 2, deleted: 0 }), "2 updated");
  });
});

describe("the address", () => {
  it("holds the data view and its day, and leaves the day out when the view is off", () => {
    const on = readGraphParams(new URLSearchParams("repoId=r&data=on&day=2026-01-08"));
    assert.equal(on.data, true);
    assert.equal(on.day, "2026-01-08");
    assert.equal(writeGraphParams(on).toString(), "repoId=r&data=on&day=2026-01-08");

    assert.equal(writeGraphParams({ ...on, data: false }).toString(), "repoId=r");
    // No day named looks at the latest one anything ran.
    assert.equal(writeGraphParams({ ...on, day: "" }).toString(), "repoId=r&data=on");
    // A day that is not a date is no day.
    assert.equal(readGraphParams(new URLSearchParams("data=on&day=today")).day, "");
    assert.equal(readGraphParams(new URLSearchParams("repoId=r")).data, false);
  });

  it("follows one flow's data: its trace with every object on it and the data view on", () => {
    assert.equal(followDataPath("flow-1", "repo-1"), "/lineage?repoId=repo-1&focus=flow-1&show=all&data=on");
    assert.equal(followDataPath("flow-1", null), "/lineage?focus=flow-1&show=all&data=on");
  });
});
