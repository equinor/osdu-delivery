import type { ReactNode } from "react";
import { CalendarDays, ChevronLeft, ChevronRight, SkipForward } from "lucide-react";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { dayLabel, neighbourDay, type DayTally } from "./dataLens";
import { LensLegend } from "./LensLegend";

/** A control of the line under the bar: at the line's height, and quiet until it is pointed at. */
const LINE_BUTTON = "inline-flex h-5 shrink-0 cursor-pointer items-center justify-center gap-1 rounded-sm text-muted-foreground hover:bg-accent hover:text-foreground disabled:cursor-default disabled:opacity-40 disabled:hover:bg-transparent disabled:hover:text-muted-foreground";

function Step({ to, label, hint, testId, onPick, children }: {
  to: number | null;
  label: string;
  hint: string;
  testId: string;
  onPick: (day: number) => void;
  children: ReactNode;
}) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <button
          type="button" disabled={to === null} aria-label={label} data-testid={testId}
          onClick={() => to !== null && onPick(to)} className={`${LINE_BUTTON} w-5`}
        >
          {children}
        </button>
      </TooltipTrigger>
      <TooltipContent>{hint}</TooltipContent>
    </Tooltip>
  );
}

/**
 * The day the data view looks at, in the line under the bar and there only while What moved is on: its name between
 * the arrows that step to the nearest day anything drawn ran, the way back to the latest day, and the key to the
 * lines. It says which day every card is about and nothing else. How much a node moved is on its own card, and a
 * node's days are in its own details, so nothing here is about a node the reader did not pick.
 */
export function DayControl({ fromUtc, day, tallies, pinned, onPickDay, onLatest }: {
  fromUtc: string;
  /** The day looked at. */
  day: number;
  /** One tally per day of the window, oldest first, over the flows drawn: which days anything ran. */
  tallies: readonly DayTally[];
  /** Whether the reader picked that day; otherwise it is the latest day anything ran, and follows it. */
  pinned: boolean;
  onPickDay: (day: number) => void;
  onLatest: () => void;
}) {
  const tally = tallies[day];
  const ran = tally !== undefined && tally.moved + tally.empty + tally.failed > 0;

  return (
    <span
      role="group"
      aria-label="The day looked at"
      data-testid="graph-day-control"
      className="inline-flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1"
    >
      <span className="inline-flex items-center gap-1">
        <CalendarDays aria-hidden className="size-3.5 shrink-0 text-primary" />
        <Step
          to={neighbourDay(tallies, day, -1)} label="The day before with a run" hint="The nearest earlier day anything drawn ran"
          testId="graph-day-earlier" onPick={onPickDay}
        >
          <ChevronLeft aria-hidden className="size-3.5" />
        </Step>
        {/* A set width, so the arrows stand still under the pointer while the days are stepped through. */}
        <Tooltip>
          <TooltipTrigger asChild>
            <span className="min-w-[78px] text-center font-medium whitespace-nowrap tabular-nums" data-testid="graph-day">
              {dayLabel(fromUtc, day)}
            </span>
          </TooltipTrigger>
          <TooltipContent>Every card says what its node moved on this day.</TooltipContent>
        </Tooltip>
        <Step
          to={neighbourDay(tallies, day, 1)} label="The day after with a run" hint="The nearest later day anything drawn ran"
          testId="graph-day-later" onPick={onPickDay}
        >
          <ChevronRight aria-hidden className="size-3.5" />
        </Step>
      </span>

      {!ran && <span className="text-muted-foreground" data-testid="graph-day-empty">nothing drawn ran</span>}

      {pinned && (
        <Tooltip>
          <TooltipTrigger asChild>
            <button type="button" onClick={onLatest} className={`${LINE_BUTTON} px-1.5`} data-testid="graph-day-latest">
              <SkipForward aria-hidden className="size-3" />
              Latest
            </button>
          </TooltipTrigger>
          <TooltipContent>Look at the latest day anything drawn ran, and keep following it</TooltipContent>
        </Tooltip>
      )}

      <LensLegend />
    </span>
  );
}
