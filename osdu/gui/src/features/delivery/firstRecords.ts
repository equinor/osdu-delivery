// A run of the first records of its scope (osdu/docs/reference/cli/run.md, A delivery flow's payload, maxRecords): what the
// run dialog's Records at most field takes, what a run's payload says it asked for, and the words the run's page names it by.

/** The most records a run of the first records of its scope reads (DeliveryRunPayload.MaxRecordKeys). */
export const MAX_FIRST_RECORDS = 1000;

/** What the Records at most field holds: no cap, the count a run reads, or why the text is not one. */
export interface FirstRecordsField {
  count: number | null;
  error: string | null;
}

/** Reads the Records at most field: empty reads every record in scope, else a whole number from 1 to 1,000. */
export function firstRecordsField(text: string): FirstRecordsField {
  const trimmed = text.trim();
  if (trimmed === "") {
    return { count: null, error: null };
  }

  const count = /^\d+$/.test(trimmed) ? Number(trimmed) : Number.NaN;
  return Number.isSafeInteger(count) && count >= 1 && count <= MAX_FIRST_RECORDS
    ? { count, error: null }
    : { count: null, error: `Records at most is a whole number from 1 to ${MAX_FIRST_RECORDS.toLocaleString("en-US")}, or empty for every record in scope.` };
}

/** How many of the first records of its scope a run's payload asked for; null when it asked for none. */
export function firstRecordsOf(payload: Record<string, unknown> | null | undefined): number | null {
  const value = payload?.maxRecords;
  return typeof value === "number" && Number.isInteger(value) && value >= 1 ? value : null;
}

/** The words a run's page names the cap by: "first 100 records of the scope". */
export function firstRecordsText(count: number): string {
  return `first ${count.toLocaleString("en-US")} ${count === 1 ? "record" : "records"} of the scope`;
}
