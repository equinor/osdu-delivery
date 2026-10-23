import type { ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { DataTable, type Column } from "@/components/DataTable";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { TruncatedText } from "@/components/TruncatedText";
import { cn } from "@/lib/utils";
import type { RemovalRun } from "../../../api/removals";
import { RemovalRunPill } from "./RemovalBadges";
import { finished, finishedText, instant, leftOver, leftText, readText } from "./removalFormat";

/** A count as data, drawn quiet when it is none, so the counts that need a look stand out. */
function Count({ value, title, body, tone, testId }: { value: number; title: string; body: string; tone?: string; testId: string }) {
  return (
    <RichTooltip title={title} body={body}>
      <span className={cn("font-mono text-[12px] tabular-nums", value === 0 ? "text-muted-foreground/70" : tone)} data-testid={testId} data-count={value}>
        {value.toLocaleString("en-US")}
      </span>
    </RichTooltip>
  );
}

/**
 * Removal runs, newest first: how each ended, when and who ran it, what it read, what it found removed (and held for approval),
 * what it finished and what it left for the next run, and what came back. Every count says what it is made of on hover; a
 * run opens its platform run, with its log and trace. `showRemoval` names the removal flow and removal of each, for a ledger
 * several removals write to.
 */
export function RemovalRunsTable({ runs, showRemoval = false, toolbar, emptyMessage, testId = "removal-runs" }: {
  runs: RemovalRun[] | undefined;
  showRemoval?: boolean;
  /** What stands over the runs, inside the table's surface. */
  toolbar?: ReactNode;
  emptyMessage: string;
  testId?: string;
}) {
  const navigate = useNavigate();
  const columns: Column<RemovalRun>[] = [
    {
      id: "run",
      header: "Run",
      render: (row) => (
        <span className="flex items-center gap-2 whitespace-nowrap">
          <span className="font-mono text-[12px] tabular-nums text-muted-foreground">{`#${row.removalRunId}`}</span>
          <RemovalRunPill status={row.status} error={row.error} testId={`${testId}-status`} />
          {row.operation === "plan" && <span className="rounded-sm border border-border px-1 text-[10.5px] text-muted-foreground">plan</span>}
        </span>
      ),
    },
    ...(showRemoval
      ? [{ id: "removal", header: "Removal", fill: true, floor: 160, render: (row: RemovalRun) => <TruncatedText text={`${row.flowName} / ${row.entry}`} mono maxWidth={260} /> }]
      : []),
    {
      id: "started",
      header: "Started",
      render: (row) => (
        <RichTooltip title={`${row.operation} by ${row.actor}`} body={`${instant(row.startedUtc)}${row.completedUtc === undefined ? ", still running" : `, ended ${instant(row.completedUtc)}`}${row.approvedBy === undefined ? "" : `\nApproval by ${row.approvedBy}`}`} mono>
          <span className="whitespace-nowrap text-[12px]"><RelativeTime value={row.startedUtc} absolute={false} /></span>
        </RichTooltip>
      ),
    },
    {
      id: "read",
      header: "Read",
      align: "right",
      render: (row) => <Count value={row.tally.sourceKeys + row.tally.listedKeys} title="What it read" body={readText(row)} testId={`${testId}-read`} />,
    },
    {
      id: "compared",
      header: "Compared",
      align: "right",
      render: (row) => <Count value={row.tally.compared} title="Compared" body="The ledger's records in the removal's scope, and the record table's keys besides." testId={`${testId}-compared`} />,
    },
    {
      id: "found",
      header: "Found",
      align: "right",
      render: (row) => (
        <span className="inline-flex items-center gap-1.5 whitespace-nowrap">
          <Count value={row.tally.detected} title="Found removed" body="Keys the source no longer holds that the ledger kept no deletion of yet." testId={`${testId}-found`} />
          {row.tally.awaitingApproval > 0 && (
            <RichTooltip title="Held" body="The guard held these for an operator's approval.">
              <span className="rounded-sm border border-warning/40 px-1 font-mono text-[10.5px] tabular-nums text-warning" data-testid={`${testId}-held`}>
                {`${row.tally.awaitingApproval.toLocaleString("en-US")} held`}
              </span>
            </RichTooltip>
          )}
        </span>
      ),
    },
    {
      id: "finished",
      header: "Taken out",
      align: "right",
      render: (row) => <Count value={finished(row.tally)} title="Finished" body={finishedText(row.tally)} testId={`${testId}-finished`} />,
    },
    {
      id: "left",
      header: "Left",
      align: "right",
      render: (row) => <Count value={leftOver(row.tally)} title="Left for the next run" body={leftText(row.tally)} tone="text-warning" testId={`${testId}-left`} />,
    },
    {
      id: "back",
      header: "Back",
      align: "right",
      render: (row) => (
        <Count
          value={row.tally.cameBack + row.tally.lifted}
          title="Came back"
          body={`${row.tally.cameBack.toLocaleString("en-US")} whose row arrived after their removal, so they were not removed\n${row.tally.lifted.toLocaleString("en-US")} deletions lifted: the source holds the key again, or the record was delivered again`}
          testId={`${testId}-back`}
        />
      ),
    },
  ];

  return (
    <DataTable
      toolbar={toolbar}
      columns={columns}
      rows={runs}
      rowKey={(row) => `${row.partition}:${row.removalRunId}`}
      onRowClick={(row) => row.runId !== undefined && navigate(`/runs/${row.runId}`)}
      rowClickable={(row) => row.runId !== undefined}
      emptyMessage={emptyMessage}
      minWidth={640}
      data-testid={`${testId}-table`}
    />
  );
}
