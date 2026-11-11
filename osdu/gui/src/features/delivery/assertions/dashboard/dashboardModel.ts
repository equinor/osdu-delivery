// The assertion dashboard's model (osdu/docs/reference/flow/assertion-sets.md, The dashboard): where every assertion stands
// now, the verdict, the map of the data landscape by two dimensions, and the groups the list shows. Pure functions of the
// board the control plane serves, with no import of the workbench, so the dashboard the GUI shows and the one its download
// carries are computed by the same code, and the unit tests run it as it is.

import type { StagedFacts } from "../assertionReading.ts";
import { briefOf, type BriefFacts, type CheckBrief, type Measure } from "./checkBrief.ts";

// What the model reads of the control plane's answers, by shape: the board (DeliveryAssertionBoard) and a test's result
// whole (DeliveryTestResult) fit these as they are.

/** A check of an assertion as its flow document states it. */
export interface BoardCheck {
  label: string;
  expected: string;
  disabled?: string | null;
}

/** A test's latest result, without its detail. */
export interface BoardLatest {
  resultId: number;
  assertionRunId: number;
  outcome: "passed" | "failed" | "warned" | "errored" | "skipped";
  matched: number | null;
  failedAssertions: number;
  error: string | null;
  completedUtc: string;
}

/** One test on the board. */
export interface BoardTest {
  name: string;
  title: string | null;
  description: string | null;
  kind: string;
  query: string | null;
  template: string | null;
  runsHere: boolean;
  disabled?: string | null;
  asOf?: string | null;
  validUntil?: string | null;
  needsNewProofValues?: boolean;
  problems: string[];
  latest: BoardLatest | null;
  changed: boolean;
  assertions: BoardCheck[];
  dimensions: { name: string; value: string }[];
}

/** One flow on the board. */
export interface BoardFlow {
  pipelineId: string;
  repoId: string;
  name: string;
  title: string | null;
  description: string | null;
  set: string | null;
  partitions: string[];
  problem: string | null;
  lastRun: { assertionRunId: number; startedUtc: string; completedUtc: string | null } | null;
  tests: BoardTest[];
}

/** The board of a partition: every assertion flow, with its tests. */
export interface Board {
  partition: string | null;
  flows: BoardFlow[];
}

/** A check's outcome in a result, with what it keeps of the staged rows it read, when it read any. */
export interface CheckOutcome extends BriefFacts, StagedFacts {
  label: string;
  examples?: { id?: string | null; value?: string | null; reason: string; key?: string[] | null }[];
}

/** A test's result whole. */
export interface ResultFacts {
  test: string;
  error?: string | null;
  notes?: string[];
  assertions?: CheckOutcome[];
}

/**
 * Where an assertion stands now, as its document states it: an outcome of its latest result that judged it as it is, not
 * yet checked as it is, or disabled by its document.
 */
export type Standing = "failed" | "errored" | "warned" | "notRun" | "passed" | "disabled";

/** The standings in the order the dashboard weighs them: what needs a look first. */
export const STANDINGS: readonly Standing[] = ["failed", "errored", "warned", "notRun", "passed", "disabled"];

/** A standing as people read it. */
export const STANDING_WORDS: Record<Standing, string> = {
  failed: "Failing",
  errored: "Could not be checked",
  warned: "Warning",
  notRun: "Not yet checked",
  passed: "Passing",
  disabled: "Disabled",
};

/** What each standing means, in one line, as the dashboard's legend says it. */
export const STANDING_MEANINGS: Record<Standing, string> = {
  failed: "OSDU holds something other than the source states.",
  errored: "the assertion could not find out.",
  warned: "close: within its warning band, or its values need restating.",
  notRun: "no result for the assertion as it is written now.",
  passed: "every check holds.",
  disabled: "switched off in its flow, with the reason given there.",
};

/** One flow the dashboard shows: what runs it, and what keeps it from running here. */
export interface DashFlow {
  pipelineId: string;
  repoId: string;
  name: string;
  title: string;
  description: string | null;
  set: string | null;
  partitions: string[];
  problem: string | null;
  lastRunId: number | null;
  lastCheckedUtc: string | null;
}

/** One assertion the dashboard shows: what it is about, where it stands, and what its latest result is. */
export interface DashTest {
  /** The flow's pipeline id and the test's name: the assertion's identity on the dashboard. */
  key: string;
  pipelineId: string;
  flow: string;
  name: string;
  title: string;
  description: string | null;
  set: string | null;
  kind: string;
  dimensions: { name: string; value: string }[];
  standing: Standing;
  /** Why it stands where it does when no result says it: the reason it is disabled, or why it is not checked. */
  reason: string | null;
  /** The id of the latest result it stands at, which opens it whole; null when it stands at none. */
  resultId: number | null;
  /** The report that result is from; null when it stands at none. */
  reportId: number | null;
  checkedUtc: string | null;
  matched: number | null;
  checks: number;
  failedChecks: number;
  /** The checks as the document states them, which a passing assertion shows when its result is not at hand. */
  definitions: { label: string; expected: string }[];
  query: string | null;
  template: string | null;
  statedAsOf: string | null;
  validUntil: string | null;
}

/** Everything the dashboard shows, as the GUI reads it or a download carries it. */
export interface DashData {
  partition: string | null;
  generatedUtc: string;
  flows: DashFlow[];
  tests: DashTest[];
}

/** The key an assertion is known by on the dashboard. */
export function testKey(pipelineId: string, name: string): string {
  return `${pipelineId}\n${name}`;
}

/** Why an assertion's document switches it off, or null when it runs: the test's reason, or every check's. */
function disabledReason(test: BoardTest): string | null {
  if (test.disabled) {
    return test.disabled;
  }

  const checks = test.assertions;
  return checks.length > 0 && checks.every((check) => check.disabled) ? `Every check is disabled: ${checks[0]!.disabled}` : null;
}

/**
 * Where a board's test stands now: disabled by its document; could not be checked when it does not fit the schema of its
 * type; not yet checked when no result judged it as it is now; else its latest outcome, a passing one whose proof values are
 * past their day being a warning that they need restating.
 */
export function standingOf(test: BoardTest): { standing: Standing; reason: string | null } {
  const disabled = disabledReason(test);
  if (disabled !== null) {
    return { standing: "disabled", reason: disabled };
  }

  const latest = test.latest !== null && !test.changed ? test.latest : null;
  const outcome = latest === null ? null : latest.outcome;
  if (latest === null || outcome === null || outcome === "skipped") {
    if (test.problems.length > 0) {
      return { standing: "errored", reason: `It does not fit the schema of its type: ${test.problems.join(" ")}` };
    }

    return {
      standing: "notRun",
      reason: outcome === "skipped"
        ? `Its latest run, report ${latest!.assertionRunId}, skipped it${latest!.error ? `: ${latest!.error}` : "."}`
        : test.changed && test.latest !== null
          ? `Changed since report ${test.latest.assertionRunId}, which checked an earlier version of it; no run has checked this version yet.`
          : "No run has checked it in this partition yet.",
    };
  }

  if (test.needsNewProofValues === true && outcome === "passed") {
    return { standing: "warned", reason: `Its proof values need restating: they held until ${test.validUntil ?? "a day now past"}.` };
  }

  return { standing: outcome, reason: outcome === "errored" ? latest.error : null };
}

/** The flows and tests of a board as the dashboard shows them: every test that checks the board's partition. */
export function fromBoard(board: Board, generatedUtc: string): DashData {
  const flows = board.flows.map((flow): DashFlow => ({
    pipelineId: flow.pipelineId,
    repoId: flow.repoId,
    name: flow.name,
    title: flow.title ?? flow.name,
    description: flow.description,
    set: flow.set,
    partitions: flow.partitions,
    problem: flow.problem,
    lastRunId: flow.lastRun === null ? null : flow.lastRun.assertionRunId,
    lastCheckedUtc: flow.lastRun === null ? null : flow.lastRun.completedUtc ?? flow.lastRun.startedUtc,
  }));
  const tests = board.flows.flatMap((flow) => flow.tests.filter((test) => test.runsHere).map((test): DashTest => {
    const { standing, reason } = standingOf(test);
    const stands = standing !== "disabled" && standing !== "notRun" && test.latest !== null && !test.changed;
    return {
      key: testKey(flow.pipelineId, test.name),
      pipelineId: flow.pipelineId,
      flow: flow.name,
      name: test.name,
      title: test.title ?? test.name,
      description: test.description,
      set: flow.set,
      kind: test.kind,
      dimensions: test.dimensions,
      standing,
      reason,
      resultId: stands ? test.latest!.resultId : null,
      reportId: stands ? test.latest!.assertionRunId : null,
      checkedUtc: stands ? test.latest!.completedUtc : null,
      matched: stands ? test.latest!.matched : null,
      checks: test.assertions.length,
      failedChecks: stands ? test.latest!.failedAssertions : 0,
      definitions: test.assertions.filter((check) => !check.disabled).map((check) => ({ label: check.label, expected: check.expected })),
      query: test.query,
      template: test.template,
      statedAsOf: test.asOf ?? null,
      validUntil: test.validUntil ?? null,
    };
  }));
  return { partition: board.partition, generatedUtc, flows, tests };
}

/** The value an assertion has along a dimension, or null when it has none there. */
export function valueOf(test: DashTest, dimension: string): string | null {
  return test.dimensions.find((d) => d.name === dimension)?.value ?? null;
}

/** How some assertions stand, counted: each standing, those a run judged, and the worst of them. */
export interface Tally {
  failed: number;
  errored: number;
  warned: number;
  notRun: number;
  passed: number;
  disabled: number;
  total: number;
  judged: number;
  worst: Standing;
}

export function tally(tests: readonly DashTest[]): Tally {
  const n: Tally = { failed: 0, errored: 0, warned: 0, notRun: 0, passed: 0, disabled: 0, total: tests.length, judged: 0, worst: "notRun" };
  for (const test of tests) {
    n[test.standing]++;
  }

  n.judged = n.failed + n.errored + n.warned + n.passed;
  n.worst = STANDINGS.find((standing) => n[standing] > 0) ?? "notRun";
  return n;
}

/** The share of the judged assertions that pass, in whole percent; null when none was judged. */
export function passRate(n: Tally): number | null {
  return n.judged === 0 ? null : Math.round((100 * n.passed) / n.judged);
}

/** Each dimension the assertions have, with how many distinct values it takes: the axes the map can be drawn along. */
export function dimensionsOf(tests: readonly DashTest[]): { name: string; values: number }[] {
  const values = new Map<string, Set<string>>();
  for (const test of tests) {
    for (const { name, value } of test.dimensions) {
      if (!values.has(name)) {
        values.set(name, new Set());
      }

      values.get(name)!.add(value);
    }
  }

  return [...values.entries()].map(([name, seen]) => ({ name, values: seen.size }));
}

/** The dimensions a dashboard reads by default when none is picked: the ones its documents name, before the three derived. */
const DERIVED = ["Set", "Type", "Check"];

/**
 * The two axes the map is drawn along when the reader picked none: the rows the first dimension a document names that takes
 * 2 to 30 values (else Check, Type or Set, whichever does), the columns the next one taking 2 to 12; one axis, with no
 * columns, when no second one does.
 */
export function defaultAxes(tests: readonly DashTest[]): { row: string | null; col: string | null } {
  const dims = dimensionsOf(tests);
  const ordered = [...dims.filter((d) => !DERIVED.includes(d.name)), ...DERIVED.map((name) => dims.find((d) => d.name === name)).filter((d) => d !== undefined)];
  const row = ordered.find((d) => d.values >= 2 && d.values <= 30) ?? ordered.find((d) => d.values >= 1) ?? null;
  const preferred = ["Check", "Type", "Set"].map((name) => ordered.find((d) => d.name === name && d.name !== row?.name && d.values >= 2 && d.values <= 12));
  const col = preferred.find((d) => d !== undefined) ?? ordered.find((d) => d.name !== row?.name && d.values >= 2 && d.values <= 12) ?? null;
  return { row: row?.name ?? null, col: col?.name ?? null };
}

/** A value along an axis as the map shows it: the value, or none for the assertions without one there. */
export interface AxisValue {
  /** The values this row or column stands for: one, or several gathered as Other; null for the assertions with none. */
  values: (string | null)[];
  label: string;
}

/** The values of a dimension the map lays out, worst first, at most <paramref name="max"/>, the rest gathered as Other. */
export function axisValues(tests: readonly DashTest[], dimension: string, max: number): AxisValue[] {
  const groups = new Map<string | null, DashTest[]>();
  for (const test of tests) {
    const value = valueOf(test, dimension);
    groups.set(value, [...(groups.get(value) ?? []), test]);
  }

  const ordered = [...groups.entries()].sort(([a, ta], [b, tb]) => {
    const x = tally(ta);
    const y = tally(tb);
    return STANDINGS.indexOf(x.worst) - STANDINGS.indexOf(y.worst)
      || (a === null ? 1 : 0) - (b === null ? 1 : 0)
      || String(a).localeCompare(String(b), undefined, { numeric: true });
  });
  const none = `No ${dimension.toLowerCase()}`;
  const shown = ordered.length > max ? ordered.slice(0, max - 1) : ordered;
  const axis: AxisValue[] = shown.map(([value]) => ({ values: [value], label: value ?? none }));
  if (ordered.length > max) {
    const rest = ordered.slice(max - 1).map(([value]) => value);
    axis.push({ values: rest, label: `Other (${rest.length})` });
  }

  return axis;
}

/** Whether an assertion lies along one value of an axis. */
export function along(test: DashTest, dimension: string, axis: AxisValue): boolean {
  return axis.values.includes(valueOf(test, dimension));
}

/** One square of the map: the assertions of one row and one column, and how they stand. */
export interface Square {
  tests: DashTest[];
  n: Tally;
  /** The figure: passing of judged, or a dash when none was judged. */
  main: string;
  /** The line beside it: the worst that is there, or that all pass. */
  sub: string;
}

export function square(tests: DashTest[]): Square {
  const n = tally(tests);
  const plural = (count: number, word: string) => `${count} ${word}${count === 1 ? "" : "s"}`;
  const sub = n.failed > 0 ? `${n.failed} failing`
    : n.errored > 0 ? `${n.errored} not checkable`
      : n.warned > 0 ? plural(n.warned, "warning")
        : n.notRun > 0 ? `${n.notRun} not yet checked`
          : n.judged > 0 ? "all pass"
            : `${n.disabled} disabled`;
  return { tests, n, main: n.judged === 0 ? "-" : `${n.passed}/${n.judged}`, sub };
}

/**
 * The set's verdict in one sentence: how many assertions fail, could not be checked or warn, and where most of them are on
 * the map (`Needs attention: 4 assertions fail, 3 of them in Norway · Proof values.`), or that every assertion checked
 * passes. What was checked, and what is left out, the figures beside it say.
 */
export function verdictOf(tests: readonly DashTest[], rows: AxisValue[], rowDim: string | null, cols: AxisValue[], colDim: string | null):
  { standing: Standing; text: string } {
  const n = tally(tests);
  let hot: { n: Tally; where: string } | null = null;
  for (const row of rows) {
    for (const col of cols.length > 0 ? cols : [null]) {
      const here = tests.filter((t) => (rowDim === null || along(t, rowDim, row)) && (col === null || colDim === null || along(t, colDim, col)));
      const k = tally(here);
      const weight = k.failed * 10000 + k.errored * 100 + k.warned;
      const best = hot === null ? 0 : hot.n.failed * 10000 + hot.n.errored * 100 + hot.n.warned;
      if (weight > best) {
        hot = { n: k, where: col === null ? row.label : `${row.label} · ${col.label}` };
      }
    }
  }

  const count = (value: number, one: string, many: string) => `${value} ${value === 1 ? one : many}`;
  if (n.failed > 0) {
    const share = hot !== null && hot.n.failed > 0 && (rows.length > 1 || cols.length > 1)
      ? `, ${hot.n.failed === n.failed ? "all" : hot.n.failed} of them in ${hot.where}`
      : "";
    return { standing: "failed", text: `Needs attention: ${count(n.failed, "assertion fails", "assertions fail")}${share}.` };
  }

  if (n.errored > 0) {
    return { standing: "errored", text: `${count(n.errored, "assertion", "assertions")} could not be checked${hot !== null && rows.length > 1 ? `, in ${hot.where}` : ""}.` };
  }

  if (n.warned > 0) {
    return { standing: "warned", text: `Holds, with ${count(n.warned, "warning", "warnings")}${hot !== null && rows.length > 1 ? ` in ${hot.where}` : ""}.` };
  }

  if (n.passed > 0) {
    return { standing: "passed", text: n.passed === 1 ? "Holds: the one assertion checked passes." : `Holds: all ${n.passed} assertions checked pass.` };
  }

  return { standing: "notRun", text: "Not yet checked: no run has checked these assertions as they are written now." };
}

/** The groups the list shows its assertions in, in this order. */
export type GroupId = "attention" | "notRun" | "passed" | "disabled";

export const GROUPS: readonly { id: GroupId; title: string; standings: Standing[]; open: boolean }[] = [
  { id: "attention", title: "Needs attention", standings: ["failed", "errored", "warned"], open: true },
  { id: "notRun", title: "Not yet checked", standings: ["notRun"], open: false },
  { id: "passed", title: "Passing", standings: ["passed"], open: false },
  { id: "disabled", title: "Disabled", standings: ["disabled"], open: true },
];

/** A group's assertions, worst first then by title, and the reasons that keep them from a result, each once with its count. */
export function groupOf(tests: readonly DashTest[], id: GroupId): { tests: DashTest[]; reasons: { reason: string; count: number }[] } {
  const group = GROUPS.find((g) => g.id === id)!;
  const members = tests.filter((t) => group.standings.includes(t.standing))
    .sort((a, b) => STANDINGS.indexOf(a.standing) - STANDINGS.indexOf(b.standing) || a.title.localeCompare(b.title, undefined, { numeric: true }));
  const reasons = new Map<string, number>();
  if (id === "notRun" || id === "disabled") {
    for (const test of members) {
      // A change since the last result names its report; gathered, the reason is the change itself.
      const reason = test.reason === null ? "No reason given." : test.reason.replace(/^Changed since report \d+,/, "Changed since its last result,");
      reasons.set(reason, (reasons.get(reason) ?? 0) + 1);
    }
  }

  return { tests: members, reasons: [...reasons.entries()].map(([reason, count]) => ({ reason, count })).sort((a, b) => b.count - a.count) };
}

/** Whether an assertion is found by a search: by a word of its title, name, flow, set, reason or dimensions. */
export function matches(test: DashTest, term: string): boolean {
  const q = term.trim().toLowerCase();
  if (q === "") {
    return true;
  }

  return [test.title, test.name, test.flow, test.set ?? "", test.reason ?? "", ...test.dimensions.map((d) => d.value)]
    .some((text) => text.toLowerCase().includes(q));
}

/**
 * One check of an assertion as the dashboard shows it (checkBrief.ts): its result, the question it asks, what it expected
 * and found, the measure the two make, why, and the records failing it.
 */
export type DashCheck = CheckBrief;

/** An assertion's checks as its latest result found them, each read once. */
export function checksOf(result: ResultFacts): DashCheck[] {
  return (result.assertions ?? []).map(briefOf);
}

/** Whether a check does not hold: it fails, could not be checked, or warns. A noted one (an info check) holds nothing up. */
export function needsLook(check: DashCheck): boolean {
  return check.result === "failed" || check.result === "errored" || check.result === "warned";
}

/** What an assertion's row says it found: the claim that does not hold, what was found against it, and what measures the two. */
export interface RowReading {
  /** The check that does not hold, as its document names it (`812 wellbores`); null when the line speaks for the assertion. */
  claim: string | null;
  /** What was found (`640 found, 812 expected`), why it has no result, or that its checks hold. */
  says: string;
  /** The measure of the check named; null when it makes none, or no check is named. */
  measure: Measure | null;
  /** How the measure reads, for the tone it is drawn in. */
  result: DashCheck["result"] | null;
  /** How many more of its checks do not hold. */
  more: number;
}

/**
 * What an assertion found, for its row: its first check that does not hold (one that fails or could not be checked before
 * one that warns), with what it found against what it expected; why it could not be checked or has no result; or that its
 * checks hold, with how many are only noted. Without its result at hand, how many checks do not hold.
 */
export function readingOf(test: DashTest, result: ResultFacts | undefined): RowReading {
  const line = (says: string): RowReading => ({ claim: null, says, measure: null, result: null, more: 0 });
  if (test.standing === "disabled" || test.standing === "notRun") {
    return line(test.reason ?? STANDING_WORDS[test.standing]);
  }

  if (result !== undefined) {
    const checks = checksOf(result);
    const bad = checks.find((c) => c.result === "failed" || c.result === "errored") ?? checks.find((c) => c.result === "warned");
    if (bad !== undefined) {
      return { claim: bad.label, says: bad.gist, measure: bad.measure, result: bad.result, more: checks.filter(needsLook).length - 1 };
    }

    if (result.error) {
      return line(result.error);
    }
  }

  if (test.standing === "errored") {
    return line(test.reason ?? "It could not be checked.");
  }

  if (test.standing === "passed") {
    // A passing assertion's checks that do not hold are of info severity: noted, and not held against it.
    const noted = Math.min(test.failedChecks, test.checks);
    return line(noted > 0 ? `${test.checks - noted} of ${test.checks} checks hold, ${noted} noted`
      : test.checks === 1 ? "Its check holds" : `All ${test.checks} checks hold`);
  }

  return line(test.failedChecks > 0
    ? `${test.failedChecks} of ${test.checks} ${test.checks === 1 ? "check does" : "checks do"} not hold`
    : test.reason ?? STANDING_WORDS[test.standing]);
}

/** A row's reading in one line, for a hover and for a reader of the page aloud. */
export function readingLine(reading: RowReading): string {
  const more = reading.more === 0 ? "" : `; ${reading.more} more ${reading.more === 1 ? "check does" : "checks do"} not hold`;
  return `${reading.claim === null ? "" : `${reading.claim}: `}${reading.says}${more}`;
}

/** The sets the dashboard's data holds, each with how its assertions stand (disabled ones counted only when included). */
export function setsOf(data: DashData, includeDisabled: boolean): { set: string | null; label: string; n: Tally }[] {
  const sets = new Map<string | null, DashTest[]>();
  for (const flow of data.flows) {
    if (!sets.has(flow.set)) {
      sets.set(flow.set, []);
    }
  }

  for (const test of data.tests) {
    if (includeDisabled || test.standing !== "disabled") {
      sets.get(test.set)?.push(test);
    }
  }

  return [...sets.entries()]
    .map(([set, tests]) => ({ set, label: set ?? "No set", n: tally(tests) }))
    .sort((a, b) => (a.set === null ? 1 : 0) - (b.set === null ? 1 : 0) || a.label.localeCompare(b.label, undefined, { numeric: true }));
}

/** The assertions the dashboard reads: those of the set picked (every set when none is), disabled ones only when included. */
export function inScope(data: DashData, set: string | null | undefined, includeDisabled: boolean): DashTest[] {
  return data.tests.filter((test) => (set === undefined || test.set === set) && (includeDisabled || test.standing !== "disabled"));
}

/** What a downloaded dashboard carries: the data as the GUI read it, the results it opens, and how it was being read. */
export interface DashSnapshot {
  data: DashData;
  /** The set picked when it was downloaded; undefined for every set. */
  set?: string | null;
  includeDisabled: boolean;
  results: Record<string, ResultFacts>;
  dark: boolean;
}
