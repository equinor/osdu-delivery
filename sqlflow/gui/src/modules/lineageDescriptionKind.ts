// A lineage description (flowType: lin) is a document that says what a step another system runs reads and writes.
// It is metadata: never executed, in no wave and no schedule. Everything in the GUI that would offer to run a flow,
// or that draws the lineage the estate computed, tells one apart by this kind. The module imports nothing, so the
// graph's pure model can read it as well as the pages.

/** The kind of a lineage description. */
export const LINEAGE_DESCRIPTION_KIND = "lin";

/** Whether a flow of `kind` is a lineage description: a step another system runs, which nothing here can run. */
export function isLineageDescription(kind: string | null | undefined): boolean {
  return kind === LINEAGE_DESCRIPTION_KIND;
}
