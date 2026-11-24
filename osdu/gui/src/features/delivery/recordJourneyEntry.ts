// What a record's timeline is made of (osdu/docs/reference/concepts/record-lifecycle.md, A record's timeline): an
// entry for each thing that happened to the record, on the lane where it happened, the chapters the entries are cut
// into, and the helpers every kind of entry is built with.

import type { ReactNode } from "react";
import { format } from "date-fns";
import type { LucideIcon } from "lucide-react";
import { parseUtc } from "@/lib/time";

export type Tone = "success" | "destructive" | "warning" | "info" | "muted";

/**
 * Where a step happened: in the record's row of its ingestion table, in the ledger, or in OSDU. The rail draws each on
 * a lane of its own, so the path between the lanes shows how the record moved.
 */
export type Lane = "source" | "ledger" | "osdu";

export interface Outcome {
  text: string;
  tone: Tone;
}

/** One thing that happened to the record: when, where, what, the facts that place it, and the rest. */
export interface Entry {
  id: string;
  at: string;
  lane: Lane;
  title: string;
  tone: Tone;
  /** The node's icon; null draws the initial of who asked instead. */
  icon: LucideIcon | null;
  /** Who asked, for a step someone asked for. */
  actor: string | null;
  /** How many records the request named, when that is known: one for this record alone. */
  scope: number | null;
  /** The facts that place the entry, on one line: the file and row, the run and submission, how long it took. */
  facts: ReactNode[];
  /** What the step left the record in, when that is not its title (held until released, tried again). */
  then: Outcome | null;
  /** What went wrong, when something did. */
  error: string | null;
  /** Everything else known about the step, revealed when the entry is opened. */
  more: ReactNode | null;
  /** Entries dated alike (a landing and the load it fed, a request and the try it started) keep their order by this. */
  order: number;
  /** A step that sets the record moving opens a chapter, named by this: its row arrived or changed, someone asked. */
  trigger: string | null;
  /** What this step settles its chapter as, for a step that settles something. */
  outcome: Outcome | null;
  /** A verify, whose match after a delivery settles the chapter as delivered and verified. */
  verifies: boolean;
  /** Consecutive tries that ended alike share this key and fold into one entry. */
  foldKey: string | null;
  testId: string;
}

/** A stretch of the record's story: what set it moving, what happened, and how it ended. */
export interface Chapter {
  number: number;
  trigger: string;
  entries: Entry[];
  outcome: Outcome;
}

/** How long something took, from two moments the ledger or the catalog recorded. */
export function between(from: string | null, to: string | null): string | null {
  if (from === null || to === null) {
    return null;
  }

  const ms = parseUtc(to).getTime() - parseUtc(from).getTime();
  if (!Number.isFinite(ms) || ms < 0) {
    return null;
  }

  return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(1)} s`;
}

/** The local day a moment fell on, as the timeline names days: "Fri 25 Sep". */
export function dayOf(at: string): string {
  return format(parseUtc(at), "EEE d MMM");
}

export function clockOf(at: string): string {
  return format(parseUtc(at), "HH:mm");
}

export function entryDefaults(): Pick<Entry, "actor" | "scope" | "then" | "error" | "more" | "trigger" | "outcome" | "verifies" | "foldKey"> {
  return { actor: null, scope: null, then: null, error: null, more: null, trigger: null, outcome: null, verifies: false, foldKey: null };
}
