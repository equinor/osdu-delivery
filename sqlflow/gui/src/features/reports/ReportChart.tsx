// The two views of a report drawn with the chart library: columns (buckets that have an order) and lines (a value
// over an ordered column, usually a date). They follow the product's chart rules (DESIGN.md 3.4, 7.7): one axis per
// chart, thin marks, hairline grid, a legend for two or more series, a hover that lists every series, colours in the
// palette's fixed slots, and text in text colours. A view's exact figures are always one click away as a table.

import {
  Bar, BarChart, CartesianGrid, Cell, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis,
  type TooltipContentProps,
} from "recharts";
import { BandCursor, LineCursor, chartBehaviour, chartLayers } from "@/lib/charts";
import { SeriesLegend, useReportInk, useSeriesSlots, type Selecting } from "./ReportViews";
import { formatNumber, formatTick, seriesSpread, type BarPlan, type LinePlan, type Series } from "./reportModel";

const CHART_HEIGHT = 248;
const SMALL_CHART_HEIGHT = 148;

/** The key a series' values are plotted under: its place among the view's series, never its name. */
function seriesKey(index: number): string {
  return `s${index}`;
}

/** The hover of a column or line chart: the position, then every series there, each figure ahead of its name. */
function ChartTip({ active, payload, label, series }: TooltipContentProps & { series: readonly Series[] }) {
  if (active !== true || payload.length === 0) {
    return null;
  }

  return (
    <div className="rounded-md border border-border bg-popover px-2.5 py-1.5 text-xs text-popover-foreground shadow-md">
      <div className="font-medium">{label}</div>
      <div className="mt-1 flex flex-col gap-0.5">
        {payload.map((entry) => {
          const index = Number(String(entry.dataKey).slice(1));
          const own = series[index];
          if (own === undefined || typeof entry.value !== "number") return null;
          return (
            <span key={String(entry.dataKey)} className="flex items-center gap-2">
              <span className="h-0.5 w-3 shrink-0 rounded-full" style={{ background: entry.color }} aria-hidden />
              <span className="font-mono font-medium tabular-nums">{formatNumber(entry.value, own.column)}</span>
              <span className="text-muted-foreground">{own.label}</span>
            </span>
          );
        })}
      </div>
    </div>
  );
}

function useFills(series: readonly Series[]) {
  const ink = useReportInk();
  const slotOf = useSeriesSlots(series);
  // What was folded together is drawn in the axis ink, quiet beside the series colours.
  const fillOf = (s: Series) => (s.other === true ? ink.axis : ink.series[slotOf(s)] ?? ink.series[0]);
  return { ink, fillOf };
}

/** Vertical columns, one per row in the order the query answered, stacked or side by side. */
export function ColumnChart({ plan, selecting, testId }: { plan: BarPlan; selecting: Selecting | null; testId: string }) {
  const { ink, fillOf } = useFills(plan.series);
  const data = plan.rows.map((row) => {
    const point: Record<string, string | number | null> = { label: row.label, raw: row.raw };
    row.values.forEach((value, index) => { point[seriesKey(index)] = value; });
    return point;
  });
  const anySelected = selecting !== null && plan.rows.some((row) => selecting.isSelected(row.raw));
  const axisColumn = plan.series[0].column;

  return (
    <div className="flex flex-col gap-2" data-testid={testId}>
      <SeriesLegend series={plan.series} fillOf={fillOf} />
      <div style={{ height: CHART_HEIGHT }}>
        <ResponsiveContainer width="100%" height="100%">
          <BarChart {...chartBehaviour} data={data} margin={{ top: 6, right: 8, bottom: 0, left: 0 }} barCategoryGap="22%" barGap={2}>
            <CartesianGrid vertical={false} stroke={ink.grid} />
            <XAxis
              dataKey="label"
              stroke={ink.axis}
              tick={{ fill: ink.axis, fontSize: 11 }}
              axisLine={{ stroke: ink.grid }}
              tickLine={false}
              minTickGap={16}
              interval="preserveStartEnd"
            />
            <YAxis
              stroke={ink.axis}
              tick={{ fill: ink.axis, fontSize: 11 }}
              axisLine={false}
              tickLine={false}
              width={52}
              domain={[plan.min, "auto"]}
              tickFormatter={(value: number) => formatTick(value, axisColumn, plan.max)}
            />
            <Tooltip
              cursor={<BandCursor fill={ink.cursor} />}
              content={(props) => <ChartTip {...props} series={plan.series} />}
            />
            {plan.series.map((series, index) => (
              <Bar
                key={series.key}
                dataKey={seriesKey(index)}
                name={series.label}
                fill={fillOf(series)}
                stackId={plan.stacked ? "stack" : undefined}
                // A hairline of the surface between the segments of a stack, and between columns set side by side.
                stroke={ink.surface}
                strokeWidth={plan.stacked ? 2 : 0}
                radius={!plan.stacked || index === plan.series.length - 1 ? [4, 4, 0, 0] : 0}
                maxBarSize={24}
                isAnimationActive={false}
                zIndex={chartLayers.bar}
                cursor={selecting !== null ? "pointer" : undefined}
                onClick={selecting !== null
                  ? (entry: { payload?: { raw?: string | null } }) => selecting.onSelect(entry.payload?.raw ?? null)
                  : undefined}
              >
                {anySelected && plan.rows.map((row) => (
                  <Cell key={`${row.raw ?? ""}`} fillOpacity={selecting?.isSelected(row.raw) === true ? 1 : 0.4} />
                ))}
              </Bar>
            ))}
          </BarChart>
        </ResponsiveContainer>
      </div>
    </div>
  );
}

/**
 * A line per series over an ordered column. Series whose sizes are far apart are drawn as one small chart each, on its
 * own axis, since one shared axis would flatten the smaller of them and a second axis on one chart is never drawn.
 */
export function LineView({ plan, testId }: { plan: LinePlan; testId: string }) {
  const { ink, fillOf } = useFills(plan.series);
  const data = plan.points.map((values, at) => {
    const point: Record<string, string | number | null> = { label: plan.labels[at] };
    values.forEach((value, index) => { point[seriesKey(index)] = value; });
    return point;
  });
  const dots = plan.points.length <= 30;
  const apart = plan.series.length > 1 && seriesSpread(plan.points, plan.series.length) > 5;
  const groups = apart ? plan.series.map((series, index) => [{ series, index }]) : [plan.series.map((series, index) => ({ series, index }))];

  return (
    <div className="flex flex-col gap-2" data-testid={testId}>
      {!apart && <SeriesLegend series={plan.series} fillOf={fillOf} mark="line" />}
      {groups.map((group) => {
        const max = Math.max(0, ...plan.points.flatMap((values) => group.map(({ index }) => Math.abs(values[index] ?? 0))));
        const axisColumn = group[0].series.column;
        return (
          <div key={group[0].series.key} className="flex flex-col gap-1">
            {apart && (
              <div className="flex items-center gap-1.5 text-xs text-muted-foreground">
                <span className="h-0.5 w-3 rounded-full" style={{ background: fillOf(group[0].series) }} aria-hidden />
                {group[0].series.label}
              </div>
            )}
            <div style={{ height: apart ? SMALL_CHART_HEIGHT : CHART_HEIGHT }}>
              <ResponsiveContainer width="100%" height="100%">
                <LineChart {...chartBehaviour} data={data} margin={{ top: 6, right: 12, bottom: 0, left: 0 }}>
                  <CartesianGrid vertical={false} stroke={ink.grid} />
                  <XAxis
                    dataKey="label"
                    stroke={ink.axis}
                    tick={{ fill: ink.axis, fontSize: 11 }}
                    axisLine={{ stroke: ink.grid }}
                    tickLine={false}
                    minTickGap={24}
                    interval="preserveStartEnd"
                  />
                  <YAxis
                    stroke={ink.axis}
                    tick={{ fill: ink.axis, fontSize: 11 }}
                    axisLine={false}
                    tickLine={false}
                    width={52}
                    tickFormatter={(value: number) => formatTick(value, axisColumn, max)}
                  />
                  <Tooltip
                    cursor={<LineCursor stroke={ink.axis} />}
                    content={(props) => <ChartTip {...props} series={plan.series} />}
                  />
                  {group.map(({ series, index }) => (
                    <Line
                      key={series.key}
                      dataKey={seriesKey(index)}
                      name={series.label}
                      stroke={fillOf(series)}
                      strokeWidth={2}
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      // A point is a filled dot with a ring of the surface, so it stays legible where lines cross.
                      dot={dots ? { r: 4, fill: fillOf(series), stroke: ink.surface, strokeWidth: 2 } : false}
                      activeDot={{ r: 5, fill: fillOf(series), stroke: ink.surface, strokeWidth: 2 }}
                      isAnimationActive={false}
                      connectNulls
                      zIndex={chartLayers.line}
                    />
                  ))}
                </LineChart>
              </ResponsiveContainer>
            </div>
          </div>
        );
      })}
    </div>
  );
}
