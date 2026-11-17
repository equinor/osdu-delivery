// How one answer on a report's page was computed: the statement as the document holds it, the values it was bound with,
// where it ran, how many rows it answered and how long it took. Everything a reader needs to trust a figure, or to
// question it, without leaving the page.

import { Link as RouterLink } from "react-router-dom";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { CodeView } from "@/components/CodeView";
import { ConnectionRef } from "@/components/ConnectionRef";
import { DetailPair } from "@/components/DetailPair";
import { IdChip } from "@/components/IdChip";
import { RelativeTime } from "@/components/RelativeTime";
import type { Report, ReportParameter } from "../../api/types";
import { parseUtc } from "../../lib/time";
import { effectiveValue, formatDuration, parameterLabel, queryTitle, type GivenValues } from "./reportModel";
import type { QueryState } from "./useReportQueries";

interface ReportQuerySheetProps {
  report: Report;
  /** The query to show, or null while the sheet is closed. */
  state: QueryState | null;
  given: GivenValues;
  onClose: () => void;
}

function seconds(from: string | null, to: string | null): number | null {
  if (from === null || to === null) return null;
  return Math.max(0, (parseUtc(to).getTime() - parseUtc(from).getTime()) / 1000);
}

export function ReportQuerySheet({ report, state, given, onClose }: ReportQuerySheetProps) {
  const query = state?.query ?? null;
  const task = state?.run?.task ?? null;
  const answer = state?.answer ?? null;

  // The values the shown answer was bound with, as the control plane acknowledged them; before an answer, the values
  // the query would be bound with now.
  const bound: { name: string; type: string; value: string | null }[] = state?.run?.accepted.parameters
    ?? (query?.parameters ?? [])
      .map((name) => report.parameters.find((parameter) => parameter.name.toLowerCase() === name.toLowerCase()))
      .filter((parameter): parameter is ReportParameter => parameter !== undefined)
      .map((parameter) => ({ name: parameter.name, type: parameter.type, value: effectiveValue(parameter, given) }));

  const ran = seconds(task?.startUtc ?? null, task?.endUtc ?? null);
  const queued = seconds(task?.enqueuedUtc ?? null, task?.startUtc ?? null);

  return (
    <Sheet open={state !== null} onOpenChange={(open) => { if (!open) onClose(); }}>
      <SheetContent
        side="right"
        className="w-full gap-0 sm:max-w-2xl"
        data-testid="report-query-sheet"
        // The sheet is read, not filled in: focus stays off its first button, whose hover text would cover a caption.
        onOpenAutoFocus={(event) => event.preventDefault()}
      >
        <SheetHeader>
          <SheetTitle className="text-sm">{query === null ? "Query" : queryTitle(query)}</SheetTitle>
          <SheetDescription>
            How this answer was computed: the statement the report's document holds, and what it ran with.
          </SheetDescription>
        </SheetHeader>

        {query !== null && state !== null && (
          <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto px-4 pb-6">
            <div className="grid grid-cols-2 gap-x-6 gap-y-3 sm:grid-cols-3">
              <DetailPair label="Connection"><ConnectionRef value={query.connection} copyTestId="copy-report-connection" /></DetailPair>
              <DetailPair label="Database">
                <span className="font-mono text-xs">{answer?.database ?? query.database ?? "the connection's own"}</span>
              </DetailPair>
              <DetailPair label="Rows">
                <span className="font-mono text-xs tabular-nums" data-testid="report-query-rows">
                  {answer === null ? "-" : answer.rowCount.toLocaleString()}
                  {answer?.truncated === true && " (cut at the limit)"}
                </span>
              </DetailPair>
              <DetailPair label="Row limit"><span className="font-mono text-xs tabular-nums">{query.maxRows.toLocaleString()}</span></DetailPair>
              <DetailPair label="May run for"><span className="font-mono text-xs">{formatDuration(query.timeoutSeconds)}</span></DetailPair>
              <DetailPair label="Took">
                <span className="font-mono text-xs" data-testid="report-query-took">
                  {ran === null ? "-" : formatDuration(ran)}
                  {queued !== null && queued >= 1 && `, after ${formatDuration(queued)} waiting for a worker`}
                </span>
              </DetailPair>
              {task !== null && (
                <>
                  <DetailPair label="Asked by"><span className="text-[13px]">{task.requestedBy ?? "-"}</span></DetailPair>
                  <DetailPair label="Asked"><RelativeTime value={task.enqueuedUtc} absolute /></DetailPair>
                  <DetailPair label="Task"><IdChip label="task" value={task.taskId} copyTestId="copy-report-task" /></DetailPair>
                </>
              )}
            </div>

            {state.status === "blocked" && (
              <p className="text-[13px] text-warning" data-testid="report-query-problem">{state.problem}</p>
            )}
            {state.status === "failed" && state.failure !== null && (
              <p className="whitespace-pre-wrap break-words font-mono text-xs text-destructive" data-testid="report-query-failure">
                {state.failure.message}
              </p>
            )}

            <section className="flex flex-col gap-1.5">
              <h3 className="text-[13px] font-medium">Values bound</h3>
              {bound.length === 0
                ? <p className="text-[13px] text-muted-foreground">The statement binds no value: it reads the same whatever the filters are set to.</p>
                : (
                  <ul className="flex flex-col gap-0.5" data-testid="report-query-values">
                    {bound.map((value) => {
                      const parameter = report.parameters.find((candidate) => candidate.name.toLowerCase() === value.name.toLowerCase());
                      return (
                        <li key={value.name} className="flex items-baseline gap-2 text-[13px]">
                          <span className="font-mono text-xs text-muted-foreground">@{value.name}</span>
                          {value.value === null
                            ? <span className="italic text-muted-foreground">NULL</span>
                            : <span className="break-all font-mono text-xs">{value.value}</span>}
                          <span className="text-xs text-muted-foreground">
                            {value.type}{parameter !== undefined && `, ${parameterLabel(parameter)}`}
                          </span>
                        </li>
                      );
                    })}
                  </ul>
                )}
            </section>

            <section className="flex min-h-0 flex-col gap-1.5">
              <h3 className="text-[13px] font-medium">Statement</h3>
              <CodeView value={query.sql} language="sql" height={320} explain={false} data-testid="report-query-sql" />
              <p className="text-xs text-muted-foreground">
                Read-only: it runs as one SELECT in a transaction that is always rolled back. It is written in{" "}
                <RouterLink to={`/pipelines/${report.id}?tab=yaml`} className="underline underline-offset-2" data-testid="report-query-document">
                  the report's document
                </RouterLink>
                {" "}({report.relativePath}).
              </p>
            </section>
          </div>
        )}
      </SheetContent>
    </Sheet>
  );
}
