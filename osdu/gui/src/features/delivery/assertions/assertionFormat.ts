import {
  Ban, CircleCheck, CircleDashed, CircleX, Info, OctagonAlert, SkipForward, TriangleAlert, type LucideIcon,
} from "lucide-react";
import type {
  AssertionReportFormat, AssertionRunStatus, AssertionSeverity, DeliveryAssertionFlow, DeliveryAssertionOutcome, DeliveryAssertionTest, TestOutcome,
} from "../../../api/delivery";
import { deliveryApi } from "../../../api/delivery";

export { checkReading, sharedPrefix, type CheckReading } from "./assertionReading";

export type OutcomeTone = "success" | "destructive" | "info" | "warning" | "muted";

/**
 * Where a test stands on a board, as its document states it now: an outcome of its latest run, `disabled` when its document
 * switches it off, `notRun` when no run has checked it as it is now, or `elsewhere` when it does not check the partition. An
 * assertion also stands as `noted`: an info assertion that does not hold, which its test reports and does not count against
 * it.
 */
export type TestStanding = TestOutcome | "notRun" | "disabled" | "elsewhere" | "noted";

interface OutcomeVisual {
  tone: OutcomeTone;
  icon: LucideIcon;
  label: string;
}

/**
 * How a test's outcome is shown (DESIGN.md 7.3): a glyph and a word in a status tone, so the colour never carries it alone.
 * A failure and an error share the destructive tone and differ in glyph: a failed test found what it asserts untrue, an
 * errored one could not find out.
 */
export const OUTCOME_VISUALS: Record<TestStanding, OutcomeVisual> = {
  passed: { tone: "success", icon: CircleCheck, label: "passed" },
  failed: { tone: "destructive", icon: CircleX, label: "failed" },
  warned: { tone: "warning", icon: TriangleAlert, label: "warned" },
  errored: { tone: "destructive", icon: OctagonAlert, label: "errored" },
  skipped: { tone: "muted", icon: SkipForward, label: "skipped" },
  notRun: { tone: "muted", icon: CircleDashed, label: "not run" },
  disabled: { tone: "muted", icon: Ban, label: "disabled" },
  elsewhere: { tone: "muted", icon: SkipForward, label: "not in partition" },
  noted: { tone: "info", icon: Info, label: "noted" },
};

/** The order outcomes are listed in: what needs a look first. */
export const OUTCOME_ORDER: readonly TestStanding[] = ["failed", "errored", "warned", "notRun", "passed", "disabled", "skipped", "elsewhere"];

/** The text colour of a standing that asks for a look; none for one that does not. */
export const STANDING_TEXT: Partial<Record<TestStanding, string>> = {
  failed: "text-destructive",
  errored: "text-destructive",
  warned: "text-warning",
  noted: "text-info",
};

/**
 * Where one assertion stands. One that does not hold weighs what its severity says, as its test counts it: an error fails,
 * a warning warns and an info only notes.
 */
export function assertionStanding(outcome: Pick<DeliveryAssertionOutcome, "outcome" | "severity">): TestStanding {
  if (outcome.outcome !== "failed") {
    return outcome.outcome;
  }

  return outcome.severity === "warning" ? "warned" : outcome.severity === "info" ? "noted" : "failed";
}

/** The heaviest standing among assertions that do not hold, or null when every one holds. */
export function worstStanding(outcomes: readonly Pick<DeliveryAssertionOutcome, "outcome" | "severity">[]): TestStanding | null {
  const standings = new Set(outcomes.map(assertionStanding));
  return (["failed", "errored", "warned", "noted"] as const).find((s) => standings.has(s)) ?? null;
}

/**
 * Where a board's test stands now: not in the board's partition at all, disabled by its document, its latest outcome when
 * that result checked the test as it is now, or not run. A result of an earlier version of the test is history, and says
 * nothing of this one.
 */
export function standingOf(test: DeliveryAssertionTest): TestStanding {
  if (!test.runsHere) {
    return "elsewhere";
  }

  if (disabledWhy(test) !== null) {
    return "disabled";
  }

  return test.latest === null || test.changed ? "notRun" : test.latest.outcome;
}

/**
 * A check that asks for a look: one that did not hold, whatever its severity, or could not be evaluated. A skipped check
 * (one its document disables) asks for none: it was meant not to run.
 */
export function needsLook(outcome: Pick<DeliveryAssertionOutcome, "outcome" | "severity">): boolean {
  const standing = assertionStanding(outcome);
  return standing !== "passed" && standing !== "skipped";
}

/** How a test's checks came out, counted by where each stands. */
export interface CheckCounts {
  total: number;
  passing: number;
  failing: number;
  errored: number;
  warning: number;
  noted: number;
  skipped: number;
}

export function checkCounts(outcomes: readonly Pick<DeliveryAssertionOutcome, "outcome" | "severity">[]): CheckCounts {
  const standings = outcomes.map(assertionStanding);
  const count = (standing: TestStanding) => standings.filter((s) => s === standing).length;
  return {
    total: outcomes.length,
    passing: count("passed"),
    failing: count("failed"),
    errored: count("errored"),
    warning: count("warned"),
    noted: count("noted"),
    skipped: count("skipped"),
  };
}

/** A phrase and the standing it wears: what a row, a heading or a sheet says beside a test's name. */
export interface Verdict {
  text: string;
  standing: TestStanding;
}

/** How a run's checks all passing reads: "all 3 checks passed", or "1 check passed" for a test of one. */
function allPassed(total: number): string {
  return total === 1 ? "1 check passed" : `all ${counted(total, "check")} passed`;
}

/** One short phrase saying how a test's checks came out in a result: all of them passed, or how many did not and how. */
export function checksVerdict(counts: CheckCounts): Verdict {
  if (counts.total === 0) {
    return { text: "no check recorded", standing: "skipped" };
  }

  if (counts.passing === counts.total) {
    return { text: allPassed(counts.total), standing: "passed" };
  }

  const parts = [
    counts.failing > 0 ? `${counts.failing} failed` : null,
    counts.errored > 0 ? `${counts.errored} could not be evaluated` : null,
    counts.warning > 0 ? `${counts.warning} warned` : null,
    counts.noted > 0 ? `${counts.noted} noted` : null,
    counts.skipped > 0 ? `${counts.skipped} skipped` : null,
  ].filter((part): part is string => part !== null);
  const standing = counts.failing > 0 ? "failed" : counts.errored > 0 ? "errored" : counts.warning > 0 ? "warned" : counts.noted > 0 ? "noted" : "skipped";
  return { text: `${counts.passing} of ${counts.total} passed · ${parts.join(" · ")}`, standing };
}

/**
 * The phrase a board shows beside a test, from its latest result's summary, which counts the checks that did not pass
 * together whatever their severity; the test's outcome says how heavily they weigh.
 */
export function testVerdict(test: DeliveryAssertionTest): Verdict {
  const standing = standingOf(test);
  const latest = test.latest;
  if (standing === "elsewhere") {
    return { text: `checks ${test.partitions.join(", ")}`, standing };
  }

  // What the document says now comes before the last result: a disabled assertion does not run, and one whose proof
  // values are out of date is flagged rather than judged.
  const disabled = disabledWhy(test);
  if (disabled !== null) {
    return { text: `disabled: ${disabled}`, standing: "disabled" };
  }

  if (test.needsNewProofValues === true) {
    return { text: `needs new proof values (valid until ${test.validUntil ?? "a day now past"})`, standing: "warned" };
  }

  if (latest === null || test.changed) {
    if (test.problems.length > 0) {
      return { text: "does not fit its schema", standing: "errored" };
    }

    return { text: test.changed ? "changed since its last result; not run as it is now" : "not run yet", standing };
  }

  if (standing === "errored") {
    return { text: "could not be evaluated", standing };
  }

  if (latest.failedAssertions === 0) {
    return { text: allPassed(latest.assertions), standing };
  }

  const how = standing === "passed" ? "noted" : standing === "warned" ? "warned" : "failed";
  return { text: `${latest.failedAssertions} of ${counted(latest.assertions, "check")} ${how}`, standing: standing === "passed" ? "noted" : standing };
}

/**
 * Why an assertion does not run: its own `disabled`, or every one of its checks disabled; null when it runs.
 */
export function disabledWhy(test: Pick<DeliveryAssertionTest, "disabled" | "assertions">): string | null {
  if (test.disabled) {
    return test.disabled;
  }

  const checks = test.assertions;
  return checks.length > 0 && checks.every((check) => check.disabled) ? `every check is disabled (${checks[0].disabled})` : null;
}

/** When an assertion's proof values were stated and how long they hold, as a line says it; null when it states neither. */
export function validityText(item: { asOf?: string | null; validUntil?: string | null }): string | null {
  if (!item.asOf && !item.validUntil) {
    return null;
  }

  return [item.asOf ? `stated as of ${item.asOf}` : null, item.validUntil ? `valid until ${item.validUntil}` : "valid for good"]
    .filter((part): part is string => part !== null)
    .join(", ");
}

/** How an assertion run's status is shown, in the same families as a test's outcome. */
export function runStatusVisual(status: AssertionRunStatus): OutcomeVisual {
  switch (status) {
    case "passed":
      return OUTCOME_VISUALS.passed;
    case "failed":
      return OUTCOME_VISUALS.failed;
    case "errored":
      return OUTCOME_VISUALS.errored;
    case "cancelled":
      return { tone: "muted", icon: CircleDashed, label: "cancelled" };
    default:
      return { tone: "info", icon: CircleDashed, label: "running" };
  }
}

/** The tone a severity chip wears: the weight its failure carries. */
export const SEVERITY_TONES: Record<AssertionSeverity, string> = {
  error: "text-destructive ring-destructive/40",
  warning: "text-warning ring-warning/45",
  info: "text-info ring-info/40",
};

/** The entity type's group of a kind (`master-data`, `work-product-component`), which a board groups tests by. */
export function kindGroup(kind: string): string {
  const entity = kind.split(":")[2] ?? kind;
  const at = entity.indexOf("--");
  return at > 0 ? entity.slice(0, at) : entity;
}

/** The entity type of a kind without its group (`Wellbore`), for a compact heading. */
export function kindEntity(kind: string): string {
  const entity = kind.split(":")[2] ?? kind;
  const at = entity.indexOf("--");
  return at > 0 ? entity.slice(at + 2) : entity;
}

/** A count with its noun, pluralized: "1 assertion", "3 assertions". */
export function counted(count: number, noun: string): string {
  return `${count.toLocaleString("en-US")} ${noun}${count === 1 ? "" : "s"}`;
}

/** A duration in milliseconds as a reader reads it. */
export function duration(milliseconds: number | null | undefined): string {
  if (milliseconds === null || milliseconds === undefined) {
    return "-";
  }

  if (milliseconds < 1000) {
    return `${milliseconds} ms`;
  }

  const seconds = milliseconds / 1000;
  return seconds < 60 ? `${seconds.toFixed(1)} s` : `${Math.floor(seconds / 60)} min ${Math.round(seconds % 60)} s`;
}

/** The tests and tags a run was asked for, as its row names them; every test when it names none. */
export function selectionText(selection: string | null): string {
  if (selection === null) {
    return "every assertion";
  }

  try {
    const parsed = JSON.parse(selection) as { tests?: string[]; tags?: string[] };
    const parts = [
      ...(parsed.tests ?? []).map((name) => name),
      ...(parsed.tags ?? []).map((tag) => `#${tag}`),
    ];
    return parts.length === 0 ? "every assertion" : parts.join(", ");
  } catch {
    return selection;
  }
}

const EXTENSIONS: Record<AssertionReportFormat, { extension: string; type: string }> = {
  html: { extension: "html", type: "text/html;charset=utf-8" },
  md: { extension: "md", type: "text/markdown;charset=utf-8" },
  json: { extension: "json", type: "application/json;charset=utf-8" },
  junit: { extension: "xml", type: "application/xml;charset=utf-8" },
};

/** The file name a run's report downloads as: the flow, the partition and the report number. */
export function reportFileName(flow: string, partition: string | null, assertionRunId: number, format: AssertionReportFormat): string {
  const safe = `${flow}-${partition ?? "partition"}-report-${assertionRunId}`.replace(/[^A-Za-z0-9._-]+/g, "_");
  return `${safe}.${EXTENSIONS[format].extension}`;
}

/** Fetches a run's report in a format and offers it as a file to save. */
export async function downloadReport(flow: string, partition: string | null, assertionRunId: number, format: AssertionReportFormat): Promise<void> {
  saveText(await deliveryApi.assertionReport(assertionRunId, format), format, reportFileName(flow, partition, assertionRunId, format));
}

/** The file name a set's report downloads as: the set and the partition. */
export function setReportFileName(set: string, partition: string | null, format: AssertionReportFormat): string {
  const safe = `${set}-${partition ?? "partition"}-assertion-report`.replace(/[^A-Za-z0-9._-]+/g, "_");
  return `${safe}.${EXTENSIONS[format].extension}`;
}

/** Fetches a set's report in a format and offers it as a file to save. */
export async function downloadSetReport(set: string, partition: string | null, format: AssertionReportFormat): Promise<void> {
  saveText(await deliveryApi.assertionSetReport(set, format), format, setReportFileName(set, partition, format));
}

/** Opens a set's HTML report in a tab of its own, to read, present or print. */
export async function openSetHtmlReport(set: string): Promise<void> {
  openHtml(await deliveryApi.assertionSetReport(set, "html"));
}

/** Offers text as a file to save, under a name and the media type of its format. */
function saveText(text: string, format: AssertionReportFormat, fileName: string) {
  const url = URL.createObjectURL(new Blob([text], { type: EXTENSIONS[format].type }));
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = fileName;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}

/** Opens a run's HTML report in a tab of its own, to read or print. */
export async function openHtmlReport(assertionRunId: number): Promise<void> {
  openHtml(await deliveryApi.assertionReport(assertionRunId, "html"));
}

/** Opens an HTML report's text in a tab of its own. */
function openHtml(text: string) {
  const url = URL.createObjectURL(new Blob([text], { type: EXTENSIONS.html.type }));
  // The report is a page with no script, so the tab it opens in has nothing to do with the workbench that opened it; the
  // URL is released once the tab has had time to load it, and a blocked pop-up saves the file instead.
  const opened = window.open(url, "_blank");
  if (opened === null) {
    URL.revokeObjectURL(url);
    throw new Error("The browser blocked the report's tab; download it instead, or allow pop-ups for the workbench.");
  }

  window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

/** The payload that runs a selection of an assertion flow's tests: by name and by tag. */
export function selectionPayload(tests: readonly string[], tags: readonly string[]): Record<string, unknown> {
  const payload: Record<string, unknown> = {};
  if (tests.length > 0) {
    payload.tests = [...tests];
  }

  if (tags.length > 0) {
    payload.tags = [...tags];
  }

  return payload;
}

/** The address of a set's page: where it stands now, its report, and what fails and why. */
export function setRoute(set: string): string {
  return `/delivery/assertions/sets/${encodeURIComponent(set)}`;
}

/** The partition a run of the flow tests, and why it cannot run while the title bar is where it is. */
export function partitionOf(flow: DeliveryAssertionFlow | undefined, active: string | null): { partition: string | null; error: string | null } {
  if (flow === undefined) {
    return { partition: null, error: "Reading the flow's assertions." };
  }

  if (flow.partitions.length > 0) {
    if (active === null) {
      return { partition: null, error: "Pick a partition in the title bar: a run checks the partition picked there." };
    }

    return flow.partitions.includes(active)
      ? { partition: active, error: null }
      : {
        partition: null,
        error: `This flow does not check ${active}, the partition picked in the title bar; it checks ${flow.partitions.join(", ")}. Pick ${flow.partitions.length > 1 ? "one of them" : "it"} in the title bar to run it.`,
      };
  }

  if (flow.problem !== null) {
    return { partition: null, error: flow.problem };
  }

  return flow.partition !== null && active !== null && flow.partition !== active
    ? {
      partition: null,
      error: `This flow checks ${flow.partition}, the partition its data-partition-id header names, and the title bar is on ${active}. Pick ${flow.partition} in the title bar to run it.`,
    }
    : { partition: null, error: null };
}
