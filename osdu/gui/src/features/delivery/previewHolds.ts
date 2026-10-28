// Whether a delivery would hold a previewed record, and what a failed assertion that holds it says beside the render's own
// holds (osdu/docs/reference/concepts/gui.md, A record's page).

/** What a preview says of the document it rendered, as far as its holds go (DeliveryPreviewDocument). */
interface PreviewedDocument {
  holds: readonly string[];
  assertions?: { held: number; failures: readonly { onFail: string; assertion: string; path: string; at: string }[] } | null;
}

/** Whether a failed assertion of the mapping would hold the record at the check before sending. */
export function heldByAssertion(document: PreviewedDocument): boolean {
  return (document.assertions?.held ?? 0) > 0;
}

/**
 * The failed assertion that would hold the record, in a line of a preview's holds: the first that holds and where its
 * value is, and how many more do. After the render's own holds it is said as one more reason, as the record is held at
 * render then and never reaches the check before sending.
 */
export function assertionHoldText(document: PreviewedDocument): string {
  const findings = document.assertions;
  const held = findings?.held ?? 0;
  const first = findings?.failures.find((failure) => failure.onFail === "hold") ?? null;
  const more = first === null ? held : held - 1;
  const which = first === null
    ? `${held.toLocaleString()} ${held === 1 ? "failure" : "failures"} of an assertion that holds`
    : `"${first.assertion}" fails at ${first.path !== "" ? first.path : first.at}${more > 0 ? `, and ${more.toLocaleString()} more ${more === 1 ? "failure holds" : "failures hold"}` : ""}`;
  return document.holds.length === 0
    ? `The check before sending would hold it, its document kept: ${which}.`
    : `An assertion of the mapping holds it as well: ${which}.`;
}

/** Whether a delivery would hold the record: the render holds it, or a failed assertion of the mapping would. */
export function wouldHold(document: PreviewedDocument): boolean {
  return document.holds.length > 0 || heldByAssertion(document);
}
