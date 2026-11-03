import { parseUtc } from "@/lib/time";
import type { DeliveryAssertionFlow, DeliveryAssertionRun, DeliveryAssertionTest } from "../../../api/delivery";
import { counted, standingOf, type TestStanding } from "./assertionFormat";

/** What the status strip narrows the tests to: where they stand now, or what keeps them from being trusted. */
export type BoardFilter = TestStanding | "problems";

/** The orders a board lists its sets and flows in: what needs a look first, by name, or by the newest run. */
export type BoardSort = "status" | "name" | "run";

export const BOARD_SORTS: readonly BoardSort[] = ["status", "name", "run"];

const FILTERS: readonly string[] = ["failed", "errored", "warned", "notRun", "disabled", "passed", "problems"];

/** The strip filter a link names (`?status=`), or none for a value that is not one. */
export function boardFilterOf(value: string | null): BoardFilter | null {
  return value !== null && FILTERS.includes(value) ? value as BoardFilter : null;
}

/** The order a link names (`?sort=`), or what needs a look first. */
export function boardSortOf(value: string | null): BoardSort {
  return (BOARD_SORTS as readonly string[]).includes(value ?? "") ? value as BoardSort : "status";
}

/**
 * What a flow is on a board: one whose tests run in the board's partition (`tested`), one that declares no test, one
 * that tests other partitions (`elsewhere`, nothing to fix), or one whose document the catalog cannot parse (`broken`).
 */
export type FlowState = "tested" | "empty" | "elsewhere" | "broken";

export function flowState(flow: DeliveryAssertionFlow): FlowState {
  if (flow.problem !== null) {
    return flow.parses ? "elsewhere" : "broken";
  }

  return flow.tests.length === 0 ? "empty" : "tested";
}

/**
 * How tests stand, counted as the board's totals count them: over the tests that run in the partition, each by where it
 * stands now, with the tests that do not fit their schema, and those changed since their last result (counted as not run),
 * counted beside.
 */
export interface FlowTally {
  tests: number;
  failed: number;
  errored: number;
  warned: number;
  notRun: number;
  disabled: number;
  passed: number;
  unfit: number;
  changed: number;
  /** Tests that name other partitions, which the counts leave out. */
  elsewhere: number;
}

const NO_TALLY: FlowTally = { tests: 0, failed: 0, errored: 0, warned: 0, notRun: 0, disabled: 0, passed: 0, unfit: 0, changed: 0, elsewhere: 0 };

export function tallyOf(flow: DeliveryAssertionFlow): FlowTally {
  const tally: FlowTally = { ...NO_TALLY };
  for (const test of flow.tests) {
    if (!test.runsHere) {
      tally.elsewhere++;
      continue;
    }

    tally.tests++;
    const standing = standingOf(test);
    if (standing === "failed" || standing === "errored" || standing === "warned" || standing === "notRun" || standing === "disabled" || standing === "passed") {
      tally[standing]++;
    }

    if (test.problems.length > 0) {
      tally.unfit++;
    }

    if (test.changed && standing === "notRun") {
      tally.changed++;
    }
  }

  return tally;
}

/** The tallies of several flows added up: a set's. */
export function sumTallies(tallies: readonly FlowTally[]): FlowTally {
  return tallies.reduce<FlowTally>((sum, tally) => ({
    tests: sum.tests + tally.tests,
    failed: sum.failed + tally.failed,
    errored: sum.errored + tally.errored,
    warned: sum.warned + tally.warned,
    notRun: sum.notRun + tally.notRun,
    disabled: sum.disabled + tally.disabled,
    passed: sum.passed + tally.passed,
    unfit: sum.unfit + tally.unfit,
    changed: sum.changed + tally.changed,
    elsewhere: sum.elsewhere + tally.elsewhere,
  }), { ...NO_TALLY });
}

/** A tally in words, worst first, as a tooltip or an accessible name says it: "3 failed, 9 passed, 2 disabled". */
export function tallyText(tally: FlowTally): string {
  if (tally.tests === 0) {
    return tally.elsewhere > 0 ? `${counted(tally.elsewhere, "assertion")} of other partitions` : "no assertion";
  }

  return [
    tally.failed > 0 ? `${tally.failed} failed` : null,
    tally.errored > 0 ? `${tally.errored} errored` : null,
    tally.warned > 0 ? `${tally.warned} warned` : null,
    tally.notRun > 0
      ? `${tally.notRun} not run${tally.changed > 0 ? ` (${tally.changed} of them changed since ${tally.changed === 1 ? "its" : "their"} last result)` : ""}`
      : null,
    tally.passed > 0 ? `${tally.passed} passed` : null,
    tally.disabled > 0 ? `${tally.disabled} disabled` : null,
    tally.unfit > 0 ? `${tally.unfit} ${tally.unfit === 1 ? "does" : "do"} not fit ${tally.unfit === 1 ? "its" : "their"} schema` : null,
    tally.elsewhere > 0 ? `${tally.elsewhere} of other partitions` : null,
  ].filter((part): part is string => part !== null).join(", ");
}

/**
 * Where a flow comes in the order of what needs a look: a failed test first, then an errored one or a document that does
 * not parse, a warning, a test that does not fit its schema, a test not run as it is now, then a flow whose every test
 * passed (or is disabled), one that declares none, and last a flow of other partitions, which needs nothing here. Within a
 * rank, the flow holding more of what put it there comes first.
 */
export function flowRank(state: FlowState, tally: FlowTally): number {
  switch (state) {
    case "broken":
      return 1;
    case "empty":
      return 6;
    case "elsewhere":
      return 7;
    default:
      return tally.failed > 0 ? 0
        : tally.errored > 0 ? 1
          : tally.warned > 0 ? 2
            : tally.unfit > 0 ? 3
              : tally.notRun > 0 ? 4
                : tally.tests > 0 ? 5 : 6;
  }
}

/** How much of what puts a tally at its rank it holds: its failed tests at the first rank, its errored ones at the second, and so on. */
function weightOf(rank: number, tally: FlowTally): number {
  switch (rank) {
    case 0:
      return tally.failed;
    case 1:
      return tally.errored;
    case 2:
      return tally.warned;
    case 3:
      return tally.unfit;
    case 4:
      return tally.notRun;
    default:
      return 0;
  }
}

/** Where a test comes in its flow: what needs a look first, a test that does not fit its schema before one not yet run. */
function testRank(test: DeliveryAssertionTest): number {
  const standing = standingOf(test);
  if (standing === "notRun" && test.problems.length > 0) {
    return 3;
  }

  const order: Partial<Record<TestStanding, number>> = { failed: 0, errored: 1, warned: 2, notRun: 4, passed: 5, disabled: 6, skipped: 7, elsewhere: 8 };
  return order[standing] ?? 7;
}

/** A flow's tests in the order they are listed: what needs a look first, then as the document declares them. */
export function orderedTests(tests: readonly DeliveryAssertionTest[]): DeliveryAssertionTest[] {
  return tests
    .map((test, order) => ({ test, order, rank: testRank(test) }))
    .sort((a, b) => a.rank - b.rank || a.order - b.order)
    .map((entry) => entry.test);
}

/** What a board is narrowed by: a search term (lower case, trimmed), a strip filter, and the types and tags picked. */
export interface BoardCriteria {
  term: string;
  filter: BoardFilter | null;
  types: ReadonlySet<string>;
  tags: ReadonlySet<string>;
}

/** Whether any criterion narrows the tests themselves, rather than the term naming sets or flows. */
function facetsOn(criteria: BoardCriteria): boolean {
  return criteria.filter !== null || criteria.types.size > 0 || criteria.tags.size > 0;
}

export function narrowing(criteria: BoardCriteria): boolean {
  return criteria.term !== "" || facetsOn(criteria);
}

function passesFacets(test: DeliveryAssertionTest, criteria: BoardCriteria): boolean {
  if (criteria.types.size > 0 && !criteria.types.has(test.kind)) {
    return false;
  }

  if (criteria.tags.size > 0 && !test.tags.some((tag) => criteria.tags.has(tag))) {
    return false;
  }

  const filter = criteria.filter;
  if (filter === null) {
    return true;
  }

  return filter === "problems" ? test.problems.length > 0 : standingOf(test) === filter;
}

function testMatches(test: DeliveryAssertionTest, term: string): boolean {
  return [test.name, test.description ?? "", test.kind, test.query ?? "", ...test.tags, ...test.assertions.map((a) => a.label)]
    .some((text) => text.toLowerCase().includes(term));
}

/** One flow as the board lists it: its state and tally, and the tests the criteria leave in view, in their order. */
export interface BoardEntry {
  flow: DeliveryAssertionFlow;
  state: FlowState;
  tally: FlowTally;
  rank: number;
  tests: DeliveryAssertionTest[];
  /**
   * Whether the criteria narrowed its tests (a strip filter, a type or a tag, or a term found among its tests), so the flow
   * opens on what was asked for; a flow listed in full, or found by its own name or its set's, stays closed until it is opened.
   */
  narrowed: boolean;
}

/** The key of the group of flows that name no set (and no batch). */
export const NO_SET = "";

/** One set as the board lists it: its flows the criteria leave in view, in their order, what they add up to, and its newest run. */
export interface SetEntry {
  /** The set's name, or {@link NO_SET} for the flows that name none. */
  key: string;
  set: string | null;
  entries: BoardEntry[];
  /** Every flow of the set on the board, whatever the criteria leave in view. */
  flows: DeliveryAssertionFlow[];
  tally: FlowTally;
  rank: number;
  /** The newest run among its flows: when the set was last checked. */
  lastRun: DeliveryAssertionRun | null;
  /** Whether the criteria narrowed what it lists, so it opens on what was asked for. */
  narrowed: boolean;
}

function runTime(run: DeliveryAssertionRun | null): number {
  return run === null ? Number.NEGATIVE_INFINITY : parseUtc(run.completedUtc ?? run.startedUtc).getTime();
}

function newestRun(flows: readonly DeliveryAssertionFlow[]): DeliveryAssertionRun | null {
  return flows.reduce<DeliveryAssertionRun | null>((newest, flow) => (runTime(flow.lastRun) > runTime(newest) ? flow.lastRun : newest), null);
}

/** The order of a sort over sets or flows: rank and weight for what needs a look, the newest run first, or the name. */
function comparer<T>(sort: BoardSort, name: (item: T) => string, run: (item: T) => DeliveryAssertionRun | null, rank: (item: T) => number, tally: (item: T) => FlowTally) {
  const byName = (a: T, b: T) => name(a).localeCompare(name(b));
  return sort === "name"
    ? byName
    : sort === "run"
      ? (a: T, b: T) => runTime(run(b)) - runTime(run(a)) || byName(a, b)
      : (a: T, b: T) => rank(a) - rank(b) || weightOf(rank(b), tally(b)) - weightOf(rank(a), tally(a)) || byName(a, b);
}

/**
 * The flows a board lists under its criteria, in the order asked for. A term finds a flow by its name, its description or
 * its set's name, listing every test of it the other criteria keep, and a test by its name, description, type, query, tags
 * or checks; a flow none of whose tests the criteria keep is left out, unless the term names it and nothing else narrows the
 * tests.
 */
export function boardEntries(flows: readonly DeliveryAssertionFlow[], criteria: BoardCriteria, sort: BoardSort): BoardEntry[] {
  const term = criteria.term;
  const facets = facetsOn(criteria);
  const entries: BoardEntry[] = [];
  for (const flow of flows) {
    const byName = term !== "" && [flow.name, flow.description ?? "", flow.set ?? ""].some((text) => text.toLowerCase().includes(term));
    const tests = flow.tests.filter((test) => passesFacets(test, criteria) && (term === "" || byName || testMatches(test, term)));
    const listed = (term === "" && !facets) || tests.length > 0 || (byName && !facets);
    if (!listed) {
      continue;
    }

    const state = flowState(flow);
    const tally = tallyOf(flow);
    entries.push({ flow, state, tally, rank: flowRank(state, tally), tests: orderedTests(tests), narrowed: facets || (term !== "" && !byName) });
  }

  return entries.sort(comparer<BoardEntry>(sort, (e) => e.flow.name, (e) => e.flow.lastRun, (e) => e.rank, (e) => e.tally));
}

/**
 * The sets a board lists under its criteria: the flows {@link boardEntries} keeps, grouped by the set each names, every set
 * in the order asked for, and the flows that name none last. A set's tally adds up every flow of it on the board, so its line
 * says how the set stands whatever the criteria list of it.
 */
export function boardSets(flows: readonly DeliveryAssertionFlow[], criteria: BoardCriteria, sort: BoardSort): SetEntry[] {
  const all = new Map<string, DeliveryAssertionFlow[]>();
  for (const flow of flows) {
    const key = flow.set ?? NO_SET;
    all.set(key, [...(all.get(key) ?? []), flow]);
  }

  const kept = new Map<string, BoardEntry[]>();
  for (const entry of boardEntries(flows, criteria, sort)) {
    const key = entry.flow.set ?? NO_SET;
    kept.set(key, [...(kept.get(key) ?? []), entry]);
  }

  const sets: SetEntry[] = [...kept.entries()].map(([key, entries]) => {
    const members = all.get(key) ?? [];
    const tally = sumTallies(members.map(tallyOf));
    const rank = Math.min(...members.map((flow) => flowRank(flowState(flow), tallyOf(flow))));
    return { key, set: key === NO_SET ? null : key, entries, flows: members, tally, rank, lastRun: newestRun(members), narrowed: entries.some((e) => e.narrowed) };
  });
  const order = comparer<SetEntry>(sort, (s) => s.key, (s) => s.lastRun, (s) => s.rank, (s) => s.tally);
  return sets.sort((a, b) => (a.set === null ? 1 : 0) - (b.set === null ? 1 : 0) || order(a, b));
}

/** The distinct values of the tests' types or tags, each with how many tests carry it, in name order. */
export function facetCounts(flows: readonly DeliveryAssertionFlow[], of: (test: DeliveryAssertionTest) => readonly string[]): [string, number][] {
  const counts = new Map<string, number>();
  for (const test of flows.flatMap((flow) => flow.tests)) {
    for (const value of of(test)) {
      counts.set(value, (counts.get(value) ?? 0) + 1);
    }
  }

  return [...counts.entries()].sort((a, b) => a[0].localeCompare(b[0]));
}
