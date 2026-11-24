import { useEffect, useRef, type KeyboardEvent } from "react";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import type { StreamPathFire, StreamPathNode } from "../../api/types";
import { kindContribution } from "../../modules/registry";
import { fireTallyText, fireTime, runFigures, runOutcome, runsByFlow, type RunOutcome } from "./streamPathModel";

const ROW = 14;

/** One flow's run in one fire, as a dot in the strip: the same marks the canvas cards wear. */
function Dot({ outcome }: { outcome: RunOutcome }) {
  return (
    <span className="flex items-center justify-center" style={{ height: ROW }}>
      <span
        aria-hidden
        className={cn(
          "block rounded-full",
          outcome === "absent" ? "h-px w-2 bg-border" : "size-2",
          outcome === "moved" && "bg-chart-1",
          outcome === "empty" && "ring-1 ring-muted-foreground/70 ring-inset",
          outcome === "failed" && "bg-destructive",
          outcome === "running" && "animate-pulse bg-chart-1",
          outcome === "queued" && "ring-1 ring-chart-1 ring-inset",
          outcome === "cancelled" && "bg-muted-foreground/40",
        )}
      />
    </span>
  );
}

interface StreamFireStripProps {
  /** Oldest first, the way the strip reads left to right. */
  fires: StreamPathFire[];
  /** The path's flows in the order the canvas runs them, top to bottom: one row of dots each. */
  flows: StreamPathNode[];
  selectedKey: string | null;
  onSelect: (key: string) => void;
}

/**
 * The fires through a path, oldest on the left: one column per fire, one row per flow in the canvas's own order, a dot
 * where the flow ran. Filled is data moved, a ring is a run that moved none, red is a failure, a dash is no run. Read
 * across, a row is one stage over time, so a stage that stopped while the one above it kept delivering is the row that
 * goes hollow. Picking a column draws that fire on the canvas; the arrow keys step through them.
 */
export function StreamFireStrip({ fires, flows, selectedKey, onSelect }: StreamFireStripProps) {
  const scroller = useRef<HTMLDivElement>(null);
  const index = fires.findIndex((fire) => fire.key === selectedKey);

  useEffect(() => {
    if (selectedKey === null) {
      return;
    }
    scroller.current
      ?.querySelector(`[data-fire-key="${CSS.escape(selectedKey)}"]`)
      ?.scrollIntoView({ block: "nearest", inline: "nearest" });
  }, [selectedKey, fires.length]);

  const step = (event: KeyboardEvent<HTMLDivElement>) => {
    const next = event.key === "ArrowLeft" ? index - 1 : event.key === "ArrowRight" ? index + 1 : null;
    if (next === null || next < 0 || next >= fires.length) {
      return;
    }
    event.preventDefault();
    onSelect(fires[next].key);
    scroller.current?.querySelector<HTMLButtonElement>(`[data-fire-key="${CSS.escape(fires[next].key)}"]`)?.focus();
  };

  return (
    <div className="flex min-w-0 items-start gap-2" data-testid="stream-fire-strip">
      <div className="flex shrink-0 flex-col pt-1 text-right" aria-hidden>
        {flows.map((flow) => (
          <span
            key={flow.id}
            className={cn(
              "max-w-[200px] truncate text-left font-mono text-[11px] text-muted-foreground [direction:rtl]",
              flow.role === "selected" && "font-semibold text-foreground",
            )}
            style={{ height: ROW, lineHeight: `${ROW}px` }}
            title={flow.name}
          >
            <bdi>{flow.name}</bdi>
          </span>
        ))}
      </div>
      <div
        ref={scroller}
        role="listbox"
        aria-label="Fires, oldest first"
        className="flex min-w-0 flex-1 items-start gap-0.5 overflow-x-auto pb-1"
        onKeyDown={step}
      >
        {fires.map((fire) => {
          const runs = runsByFlow(fire);
          const picked = fire.key === selectedKey;
          return (
            <Tooltip key={fire.key}>
              <TooltipTrigger asChild>
                <button
                  type="button"
                  role="option"
                  aria-selected={picked}
                  tabIndex={picked || (selectedKey === null && fire === fires[fires.length - 1]) ? 0 : -1}
                  data-fire-key={fire.key}
                  data-testid="stream-fire"
                  onClick={() => onSelect(fire.key)}
                  className={cn(
                    "flex w-4 shrink-0 cursor-pointer flex-col rounded-sm py-1 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                    picked ? "bg-accent ring-1 ring-primary" : "hover:bg-accent/60",
                  )}
                >
                  {flows.map((flow) => (
                    <Dot key={flow.id} outcome={runOutcome(flow.pipelineId === null ? undefined : runs.get(flow.pipelineId)?.run)} />
                  ))}
                </button>
              </TooltipTrigger>
              <TooltipContent className="max-w-[360px]">
                <div className="font-medium">{fireTime(fire.startedUtc)}</div>
                <div className="opacity-80">{fireTallyText(fire)}</div>
                <ul className="mt-1 flex flex-col gap-0.5">
                  {flows.map((flow) => {
                    const entry = flow.pipelineId === null ? undefined : runs.get(flow.pipelineId);
                    const outcome = runOutcome(entry?.run);
                    return (
                      <li key={flow.id} className="flex gap-2 font-mono text-[11px]">
                        <span className="min-w-0 flex-1 truncate">{flow.name}</span>
                        <span className={cn("shrink-0", outcome === "failed" && "text-destructive")}>
                          {entry === undefined
                            ? "no run"
                            : outcome === "failed" || outcome === "running" || outcome === "queued" || outcome === "cancelled"
                              ? outcome
                              : kindContribution(entry.run.flowKind)?.runCountColumns !== undefined
                                ? outcome === "moved" ? "succeeded" : "succeeded, nothing to do"
                                : runFigures(entry.run)}
                        </span>
                      </li>
                    );
                  })}
                </ul>
              </TooltipContent>
            </Tooltip>
          );
        })}
      </div>
    </div>
  );
}
