// A report (flowType: rpt) is a document of read-only queries a person runs from the report's own page. It is stored as
// a pipeline so its document has one home, and it is no flow: never run, in no wave and no schedule. Everything in the
// GUI that would offer to run a flow tells one apart by this kind. The module imports nothing, so pure models can read
// it as well as the pages.

/** The kind of a report. */
export const REPORT_KIND = "rpt";

/** Whether a pipeline of `kind` is a report: queries run from its own page, which nothing runs as a flow. */
export function isReport(kind: string | null | undefined): boolean {
  return kind === REPORT_KIND;
}

/** Where a report's page is. */
export function reportPath(reportId: string): string {
  return `/reports/${encodeURIComponent(reportId)}`;
}
