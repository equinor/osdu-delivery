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

/** What a brief reads of a check's outcome (DeliveryAssertionOutcome). */
export interface BriefFacts extends CheckFacts {
  label: string;
  description?: string | null;
  severity: string;
  checked?: number | null;
  failing?: number | null;
  examples?: CheckExample[];
  examplesTrimmed?: boolean;
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

/** One part of a share: how many, and the words that follow the count for one and for many (`differs`, `differ`). */
export interface SharePart {
  tone: PartTone;
  count: number;
  words: [string, string];
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
  if (outside && against !== null && counts && found === 0) {
    gap = "Nothing found";
  } else if (outside && against !== null) {
    const amount = Math.abs(found - against);
    const side = found < against ? (counts ? "short of" : "below") : (counts ? "over" : "above");
    gap = `${figure(amount)} ${side} ${figure(against)}${against === 0 ? "" : ` (${percent(amount / Math.abs(against))})`}`;
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
  };
}

/** The parts a proof values check counts its rows in, as its finding names them. */
const ROW_PARTS: readonly { says: string; tone: PartTone; words: [string, string] }[] = [
  { says: "hold their values", tone: "passed", words: ["holds", "hold"] },
  { says: "within the warning band", tone: "warned", words: ["is within the warning band", "are within the warning band"] },
  { says: "differ", tone: "failed", words: ["differs", "differ"] },
  { says: "not found", tone: "missing", words: ["is not found", "are not found"] },
  { says: "found more than once", tone: "failed", words: ["is found more than once", "are found more than once"] },
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
      return counted === null ? [] : [{ tone: part.tone, count: Number(counted[1]), words: part.words }];
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
      { tone: "passed", count: checked - failing, words: ["passes", "pass"] },
      { tone: result === "warned" ? "warned" : "failed", count: failing, words: ["does not", "do not"] },
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

/** One value a record differs in: what it is, what was found and what was expected, and what the engine adds. */
export interface Difference {
  what: string;
  found: string | null;
  expected: string | null;
  note: string | null;
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
      differences.push({ what: differs[1]!, found: differs[2]!, expected: plain(differs[3]!), note: null });
    } else if (last !== undefined && last.expected === null && /^expected (?:it|each step) /.test(clause)) {
      last.expected = plain(clause.replace(/^expected (?:it|each step) /, ""));
    } else if (last !== undefined && last.expected !== null && /^-?[\d.]+ (?:below|above) /.test(clause)) {
      last.note = last.note === null ? plain(clause) : `${last.note}; ${plain(clause)}`;
    } else {
      // A value that is not there to compare says so after its name: `GR at 1000 has no row: the index MD holds no 1000`.
      const cut = clause.indexOf(": ");
      differences.push(cut < 0
        ? { what: plain(clause), found: null, expected: null, note: null }
        : { what: plain(clause.slice(0, cut)), found: plain(clause.slice(cut + 2)), expected: null, note: null });
    }
  }

  return { id, value, reason: plain(listed[1]!), differences };
}

/** One check as the dashboard reads it. */
export interface CheckBrief {
  label: string;
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
  measure: Measure | null;
  /** What it found in one line, for an assertion's row: `640 found, 812 expected`, `3 of 5 rows hold, 2 differ`. */
  gist: string;
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
export function briefOf(check: BriefFacts): CheckBrief {
  const reading = checkReading(check);
  const result: CheckResult = check.outcome === "failed"
    ? (check.severity === "warning" ? "warned" : check.severity === "info" ? "noted" : "failed")
    : check.outcome === "skipped" ? "disabled" : check.outcome === "errored" ? "errored" : check.outcome === "warned" ? "warned" : "passed";
  const measure = result === "disabled" || result === "errored" ? null : measureOf(check, reading.expected, reading.qualifiers, result);
  const why = whyOf(reading.why, measure);
  const found = plain(reading.found);
  const said = why === null ? null : why.replace(/\.$/, "");
  const gist = measure === null
    ? (result === "passed" ? null : said) ?? (reading.found === "-" ? "nothing found" : `found ${found}`)
    : measure.form === "share" ? shareLine(measure)
      : measure.gap === null ? `${figure(measure.found)} found`
        : `${figure(measure.found)} found, ${measure.lead === null ? "" : `${measure.lead} `}${measure.expected} expected`;
  return {
    label: plain(check.label),
    description: check.description ?? null,
    result,
    asks: asksOf(check),
    expected: plain(reading.expected),
    qualifiers: reading.qualifiers.map(plain),
    found,
    foundNotes: measure === null ? [] : notesOf(check, measure),
    why,
    measure,
    gist,
    failing: typeof check.failing === "number" && check.type !== "count" ? check.failing : null,
    examples: (check.examples ?? []).map(evidenceOf),
    examplesTrimmed: check.examplesTrimmed === true,
  };
}
