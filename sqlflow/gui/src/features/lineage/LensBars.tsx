import { memo, useRef, useState, type MouseEvent } from "react";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import type { LensBar } from "./dataLens";
import { useLensHover, useLensHoverDay } from "./lensHover";

/** The ink of a day's mark, by how the day went. A day's amount is the bar; a day that ran and moved nothing, or
 * failed, has no amount to draw and is a stub at the baseline instead, so it is still a day something happened on. */
const INK: Record<LensBar["outcome"], string> = {
  moved: "var(--chart-1)",
  empty: "var(--muted-foreground)",
  failed: "var(--destructive)",
  running: "var(--primary)",
  absent: "transparent",
};

/**
 * A node's chart over the window, one mark per day, oldest on the left: how much moved on each day it ran. The day
 * looked at is marked in every chart on the page, and so is the day under the pointer, wherever the pointer is;
 * clicking a day looks at that day. Heights grow with the square root of the amount, so one heavy day does not
 * flatten the ordinary ones into the baseline. Nothing rests on colour alone: the tooltip names the day and says in
 * words what happened on it.
 */
function LensBarsInner({ bars, day, width, height, describe, onPick }: {
  bars: readonly LensBar[];
  /** The day looked at. */
  day: number;
  width: number;
  height: number;
  /** A day in words, for the tooltip: its date and what the node did on it. */
  describe: (day: number) => string;
  onPick: (day: number) => void;
}) {
  const store = useLensHover();
  const hovered = useLensHoverDay();
  const [here, setHere] = useState(false);
  const svg = useRef<SVGSVGElement>(null);
  const count = bars.length;
  if (count === 0) {
    return null;
  }

  const slot = width / count;
  const gap = slot >= 4 ? 1 : slot >= 2.5 ? 0.6 : 0;
  const bar = Math.max(1, slot - gap);
  const largest = Math.max(1, ...bars.map((entry) => entry.value));
  const floor = 2;
  const usable = height - floor - 1;
  const dayAt = (event: MouseEvent) => {
    const box = svg.current?.getBoundingClientRect();
    if (box === undefined || box.width === 0) {
      return null;
    }

    return Math.min(count - 1, Math.max(0, Math.floor(((event.clientX - box.left) / box.width) * count)));
  };
  const lit = hovered !== null && hovered !== day && hovered < count ? hovered : null;

  return (
    <Tooltip open={here && hovered !== null}>
      <TooltipTrigger asChild>
        <svg
          ref={svg}
          width={width}
          height={height}
          viewBox={`0 0 ${width} ${height}`}
          role="img"
          aria-label={describe(day)}
          data-testid="lens-bars"
          // The chart takes its own clicks: a click on a day looks at that day, and must neither select the card's
          // node nor start dragging it.
          className="nodrag nopan block cursor-pointer overflow-visible"
          onMouseEnter={() => setHere(true)}
          onMouseMove={(event) => store.set(dayAt(event))}
          onMouseLeave={() => {
            setHere(false);
            store.set(null);
          }}
          onClick={(event) => {
            event.stopPropagation();
            const picked = dayAt(event);
            if (picked !== null) {
              onPick(picked);
            }
          }}
        >
          <rect x={day * slot} y={0} width={slot} height={height} rx={1} fill="var(--foreground)" opacity={0.12} />
          {lit !== null && (
            <rect x={lit * slot} y={0} width={slot} height={height} rx={1} fill="var(--foreground)" opacity={0.06} />
          )}
          <line x1={0} x2={width} y1={height - 0.5} y2={height - 0.5} stroke="var(--border)" strokeWidth={1} />
          {bars.map((entry, index) => {
            if (entry.outcome === "absent") {
              return null;
            }

            const tall = entry.outcome === "moved" ? floor + Math.sqrt(entry.value / largest) * usable : floor + 1;
            const focus = index === day || index === lit;
            return (
              <g key={index} opacity={focus ? 1 : entry.outcome === "empty" ? 0.45 : 0.7}>
                <rect
                  x={index * slot + gap / 2}
                  y={height - 1 - tall}
                  width={bar}
                  height={tall}
                  rx={bar >= 3 ? 1 : 0}
                  fill={INK[entry.outcome]}
                />
                {entry.someFailed && (
                  <rect x={index * slot + gap / 2} y={height - 3} width={bar} height={2} fill="var(--destructive)" />
                )}
              </g>
            );
          })}
        </svg>
      </TooltipTrigger>
      <TooltipContent side="top" className="max-w-xs text-[11px]">
        {describe(hovered ?? day)}
      </TooltipContent>
    </Tooltip>
  );
}

export const LensBars = memo(LensBarsInner);
