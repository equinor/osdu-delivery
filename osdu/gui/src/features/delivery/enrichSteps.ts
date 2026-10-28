// What each enrichment of a mapping did to a record (osdu/docs/reference/flow/enrich.md), as the record's Render tab and the
// flow's Preview tab say it: one phrase per enrichment, and each value it wrote shortened to fit a line.

/** One enrichment as a preview answers it; the fields a preview leaves out when they hold nothing are optional. */
export interface EnrichStepOutcome {
  /** How many rows of its dataset the record had; absent for one that reads no rows. */
  rows?: number;
  effects: readonly unknown[];
  holds: readonly string[];
  notRendered?: boolean;
  setAside?: boolean;
}

/** What the enrichment came to for the record, as one phrase: not rendered, holding it, adding nothing, or how many changes. */
export function enrichOutcomeText(step: EnrichStepOutcome): string {
  if (step.notRendered) {
    return "not rendered: an enrichment before it holds the record or waits for the platform";
  }

  if (step.setAside) {
    return "held, set aside: keeps what it last added";
  }

  if (step.holds.length > 0) {
    return step.holds.length === 1 ? "holds the record" : `holds the record (${step.holds.length} reasons)`;
  }

  const rows = step.rows === undefined ? "" : `${step.rows.toLocaleString("en-US")} ${step.rows === 1 ? "row" : "rows"}, `;
  const changes = step.effects.length;
  return changes === 0 ? `${rows}adds nothing` : `${rows}${changes.toLocaleString("en-US")} ${changes === 1 ? "change" : "changes"}`;
}

/**
 * A value a step wrote, as one line: text as it is, anything else as its JSON, cut at <paramref name="max"/> characters with
 * an ellipsis; null when the preview left the values out because the record was too large to show.
 */
export function effectValueText(value: unknown, max = 120): string | null {
  if (value === undefined) {
    return null;
  }

  const text = typeof value === "string" ? value : JSON.stringify(value);
  return text.length <= max ? text : `${text.slice(0, max - 1)}…`;
}

/** What an enrichment does beside adding paths, as the enrichments table names it: the records it adds, the record's bulk data, its workflow. */
export function suppliesText(supplies: readonly string[] | undefined): string | null {
  const names = (supplies ?? []).map((supply) => (supply === "records" ? "records" : supply === "bulk" ? "bulk data" : supply === "workflow" ? "workflow" : supply));
  return names.length === 0 ? null : names.join(", ");
}
