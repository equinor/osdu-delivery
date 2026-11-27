// What the probe of a page's delivery runs reads off the runs it lists: which flows a run moved on, so their views are read
// again, and which runs are under way, which a flow's page shows above its tabs.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  countsDue, moved, RUNNING_COUNTS_MS, runsUnderWay, signatures, underWay, type FollowedRun,
} from "../src/features/delivery/followRunsState.ts";

function run(runId: string, status: string, pipelineId = "flow-a"): FollowedRun {
  return { runId, pipelineId, status };
}

describe("underWay", () => {
  it("counts a queued or running run, and no run that ended", () => {
    assert.equal(underWay(run("r1", "queued")), true);
    assert.equal(underWay(run("r1", "running")), true);
    for (const ended of ["succeeded", "failed", "cancelled"]) {
      assert.equal(underWay(run("r1", ended)), false, ended);
    }
  });
});

describe("signatures and moved", () => {
  it("moves nothing on the first probe, which nothing has shown yet", () => {
    assert.deepEqual(moved(new Map(), signatures([run("r1", "running")])), []);
  });

  it("moves a flow whose newest run ended", () => {
    const before = signatures([run("r1", "running")]);
    assert.deepEqual(moved(before, signatures([run("r1", "succeeded")])), ["flow-a"]);
  });

  it("moves a flow when a run arrives, even while another of its runs stands still", () => {
    const before = signatures([run("r1", "running")]);
    assert.deepEqual(moved(before, signatures([run("r2", "queued"), run("r1", "running")])), ["flow-a"]);
  });

  it("moves a flow when an older run of it ends, not only its newest: a part of a split delivery, say", () => {
    const before = signatures([run("part", "running"), run("root", "running")]);
    const now = signatures([run("part", "succeeded"), run("root", "running")]);
    assert.deepEqual(moved(before, now), ["flow-a"]);
  });

  it("leaves a flow whose runs stand as they were", () => {
    const listing = [run("r2", "running"), run("r1", "succeeded")];
    assert.deepEqual(moved(signatures(listing), signatures(listing)), []);
  });

  it("keeps each flow's runs to its own signature", () => {
    const before = signatures([run("a1", "running", "flow-a"), run("b1", "running", "flow-b")]);
    const now = signatures([run("a1", "running", "flow-a"), run("b1", "failed", "flow-b")]);
    assert.deepEqual(moved(before, now), ["flow-b"]);
  });
});

describe("runsUnderWay", () => {
  it("keeps the runs under way in the listing's order, newest first", () => {
    const listing = [run("r3", "queued"), run("r2", "succeeded"), run("r1", "running")];
    assert.deepEqual(runsUnderWay(listing).map((each) => each.runId), ["r3", "r1"]);
  });

  it("answers none while nothing runs", () => {
    assert.deepEqual(runsUnderWay([run("r1", "succeeded"), run("r0", "failed")]), []);
  });
});

describe("countsDue", () => {
  it("reads a running flow's counts again once they are as old as the cadence allows, and not before", () => {
    const read = 1_000_000;
    assert.equal(countsDue(read, read + RUNNING_COUNTS_MS - 1), false);
    assert.equal(countsDue(read, read + RUNNING_COUNTS_MS), true);
    assert.equal(countsDue(read, read + 60_000), true);
  });

  it("reads counts never read at all", () => {
    assert.equal(countsDue(0, Date.now()), true);
  });
});
