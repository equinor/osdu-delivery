// The values a page holds for a flow's parameters (ScopeParameterFields), and what a run, a preview or a check is sent of them.

import type { RunValue } from "@/api/types";
import { runValueItems, runValueOf } from "@/lib/runValues";
import type { DeliveryParameter } from "../../api/delivery";

/** The values a page holds: the text of a typed field, the values of a field the scope reads. */
export type ScopeFieldValues = Record<string, RunValue>;

/**
 * The values a run, a preview or a check is sent: each parameter's values trimmed, each once, an empty one left out so the
 * parameter's default applies; one value as itself and several as their list, which only a parameter the scope reads takes.
 */
export function scopeValuesToSend(parameters: readonly DeliveryParameter[], values: ScopeFieldValues): Record<string, RunValue> {
  const sent: Record<string, RunValue> = {};
  for (const parameter of parameters) {
    const items = [...new Set(runValueItems(values[parameter.name]).map((item) => item.trim()).filter((item) => item !== ""))];
    const value = runValueOf(items);
    if (value !== undefined) {
      sent[parameter.name] = value;
    }
  }

  return sent;
}

/** The parameters the flow declares required, with no default, that `sent` gives no value. */
export function missingParameters(parameters: readonly DeliveryParameter[], sent: Record<string, RunValue>): DeliveryParameter[] {
  return parameters.filter((p) => p.required && (p.default === null || p.default === undefined) && sent[p.name] === undefined);
}
