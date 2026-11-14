// What the Compute usage page works out from the control plane's answer (osdu/docs/reference/concepts/compute-usage.md):
// the period a choice stands for, the days of a period, the division of the usage as a tree of teams, repositories,
// project folders and flows, the words and figures it is shown in, and the file it downloads as. No React and no
// network here, so every rule is checked on its own.

import { parseUtc } from "../../../../../../sqlflow/gui/src/lib/time.ts";

/** A sum of compute usage: pieces of work, their wall-clock milliseconds and their compute units. */
export interface ComputeTotals {
  work: number;
  wallClockMs: number;
  computeUnits: number;
}

/** One UTC day's usage; `day` is yyyy-MM-dd. */
export interface ComputeDay extends ComputeTotals {
  day: string;
}

/** One flow's usage under the team it was charged to. An empty team, repository or project stands for none. */
export interface ComputeShare extends ComputeTotals {
  team: string;
  repo: string;
  project: string;
  flow: string;
  kind: "run" | "task";
}

/** The periods the page offers, each named by what it covers. */
export const PERIODS = [
  { id: "7d", label: "Last 7 days" },
  { id: "30d", label: "Last 30 days" },
  { id: "90d", label: "Last 90 days" },
  { id: "month", label: "This month" },
  { id: "last-month", label: "Last month" },
  { id: "365d", label: "Last 365 days" },
] as const;

export type PeriodId = (typeof PERIODS)[number]["id"];

/** The period the page opens on. */
export const DEFAULT_PERIOD: PeriodId = "30d";

/** The period an address names, or the default when it names none the page offers. */
export function periodOf(value: string | null): PeriodId {
  return PERIODS.find((period) => period.id === value)?.id ?? DEFAULT_PERIOD;
}

const dayText = (date: Date) => date.toISOString().slice(0, 10);

/** The first and last UTC day of a period that is asked for at `now`, both included, written yyyy-MM-dd. */
export function periodRange(period: PeriodId, now: Date): { from: string; to: string } {
  const today = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()));
  const daysBack = (days: number) => new Date(today.getTime() - (days - 1) * 86_400_000);
  switch (period) {
    case "7d":
      return { from: dayText(daysBack(7)), to: dayText(today) };
    case "90d":
      return { from: dayText(daysBack(90)), to: dayText(today) };
    case "365d":
      return { from: dayText(daysBack(365)), to: dayText(today) };
    case "month":
      return { from: dayText(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), 1))), to: dayText(today) };
    case "last-month":
      return {
        from: dayText(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth() - 1, 1))),
        to: dayText(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), 0))),
      };
    default:
      return { from: dayText(daysBack(30)), to: dayText(today) };
  }
}

/** Every day of a period, oldest first, each with its usage or none: what the chart draws a bar for. */
export function daysOf(from: string, to: string, days: readonly ComputeDay[]): ComputeDay[] {
  const have = new Map(days.map((day) => [day.day, day]));
  const all: ComputeDay[] = [];
  const last = parseUtc(`${to}T00:00:00`).getTime();
  for (let at = parseUtc(`${from}T00:00:00`).getTime(); at <= last; at += 86_400_000) {
    const day = dayText(new Date(at));
    all.push(have.get(day) ?? { day, work: 0, wallClockMs: 0, computeUnits: 0 });
  }

  return all;
}

/** What a level of the division is: a team, a repository in it, a project folder in that, or a flow. */
export type DivisionLevel = "team" | "repo" | "project" | "flow";

/** Which work a node of the division stands for: the values the work list is asked with. */
export interface DivisionFilter {
  team: string;
  repo?: string;
  project?: string;
  flow?: string;
}

/** A node of the division: its usage is the sum of everything under it. */
export interface DivisionNode extends ComputeTotals {
  id: string;
  level: DivisionLevel;
  /** The value as the usage holds it; empty for none. */
  name: string;
  /** The value as the page shows it. */
  label: string;
  /** True when `name` is empty: the label says what is missing rather than naming anything. */
  unnamed: boolean;
  /** For a flow: whether it is runs of a flow or compute tasks of an operation. */
  kind?: "run" | "task";
  filter: DivisionFilter;
  children: DivisionNode[];
}

const UNNAMED: Record<DivisionLevel, string> = {
  team: "No team",
  repo: "No repository",
  project: "Repository root",
  flow: "Unnamed flow",
};

/** A level's value as the page shows it: the value, or what it means that there is none. */
export function labelOf(level: DivisionLevel, name: string): string {
  return name === "" ? UNNAMED[level] : name;
}

/**
 * Whether `text` is found by `term`: every word of the term starts a word of the text, whatever the case. A name is
 * found by how it starts or by a word inside it ("wells" finds "estate-wells-delivery"), never by the middle of a word.
 */
export function matches(term: string, text: string): boolean {
  const words = text.toLowerCase().split(/[^a-z0-9]+/).filter((word) => word !== "");
  return term.toLowerCase().split(/[^a-z0-9]+/).filter((word) => word !== "")
    .every((asked) => words.some((word) => word.startsWith(asked)));
}

const byUsage = (a: DivisionNode, b: DivisionNode) =>
  b.computeUnits - a.computeUnits || b.wallClockMs - a.wallClockMs || a.label.localeCompare(b.label);

/**
 * The division of a period's usage: teams, each with its repositories, their project folders and their flows, the
 * largest first at every level. With a search term, only the shares a word of it finds, by team, repository, project
 * folder or flow, and the sums are of those.
 */
export function divisionOf(shares: readonly ComputeShare[], term = ""): DivisionNode[] {
  const asked = term.trim();
  const kept = asked === ""
    ? shares
    : shares.filter((share) => matches(asked, `${labelOf("team", share.team)} ${share.repo} ${share.project} ${share.flow}`));

  const roots = new Map<string, DivisionNode>();
  const child = (siblings: Map<string, DivisionNode> | DivisionNode[], key: string, make: () => DivisionNode): DivisionNode => {
    if (siblings instanceof Map) {
      let node = siblings.get(key);
      if (node === undefined) {
        node = make();
        siblings.set(key, node);
      }

      return node;
    }

    let node = siblings.find((candidate) => candidate.id === key);
    if (node === undefined) {
      node = make();
      siblings.push(node);
    }

    return node;
  };
  const node = (id: string, level: DivisionLevel, name: string, filter: DivisionFilter, kind?: "run" | "task"): DivisionNode => ({
    id, level, name, label: labelOf(level, name), unnamed: name === "", kind, filter, children: [], work: 0, wallClockMs: 0, computeUnits: 0,
  });
  const add = (to: DivisionNode, share: ComputeShare) => {
    to.work += share.work;
    to.wallClockMs += share.wallClockMs;
    to.computeUnits += share.computeUnits;
  };

  for (const share of kept) {
    // Ids are JSON so a name holding any separator still gives its own node.
    const teamId = JSON.stringify([share.team]);
    const repoId = JSON.stringify([share.team, share.repo]);
    const projectId = JSON.stringify([share.team, share.repo, share.project]);
    const flowId = JSON.stringify([share.team, share.repo, share.project, share.flow, share.kind]);
    const team = child(roots, teamId, () => node(teamId, "team", share.team, { team: share.team }));
    const repo = child(team.children, repoId, () => node(repoId, "repo", share.repo, { team: share.team, repo: share.repo }));
    const project = child(repo.children, projectId, () =>
      node(projectId, "project", share.project, { team: share.team, repo: share.repo, project: share.project }));
    const flow = child(project.children, flowId, () =>
      node(flowId, "flow", share.flow, { team: share.team, repo: share.repo, project: share.project, flow: share.flow }, share.kind));
    for (const level of [team, repo, project, flow]) {
      add(level, share);
    }
  }

  const sorted = (nodes: DivisionNode[]): DivisionNode[] => {
    nodes.sort(byUsage);
    for (const each of nodes) {
      sorted(each.children);
    }

    return nodes;
  };

  return sorted([...roots.values()]);
}

/** The ids of every node of a division that has children: what a search opens so its finds show. */
export function branchIds(nodes: readonly DivisionNode[]): string[] {
  return nodes.flatMap((node) => (node.children.length === 0 ? [] : [node.id, ...branchIds(node.children)]));
}

/** The sum of a division's top level. */
export function totalOf(nodes: readonly ComputeTotals[]): ComputeTotals {
  return nodes.reduce<ComputeTotals>(
    (sum, node) => ({ work: sum.work + node.work, wallClockMs: sum.wallClockMs + node.wallClockMs, computeUnits: sum.computeUnits + node.computeUnits }),
    { work: 0, wallClockMs: 0, computeUnits: 0 });
}

/** The compute units charged to no team, of the shares given. */
export function unassignedUnits(shares: readonly ComputeShare[]): number {
  return shares.reduce((sum, share) => (share.team === "" ? sum + share.computeUnits : sum), 0);
}

/**
 * Compute units as the page shows them: whole from a hundred up, two decimals from one up, and below one as many as it
 * takes to show two figures that are not zero, so a few seconds of work never reads as nothing.
 */
export function formatUnits(units: number): string {
  if (units === 0) {
    return "0";
  }

  if (units >= 100) {
    return Math.round(units).toLocaleString("en-US");
  }

  if (units >= 1) {
    return units.toFixed(2);
  }

  if (units < 0.00001) {
    return "<0.00001";
  }

  const decimals = Math.min(5, Math.max(2, 1 - Math.floor(Math.log10(units))));
  return units.toFixed(decimals);
}

/** Wall-clock time as the page shows it: milliseconds, seconds, minutes, hours or days, in the two largest parts. */
export function formatWallClock(ms: number): string {
  if (ms < 1000) {
    return `${Math.round(ms)} ms`;
  }

  const seconds = ms / 1000;
  if (seconds < 60) {
    return `${seconds.toFixed(seconds < 10 ? 1 : 0)} s`;
  }

  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) {
    return `${minutes} min ${Math.floor(seconds % 60)} s`;
  }

  const hours = Math.floor(minutes / 60);
  if (hours < 48) {
    return `${hours} h ${minutes % 60} min`;
  }

  return `${Math.floor(hours / 24)} d ${hours % 24} h`;
}

/** A part of a whole as a percentage, or null when the whole is nothing. */
export function shareOf(part: number, whole: number): number | null {
  return whole > 0 ? (part / whole) * 100 : null;
}

/** A percentage as the page shows it: one decimal, and a part too small for one decimal said as such rather than as 0. */
export function formatShare(percent: number | null): string {
  if (percent === null) {
    return "-";
  }

  if (percent > 0 && percent < 0.1) {
    return "<0.1%";
  }

  return `${percent.toFixed(1)}%`;
}

/** The cores a piece of work was charged: the share of the node's cores its slots stand for. */
export function chargedCores(cores: number, slots: number, nodeSlots: number): number {
  return nodeSlots > 0 ? (cores * Math.min(slots, nodeSlots)) / nodeSlots : 0;
}

const csvCell = (value: string | number) => {
  const text = String(value);
  return /[",\r\n]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text;
};

/**
 * The division as a CSV file, one line per flow and team with every figure a share is worked out from: what the page
 * shows, to divide a cost by. The values are as the usage holds them, so a line with no team has an empty team.
 */
export function divisionCsv(shares: readonly ComputeShare[]): string {
  const total = shares.reduce((sum, share) => sum + share.computeUnits, 0);
  const lines = [["team", "repository", "project", "flow", "kind", "work", "wall_clock_ms", "wall_clock_hours", "compute_units", "share_percent"].join(",")];
  for (const share of shares) {
    const percent = shareOf(share.computeUnits, total);
    lines.push([
      share.team, share.repo, share.project, share.flow, share.kind, share.work, share.wallClockMs,
      (share.wallClockMs / 3_600_000).toFixed(4), share.computeUnits.toFixed(6), percent === null ? "" : percent.toFixed(3),
    ].map(csvCell).join(","));
  }

  return `${lines.join("\r\n")}\r\n`;
}
