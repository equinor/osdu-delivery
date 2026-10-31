import { CircleCheck, CircleDashed, CircleMinus, CircleX, OctagonAlert, TriangleAlert, type LucideIcon } from "lucide-react";
import type {
  DeliveryMappingFlow,
  DeliveryValueCheckCounts,
  DeliveryValueCheckFinding,
  DeliveryValueCheckRows,
  DeliveryValueCheckSample,
  ValueCheckOutcome,
} from "../../api/delivery";

/**
 * What a variable came to in a row: one of the findings' outcomes, or a value the template accepts. A failed assertion
 * (`asserted`) is counted apart from the others, since the value that fails one is still accepted by the template or not.
 */
export type OutcomeKey = ValueCheckOutcome | "valid";

/**
 * How an outcome reads wherever a check shows one: its word, its glyph and its status tone, so color never carries it
 * alone. Held stops the record, an invalid value reaches OSDU breaking its template, an empty one leaves the attribute
 * out, a row the mapping means no value for is no failure at all, and a value failing an assertion of the mapping does
 * what the assertion says.
 */
export interface OutcomeVisual {
  key: OutcomeKey;
  label: string;
  icon: LucideIcon;
  /** The status tone an outcome pill wears (DESIGN.md 7.3). */
  tone: "destructive" | "warning" | "info" | "muted" | "success";
  /** Text in the outcome's tone, for a glyph or a count. */
  textClass: string;
  /** The fill of the outcome's segment of a meter. */
  barClass: string;
  /** A chip's tinted surface and text. */
  chipClass: string;
  /** What the outcome means for the record, for a legend's hover. */
  meaning: string;
}

const HELD: OutcomeVisual = {
  key: "held",
  label: "Held",
  icon: OctagonAlert,
  tone: "destructive",
  textClass: "text-destructive",
  barClass: "bg-destructive",
  chipClass: "bg-destructive/12 text-destructive",
  meaning: "No value could be produced where one is needed, so the record is held and not delivered.",
};

const INVALID: OutcomeVisual = {
  key: "invalid",
  label: "Invalid",
  icon: TriangleAlert,
  tone: "warning",
  textClass: "text-warning",
  barClass: "bg-warning",
  chipClass: "bg-warning/15 text-warning",
  meaning: "A value is written that breaks a rule of the template (type, format, pattern, allowed values). The record is still sent.",
};

const EMPTY: OutcomeVisual = {
  key: "empty",
  label: "Left out",
  icon: CircleDashed,
  tone: "info",
  textClass: "text-info",
  barClass: "bg-info",
  chipClass: "bg-info/12 text-info",
  meaning: "No value: the entry is optional, so the record is sent without the attribute.",
};

const NOT_APPLICABLE: OutcomeVisual = {
  key: "notApplicable",
  label: "Not applicable",
  icon: CircleMinus,
  tone: "muted",
  textClass: "text-muted-foreground",
  barClass: "bg-muted-foreground/35",
  chipClass: "bg-muted text-muted-foreground",
  meaning: "The mapping means no value for the row: its $when does not hold, or the row has no item to write.",
};

const VALID: OutcomeVisual = {
  key: "valid",
  label: "Valid",
  icon: CircleCheck,
  tone: "success",
  textClass: "text-success",
  barClass: "bg-success",
  chipClass: "bg-success/12 text-success",
  meaning: "A value the template accepts.",
};

/**
 * A value failing an assertion of the mapping ($assert). Not one of {@link OUTCOMES}: a row it is found in is also held,
 * valid, invalid or empty by the template, so it never takes a segment of a meter or adds to a total of them.
 */
export const ASSERTED: OutcomeVisual = {
  key: "asserted",
  label: "Assertion failed",
  icon: CircleX,
  tone: "warning",
  textClass: "text-warning",
  barClass: "bg-warning",
  chipClass: "bg-warning/15 text-warning",
  meaning: "A value fails an assertion the mapping states ($assert). Its finding says what the failure does: the record "
    + "held before it is sent, its document kept; sent, with the failure recorded; or sent without the value. Counted apart "
    + "from held, invalid, empty and valid, as the value is still one the template accepts, or not.",
};

/**
 * The outcomes a row comes to by the template, worst first: the order a meter lays its segments out in. Each row comes to
 * exactly one of them, so their counts add up to the rows checked.
 */
export const OUTCOMES: readonly OutcomeVisual[] = [HELD, INVALID, EMPTY, NOT_APPLICABLE, VALID];

/** Every outcome a finding can name, worst first, a failed assertion after an invalid value: the order the worst is picked in. */
export const FINDING_OUTCOMES: readonly OutcomeVisual[] = [HELD, INVALID, ASSERTED, EMPTY, NOT_APPLICABLE, VALID];

export function outcomeVisual(key: OutcomeKey): OutcomeVisual {
  return FINDING_OUTCOMES.find((outcome) => outcome.key === key) ?? VALID;
}

/** How many rows (or items) came to an outcome; zero for one a node does not count. */
export function countOf(counts: DeliveryValueCheckCounts, key: OutcomeKey): number {
  const count = counts[key];
  return typeof count === "number" ? count : 0;
}

/** Rows that will not give the variable an expected value: held, written with an invalid value, or left empty. */
export function failingOf(counts: DeliveryValueCheckCounts): number {
  return counts.held + counts.invalid + counts.empty;
}

/**
 * Rows a value of the variable failed an assertion of the mapping in, counted apart from {@link failingOf}: a row can be
 * in both, so the two are never added. Zero from a node that counts none.
 */
export function assertedOf(counts: DeliveryValueCheckCounts): number {
  return countOf(counts, "asserted");
}

/** Whether any row of the variable asks for a look: one that will not give it an expected value, or fails an assertion. */
export function needsLook(counts: DeliveryValueCheckCounts): boolean {
  return failingOf(counts) > 0 || assertedOf(counts) > 0;
}

/** The worst outcome any row came to by the template, or null for a variable no row was checked for. */
export function worstOf(counts: DeliveryValueCheckCounts): OutcomeKey | null {
  return OUTCOMES.find((outcome) => counts[outcome.key] > 0)?.key ?? null;
}

/**
 * The worst of the outcomes that ask for a look (held, invalid, a failed assertion, empty) a test holds for, or null when
 * it holds for none: what a mark standing for several findings or variables takes its glyph and tone from.
 */
export function worstFailing(found: (key: OutcomeKey) => boolean): OutcomeVisual | null {
  return FINDING_OUTCOMES.find((outcome) => outcome.key !== "notApplicable" && outcome.key !== "valid" && found(outcome.key)) ?? null;
}

const whole = new Intl.NumberFormat("en-US");
const compact = new Intl.NumberFormat("en-US", { notation: "compact", maximumFractionDigits: 1 });

/** A count as a table reads it: 12,345. */
export function formatCount(value: number): string {
  return whole.format(value);
}

/**
 * The rows a check found failing an assertion of the mapping, and of them those a failure would hold before they are
 * sent; zero from a node that counts none.
 */
export function assertionRows(rows: Pick<DeliveryValueCheckRows, "withFailedAssertion" | "heldByAssertion">): { failed: number; held: number } {
  return {
    failed: typeof rows.withFailedAssertion === "number" ? rows.withFailedAssertion : 0,
    held: typeof rows.heldByAssertion === "number" ? rows.heldByAssertion : 0,
  };
}

/** {@link assertionRows} in the CLI's words; null when no row failed an assertion. */
export function assertionRowsLine(rows: Pick<DeliveryValueCheckRows, "withFailedAssertion" | "heldByAssertion">): string | null {
  const { failed, held } = assertionRows(rows);
  return failed === 0
    ? null
    : `${formatCount(failed)} failing an assertion of the mapping, ${held === 0 ? "none" : formatCount(held)} of them to be held before they are sent`;
}

/** A count where a chip has room for a few characters: 12.3K. */
export function compactCount(value: number): string {
  return value < 1000 ? String(value) : compact.format(value);
}

/** A share of a whole, never rounding a part that exists to nothing or a part that is short of the whole to all of it. */
export function percent(part: number, total: number): string {
  if (total <= 0 || part <= 0) {
    return "0%";
  }

  const share = (part / total) * 100;
  if (share < 0.1) {
    return "<0.1%";
  }

  if (share > 99.9 && part < total) {
    return ">99.9%";
  }

  return `${share >= 10 ? share.toFixed(0) : share.toFixed(1)}%`;
}

/** The rows a check reads: a number, or 0 for every row of the scope. */
export const ROW_BUDGETS: readonly { value: number; label: string }[] = [
  { value: 1000, label: "1,000 rows" },
  { value: 10000, label: "10,000 rows" },
  { value: 100000, label: "100,000 rows" },
  { value: 1000000, label: "1,000,000 rows" },
  { value: 0, label: "Whole scope" },
];

export const DEFAULT_ROW_BUDGET = 10000;

/** Example records a check of every variable names per finding, and a check of one variable. */
export const SAMPLES_ALL = 20;
export const SAMPLES_ONE = 50;

/** Example records one more page of a finding loads. */
export const SAMPLES_PAGE = 100;

/** A finding told apart from the others of its variable, across the pages of it a check answers with. */
export function findingKey(finding: Pick<DeliveryValueCheckFinding, "outcome" | "at" | "message">): string {
  return `${finding.outcome}\u001f${finding.at}\u001f${finding.message}`;
}

/** A reason as a heading names it: its outcome, with the template's rule or the assertion it breaks. */
export function findingLabel(finding: Pick<DeliveryValueCheckFinding, "outcome" | "rule">): string {
  const { label } = outcomeVisual(finding.outcome);
  return (finding.outcome === "invalid" || finding.outcome === "asserted") && finding.rule ? `${label}: ${finding.rule}` : label;
}

/** A flow interface told apart from every other, in every partition. */
export function flowKey(flow: DeliveryMappingFlow): string {
  return `${flow.pipelineId}\u001f${flow.interface ?? ""}\u001f${flow.partition ?? ""}`;
}

/** A flow interface as a picker names it. */
export function flowLabel(flow: Pick<DeliveryMappingFlow, "flow" | "interface">): string {
  return flow.interface === null ? flow.flow : `${flow.flow} / ${flow.interface}`;
}

/** Whether `inner` is `outer` or lies inside it: a property of it, or of its items. The rule the check selects entries by. */
export function within(inner: string, outer: string): boolean {
  return inner === outer || inner.startsWith(`${outer}.`) || inner.startsWith(`${outer}[].`);
}

/** Where a record's row came from: the file and its row, as far as the ingestion table says. */
export function sampleOrigin(sample: DeliveryValueCheckSample): string | null {
  if (sample.file === null || sample.file === undefined) {
    return sample.row === null || sample.row === undefined ? null : `row ${sample.row}`;
  }

  return sample.row === null || sample.row === undefined ? sample.file : `${sample.file}, row ${sample.row}`;
}

/** A CSV cell (RFC 4180): quoted when it holds a separator, a quote or a line break, with its quotes doubled. */
export function csvCell(text: string | number | null | undefined): string {
  if (text === null || text === undefined) {
    return "";
  }

  const value = String(text);
  return /[",\r\n]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value;
}

/** The example records of a finding as CSV, one line each, the columns the CLI's --rows file has. */
export function samplesCsv(variable: string, finding: DeliveryValueCheckFinding, samples: readonly DeliveryValueCheckSample[]): string {
  const header = ["variable", "at", "outcome", "rule", "reason", "value", "source_key", "label", "delivery_key", "file", "row", "item"];
  const lines = samples.map((sample) => [
    variable, finding.at, finding.outcome, finding.rule, sample.message ?? finding.message, sample.value, sample.sourceKey, sample.label,
    sample.deliveryKey, sample.file, sample.row, sample.item,
  ].map(csvCell).join(","));
  return [header.join(","), ...lines].join("\r\n") + "\r\n";
}
