// Compute usage (osdu/docs/reference/concepts/compute-usage.md): how much compute the nodes spent in a period, how it
// divides over teams, repositories, project folders and flows, the work behind a share, and which folder of which
// repository names which team. Same conventions as delivery.ts: one function per endpoint, pages compose them with
// TanStack Query. Every route is an admin's.

import { get } from "@/api/client";
import type { ComputeDay, ComputeShare, ComputeTotals } from "../features/delivery/compute/computeUsageModel";

/** The compute usage of a period: `from` and `to` are the UTC days asked for, both included, written yyyy-MM-dd. */
export interface ComputeUsage {
  from: string;
  to: string;
  totals: ComputeTotals;
  /** Each day that has usage, oldest first. */
  days: ComputeDay[];
  /** Each flow's share under the team it was charged to, the largest first. */
  shares: ComputeShare[];
  /** When the oldest piece of work still listed one by one ended; null when none is. */
  workKeptFromUtc: string | null;
}

/** One piece of work a node did: one attempt of a run (`workId` is the run) or one compute task. */
export interface ComputeWork {
  usageId: number;
  node: string;
  kind: "run" | "task";
  workId: string;
  /** The run's attempt; 0 for a compute task. */
  attempt: number;
  team: string;
  repo: string;
  project: string;
  flow: string;
  requestedBy: string | null;
  startedUtc: string;
  endedUtc: string;
  wallClockMs: number;
  /** The node's cores. */
  cores: number;
  /** The slots of the node the work held. */
  slots: number;
  /** The node's slots in all. */
  nodeSlots: number;
  computeUnits: number;
  /** completed, failed, cancelled, or interrupted for work the node gave up without an outcome. */
  outcome: string;
}

/** A page of work, newest first; `next` continues it, null on the last page. */
export interface ComputeWorkPage {
  rows: ComputeWork[];
  next: string | null;
}

/** Which work a list asks for. A filter left undefined is not applied; an empty one asks for the work that names none. */
export interface ComputeWorkFilter {
  from: string;
  to: string;
  team?: string;
  repo?: string;
  project?: string;
  flow?: string;
}

/** The team a folder of a repository belongs to, as the repository's last sync read it from its repository document. */
export interface RepositoryTeam {
  repoId: string;
  /** The repository's name; null once the catalog no longer holds it. */
  repo: string | null;
  /** The folder holding the document; empty for the repository's root. */
  folder: string;
  team: string;
  updatedUtc: string;
}

export const COMPUTE_USAGE_KEY = ["delivery", "compute-usage"] as const;

export const computeUsageApi = {
  /** The usage of the days from `from` to `to`, both included. */
  summary: (from: string, to: string) => get<ComputeUsage>("/api/v1/delivery/compute-usage", { from, to }),
  /** The work of a period still listed one by one, newest first, a page at a time. */
  work: (filter: ComputeWorkFilter, after: string | null, take: number) => {
    // The address is written here rather than handed over as a query: the client leaves out an empty value, and an empty
    // team, repository or project folder is a filter of its own (the work that names none).
    const query = new URLSearchParams({ from: filter.from, to: filter.to, take: String(take) });
    for (const key of ["team", "repo", "project", "flow"] as const) {
      const value = filter[key];
      if (value !== undefined) {
        query.set(key, value);
      }
    }

    if (after !== null) {
      query.set("after", after);
    }

    return get<ComputeWorkPage>(`/api/v1/delivery/compute-usage/work?${query.toString()}`);
  },
  /** Every folder that names a team, by repository then folder. */
  teams: () => get<RepositoryTeam[]>("/api/v1/delivery/compute-usage/teams"),
};
