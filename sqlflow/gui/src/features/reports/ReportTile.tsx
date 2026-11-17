// One view of one query on a report's page: its heading, its menu, and whichever of its states it is in. A tile never
// shows a blank rectangle: it waits in the shape of what is coming, says what it needs, says what went wrong and how to
// put it right, or shows the answer, with the answer before it held while a newer one is read.

import { useMemo, useState, type ReactNode } from "react";
import { Link as RouterLink } from "react-router-dom";
import {
  ChartColumn, CircleAlert, CircleStop, Download, EllipsisVertical, Info, Loader2, RotateCw, SquareCode, Table2,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Skeleton } from "@/components/ui/skeleton";
import { CorrelationError } from "@/components/CorrelationError";
import { ExplainTip } from "@/components/ExplainTip";
import { useNow } from "@/hooks/useNow";
import { downloadFileName, downloadText } from "@/lib/download";
import { cn } from "@/lib/utils";
import type { Report, ReportParameter } from "../../api/types";
import { ColumnChart, LineView } from "./ReportChart";
import { BarView, FigureView, MatrixView, ShareView, TableView, type Selecting } from "./ReportViews";
import {
  effectiveValue, formatDuration, parameterLabel, planView, queryTitle, selectValue, shownColumns, tileSpan, toCsv,
  type GivenValues, type Tile, type ViewPlan,
} from "./reportModel";
import type { QueryState } from "./useReportQueries";

interface ReportTileProps {
  report: Report;
  tile: Tile;
  state: QueryState;
  given: GivenValues;
  onSelect: (parameter: ReportParameter, value: string | null) => void;
  onDetails: (queryName: string) => void;
}

/** A tile's share of the page's twelve columns, narrower pages giving a tile more of the row. */
const SPAN_CLASS: Record<number, string> = {
  3: "col-span-12 @2xl/report:col-span-6 @5xl/report:col-span-3",
  4: "col-span-12 @2xl/report:col-span-6 @5xl/report:col-span-4",
  6: "col-span-12 @4xl/report:col-span-6",
  8: "col-span-12 @4xl/report:col-span-8",
  12: "col-span-12",
};

/** What a view's type is drawn as before its answer arrives, so the tile waits at the width it will have. */
function expectedKind(type: Tile["view"]["type"]): ViewPlan["kind"] | null {
  return type === "auto" ? null : type;
}

export function ReportTile({ report, tile, state, given, onSelect, onDetails }: ReportTileProps) {
  const { query, view } = tile;
  const [asTable, setAsTable] = useState(false);
  const title = view.title ?? queryTitle(query);
  const description = view.description ?? query.description;
  const answer = state.answer;

  const columns = useMemo(
    () => (answer === null ? [] : shownColumns(answer.columns, query.columns)), [answer, query.columns]);
  const plan = useMemo<ViewPlan | null>(
    () => (answer === null ? null : planView(view, columns, answer.rows)), [answer, view, columns]);

  // What a click on a mark does: sets the parameter the view names to the mark's value, or clears it when the mark is
  // the one already in effect.
  const parameter = view.selects === null
    ? null
    : report.parameters.find((candidate) => candidate.name.toLowerCase() === view.selects?.toLowerCase()) ?? null;
  const selecting: Selecting | null = parameter === null ? null : {
    label: parameterLabel(parameter),
    isSelected: (raw) => raw !== null && selectValue(raw, parameter.type) === effectiveValue(parameter, given),
    onSelect: (raw) => {
      const value = selectValue(raw, parameter.type);
      onSelect(parameter, value !== null && value === effectiveValue(parameter, given) ? null : value);
    },
  };

  const kind = plan?.kind ?? expectedKind(view.type);
  const span = tileSpan(view.width, kind);
  const testId = `report-tile-${query.name}`;
  const charted = plan !== null && plan.kind !== "table" && plan.kind !== "empty" && plan.kind !== "problem";
  // Figures are tiles of their own and a table is its own framed surface: neither is set inside a second frame.
  const tabled = state.status === "answered" && plan !== null && (plan.kind === "table" || (asTable && charted));
  const figures = state.status === "answered" && plan?.kind === "figure" && !asTable;
  const bare = figures || tabled;

  const download = () => {
    if (answer === null) return;
    downloadText(downloadFileName([report.name, query.name], "csv"), toCsv(answer.columns, answer.rows), "text/csv");
  };

  const notes: ReactNode[] = [];
  if (answer !== null && answer.truncated) {
    notes.push(
      <span key="truncated" className="inline-flex items-center gap-1 text-warning" data-testid={`${testId}-truncated`}>
        <CircleAlert className="size-3.5" />
        The first {answer.rowCount.toLocaleString()} rows: the query answers more than its limit of {query.maxRows.toLocaleString()}, so a total over them is not the whole.
      </span>,
    );
  }

  if (plan !== null && "notes" in plan && !asTable) {
    plan.notes.forEach((note, index) => notes.push(<span key={`note-${index}`}>{note}</span>));
  }

  if (selecting !== null && charted && !asTable) {
    notes.push(<span key="select">Click a {plan?.kind === "share" ? "part" : plan?.kind === "matrix" ? "row" : "bar"} to set {selecting.label}; click it again to clear.</span>);
  }

  const body = (() => {
    switch (state.status) {
      case "blocked":
        return (
          <Notice tone="warning" title="This query cannot run" testId={`${testId}-blocked`}>
            <p>{state.problem}</p>
            <p className="mt-1">
              The statement is in <RouterLink to={`/pipelines/${report.id}?tab=yaml`} className="underline underline-offset-2">the report's document</RouterLink>;
              it runs once it is one read-only SELECT and the repository is synced.
            </p>
          </Notice>
        );
      case "waiting":
        return (
          <Notice tone="quiet" title={`Set ${state.waitingFor.join(" and ")} above to see this`} testId={`${testId}-waiting`}>
            <p>The query needs {state.waitingFor.length === 1 ? "that value" : "those values"} before it can run.</p>
          </Notice>
        );
      case "running":
        return <Waiting kind={kind} askedAtMs={state.askedAtMs} onCancel={state.cancel} testId={`${testId}-running`} />;
      case "cancelled":
        return (
          <Notice tone="quiet" title="Stopped before it answered" testId={`${testId}-cancelled`}>
            <Button variant="outline" size="xs" className="mt-2" onClick={state.runAgain}>
              <RotateCw />
              Run again
            </Button>
          </Notice>
        );
      case "failed":
        return (
          <div className="flex flex-col gap-2" data-testid={`${testId}-failed`}>
            {state.failure?.error !== null && state.failure?.error !== undefined
              ? <CorrelationError error={state.failure.error} />
              : (
                <Notice tone="destructive" title="The query did not answer">
                  <p className="whitespace-pre-wrap break-words font-mono text-xs">{state.failure?.message}</p>
                </Notice>
              )}
            <div className="flex gap-2">
              <Button variant="outline" size="xs" onClick={state.runAgain}>
                <RotateCw />
                Run again
              </Button>
              <Button variant="ghost" size="xs" onClick={() => onDetails(query.name)}>
                <SquareCode />
                How it was computed
              </Button>
            </div>
          </div>
        );
      default: {
        if (answer === null || plan === null) {
          return <Waiting kind={kind} askedAtMs={null} onCancel={state.cancel} testId={`${testId}-running`} />;
        }

        if (plan.kind === "empty") {
          return (
            <Notice tone="quiet" title="No rows" testId={`${testId}-empty`}>
              <p>The query answered nothing for these values.</p>
            </Notice>
          );
        }

        if (plan.kind === "problem") {
          return (
            <Notice tone="warning" title="This view cannot be drawn from the answer" testId={`${testId}-problem`}>
              <p>{plan.message}</p>
              <p className="mt-1">The rows are shown as a table until the view is put right in the report's document.</p>
            </Notice>
          );
        }

        if (asTable || plan.kind === "table") {
          return (
            <TableView
              columns={plan.kind === "table" && !asTable ? plan.columns : columns}
              rows={answer.rows}
              by={plan.kind === "table" ? plan.by : null}
              selecting={plan.kind === "table" ? selecting : null}
              listKey={`${query.name}:${answer.rowCount}:${answer.sql.length}`}
              testId={`${testId}-table`}
            />
          );
        }

        switch (plan.kind) {
          case "figure":
            return <FigureView items={plan.items} testId={`${testId}-figures`} />;
          case "bar":
            return <BarView plan={plan} selecting={selecting} testId={`${testId}-bars`} />;
          case "column":
            return <ColumnChart plan={plan} selecting={selecting} testId={`${testId}-columns`} />;
          case "line":
            return <LineView plan={plan} testId={`${testId}-line`} />;
          case "share":
            return <ShareView plan={plan} selecting={selecting} testId={`${testId}-share`} />;
          default:
            return <MatrixView plan={plan} selecting={selecting} testId={`${testId}-matrix`} />;
        }
      }
    }
  })();

  const heading = (
    <div className="flex min-h-6 items-center gap-1.5">
      <h3 className={cn("min-w-0 truncate font-medium", figures ? "text-[13px] text-muted-foreground" : "text-sm")} title={title}>{title}</h3>
      {description !== null && (
        <ExplainTip title={title} text={description} testId={`${testId}-about`} side="bottom">
          <button
            type="button"
            className="inline-flex size-5 shrink-0 items-center justify-center rounded text-muted-foreground hover:text-foreground focus-visible:outline-1 focus-visible:outline-ring"
            aria-label={`About: ${title}`}
            data-testid={`${testId}-about`}
          >
            <Info className="size-3.5" />
          </button>
        </ExplainTip>
      )}
      <span className="flex-1" />
      {state.refreshing && (
        <span className="inline-flex items-center gap-1 text-xs text-muted-foreground" data-testid={`${testId}-refreshing`}>
          <Loader2 className="size-3.5 animate-spin" />
          Reading
        </span>
      )}
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="ghost" size="icon-xs" aria-label={`More for ${title}`} data-testid={`${testId}-menu`}>
            <EllipsisVertical />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end">
          {charted && (
            <DropdownMenuItem onSelect={() => setAsTable((shown) => !shown)} data-testid={`${testId}-as-table`}>
              {asTable ? <ChartColumn /> : <Table2 />}
              {asTable ? "Show as drawn" : "Show as a table"}
            </DropdownMenuItem>
          )}
          <DropdownMenuItem onSelect={download} disabled={answer === null} data-testid={`${testId}-download`}>
            <Download />
            Download CSV
          </DropdownMenuItem>
          <DropdownMenuItem onSelect={() => onDetails(query.name)} data-testid={`${testId}-details`}>
            <SquareCode />
            How it was computed
          </DropdownMenuItem>
          <DropdownMenuSeparator />
          {state.status === "running" || state.refreshing
            ? (
              <DropdownMenuItem onSelect={state.cancel} data-testid={`${testId}-cancel`}>
                <CircleStop />
                Stop
              </DropdownMenuItem>
            )
            : (
              <DropdownMenuItem
                onSelect={state.runAgain}
                disabled={state.status === "blocked" || state.status === "waiting"}
                data-testid={`${testId}-run-again`}
              >
                <RotateCw />
                Run again
              </DropdownMenuItem>
            )}
        </DropdownMenuContent>
      </DropdownMenu>
    </div>
  );

  const content = (
    <>
      {/* The answer before a newer one is held, dimmed, so the page keeps its shape while it is read. */}
      <div className={cn("min-w-0 transition-opacity", state.refreshing && "opacity-55")}>{body}</div>
      {notes.length > 0 && (
        <div className="flex flex-col gap-0.5 text-xs text-muted-foreground" data-testid={`${testId}-notes`}>{notes}</div>
      )}
    </>
  );

  // A view the answer cannot be drawn as still shows the answer: its rows as a table, under the tile that says why.
  const fallback = state.status === "answered" && plan?.kind === "problem" && answer !== null
    ? <TableView columns={columns} rows={answer.rows} by={null} selecting={null} listKey={`${query.name}:fallback`} testId={`${testId}-table`} />
    : null;

  return (
    <section
      className={cn("flex min-w-0 flex-col gap-2", SPAN_CLASS[span] ?? SPAN_CLASS[12])}
      data-testid={testId}
      data-status={state.status}
    >
      {bare
        ? <>{heading}{content}</>
        : (
          <Card className={cn("gap-3 rounded-lg p-4", fallback === null && "h-full")}>
            {heading}
            {content}
          </Card>
        )}
      {fallback}
    </section>
  );
}

const NOTICE_TONE = {
  quiet: "text-muted-foreground",
  warning: "text-warning",
  destructive: "text-destructive",
} as const;

/** A tile's own word on where it stands, in place of an answer: what it waits for, or what is wrong and what to do. */
function Notice({ tone, title, children, testId }: {
  tone: keyof typeof NOTICE_TONE;
  title: string;
  children?: ReactNode;
  testId?: string;
}) {
  return (
    <div className="flex items-start gap-2 py-2" data-testid={testId}>
      {tone !== "quiet" && <CircleAlert className={cn("mt-0.5 size-4 shrink-0", NOTICE_TONE[tone])} />}
      <div className="min-w-0 text-[13px]">
        <div className={cn("font-medium", tone === "quiet" ? "text-muted-foreground" : NOTICE_TONE[tone])}>{title}</div>
        <div className="text-muted-foreground">{children}</div>
      </div>
    </div>
  );
}

/**
 * A tile waiting for its first answer, in the shape of what is coming. Once the wait is long enough to wonder about, it
 * says how long it has been and offers to stop.
 */
function Waiting({ kind, askedAtMs, onCancel, testId }: {
  kind: ViewPlan["kind"] | null;
  askedAtMs: number | null;
  onCancel: () => void;
  testId: string;
}) {
  const now = useNow(askedAtMs !== null);
  const seconds = askedAtMs === null ? 0 : Math.max(0, (now - askedAtMs) / 1000);
  const shape = kind === "figure"
    ? (
      <div className="grid grid-cols-[repeat(auto-fit,minmax(12rem,1fr))] gap-3">
        {[0, 1, 2].map((index) => <Skeleton key={index} className="h-[86px] rounded-lg" />)}
      </div>
    )
    : kind === "table" || kind === "matrix" || kind === null
      ? (
        <div className="flex flex-col gap-2">
          {[0, 1, 2, 3].map((index) => <Skeleton key={index} className="h-6 w-full" />)}
        </div>
      )
      : kind === "share"
        ? <Skeleton className="h-7 w-full" />
        : (
          <div className="flex flex-col gap-2">
            {[82, 64, 47, 33, 21].map((width) => <Skeleton key={width} className="h-4" style={{ width: `${width}%` }} />)}
          </div>
        );
  return (
    <div className="flex flex-col gap-2" data-testid={testId}>
      {shape}
      {seconds >= 2 && (
        <div className="flex items-center gap-2 text-xs text-muted-foreground">
          <Loader2 className="size-3.5 animate-spin" />
          Running for {formatDuration(Math.floor(seconds))}
          <Button variant="ghost" size="xs" onClick={onCancel} data-testid={`${testId}-cancel`}>
            <CircleStop />
            Stop
          </Button>
        </div>
      )}
    </div>
  );
}
