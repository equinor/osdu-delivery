import { useNavigate } from "react-router-dom";
import { Badge } from "@/components/ui/badge";
import type { SearchCategory, SearchHit } from "@/api/types";
import { DataTable, type Column } from "@/components/DataTable";
import { Mono } from "@/components/Mono";
import { RelativeTime } from "@/components/RelativeTime";
import { RunDuration } from "@/components/RunDuration";
import { RunStatusBadge } from "@/components/StatusBadge";
import type { DeliveryRunHit, DeliveryRunLookupRun } from "../../api/delivery";
import { shortId } from "./idTail";

function isRunHit(value: unknown): value is DeliveryRunHit {
  return value !== null && typeof value === "object" && "asked" in value && "run" in value && "reason" in value;
}

/** The run hit a contributed search hit carries in its `data`; null for a hit that carries none. */
function hitOf(item: unknown): DeliveryRunHit | null {
  if (item === null || typeof item !== "object") {
    return null;
  }

  const data = (item as Partial<SearchHit>).data;
  return isRunHit(data) ? data : null;
}

/** Where a run stands in its execution: a part of a split delivery, or the run that coordinates its parts. */
function placeOf(run: DeliveryRunLookupRun, split: boolean): string {
  if (run.fanOutRoot === null) {
    return split ? "Coordinates" : "-";
  }

  return run.fanOutSlot === null ? "Part" : `Part ${run.fanOutSlot} of ${run.fanOutCount ?? "?"}`;
}

interface RunRow extends DeliveryRunLookupRun {
  asked: boolean;
}

function columnsOf(split: boolean): Column<RunRow>[] {
  return [
    {
      id: "run",
      header: "Run",
      render: (row) => (
        <span className="flex items-center gap-2">
          <span title={row.runId}><Mono>{shortId(row.runId)}</Mono></span>
          {split && row.asked && <Badge variant="secondary">This id</Badge>}
        </span>
      ),
    },
    ...(split ? [{ id: "place", header: "Part", render: (row: RunRow) => placeOf(row, split) }] : []),
    { id: "status", header: "Status", render: (row) => <RunStatusBadge status={row.status} /> },
    { id: "outcome", header: "Outcome", render: (row) => row.outcome ?? <span className="text-muted-foreground">-</span> },
    { id: "started", header: "Started", render: (row) => <RelativeTime value={row.startUtc ?? row.enqueuedUtc} /> },
    {
      id: "duration",
      header: "Duration",
      render: (row) => (
        <RunDuration durationSeconds={row.durationSeconds} startUtc={row.startUtc} endUtc={row.endUtc} enqueuedUtc={row.enqueuedUtc} />
      ),
    },
  ];
}

/**
 * The run a search names by its id or a correlation id copied from a platform's log, with every run of its execution: a
 * delivery split over nodes sends under one id per part, so any of them lists them all. An id no run has says what that means.
 */
export function RunSearchHits({ category }: { category: SearchCategory<unknown> }) {
  const navigate = useNavigate();
  const hits = category.items.map(hitOf).filter((hit): hit is DeliveryRunHit => hit !== null);
  const rows = hits.flatMap((hit) => (hit.run === null ? [] : [{ ...hit.run, asked: hit.asked }]));
  const reason = hits.find((hit) => hit.reason !== null)?.reason ?? null;
  if (reason !== null) {
    return (
      <section data-testid="search-runs">
        <p className="text-[13px] text-muted-foreground" data-testid="search-runs-none">{reason}</p>
      </section>
    );
  }

  if (rows.length === 0) {
    return null;
  }

  const split = rows.length > 1;
  return (
    <section className="flex flex-col gap-2" data-testid="search-runs">
      <h2 className="text-[13px] font-medium">
        {split ? `${rows.length} runs of one execution of ` : "1 run of "}
        <Mono>{rows[0].flowName}</Mono>
      </h2>
      <DataTable
        columns={columnsOf(split)}
        rows={rows}
        rowKey={(row) => row.runId}
        onRowClick={(row) => navigate(`/runs/${row.runId}`)}
        emptyMessage="No runs match."
        data-testid="search-runs-table"
      />
    </section>
  );
}
