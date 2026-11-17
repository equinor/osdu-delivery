// The views of a report that are drawn in the page itself, without a chart library: figures, ranked bars, the share
// bar, the matrix and the table. Each takes a plan the pure model made (reportModel.planView) and draws it; none of
// them reads the answer on its own. The column and line charts are in ReportChart.

import { useMemo, useState, type CSSProperties, type ReactNode } from "react";
import { DataTable, type Column } from "@/components/DataTable";
import { SummaryStrip } from "@/components/SummaryStrip";
import { TablePager } from "@/components/TablePager";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { usePagedRows } from "@/hooks/useTablePaging";
import { cn } from "@/lib/utils";
import { useThemeMode } from "@/theme/ThemeModeContext";
import {
  assignSlots, formatCompact, formatNumber, formatValue, MAX_SERIES,
  type BarPlan, type FigureItem, type MatrixPlan, type Row, type Series, type SharePlan, type ShownColumn,
} from "./reportModel";

/** What a click on a mark does, for a view whose document names a parameter to set. */
export interface Selecting {
  /** The caption of the parameter a click sets, for the mark's accessible name. */
  label: string;
  /** Whether the mark of this value is the one the parameter is set to now. */
  isSelected: (raw: string | null) => boolean;
  onSelect: (raw: string | null) => void;
}

// ---- Colour ----------------------------------------------------------------------------------------------------------

/** The quiet tone of what was folded together ("Other"): never a series colour, so it is never taken for a series. */
const OTHER_FILL = "color-mix(in oklab, var(--muted-foreground) 45%, var(--card))";

/** The fill of a series slot, as a CSS colour the page's own elements take. */
export function slotFill(slot: number): string {
  return `var(--chart-${(slot % MAX_SERIES) + 1})`;
}

/**
 * The colour slot of each series of one view, kept for as long as the view is on the page: a series keeps its colour
 * when a filter changes which series are drawn around it (reportModel.assignSlots).
 */
export function useSeriesSlots(series: readonly Series[]): (series: Series) => number {
  const [held, setHeld] = useState<ReadonlyMap<string, number>>(() => new Map());
  const keys = series.filter((s) => s.other !== true).map((s) => s.key);
  const next = assignSlots(held, keys);
  if (next !== held) {
    // Derived from what is drawn now and what was drawn before, so it is settled while rendering, not after.
    setHeld(next);
  }

  return (s) => next.get(s.key) ?? 0;
}

/** The colours a chart library needs as literal text, read from the design's tokens and read again when it changes. */
export function useReportInk() {
  const { palette } = useThemeMode();
  return useMemo(() => {
    const token = (name: string) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    return {
      series: Array.from({ length: MAX_SERIES }, (_, slot) => token(`--chart-${slot + 1}`)),
      axis: token("--muted-foreground"),
      grid: token("--border"),
      cursor: token("--muted"),
      surface: token("--card"),
    };
    // The tokens are read from the document, which `palette` stands for: a new design or mode is a new set.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [palette]);
}

/** The names of a view's series beside the mark each is drawn with; present whenever there are two or more. */
export function SeriesLegend({ series, fillOf, mark = "bar" }: {
  series: readonly Series[];
  fillOf: (series: Series) => string;
  mark?: "bar" | "line";
}) {
  if (series.length < 2) return null;
  return (
    <ul className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-muted-foreground" data-testid="report-legend">
      {series.map((s) => (
        <li key={s.key} className="inline-flex min-w-0 items-center gap-1.5">
          <span
            className={mark === "line" ? "h-0.5 w-3 shrink-0 rounded-full" : "size-2.5 shrink-0 rounded-sm"}
            style={{ background: fillOf(s) }}
            aria-hidden
          />
          <span className="truncate">{s.label}</span>
        </li>
      ))}
    </ul>
  );
}

/** What a value reads as when the answer holds none. */
function NoValue() {
  return <span className="font-sans italic text-muted-foreground">NULL</span>;
}

// ---- Figures -----------------------------------------------------------------------------------------------------------

/** What one figure shows: the value, and after it, quieter, what belongs to it but is not the figure itself. */
function figureText(item: FigureItem): { value: string; caption: string | undefined } {
  const { column, value } = item;
  if (value === null) return { value: "No value", caption: undefined };

  // A date and time is its date, with the time of day after it.
  if (column.format === "datetime") {
    const whole = formatValue(value, column) ?? value;
    const [date, time] = whole.split(" ");
    return { value: date, caption: time };
  }

  // A number leads without its unit, which follows it; a long one is shortened, with the exact figure after it.
  const bare = formatValue(value, { ...column, unit: null }) ?? value;
  const numeric = column.format === "integer" || column.format === "number";
  if (numeric && bare.length > 11 && Number.isFinite(Number(value))) {
    return { value: formatCompact(Number(value)), caption: [bare, column.unit].filter(Boolean).join(" ") };
  }

  return { value: bare, caption: column.unit ?? undefined };
}

/** How narrow one figure of a strip may be before the strip takes a second line; a date and its time need more. */
const FIGURE_MIN_WIDTH = 132;
const STAMP_MIN_WIDTH = 192;

/**
 * A query's figures as one strip: a caption over each value, hairlines between them, at the height of a toolbar. A
 * report leads with its figures, and a row of cards as tall as a chart would push the charts off the first screen.
 */
export function FigureView({ items, testId }: { items: readonly FigureItem[]; testId: string }) {
  return (
    <SummaryStrip
      minCellWidth={FIGURE_MIN_WIDTH}
      fill
      data-testid={testId}
      cells={items.map((item) => {
        const text = figureText(item);
        return {
          label: item.column.label,
          value: (
            <>
              <span className="truncate">{text.value}</span>
              {text.caption !== undefined && (
                <span className="truncate text-xs font-normal text-muted-foreground">{text.caption}</span>
              )}
            </>
          ),
          testId: `${testId}-${item.column.index}`,
          minWidth: item.column.format === "datetime" && item.value !== null ? STAMP_MIN_WIDTH : undefined,
        };
      })}
    />
  );
}

// ---- Bars --------------------------------------------------------------------------------------------------------------

/** How many characters of a name a bar list gives its names before it clips them. */
const NAME_CHARACTERS = 26;

function tipLine(label: string, value: string, fill: string): ReactNode {
  return (
    <span key={label} className="flex items-center gap-2">
      <span className="size-2 shrink-0 rounded-sm" style={{ background: fill }} aria-hidden />
      <span className="font-mono font-medium tabular-nums">{value}</span>
      <span className="text-muted-foreground">{label}</span>
    </span>
  );
}

/**
 * Ranked horizontal bars: a name, its bar and its figure on each row, every bar on one baseline, in the order the query
 * answered. Several series stack on one bar or sit under each other; a row is a button when a click sets a parameter.
 */
export function BarView({ plan, selecting, testId }: { plan: BarPlan; selecting: Selecting | null; testId: string }) {
  const [all, setAll] = useState(false);
  const slotOf = useSeriesSlots(plan.series);
  const fillOf = (series: Series) => (series.other === true ? OTHER_FILL : slotFill(slotOf(series)));

  // One row more than the limit is shown as it is: "show all 13" over twelve rows hides one row behind a click.
  const folds = plan.rows.length > plan.top + 1;
  const first = folds && !all ? plan.rows.slice(0, plan.top) : plan.rows;
  // The row in effect is never left behind "Show all": it follows the first ones, so what is chosen is always in view.
  const chosen = folds && !all && selecting !== null
    ? plan.rows.slice(plan.top).find((row) => selecting.isSelected(row.raw))
    : undefined;
  const rows = chosen === undefined ? first : [...first, chosen];
  const span = plan.max - plan.min || 1;
  const zero = ((0 - plan.min) / span) * 100;
  const anySelected = selecting !== null && rows.some((row) => selecting.isSelected(row.raw));
  const longestName = Math.min(NAME_CHARACTERS, Math.max(4, ...rows.map((row) => row.label.length)));
  const single = plan.series.length === 1;
  const totalColumn = plan.series[0].column;

  const totals = rows.map((row) => row.values.reduce<number>((sum, value) => sum + (value ?? 0), 0));
  const figures = rows.map((row, index) => (plan.stacked || single
    ? (single ? row.values[0] === null ? "" : formatNumber(row.values[0], totalColumn) : formatNumber(totals[index], totalColumn))
    : ""));
  const figureWidth = Math.max(0, ...figures.map((figure) => figure.length));

  const template: CSSProperties = {
    gridTemplateColumns: `minmax(0, ${longestName + 1}ch) minmax(3rem, 1fr)${figureWidth > 0 ? ` ${figureWidth + 1}ch` : ""}`,
  };

  return (
    <div className="flex flex-col gap-2" data-testid={testId}>
      <SeriesLegend series={plan.series} fillOf={fillOf} />
      {/* Every row, once asked for, scrolls in the room of a dozen, so the tile keeps its place among its neighbours. */}
      <ul className={cn("flex flex-col", folds && all && "max-h-80 overflow-y-auto pr-1")}>
        {rows.map((row, index) => {
          const selected = selecting?.isSelected(row.raw) === true;
          // While one row is in effect the others step back: their bars fade and their text goes quiet, still legible.
          const aside = anySelected && !selected;
          const body = (
            <>
              <span className="truncate text-left text-[13px]" title={row.label}>{row.label}</span>
              <span className="flex min-w-0 flex-col justify-center gap-0.5">
                {plan.stacked
                  ? (
                    // One bar, each series a segment, a hairline of the surface between them.
                    <span
                      className={cn("flex h-4 gap-[2px] transition-opacity", aside && "opacity-35")}
                      style={{ width: `${(totals[index] / span) * 100}%` }}
                    >
                      {plan.series.map((series, s) => {
                        const value = row.values[s];
                        if (value === null || value <= 0) return null;
                        const last = !plan.series.slice(s + 1).some((_, later) => (row.values[s + 1 + later] ?? 0) > 0);
                        return (
                          <span
                            key={series.key}
                            className={cn("h-full min-w-0", last && "rounded-r-[4px]")}
                            style={{ flexGrow: value, flexBasis: 0, background: fillOf(series) }}
                          />
                        );
                      })}
                    </span>
                  )
                  : plan.series.map((series, s) => {
                    const value = row.values[s];
                    const width = value === null ? 0 : (Math.abs(value) / span) * 100;
                    const below = value !== null && value < 0;
                    return (
                      <span key={series.key} className="flex items-center gap-2">
                        <span className={cn("relative min-w-0 flex-1", single ? "h-4" : "h-2")}>
                          {plan.min < 0 && (
                            <span className="absolute inset-y-[-2px] w-px bg-border" style={{ left: `${zero}%` }} aria-hidden />
                          )}
                          <span
                            className={cn(
                              "absolute inset-y-0 transition-opacity", below ? "rounded-l-[4px]" : "rounded-r-[4px]",
                              aside && "opacity-35",
                            )}
                            style={{ left: `${below ? zero - width : zero}%`, width: `${width}%`, background: fillOf(series) }}
                          />
                        </span>
                        {!single && (
                          <span className="w-[9ch] shrink-0 text-right font-mono text-[11px] tabular-nums text-muted-foreground">
                            {value === null ? "" : formatNumber(value, series.column)}
                          </span>
                        )}
                      </span>
                    );
                  })}
              </span>
              {figureWidth > 0 && (
                <span className="text-right font-mono text-xs tabular-nums">{figures[index]}</span>
              )}
            </>
          );

          const rowClass = cn(
            "grid w-full items-center gap-x-3 rounded-md px-2 py-1",
            aside && "text-muted-foreground",
            selected && "bg-accent font-medium",
          );
          const line = selecting !== null
            ? (
              <button
                type="button"
                className={cn(rowClass, "cursor-pointer hover:bg-accent/60 focus-visible:bg-accent/60 focus-visible:outline-1 focus-visible:outline-ring")}
                style={template}
                aria-pressed={selected}
                aria-label={`${row.label}: ${selected ? "clear" : "set"} ${selecting.label}`}
                onClick={() => selecting.onSelect(row.raw)}
                data-testid={`${testId}-row`}
              >
                {body}
              </button>
            )
            : <div className={rowClass} style={template} data-testid={`${testId}-row`}>{body}</div>;

          return (
            <li key={`${row.raw ?? ""}-${index}`}>
              {single
                ? line
                : (
                  // Several series on one row: the hover names each with its figure, since a stack prints only its total.
                  <Tooltip delayDuration={250} disableHoverableContent>
                    <TooltipTrigger asChild>{line}</TooltipTrigger>
                    <TooltipContent variant="panel" side="top" align="center" className="pointer-events-none">
                      <div className="font-medium">{row.label}</div>
                      <div className="mt-1 flex flex-col gap-0.5">
                        {plan.series.map((series, s) => (row.values[s] === null
                          ? null
                          : tipLine(series.label, formatNumber(row.values[s] ?? 0, series.column), fillOf(series))))}
                      </div>
                    </TooltipContent>
                  </Tooltip>
                )}
            </li>
          );
        })}
      </ul>
      {folds && (
        <button
          type="button"
          className="self-start rounded px-2 text-xs text-muted-foreground underline-offset-2 hover:text-foreground hover:underline focus-visible:outline-1 focus-visible:outline-ring"
          onClick={() => setAll((shown) => !shown)}
          data-testid={`${testId}-all`}
        >
          {all ? `Show the first ${plan.top}` : `Show all ${plan.rows.length}`}
        </button>
      )}
    </div>
  );
}

// ---- Share -------------------------------------------------------------------------------------------------------------

/**
 * One whole split into its parts: a thin bar divided in proportion, over a list that gives each part its name, its
 * amount and its share. The list carries the figures, so the bar prints none; each row's swatch ties it to its part of
 * the bar. A row is a button when a click sets a parameter.
 */
export function ShareView({ plan, selecting, testId }: { plan: SharePlan; selecting: Selecting | null; testId: string }) {
  const series = plan.parts.map((part): Series => ({ key: part.label, label: part.label, column: plan.value, other: part.other }));
  const slotOf = useSeriesSlots(series);
  const fillOf = (index: number) => (plan.parts[index].other === true ? OTHER_FILL : slotFill(slotOf(series[index])));
  const percent = (share: number) => new Intl.NumberFormat(undefined, { style: "percent", maximumFractionDigits: share < 0.1 ? 1 : 0 }).format(share);
  const anySelected = selecting !== null && plan.parts.some((part) => part.other !== true && selecting.isSelected(part.raw));
  const amounts = plan.parts.map((part) => formatNumber(part.value, plan.value));
  const amountWidth = Math.max(...amounts.map((amount) => amount.length));

  return (
    <div className="flex flex-col gap-3" data-testid={testId}>
      <div
        className="flex h-2 w-full gap-[2px]"
        role="img"
        aria-label={plan.parts.map((part) => `${part.label} ${percent(part.share)}`).join(", ")}
      >
        {plan.parts.map((part, index) => {
          const selected = part.other !== true && selecting?.isSelected(part.raw) === true;
          return (
            <span
              key={part.label}
              className={cn(
                "h-full min-w-[3px] transition-opacity first:rounded-l-full last:rounded-r-full",
                anySelected && !selected && "opacity-40",
              )}
              style={{ flexGrow: part.value, flexBasis: 0, background: fillOf(index) }}
              data-testid={`${testId}-part`}
            />
          );
        })}
      </div>
      <ul className="flex flex-col">
        {plan.parts.map((part, index) => {
          const selected = part.other !== true && selecting?.isSelected(part.raw) === true;
          const body = (
            <>
              <span
                className={cn("size-2.5 shrink-0 rounded-sm transition-opacity", anySelected && !selected && "opacity-40")}
                style={{ background: fillOf(index) }}
                aria-hidden
              />
              <span className="min-w-0 flex-1 truncate text-left text-[13px]" title={part.label}>{part.label}</span>
              <span className="shrink-0 text-right font-mono text-xs tabular-nums" style={{ width: `${amountWidth + 1}ch` }}>{amounts[index]}</span>
              <span className="w-[6ch] shrink-0 text-right font-mono text-xs tabular-nums text-muted-foreground">{percent(part.share)}</span>
            </>
          );
          const rowClass = cn(
            "flex w-full items-center gap-2 rounded-md px-2 py-1",
            anySelected && !selected && "text-muted-foreground",
            selected && "bg-accent font-medium",
          );
          return (
            <li key={part.label}>
              {selecting !== null && part.other !== true
                ? (
                  <button
                    type="button"
                    className={cn(rowClass, "cursor-pointer hover:bg-accent/60 focus-visible:bg-accent/60 focus-visible:outline-1 focus-visible:outline-ring")}
                    aria-pressed={selected}
                    aria-label={`${part.label}: ${selected ? "clear" : "set"} ${selecting.label}`}
                    onClick={() => selecting.onSelect(part.raw)}
                    data-testid={`${testId}-row`}
                  >
                    {body}
                  </button>
                )
                : <div className={rowClass} data-testid={`${testId}-row`}>{body}</div>}
            </li>
          );
        })}
        <li className="mt-1 flex items-center gap-2 border-t border-border px-2 pt-1.5 text-xs text-muted-foreground">
          <span className="size-2.5 shrink-0" aria-hidden />
          <span className="min-w-0 flex-1 truncate">{plan.value.label}, in all</span>
          <span className="shrink-0 text-right font-mono tabular-nums text-foreground" style={{ width: `${amountWidth + 1}ch` }}>
            {formatNumber(plan.total, plan.value)}
          </span>
          <span className="w-[6ch] shrink-0" aria-hidden />
        </li>
      </ul>
    </div>
  );
}

// ---- Matrix ------------------------------------------------------------------------------------------------------------

/**
 * One value by two names: rows down, columns across, each cell's figure on a wash of one hue that deepens with the
 * figure, so the eye finds the large cells and the figure is still read as text.
 */
export function MatrixView({ plan, selecting, testId }: { plan: MatrixPlan; selecting: Selecting | null; testId: string }) {
  const anySelected = selecting !== null && plan.rows.some((row) => selecting.isSelected(row.raw));
  const wash = (value: number | null): CSSProperties | undefined => {
    if (value === null || plan.max === 0) return undefined;
    const depth = 6 + Math.round((Math.abs(value) / plan.max) * 46);
    return { background: `color-mix(in oklab, var(--chart-1) ${depth}%, transparent)` };
  };

  return (
    <div className="overflow-x-auto" data-testid={testId}>
      <table className="w-full border-separate border-spacing-[2px] text-xs">
        <thead>
          <tr>
            <th scope="col" className="px-2 py-1 text-left font-medium text-muted-foreground">{plan.by.label}</th>
            {plan.columns.map((column) => (
              <th key={column} scope="col" className="max-w-40 truncate px-2 py-1 text-right font-medium text-muted-foreground" title={column}>
                {column}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {plan.rows.map((row, r) => {
            const selected = selecting?.isSelected(row.raw) === true;
            return (
              <tr key={row.label} className={cn("transition-opacity", anySelected && !selected && "opacity-60")}>
                <th scope="row" className="max-w-56 px-2 py-1 text-left font-normal">
                  {selecting !== null
                    ? (
                      <button
                        type="button"
                        className={cn(
                          "block max-w-full cursor-pointer truncate rounded px-1 text-left text-[13px] hover:bg-accent/60 focus-visible:outline-1 focus-visible:outline-ring",
                          selected && "bg-accent",
                        )}
                        aria-pressed={selected}
                        aria-label={`${row.label}: ${selected ? "clear" : "set"} ${selecting.label}`}
                        onClick={() => selecting.onSelect(row.raw)}
                        data-testid={`${testId}-row`}
                      >
                        {row.label}
                      </button>
                    )
                    : <span className="block truncate text-[13px]" title={row.label}>{row.label}</span>}
                </th>
                {plan.cells[r].map((cell, c) => (
                  <td
                    key={plan.columns[c]}
                    className="rounded-[3px] px-2 py-1 text-right font-mono tabular-nums"
                    style={wash(cell)}
                    title={cell === null ? undefined : `${row.label}, ${plan.columns[c]}: ${formatNumber(cell, plan.value)}`}
                  >
                    {cell === null ? <span className="text-muted-foreground/60">-</span> : formatNumber(cell, plan.value)}
                  </td>
                ))}
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

// ---- Table -------------------------------------------------------------------------------------------------------------

const RIGHT_ALIGNED = new Set(["integer", "number", "percent", "bytes", "duration"]);

/** How many rows a report's table shows on a page until a reader picks another size. */
const TABLE_PAGE_SIZE = 25;

/**
 * The rows as they are, each value written as its column is, a page at a time. A row is clickable when a click sets a
 * parameter to its `by` value.
 */
export function TableView({ columns, rows, by, selecting, listKey, testId }: {
  columns: readonly ShownColumn[];
  rows: readonly Row[];
  /** The column a click reads its value from, when the view sets a parameter. */
  by: ShownColumn | null;
  selecting: Selecting | null;
  /** What names this list of rows, so a new answer starts on its first page. */
  listKey: string;
  testId: string;
}) {
  const indexed = useMemo(() => rows.map((row, index) => ({ row, index })), [rows]);
  // A report's table shares its page with the report's other tiles, so it starts at the shortest page.
  const paged = usePagedRows(indexed, listKey, TABLE_PAGE_SIZE);
  const tableColumns = useMemo<Column<{ row: Row; index: number }>[]>(() => columns.map((column) => ({
    id: `c${column.index}`,
    header: column.label,
    align: RIGHT_ALIGNED.has(column.format) ? "right" : "left",
    render: ({ row }) => {
      const text = formatValue(row[column.index], column);
      if (text === null) return <NoValue />;
      return column.format === "text"
        ? <span className="text-[13px]">{text}</span>
        : <span className="font-mono text-xs tabular-nums">{text}</span>;
    },
  })), [columns]);

  const clickable = selecting !== null && by !== null;
  return (
    <DataTable
      columns={tableColumns}
      rows={paged.rows}
      rowKey={(entry) => entry.index}
      onRowClick={clickable ? (entry) => selecting.onSelect(entry.row[by.index]) : undefined}
      rowSx={clickable ? (entry) => (selecting.isSelected(entry.row[by.index]) ? { background: "var(--accent)" } : undefined) : undefined}
      emptyMessage="No rows."
      minWidth={columns.length > 6 ? columns.length * 132 : undefined}
      footer={rows.length > TABLE_PAGE_SIZE ? <TablePager {...paged.pager} testId={`${testId}-pager`} /> : undefined}
      data-testid={testId}
    />
  );
}
