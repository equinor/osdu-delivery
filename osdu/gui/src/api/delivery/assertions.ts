// Assertion flows as the delivery API answers them: the board of every flow's tests with their latest results and
// history, a flow's runs and matrix, and a run's results with what each assertion found.

import type { DeliveryParameter } from "./flows";

/** How one test came out in one run, or how one assertion of it did (`passed`, `failed`, `errored` or `skipped`). */
export type TestOutcome = "passed" | "failed" | "warned" | "errored" | "skipped";

/** An assertion run's status: running while its tests run, then how they came out, or why it stopped. */
export type AssertionRunStatus = "running" | "passed" | "failed" | "errored" | "cancelled";

export type AssertionSeverity = "error" | "warning" | "info";

/** The formats a run's report downloads in. */
export type AssertionReportFormat = "html" | "md" | "json" | "junit";

/** A check of an assertion as the flow document states it. */
export interface DeliveryAssertionDefinition {
  index: number;
  label: string;
  type: string;
  severity: AssertionSeverity;
  expected: string;
  description: string | null;
  /** Why the check is disabled (its `disabled`); absent or null when it is judged. */
  disabled?: string | null;
  /** The day what it expects was stated (its own `asOf`, else its assertion's). */
  asOf?: string | null;
  /** The last day what it expects holds (`validUntil`, or `validFor` after `asOf`); absent when it holds for good. */
  validUntil?: string | null;
  /** True when that day is past: a run flags the check as needing new proof values instead of judging it. */
  needsNewProofValues?: boolean;
  /** What it tests, whole: what it reads and what that has to be (`data.WellboreID resolves to a master-data--Wellbore`). */
  criterion?: string | null;
  /** True when `label` is a name the document gives the check, rather than one read off what it tests. */
  named?: boolean;
}

/** One value a check read and held to what it expects, alike values once with how many records hold them. */
export interface DeliveryAssertionReading {
  /** What was read: a record path, a measure (`record count`), a proof value's label. */
  what: string;
  holds: boolean;
  /** What was found there; absent for a value the check judged without keeping it. */
  found?: string | null;
  /** What this value had to be, where each value has its own (a proof value). */
  expected?: string | null;
  /** The first record met that holds the value. */
  id?: string | null;
  /** What the value belongs to: the row of proof values that states it. */
  of?: string | null;
  count?: number;
}

/** One run's outcome of a test, as a history strip or a matrix cell shows it. */
export interface DeliveryAssertionPoint {
  assertionRunId: number;
  outcome: TestOutcome;
  failedAssertions: number;
  matched: number | null;
  completedUtc: string;
}

/** A test's latest result, without its detail. */
export interface DeliveryAssertionLatest {
  resultId: number;
  assertionRunId: number;
  outcome: TestOutcome;
  severity: AssertionSeverity | null;
  matched: number | null;
  evaluated: number | null;
  sampled: boolean;
  assertions: number;
  failedAssertions: number;
  durationMs: number;
  error: string | null;
  completedUtc: string;
  definitionHash: string;
}

/**
 * One test on a board: what the flow document says of it, whether it fits the template of its kind (`problems`), its
 * latest result, how it came out over the last runs (newest first), and whether it changed since that result.
 */
/** One dimension of an assertion: what it is about along one axis, such as `Country: Norway`. */
export interface DeliveryAssertionDimension {
  name: string;
  value: string;
}

export interface DeliveryAssertionTest {
  name: string;
  /** What people read it as; null when it names none. */
  title: string | null;
  description: string | null;
  kind: string;
  tags: string[];
  severity: AssertionSeverity;
  query: string | null;
  ids: number;
  bulk: boolean;
  read: "storage" | "index";
  maxRecords: number;
  assertions: DeliveryAssertionDefinition[];
  /** False when the test names partitions and the board's partition is not one of them. */
  runsHere: boolean;
  partitions: string[];
  /** The version of the template of its kind its fields are checked against; null when it reads no field of an exact kind. */
  template: string | null;
  /** The mapping (`Name@version`) whose pinned template it is checked against (`template: WellLog@1.5.0`). */
  templateMapping?: string | null;
  /** True when it names its template version itself; false takes the newest captured, or the mapping's. */
  templatePinned?: boolean;
  /** Why it is disabled (its `disabled`); absent or null when it runs. */
  disabled?: string | null;
  /** The day what it expects was stated (`asOf`). */
  asOf?: string | null;
  /** The last day what it expects holds; absent when it holds for good. */
  validUntil?: string | null;
  /** True when every check it judges is past that day, so a run flags it as needing new proof values. */
  needsNewProofValues?: boolean;
  problems: string[];
  latest: DeliveryAssertionLatest | null;
  changed: boolean;
  definitionHash: string;
  /** Every axis the dashboard maps it by: Set, Type and Check, then the ones its documents name. */
  dimensions: DeliveryAssertionDimension[];
}

/** An assertion run as listings show it. */
export interface DeliveryAssertionRun {
  assertionRunId: number;
  flowId: string;
  flowName: string;
  partition: string | null;
  runId: string | null;
  actor: string;
  /** The tests the run was asked for, as JSON; null when it ran every test. */
  selection: string | null;
  status: AssertionRunStatus;
  tests: number;
  passed: number;
  failed: number;
  warned: number;
  errored: number;
  skipped: number;
  startedUtc: string;
  completedUtc: string | null;
  error: string | null;
}

/** One assertion flow on a board, in the board's partition. */
export interface DeliveryAssertionFlow {
  pipelineId: string;
  repoId: string;
  name: string;
  /** What people read the flow as; null when it names none. */
  title: string | null;
  description: string | null;
  batch: string | null;
  /** The set its assertions are grouped under: its `set`, else its batch; null when it names neither. */
  set: string | null;
  partition: string | null;
  /** False when the flow does not test the board's partition (or its document does not parse: see `problem`). */
  testsPartition: boolean;
  /** False when the catalog's copy of the document does not parse, which needs a fix; a flow of another partition parses. */
  parses: boolean;
  partitions: string[];
  failRunOn: "error" | "warning" | "never";
  /** The parameters a run of the flow takes, which its tests use as `{name}` tokens. */
  parameters: DeliveryParameter[];
  problem: string | null;
  lastRun: DeliveryAssertionRun | null;
  tests: DeliveryAssertionTest[];
}

/**
 * What a board adds up to over every test that runs in its partition, each by where it stands now: a disabled test counts
 * as disabled alone, and one whose latest result checked an earlier version of it as not run.
 */
export interface DeliveryAssertionTotals {
  flows: number;
  tests: number;
  passed: number;
  failed: number;
  warned: number;
  errored: number;
  notRun: number;
  disabled: number;
  problems: number;
  changed: number;
  passRate: number | null;
}

export interface DeliveryAssertionBoard {
  partition: string | null;
  totals: DeliveryAssertionTotals;
  flows: DeliveryAssertionFlow[];
}

/** A record that failed an assertion: what it held there and why it failed. */
export interface DeliveryAssertionExample {
  id?: string | null;
  value?: string | null;
  reason: string;
  /** For a row of a delivery flow's staged data, its key parts in the flow's key order; absent for anything else. */
  key?: string[] | null;
}

/** The staged rows an assertion read: the delivery flow, its interface, and the parameter values its scope was read under. */
export interface DeliveryStagedScope {
  flow: string;
  interface?: string | null;
  values: Record<string, string>;
}

/** How one assertion of a test came out in one run. */
export interface DeliveryAssertionOutcome {
  index: number;
  label: string;
  description?: string | null;
  type: string;
  severity: AssertionSeverity;
  outcome: TestOutcome;
  expected: string;
  actual?: string | null;
  message?: string | null;
  checked?: number | null;
  failing?: number | null;
  /** The number the assertion measured (a count, an aggregate, the share that held), for a trend. */
  value?: number | null;
  examples?: DeliveryAssertionExample[];
  examplesTrimmed?: boolean;
  /** The staged rows it read, for a staged delivered assertion; absent for one that reads OSDU alone. */
  staged?: DeliveryStagedScope | null;
  /** What it tests, whole; absent in a result recorded before results kept it. */
  criterion?: string | null;
  /** True when `label` is a name the document gives the check. */
  named?: boolean;
  /** The values it read and held to what it expects, whether they hold or not; empty for a check that keeps none. */
  readings?: DeliveryAssertionReading[];
  /** True when it read more distinct values than `readings` lists. */
  readingsTrimmed?: boolean;
}

/** Which of the staged rows a check read a query shows: the ones it lists by their keys, or every row of its scope. */
export type AssertionSourceRows = "listed" | "all";

/** One test's whole result in one run: what it read, and every assertion. */
export interface DeliveryTestResult {
  test: string;
  description?: string | null;
  kind: string;
  tags?: string[];
  outcome: TestOutcome;
  severity?: AssertionSeverity | null;
  matched?: number | null;
  evaluated?: number | null;
  sampled?: boolean;
  query?: string | null;
  ids?: number;
  template?: string | null;
  definitionHash: string;
  durationMs?: number;
  error?: string | null;
  problems?: string[];
  notes?: string[];
  assertions?: DeliveryAssertionOutcome[];
  startedUtc?: string;
  completedUtc?: string;
}

/** An assertion run with every test's result. */
export interface DeliveryAssertionRunDetail {
  run: DeliveryAssertionRun;
  pipelineId: string | null;
  results: DeliveryTestResult[];
}

/** A test's result whole, by the id the board gave it, with the report it is from. */
export interface DeliveryAssertionResult {
  resultId: number;
  assertionRunId: number;
  result: DeliveryTestResult;
}

/** A flow's recent runs against its tests: each test's outcome in each run, null where the run did not run it. */
export interface DeliveryAssertionMatrix {
  runs: DeliveryAssertionRun[];
  tests: { test: string; kind: string; cells: (DeliveryAssertionPoint | null)[] }[];
}
