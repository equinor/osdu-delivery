import { useMemo, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft, ChevronDown, ChevronRight, CircleAlert, FolderOpen } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { DataTable, type Column } from "@/components/DataTable";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { RunDuration } from "@/components/RunDuration";
import { TruncatedText } from "@/components/TruncatedText";
import { formatBytes } from "@/lib/time";
import {
  DOWNLOAD_RECORD_STATES, downloadApi, type Download, type DownloadEntry, type DownloadFlow, type DownloadRecordState, type DownloadRun,
} from "../../../api/downloads";
import { KindText } from "../KindText";
import { useActivePartition } from "../activePartition";
import { ProblemView } from "../TemplateSheet";
import { DownloadRunPill, RecordCounts } from "./DownloadBadges";
import { DownloadRecordsGrid } from "./DownloadRecordsGrid";
import { REWRITE_TEXT } from "./downloadFormat";

/** How often the tab reads the ledger again, so a run under way shows what it wrote as it lands. */
const REFRESH_MS = 15000;

/** The runs a download's Runs tab lists. */
const RUNS_SHOWN = 50;

/** The address keys the tab keeps what is open in, so a link lands on the same view. */
const OPEN_KEY = "download";
const VIEW_KEY = "downloadView";
const STATE_KEY = "downloadState";

type DownloadTab = "records" | "runs";

const isState = (value: string | null): value is DownloadRecordState => DOWNLOAD_RECORD_STATES.includes(value as DownloadRecordState);

/** The folder a download's files are in: the one its last completed run wrote, else where the flow says a run writes them. */
function folderOf(entry: DownloadEntry, flow: DownloadFlow): { folder: string | undefined; written: boolean } {
  if (entry.download?.location !== undefined) {
    return { folder: entry.download.location, written: true };
  }

  return { folder: flow.location === undefined ? undefined : `${flow.location.replace(/[\\/]+$/, "")}/${entry.name}`, written: false };
}

/**
 * A download flow's Downloads tab (osdu/docs/reference/flow/download.md): each download it declares in the workbench's partition,
 * the kind it reads, its records by state, the bytes its files hold, its last run and its folder; a download's row opens it in
 * place on its records, in a grid that fills the window and is found by id or file name, and on its runs. Downloads the flow no longer
 * declares, which keep the files they wrote, wait behind a toggle. The page's own Trigger run runs the pipeline, and its dialog
 * picks the downloads a run takes and whether it writes every record again, so the tab adds no button of its own for that. The
 * download open, its tab and its state live in the address.
 */
export function DownloadsPanel({ pipelineId }: { pipelineId: string }) {
  const [active] = useActivePartition();
  const [params, setParams] = useSearchParams();
  const [showRetired, setShowRetired] = useState(false);
  const flow = useQuery({
    queryKey: ["delivery", "downloads", "flow", pipelineId, active],
    queryFn: () => downloadApi.flow(pipelineId),
    refetchInterval: REFRESH_MS,
  });
  const entries = useMemo(() => flow.data?.downloads ?? [], [flow.data]);

  // Each change of the address is made from the address as it stands, never from the value drawn, so quick clicks never undo
  // one another.
  const change = (apply: (next: URLSearchParams) => void) => setParams((current) => {
    const next = new URLSearchParams(current);
    apply(next);
    return next;
  }, { replace: false });
  const open = (name: string | null) => change((next) => {
    if (name === null) {
      next.delete(OPEN_KEY);
    } else {
      next.set(OPEN_KEY, name);
    }

    next.delete(VIEW_KEY);
    next.delete(STATE_KEY);
  });

  if (flow.isError) {
    return <ProblemView error={flow.error} testId="download-panel-error" />;
  }

  if (flow.data === undefined) {
    return <Skeleton className="h-48 w-full rounded-lg" />;
  }

  const data = flow.data;
  const openName = params.get(OPEN_KEY);
  const opened = openName === null ? undefined : entries.find((entry) => entry.name.toLowerCase() === openName.toLowerCase());
  if (opened?.download !== undefined) {
    const view: DownloadTab = params.get(VIEW_KEY) === "runs" ? "runs" : "records";
    const asked = params.get(STATE_KEY);
    return (
      <DownloadView
        entry={opened}
        download={opened.download}
        flow={data}
        view={view}
        onView={(next) => change((address) => {
          address.set(VIEW_KEY, next);
        })}
        state={isState(asked) ? asked : null}
        onState={(next) => change((address) => {
          if (next === null) {
            address.delete(STATE_KEY);
          } else {
            address.set(STATE_KEY, next);
          }
        })}
        onBack={() => open(null)}
      />
    );
  }

  const declared = entries.filter((entry) => entry.declared);
  const retired = entries.filter((entry) => !entry.declared);
  const columns: Column<DownloadEntry>[] = [
    {
      id: "download",
      header: "Download",
      render: (row) => {
        // Where its files are, and what it holds, on hover: the folder is the line above's, under the download's name.
        const { folder, written } = folderOf(row, data);
        const where = folder === undefined ? undefined : written ? `Its files are in ${folder}.` : `Not written yet: a run writes its files to ${folder}.`;
        const body = [row.description, where].filter((part): part is string => part !== undefined).join("\n\n");
        return body === ""
          ? <span className="font-medium">{row.name}</span>
          : <RichTooltip title={row.name} body={body}><span className="font-medium" data-testid={`download-panel-name-${row.name}`}>{row.name}</span></RichTooltip>;
      },
    },
    {
      id: "kind",
      header: "Kind",
      fill: true,
      floor: 160,
      render: (row) => (
        <span className="flex min-w-0 items-center gap-1.5">
          <span className="min-w-0"><KindText kind={row.kind} /></span>
          {row.query !== undefined && (
            <RichTooltip title="Narrowed by" body={row.query} mono>
              <span className="shrink-0 rounded-sm border border-border px-1 text-[10.5px] text-muted-foreground">query</span>
            </RichTooltip>
          )}
        </span>
      ),
    },
    {
      id: "records",
      header: "Records",
      render: (row) => (row.download === undefined
        ? <span className="text-[12px] text-muted-foreground">no run yet</span>
        : <RecordCounts counts={row.download.counts} testId={`download-panel-counts-${row.name}`} />),
    },
    {
      id: "size",
      header: "Size",
      align: "right",
      render: (row) => (row.download === undefined
        ? <span className="text-[12px] text-muted-foreground">-</span>
        : <span className="font-mono text-[12px] tabular-nums">{formatBytes(row.download.counts.bytes)}</span>),
    },
    {
      id: "last",
      header: "Last run",
      render: (row) => (row.download?.lastRun === undefined
        ? <span className="text-[12px] text-muted-foreground">none</span>
        : (
          <span className="inline-flex items-center gap-1.5 whitespace-nowrap">
            <DownloadRunPill status={row.download.lastRun.status} error={row.download.lastRun.error} testId={`download-panel-last-${row.name}`} />
            <span className="text-[12px] text-muted-foreground"><RelativeTime value={row.download.lastRun.startedUtc} absolute={false} /></span>
          </span>
        )),
    },
  ];

  const where = data.partition === undefined
    ? "Read in the partition its data-partition-id header names"
    : `In ${data.partition}${data.partitions.length > 1 ? `, one of ${data.partitions.join(", ")}` : ""}`;
  return (
    <div className="flex flex-col gap-3" data-testid="download-panel">
      {data.problem !== undefined && (
        <Alert variant="destructive" data-testid="download-panel-problem">
          <CircleAlert />
          <AlertDescription>{data.problem}</AlertDescription>
        </Alert>
      )}
      <p className="text-[13px] text-muted-foreground" data-testid="download-panel-where">
        {where}{data.read === undefined ? "" : `, listed through ${data.read}`}; each record is written as a JSON file named after its id, in a folder per download
        {data.location === undefined ? "." : <> under <span className="font-mono text-[12px] text-foreground">{data.location}</span>.</>}
      </p>
      <DataTable
        columns={columns}
        rows={declared}
        rowKey={(row) => row.name}
        onRowClick={(row) => {
          if (row.download !== undefined) {
            open(row.name);
          }
        }}
        rowClickable={(row) => row.download !== undefined}
        emptyMessage="The flow declares no download."
        data-testid="download-panel-table"
      />
      {retired.length > 0 && (
        <div className="flex flex-col gap-2" data-testid="download-panel-retired">
          <Button
            variant="ghost"
            size="sm"
            className="self-start text-muted-foreground"
            onClick={() => setShowRetired((shown) => !shown)}
            aria-expanded={showRetired}
            data-testid="download-panel-retired-toggle"
          >
            {showRetired ? <ChevronDown /> : <ChevronRight />}
            No longer declared ({retired.length})
          </Button>
          {showRetired && (
            <DataTable
              columns={columns}
              rows={retired}
              rowKey={(row) => row.name}
              onRowClick={(row) => open(row.name)}
              emptyMessage="Every download kept here is declared."
              data-testid="download-panel-retired-table"
            />
          )}
        </div>
      )}
    </div>
  );
}

/**
 * One download, open in place of the list: a line saying where its files are, what it reads and what its records hold, then its
 * records and its runs as tabs.
 */
function DownloadView({ entry, download, flow, view, onView, state, onState, onBack }: {
  entry: DownloadEntry;
  download: Download;
  flow: DownloadFlow;
  view: DownloadTab;
  onView: (view: DownloadTab) => void;
  state: DownloadRecordState | null;
  onState: (state: DownloadRecordState | null) => void;
  onBack: () => void;
}) {
  const reference = { partition: download.partition, downloadId: download.downloadId };
  const { folder } = folderOf(entry, flow);
  return (
    <div className="flex min-w-0 flex-col gap-2" data-testid="download-view">
      <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1">
        <Button variant="ghost" size="sm" className="-ml-2 text-muted-foreground" onClick={onBack} data-testid="download-view-back">
          <ArrowLeft />
          Downloads
        </Button>
        <span className="text-[14px] font-semibold" data-testid="download-view-name">{download.name}</span>
        {!entry.declared && <span className="rounded-sm border border-border px-1 text-[10.5px] text-muted-foreground">no longer declared</span>}
        <span className="min-w-0"><KindText kind={download.kind} /></span>
        <span className="ml-auto inline-flex min-w-0 items-center gap-1.5 text-[12px] text-muted-foreground">
          <FolderOpen className="size-3.5 shrink-0" aria-hidden />
          <TruncatedText text={folder} mono maxWidth={420} copy copyTestId="download-view-copy-folder" />
        </span>
      </div>
      <Tabs value={view} onValueChange={(next) => onView(next as DownloadTab)} className="min-w-0 gap-2">
        <TabsList variant="line" data-testid="download-tabs">
          <TabsTrigger value="records" data-testid="download-tab-records">Records</TabsTrigger>
          <TabsTrigger value="runs" data-testid="download-tab-runs">Runs</TabsTrigger>
        </TabsList>
        <TabsContent value="records">
          <DownloadRecordsGrid reference={reference} counts={download.counts} state={state} onState={onState} />
        </TabsContent>
        <TabsContent value="runs">
          <DownloadRuns partition={download.partition} downloadId={download.downloadId} />
        </TabsContent>
      </Tabs>
    </div>
  );
}

/** What a run's listing changed of the download, in a line: what it found new, changed, gone and served again. */
function changesText(run: DownloadRun): string {
  const parts = [
    run.added > 0 ? `${run.added.toLocaleString("en-US")} new` : null,
    run.changed > 0 ? `${run.changed.toLocaleString("en-US")} changed` : null,
    run.gone > 0 ? `${run.gone.toLocaleString("en-US")} gone` : null,
    run.returned > 0 ? `${run.returned.toLocaleString("en-US")} served again` : null,
  ].filter((part): part is string => part !== null);
  return parts.length === 0 ? "nothing" : parts.join(", ");
}

/** A download's runs, newest first: how each ended, what its listing changed, the files it wrote and why it wrote every one again. */
function DownloadRuns({ partition, downloadId }: { partition: string; downloadId: number }) {
  const navigate = useNavigate();
  const runs = useQuery({
    queryKey: ["delivery", "downloads", "runs", partition, downloadId],
    queryFn: () => downloadApi.runs({ partition, downloadId }, RUNS_SHOWN),
    refetchInterval: (query) => ((query.state.data ?? []).some((run) => run.status === "running") ? REFRESH_MS / 3 : REFRESH_MS),
  });

  if (runs.isError) {
    return <ProblemView error={runs.error} testId="download-runs-error" />;
  }

  const columns: Column<DownloadRun>[] = [
    { id: "status", header: "Status", render: (row) => <DownloadRunPill status={row.status} error={row.error} testId="download-run-status" /> },
    { id: "started", header: "Started", render: (row) => <span className="whitespace-nowrap text-[12px]"><RelativeTime value={row.startedUtc} absolute={false} /></span> },
    {
      id: "took",
      header: "Took",
      render: (row) => <RunDuration durationSeconds={null} startUtc={row.startedUtc} endUtc={row.completedUtc ?? null} live={row.status === "running"} />,
    },
    { id: "listed", header: "Listed", align: "right", render: (row) => <span className="font-mono text-[12px] tabular-nums">{row.listed.toLocaleString("en-US")}</span> },
    { id: "changes", header: "Listing changed", render: (row) => <span className="text-[12px] text-muted-foreground">{changesText(row)}</span> },
    {
      id: "written",
      header: "Written",
      align: "right",
      render: (row) => (
        <span className="inline-flex items-center justify-end gap-1.5">
          {row.rewrite !== undefined && (
            <RichTooltip title="Every record again" body={REWRITE_TEXT[row.rewrite]}>
              <span className="rounded-sm border border-border px-1 text-[10.5px] text-muted-foreground" data-testid="download-run-rewrite">{row.rewrite}</span>
            </RichTooltip>
          )}
          <span className="font-mono text-[12px] tabular-nums">{row.written.toLocaleString("en-US")}</span>
        </span>
      ),
    },
    { id: "size", header: "Size", align: "right", render: (row) => <span className="font-mono text-[12px] tabular-nums">{formatBytes(row.bytes)}</span> },
    {
      id: "missing",
      header: "Not returned",
      align: "right",
      render: (row) => <span className={row.missing === 0 ? "font-mono text-[12px] text-muted-foreground" : "font-mono text-[12px] tabular-nums"}>{row.missing.toLocaleString("en-US")}</span>,
    },
    { id: "by", header: "By", render: (row) => <TruncatedText text={row.actor} maxWidth={180} /> },
  ];

  return (
    <DataTable
      columns={columns}
      rows={runs.data}
      rowKey={(row) => String(row.downloadRunId)}
      onRowClick={(row) => {
        if (row.runId !== undefined) {
          navigate(`/runs/${row.runId}`);
        }
      }}
      rowClickable={(row) => row.runId !== undefined}
      emptyMessage="No run has written this download yet."
      data-testid="download-runs-table"
    />
  );
}
