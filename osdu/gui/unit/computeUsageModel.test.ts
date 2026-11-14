// What the Compute usage page works out (osdu/docs/reference/concepts/compute-usage.md): the days a period stands for,
// the division of the usage as teams, repositories, project folders and flows, what a search finds in it, the figures
// as they are shown, and the file the division downloads as.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  branchIds, chargedCores, daysOf, divisionCsv, divisionOf, formatShare, formatUnits, formatWallClock, labelOf, matches, periodOf,
  periodRange, shareOf, totalOf, unassignedUnits, type ComputeShare,
} from "../src/features/delivery/compute/computeUsageModel.ts";

const share = (team: string, repo: string, project: string, flow: string, computeUnits: number, kind: "run" | "task" = "run"): ComputeShare => ({
  team, repo, project, flow, kind, work: 2, wallClockMs: Math.round(computeUnits * 3_600_000), computeUnits,
});

const SHARES: ComputeShare[] = [
  share("Wells", "estate", "wells", "wells-delivery", 6),
  share("Wells", "estate", "wells", "wells-ingest", 2),
  share("Logs", "estate", "logs", "logs-delivery", 10),
  share("Logs", "other", "", "at-the-root", 1),
  share("", "estate", "misc", "untagged-flow", 0.5),
  share("", "", "", "query", 0.5, "task"),
];

describe("the period", () => {
  const now = new Date("2026-11-14T08:30:00Z");

  it("covers the days its name says, ending today in UTC", () => {
    assert.deepEqual(periodRange("7d", now), { from: "2026-11-08", to: "2026-11-14" });
    assert.deepEqual(periodRange("30d", now), { from: "2026-10-16", to: "2026-11-14" });
    assert.deepEqual(periodRange("90d", now), { from: "2026-08-17", to: "2026-11-14" });
    assert.deepEqual(periodRange("365d", now), { from: "2025-11-15", to: "2026-11-14" });
    assert.deepEqual(periodRange("month", now), { from: "2026-11-01", to: "2026-11-14" });
    assert.deepEqual(periodRange("last-month", now), { from: "2026-10-01", to: "2026-10-31" });
  });

  it("takes last month across a year's end", () => {
    assert.deepEqual(periodRange("last-month", new Date("2027-01-03T00:10:00Z")), { from: "2026-12-01", to: "2026-12-31" });
  });

  it("falls back to the default for an address that names none", () => {
    assert.equal(periodOf("90d"), "90d");
    assert.equal(periodOf("a-year"), "30d");
    assert.equal(periodOf(null), "30d");
  });

  it("gives every day a bar, with nothing on the days nothing ran", () => {
    const days = daysOf("2026-11-10", "2026-11-13", [{ day: "2026-11-11", work: 3, wallClockMs: 60_000, computeUnits: 0.2 }]);
    assert.deepEqual(days.map((day) => day.day), ["2026-11-10", "2026-11-11", "2026-11-12", "2026-11-13"]);
    assert.deepEqual(days.map((day) => day.computeUnits), [0, 0.2, 0, 0]);
  });
});

describe("the division", () => {
  it("sums each team, repository and project folder from its flows, the largest first", () => {
    const division = divisionOf(SHARES);
    assert.deepEqual(division.map((team) => [team.label, team.computeUnits, team.work]), [["Logs", 11, 4], ["Wells", 8, 4], ["No team", 1, 4]]);

    const logs = division[0];
    assert.deepEqual(logs.children.map((repo) => [repo.label, repo.computeUnits]), [["estate", 10], ["other", 1]]);
    assert.deepEqual(logs.children[1].children.map((project) => [project.label, project.unnamed]), [["Repository root", true]]);

    const wells = division[1].children[0].children[0];
    assert.deepEqual(wells.children.map((flow) => [flow.label, flow.computeUnits, flow.kind]), [["wells-delivery", 6, "run"], ["wells-ingest", 2, "run"]]);
    assert.deepEqual(wells.children[0].filter, { team: "Wells", repo: "estate", project: "wells", flow: "wells-delivery" });
    assert.deepEqual(wells.filter, { team: "Wells", repo: "estate", project: "wells" });

    const none = division[2];
    assert.equal(none.unnamed, true);
    assert.deepEqual(none.filter, { team: "" });
    assert.deepEqual(none.children.map((repo) => repo.label).sort(), ["No repository", "estate"]);
    assert.deepEqual(totalOf(division), { work: 12, wallClockMs: 72_000_000, computeUnits: 20 });
  });

  it("keeps a flow and a compute task of the same name apart, and the same flow under two teams", () => {
    const division = divisionOf([
      share("A", "estate", "p", "same", 1),
      share("A", "estate", "p", "same", 2, "task"),
      share("B", "estate", "p", "same", 5),
    ]);
    assert.deepEqual(division.map((team) => team.label), ["B", "A"]);
    assert.deepEqual(division[1].children[0].children[0].children.map((flow) => [flow.kind, flow.computeUnits]), [["task", 2], ["run", 1]]);
  });

  it("narrows to what a search finds, by team, repository, project folder or flow, and sums only that", () => {
    assert.deepEqual(divisionOf(SHARES, "logs").map((team) => [team.label, team.computeUnits]), [["Logs", 11]]);
    assert.deepEqual(divisionOf(SHARES, "ingest").map((team) => [team.label, team.computeUnits]), [["Wells", 2]]);
    assert.deepEqual(divisionOf(SHARES, "other").map((team) => [team.label, team.computeUnits]), [["Logs", 1]]);
    assert.deepEqual(divisionOf(SHARES, "no team").map((team) => team.label), ["No team"]);
    assert.deepEqual(divisionOf(SHARES, "  ").length, 3);
    assert.deepEqual(divisionOf(SHARES, "nothing-like-it"), []);
  });

  it("names every branch a search opens", () => {
    const division = divisionOf(SHARES, "ingest");
    assert.equal(branchIds(division).length, 3);
    assert.deepEqual(branchIds([]), []);
  });

  it("counts what no team is charged for", () => {
    assert.equal(unassignedUnits(SHARES), 1);
    assert.equal(unassignedUnits([]), 0);
  });

  it("says what an empty value stands for", () => {
    assert.equal(labelOf("team", ""), "No team");
    assert.equal(labelOf("repo", ""), "No repository");
    assert.equal(labelOf("project", ""), "Repository root");
    assert.equal(labelOf("team", "Wells"), "Wells");
  });
});

describe("the search", () => {
  it("finds a name by how a word of it starts, never by the middle of a word", () => {
    assert.equal(matches("wells", "estate-wells-delivery"), true);
    assert.equal(matches("del", "estate-wells-delivery"), true);
    assert.equal(matches("WELLS del", "estate-wells-delivery"), true);
    assert.equal(matches("ells", "estate-wells-delivery"), false);
    assert.equal(matches("wells logs", "estate-wells-delivery"), false);
  });
});

describe("the figures", () => {
  it("shows compute units so a few seconds of work never read as nothing", () => {
    assert.equal(formatUnits(0), "0");
    assert.equal(formatUnits(1234.6), "1,235");
    assert.equal(formatUnits(100), "100");
    assert.equal(formatUnits(12.345), "12.35");
    assert.equal(formatUnits(1), "1.00");
    assert.equal(formatUnits(0.5), "0.50");
    assert.equal(formatUnits(0.0123), "0.012");
    assert.equal(formatUnits(0.00042), "0.00042");
    assert.equal(formatUnits(0.000001), "<0.00001");
  });

  it("shows wall-clock time in its two largest parts", () => {
    assert.equal(formatWallClock(0), "0 ms");
    assert.equal(formatWallClock(850), "850 ms");
    assert.equal(formatWallClock(4_200), "4.2 s");
    assert.equal(formatWallClock(42_000), "42 s");
    assert.equal(formatWallClock(125_000), "2 min 5 s");
    assert.equal(formatWallClock(3 * 3_600_000 + 12 * 60_000), "3 h 12 min");
    assert.equal(formatWallClock(50 * 3_600_000), "2 d 2 h");
  });

  it("shows a share of the whole, and a part too small for a decimal as such", () => {
    assert.equal(shareOf(1, 4), 25);
    assert.equal(shareOf(1, 0), null);
    assert.equal(formatShare(25), "25.0%");
    assert.equal(formatShare(0.04), "<0.1%");
    assert.equal(formatShare(0), "0.0%");
    assert.equal(formatShare(null), "-");
  });

  it("works out the cores a piece of work was charged from its slots", () => {
    assert.equal(chargedCores(8, 1, 4), 2);
    assert.equal(chargedCores(8, 4, 6), 8 * 4 / 6);
    assert.equal(chargedCores(8, 9, 6), 8);
    assert.equal(chargedCores(8, 1, 0), 0);
  });
});

describe("the download", () => {
  it("holds one line per flow and team with every figure a share is worked out from", () => {
    const csv = divisionCsv([share("Wells, north", "estate", "wells", "say \"hi\"", 3), share("", "estate", "", "f", 1, "task")]);
    assert.deepEqual(csv.split("\r\n"), [
      "team,repository,project,flow,kind,work,wall_clock_ms,wall_clock_hours,compute_units,share_percent",
      "\"Wells, north\",estate,wells,\"say \"\"hi\"\"\",run,2,10800000,3.0000,3.000000,75.000",
      ",estate,,f,task,2,3600000,1.0000,1.000000,25.000",
      "",
    ]);
  });

  it("is the header alone when nothing ran", () => {
    assert.equal(divisionCsv([]).split("\r\n").length, 2);
  });
});
