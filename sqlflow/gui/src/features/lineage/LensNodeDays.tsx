import { useState } from "react";
import { ChevronLeft, ChevronRight } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { dayLabel, nodeDays, startsWeek, type LensBar } from "./dataLens";
import { counted } from "./graphKinds";
import { LensBars } from "./LensBars";
import { useLensHover } from "./lensHover";

/** The days listed before the rest open on request: the latest ones are what a reader comes for. */
const DAYS_SHOWN = 5;
/** The chart is drawn at this size and takes the column's width, whatever that is. */
const CHART_WIDTH = 348;
const CHART_HEIGHT = 56;

/**
 * The selected node's own days: its card's chart at the column's width, and under it every day it ran with what it
 * did that day in words, the latest first. It is about this node and nothing else, so its amounts are never added to
 * another node's. A click on a bar or on a row looks at that day in the whole drawing, and the arrows step over the
 * days this node ran, which is how a reader walks one flow's history without leaving it. Pointing at a day lights
 * the same day in every card's chart.
 */
export function LensNodeDays({ fromUtc, bars, day, lineOf, onPick }: {
  fromUtc: string;
  /** The node's chart over the window, oldest day first: the same bars its card draws. */
  bars: readonly LensBar[];
  /** The day looked at. */
  day: number;
  /** What the node did on a day, in one line: "495k new rows · of 495k read". */
  lineOf: (day: number) => string;
  onPick: (day: number) => void;
}) {
  const store = useLensHover();
  const [all, setAll] = useState(false);
  const { ran, earlier, later, listed: latest } = nodeDays(bars, day, DAYS_SHOWN);
  const listed = all ? [...ran].reverse() : latest;
  // Dates are written under Mondays, and under every other Monday once the weeks stand too close to read.
  const mondays = bars.map((_, index) => index).filter((index) => startsWeek(fromUtc, index));
  const dated = bars.length > 45 ? mondays.filter((_, at) => at % 2 === 0) : mondays;

  return (
    <section className="mt-3 flex flex-col gap-1.5" data-testid="lens-days">
      <div className="flex h-5 items-center gap-0.5">
        <h4 className="flex-1 text-[11px] font-medium tracking-wider text-muted-foreground uppercase">
          Day by day
        </h4>
        <Tooltip>
          <TooltipTrigger asChild>
            <Button
              variant="ghost" size="icon-xs" className="size-5" disabled={earlier === null} aria-label="The day before that it ran"
              onClick={() => earlier !== null && onPick(earlier)} data-testid="lens-days-earlier"
            >
              <ChevronLeft />
            </Button>
          </TooltipTrigger>
          <TooltipContent>The nearest earlier day it ran</TooltipContent>
        </Tooltip>
        <Tooltip>
          <TooltipTrigger asChild>
            <Button
              variant="ghost" size="icon-xs" className="size-5" disabled={later === null} aria-label="The day after that it ran"
              onClick={() => later !== null && onPick(later)} data-testid="lens-days-later"
            >
              <ChevronRight />
            </Button>
          </TooltipTrigger>
          <TooltipContent>The nearest later day it ran</TooltipContent>
        </Tooltip>
      </div>

      {ran.length === 0 ? (
        <p className="text-[12px] text-muted-foreground" data-testid="lens-days-none">
          It did not run in the last {counted(bars.length, "day")}.
        </p>
      ) : (
        <>
          <div>
            <LensBars
              bars={bars}
              day={day}
              width={CHART_WIDTH}
              height={CHART_HEIGHT}
              fluid
              describe={(index) => `${dayLabel(fromUtc, index)}: ${lineOf(index)}`}
              onPick={onPick}
            />
            <div aria-hidden className="relative mt-0.5 h-3">
              {dated.map((index) => (
                <span
                  key={index}
                  className="absolute top-0 text-[9px] leading-3 whitespace-nowrap text-muted-foreground"
                  // A date near the chart's end is written back from its day, so it is not cut by the column's edge.
                  style={index / bars.length > 0.88
                    ? { right: `${(1 - (index + 1) / bars.length) * 100}%` }
                    : { left: `${(index / bars.length) * 100}%` }}
                >
                  {dayLabel(fromUtc, index, false)}
                </span>
              ))}
            </div>
          </div>

          <ul className="flex flex-col" onMouseLeave={() => store.set(null)}>
            {listed.map((index) => {
              const line = lineOf(index);
              const failed = bars[index].outcome === "failed";
              return (
                <li key={index}>
                  <button
                    type="button"
                    onClick={() => onPick(index)}
                    onMouseEnter={() => store.set(index)}
                    aria-current={index === day ? "date" : undefined}
                    title={line}
                    data-testid="lens-days-row"
                    className={cn(
                      "flex w-full cursor-pointer items-baseline gap-2 rounded-sm border-l-2 border-transparent px-1.5 py-1 text-left text-[12px]",
                      "hover:bg-accent aria-[current]:border-primary aria-[current]:bg-primary/10",
                    )}
                  >
                    <span className={cn("w-[76px] shrink-0 whitespace-nowrap tabular-nums", index === day ? "font-medium" : "text-muted-foreground")}>
                      {dayLabel(fromUtc, index)}
                    </span>
                    <span className={cn("min-w-0 flex-1 truncate", failed && "text-destructive")}>{line}</span>
                  </button>
                </li>
              );
            })}
          </ul>
          {ran.length > DAYS_SHOWN && (
            <Button
              variant="ghost" size="xs" className="self-start px-1 text-muted-foreground" onClick={() => setAll(!all)}
              data-testid="lens-days-all"
            >
              {all ? "Show the latest" : `Show all ${counted(ran.length, "day")}`}
            </Button>
          )}
        </>
      )}
    </section>
  );
}
