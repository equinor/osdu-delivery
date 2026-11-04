// How an assertion's check and a set's flows read on a page: pure functions of what a run recorded, with no import at
// all, so the unit tests compile and run them as they are.

/** What a check's reading is made of: the parts of a check's outcome (DeliveryAssertionOutcome) it reads. */
export interface CheckFacts {
  expected: string;
  actual?: string | null;
  message?: string | null;
  type: string;
  value?: number | null;
  outcome: string;
}

/**
 * How one check reads, each fact once (the control plane's CheckView reads it alike for its reports): what it expects (the
 * comparison alone, `equals 5191`), what qualifies that (`warns within 10%`, `leaving out "MD"`, `stated as of 2026-10-27`),
 * what it found (the number a count or an aggregate measured, else what the check says it found), and why it came out as it
 * did when that adds to the two (`1 below 5191 (0.02%), within the warning band of 10%, so it warns.`).
 */
export interface CheckReading {
  expected: string;
  qualifiers: string[];
  found: string;
  why: string | null;
}

/** A check's expectation is written as the comparison, then the warning band, the values left out and the day stated. */
const STATED = / \(stated as of ([^)]+)\)$/;
const LEAVING_OUT = /, leaving out (.+)$/;
const BAND = / \(warning within ([^)]+)\)/;

export function checkReading(outcome: CheckFacts): CheckReading {
  let expected = outcome.expected;
  const qualifiers: string[] = [];
  let stated: string | null = null;
  const statedAt = STATED.exec(expected);
  if (statedAt !== null) {
    stated = statedAt[1]!;
    expected = expected.slice(0, statedAt.index);
  }

  const leaving = LEAVING_OUT.exec(expected);
  if (leaving !== null) {
    qualifiers.push(`leaving out ${leaving[1]}`);
    expected = expected.slice(0, leaving.index);
  }

  const band = BAND.exec(expected);
  if (band !== null) {
    qualifiers.unshift(`warns within ${band[1]}`);
    expected = expected.slice(0, band.index) + expected.slice(band.index + band[0].length);
  }

  if (stated !== null) {
    qualifiers.push(`stated as of ${stated}`);
  }

  const value = outcome.value;
  const found = (outcome.type === "count" || outcome.type === "aggregate") && typeof value === "number" && Number.isFinite(value)
    ? String(Math.round(value * 10000) / 10000)
    : outcome.actual ?? "-";
  let why: string | null = null;
  if (outcome.message && outcome.outcome !== "passed") {
    const clauses = outcome.message.replace(/[. ]+$/, "").split("; ")
      .filter((clause) => clause !== "" && !clause.startsWith("expected ") && !(typeof outcome.actual === "string" && outcome.actual.startsWith(clause)));
    why = clauses.length === 0 ? null : `${clauses.join("; ")}.`;
  }

  return { expected: expected.trim(), qualifiers, found, why };
}

/** What a check keeps of the staged rows it read: the parts of a check's outcome (DeliveryAssertionOutcome) a query of them reads. */
export interface StagedFacts {
  index?: number;
  label: string;
  failing?: number | null;
  examples?: readonly { key?: readonly string[] | null }[];
  staged?: { flow: string; interface?: string | null; values: Record<string, string> } | null;
}

/** The staged rows a check read, as a query of them names them: the check, where the rows are, and how many it lists. */
export interface StagedRows {
  index: number;
  label: string;
  flow: string;
  interface: string | null;
  values: Record<string, string>;
  /** How many rows it lists by their keys: what a query of the rows it lists narrows to. */
  listed: number;
  /** How many rows did not hold, of which it lists the first; null when it does not say. */
  failing: number | null;
}

/** The staged rows a check read, or null for a check that reads OSDU alone (every check but a staged delivered one). */
export function stagedRowsOf(check: StagedFacts): StagedRows | null {
  if (check.staged === null || check.staged === undefined || check.index === undefined) {
    return null;
  }

  return {
    index: check.index,
    label: check.label,
    flow: check.staged.flow,
    interface: check.staged.interface ?? null,
    values: check.staged.values,
    listed: (check.examples ?? []).filter((example) => (example.key ?? []).length > 0).length,
    failing: check.failing ?? null,
  };
}

/** The values a scope was read under, as a line names them: `logSource = COMPOSITE, project = NORTH`. */
export function scopeText(values: Record<string, string>): string {
  return Object.entries(values).map(([name, value]) => `${name} = ${value}`).join(", ");
}

/**
 * The start the names of a set's flows share, up to a hyphen (`welldb-welllog-assertion-`), which a list of them can leave
 * out so the part that tells them apart shows; empty for one flow, or when the names share none.
 */
export function sharedPrefix(names: readonly string[]): string {
  if (names.length < 2) {
    return "";
  }

  let prefix = names[0]!;
  for (const name of names) {
    while (!name.startsWith(prefix)) {
      prefix = prefix.slice(0, -1);
    }
  }

  const cut = prefix.lastIndexOf("-") + 1;
  return names.every((name) => name.length > cut) ? prefix.slice(0, cut) : "";
}
