// A run of the first records of its scope (osdu/docs/reference/cli/run.md, A delivery flow's payload, maxRecords): what a
// run's payload says it asked for, and the words the run's page names it by. The field a run gives it in is the run
// dialog's Run settings, which the control plane describes and explains (Records at most).

/** How many of the first records of its scope a run's payload asked for, 0 for the whole scope; null when it asked nothing. */
export function firstRecordsOf(payload: Record<string, unknown> | null | undefined): number | null {
  const value = payload?.maxRecords;
  return typeof value === "number" && Number.isInteger(value) && value >= 0 ? value : null;
}

/** The words a run's page names what it asked by: "first 100 records of the scope", or the whole scope for 0. */
export function firstRecordsText(count: number): string {
  return count === 0
    ? "the whole scope (maxRecords 0)"
    : `first ${count.toLocaleString("en-US")} ${count === 1 ? "record" : "records"} of the scope`;
}
