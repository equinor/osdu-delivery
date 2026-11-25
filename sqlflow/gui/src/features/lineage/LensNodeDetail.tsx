import { useState, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "react-router-dom";
import { CircleCheck, CircleDashed, CircleX, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import { runApi } from "../../api/endpoints";
import type { FlowFire, FlowFireRun } from "../../api/types";
import { CopyButton } from "../../components/CopyButton";
import { CorrelationError } from "../../components/CorrelationError";
import { formatBytes, formatDurationSeconds } from "../../lib/time";
import { kindContribution } from "../../modules/registry";
import {
  figureLine, figureOf, fireTime, formatCount, nodeFigure, totalsOfRuns, type Figure, type LensNodeSpec, type NodeFigure,
  type Outcome,
} from "./dataLens";
import { kindLook } from "./graphKinds";

const FILES_SHOWN = 50;
const ROWS_SHOWN = 6;

/** A section of the column under a quiet heading, with a count when it lists things. */
function Section({ title, count, children, testId }: { title: string; count?: number; children: ReactNode; testId?: string }) {
  return (
    <section className="mt-3 flex flex-col gap-1" data-testid={testId}>
      <h4 className="text-[11px] font-medium tracking-wider text-muted-foreground uppercase">
        {title}
        {count !== undefined && <span className="ml-1.5 tabular-nums">{count}</span>}
      </h4>
      {children}
    </section>
  );
}

/** A caption and its value, one per row of the column. */
function Fact({ label, children, tone }: { label: string; children: ReactNode; tone?: "problem" | "quiet" }) {
  return (
    <div className="flex items-baseline justify-between gap-3 py-0.5">
      <span className="shrink-0 text-[12px] text-muted-foreground">{label}</span>
      <span
        className={cn(
          "min-w-0 text-right font-mono text-[12px] tabular-nums",
          tone === "problem" && "text-destructive",
          tone === "quiet" && "text-muted-foreground/70",
        )}
      >
        {children}
      </span>
    </div>
  );
}

const OUTCOME_ICON: Record<Outcome, typeof CircleCheck> = {
  moved: CircleCheck,
  empty: CircleCheck,
  failed: CircleX,
  running: Loader2,
  absent: CircleDashed,
};

/** The glyph of an outcome, in the status colour only when it is a problem or under way. */
function OutcomeMark({ outcome }: { outcome: Outcome }) {
  const Icon = OUTCOME_ICON[outcome];
  return (
    <Icon
      aria-hidden
      className={cn(
        "size-3.5 shrink-0",
        outcome === "failed" ? "text-destructive"
          : outcome === "running" ? "animate-spin text-primary"
            : outcome === "moved" ? "text-chart-1" : "text-muted-foreground",
      )}
    />
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
      <ul className="flex flex-col" data-testid="lens-files">
        {query.data.items.map((file) => (
          <li key={file.id} className="flex items-baseline gap-2 border-b border-border/50 py-1 last:border-b-0">
            <span className="min-w-0 flex-1 truncate font-mono text-[12px]" title={file.path ?? file.name}>{file.name}</span>
            <span className="shrink-0 font-mono text-[11px] text-muted-foreground tabular-nums">
              {formatCount(file.rows)} rows · {formatBytes(file.sizeBytes)}
            </span>
          </li>
        ))}
      </ul>
      {total > query.data.items.length && (
        <p className="text-[11px] text-muted-foreground">{total - query.data.items.length} more on the run's page.</p>
      )}
    </>
  );
}

/** What one flow did in the period, as captions and values: its own kind's counts when it has them, rows otherwise. */
function FigureFacts({ figure }: { figure: Figure }) {
  if (figure.outcome === "absent") {
    return <p className="text-[12px] text-muted-foreground">It did not run.</p>;
  }

  return (
    <div className="flex flex-col">
      {figure.changes !== null ? (
        <>
          {figure.taken !== null && <Fact label="Rows read" tone={quiet(figure.taken)}>{formatCount(figure.taken)}</Fact>}
          <Fact label="New" tone={quiet(figure.changes.inserted)}>{formatCount(figure.changes.inserted)}</Fact>
          <Fact label="Updated" tone={quiet(figure.changes.updated)}>{formatCount(figure.changes.updated)}</Fact>
          <Fact label="Removed" tone={quiet(figure.changes.deleted)}>{formatCount(figure.changes.deleted)}</Fact>
        </>
      ) : (
        <>
          {figure.taken !== null && <Fact label={capitalize(figure.takenLabel)}>{formatCount(figure.taken)}</Fact>}
          <Fact label={capitalize(figure.label)} tone={quiet(figure.moved)}>{formatCount(figure.moved)}</Fact>
          {figure.problems > 0 && <Fact label={capitalize(figure.problemsLabel)} tone="problem">{formatCount(figure.problems)}</Fact>}
          {figure.details.map((detail) => (
            <Fact key={detail.label} label={capitalize(detail.label)}>{formatCount(detail.value)}</Fact>
          ))}
        </>
      )}
      {figure.files > 0 && (
        <Fact label="Files taken in">{formatCount(figure.files)} · {formatBytes(figure.fileBytes)}</Fact>
      )}
      <Fact label={figure.runs === 1 ? "Run" : "Runs"}>
        {figure.failed > 0
          ? <span className="text-destructive">{figure.failed} of {figure.runs} failed</span>
          : figure.runs}
        {figure.active > 0 && ` · ${figure.active} under way`}
      </Fact>
      {figure.seconds > 0 && <Fact label="Took">{formatDurationSeconds(figure.seconds)}</Fact>}
    </div>
  );
}

/** A zero is not news: it is set quietly, so the counts that are something stand out of the column. */
function quiet(value: number): "quiet" | undefined {
  return value === 0 ? "quiet" : undefined;
}

function capitalize(text: string): string {
  return text === "" ? text : text[0].toUpperCase() + text.slice(1);
}

/** One run of the day, in a row: when it started, how it went, its figure, and the way to its own page. A failed
 * run's error is under it whole, with a copy button, and the files it took in open by name. */
function RunRow({ entry }: { entry: FlowFireRun }) {
  const navigate = useNavigate();
  const [filesOpen, setFilesOpen] = useState(false);
  const run = entry.run;
  const figure = figureOf(totalsOfRuns([entry]), kindContribution(run.flowKind)?.runMeasure);
  const line = nodeFigure({ id: run.runId, type: "flow", flows: [run.pipelineId], writers: [], readers: [] }, () => figure);
  const outcome: Outcome = run.status === "queued" ? "running" : line.outcome;
  return (
    <li className="border-b border-border/50 py-1.5 last:border-b-0" data-testid="lens-run">
      <div className="flex items-center gap-2">
        <OutcomeMark outcome={run.status === "cancelled" ? "absent" : outcome} />
        <span className="shrink-0 font-mono text-[12px] tabular-nums">{fireTime(run.startUtc ?? run.enqueuedUtc ?? run.writtenUtc)}</span>
        <span className={cn("min-w-0 flex-1 truncate text-[12px]", outcome === "failed" ? "text-destructive" : "text-muted-foreground")}>
          {run.status === "cancelled" ? "Cancelled" : run.status === "queued" ? "Queued" : figureLine(line)}
        </span>
        <Button variant="ghost" size="xs" className="h-5 shrink-0 px-1.5" onClick={() => navigate(`/runs/${run.runId}`)} data-testid="lens-open-run">
          Open run
        </Button>
      </div>
      <div className="mt-0.5 flex flex-wrap items-center gap-x-2 pl-[22px] text-[11px] text-muted-foreground">
        {run.durationSeconds !== null && <span>took {formatDurationSeconds(run.durationSeconds)}</span>}
        {entry.parts > 0 && <span>split over {entry.parts} parts</span>}
        {(run.fullLoad || run.backfillFrom !== null || run.backfillTo !== null) && (
          <span>{run.fullLoad ? "full load" : "backfill window"}</span>
        )}
        {entry.files !== null && entry.files.files > 0 && (
          <button
            type="button"
            className="cursor-pointer underline decoration-dotted underline-offset-2 hover:text-foreground"
            onClick={() => setFilesOpen(!filesOpen)}
            aria-expanded={filesOpen}
            data-testid="lens-run-files-toggle"
          >
            {formatCount(entry.files.files)} {entry.files.files === 1 ? "file" : "files"} · {formatBytes(entry.files.bytes)}
          </button>
        )}
      </div>
      {filesOpen && entry.files !== null && (
        <div className="mt-1 pl-[22px]">
          <RunFiles runId={run.runId} total={entry.files.files} />
        </div>
      )}
      {run.error !== null && (
        <div className="mt-1 ml-[22px] rounded-md border border-destructive/40 bg-destructive/5 p-2">
          <div className="flex items-start gap-1">
            <p className="min-w-0 flex-1 font-mono text-[11px] leading-4 break-words whitespace-pre-wrap" data-testid="lens-run-error">
              {run.error}
            </p>
            <CopyButton text={run.error} label="Copy the error" testId="lens-copy-error" iconOnly />
          </div>
        </div>
      )}
    </li>
  );
}

/** A list that shows its first rows and opens in full on request. */
function Rows<T>({ items, render }: { items: readonly T[]; render: (item: T) => ReactNode }) {
  const [all, setAll] = useState(false);
  const shown = all ? items : items.slice(0, ROWS_SHOWN);
  return (
    <>
      <ul className="flex flex-col">{shown.map(render)}</ul>
      {items.length > ROWS_SHOWN && (
        <Button variant="ghost" size="xs" className="self-start px-1 text-muted-foreground" onClick={() => setAll(!all)}>
          {all ? "Show fewer" : `Show all ${items.length}`}
        </Button>
      )}
    </>
  );
}

/** A flow beside the selected node, with what it did: a click draws and selects it. */
function FlowRow({ id, name, kind, figure, intake, onPick }: {
  id: string;
  name: string;
  kind: string;
  figure: NodeFigure;
  /** Say what reached the flow instead of what it moved: for a flow that reads the selected object. */
  intake?: boolean;
  onPick: (id: string) => void;
}) {
  const look = kindLook("flow", kind);
  const outcome = intake === true ? figure.intake.outcome : figure.outcome;
  const text = intake === true
    ? figure.intake.outcome === "moved"
      ? `took in ${formatCount(figure.intake.volume)}`
      : figure.intake.outcome === "empty" ? "ran, nothing to take in"
        : figure.intake.outcome === "failed" ? "failed" : figure.intake.outcome === "running" ? "running" : "did not run"
    : figureLine(figure);
  return (
    <li>
      <button
        type="button"
        onClick={() => onPick(id)}
        title={`${name}\n${look.label}`}
        data-testid="lens-flow-row"
        className="flex w-full cursor-pointer items-center gap-1.5 rounded-sm px-1 py-1 text-left hover:bg-accent"
      >
        <OutcomeMark outcome={outcome} />
        <span className="min-w-0 flex-1">
          <span className="block truncate font-mono text-[12px] [direction:rtl]"><bdi>{name}</bdi></span>
          <span className={cn("block truncate text-[11px]", outcome === "failed" ? "text-destructive" : "text-muted-foreground")}>{text}</span>
        </span>
      </button>
    </li>
  );
}

/**
 * What the selected node did in the day looked at, laid out for the details column: a flow's own figures and each of
 * its runs that day (how it went, its error whole, the files it took in by name); an object's writers and readers with
 * what each moved or took in, and for where data enters, the files that arrived; a folder's or a stack's flows, the
 * ones with news first. Every flow named selects its node.
 */
export function LensNodeDetail({
  spec, period, fires, firesLoading, figureFor, flowName, flowKind, onPick,
}: {
  spec: LensNodeSpec;
  /** The day looked at, in words: "Mon 16 Nov". */
  period: string;
  /** The day's fires through the drawn flows; undefined while they are read. */
  fires: readonly FlowFire[] | undefined;
  firesLoading: boolean;
  figureFor: (flowId: string) => Figure;
  flowName: (flowId: string) => string;
  flowKind: (flowId: string) => string;
  onPick: (id: string) => void;
}) {
  const runsOf = (flowIds: readonly string[]) => (fires ?? [])
    .flatMap((fire) => fire.runs)
    .filter((entry) => flowIds.includes(entry.run.pipelineId))
    .sort((a, b) => (a.run.startUtc ?? a.run.writtenUtc).localeCompare(b.run.startUtc ?? b.run.writtenUtc));
  const asNode = (flowId: string) => nodeFigure({ id: flowId, type: "flow", flows: [flowId], writers: [], readers: [] }, figureFor);
  const newsFirst = (ids: readonly string[]) => {
    const rank: Record<Outcome, number> = { failed: 0, running: 1, moved: 2, empty: 3, absent: 4 };
    return [...ids].sort((a, b) => rank[figureFor(a).outcome] - rank[figureFor(b).outcome] || flowName(a).localeCompare(flowName(b)));
  };
  const waiting = firesLoading && fires === undefined;

  if (spec.type === "flow" && spec.flows.length === 1) {
    const runs = runsOf(spec.flows);
    return (
      <>
        <Section title={period} testId="lens-detail">
          <FigureFacts figure={figureFor(spec.flows[0])} />
        </Section>
        {(waiting || runs.length > 0) && (
          <Section title={runs.length === 1 ? "Its run" : "Its runs"} count={runs.length > 1 ? runs.length : undefined} testId="lens-detail-runs">
            {waiting ? <Skeleton className="h-10 rounded-md" /> : <Rows items={runs} render={(entry) => <RunRow key={entry.run.runId} entry={entry} />} />}
          </Section>
        )}
      </>
    );
  }

  if (spec.type === "flow") {
    return (
      <Section title={`${period}: its flows`} count={spec.flows.length} testId="lens-detail">
        <Rows
          items={newsFirst(spec.flows)}
          render={(id) => <FlowRow key={id} id={id} name={flowName(id)} kind={flowKind(id)} figure={asNode(id)} onPick={onPick} />}
        />
      </Section>
    );
  }

  const arrivals = spec.writers.length === 0 ? runsOf(spec.readers).filter((entry) => (entry.files?.files ?? 0) > 0) : [];
  return (
    <>
      {spec.writers.length > 0 && (
        <Section title={`${period}: written by`} count={spec.writers.length > 1 ? spec.writers.length : undefined} testId="lens-detail">
          <Rows
            items={newsFirst(spec.writers)}
            render={(id) => <FlowRow key={id} id={id} name={flowName(id)} kind={flowKind(id)} figure={asNode(id)} onPick={onPick} />}
          />
        </Section>
      )}
      {spec.readers.length > 0 && (
        <Section title={`${period}: read by`} count={spec.readers.length > 1 ? spec.readers.length : undefined} testId="lens-detail-readers">
          <Rows
            items={newsFirst(spec.readers)}
            render={(id) => <FlowRow key={id} id={id} name={flowName(id)} kind={flowKind(id)} figure={asNode(id)} intake onPick={onPick} />}
          />
        </Section>
      )}
      {spec.writers.length === 0 && spec.readers.length === 0 && (
        <Section title={period} testId="lens-detail">
          <p className="text-[12px] text-muted-foreground">No flow in this scope reads or writes this node.</p>
        </Section>
      )}
      {waiting && spec.writers.length === 0 && <Skeleton className="mt-3 h-10 rounded-md" />}
      {arrivals.map((entry) => (
        <Section
          key={entry.run.runId}
          title={`Files that arrived, ${fireTime(entry.run.startUtc ?? entry.run.writtenUtc)}`}
          count={entry.files?.files}
          testId="lens-detail-arrivals"
        >
          <RunFiles runId={entry.run.runId} total={entry.files?.files ?? 0} />
        </Section>
      ))}
    </>
  );
}
