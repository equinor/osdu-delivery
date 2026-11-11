// The words and times the assertion dashboard's parts share (osdu/docs/reference/flow/assertion-sets.md, The dashboard).

import { parseUtc } from "@/lib/time";
import type { CheckResult } from "./checkBrief";
import type { Standing } from "./dashboardModel";

const WHEN = new Intl.DateTimeFormat(undefined, { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
const WHEN_LONG = new Intl.DateTimeFormat(undefined, { day: "numeric", month: "long", year: "numeric", hour: "2-digit", minute: "2-digit", timeZoneName: "short" });

/** A UTC time as the reader's own, short for a row and long where it stands alone; the text itself when it is not a time. */
export function when(utc: string | null, long = false): string {
  if (utc === null) {
    return "";
  }

  const at = parseUtc(utc);
  return Number.isNaN(at.getTime()) ? utc : (long ? WHEN_LONG : WHEN).format(at);
}

/** A count with its noun, for one or many. */
export function plural(count: number, one: string, many = `${one}s`): string {
  return `${count.toLocaleString("en-US")} ${count === 1 ? one : many}`;
}

/** How a check came out, in a word. */
export const RESULT_WORDS: Record<CheckResult, string> = {
  passed: "Holds",
  failed: "Fails",
  warned: "Warns",
  errored: "Not checked",
  noted: "Noted",
  disabled: "Disabled",
};

/** The standing a check's result is drawn as: its glyph and its tone. */
export const RESULT_STANDING: Record<CheckResult, Standing> = {
  passed: "passed",
  failed: "failed",
  warned: "warned",
  errored: "errored",
  noted: "notRun",
  disabled: "disabled",
};
