import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Badge } from "@/components/ui/badge";
import { isApiError } from "@/api/client";
import { deliveryApi, type DeliveryRetrieval } from "../../api/delivery";
import { CorrelationError } from "@/components/CorrelationError";
import { DataTable, type Column } from "@/components/DataTable";
import { KpiCard } from "@/components/KpiCard";
import { RelativeTime } from "@/components/RelativeTime";
import { RunDuration } from "@/components/RunDuration";
import { TruncatedText } from "@/components/TruncatedText";
import { formatBytes } from "@/lib/time";

function RetrievalStatusBadge({ status }: { status: DeliveryRetrieval["status"] }) {
  const variant = status === "done" ? "default" : status === "failed" ? "destructive" : status === "cancelled" ? "secondary" : "outline";
  return <Badge variant={variant} data-testid="retrieval-status">{status}</Badge>;
}

/** The window a run covered, as the query expressed it: everything, or a half-open range on the watermark field. */
function describeWindow(row: DeliveryRetrieval): string {
  if (row.windowField === null) {
    return "everything the query matches";
  }

  return `${row.windowField} [${row.windowFrom ?? "*"} TO ${row.windowTo ?? "*"})`;
}

const columns: Column<DeliveryRetrieval>[] = [
  { id: "status", header: "Status", render: (row) => <RetrievalStatusBadge status={row.status} /> },
  { id: "started", header: "Started", render: (row) => <RelativeTime value={row.startedUtc} /> },
  {
    id: "duration",
    header: "Took",
    render: (row) => (
      <RunDuration durationSeconds={null} startUtc={row.startedUtc} endUtc={row.completedUtc} live={row.status === "running"} />
    ),
  },
  { id: "kinds", header: "Kinds", render: (row) => <TruncatedText text={row.kinds} mono maxWidth={260} /> },
  { id: "window", header: "Window", render: (row) => <TruncatedText text={describeWindow(row)} mono maxWidth={300} /> },
  { id: "records", header: "Records", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.records}</span> },
  { id: "files", header: "Files", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.files}</span> },
  { id: "bytes", header: "Bytes", align: "right", render: (row) => <span className="font-mono tabular-nums">{formatBytes(row.bytes)}</span> },
  { id: "location", header: "Location", render: (row) => <TruncatedText text={row.location} mono maxWidth={280} /> },
  { id: "actor", header: "By", render: (row) => <span>{row.actor}</span> },
  { id: "error", header: "Error", render: (row) => <TruncatedText text={row.error} maxWidth={280} /> },
];

/**
 * A retrieval flow's runs: the ledger row each retrieve run opened and closed, with the window it covered, where
 * its files went and its counts. A row opens the platform run that produced it.
 */
export function RetrievalFlowPanel({ pipelineId }: { pipelineId: string }) {
  const navigate = useNavigate();
  const retrievals = useQuery({
    queryKey: ["delivery", "retrievals", pipelineId],
    queryFn: () => deliveryApi.retrievals(pipelineId, 100),
    refetchInterval: 10000,
  });

  if (retrievals.isError) {
    return isApiError(retrievals.error)
      ? <CorrelationError error={retrievals.error} />
      : <p className="text-[13px] text-destructive">{String(retrievals.error)}</p>;
  }

  const rows = retrievals.data ?? [];
  const latest = rows[0] ?? null;
  const lastDone = rows.find((row) => row.status === "done") ?? null;
  const written = rows.filter((row) => row.status === "done").reduce((sum, row) => sum + row.records, 0);

  return (
    <div className="flex flex-col gap-4" data-testid="retrieval-panel">
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <KpiCard label="Runs" value={rows.length} caption="retrieval runs on record" testId="retrieval-kpi-runs" />
        <KpiCard
          label="Last run"
          value={latest ? latest.status : "none"}
          caption={latest ? new Date(latest.startedUtc).toLocaleString() : "no run yet"}
          testId="retrieval-kpi-last"
        />
        <KpiCard label="Records" value={written} caption="written by the completed runs listed here" testId="retrieval-kpi-records" />
        <KpiCard
          label="Watermark"
          value={lastDone?.windowTo ? new Date(lastDone.windowTo).toLocaleString() : "none"}
          caption={lastDone?.windowField ? `the next run continues from here on ${lastDone.windowField}` : "the flow is not incremental"}
          testId="retrieval-kpi-watermark"
        />
      </div>
      <DataTable
        columns={columns}
        rows={rows}
        rowKey={(row) => String(row.retrievalId)}
        onRowClick={(row) => {
          if (row.runId) {
            navigate(`/runs/${row.runId}`);
          }
        }}
        emptyMessage="No retrieval runs yet. Trigger a run with the retrieve operation."
        data-testid="retrieval-table"
      />
    </div>
  );
}
