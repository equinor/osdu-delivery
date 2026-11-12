// How one check of an assertion reads to someone who did not write it (osdu/docs/reference/flow/assertion-sets.md, An
// open assertion): the question its type asks, what it expected and found in plain words, the measure the two make (a
// number against a target, or a share that holds), by how much it missed, and the records that fail it with each
// difference apart. Pure functions of what a run recorded, with no import of the workbench, so the dashboard, the file it
// downloads and the unit tests read a check through the same code. Whatever a text does not fit is shown as it is.

import { checkReading, type CheckFacts } from "../assertionReading.ts";

/** How a check came out, as the dashboard words it. */
export type CheckResult = "passed" | "failed" | "warned" | "errored" | "noted" | "disabled";

/** A record, or a row, a check lists as failing it. */
export interface CheckExample {
  id?: string | null;
  value?: string | null;
  reason: string;
  key?: string[] | null;
}

/** One value a check read and held to what it expects (DeliveryAssertionReading). */
export interface ReadingFacts {
  what: string;
  holds: boolean;
  found?: string | null;
  expected?: string | null;
  id?: string | null;
  of?: string | null;
  count?: number;
}

/** What a brief reads of a check's outcome (DeliveryAssertionOutcome). */
export interface BriefFacts extends CheckFacts {
  /** The check's position in its assertion, which its document's statement of it is found by. */
  index?: number;
  label: string;
  description?: string | null;
  severity: string;
  checked?: number | null;
  failing?: number | null;
  examples?: CheckExample[];
  examplesTrimmed?: boolean;
  /** What it tests, whole; absent in a result recorded before results kept it. */
  criterion?: string | null;
  /** True when its label is a name its document gives it. */
  named?: boolean;
  readings?: ReadingFacts[];
  readingsTrimmed?: boolean;
  staged?: { flow: string; interface?: string | null; values: Record<string, string> } | null;
}

/** A check as its document states it now (DeliveryAssertionDefinition): what a result recorded earlier is read with. */
export interface CheckStated {
  index?: number;
  label: string;
  expected: string;
  criterion?: string | null;
  named?: boolean;
}

/** The words a document's keys are spelled out as, wherever a text names one outside a quoted value. */
const SPELLED: readonly [RegExp, string][] = [
  [/\batLeast\b/g, "at least"],
  [/\batMost\b/g, "at most"],
  [/\bgreaterThan\b/g, "more than"],
  [/\blessThan\b/g, "less than"],
  [/\bnotEquals\b/g, "not equal to"],
  [/\bnotMatches\b/g, "does not match"],
  [/\bnotContains\b/g, "does not contain"],
  [/\bstartsWith\b/g, "starts with"],
  [/\bendsWith\b/g, "ends with"],
  [/\bstrictlyIncreasing\b/g, "strictly increasing"],
  [/\bstrictlyDecreasing\b/g, "strictly decreasing"],
  [/\browCount\b/g, "row count"],
];

/** A noun the engine writes for one or many (`record(s)`, `search(es)`), with the number that says which before it. */
const COUNTED = /(\d[\d,]*(?:\.\d+)?)((?: [A-Za-z][A-Za-z-]*){0,3}?) ([A-Za-z]+)\((e?s)\)('?)( (?:find|differ|do|meet|hold|are|were|have)\b)?/g;

/** The verb one of something takes where the engine wrote the verb of many. */
const OF_ONE: Record<string, string> = { find: "finds", differ: "differs", do: "does", meet: "meets", hold: "holds", are: "is", were: "was", have: "has" };

/**
 * A text of the engine as people read it: `5 record(s)` is `5 records` and `1 row(s) find` is `1 row finds`, and a key
 * of the document is spelled out (`atLeast 5` is `at least 5`). What stands between double quotes is a value and is left
 * as it is.
 */
export function plain(text: string): string {
  return text.split(/("(?:[^"\\]|\\.)*")/).map((part, at) => {
    if (at % 2 === 1) {
      return part;
    }

    let said = part.replace(COUNTED, (_, number: string, between: string, noun: string, ending: string, possessive: string, verb: string | undefined) => {
      const one = Number(number.replace(/,/g, "")) === 1;
      const named = one ? noun : `${noun}${ending}`;
      const owned = possessive === "" ? "" : one ? "'s" : "'";
      const does = verb === undefined ? "" : one && possessive === "" ? ` ${OF_ONE[verb.trim()] ?? verb.trim()}` : verb;
      return `${number}${between} ${named}${owned}${does}`;
    });
    for (const [key, words] of SPELLED) {
      said = said.replace(key, words);
    }

    return said;
  }).join("");
}

/** A number as a figure: thousands apart, at most four decimals, and a number too small for them by its first digits. */
export function figure(value: number): string {
  return value !== 0 && Math.abs(value) < 0.0001
    ? value.toLocaleString("en-US", { maximumSignificantDigits: 4 })
    : value.toLocaleString("en-US", { maximumFractionDigits: 4 });
}

/** A share in percent: one decimal from 1% up, two significant digits under it, so a near miss does not read as none. */
function percent(share: number): string {
  const value = share * 100;
  return `${value >= 1 ? String(Math.round(value * 10) / 10) : String(Number(value.toPrecision(2)))}%`;
}

/** The question each type of check asks, in the words of someone who did not write it. */
const ASKS: Record<string, string> = {
  count: "Are there as many records as there should be?",
  aggregate: "Does a figure measured over the records come to what it should?",
  field: "Does this property hold what it should?",
  unique: "Is every record there only once?",
  groupBy: "Do the records fall into the groups they should?",
  recordSet: "Are the records the source states there?",
  knownValues: "Do the records hold the values the source states?",
  conforms: "Does every record follow the schema of its type?",
  mapping: "Does every record meet the rules of its mapping?",
  legal: "Is every legal tag on the records valid?",
  delivered: "Does OSDU hold exactly what was delivered?",
  indexed: "Was every record indexed, so a search finds it?",
  columns: "Does the bulk data have the columns it should?",
  rowCount: "Does the bulk data have the number of rows it should?",
  column: "Does a column of the bulk data hold what it should?",
  monotonic: "Do the values of a column run in order?",
};

/** The question an aggregate asks, by what it measures. */
const ASKS_OF_MEASURE: Record<string, string> = {
  count: "Are there as many values as there should be?",
  distinct: "Are there as many different values as there should be?",
  missing: "Are as many records without a value as there should be?",
  sum: "Does the sum come to what it should?",
  avg: "Does the average come to what it should?",
  min: "Is the lowest value what it should be?",
  max: "Is the highest value what it should be?",
};

/**
 * The question a check asks: its type's, an aggregate's by what it measures, a reference's when it looks one up; empty
 * for a type the dashboard does not know.
 */
export function asksOf(check: Pick<BriefFacts, "type" | "expected" | "actual">): string {
  if (check.type === "field" && check.expected.startsWith("resolves to ")) {
    return "Does every reference lead to a record that is there?";
  }

  if (check.type === "aggregate") {
    const actual = check.actual ?? "";
    const measured = MEASURED.exec(actual);
    return PER_RECORD.test(actual) ? "Does a figure measured over each record's bulk data come to what it should?"
      : measured === null ? ASKS.aggregate! : ASKS_OF_MEASURE[measured[1]!]!;
  }

  return ASKS[check.type] ?? "";
}

/** How a share's part is drawn: what holds, what is within a warning band, what does not hold, and what is not there at all. */
export type PartTone = "passed" | "warned" | "failed" | "missing";

/**
 * One part of a share: how many, the words that follow the count for one and for many (`differs`, `differ`), and what
 * the part is called where its count stands as a figure (`Differ`).
 */
export interface SharePart {
  tone: PartTone;
  count: number;
  words: [string, string];
  label: string;
}

/** A number measured against the range that holds: `640` against `exactly 812`. */
export interface TargetMeasure {
  form: "target";
  found: number;
  /** The range that holds, an end left open being null; one number when both are it. */
  low: number | null;
  high: number | null;
  /** The number the document states, when it states one (`equals`), its tolerance already in the range. */
  exact: number | null;
  /** How far under the range still only warns, and how far over it; the ends themselves when the check has no warning band. */
  warnLow: number | null;
  warnHigh: number | null;
  /** What it expected, as a figure with the words before it: `at least` `5`, `172 to 458`, `812`. */
  lead: string | null;
  expected: string;
  /** What follows the figure expected: its tolerance. */
  trail: string | null;
  /** What was measured: `records`, `distinct values of data.WellboreID`; null when the check does not say. */
  of: string | null;
  /** By how much it missed (`172 short of 812 (21.2%)`, `Nothing found`); null when it is within the range. */
  gap: string | null;
  /** Whether it counts things, so what it is short by is missing and what it is over by is too many. */
  counts: boolean;
  /** How far outside the range it is, from the end it missed: under it below zero, over it above; null within the range. */
  off: number | null;
  /** That distance as a share of the end it missed (`21.2%`); null within the range, or when that end is zero. */
  offShare: string | null;
}

/**
 * How much of a measure does not hold, from 0 (all of it holds) to 1 (none of it does): the share of what was judged, or
 * how far a number is from the end it missed, as a part of that end. What the dashboard lists the largest gaps by.
 */
export function missOf(measure: Measure): number {
  if (measure.form === "share") {
    return (measure.whole - heldOf(measure)) / measure.whole;
  }

  if (measure.off === null) {
    return 0;
  }

  const against = measure.found - measure.off;
  return against === 0 ? 1 : Math.min(1, Math.abs(measure.off) / Math.abs(against));
}

/** How many of what a check judged hold: `3 of 5 rows`, each part of the rest named. */
export interface ShareMeasure {
  form: "share";
  whole: number;
  /** What was judged, for one and for many. */
  unit: [string, string];
  parts: SharePart[];
}

export type Measure = TargetMeasure | ShareMeasure;

const NUMBER = "(-?\\d+(?:\\.\\d+)?(?:[eE][-+]?\\d+)?)";
const TERM = new RegExp(`^(?:between ${NUMBER} and ${NUMBER}|(equals|atLeast|atMost|greaterThan|lessThan) ${NUMBER}(?: \\(tolerance ${NUMBER}\\))?)(?: and |$)`);
const BAND = /^warns within (\d+(?:\.\d+)?)(%?)$/;
const MEASURED = /^(count|distinct|sum|avg|min|max|missing)\((.+?)\) is /;
const PER_RECORD = /^\d+ of \d+ record\(s\)/;

const MEASURED_AS: Record<string, string> = {
  count: "values of",
  distinct: "distinct values of",
  sum: "the sum of",
  avg: "the average of",
  min: "the lowest",
  max: "the highest",
  missing: "records without",
};

/** The range a number has to be in, read off a comparison (`atLeast 5 and atMost 10`, `equals 0.1524 (tolerance 0.0001)`). */
function rangeOf(expected: string): Pick<TargetMeasure, "low" | "high" | "exact" | "lead" | "expected" | "trail"> | null {
  let rest = expected;
  let low: number | null = null;
  let high: number | null = null;
  let exact: number | null = null;
  let tolerance: number | null = null;
  let lead: string | null = null;
  let terms = 0;
  while (rest !== "") {
    const term = TERM.exec(rest);
    if (term === null) {
      return null;
    }

    const [whole, from, to, operator, operand, within] = term;
    if (from !== undefined && to !== undefined) {
      low = Math.max(low ?? -Infinity, Number(from));
      high = Math.min(high ?? Infinity, Number(to));
    } else {
      const value = Number(operand);
      if (operator === "equals") {
        exact = value;
        tolerance = within === undefined ? null : Number(within);
        low = Math.max(low ?? -Infinity, value - (tolerance ?? 0));
        high = Math.min(high ?? Infinity, value + (tolerance ?? 0));
      } else if (operator === "atLeast" || operator === "greaterThan") {
        low = Math.max(low ?? -Infinity, value);
        lead = operator === "atLeast" ? "at least" : "more than";
      } else {
        high = Math.min(high ?? Infinity, value);
        lead = operator === "atMost" ? "at most" : "less than";
      }
    }

    terms++;
    rest = rest.slice(whole.length);
  }

  if (terms === 0 || (low !== null && high !== null && low > high)) {
    return null;
  }

  if (exact !== null) {
    return { low, high, exact, lead: null, expected: figure(exact), trail: tolerance === null ? null : `give or take ${figure(tolerance)}` };
  }

  return low !== null && high !== null
    ? { low, high, exact: null, lead: null, expected: `${figure(low)} to ${figure(high)}`, trail: null }
    : { low, high, exact: null, lead: terms === 1 ? lead : null, expected: figure((low ?? high)!), trail: null };
}

/** What a count or an aggregate measured against what it had to be; null for a check that does not compare one number. */
function targetOf(check: BriefFacts, expected: string, qualifiers: readonly string[]): TargetMeasure | null {
  const found = check.value;
  if ((check.type !== "count" && check.type !== "aggregate") || typeof found !== "number" || !Number.isFinite(found) || PER_RECORD.test(check.actual ?? "")) {
    return null;
  }

  const range = rangeOf(expected);
  if (range === null) {
    return null;
  }

  const measured = MEASURED.exec(check.actual ?? "");
  const counts = check.type === "count" || (measured !== null && ["count", "distinct", "missing"].includes(measured[1]!));
  const against = range.exact ?? (range.low !== null && found < range.low ? range.low : range.high !== null && found > range.high ? range.high : null);
  const outside = (range.low !== null && found < range.low) || (range.high !== null && found > range.high);
  let gap: string | null = null;
  let off: number | null = null;
  let offShare: string | null = null;
  if (outside && against !== null) {
    off = found - against;
    offShare = against === 0 ? null : percent(Math.abs(off) / Math.abs(against));
    const side = found < against ? (counts ? "short of" : "below") : (counts ? "over" : "above");
    gap = counts && found === 0 ? "Nothing found" : `${figure(Math.abs(off))} ${side} ${figure(against)}${offShare === null ? "" : ` (${offShare})`}`;
  }

  // The band widens each number the comparison names its own way: a percentage is of that number.
  const banded = qualifiers.map((q) => BAND.exec(q)).find((match) => match !== null) ?? null;
  const reach = (end: number) => (banded === null ? 0 : banded[2] === "%" ? (Math.abs(range.exact ?? end) * Number(banded[1])) / 100 : Number(banded[1]));
  return {
    form: "target",
    found,
    ...range,
    warnLow: range.low === null ? null : range.low - reach(range.low),
    warnHigh: range.high === null ? null : range.high + reach(range.high),
    of: check.type === "count" ? "records" : measured === null ? null : `${MEASURED_AS[measured[1]!]} ${measured[2]}`,
    gap,
    counts,
    off,
    offShare,
  };
}

/** The parts a proof values check counts its rows in, as its finding names them. */
const ROW_PARTS: readonly { says: string; tone: PartTone; words: [string, string]; label: string }[] = [
  { says: "hold their values", tone: "passed", words: ["holds", "hold"], label: "Hold" },
  { says: "within the warning band", tone: "warned", words: ["is within the warning band", "are within the warning band"], label: "In the warning band" },
  { says: "differ", tone: "failed", words: ["differs", "differ"], label: "Differ" },
  { says: "not found", tone: "missing", words: ["is not found", "are not found"], label: "Not found" },
  { says: "found more than once", tone: "failed", words: ["is found more than once", "are found more than once"], label: "Found more than once" },
];

/** The checks whose `checked` and `failing` count the same things, so the two make a share, and what those things are. */
const SHARED_UNITS: Record<string, [string, string]> = {
  field: ["record", "records"],
  column: ["record", "records"],
  conforms: ["record", "records"],
  mapping: ["record", "records"],
  columns: ["record", "records"],
  rowCount: ["record", "records"],
  monotonic: ["record", "records"],
  aggregate: ["record", "records"],
  indexed: ["record", "records"],
  delivered: ["record", "records"],
  legal: ["legal tag", "legal tags"],
  knownValues: ["row", "rows"],
};

/** How many of what a check judged hold; null when it judged nothing, or does not count the two alike. */
function shareOf(check: BriefFacts, result: CheckResult): ShareMeasure | null {
  const { checked, failing } = check;
  if (typeof checked !== "number" || typeof failing !== "number" || checked <= 0 || failing < 0 || failing > checked) {
    return null;
  }

  if (check.type === "knownValues") {
    const parts = ROW_PARTS.flatMap((part): SharePart[] => {
      const counted = new RegExp(`(\\d+) ${part.says}\\b`).exec(check.actual ?? "");
      return counted === null ? [] : [{ tone: part.tone, count: Number(counted[1]), words: part.words, label: part.label }];
    });
    if (parts.reduce((sum, part) => sum + part.count, 0) === checked && parts.some((part) => part.tone === "passed")) {
      return { form: "share", whole: checked, unit: SHARED_UNITS.knownValues!, parts };
    }
  }

  const unit = check.type === "field" && check.expected.startsWith("resolves to ") ? (["reference", "references"] as [string, string]) : SHARED_UNITS[check.type];
  if (unit === undefined || (check.type === "aggregate" && !PER_RECORD.test(check.actual ?? ""))) {
    return null;
  }

  return {
    form: "share",
    whole: checked,
    unit,
    parts: [
      { tone: "passed", count: checked - failing, words: ["passes", "pass"], label: "Pass" },
      { tone: result === "warned" ? "warned" : "failed", count: failing, words: ["does not", "do not"], label: "Do not pass" },
    ],
  };
}

/** The measure a check's outcome makes: a number against its target, a share that holds, or none. */
export function measureOf(check: BriefFacts, expected: string, qualifiers: readonly string[], result: CheckResult): Measure | null {
  return targetOf(check, expected, qualifiers) ?? shareOf(check, result);
}

/** How many of a share hold. */
export function heldOf(share: ShareMeasure): number {
  return share.parts.filter((part) => part.tone === "passed").reduce((sum, part) => sum + part.count, 0);
}

/** What the part of a share that holds does, for one and for many: rows `hold`, records `pass`. */
export function holdsAs(share: ShareMeasure): [string, string] {
  return share.parts.find((part) => part.tone === "passed")?.words ?? ["passes", "pass"];
}

/**
 * A share in one line: `3 of 5 rows hold, 1 differs, 1 is not found`, `0 of 3 records pass`; of one thing, what it does
 * (`The row is not found`, `The record does not pass`).
 */
export function shareLine(share: ShareMeasure): string {
  const held = heldOf(share);
  const holds = holdsAs(share);
  const rest = share.parts.filter((part) => part.tone !== "passed" && part.count > 0);
  if (share.whole === 1) {
    const does = held === 1 || rest.length === 0 ? holds[0] : rest[0]!.words[0] === "does not" ? `does not ${holds[1]}` : rest[0]!.words[0];
    return `The ${share.unit[0]} ${does}`;
  }

  const named = rest.length === 1 && rest[0]!.words[1] === "do not" ? [] : rest;
  return [
    `${figure(held)} of ${figure(share.whole)} ${share.unit[1]} ${holds[1]}`,
    ...named.map((part) => `${figure(part.count)} ${part.words[part.count === 1 ? 0 : 1]}`),
  ].join(", ");
}

/**
 * What of a share does not hold, in one line, the news before the rest: `1 of 75 records does not pass`,
 * `2 of 5 rows do not hold: 1 differs, 1 is not found`, `None of the 3 records pass`, `All 4 rows are not found`; of one
 * thing, what it does.
 */
export function offLine(share: ShareMeasure): string {
  const held = heldOf(share);
  const off = share.whole - held;
  const rest = share.parts.filter((part) => part.tone !== "passed" && part.count > 0);
  if (share.whole === 1 || off === 0 || rest.length === 0) {
    return shareLine(share);
  }

  const holds = holdsAs(share);
  const says = (part: SharePart) => `${figure(part.count)} ${part.words[part.count === 1 ? 0 : 1]}`;
  // A part named only as what does not hold takes the verb of what holds: `does not pass`.
  const plainly = rest.length === 1 && rest[0]!.words[1] === "do not";
  const each = plainly ? "" : `: ${rest.map(says).join(", ")}`;
  if (held === 0) {
    return rest.length === 1 && !plainly
      ? `All ${figure(share.whole)} ${share.unit[1]} ${rest[0]!.words[1]}`
      : `None of the ${figure(share.whole)} ${share.unit[1]} ${holds[1]}${each}`;
  }

  if (rest.length === 1 && !plainly) {
    return `${figure(off)} of ${figure(share.whole)} ${share.unit[1]} ${rest[0]!.words[off === 1 ? 0 : 1]}`;
  }

  return `${figure(off)} of ${figure(share.whole)} ${share.unit[1]} ${off === 1 ? "does" : "do"} not ${holds[1]}${each}`;
}

/** One figure of a check as it stands in a row of them: what it counts, the number, and what qualifies it. */
export interface Tile {
  label: string;
  /** The words before the figure: `at least`. */
  lead: string | null;
  value: string;
  /** What follows the figure, smaller: a tolerance, a share of the whole. */
  aside: string | null;
  /** The tone the figure is drawn in when it is the news (what is missing, what differs); null for a plain figure. */
  tone: PartTone | null;
}

/**
 * The figures a measure is read by, in the order a reader takes them: what was expected, what was found and by how much it
 * missed; or how many were checked, how many hold, and each part of the rest. None for a share of one thing, which a
 * line says better.
 */
export function tilesOf(measure: Measure, result: CheckResult): Tile[] {
  if (measure.form === "target") {
    const tiles: Tile[] = [
      { label: "Expected", lead: measure.lead, value: measure.expected, aside: measure.trail, tone: null },
      { label: "Found", lead: null, value: figure(measure.found), aside: null, tone: null },
    ];
    if (measure.off !== null) {
      const under = measure.off < 0;
      tiles.push({
        label: measure.counts ? (under ? "Short by" : "Over by") : (under ? "Below by" : "Above by"),
        lead: null,
        value: figure(Math.abs(measure.off)),
        aside: measure.offShare,
        // A check only noted is held against nothing, so its figure is plain.
        tone: result === "warned" ? "warned" : result === "noted" ? null : "failed",
      });
    }

    return tiles;
  }

  if (measure.whole <= 1) {
    return [];
  }

  const unit = measure.unit[1];
  const held = measure.parts.find((part) => part.tone === "passed");
  return [
    { label: `${unit.charAt(0).toUpperCase()}${unit.slice(1)} checked`, lead: null, value: figure(measure.whole), aside: null, tone: null },
    { label: held?.label ?? "Pass", lead: null, value: figure(heldOf(measure)), aside: null, tone: null },
    ...measure.parts.filter((part) => part.tone !== "passed" && part.count > 0).map((part): Tile => ({
      label: part.label,
      lead: null,
      value: figure(part.count),
      aside: percent(part.count / measure.whole),
      tone: result === "noted" ? null : part.tone,
    })),
  ];
}

/** What a clause of a message only says again once a measure is drawn: the counts the measure shows. */
const RESTATES: readonly RegExp[] = [
  /^The bulk data of \d+ of \d+ record\(s\) does not meet it$/,
  /^\d+ of \d+ record\(s\) do not meet it$/,
  /^\d+ row\(s\) find no record$/,
  /^\d+ row\(s\) find more than one record$/,
  /^\d+ row\(s\) differ from the record they find$/,
  /^\d+ row\(s\) differ only within the warning band \(warning\)$/,
  /^The test matches \d+ record\(s\)$/,
  /^it expects .+$/,
];

/** A clause that says by how much a number missed, and what follows it (`, within the warning band of 10%, so it warns`). */
const MISSED_BY = /^-?[\d.]+ (?:below|above) -?[\d.]+(?: \([\d.]+%\))?(?:, (.+))?$/;

/** Why a check came out as it did, without what its measure already shows; null when nothing is left to say. */
function whyOf(why: string | null, measure: Measure | null): string | null {
  if (why === null || measure === null) {
    return why === null ? null : plain(why);
  }

  const kept = why.replace(/[. ]+$/, "").split("; ").flatMap((clause) => {
    if (RESTATES.some((pattern) => pattern.test(clause))) {
      return [];
    }

    const missed = measure.form === "target" && measure.gap !== null ? MISSED_BY.exec(clause) : null;
    return missed === null ? [clause] : missed[1] === undefined ? [] : [missed[1]];
  });
  if (kept.length === 0) {
    return null;
  }

  const said = plain(kept.join("; "));
  return `${said.charAt(0).toUpperCase()}${said.slice(1)}.`;
}

/**
 * One value a record differs in: what it is, what was expected and what was found, each as a figure where it is one
 * number, how far apart the two are, and what the engine adds.
 */
export interface Difference {
  what: string;
  /** What was found: a number as a figure (`25.2812`), anything else as it is written. */
  found: string | null;
  /** What was found as the engine wrote it, when the figure shortens it. */
  foundExact: string | null;
  /** What follows what was found: where it was read (`row 5565`). */
  foundNote: string | null;
  /** What was expected: `60 ± 0.0001`, `at least 5`. */
  expected: string | null;
  /** How far what was found is from what was expected, signed (`-6.8125`); null when either is not a number. */
  off: string | null;
  note: string | null;
}

const FOUND_NUMBER = new RegExp(`^${NUMBER}(?: (.+))?$`);

/** What a comparison expects, as a reader says it: `60 ± 0.0001`, `at least 5`, `2 to 4`; any other as it is written. */
function expectedWords(expected: string): { said: string; range: ReturnType<typeof rangeOf> } {
  const range = rangeOf(expected);
  if (range === null) {
    return { said: plain(expected), range };
  }

  const within = range.trail === null ? "" : ` ± ${range.trail.replace(/^give or take /, "")}`;
  return { said: `${range.lead === null ? "" : `${range.lead} `}${range.expected}${within}`, range };
}

/** What was found as a figure with what follows it, and how far it is from the range expected. */
function differenceOf(what: string, found: string | null, expected: string | null): Difference {
  const wanted = expected === null ? null : expectedWords(expected);
  const number = found === null ? null : FOUND_NUMBER.exec(found);
  if (found === null || number === null) {
    return { what, found: found === null ? null : plain(found), foundExact: null, foundNote: null, expected: wanted?.said ?? null, off: null, note: null };
  }

  const value = Number(number[1]);
  const shown = figure(value);
  const range = wanted?.range ?? null;
  const against = range === null ? null : range.exact ?? (range.low !== null && value < range.low ? range.low : range.high !== null && value > range.high ? range.high : null);
  const off = against === null || value === against ? null : value - against;
  return {
    what,
    found: shown,
    foundExact: shown === number[1] ? null : number[1]!,
    foundNote: number[2] === undefined ? null : plain(number[2].replace(/^\((.*)\)$/, "$1")),
    expected: wanted?.said ?? null,
    off: off === null ? null : `${off < 0 ? "-" : "+"}${figure(Math.abs(off))}`,
    note: null,
  };
}

/** Why one record, or row, fails a check: the reason in a line, and each value it differs in when the reason lists several. */
export interface Evidence {
  id: string | null;
  value: string | null;
  /** The reason, or the words before its list of differences (`differs in 5 proof values`). */
  reason: string;
  differences: Difference[];
}

const LISTED = /^(differs(?: within the warning band)? in \d+ proof value\(s\)): (.+)$/;
const DIFFERS = /^(.+?) is (.+?), expected (?:it|each step) (.+)$/;

/** A failing record as the brief shows it: a reason that lists differences is read into them, one a line. */
export function evidenceOf(example: CheckExample): Evidence {
  const id = example.id ?? null;
  const value = example.value ?? null;
  const listed = LISTED.exec(example.reason);
  if (listed === null) {
    return { id, value, reason: plain(example.reason), differences: [] };
  }

  const differences: Difference[] = [];
  for (const clause of listed[2]!.split("; ")) {
    const last = differences[differences.length - 1];
    const differs = DIFFERS.exec(clause);
    if (differs !== null) {
      differences.push(differenceOf(differs[1]!, differs[2]!, differs[3]!));
    } else if (last !== undefined && last.expected === null && /^expected (?:it|each step) /.test(clause)) {
      last.expected = expectedWords(clause.replace(/^expected (?:it|each step) /, "")).said;
    } else if (last !== undefined && last.expected !== null && /^-?[\d.]+ (?:below|above) /.test(clause)) {
      // By how much it missed the figures already say; what is left is that it only warns.
      const band = /, (within the warning band of [^,]+)/.exec(clause);
      const said = band !== null ? band[1]! : last.off === null ? plain(clause) : null;
      if (said !== null) {
        last.note = last.note === null ? said : `${last.note}; ${said}`;
      }
    } else {
      // A value that is not there to compare says so after its name: `GR at 1000 has no row: the index MD holds no 1000`.
      const cut = clause.indexOf(": ");
      differences.push(cut < 0
        ? { what: plain(clause), found: null, foundExact: null, foundNote: null, expected: null, off: null, note: null }
        : { what: plain(clause.slice(0, cut)), found: plain(clause.slice(cut + 2)), foundExact: null, foundNote: null, expected: null, off: null, note: null });
    }
  }

  return { id, value, reason: plain(listed[1]!), differences };
}

/** The records, or rows, that fail a check for one reason: the reason once, and each of them under it. */
export interface EvidenceGroup {
  reason: string;
  /** The values it differs in, when the reason lists them: then the group is the one record. */
  differences: Difference[];
  items: { id: string | null; value: string | null }[];
}

/**
 * The records that fail a check, gathered by why: a reason many records share is said once with the records under it,
 * in the order the reasons first appear. A record whose reason lists the values it differs in stands alone.
 */
export function evidenceGroups(examples: readonly Evidence[]): EvidenceGroup[] {
  const groups: EvidenceGroup[] = [];
  const shared = new Map<string, EvidenceGroup>();
  for (const example of examples) {
    const item = { id: example.id, value: example.value };
    const known = example.differences.length === 0 ? shared.get(example.reason) : undefined;
    if (known !== undefined) {
      known.items.push(item);
      continue;
    }

    const group: EvidenceGroup = { reason: example.reason, differences: example.differences, items: [item] };
    groups.push(group);
    if (example.differences.length === 0) {
      shared.set(example.reason, group);
    }
  }

  return groups;
}

const HTTP_PROBLEM = /^HTTP (\d{3}) ([A-Za-z]+) from (?:GET|POST|PUT|PATCH|DELETE) (\S+?)(?: \(correlation-id [^)]*\))?(?:: (.+))?$/s;

/** The OSDU service an address is of, as people name it: the part after `/api/`. */
function serviceOf(address: string): string {
  const named = /\/api\/([a-z-]+)/.exec(address)?.[1];
  return named === undefined ? "OSDU" : `The ${named.replace(/-service$/, "").replace(/-/g, " ")} service`;
}

/**
 * A problem as a reader takes it: an answer of an OSDU service is said by the service and what it answered
 * (`The search service answered 503 Service Unavailable`), with what it said after it, and the text as written is kept
 * for whoever looks into it. Any other text is said as it is, in plain words, with nothing more to show.
 */
export function plainProblem(text: string): { said: string; written: string | null } {
  const answer = HTTP_PROBLEM.exec(text.trim());
  if (answer === null) {
    return { said: plain(text), written: null };
  }

  const [, status, reason, address, detail] = answer;
  const words = reason!.replace(/([a-z])([A-Z])/g, "$1 $2");
  // What the service said often ends by naming its status again: `(Bad Request)`.
  const more = detail === undefined ? "" : detail.trim().replace(/\.$/, "").replace(new RegExp(`\\s*\\(${words}\\)$`, "i"), "").trim();
  const said = `${serviceOf(address!)} answered ${status} ${words}${more === "" ? "." : `: ${more}${/[.!?]$/.test(more) ? "" : "."}`}`;
  return { said, written: text };
}

/** One value a check read, as the brief shows it: a number as a figure, what it had to be in a reader's words. */
export interface ReadValue {
  what: string;
  holds: boolean;
  /** What was found; null for a value the check judged without keeping it. */
  found: string | null;
  /** What this value had to be, where each value has its own; null takes the check's criterion. */
  expected: string | null;
  /** The first record that holds it. */
  id: string | null;
  /** What it belongs to: the row of proof values that states it. */
  of: string | null;
  /** How many records (references, rows) hold it. */
  count: number;
}

/** What each type of check does to find out, in a sentence. */
const DID: Record<string, string> = {
  count: "Counted the records the assertion's query matches.",
  field: "Read the property from each record, and held what it holds to the condition.",
  aggregate: "Took the measure over the values every record holds at the property.",
  unique: "Compared the values of the properties across the records, looking for two records that share them.",
  groupBy: "Asked the search to group the records by the property, and counted each group.",
  recordSet: "Laid the records out as rows of the properties named, and compared them with the rows stated.",
  knownValues: "Found the record each row names, and compared each proof value the row states with what that record holds.",
  conforms: "Checked each record, whole, against the schema of its type.",
  mapping: "Held each record to what its mapping asserts of the values a record carries.",
  legal: "Gathered the legal tags the records carry, and asked the legal service whether each is valid.",
  delivered: "Compared the records the delivery flow's ledger holds as delivered with the records the query matches.",
  indexed: "Searched for records the indexer could not index whole: an index status above 200.",
  columns: "Read which columns each record's bulk data holds.",
  rowCount: "Read how many rows each record's bulk data holds.",
  column: "Read the column from each record's bulk data, row by row, and held each value to the condition.",
  monotonic: "Read the column from each record's bulk data, and compared each value with the one before it.",
};

/** What a check did to find out, in a sentence; empty for a type the dashboard does not know. */
export function didOf(check: Pick<BriefFacts, "type" | "expected" | "actual" | "staged">): string {
  if (check.type === "field" && check.expected.startsWith("resolves to ")) {
    return "Read the references the records hold at the property, and looked each one up in storage.";
  }

  if (check.type === "aggregate" && PER_RECORD.test(check.actual ?? "")) {
    return "Took the measure over the column of each record's bulk data, record by record.";
  }

  if (check.type === "delivered" && check.staged !== null && check.staged !== undefined) {
    return "Compared the rows the delivery flow stages with the records the query matches, and asked its ledger about each row that is not one.";
  }

  return DID[check.type] ?? "";
}

/**
 * What a check tests, whole, in plain words, and the name its document gives it, if it gives one: read from its
 * statement. One stated with no criterion (a result recorded before results kept it, read without its document) is
 * taken by its label: a label that says what the check expects is read off what it tests, so it is the criterion; any
 * other is a name its document gave it, and what it expects is all of the criterion there is to show.
 */
export function criterionOf(stated: CheckStated): { name: string | null; criterion: string } {
  const label = plain(stated.label);
  const expected = plain(stated.expected);
  if (stated.criterion !== null && stated.criterion !== undefined) {
    return { name: stated.named === true ? label : null, criterion: plain(stated.criterion) };
  }

  return label.toLowerCase().includes(expected.toLowerCase()) ? { name: null, criterion: label } : { name: label, criterion: expected };
}

const WHOLE_NUMBER = new RegExp(`^${NUMBER}$`);

/** A value a check read as the brief shows it. */
function readValueOf(reading: ReadingFacts): ReadValue {
  const found = reading.found ?? null;
  return {
    what: reading.what,
    holds: reading.holds,
    found: found !== null && WHOLE_NUMBER.test(found) ? figure(Number(found)) : found,
    expected: reading.expected === null || reading.expected === undefined ? null : expectedWords(reading.expected).said,
    id: reading.id ?? null,
    of: reading.of ?? null,
    count: reading.count ?? 1,
  };
}

/** The values a line names before it counts the rest. */
const VALUES_NAMED = 3;

/** An id's own part as people read it: its percent escapes undone (`g%2Fcm3` is `g/cm3`), or as it is when they are not escapes. */
function unescaped(part: string): string {
  try {
    return decodeURIComponent(part);
  } catch {
    return part;
  }
}

/**
 * What a check found, in values, for its line: the values it read (`"MD"`, `m, ft, g/cm3, +5 more`, a value many records
 * hold with how many), how many proof values it read, or, for a result that keeps no values, the number it measured or
 * how many it judged (`8 references`). Never that it holds: the mark beside it says so.
 */
function valueLine(check: BriefFacts, values: readonly ReadValue[], measure: Measure | null, found: string): string {
  if (values.some((value) => value.of !== null)) {
    return `${figure(values.length)} proof ${values.length === 1 ? "value" : "values"}`;
  }

  const kept = values.filter((value) => value.found !== null);
  if (kept.length > 0) {
    // A reference is named by its own part: the criterion beside it already says which type of record it leads to.
    const type = check.type === "field" ? /^resolves to an? (\S+)$/.exec(check.expected)?.[1] : undefined;
    const said = (found: string) => (type !== undefined && found.includes(`:${type}:`) ? unescaped(found.slice(found.indexOf(`:${type}:`) + type.length + 2)) : found);
    const named = kept.slice(0, VALUES_NAMED).map((value) => (value.count > 1 && kept.length > 1 ? `${said(value.found!)} (${figure(value.count)})` : said(value.found!)));
    return `${named.join(", ")}${kept.length > VALUES_NAMED ? `, +${figure(kept.length - VALUES_NAMED)} more` : ""}`;
  }

  if (measure !== null) {
    return measure.form === "target" ? figure(measure.found) : `${figure(measure.whole)} ${measure.unit[measure.whole === 1 ? 0 : 1]}`;
  }

  return check.actual === null || check.actual === undefined ? "" : found;
}

/** One check as the dashboard reads it. */
export interface CheckBrief {
  /** What the check is called: the name its document gives it, else what it tests. */
  label: string;
  /** The name its document gives it; null when it gives none, and its criterion is what it is called. */
  name: string | null;
  /** What it tests, whole, in plain words: what it reads and what that has to be. */
  criterion: string;
  /** Its type as documents name it: count, field, aggregate, knownValues, ... */
  type: string;
  /** What it did to find out, in a sentence. */
  did: string;
  /** What it found as its run recorded it, in plain words; empty when it did not get as far. */
  recorded: string;
  /** The values it read and held to its criterion, whether they hold or not; empty for a check that keeps none. */
  values: ReadValue[];
  valuesTrimmed: boolean;
  /** True for a result of a run that keeps the values a check reads; false for one recorded before runs did. */
  keepsValues: boolean;
  /** What it found, in values, for its line: never that it holds, which its mark says. */
  value: string;
  /** What the document says the check is for, when it says. */
  description: string | null;
  result: CheckResult;
  asks: string;
  expected: string;
  qualifiers: string[];
  found: string;
  /** What the check adds about what it found: values left out, and the like. */
  foundNotes: string[];
  why: string | null;
  /** Why as the engine wrote it, when the brief says it in other words: a service's answer whole, for whoever looks into it. */
  whyWritten: string | null;
  measure: Measure | null;
  /** What it found in one line, where it holds: `640 found, 812 expected`, `3 of 5 rows hold, 2 differ`. */
  gist: string;
  /**
   * What does not hold, the news first, for an assertion's row and its largest gaps: `172 short of 812 (21.2%)`,
   * `Nothing found, 62 expected`, `2 of 5 rows do not hold: 1 differs, 1 is not found`; the gist where it holds.
   */
  headline: string;
  /**
   * How much of it does not hold, from 0 to 1: its measure's miss; all of it where it could not be made or had nothing
   * to check; and half where it does not hold and makes no measure, so it ranks under what is known to be all off.
   */
  miss: number;
  /** How many records, or rows, fail it; null when it does not say. */
  failing: number | null;
  examples: Evidence[];
  examplesTrimmed: boolean;
}

/** What a finding of record by record says, which a share already shows: `2 of 3 record(s)' bulk data meet it`. */
const PER_RECORD_ONLY = /^\d+ of \d+ record\(s\)(?:' bulk data)? meet it$/;

/**
 * What a check's finding adds to its measure, each once: for a number, what stands in parentheses after it (the values
 * left out); for a share, the clauses that do not count its parts again (how many records were read, where rows differ
 * most).
 */
function notesOf(check: BriefFacts, measure: Measure): string[] {
  const actual = check.actual;
  if (measure.form === "share") {
    const clauses = (actual ?? "").split("; ").filter((clause) => clause !== "" && !PER_RECORD_ONLY.test(clause));
    return (check.type === "knownValues" ? clauses.slice(1) : clauses).map(plain);
  }

  const measured = MEASURED.exec(actual ?? "");
  if (measured === null) {
    return [];
  }

  // In plain words first: a noun written for one or many holds parentheses of its own.
  const after = plain((actual ?? "").slice(measured[0].length).replace(/^\S+\s*/, ""));
  const notes = [...after.matchAll(/\(([^()]*)\)/g)].map((match) => match[1]!).filter((note) => !note.startsWith("0 values left out"));
  return after.replace(/\([^()]*\)/g, "").trim() === "" ? notes : [after];
}

/** A check as the dashboard reads it, from what its run recorded. */
export function briefOf(check: BriefFacts, stated?: CheckStated): CheckBrief {
  const reading = checkReading(check);
  const result: CheckResult = check.outcome === "failed"
    ? (check.severity === "warning" ? "warned" : check.severity === "info" ? "noted" : "failed")
    : check.outcome === "skipped" ? "disabled" : check.outcome === "errored" ? "errored" : check.outcome === "warned" ? "warned" : "passed";
  const measure = result === "disabled" || result === "errored" ? null : measureOf(check, reading.expected, reading.qualifiers, result);
  const problem = reading.why === null || measure !== null ? null : plainProblem(reading.why);
  const why = problem !== null && problem.written !== null ? problem.said : whyOf(reading.why, measure);
  const found = plain(reading.found);
  const said = why === null ? null : why.replace(/\.$/, "");
  const wanted = measure === null || measure.form !== "target" ? "" : `${measure.lead === null ? "" : `${measure.lead} `}${measure.expected} expected`;
  const gist = measure === null
    ? (result === "passed" ? null : said) ?? (reading.found === "-" ? "nothing found" : `found ${found}`)
    : measure.form === "share" ? shareLine(measure)
      : measure.gap === null ? `${figure(measure.found)} found`
        : `${figure(measure.found)} found, ${wanted}`;
  const headline = measure === null || result === "passed" ? gist
    : measure.form === "share" ? offLine(measure)
      : measure.gap === null ? gist
        : measure.counts && measure.found === 0 ? `Nothing found, ${wanted}` : measure.gap;

  // What it tests, whole: as its result kept it, else as its document states it now.
  const { name, criterion } = criterionOf({
    label: check.label,
    expected: reading.expected,
    criterion: check.criterion ?? stated?.criterion,
    named: check.named ?? stated?.named,
  });
  const values = (check.readings ?? []).map(readValueOf);
  return {
    label: name ?? criterion,
    name,
    criterion,
    type: check.type,
    did: didOf(check),
    recorded: check.actual === null || check.actual === undefined ? "" : plain(check.actual),
    values,
    valuesTrimmed: check.readingsTrimmed === true,
    keepsValues: check.criterion !== null && check.criterion !== undefined,
    value: valueLine(check, values, measure, found),
    description: check.description ?? null,
    result,
    asks: asksOf(check),
    expected: plain(reading.expected),
    qualifiers: reading.qualifiers.map(plain),
    found,
    foundNotes: measure === null ? [] : notesOf(check, measure),
    why,
    whyWritten: problem?.written ?? null,
    measure,
    gist,
    headline,
    miss: result === "passed" || result === "disabled" ? 0
      : measure !== null ? missOf(measure)
        : result === "errored" || check.checked === 0 ? 1 : 0.5,
    failing: typeof check.failing === "number" && check.type !== "count" ? check.failing : null,
    examples: (check.examples ?? []).map(evidenceOf),
    examplesTrimmed: check.examplesTrimmed === true,
  };
}
