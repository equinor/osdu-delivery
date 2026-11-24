import { useState, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "react-router-dom";
import { X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import { runApi } from "../../api/endpoints";
import type { StreamPath, StreamPathFire, StreamPathNode, StreamPathRun } from "../../api/types";
import { CopyButton } from "../../components/CopyButton";
import { CorrelationError } from "../../components/CorrelationError";
import { LineageJumpButton } from "../../components/LineageJumpButton";
import { RelativeTime } from "../../components/RelativeTime";
import { formatBytes, formatDurationSeconds } from "../../lib/time";
import { flowKindIdentity, flowKindToneClasses } from "../../modules/flowKinds";
import { kindContribution } from "../../modules/registry";
import { kindLook } from "../lineage/graphKinds";
import { StreamDetailBody } from "./StreamDetailBody";
import { formatCount, nodeLabel, omissionText, runOutcome, runsByFlow, type RunOutcome } from "./streamPathModel";

const FILES_SHOWN = 50;
const NAMES_SHOWN = 8;

/** A section of the column under a quiet heading, with a count when it lists things. */
function Section({ title, count, children, testId }: { title: string; count?: number; children: ReactNode; testId?: string }) {
  return (
    <section className="flex flex-col gap-1" data-testid={testId}>
      <h4 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
        {title}
        {count !== undefined && <span className="ml-1.5 tabular-nums">{count}</span>}
      </h4>
      {children}
    </section>
  );
}

/** A caption and its value, one per row of the column. */
function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex items-baseline justify-between gap-3 py-0.5">
      <span className="shrink-0 text-[12px] text-muted-foreground">{label}</span>
      <span className="min-w-0 text-right font-mono text-[12px] tabular-nums">{children}</span>
    </div>
  );
}

const OUTCOME_WORDS: Record<RunOutcome, string> = {
  moved: "Succeeded",
  empty: "Succeeded, wrote nothing",
  failed: "Failed",
  running: "Running",
  queued: "Queued",
  cancelled: "Cancelled",
  absent: "Not in this fire",
};

/** A list of names that opens in full on request, for what a node stands for without drawing it. */
function NameList({ names, count }: { names: string[]; count: number }) {
  const [all, setAll] = useState(false);
  const shown = all ? names : names.slice(0, NAMES_SHOWN);
  return (
    <>
      <ul className="flex flex-col">
        {shown.map((name) => (
          <li key={name} className="truncate font-mono text-[12px] leading-5" title={name}>{name}</li>
        ))}
      </ul>
      {(names.length > NAMES_SHOWN || count > names.length) && (
        <div className="flex items-center gap-2 text-[11px] text-muted-foreground">
          {names.length > NAMES_SHOWN && (
            <Button variant="ghost" size="xs" className="px-1" onClick={() => setAll(!all)}>
              {all ? "Show fewer" : `Show all ${names.length}`}
            </Button>
          )}
          {count > names.length && <span>{count - names.length} more not listed</span>}
        </div>
      )}
    </>
  );
}

/** The files a run took in: what the delivery brought, by name, with its rows and size. */
function RunFiles({ runId, total }: { runId: string; total: number }) {
  const query = useQuery({
    queryKey: ["runs", runId, "files", FILES_SHOWN],
    queryFn: () => runApi.files(runId, { pageSize: FILES_SHOWN }),
  });

  if (query.isError) {
    return isApiError(query.error)
      ? <CorrelationError error={query.error} />
      : <p className="text-[12px] text-destructive">{String(query.error)}</p>;
  }

  if (query.data === undefined) {
    return <Skeleton className="h-16 rounded-md" />;
  }

  return (
    <>
      <ul className="flex flex-col" data-testid="stream-path-files">
        {query.data.items.map((file) => (
          <li key={file.id} className="flex items-baseline gap-2 border-b border-border/50 py-1 last:border-b-0">
            <span className="min-w-0 flex-1 truncate font-mono text-[12px]" title={file.path ?? file.name}>{file.name}</span>
            <span className="shrink-0 font-mono text-[11px] tabular-nums text-muted-foreground">
              {formatCount(file.rows)} rows · {formatBytes(file.sizeBytes)}
            </span>
          </li>
        ))}
      </ul>
      {total > query.data.items.length && (
        <p className="text-[11px] text-muted-foreground">
          {total - query.data.items.length} more on the run's page.
        </p>
      )}
    </>
  );
}

/** What one flow's run did in the fire: its outcome, its figures, its error whole, and the files it took in. */
function FireRun({ entry }: { entry: StreamPathRun }) {
  const navigate = useNavigate();
  const run = entry.run;
  const outcome = runOutcome(run);
  const counts = kindContribution(run.flowKind)?.runCountColumns;
  const started = run.startUtc ?? run.enqueuedUtc ?? run.writtenUtc;
  return (
    <div className="flex flex-col gap-1" data-testid="stream-path-fire-run">
      <Fact label="Outcome">
        <span className={cn(outcome === "failed" && "text-destructive")}>{OUTCOME_WORDS[outcome]}</span>
      </Fact>
      <Fact label="Started"><RelativeTime value={started} /></Fact>
      {run.durationSeconds !== null && <Fact label="Took">{formatDurationSeconds(run.durationSeconds)}</Fact>}
      {entry.parts > 0 && <Fact label="Split over">{entry.parts} parts</Fact>}
      {(run.fullLoad || run.backfillFrom !== null || run.backfillTo !== null) && (
        <Fact label="Scope">{run.fullLoad ? "full load" : "backfill window"}</Fact>
      )}
      {counts !== undefined && counts.length > 0
        ? counts.map((column) => <Fact key={column.id} label={column.header}>{column.render(run)}</Fact>)
        : (
          <>
            {run.rowsLoaded !== null && <Fact label="Rows in">{formatCount(run.rowsLoaded)}</Fact>}
            {run.rowsInserted !== null && <Fact label="New">{formatCount(run.rowsInserted)}</Fact>}
            {run.rowsUpdated !== null && <Fact label="Changed">{formatCount(run.rowsUpdated)}</Fact>}
            {run.rowsDeleted !== null && <Fact label="Removed">{formatCount(run.rowsDeleted)}</Fact>}
          </>
        )}
      {entry.files !== null && (
        <>
          <Fact label="Files">{formatCount(entry.files.files)} · {formatBytes(entry.files.bytes)}</Fact>
          {entry.files.newestModified !== null && (
            <Fact label="Newest file"><RelativeTime value={entry.files.newestModified} /></Fact>
          )}
        </>
      )}
      {run.error !== null && (
        <div className="mt-1 rounded-md border border-destructive/40 bg-destructive/5 p-2">
          <div className="flex items-start gap-1">
            <p className="min-w-0 flex-1 whitespace-pre-wrap break-words font-mono text-[11px] leading-4" data-testid="stream-path-run-error">
              {run.error}
            </p>
            <CopyButton text={run.error} label="Copy the error" testId="stream-path-copy-error" iconOnly />
          </div>
        </div>
      )}
      <div className="mt-1 flex flex-wrap gap-1.5">
        <Button variant="outline" size="xs" onClick={() => navigate(`/runs/${run.runId}`)} data-testid="stream-path-open-run">
          Open run
        </Button>
        {run.groupId !== null && (
          <Button variant="ghost" size="xs" onClick={() => navigate(`/runs/groups/${run.groupId}`)}>
            Open the fire
          </Button>
        )}
      </div>
      {entry.files !== null && entry.files.files > 0 && (
        <div className="mt-2">
          <Section title="Files taken in" count={entry.files.files}>
            <RunFiles runId={run.runId} total={entry.files.files} />
          </Section>
        </div>
      )}
    </div>
  );
}

/** The flow that writes an object on the path, and the one run that took in what an object where data enters holds. */
function neighbours(node: StreamPathNode, path: StreamPath): { writers: StreamPathNode[]; readers: StreamPathNode[] } {
  const byId = new Map(path.nodes.map((n) => [n.id, n]));
  const writers = path.edges.filter((e) => e.to === node.id).map((e) => byId.get(e.from)).filter((n): n is StreamPathNode => n?.type === "flow");
  const readers = path.edges.filter((e) => e.from === node.id).map((e) => byId.get(e.to)).filter((n): n is StreamPathNode => n?.type === "flow");
  return { writers, readers };
}

interface StreamPathPanelProps {
  node: StreamPathNode;
  path: StreamPath;
  fire: StreamPathFire | null;
  fireLabel: string | null;
  windowDays: number;
  includeBackfills: boolean;
  onClose: () => void;
  onPick: (id: string) => void;
}

/**
 * One node of a stream's path, slid in over the right of the canvas: what it is, what it did in the fire shown, and
 * (for a flow) its verdict over the window with the evidence behind it. Laid out for a narrow column: sections under
 * quiet headings, a caption and its value per row, long names wrapped, the flows it hands over to one click away.
 */
export function StreamPathPanel({ node, path, fire, fireLabel, windowDays, includeBackfills, onClose, onPick }: StreamPathPanelProps) {
  const navigate = useNavigate();
  const runs = runsByFlow(fire);
  const flow = node.type === "flow";
  const identity = flow ? flowKindIdentity(node.kind) : null;
  const look = kindLook(node.type, node.kind.toLowerCase());
  const Icon = look.icon;
  const { writers, readers } = neighbours(node, path);
  const entry = flow && node.pipelineId !== null ? runs.get(node.pipelineId) : undefined;
  const target = flow ? path.edges.find((e) => e.from === node.id) : undefined;
  const targetNode = target === undefined ? undefined : path.nodes.find((n) => n.id === target.to);
  const lineageObject = flow ? targetNode : node;

  return (
    <div
      className="flex h-full w-full flex-col overflow-hidden rounded-lg border border-border bg-card shadow-lg"
      data-testid="stream-path-panel"
    >
      <div className="flex shrink-0 items-start gap-2 border-b border-border px-3 py-2">
        <Icon aria-hidden className={cn("mt-0.5 size-4 shrink-0", identity === null ? "text-muted-foreground" : flowKindToneClasses(identity.tone).icon)} />
        <div className="min-w-0 flex-1">
          <div className="break-all font-mono text-[13px] font-semibold leading-[18px]" data-testid="stream-path-panel-name">
            {nodeLabel(node)}
          </div>
          <div className="text-[11px] leading-4 text-muted-foreground">
            {flow
              ? `${identity!.label} flow${node.wave === null ? "" : `, wave ${node.wave}`}`
              : node.external ? `Where the data enters (${look.label})` : look.label}
            {!flow && node.qualifiedName !== null && node.qualifiedName !== node.name && (
              <span className="block break-all font-mono">{node.qualifiedName}</span>
            )}
          </div>
        </div>
        <Tooltip>
          <TooltipTrigger asChild>
            <Button variant="ghost" size="icon-xs" onClick={onClose} aria-label="Close" data-testid="stream-path-panel-close">
              <X />
            </Button>
          </TooltipTrigger>
          <TooltipContent>Close (Esc)</TooltipContent>
        </Tooltip>
      </div>

      <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto p-3">
        <div className="flex flex-wrap items-center gap-1.5">
          {flow && node.pipelineId !== null && (
            <>
              <Button variant="outline" size="xs" onClick={() => navigate(`/pipelines/${node.pipelineId}`)}>Open flow</Button>
              <Button variant="ghost" size="xs" onClick={() => navigate(`/runs?pipelineId=${node.pipelineId}`)}>Runs</Button>
              {node.role !== "selected" && (
                <Button variant="ghost" size="xs" onClick={() => navigate(`/datastreams/${node.pipelineId}`)} data-testid="stream-path-follow">
                  Follow this flow
                </Button>
              )}
            </>
          )}
          {lineageObject !== undefined && lineageObject.objectKey !== null && (
            <LineageJumpButton
              variant="outlined"
              target={{
                kind: "object",
                objectKey: lineageObject.objectKey,
                objectKind: lineageObject.kind,
                label: lineageObject.qualifiedName ?? lineageObject.name,
                sublabel: flow ? "The table this flow writes: what feeds it, and what reads it" : "What feeds this, and what reads it",
              }}
            />
          )}
        </div>

        {fire !== null && (
          <Section title={fireLabel === null ? "This fire" : `Fire of ${fireLabel}`} testId="stream-path-panel-fire">
            {flow
              ? entry === undefined
                ? <p className="text-[12px] text-muted-foreground">This flow had no run in this fire.</p>
                : <FireRun entry={entry} />
              : (
                <>
                  {writers.map((writer) => {
                    const written = writer.pipelineId === null ? undefined : runs.get(writer.pipelineId);
                    return (
                      <Fact key={writer.id} label="Written by">
                        <button type="button" className="cursor-pointer underline decoration-dotted underline-offset-4" onClick={() => onPick(writer.id)}>
                          {writer.name}
                        </button>
                        {written === undefined
                          ? <span className="ml-1 text-muted-foreground">(no run)</span>
                          : <span className="ml-1 text-muted-foreground">({OUTCOME_WORDS[runOutcome(written.run)].toLowerCase()})</span>}
                      </Fact>
                    );
                  })}
                  {!node.external && readers.map((reader) => {
                    const read = reader.pipelineId === null ? undefined : runs.get(reader.pipelineId);
                    return (
                      <Fact key={reader.id} label="Read by">
                        <button type="button" className="cursor-pointer underline decoration-dotted underline-offset-4" onClick={() => onPick(reader.id)}>
                          {reader.name}
                        </button>
                        {read === undefined
                          ? <span className="ml-1 text-muted-foreground">(no run)</span>
                          : <span className="ml-1 text-muted-foreground">({OUTCOME_WORDS[runOutcome(read.run)].toLowerCase()})</span>}
                      </Fact>
                    );
                  })}
                  {node.external && readers.map((reader) => {
                    const read = reader.pipelineId === null ? undefined : runs.get(reader.pipelineId);
                    return (
                      <div key={reader.id} className="flex flex-col gap-1">
                        <Fact label="Read by">
                          <button type="button" className="cursor-pointer underline decoration-dotted underline-offset-4" onClick={() => onPick(reader.id)}>
                            {reader.name}
                          </button>
                        </Fact>
                        {read === undefined
                          ? <p className="text-[12px] text-muted-foreground">Its flow had no run in this fire.</p>
                          : read.files !== null && read.files.files > 0
                            ? (
                              <Section title="Files that arrived" count={read.files.files}>
                                <RunFiles runId={read.run.runId} total={read.files.files} />
                              </Section>
                            )
                            : <p className="text-[12px] text-muted-foreground">No files were taken in.</p>}
                      </div>
                    );
                  })}
                </>
              )}
          </Section>
        )}

        {flow && node.pipelineId !== null && (
          <Section title={`Over the last ${windowDays} days`}>
            <StreamDetailBody pipelineId={node.pipelineId} windowDays={windowDays} includeBackfills={includeBackfills} />
          </Section>
        )}

        {node.omitted.map((omission) => (
          <Section key={omission.what} title={omissionText(omission.what, omission.count)} testId={`stream-path-omitted-${omission.what}`}>
            <NameList names={omission.names} count={omission.count} />
          </Section>
        ))}

      </div>
    </div>
  );
}
