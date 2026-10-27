import { parseUtc } from "@/lib/time";
import type { DeliveryAssertionFlow, DeliveryAssertionTest } from "../../../api/delivery";
import { counted, standingOf, type TestStanding } from "./assertionFormat";

/** What the status strip narrows the tests to: where they stand, or what keeps them from being trusted. */
export type BoardFilter = TestStanding | "problems" | "changed";

/** The orders a board lists its flows in: what needs a look first, by name, or by the newest run. */
export type BoardSort = "status" | "name" | "run";

export const BOARD_SORTS: readonly BoardSort[] = ["status", "name", "run"];

const FILTERS: readonly string[] = ["failed", "errored", "warned", "notRun", "passed", "problems", "changed"];

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
 * How a flow's tests stand, counted as the board's totals count them: over the tests that run in the partition, each by
 * its latest result, with the tests that do not fit their schema and those changed since their result counted beside.
 */
export interface FlowTally {
  tests: number;
  failed: number;
  errored: number;
  warned: number;
  notRun: number;
  passed: number;
  unfit: number;
  changed: number;
  /** Tests that name other partitions, which the counts leave out. */
  elsewhere: number;
}

export function tallyOf(flow: DeliveryAssertionFlow): FlowTally {
  const tally: FlowTally = { tests: 0, failed: 0, errored: 0, warned: 0, notRun: 0, passed: 0, unfit: 0, changed: 0, elsewhere: 0 };
  for (const test of flow.tests) {
    if (!test.runsHere) {
      tally.elsewhere++;
      continue;
    }

    tally.tests++;
    const standing = standingOf(test);
    if (standing === "failed" || standing === "errored" || standing === "warned" || standing === "notRun" || standing === "passed") {
      tally[standing]++;
    }

    if (test.problems.length > 0) {
      tally.unfit++;
    }

    if (test.changed) {
      tally.changed++;
    }
  }

  return tally;
}

/** A tally in words, worst first, as a tooltip or an accessible name says it: "3 failed, 9 passed, 2 do not fit". */
export function tallyText(tally: FlowTally): string {
  if (tally.tests === 0) {
    return tally.elsewhere > 0 ? `${counted(tally.elsewhere, "assertion")} of other partitions` : "no assertion";
  }

  return [
    tally.failed > 0 ? `${tally.failed} failed` : null,
    tally.errored > 0 ? `${tally.errored} errored` : null,
    tally.warned > 0 ? `${tally.warned} warned` : null,
    tally.notRun > 0 ? `${tally.notRun} not run` : null,
    tally.passed > 0 ? `${tally.passed} passed` : null,
    tally.unfit > 0 ? `${tally.unfit} ${tally.unfit === 1 ? "does" : "do"} not fit ${tally.unfit === 1 ? "its" : "their"} schema` : null,
    tally.changed > 0 ? `${tally.changed} changed since ${tally.changed === 1 ? "its" : "their"} result` : null,
    tally.elsewhere > 0 ? `${tally.elsewhere} of other partitions` : null,
  ].filter((part): part is string => part !== null).join(", ");
}

/**
 * Where a flow comes in the order of what needs a look: a failed test first, then an errored one or a document that does
 * not parse, a warning, a test that does not fit its schema, a test not run or changed since its result, then a flow whose
 * every test passed, one that declares none, and last a flow of other partitions, which needs nothing here. Within a rank,
 * the flow holding more of what put it there comes first.
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
              : tally.notRun > 0 || tally.changed > 0 ? 4
                : tally.tests > 0 ? 5 : 6;
  }
}

/**
 * How much of what puts a flow at its rank it holds, so of two flows with failed tests the one with more comes first: its
 * failed tests at the first rank, its errored ones at the second, and so on.
 */
function flowWeight(entry: Pick<BoardEntry, "rank" | "tally">): number {
  const tally = entry.tally;
  switch (entry.rank) {
    case 0:
      return tally.failed;
    case 1:
      return tally.errored;
    case 2:
      return tally.warned;
    case 3:
      return tally.unfit;
    case 4:
      return tally.notRun + tally.changed;
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

  const order: Partial<Record<TestStanding, number>> = { failed: 0, errored: 1, warned: 2, notRun: 4, passed: 5, skipped: 6, elsewhere: 7 };
  return order[standing] ?? 6;
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

/** Whether any criterion narrows the tests themselves, rather than the term naming flows. */
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

  if (filter === "problems") {
    return test.problems.length > 0;
  }

  return filter === "changed" ? test.changed : standingOf(test) === filter;
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
   * opens on what was asked for; a flow listed in full, or found by its own name, stays closed until it is opened.
   */
  narrowed: boolean;
}

function lastRunTime(flow: DeliveryAssertionFlow): number {
  return flow.lastRun === null ? Number.NEGATIVE_INFINITY : parseUtc(flow.lastRun.startedUtc).getTime();
}

/**
 * The flows a board lists under its criteria, in the order asked for. A term finds a flow by its name or description,
 * listing every test of it the other criteria keep, and a test by its name, description, type, query, tags or checks;
 * a flow none of whose tests the criteria keep is left out, unless the term names it and nothing else narrows the tests.
 */
export function boardEntries(flows: readonly DeliveryAssertionFlow[], criteria: BoardCriteria, sort: BoardSort): BoardEntry[] {
  const term = criteria.term;
  const facets = facetsOn(criteria);
  const entries: BoardEntry[] = [];
  for (const flow of flows) {
    const byName = term !== "" && [flow.name, flow.description ?? ""].some((text) => text.toLowerCase().includes(term));
    const tests = flow.tests.filter((test) => passesFacets(test, criteria) && (term === "" || byName || testMatches(test, term)));
    const listed = (term === "" && !facets) || tests.length > 0 || (byName && !facets);
    if (!listed) {
      continue;
    }

    const state = flowState(flow);
    const tally = tallyOf(flow);
    entries.push({ flow, state, tally, rank: flowRank(state, tally), tests: orderedTests(tests), narrowed: facets || (term !== "" && !byName) });
  }

  const byName = (a: BoardEntry, b: BoardEntry) => a.flow.name.localeCompare(b.flow.name);
  return entries.sort(sort === "name"
    ? byName
    : sort === "run"
      ? (a, b) => lastRunTime(b.flow) - lastRunTime(a.flow) || byName(a, b)
      : (a, b) => a.rank - b.rank || flowWeight(b) - flowWeight(a) || byName(a, b));
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
