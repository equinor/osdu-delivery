// How the trigger dialog's fields read what an operator types (lines of names) and what a run being repeated was given.

import type { RunValue } from "@/api/types";

/** The non-blank, trimmed lines of a textarea. */
export function lines(text: string): string[] {
  return text.split(/\r?\n/).map((line) => line.trim()).filter((line) => line !== "");
}

/** The one value a run gave `name` (the partition it ran in, say); null when it gave none or several. */
export function singleValue(values: Readonly<Record<string, RunValue>>, name: string): string | null {
  const value = values[name];
  return typeof value === "string" ? value : null;
}
