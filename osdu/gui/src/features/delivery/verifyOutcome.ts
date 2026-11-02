// How a record's verify outcome reads (osdu/docs/reference/flow/delivery.md, verify): a delivered record's `match`,
// `drifted`, `missing` or `error`, and a removed record's `gone` or `served`.

/** A verify outcome as a person reads it: a removed record OSDU still holds reads `still in OSDU`. */
export function verifyOutcomeLabel(outcome: string): string {
  return outcome === "served" ? "still in OSDU" : outcome;
}

/** Whether a verify outcome is what the ledger expects: a delivered record OSDU holds as delivered, a removed one gone from it. */
export function verifyOutcomeAgrees(outcome: string): boolean {
  return outcome === "match" || outcome === "gone";
}
