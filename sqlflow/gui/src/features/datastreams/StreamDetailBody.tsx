import type { ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import {
  Bar,
  CartesianGrid,
  Line,
  ComposedChart,
  Rectangle,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
  type BarShapeProps,
  type TooltipContentProps,
} from "recharts";
import { CircleCheck, CircleSlash } from "lucide-react";
import { Skeleton } from "@/components/ui/skeleton";
import { LineCursor, chartBehaviour, chartLayers } from "@/lib/charts";
import { isApiError } from "../../api/client";
import { dataStreamApi } from "../../api/endpoints";
import type { StreamPoint, StreamSignal } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { RichTooltip } from "../../components/RichTooltip";
import { useChartInk, detectorLabels, detectorMethods, evidence, finding, formatRows, formatDays } from "./streamPresentation";
import { StreamStatusBadge } from "./StreamStatusBadge";

/** The chart's hover readout: what arrived, what was expected, and (when flagged) why. */
function PointTooltip({ active, payload }: TooltipContentProps) {
  if (active !== true || payload.length === 0) {
    return null;
  }

  const point = payload[0].payload as StreamPoint;
  return (
    <div className="rounded-md border border-border bg-popover px-2.5 py-1.5 text-xs text-popover-foreground shadow-md">
      <div className="font-mono font-medium">{point.date.slice(0, 10)}</div>
      <div className="mt-1 flex flex-col gap-0.5 font-mono tabular-nums">
        <span>{formatRows(point.rowsWritten)} written</span>
        <span className="text-muted-foreground">{formatRows(point.expected)} expected</span>
        {point.rowsWritten > 0 && (
          <span className="text-muted-foreground">
            {point.rowsInserted.toLocaleString()} ins / {point.rowsUpdated.toLocaleString()} upd /{" "}
            {point.rowsDeleted.toLocaleString()} del
          </span>
        )}
        <span className="text-muted-foreground">
          {point.runs} run(s){point.failures > 0 ? `, ${point.failures} failed` : ""}
        </span>
        {point.excludedBackfillRuns > 0 && (
          <span className="text-muted-foreground">{point.excludedBackfillRuns} backfill run(s) excluded</span>
        )}
      </div>
      {point.anomaly && (
        <div className="mt-1 border-t border-border/60 pt-1 text-destructive">
          {point.reason} ({point.severity.toFixed(1)} sigma)
        </div>
      )}
      {point.immature && <div className="mt-1 text-[11px] text-muted-foreground">Still arriving; not flagged.</div>}
    </div>
  );
}

/** One detector's row: whether it fired, its confidence, and the sentence it measured. */
function SignalRow({ signal }: { signal: StreamSignal }) {
  return (
    <div className="flex items-start gap-2.5 border-b border-border/60 py-2 last:border-b-0">
      {signal.fired ? (
        <CircleSlash className="mt-0.5 size-3.5 shrink-0 text-destructive" aria-hidden />
      ) : (
        <CircleCheck className="mt-0.5 size-3.5 shrink-0 text-success" aria-hidden />
      )}
      <div className="flex min-w-0 flex-col gap-0.5">
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-[13px] font-medium">{detectorLabels[signal.detector]}</span>
          {signal.primary && (
            <span className="rounded-sm bg-muted px-1.5 py-0.5 text-[10px] uppercase tracking-wide text-muted-foreground">
              primary
            </span>
          )}
          <span className="text-[11px] text-muted-foreground">
            {signal.fired ? `fired, confidence ${(signal.score * 100).toFixed(0)}%` : "quiet"}
          </span>
        </div>
        <p className="text-[13px] leading-5 text-muted-foreground">{signal.detail}</p>
        <p className="text-[11px] leading-4 text-muted-foreground/70">{detectorMethods[signal.detector]}</p>
      </div>
    </div>
  );
}

/** A caption and its value, one per row of a narrow column. */
function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-baseline justify-between gap-3 py-0.5">
      <span className="text-[12px] text-muted-foreground">{label}</span>
      <span className="text-right font-mono text-[12px] tabular-nums">{value}</span>
    </div>
  );
}

/** A section of the column under a quiet heading. */
function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-1">
      <h4 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">{title}</h4>
      {children}
    </section>
  );
}

interface StreamDetailBodyProps {
  pipelineId: string;
  windowDays: number;
  includeBackfills: boolean;
}

/**
 * One stream's verdict over the window, laid out for a side panel's column: the verdict and the sentence behind it,
 * the learned pattern it is stated against, the measures, the daily volume against what the detector expected, and
 * every detector's reasoning (including the ones that stayed quiet, so a verdict can be checked rather than taken on
 * faith).
 *
 * The chart is one measure on one axis: bars are the rows that actually arrived, the dashed line is the
 * trend-and-weekday expectation drawn in recessive ink because it is a reference rather than a second series, and a
 * flagged day wears the reserved status color with its reason spelled out in the tooltip, so identity never rests on
 * color alone (DESIGN.md 3.4).
 *
 * It reads the same window and backfill preferences the board stores, so the two can never show one table two answers.
 */
export function StreamDetailBody({ pipelineId, windowDays, includeBackfills }: StreamDetailBodyProps) {
  const ink = useChartInk();
  const query = useQuery({
    queryKey: ["datastreams", "detail", pipelineId, windowDays, includeBackfills],
    queryFn: () => dataStreamApi.get(pipelineId, windowDays, includeBackfills),
  });

  if (query.isError) {
    // A flow with no runs in the window is not a fault: there is simply nothing to judge it on yet.
    if (isApiError(query.error) && query.error.status === 404) {
      return (
        <p className="text-[12px] leading-5 text-muted-foreground" data-testid="stream-detail-none">
          No runs in the last {windowDays} days, so there is nothing to judge yet.
        </p>
      );
    }

    return isApiError(query.error)
      ? <CorrelationError error={query.error} />
      : <p className="text-[13px] text-destructive">{String(query.error)}</p>;
  }

  const stream = query.data;
  if (stream === undefined) {
    return (
      <div className="flex flex-col gap-2">
        <Skeleton className="h-6 rounded-md" />
        <Skeleton className="h-40 rounded-lg" />
        <Skeleton className="h-24 rounded-lg" />
      </div>
    );
  }

  const profile = stream.profile;
  const pattern = profile.pattern;
  return (
    <div className="flex flex-col gap-4" data-testid="stream-detail-body">
      <div className="flex flex-col gap-1">
        <StreamStatusBadge status={stream.status} severity={stream.severity} />
        {/* The headline with its number in it; the whole reasoning is one hover away, as on the board's row. */}
        <RichTooltip body={stream.summary} title="Finding">
          <p className="w-fit cursor-help text-[12px] leading-5 underline decoration-dotted decoration-muted-foreground/60 underline-offset-4">
            {finding(stream)}
          </p>
        </RichTooltip>
        <p className="text-[11px] leading-4 text-muted-foreground">{evidence(stream)}</p>
      </div>

      {/* The learned pattern: the reference the verdict above is stated against. Without it, "silent for four days"
          is a number rather than a judgement. */}
      <Section title="Learned pattern">
        <p className="text-[12px] leading-5">{pattern.description}</p>
        {/* The recurring delivery, called out because the date the next one is due is the one thing here an operator
            can act on: when to come back and check that the big one arrived. */}
        {pattern.cycle !== null && (
          <div className="mt-1 border-t border-border/60 pt-1">
            <Stat label="Cycle" value={pattern.cycle.monthly ? "monthly" : `every ${pattern.cycle.periodDays}d`} />
            <Stat
              label={pattern.cycle.heavier ? "Cycle day" : "Light day"}
              value={`${formatRows(pattern.cycle.cycleRows)} vs ${formatRows(pattern.cycle.ordinaryRows)}`}
            />
            <Stat label="Last one" value={pattern.cycle.lastOccurrenceUtc?.slice(0, 10) ?? "-"} />
            <Stat label="Next due" value={pattern.cycle.nextExpectedUtc?.slice(0, 10) ?? "-"} />
          </div>
        )}
      </Section>

      {stream.series !== null && stream.series.length > 0 ? (
        <Section title={`Rows written, last ${windowDays} days`}>
          <div className="flex flex-wrap items-center gap-3 text-[11px] text-muted-foreground">
            <span className="inline-flex items-center gap-1.5">
              <span className="size-2.5 rounded-sm" style={{ background: ink.series }} aria-hidden />
              Written
            </span>
            <span className="inline-flex items-center gap-1.5">
              <span className="h-0.5 w-4 border-t-2 border-dashed" style={{ borderColor: ink.axis }} aria-hidden />
              Expected
            </span>
            <span className="inline-flex items-center gap-1.5">
              <span className="size-2.5 rounded-sm bg-destructive" aria-hidden />
              Flagged
            </span>
          </div>
          <ResponsiveContainer width="100%" height={170}>
            <ComposedChart {...chartBehaviour} data={stream.series} margin={{ top: 4, right: 4, bottom: 0, left: 0 }}>
              <CartesianGrid stroke={ink.grid} strokeDasharray="3 3" vertical={false} />
              <XAxis
                dataKey="date"
                tickFormatter={(value: string) => value.slice(5, 10)}
                stroke={ink.axis}
                tick={{ fontSize: 11 }}
                minTickGap={24}
              />
              <YAxis
                stroke={ink.axis}
                tick={{ fontSize: 11 }}
                width={44}
                tickFormatter={(value: number) => formatRows(value, true)}
              />
              <Tooltip content={PointTooltip} cursor={<LineCursor fill={ink.cursor} fillOpacity={0.35} />} />
              <Bar
                dataKey="rowsWritten"
                radius={[3, 3, 0, 0]}
                isAnimationActive={false}
                zIndex={chartLayers.bar}
                shape={(bar: BarShapeProps) => {
                  const point = bar.payload as StreamPoint;
                  return (
                    <Rectangle
                      {...bar}
                      fill={point.anomaly ? ink.flagged : ink.series}
                      fillOpacity={point.immature ? 0.45 : 1}
                    />
                  );
                }}
              />
              <Line
                dataKey="expected"
                stroke={ink.axis}
                strokeWidth={2}
                strokeDasharray="4 3"
                dot={false}
                isAnimationActive={false}
                zIndex={chartLayers.line}
              />
            </ComposedChart>
          </ResponsiveContainer>
        </Section>
      ) : (
        <EmptyState title="No scored series" description="This stream has no analysed days in the window." />
      )}

      <Section title="Measures">
        <Stat label="Last load" value={formatDays(profile.daysSinceLastLoad)} />
        <Stat label="Expected gap" value={`${profile.expectedGapDays}d (${profile.cadenceSource})`} />
        <Stat label="Median load / day" value={formatRows(profile.medianRowsWrittenPerLoadedDay)} />
        <Stat label="Avg inserted / run" value={formatRows(profile.avgRowsInsertedPerRun)} />
        <Stat label="Avg updated / run" value={formatRows(profile.avgRowsUpdatedPerRun)} />
        <Stat label="Avg deleted / run" value={formatRows(profile.avgRowsDeletedPerRun)} />
        <Stat label="Empty days" value={`${profile.unexpectedNullDays} vs ${profile.predictedNullDays.toFixed(1)} predicted`} />
        <Stat label="Empty day split" value={`${profile.emptyRunDays} ran empty, ${profile.noRunDays} no run`} />
        <Stat label="Normal delivery" value={`${(pattern.reliability * 100).toFixed(0)}% of expected days`} />
        <Stat label="Loading days" value={`${profile.loadedDays} of ${profile.runDays} run days`} />
        <Stat label="Runs" value={`${profile.runs}${profile.failures > 0 ? `, ${profile.failures} failed` : ""}`} />
        <Stat
          label="Trend"
          value={`${profile.trendRowsPerDay >= 0 ? "+" : ""}${Math.round(profile.trendRowsPerDay).toLocaleString()} rows/day`}
        />
        {profile.trimmedLoadDays > 0 && (
          <p className="mt-1 text-[11px] leading-4 text-muted-foreground">
            {profile.trimmedLoadDays} day(s) above {formatRows(profile.trimFence)} rows were trimmed as suspected
            reprocessing before fitting. The chart still shows what really landed.
          </p>
        )}
        {stream.scheduleName !== null && (
          <p className="mt-1 text-[11px] leading-4 text-muted-foreground">
            Held to schedule <span className="font-mono">{stream.scheduleName}</span>
            {stream.cron !== null && <> (<span className="font-mono">{stream.cron}</span>, {stream.timezone})</>}
          </p>
        )}
      </Section>

      <Section title="Detectors">
        <div>
          {stream.signals.map((signal) => <SignalRow key={signal.detector} signal={signal} />)}
        </div>
      </Section>
    </div>
  );
}
