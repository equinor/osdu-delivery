// How an issue reads on the Issues tab (osdu/docs/reference/concepts/record-lifecycle.md, Issues): the error its records
// share, broken into what they all say and the placeholders where each says something of its own, and the mapping
// property it names first, which heads the issue.

/** The placeholders a pattern holds where each record says something of its own, and what goes there. */
const PLACEHOLDERS: ReadonlyMap<string, string> = new Map([
  ["<value>", "a value the record read"],
  ["<n>", "a number"],
  ["<id>", "an id"],
  ["<osdu id>", "an OSDU record id"],
  ["<time>", "a moment"],
  ["<path>", "a file or folder"],
  ["<hash>", "a hash"],
  ["<query>", "a query string"],
]);

const PLACEHOLDER_SPLIT = /(<(?:value|n|id|osdu id|time|path|hash|query)>)/;

/** A part of an issue's text: words its records all say, or a placeholder where each says its own (`name` is what it shows). */
export type IssuePart =
  | { kind: "text"; text: string }
  | { kind: "placeholder"; name: string; meaning: string };

/** An issue's text as its parts, in order; empty text is dropped. */
export function issueParts(text: string): IssuePart[] {
  return text.split(PLACEHOLDER_SPLIT).flatMap((part): IssuePart[] => {
    const meaning = PLACEHOLDERS.get(part);
    if (meaning !== undefined) {
      return [{ kind: "placeholder", name: part.slice(1, -1), meaning }];
    }

    return part === "" ? [] : [{ kind: "text", text: part }];
  });
}

/** What heads an issue: the mapping property its error names first, how far through a list it reaches, and the rest. */
export interface IssueHeadline {
  /** The property, as the record's own path (`data.Curves[].CurveUnit`); null when the error names none first. */
  property: string | null;
  /** How far through a list the reason reaches (`in 3 of 12 items`), when it is a list's items'. */
  scope: string | null;
  /** What the error says, after the property; the whole error when it names none. */
  reason: string;
}

// A render's hold names the template path it holds for first: `osdu.data.X: why`, or `osdu.data.X[].Y, in 3 of 12 items: why`.
const HEADED = /^osdu\.([^\s:,]+)(?:, ([^:]+))?: ([\s\S]+)$/;

/** The property an issue's error names first, and what it says of it. */
export function issueHeadline(text: string): IssueHeadline {
  const match = HEADED.exec(text);
  return match === null
    ? { property: null, scope: null, reason: text }
    : { property: match[1], scope: match[2] ?? null, reason: match[3] };
}

// A render holds a record for each reason on its own, joined by "; ": a reason starts where a property heads one.
const NEXT_HOLD = /; (?=osdu\.[^\s:,]+(?:, [^:]+)?: )/;

/** The reasons an issue's error joins, each headed by the property it names; one reason for an error naming none. */
export function issueHolds(text: string): string[] {
  return text.split(NEXT_HOLD);
}

/** A reason that lists what each of several things said (a `$coalesce`'s alternatives): what leads the list, and its items. */
export interface IssueSteps {
  lead: string;
  steps: string[];
}

// "...is required: 1. a; 2. b; 3. c": the list starts at one, and each later item at its own number.
const FIRST_STEP = /: (?:1|<n>)\. /;
const NEXT_STEP = /; (?=(?:\d+|<n>)\. )/;
const STEP_NUMBER = /^(?:\d+|<n>)\. /;

/** The numbered list a reason ends with, when it ends with one of two items or more; null otherwise. */
export function issueSteps(reason: string): IssueSteps | null {
  const first = FIRST_STEP.exec(reason);
  if (first === null) {
    return null;
  }

  const steps = reason.slice(first.index + first[0].length).split(NEXT_STEP).map((step, index) => (index === 0 ? step : step.replace(STEP_NUMBER, "")));
  return steps.length < 2 ? null : { lead: reason.slice(0, first.index), steps };
}

/** Whether an issue answers what a reader typed: its text or its id, ignoring case; blank matches every issue. */
export function issueMatches(issue: { issue: string; shared: string }, typed: string): boolean {
  const term = typed.trim().toLowerCase();
  return term === "" || issue.shared.toLowerCase().includes(term) || issue.issue.toLowerCase().startsWith(term);
}

/** How many records an issue keeps blocked, in a few words: all held, all failed, or how many of each. */
export function issueStatesText(held: number, failed: number): string {
  if (held > 0 && failed > 0) {
    return `${held.toLocaleString("en-US")} held · ${failed.toLocaleString("en-US")} failed`;
  }

  return failed > 0 ? "all failed" : "all held";
}

/** What the foot of the issues says: how many are listed of how many, and how many a filter leaves. */
export function issuesFootText(listed: number, total: number, matching: number, filtered: boolean): string {
  const noun = (count: number) => `issue${count === 1 ? "" : "s"}`;
  const of = listed < total ? `the ${listed.toLocaleString("en-US")} largest of ${total.toLocaleString("en-US")} ${noun(total)}` : `${listed.toLocaleString("en-US")} ${noun(listed)}`;
  return filtered ? `${matching.toLocaleString("en-US")} of ${of} match` : of;
}

/** What an issue's samples say of it, by how many there are of its records and whether they share one error. */
export function samplesText(count: number, records: number, shape: "set" | "rows"): string {
  if (count === 0) {
    return "none left: its records moved on";
  }

  if (count === 1) {
    return records === 1 ? "its one record" : `1 of its ${records.toLocaleString("en-US")} records`;
  }

  return shape === "rows"
    ? `${count.toLocaleString("en-US")} records naming values of their own, newest first`
    : `the same error in all ${count.toLocaleString("en-US")}, newest first`;
}
