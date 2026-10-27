import { useMemo } from "react";
import { Link, useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { format } from "date-fns";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { DataTable, type Column } from "@/components/DataTable";
import { EmptyState } from "@/components/EmptyState";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import { parseUtc } from "@/lib/time";
import { cn } from "@/lib/utils";
import { deliveryApi, type DeliveryAssertionMatrix, type DeliveryAssertionRun } from "../../../api/delivery";
import { useActivePartition } from "../activePartition";
import { ProblemView } from "../TemplateSheet";
import { AssertionRunStatusBadge, OutcomeBar, OutcomeSquare } from "./AssertionBadges";
import { AssertionBoard } from "./AssertionBoard";
import { ReportDownloads } from "./AssertionRunDialog";
import { counted, duration, kindEntity, runStatusVisual, selectionText } from "./assertionFormat";

/** How often a flow's tabs read the ledger again, so a run under way shows its results as they land. */
const REFRESH_MS = 15000;

/** The runs the History tab lays out against the tests. */
const MATRIX_RUNS = 30;

/** An assertion flow's Assertions tab: its board in the workbench's partition. */
export function AssertionTestsPanel({ pipelineId }: { pipelineId: string }) {
  const [active] = useActivePartition();
  const board = useQuery({
    queryKey: ["delivery", "assertions", "flow", pipelineId, active],
    queryFn: () => deliveryApi.assertionFlowBoard(pipelineId),
    refetchInterval: REFRESH_MS,
  });

  if (board.isError) {
    return <ProblemView error={board.error} testId="assertion-tests-error" />;
  }

  return board.data === undefined
    ? <Skeleton className="h-64 w-full rounded-lg" />
    : <AssertionBoard board={board.data} scope="flow" />;
}

/** How often a test's outcome changed from one run to the next, among the runs that ran it and did not skip it. */
function flipsOf(cells: DeliveryAssertionMatrix["tests"][number]["cells"]): number {
  const outcomes = cells.filter((cell) => cell !== null && cell.outcome !== "skipped").map((cell) => cell!.outcome);
  let flips = 0;
  for (let i = 1; i < outcomes.length; i++) {
    if (outcomes[i] !== outcomes[i - 1]) {
      flips++;
    }
  }

  return flips;
}

/**
 * An assertion flow's History tab: its tests against its recent runs, oldest on the left, so a test that broke, when it
 * broke, and one that keeps flipping between passing and failing are seen at once. A column opens its run's report.
 */
export function AssertionHistoryPanel({ pipelineId }: { pipelineId: string }) {
  const navigate = useNavigate();
  const [active] = useActivePartition();
  const matrix = useQuery({
    queryKey: ["delivery", "assertions", "matrix", pipelineId, active],
    queryFn: () => deliveryApi.assertionMatrix(pipelineId, MATRIX_RUNS),
    refetchInterval: REFRESH_MS,
  });
  const laid = useMemo(() => {
    if (matrix.data === undefined) {
      return undefined;
    }

    // The control plane serves the runs newest first; the grid reads left to right in time.
    const runs = [...matrix.data.runs].reverse();
    const tests = matrix.data.tests.map((row) => {
      const cells = [...row.cells].reverse();
      const ran = cells.filter((cell) => cell !== null && cell.outcome !== "skipped");
      return {
        ...row,
        cells,
        flips: flipsOf(cells),
        passRate: ran.length === 0 ? null : Math.round((100 * ran.filter((cell) => cell!.outcome === "passed").length) / ran.length),
      };
    });
    return { runs, tests };
  }, [matrix.data]);

  if (matrix.isError) {
    return <ProblemView error={matrix.error} testId="assertion-history-error" />;
  }

  if (laid === undefined) {
    return <Skeleton className="h-64 w-full rounded-lg" />;
  }

  if (laid.runs.length === 0) {
    return (
      <EmptyState
        title="No run yet"
        description="Each run of the flow's assertions adds a column here, so an assertion that breaks, and one that keeps flipping, shows at once."
        data-testid="assertion-history-empty"
      />
    );
  }

  return (
    <div className="flex flex-col gap-3" data-testid="assertion-history">
      <div className="overflow-x-auto rounded-lg border">
        <table className="text-[12px]">
          <thead>
            <tr className="border-b bg-muted/40">
              <th className="sticky left-0 z-10 min-w-56 bg-muted/40 px-3 py-2 text-left text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Assertion</th>
              <th className="px-2 py-2 text-right text-[11px] font-medium uppercase tracking-wider text-muted-foreground" title="Runs it passed, of those that evaluated it">Pass</th>
              <th className="px-2 py-2 text-right text-[11px] font-medium uppercase tracking-wider text-muted-foreground" title="How often its outcome changed from one run to the next">Flips</th>
              {laid.runs.map((run) => {
                const visual = runStatusVisual(run.status);
                const Icon = visual.icon;
                return (
                  <th key={run.assertionRunId} className="px-[3px] py-2">
                    <Tooltip>
                      <TooltipTrigger asChild>
                        <button
                          type="button"
                          onClick={() => navigate(`/delivery/assertions/runs/${run.assertionRunId}`)}
                          className="flex flex-col items-center gap-0.5 rounded px-0.5 hover:bg-muted"
                          aria-label={`report ${run.assertionRunId}, ${visual.label}`}
                          data-testid="assertion-history-run"
                        >
                          <Icon className={cn("size-3.5", visual.tone === "success" ? "text-success" : visual.tone === "destructive" ? "text-destructive" : "text-muted-foreground")} />
                        </button>
                      </TooltipTrigger>
                      <TooltipContent>
                        {`#${run.assertionRunId}, ${visual.label}, ${format(parseUtc(run.startedUtc), "yyyy-MM-dd HH:mm")}, ${selectionText(run.selection)}`}
                      </TooltipContent>
                    </Tooltip>
                  </th>
                );
              })}
            </tr>
          </thead>
          <tbody>
            {laid.tests.map((row) => (
              <tr key={row.test} className="border-b last:border-b-0 hover:bg-muted/30" data-testid={`assertion-history-test-${row.test}`}>
                <td className="sticky left-0 z-10 bg-card px-3 py-1.5">
                  <Link to={`?tab=tests&test=${encodeURIComponent(row.test)}`} className="block truncate font-mono text-[12px] hover:underline">{row.test}</Link>
                  <span className="block truncate text-[10.5px] text-muted-foreground">{kindEntity(row.kind)}</span>
                </td>
                <td className="px-2 py-1.5 text-right font-mono tabular-nums">{row.passRate === null ? "-" : `${row.passRate}%`}</td>
                <td className={cn("px-2 py-1.5 text-right font-mono tabular-nums", row.flips >= 3 && "font-semibold text-warning")} title={row.flips >= 3 ? "It keeps changing: an assertion that flips is either unsteady data or an unsteady assertion." : undefined}>
                  {row.flips}
                </td>
                {row.cells.map((cell, index) => (
                  <td key={laid.runs[index]!.assertionRunId} className="px-[3px] py-1.5 text-center">
                    {cell === null
                      ? <span className="inline-block size-3.5 rounded-[2px] border border-dashed border-border align-middle" title="not run in this run" />
                      : (
                        <span className="inline-flex align-middle">
                          <OutcomeSquare
                            outcome={cell.outcome}
                            size="size-3.5"
                            title={`#${cell.assertionRunId}${cell.failedAssertions > 0 ? `, ${counted(cell.failedAssertions, "failed check")}` : ""}${cell.matched !== null ? `, ${cell.matched.toLocaleString("en-US")} matched` : ""}`}
                            onClick={() => navigate(`/delivery/assertions/runs/${cell.assertionRunId}`)}
                            testId="assertion-history-cell"
                          />
                        </span>
                      )}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="text-[12px] text-muted-foreground">
        The last {laid.runs.length} run{laid.runs.length === 1 ? "" : "s"} in this partition, oldest on the left. A dashed square is a run
        that did not run the assertion; an assertion with three or more flips is marked, since it keeps changing.
      </p>
    </div>
  );
}

const runColumns: Column<DeliveryAssertionRun>[] = [
  {
    id: "report",
    header: "Report",
    render: (row) => <span className="font-mono text-[12px]">#{row.assertionRunId}</span>,
  },
  { id: "status", header: "Status", render: (row) => <AssertionRunStatusBadge status={row.status} /> },
  { id: "started", header: "Started", render: (row) => <RelativeTime value={row.startedUtc} /> },
  {
    id: "took",
    header: "Took",
    align: "right",
    render: (row) => (
      <span className="font-mono tabular-nums">
        {row.completedUtc === null ? "-" : duration(parseUtc(row.completedUtc).getTime() - parseUtc(row.startedUtc).getTime())}
      </span>
    ),
  },
  { id: "ran", header: "Ran", fill: true, floor: 140, render: (row) => <TruncatedText text={selectionText(row.selection)} mono /> },
  {
    id: "outcomes",
    header: "Outcomes",
    width: 200,
    render: (row) => (
      <div className="flex flex-col gap-1">
        <OutcomeBar counts={row} />
        <span className="font-mono text-[11px] tabular-nums text-muted-foreground">
          {`${row.passed} of ${row.tests} passed${row.failed > 0 ? `, ${row.failed} failed` : ""}${row.warned > 0 ? `, ${row.warned} warned` : ""}${row.errored > 0 ? `, ${row.errored} errored` : ""}`}
        </span>
      </div>
    ),
  },
  { id: "actor", header: "By", render: (row) => <TruncatedText text={row.actor} maxWidth={180} /> },
  {
    id: "download",
    header: "",
    align: "right",
    render: (row) => (
      <span onClick={(event) => event.stopPropagation()}>
        <ReportDownloads assertionRunId={row.assertionRunId} flowName={row.flowName} partition={row.partition} size="icon-sm" testId={`report-downloads-${row.assertionRunId}`} />
      </span>
    ),
  },
];

/** An assertion flow's Reports tab: every run of its tests in the workbench's partition, each opening its report. */
export function AssertionReportsPanel({ pipelineId }: { pipelineId: string }) {
  const navigate = useNavigate();
  const [active] = useActivePartition();
  const runs = useQuery({
    queryKey: ["delivery", "assertions", "runs", pipelineId, active],
    queryFn: () => deliveryApi.assertionRuns(pipelineId, 200),
    refetchInterval: REFRESH_MS,
  });

  if (runs.isError) {
    return <ProblemView error={runs.error} testId="assertion-reports-error" />;
  }

  return (
    <DataTable
      columns={runColumns}
      rows={runs.data}
      rowKey={(row) => row.assertionRunId}
      onRowClick={(row) => navigate(`/delivery/assertions/runs/${row.assertionRunId}`)}
      emptyMessage="No report yet. Each run of the flow's assertions keeps one: run them from the Assertions tab."
      data-testid="assertion-reports"
    />
  );
}
