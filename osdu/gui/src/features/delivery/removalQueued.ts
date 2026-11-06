// What a page says once a removal is queued as a run of its flow (osdu/docs/reference/concepts/removal-and-reversal.md,
// Removing records from OSDU; osdu/docs/reference/concepts/access-restriction.md): one sentence by its scope, naming how many
// records it acts on when there are several. Pure, so the unit tests run it without a browser.

/** The parts of a queued removal the sentence reads: its scope, whether it deletes from the ledger too, the restriction it sets. */
export interface QueuedRemoval {
  scope: string;
  records: number;
  purgeLedger: boolean;
  restriction?: string | null;
}

/** The sentence a page shows once `queued` is a run: of `records` when `counted` (a selection), of the record when not. */
export function removalQueuedText(queued: QueuedRemoval, counted: boolean): string {
  const of = counted ? ` of ${queued.records.toLocaleString("en-US")} record${queued.records === 1 ? "" : "s"}` : "";
  switch (queued.scope) {
    case "previous":
      return `Restoring the previous version${of}: queued as a run.`;
    case "restrict":
      return `Restricting the access${of}${queued.restriction ? ` under ${queued.restriction}` : ""}: queued as a run.`;
    case "unrestrict":
      return `Lifting the restriction${of}: queued as a run.`;
    default:
      return queued.purgeLedger
        ? `Removal${of}, then deletion from the ledger: queued as a run.`
        : `Removal${of}: queued as a run.`;
  }
}
