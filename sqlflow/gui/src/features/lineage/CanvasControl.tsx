import { useState, type ReactNode } from "react";
import { Panel, useReactFlow, useStore } from "@xyflow/react";
import {
  ArrowDownFromLine, ArrowLeftFromLine, ArrowRightFromLine, ArrowUpFromLine, Infinity as AllHops, Maximize, Minus, Plus,
  UnfoldHorizontal, UnfoldVertical, type LucideIcon,
} from "lucide-react";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuLabel, DropdownMenuRadioGroup, DropdownMenuRadioItem, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { counted } from "./graphKinds";
import type { Orientation } from "./graphLayout";
import type { TraceDirection } from "./graphModel";

/** While a node is traced: which side of it is drawn and how far, and how each is changed. */
export interface CanvasTrace {
  direction: TraceDirection;
  /** How many hops from the node are drawn; null draws all of them. */
  hops: number | null;
  onDirection: (direction: TraceDirection) => void;
  onHops: (hops: number | null) => void;
}

/** A glyph of the control: what it does is said beside it on hover, so the canvas carries no words. */
const BUTTON = "inline-flex h-7 min-w-7 shrink-0 cursor-pointer items-center justify-center rounded-none p-0 text-muted-foreground shadow-none hover:bg-accent hover:text-foreground focus-visible:z-10 disabled:cursor-default disabled:opacity-40 disabled:hover:bg-transparent disabled:hover:text-muted-foreground [&_svg]:size-3.5";

/**
 * A chosen side is told by its fill and ink, as on the mapping overview's rail. Keyed on the ARIA state: each side is
 * also a tooltip's trigger, and the tooltip writes its own `data-state` over the toggle's.
 */
const CHOSEN = "aria-checked:bg-primary/15 aria-checked:text-primary aria-checked:hover:bg-primary/20 aria-checked:hover:text-primary";

/** A toggle group's first and last items round their own corners; inside the control the control's edge does that. */
const SQUARE = "data-[spacing=0]:first:rounded-none data-[spacing=0]:last:rounded-none";

const SIDES: readonly { value: TraceDirection; label: string; detail: string; testId: string }[] = [
  { value: "upstream", label: "Upstream", detail: "What it is made from.", testId: "graph-trace-upstream" },
  { value: "both", label: "Both sides", detail: "What it is made from, and what a change to it reaches.", testId: "graph-trace-both" },
  { value: "downstream", label: "Downstream", detail: "What a change to it reaches.", testId: "graph-trace-downstream" },
];

/**
 * Each side's glyph points the way that side lies in the drawing: above and below the node when data runs top to
 * bottom, left and right of it when it runs left to right. The glyph is read against the drawing, not learned.
 */
const SIDE_GLYPHS: Readonly<Record<Orientation, Readonly<Record<TraceDirection, LucideIcon>>>> = {
  down: { upstream: ArrowUpFromLine, both: UnfoldVertical, downstream: ArrowDownFromLine },
  right: { upstream: ArrowLeftFromLine, both: UnfoldHorizontal, downstream: ArrowRightFromLine },
};

const HOPS: readonly { value: string; label: string }[] = [
  { value: "1", label: "1 hop" },
  { value: "2", label: "2 hops" },
  { value: "3", label: "3 hops" },
  { value: "all", label: "All, to the ends" },
];

/** A tooltip's words: a name, and a sentence under it where a name alone cannot say what the control changes. */
function Tip({ label, detail }: { label: string; detail?: string }) {
  return detail === undefined
    ? <>{label}</>
    : (
      <>
        <span className="block font-medium">{label}</span>
        <span className="block text-muted-foreground">{detail}</span>
      </>
    );
}

/**
 * How far a trace reaches, as one glyph that opens its choices: the count it draws, or the infinity sign for all of
 * it, with a corner mark that says a menu is under it. Four numbers would stand on the canvas for a choice made once
 * per trace; the count alone says what is drawn.
 */
function HopsMenu({ hops, side, row, onHops }: {
  hops: number | null;
  side: "top" | "right";
  row: boolean;
  onHops: (hops: number | null) => void;
}) {
  const [open, setOpen] = useState(false);
  const [hinted, setHinted] = useState(false);
  const reach = hops === null ? "All hops" : counted(hops, "hop");
  return (
    <DropdownMenu open={open} onOpenChange={setOpen}>
      <Tooltip open={hinted && !open} onOpenChange={setHinted}>
        <TooltipTrigger asChild>
          <DropdownMenuTrigger asChild>
            <button
              type="button"
              aria-label={`How far the trace reaches: ${reach.toLowerCase()}`}
              data-testid="graph-trace-hops"
              data-hops={hops === null ? "all" : String(hops)}
              className={cn(
                BUTTON,
                "relative text-[12px] font-medium text-foreground tabular-nums aria-expanded:bg-accent",
                row && "flex-1",
              )}
            >
              {hops === null ? <AllHops aria-hidden /> : hops}
              <span
                aria-hidden
                className="absolute right-[3px] bottom-[3px] size-0 border-b-[4px] border-l-[4px] border-b-muted-foreground/70 border-l-transparent"
              />
            </button>
          </DropdownMenuTrigger>
        </TooltipTrigger>
        <TooltipContent side={side} variant="panel" className="pointer-events-none">
          <Tip label={reach} detail="How far from the node the trace draws. Click to change." />
        </TooltipContent>
      </Tooltip>
      <DropdownMenuContent side="right" align="end" sideOffset={6} className="w-44">
        <DropdownMenuLabel className="text-[11px] font-medium tracking-wider text-muted-foreground uppercase">Drawn around it</DropdownMenuLabel>
        <DropdownMenuRadioGroup
          // A reach the address asked for past three is drawn as asked and shown on the glyph; no choice is ticked for it.
          value={hops === null ? "all" : String(hops)}
          onValueChange={(next) => onHops(next === "all" ? null : Number(next))}
        >
          {HOPS.map((option) => (
            <DropdownMenuRadioItem key={option.value} value={option.value} data-testid={`graph-trace-hops-${option.value}`} className="tabular-nums">
              {option.label}
            </DropdownMenuRadioItem>
          ))}
        </DropdownMenuRadioGroup>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/**
 * The trace's tools, there only while a node is traced: which side of it is drawn, as three glyphs pointing the way
 * each side lies, and how far, as one glyph holding the choices. The node's name and the way out stay in the line
 * under the bar, which says what the canvas shows.
 */
function TraceTools({ trace, orientation, row, side }: {
  trace: CanvasTrace;
  orientation: Orientation;
  row: boolean;
  side: "top" | "right";
}) {
  const glyphs = SIDE_GLYPHS[orientation];
  return (
    <div
      role="group"
      aria-label="Trace"
      data-testid="graph-trace-tools"
      className={cn("flex animate-in fade-in-0 duration-150 motion-reduce:animate-none", !row && "flex-col")}
    >
      <ToggleGroup
        type="single"
        value={trace.direction}
        onValueChange={(next) => {
          if (next !== "") {
            trace.onDirection(next as TraceDirection);
          }
        }}
        orientation={row ? "horizontal" : "vertical"}
        aria-label="Side of the traced node that is drawn"
        data-testid="graph-trace-direction"
        className={cn("rounded-none", row ? "w-auto flex-[3]" : "flex-col")}
      >
        {SIDES.map((option) => {
          const Glyph = glyphs[option.value];
          return (
            <Tooltip key={option.value}>
              <TooltipTrigger asChild>
                <ToggleGroupItem
                  value={option.value}
                  aria-label={option.label}
                  data-testid={option.testId}
                  className={cn(BUTTON, CHOSEN, SQUARE, "px-0", row && "flex-1")}
                >
                  <Glyph aria-hidden />
                </ToggleGroupItem>
              </TooltipTrigger>
              <TooltipContent side={side} variant="panel" className="pointer-events-none">
                <Tip label={option.label} detail={option.detail} />
              </TooltipContent>
            </Tooltip>
          );
        })}
      </ToggleGroup>
      <HopsMenu hops={trace.hops} side={side} row={row} onHops={trace.onHops} />
    </div>
  );
}

/**
 * The one thing that stands on the canvas: its tools, as glyphs in one control in the lower left corner, with a map of
 * the whole drawing over them where the drawing is too large to see whole. The tools come in groups that are there
 * only while they act: a trace's side and reach while a node is traced, then the zoom. Over a map each group is a row
 * under it; without one they stack, so they take the least width beside the bands' names. What each glyph does is said
 * on hover, so the canvas carries no words; what the canvas shows is said in the line under the bar.
 */
export function CanvasControl({ orientation, trace, onFit, children }: {
  orientation: Orientation;
  /** The trace's side and reach, while a node is traced. */
  trace: CanvasTrace | null;
  onFit: () => void;
  /** The map of the whole drawing, where one is drawn. */
  children: ReactNode;
}) {
  const flow = useReactFlow();
  const atLeast = useStore((state) => state.transform[2] <= state.minZoom);
  const atMost = useStore((state) => state.transform[2] >= state.maxZoom);
  const mapped = children !== null && children !== undefined && children !== false;
  const side = mapped ? "top" as const : "right" as const;
  // Over a map every group is a row with a rule above it; in a stack a rule parts one group from the next.
  const group = (first: boolean) => cn("flex", mapped ? "border-t border-border" : cn("flex-col", !first && "border-t border-border"));
  return (
    <Panel position="bottom-left" className="!m-3">
      <div
        data-testid="graph-canvas-control"
        className="flex flex-col overflow-hidden rounded-md border border-border bg-card shadow-sm"
      >
        {children}
        {trace !== null && (
          <div className={group(true)}>
            <TraceTools trace={trace} orientation={orientation} row={mapped} side={side} />
          </div>
        )}
        <div className={group(trace === null)} role="group" aria-label="Zoom">
          <Tooltip>
            <TooltipTrigger asChild>
              <button
                type="button" className={cn(BUTTON, mapped && "flex-1")} disabled={atMost} aria-label="Zoom in"
                onClick={() => void flow.zoomIn({ duration: 160 })} data-testid="graph-zoom-in"
              >
                <Plus aria-hidden />
              </button>
            </TooltipTrigger>
            <TooltipContent side={side}>Zoom in</TooltipContent>
          </Tooltip>
          <Tooltip>
            <TooltipTrigger asChild>
              <button
                type="button" className={cn(BUTTON, mapped && "flex-1")} disabled={atLeast} aria-label="Zoom out"
                onClick={() => void flow.zoomOut({ duration: 160 })} data-testid="graph-zoom-out"
              >
                <Minus aria-hidden />
              </button>
            </TooltipTrigger>
            <TooltipContent side={side}>Zoom out</TooltipContent>
          </Tooltip>
          <Tooltip>
            <TooltipTrigger asChild>
              <button
                type="button" className={cn(BUTTON, mapped && "flex-1")} aria-label="Fit the whole drawing"
                onClick={onFit} data-testid="graph-zoom-fit"
              >
                <Maximize aria-hidden />
              </button>
            </TooltipTrigger>
            <TooltipContent side={side}>Fit the whole drawing in the canvas</TooltipContent>
          </Tooltip>
        </div>
      </div>
    </Panel>
  );
}
