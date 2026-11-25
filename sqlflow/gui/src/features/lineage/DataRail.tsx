import { useRef, type KeyboardEvent } from "react";
import { ChevronLeft, ChevronRight, CircleCheck, CircleX, Loader2, SkipForward, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import type { FlowFire } from "../../api/types";
import {
  dayLabel, fireFlows, firesShareAMinute, fireTally, fireTallyText, fireTime, formatCount, movementText, neighbourDay, startsWeek,
  type DayTally, type MovementAmount,
} from "./dataLens";
import { counted } from "./graphKinds";
import { LensLegend } from "./LensLegend";
import { useLensHover, useLensHoverDay } from "./lensHover";

/** Up to this many fires a day are offered as buttons side by side; a busier day lists them in a menu. */
const FIRES_AS_BUTTONS = 6;
const WINDOWS = [30, 60, 90] as const;
/** A measure's row: its name and the day's amount beside one bar per day. */
const ROW_HEIGHT = 16;

/** One measure of what moved through the drawing: what it counts, and its amount day by day. */
export interface RailRow {
  key: string;
  title: string;
  /** How many of the drawn flows count this measure: what the row's amounts are sums over. */
  flows: number;
  /** Its amount on each day of the window, oldest first. */
  days: readonly MovementAmount[];
  /** Its amount in the one start looked at; null while the whole day is. */
  fire: MovementAmount | null;
}

/** A fire's glyph: failed, still under way, or done. Told by shape as well as ink. */
function FireMark({ fire }: { fire: FlowFire }) {
  const tally = fireTally(fire);
  if (tally.failed > 0) {
    return <CircleX aria-hidden className="size-3.5 text-destructive" />;
  }

  return tally.underWay > 0
    ? <Loader2 aria-hidden className="size-3.5 animate-spin text-primary" />
    : <CircleCheck aria-hidden className="size-3.5 text-muted-foreground" />;
}

/** A measure's amount as its row says it: the number, with a failure before it in the status colour. */
function Amount({ amount }: { amount: MovementAmount | undefined }) {
  if (amount === undefined || amount.ran === 0) {
    return <span className="shrink-0 text-muted-foreground/70">no run</span>;
  }

  return (
    <span className="flex shrink-0 items-baseline gap-1.5 whitespace-nowrap">
      {amount.failed > 0 && <span className="text-destructive">{amount.failed} failed</span>}
      <span className={cn("text-[12px] tabular-nums", amount.moved > 0 ? "font-semibold text-foreground" : "text-muted-foreground")}>
        {formatCount(amount.moved)}
      </span>
    </span>
  );
}

/**
 * How much moved through the drawing, day by day, and the day the data view looks at. One row per measure, in the
 * order data goes through the drawing (the rows its loads read, the rows its ingestions changed, what its deliveries
 * delivered): the row names the measure, writes its amount for the day, and draws one bar per day of the window, so
 * a reader sees what came in, what it turned into, and on which days. A flow that only checks moves no data and has
 * no row. Clicking a day looks at it, the arrows step to the nearest day anything ran, and a day's starts narrow the
 * drawing to one of them. Pointing at a day writes that day's amounts and lights it in every card's chart.
 */
export function DataRail({
  fromUtc, rows, tallies, day, pinned, fires, firesLoading, totalFires, fireKey, windowDays,
  onPickDay, onLatest, onPickFire, onWindowDays, onClose,
}: {
  fromUtc: string;
  /** The measures of the drawing, in the order data goes through them. */
  rows: readonly RailRow[];
  /** One tally per day of the window, oldest first, over the flows drawn: which days anything ran, and failed. */
  tallies: readonly DayTally[];
  /** The day looked at. */
  day: number;
  /** Whether the reader picked that day; otherwise it is the latest day anything ran, and follows it. */
  pinned: boolean;
  /** The day's fires through the drawn flows, oldest first; undefined while they are read. */
  fires: readonly FlowFire[] | undefined;
  firesLoading: boolean;
  /** Every fire of the day, when the server held more than it sent. */
  totalFires: number;
  /** The fire looked at; empty for the whole day. */
  fireKey: string;
  windowDays: number;
  onPickDay: (day: number) => void;
  onLatest: () => void;
  onPickFire: (key: string) => void;
  onWindowDays: (days: number) => void;
  onClose: () => void;
}) {
  const store = useLensHover();
  const hovered = useLensHoverDay();
  const columns = useRef<HTMLDivElement>(null);
  const shown = hovered !== null && hovered < tallies.length ? hovered : day;
  const earlier = neighbourDay(tallies, day, -1);
  const later = neighbourDay(tallies, day, 1);
  const fire = fires?.find((entry) => entry.key === fireKey);
  const seconds = fires !== undefined && firesShareAMinute(fires);
  // Starts are offered side by side while there are few of them and their times tell them apart; a busier day, or
  // one whose starts fall in the same second, lists them in a menu, where each says what it ran.
  const asButtons = fires !== undefined && fires.length > 1 && fires.length <= FIRES_AS_BUTTONS
    && new Set(fires.map((entry) => fireTime(entry.startedUtc, seconds))).size === fires.length;
  // Dates are written under Mondays, and under every other Monday once the weeks stand too close to read.
  const mondays = tallies.map((_, index) => index).filter((index) => startsWeek(fromUtc, index));
  const dated = new Set(tallies.length > 75 ? mondays.filter((_, at) => at % 2 === 0) : mondays);
  const weekStarts = new Set(mondays);
  // Each row is drawn against its own largest day: the measures count different things, so they share no scale.
  const largest = rows.map((row) => Math.max(1, ...row.days.map((amount) => amount.moved)));
  const ranOn = (index: number) => {
    const tally = tallies[index];
    return tally !== undefined && tally.moved + tally.empty + tally.failed > 0;
  };
  const dayInWords = (index: number) => {
    const said = rows.filter((row) => (row.days[index]?.ran ?? 0) > 0).map((row) => `${row.title} ${movementText(row.days[index])}`);
    return `${dayLabel(fromUtc, index)}: ${said.length > 0 ? said.join("; ") : ranOn(index) ? "ran, no data moved" : "nothing ran"}`;
  };

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    const step = event.key === "ArrowLeft" ? -1 : event.key === "ArrowRight" ? 1 : 0;
    if (step === 0) {
      return;
    }

    event.preventDefault();
    const next = neighbourDay(tallies, day, step);
    if (next !== null) {
      onPickDay(next);
    }
  };

  return (
    <div
      data-testid="data-rail"
      className="@container/rail flex shrink-0 flex-col gap-1 border-t border-border bg-card px-3 pt-1.5 pb-1"
    >
      {/* One line whatever the day holds: a rail that grew with a day's starts would move the canvas under the pointer. */}
      <div className="flex h-7 items-center gap-x-3 overflow-hidden">
        <div className="flex shrink-0 items-center gap-0.5">
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                variant="ghost" size="icon-xs" disabled={earlier === null} aria-label="The day before with a run"
                onClick={() => earlier !== null && onPickDay(earlier)} data-testid="data-rail-earlier"
              >
                <ChevronLeft />
              </Button>
            </TooltipTrigger>
            <TooltipContent>The nearest earlier day anything drawn ran</TooltipContent>
          </Tooltip>
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                variant="ghost" size="icon-xs" disabled={later === null} aria-label="The day after with a run"
                onClick={() => later !== null && onPickDay(later)} data-testid="data-rail-later"
              >
                <ChevronRight />
              </Button>
            </TooltipTrigger>
            <TooltipContent>The nearest later day anything drawn ran</TooltipContent>
          </Tooltip>
        </div>

        <div className="flex min-w-0 items-baseline gap-2" data-testid="data-rail-caption">
          <span className={cn("text-[14px] font-semibold whitespace-nowrap", shown !== day && "text-muted-foreground")} data-testid="data-rail-day">
            {dayLabel(fromUtc, shown)}
          </span>
          {/* The amounts are in the rows below. This says only what they cannot: which start is looked at, that a
              flow failed, or that nothing ran at all. A day that went well adds no words. */}
          <span className="truncate text-[12px] text-muted-foreground">
            {shown === day && fire !== undefined
              ? `start of ${fireTime(fire.startedUtc, seconds)}: ${fireTallyText(fire)}`
              : !ranOn(shown)
                ? "nothing ran"
                : tallies[shown].failed > 0 && <span className="text-destructive">{counted(tallies[shown].failed, "flow")} failed</span>}
          </span>
        </div>

        {/* A day's fires: the whole day by default, or one start and only what it ran. */}
        <div className="flex shrink-0 items-center gap-1" data-testid="data-rail-fires">
          {firesLoading && fires === undefined && <Skeleton className="h-6 w-40 rounded-md" />}
          {fires !== undefined && asButtons && (
            <>
              <Button
                variant="outline" size="xs" aria-pressed={fireKey === ""} onClick={() => onPickFire("")}
                className="h-6 aria-pressed:border-primary aria-pressed:bg-primary/10" data-testid="data-rail-all-day"
              >
                Whole day
              </Button>
              {fires.map((entry) => (
                <Tooltip key={entry.key}>
                  <TooltipTrigger asChild>
                    <Button
                      variant="outline" size="xs" aria-pressed={fireKey === entry.key}
                      onClick={() => onPickFire(fireKey === entry.key ? "" : entry.key)}
                      className="h-6 gap-1 font-mono tabular-nums aria-pressed:border-primary aria-pressed:bg-primary/10"
                      data-testid="data-rail-fire"
                    >
                      <FireMark fire={entry} />
                      {fireTime(entry.startedUtc, seconds)}
                    </Button>
                  </TooltipTrigger>
                  <TooltipContent>
                    Show only what this start ran: {fireFlows(entry)} ({fireTallyText(entry)}).
                  </TooltipContent>
                </Tooltip>
              ))}
            </>
          )}
          {fires !== undefined && fires.length > 1 && !asButtons && (
            <Select value={fireKey === "" ? "day" : fireKey} onValueChange={(value) => onPickFire(value === "day" ? "" : value)}>
              <SelectTrigger size="sm" className="h-6 w-64 text-[12px]" aria-label="Which start of the day to show" data-testid="data-rail-fire-select">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="day">
                  Whole day ({totalFires > fires.length ? `${fires.length} of ${totalFires}` : fires.length} starts)
                </SelectItem>
                {fires.map((entry) => (
                  <SelectItem key={entry.key} value={entry.key}>
                    {fireTime(entry.startedUtc, seconds)} · {fireFlows(entry, 2)} · {fireTallyText(entry)}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          )}
        </div>

        <div className="ml-auto flex shrink-0 items-center gap-1.5">
          <LensLegend />
          {pinned && (
            <Tooltip>
              <TooltipTrigger asChild>
                <Button variant="outline" size="xs" className="h-6 gap-1" onClick={onLatest} data-testid="data-rail-latest">
                  <SkipForward className="size-3" />
                  Latest
                </Button>
              </TooltipTrigger>
              <TooltipContent>Look at the latest day anything drawn ran, and keep following it</TooltipContent>
            </Tooltip>
          )}
          <Select value={String(windowDays)} onValueChange={(value) => onWindowDays(Number(value))}>
            <SelectTrigger size="sm" className="h-6 w-[104px] text-[12px]" aria-label="How many days are charted" data-testid="data-rail-window">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {WINDOWS.map((days) => <SelectItem key={days} value={String(days)}>{days} days</SelectItem>)}
            </SelectContent>
          </Select>
          <Tooltip>
            <TooltipTrigger asChild>
              <Button variant="ghost" size="icon-xs" onClick={onClose} aria-label="Stop showing data" data-testid="data-rail-close">
                <X />
              </Button>
            </TooltipTrigger>
            <TooltipContent>Stop showing data, and draw how the nodes connect</TooltipContent>
          </Tooltip>
        </div>
      </div>

      <div className="flex items-stretch">
        {/* Each measure by name, with its amount on the day looked at, or on the day under the pointer. */}
        <div className="flex w-[208px] shrink-0 flex-col pt-[3px] pr-3 @max-[900px]/rail:w-[152px]" data-testid="data-rail-measures">
          {rows.length === 0 && (
            <span className="truncate text-[11px] text-muted-foreground" style={{ lineHeight: `${ROW_HEIGHT}px` }}>
              No flow drawn moved data
            </span>
          )}
          {rows.map((row) => {
            const amount = shown === day && row.fire !== null ? row.fire : row.days[shown];
            const files = amount?.files ?? 0;
            return (
              <div
                key={row.key}
                className="flex items-center gap-2 text-[11px] leading-none"
                style={{ height: ROW_HEIGHT }}
                data-testid="data-rail-measure"
                data-measure={row.key}
              >
                <Tooltip>
                  <TooltipTrigger asChild>
                    <span className="min-w-0 flex-1 truncate text-muted-foreground">
                      {row.title}
                      {files > 0 && ` · ${formatCount(files)} ${files === 1 ? "file" : "files"}`}
                    </span>
                  </TooltipTrigger>
                  <TooltipContent>
                    {row.title}, added up over the {counted(row.flows, "flow")} drawn that {row.flows === 1 ? "counts" : "count"} it.
                  </TooltipContent>
                </Tooltip>
                <Amount amount={amount} />
              </div>
            );
          })}
        </div>

        {/* One column per day. The columns are one control: the arrow keys step it by days with a run. */}
        <div
          ref={columns}
          role="slider"
          tabIndex={0}
          aria-label="The day looked at"
          aria-valuemin={0}
          aria-valuemax={tallies.length - 1}
          aria-valuenow={day}
          aria-valuetext={dayInWords(day)}
          onKeyDown={onKeyDown}
          onMouseLeave={() => store.set(null)}
          className="relative flex min-w-0 flex-1 items-stretch rounded-sm outline-none focus-visible:ring-2 focus-visible:ring-ring"
          data-testid="data-rail-days"
        >
          {tallies.map((_, index) => {
            const focus = index === day || index === hovered;
            return (
              <button
                key={index}
                type="button"
                tabIndex={-1}
                aria-label={dayInWords(index)}
                onMouseEnter={() => store.set(index)}
                onClick={() => onPickDay(index)}
                data-testid="data-rail-column"
                data-picked={index === day || undefined}
                className={cn(
                  "relative flex min-w-0 flex-1 cursor-pointer flex-col pt-[3px]",
                  weekStarts.has(index) && "border-l border-border/70",
                  index === day ? "bg-primary/15" : index === hovered ? "bg-foreground/10" : "hover:bg-foreground/10",
                )}
              >
                {rows.map((row, at) => {
                  const amount = row.days[index];
                  const ran = amount !== undefined && amount.ran > 0;
                  // A day that ran and moved nothing has no amount to draw: a stub at the baseline says it ran.
                  const tall = ran && amount.moved > 0 ? 2 + Math.sqrt(amount.moved / largest[at]) * (ROW_HEIGHT - 6) : 2;
                  return (
                    <span key={row.key} className="relative block w-full border-b border-border/60" style={{ height: ROW_HEIGHT }}>
                      {ran && (
                        <span
                          className={cn(
                            "absolute bottom-0 left-1/2 w-[56%] min-w-[2px] -translate-x-1/2 rounded-t-[1px]",
                            amount.moved > 0 ? "bg-chart-1" : "bg-muted-foreground",
                            focus ? "opacity-100" : amount.moved > 0 ? "opacity-75" : "opacity-40",
                          )}
                          style={{ height: tall }}
                        />
                      )}
                      {ran && amount.failed > 0 && (
                        <span className="absolute bottom-0 left-1/2 h-[2px] w-[56%] min-w-[2px] -translate-x-1/2 bg-destructive" />
                      )}
                    </span>
                  );
                })}
                <span className="relative block h-[13px] w-full">
                  {dated.has(index) && (
                    <span className="pointer-events-none absolute top-px left-1 text-[9px] leading-[12px] whitespace-nowrap text-muted-foreground">
                      {dayLabel(fromUtc, index, false)}
                    </span>
                  )}
                </span>
                {index === day && <span aria-hidden className="absolute inset-x-0 top-0 h-[3px] rounded-b-sm bg-primary" />}
              </button>
            );
          })}
        </div>
      </div>
    </div>
  );
}
