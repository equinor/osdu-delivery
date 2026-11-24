import { useRef, type KeyboardEvent } from "react";
import { ChevronLeft, ChevronRight, CircleCheck, CircleX, Loader2, SkipForward, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import type { FlowFire } from "../../api/types";
import {
  dayLabel, fireFlows, firesShareAMinute, fireTally, fireTallyText, fireTime, neighbourDay, startsWeek, tallyText, type DayTally,
} from "./dataLens";
import { counted } from "./graphKinds";
import { useLensHover, useLensHoverDay } from "./lensHover";

/** Up to this many fires a day are offered as buttons side by side; a busier day lists them in a menu. */
const FIRES_AS_BUTTONS = 6;
const WINDOWS = [30, 60, 90] as const;

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

/**
 * The day the data view looks at, and the way to another. A chart of the window stands one column per day for how
 * many of the drawn flows moved data, ran with no change, or had a failed run, so the days worth looking at are
 * found by eye; the day looked at is marked on it and named in words beside it. Clicking a day looks at it, the
 * arrows step to the nearest day anything ran, and a day's fires narrow the drawing to one of them. Pointing at a
 * day lights it in every card's chart.
 */
export function DataRail({
  fromUtc, tallies, day, pinned, flows, fires, firesLoading, totalFires, fireKey, windowDays,
  onPickDay, onLatest, onPickFire, onWindowDays, onClose,
}: {
  fromUtc: string;
  /** One tally per day of the window, oldest first, over the flows drawn. */
  tallies: readonly DayTally[];
  /** The day looked at. */
  day: number;
  /** Whether the reader picked that day; otherwise it is the latest day anything ran, and follows it. */
  pinned: boolean;
  /** How many flows the drawing holds: what the tallies are counts of. */
  flows: number;
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
  const largest = Math.max(1, ...tallies.map((tally) => tally.moved + tally.empty + tally.failed));
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
      className="flex shrink-0 flex-col gap-1 border-t border-border bg-card px-3 pt-1.5 pb-1"
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
          <span className="truncate text-[12px] text-muted-foreground">
            {shown === day && fire !== undefined
              ? `start of ${fireTime(fire.startedUtc, seconds)}: ${fireTallyText(fire)}`
              : `${tallyText(tallies[shown])} of ${counted(flows, "flow")} drawn`}
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

      {/* One column per day. The columns are one control: the arrow keys step it by days with a run. */}
      <div
        ref={columns}
        role="slider"
        tabIndex={0}
        aria-label="The day looked at"
        aria-valuemin={0}
        aria-valuemax={tallies.length - 1}
        aria-valuenow={day}
        aria-valuetext={`${dayLabel(fromUtc, day)}: ${tallyText(tallies[day])}`}
        onKeyDown={onKeyDown}
        onMouseLeave={() => store.set(null)}
        className="relative flex h-[44px] items-stretch rounded-sm outline-none focus-visible:ring-2 focus-visible:ring-ring"
        data-testid="data-rail-days"
      >
        {tallies.map((tally, index) => {
          const total = tally.moved + tally.empty + tally.failed;
          const share = (count: number) => `${(count / largest) * 100}%`;
          return (
            <button
              key={index}
              type="button"
              tabIndex={-1}
              aria-label={`${dayLabel(fromUtc, index)}: ${tallyText(tally)}`}
              onMouseEnter={() => store.set(index)}
              onClick={() => onPickDay(index)}
              data-testid="data-rail-column"
              data-picked={index === day || undefined}
              className={cn(
                "group relative flex min-w-0 flex-1 cursor-pointer flex-col items-center justify-end pt-1 pb-[13px]",
                weekStarts.has(index) && "border-l border-border/70",
                index === day ? "bg-primary/15" : index === hovered ? "bg-foreground/10" : "hover:bg-foreground/10",
              )}
            >
              {/* Worst at the foot, where the eye lands; a flow that ran and changed nothing is the least news of
                  the three and is drawn the quietest. */}
              <span className="flex h-full w-[64%] min-w-[2px] flex-col justify-end overflow-hidden rounded-t-[2px]">
                <span className="block w-full bg-muted-foreground/30" style={{ height: share(tally.empty) }} />
                <span className="block w-full bg-chart-1" style={{ height: share(tally.moved) }} />
                <span className="block w-full bg-destructive" style={{ height: share(tally.failed) }} />
                {total === 0 && <span className="block h-px w-full bg-border" />}
              </span>
              {dated.has(index) && (
                <span className="pointer-events-none absolute bottom-0 left-1 text-[9px] leading-[12px] whitespace-nowrap text-muted-foreground">
                  {dayLabel(fromUtc, index, false)}
                </span>
              )}
              {index === day && <span aria-hidden className="absolute inset-x-0 top-0 h-[3px] rounded-b-sm bg-primary" />}
            </button>
          );
        })}
      </div>
    </div>
  );
}
