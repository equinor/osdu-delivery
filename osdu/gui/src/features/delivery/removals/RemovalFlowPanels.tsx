import { useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowRight, CircleAlert, ShieldCheck } from "lucide-react";
import { toast } from "sonner";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { DataTable, type Column } from "@/components/DataTable";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { removalApi, type RemovalEntry } from "../../../api/removals";
import { useActivePartition } from "../activePartition";
import { ProblemView } from "../TemplateSheet";
import { DeletionCounts, RemovalRunPill } from "./RemovalBadges";
import { ApproveDialog } from "./RemovalDialogs";
import { deletionsRoute, guardLine, ledgerName, removalRoute } from "./removalFormat";

/** How often the tab reads the ledgers again, so a run under way shows what it found as it lands. */
const REFRESH_MS = 15000;

/**
 * A removal flow's Removals tab (osdu/docs/reference/flow/removal.md): in the workbench's partition, each removal it declares
 * with the delivery flow whose ledger it writes to, that ledger's deletions by state, and the removal's last run; a removal
 * whose guard held what it found offers its approval in its row. A removal opens on the Removals page, where its deletions
 * and runs fill the window. The page's own Trigger run runs the flow, so the tab adds no button of its own for that.
 */
export function RemovalsPanel({ pipelineId }: { pipelineId: string }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [active] = useActivePartition();
  const [approving, setApproving] = useState<RemovalEntry | null>(null);
  const flow = useQuery({
    queryKey: ["delivery", "removals", "flow", pipelineId, active],
    queryFn: () => removalApi.flow(pipelineId),
    refetchInterval: REFRESH_MS,
  });

  if (flow.isError) {
    return <ProblemView error={flow.error} testId="removal-panel-error" />;
  }

  if (flow.data === undefined) {
    return <Skeleton className="h-48 w-full rounded-lg" />;
  }

  const data = flow.data;
  const columns: Column<RemovalEntry>[] = [
    { id: "removal", header: "Removal", render: (row) => <span className="font-medium">{row.name}</span> },
    {
      id: "flow",
      header: "Delivery flow",
      fill: true,
      floor: 180,
      render: (row) => {
        const route = deletionsRoute(row, data.partition);
        return route === null
          ? <span className="min-w-0 truncate font-mono text-[12px]">{ledgerName(row)}</span>
          : (
            <Link to={route} onClick={(event) => event.stopPropagation()} className="min-w-0 truncate font-mono text-[12px] text-primary hover:underline">
              {ledgerName(row)}
            </Link>
          );
      },
    },
    {
      id: "deletions",
      header: "Deletions",
      render: (row) => (row.ledgerFlowId === undefined
        ? (
          <RichTooltip title="No ledger yet" body={`${ledgerName(row)} has delivered nothing in ${data.partition ?? "this partition"}, so there is nothing to compare.`}>
            <span className="text-[12px] text-muted-foreground">no ledger</span>
          </RichTooltip>
        )
        : <DeletionCounts counts={row.deletions} testId={`removal-panel-counts-${row.name}`} />),
    },
    {
      id: "last",
      header: "Last run",
      render: (row) => (row.lastRun === undefined
        ? <span className="text-[12px] text-muted-foreground">never run</span>
        : (
          <span className="flex items-center gap-2 whitespace-nowrap">
            <RemovalRunPill status={row.lastRun.status} error={row.lastRun.error} testId={`removal-panel-last-${row.name}`} />
            <span className="text-[12px] text-muted-foreground"><RelativeTime value={row.lastRun.startedUtc} absolute={false} /></span>
          </span>
        )),
    },
    {
      id: "actions",
      header: "",
      align: "right",
      render: (row) => (
        <span className="inline-flex items-center gap-1">
          {row.awaitingApproval > 0 && data.partition !== undefined && (
            <Button
              variant="outline"
              size="xs"
              className="border-warning/60"
              onClick={(event) => { event.stopPropagation(); setApproving(row); }}
              title={`${row.awaitingApproval.toLocaleString("en-US")} removed keys the guard held wait for approval`}
              data-testid={`removal-panel-approve-${row.name}`}
            >
              <ShieldCheck className="text-warning" />
              {`Approve ${row.awaitingApproval.toLocaleString("en-US")}`}
            </Button>
          )}
          <Button asChild variant="ghost" size="xs" data-testid={`removal-panel-open-${row.name}`}>
            <Link to={removalRoute(data.pipelineId, row.name)} onClick={(event) => event.stopPropagation()}>
              Open
              <ArrowRight />
            </Link>
          </Button>
        </span>
      ),
    },
  ];

  return (
    <div className="flex min-w-0 flex-col gap-3" data-testid="removal-panel">
      {data.problem !== undefined && (
        <Alert variant="destructive" data-testid="removal-panel-problem">
          <CircleAlert />
          <AlertDescription>{data.problem}</AlertDescription>
        </Alert>
      )}
      {data.partition !== undefined && (
        <p className="text-[13px] text-muted-foreground" data-testid="removal-panel-where">
          {`In ${data.partition}${data.partitions.length > 1 ? `, one of ${data.partitions.join(", ")}` : ""}: ${guardLine(data)}.`}
        </p>
      )}
      <DataTable
        columns={columns}
        rows={data.removals}
        rowKey={(row) => row.name}
        onRowClick={(row) => navigate(removalRoute(data.pipelineId, row.name))}
        emptyMessage="The flow declares no removal."
        data-testid="removal-panel-table"
      />
      {approving !== null && data.partition !== undefined && (
        <ApproveDialog
          open
          onClose={() => setApproving(null)}
          pipelineId={data.pipelineId}
          partition={data.partition}
          entry={approving}
          run={approving.lastRun?.status === "guarded" ? approving.lastRun : undefined}
          onReview={() => navigate(`${removalRoute(data.pipelineId, approving.name)}&state=awaiting-approval`)}
          onQueued={(accepted) => {
            toast.success(
              `Approval of ${accepted.expected.toLocaleString("en-US")} removed key${accepted.expected === 1 ? "" : "s"} queued as a run; it takes them out of OSDU.`,
              { action: { label: "Open run", onClick: () => navigate(`/runs/${accepted.runId}`) } });
            void queryClient.invalidateQueries({ queryKey: ["delivery", "removals"] });
          }}
        />
      )}
    </div>
  );
}
