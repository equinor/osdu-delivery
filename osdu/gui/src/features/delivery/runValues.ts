// How the trigger dialog's fields read what an operator types: lines of names, and name=value pairs for a flow's parameters.

import type { RunValue } from "@/api/types";
import { runValueItems, runValueOf, runValueText } from "@/lib/runValues";

const IDENTIFIER = /^[A-Za-z_][A-Za-z0-9_]*$/;

/** The non-blank, trimmed lines of a textarea. */
export function lines(text: string): string[] {
  return text.split(/\r?\n/).map((line) => line.trim()).filter((line) => line !== "");
}

/**
 * Parses "name=value" lines into the flow's parameter values; the first malformed line is the error. A name written on
 * several lines is given each of their values, as a name repeated with `--set` is on the CLI.
 */
export function parseValues(text: string): { values: Record<string, RunValue>; error: string | null } {
  const items: Record<string, string[]> = {};
  for (const line of lines(text)) {
    const at = line.indexOf("=");
    const name = at > 0 ? line.slice(0, at).trim() : "";
    if (at <= 0 || !IDENTIFIER.test(name)) {
      return { values: valuesOf(items), error: `Parameter '${line}' must be written as name=value (the name an identifier).` };
    }

    (items[name] ??= []).push(line.slice(at + 1));
  }

  return { values: valuesOf(items), error: null };
}

function valuesOf(items: Record<string, string[]>): Record<string, RunValue> {
  return Object.fromEntries(Object.entries(items).map(([name, given]) => [name, runValueOf(given) ?? ""]));
}

/** A run's values as the textarea writes them, the one named `except` left out: a name=value line per value. */
export function valuesToLines(values: Readonly<Record<string, RunValue>>, except: string): string {
  return Object.entries(values)
    .filter(([name]) => name !== except)
    .flatMap(([name, value]) => runValueItems(value).map((item) => `${name}=${item}`))
    .join("\n");
}

/** A run's values as fields of one value each hold them, the one named `except` left out. */
export function valuesToFields(values: Readonly<Record<string, RunValue>>, except: string): Record<string, string> {
  return Object.fromEntries(Object.entries(values).filter(([name]) => name !== except).map(([name, value]) => [name, runValueText(value)]));
}

/** The one value a run gave `name` (the partition it ran in, say); null when it gave none or several. */
export function singleValue(values: Readonly<Record<string, RunValue>>, name: string): string | null {
  const value = values[name];
  return typeof value === "string" ? value : null;
}
