import { useId, useRef, useState, type ReactNode } from "react";
import {
  Activity, ArrowDown, ArrowRight, ChevronDown, Crosshair, Download, Ellipsis, Eye, EyeOff, Rows3, SlidersHorizontal, X,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Slider } from "@/components/ui/slider";
import { Switch } from "@/components/ui/switch";
import { Toggle } from "@/components/ui/toggle";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { counted, kindLook } from "./graphKinds";
import type { Orientation } from "./graphLayout";
import type { GraphLevel, KindCount } from "./graphModel";

/**
 * A pressed choice is told by its edge and fill, not by a shade the eye has to compare. Keyed on the ARIA state, not
 * on `data-state`: each choice is also a tooltip's trigger, and the tooltip writes its own `data-state` over the toggle's.
 */
const PRESSED = "aria-checked:border-primary aria-checked:bg-primary/10 aria-checked:text-foreground aria-pressed:border-primary aria-pressed:bg-primary/10 aria-pressed:text-foreground";

/**
 * A set of choices drawn as ONE control: a single edge around them all, and only the chosen one carrying an edge of
 * its own. Four boxes side by side read as four controls; this reads as one question with four answers.
 */
const SEGMENTS = "rounded-md border border-border p-0.5";
const SEGMENT = "h-[26px] min-w-0 rounded-[4px] border border-transparent px-2.5 text-xs font-medium text-muted-foreground shadow-none hover:bg-accent hover:text-foreground";

const LABEL = "text-[10px] leading-none font-medium tracking-wider text-muted-foreground uppercase";

const LEVELS: readonly { value: GraphLevel; label: string; effect: string }[] = [
  { value: "flows", label: "Flows", effect: "Flows alone. An arrow says one flow reads what the other writes." },
  { value: "handovers", label: "Handovers", effect: "Flows, and the objects one flow writes and another reads." },
  { value: "all", label: "Everything", effect: "Flows with every object each one reads and writes." },
  { value: "objects", label: "Data only", effect: "Tables, files and datasets, joined by the flows that move data between them." },
];

/**
 * How much is drawn: the one choice the bar leads with, since it decides what kind of drawing the reader gets. Each
 * choice says on hover what it draws and how many nodes that is for the whole scope, so its cost is known before it
 * is picked without four numbers standing in the bar.
 */
export function LevelControl({ value, sizes, onChange }: {
  value: GraphLevel;
  sizes: Record<GraphLevel, number>;
  onChange: (level: GraphLevel) => void;
}) {
  return (
    <ToggleGroup
      type="single"
      spacing={0.5}
      value={value}
      onValueChange={(next) => {
        if (next !== "") {
          onChange(next as GraphLevel);
        }
      }}
      aria-label="How much is drawn"
      data-testid="graph-level"
      className={SEGMENTS}
    >
      {LEVELS.map((level) => (
        <Tooltip key={level.value}>
          <TooltipTrigger asChild>
            <ToggleGroupItem value={level.value} data-testid={`graph-level-${level.value}`} className={cn(SEGMENT, PRESSED)}>
              {level.label}
            </ToggleGroupItem>
          </TooltipTrigger>
          <TooltipContent>{level.effect} {counted(sizes[level.value], "node")} in this scope.</TooltipContent>
        </Tooltip>
      ))}
    </ToggleGroup>
  );
}

/**
 * Whether the drawing shows what moved through it. Off, the graph says how nodes connect: what a flow should read
 * and write. On, it says what happened: every card leads with what its node did in the day looked at and charts it
 * over the window, and every line is inked and weighed by what it carried, so a delivery is followed from the
 * files that arrived to where it stopped.
 */
export function DataControl({ value, onChange }: { value: boolean; onChange: (value: boolean) => void }) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Toggle
          pressed={value}
          onPressedChange={onChange}
          data-testid="graph-data-toggle"
          className={cn("h-8 gap-1.5 border border-transparent px-2.5 text-xs", PRESSED)}
        >
          <Activity aria-hidden />
          What moved
        </Toggle>
      </TooltipTrigger>
      <TooltipContent className="max-w-xs">
        {value
          ? "Every card says what its node did in the day looked at, and charts it over the window; every line is inked and weighed by what it carried. Turn off to draw only how the nodes connect."
          : "Show what moved through this drawing on a day: the files that arrived, the rows each flow read and changed, what landed in each table, and where the data stopped. A node's details then hold its own days."}
      </TooltipContent>
    </Tooltip>
  );
}

/**
 * Everything about HOW the drawing is made, behind one word of the bar: the wave range, the kinds, what draws as one
 * node, the steps other systems run, and which way data runs. These are set once in a while and then left alone, so
 * they do not stand in the bar beside the choices a reader makes every time. The menu stays open while its choices
 * are made, the canvas redrawing behind it, and the button wears a mark while any of them differs from how the scope
 * opens, so a narrowed drawing is never mistaken for the whole of it.
 */
export function ViewMenu({ changes, onClose, children }: {
  changes: number;
  /** The menu closed, with whatever was under way in it (a wave range half dragged) left as it stood. */
  onClose: () => void;
  children: ReactNode;
}) {
  return (
    <Popover
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
    >
      <Tooltip>
        <TooltipTrigger asChild>
          <PopoverTrigger asChild>
            <Button
              variant="ghost"
              size="sm"
              className="h-8 gap-1.5 px-2.5 text-xs font-medium aria-expanded:bg-accent"
              data-testid="graph-view-menu"
            >
              <SlidersHorizontal aria-hidden />
              View
              {changes > 0 && (
                <>
                  <span aria-hidden className="size-1.5 rounded-full bg-primary" data-testid="graph-view-changed" />
                  <span className="sr-only">, {counted(changes, "choice")} changed</span>
                </>
              )}
            </Button>
          </PopoverTrigger>
        </TooltipTrigger>
        <TooltipContent className="max-w-xs">
          How the drawing is made: waves, kinds, folders and stacks, layout.
          {changes > 0 && ` ${counted(changes, "choice")} here ${changes === 1 ? "differs" : "differ"} from how the scope opens.`}
        </TooltipContent>
      </Tooltip>
      <PopoverContent
        align="end"
        className="flex max-h-(--radix-popover-content-available-height) w-80 flex-col divide-y divide-border overflow-y-auto p-0"
        data-testid="graph-view-panel"
      >
        {children}
      </PopoverContent>
    </Popover>
  );
}

/**
 * What is done with a drawing once in a while, under one mark at the bar's end: saving it as a picture, and leaving
 * for the object explorer. The mark says what it holds on hover, and stops saying it while the menu itself is open.
 */
export function MoreMenu({ onDownload, onOpenExplorer }: { onDownload: () => void; onOpenExplorer: () => void }) {
  const [open, setOpen] = useState(false);
  const [hinted, setHinted] = useState(false);
  return (
    <DropdownMenu open={open} onOpenChange={setOpen}>
      <Tooltip open={hinted && !open} onOpenChange={setHinted}>
        <TooltipTrigger asChild>
          <DropdownMenuTrigger asChild>
            <Button
              variant="ghost"
              size="icon-sm"
              aria-label="More: download the drawing, open the object explorer"
              className="text-muted-foreground hover:text-foreground"
              data-testid="graph-more"
            >
              <Ellipsis />
            </Button>
          </DropdownMenuTrigger>
        </TooltipTrigger>
        <TooltipContent>Download this drawing, or open the object explorer</TooltipContent>
      </Tooltip>
      <DropdownMenuContent align="end" className="w-56">
        <DropdownMenuItem onSelect={onDownload} data-testid="download-lineage">
          <Download aria-hidden />
          Download as SVG
        </DropdownMenuItem>
        <DropdownMenuItem onSelect={onOpenExplorer} data-testid="open-lineage-objects">
          <Rows3 aria-hidden />
          Open the object explorer
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/** One part of the View menu, under the word that names what it changes, with what it is set to beside the word. */
function ViewSection({ label, summary, children, testId }: {
  label: string;
  summary?: ReactNode;
  children: ReactNode;
  testId?: string;
}) {
  return (
    <section className="flex flex-col gap-2.5 px-3.5 py-3" data-testid={testId}>
      <div className="flex items-baseline justify-between gap-3">
        <h4 className={LABEL}>{label}</h4>
        {summary !== undefined && <span className="text-[12px] leading-none tabular-nums">{summary}</span>}
      </div>
      {children}
    </section>
  );
}

/** A choice that is on or off, with what it does said under its name, where a tooltip would have hidden it. */
function SwitchRow({ label, description, checked, onChange, testId }: {
  label: string;
  description: string;
  checked: boolean;
  onChange: (value: boolean) => void;
  testId: string;
}) {
  const id = useId();
  return (
    <label htmlFor={id} className="flex cursor-pointer items-start gap-3">
      <span className="min-w-0 flex-1">
        <span id={`${id}-name`} className="block text-[13px] leading-5">{label}</span>
        <span id={`${id}-what`} className="block text-[11px] leading-4 text-muted-foreground">{description}</span>
      </span>
      <Switch
        id={id}
        checked={checked}
        onCheckedChange={onChange}
        aria-labelledby={`${id}-name`}
        aria-describedby={`${id}-what`}
        data-testid={testId}
        className="mt-0.5"
      />
    </label>
  );
}

/** The two ways several nodes draw as one: a folder's flows, and look-alike nodes. */
export function CompactControl({ grouped, groupable, stacked, onGrouped, onStacked }: {
  grouped: boolean;
  groupable: boolean;
  stacked: boolean;
  onGrouped: (value: boolean) => void;
  onStacked: (value: boolean) => void;
}) {
  return (
    <ViewSection label="Draw as one">
      {groupable && (
        <SwitchRow
          label="Folders"
          description="Each folder's flows draw as one node. Double-click a folder to open it."
          checked={grouped}
          onChange={onGrouped}
          testId="graph-group-toggle"
        />
      )}
      <SwitchRow
        label="Stacks"
        description="Nodes of one kind with the same producers and consumers draw as one. Double-click a stack to open it."
        checked={stacked}
        onChange={onStacked}
        testId="graph-stack-toggle"
      />
    </ViewSection>
  );
}

function waveName(wave: number): string {
  return wave < 0 ? "Unwaved" : `Wave ${wave}`;
}

/**
 * The wave range. Each stop is a wave, with a bar as tall as its share of the flows, so the reader sees where the
 * bulk sits before dragging; the two thumbs keep every wave between them, so a chain running through those waves
 * stays whole. While a thumb moves, `onPreview` tells the canvas which waves would stay (it fades the rest), and
 * the range is applied when the thumb is released.
 */
export function WaveRange({ stops, range, onPreview, onCommit }: {
  stops: readonly { wave: number; count: number }[];
  /** The first and last drawn stop, as indexes into `stops`. */
  range: readonly [number, number];
  onPreview: (range: readonly [number, number] | null) => void;
  onCommit: (range: readonly [number, number]) => void;
}) {
  const [draft, setDraft] = useState<readonly [number, number] | null>(null);
  // A range is previewed only while a thumb is dragged. By the keyboard each step is applied at once, and there the
  // slider reports the step as applied before it reports the change, so a draft kept from the change would outlive it.
  const dragging = useRef(false);
  const endDrag = () => {
    dragging.current = false;
    setDraft(null);
    onPreview(null);
  };
  const shown = draft ?? range;
  const most = Math.max(...stops.map((stop) => stop.count), 1);
  const flows = stops.slice(shown[0], shown[1] + 1).reduce((sum, stop) => sum + stop.count, 0);
  const whole = shown[0] === 0 && shown[1] === stops.length - 1;
  const first = stops[shown[0]];
  const last = stops[shown[1]];
  const text = whole
    ? `All ${stops.length}`
    : shown[0] === shown[1] ? waveName(first.wave) : `${waveName(first.wave)} to ${last.wave < 0 ? "unwaved" : last.wave}`;

  return (
    <ViewSection
      label="Waves"
      testId="graph-wave-range"
      summary={<span data-testid="graph-wave-range-text">{text} · {counted(flows, "flow")}</span>}
    >
      <div className="flex flex-col gap-1">
        <div className="flex h-7 items-end gap-0.5 px-[7px]" aria-hidden>
          {stops.map((stop, index) => (
            <div
              key={stop.wave}
              title={`${waveName(stop.wave)}: ${counted(stop.count, "flow")}`}
              className={cn("flex-1 rounded-t-[2px]", index >= shown[0] && index <= shown[1] ? "bg-primary/60" : "bg-muted-foreground/25")}
              style={{ height: `${Math.max(15, Math.round((stop.count / most) * 100))}%` }}
            />
          ))}
        </div>
        <Slider
          min={0}
          max={stops.length - 1}
          step={1}
          minStepsBetweenThumbs={0}
          value={[shown[0], shown[1]]}
          thumbLabels={["First wave drawn", "Last wave drawn"]}
          onPointerDown={() => {
            dragging.current = true;
          }}
          onPointerUp={endDrag}
          onPointerCancel={endDrag}
          onValueChange={(next) => {
            if (dragging.current) {
              const value: [number, number] = [Math.min(next[0], next[1]), Math.max(next[0], next[1])];
              setDraft(value);
              onPreview(value);
            }
          }}
          onValueCommit={(next) => onCommit([Math.min(next[0], next[1]), Math.max(next[0], next[1])])}
        />
      </div>
      <p className="text-[11px] leading-4 text-muted-foreground">
        A flow runs in the wave after everything it reads from. Drag the ends to draw only those waves; a taller bar
        is a wave with more flows.
      </p>
    </ViewSection>
  );
}

/**
 * The legend, which is also the filter: every kind of node the drawing holds, with its glyph and count, and an eye
 * to hide it. Hiding a kind takes its nodes out and joins what they joined, so a chain through them stays connected.
 * It is one line of the menu until it is asked for, since a drawing of everything holds a dozen kinds or more; it
 * opens by itself while a kind is hidden, which is then what the reader came to see.
 */
export function KindFilter({ kinds, hidden, onToggle, onShowAll }: {
  kinds: readonly KindCount[];
  hidden: ReadonlySet<string>;
  onToggle: (key: string) => void;
  onShowAll: () => void;
}) {
  const hiddenHere = kinds.filter((kind) => hidden.has(kind.key)).length;
  const [open, setOpen] = useState(hiddenHere > 0);
  const listId = useId();
  return (
    <section className="flex flex-col gap-2 px-3.5 py-3" data-testid="graph-kinds">
      <button
        type="button"
        aria-expanded={open}
        aria-controls={listId}
        onClick={() => setOpen(!open)}
        data-testid="graph-kind-filter"
        className="-mx-1.5 -my-1 flex cursor-pointer items-center justify-between gap-3 rounded-sm px-1.5 py-1.5 text-left hover:bg-accent"
      >
        <span className={LABEL}>Kinds</span>
        <span className="flex items-center gap-1 text-[12px] leading-none tabular-nums">
          {hiddenHere === 0 ? `All ${kinds.length} shown` : `${hiddenHere} of ${kinds.length} hidden`}
          <ChevronDown aria-hidden className={cn("size-3.5 text-muted-foreground transition-transform", open && "rotate-180")} />
        </span>
      </button>
      {open && (
        <div id={listId} className="flex flex-col gap-1.5">
          <ul className="-mx-1.5 max-h-64 overflow-y-auto">
            {kinds.map((kind) => {
              const look = kindLook(kind.type, kind.kind);
              const isHidden = hidden.has(kind.key);
              return (
                <li key={kind.key}>
                  <button
                    type="button"
                    role="switch"
                    aria-checked={!isHidden}
                    data-testid="graph-kind-option"
                    data-kind={kind.key}
                    onClick={() => onToggle(kind.key)}
                    className={cn(
                      "flex w-full cursor-pointer items-center gap-2 rounded-sm px-1.5 py-1 text-left text-[13px] hover:bg-accent",
                      isHidden && "text-muted-foreground",
                    )}
                  >
                    <look.icon aria-hidden className={cn("size-4 shrink-0", isHidden ? "text-muted-foreground/60" : look.iconClass)} />
                    <span className={cn("min-w-0 flex-1 truncate", isHidden && "line-through")}>
                      {look.label}
                      <span className="ml-1.5 text-xs text-muted-foreground">{kind.type === "flow" ? "flow" : "data"}</span>
                    </span>
                    <span className="text-xs text-muted-foreground tabular-nums">{kind.count}</span>
                    {isHidden ? <EyeOff aria-hidden className="size-3.5 shrink-0" /> : <Eye aria-hidden className="size-3.5 shrink-0 text-muted-foreground" />}
                  </button>
                </li>
              );
            })}
          </ul>
          <p className="text-[11px] leading-4 text-muted-foreground">
            A hidden kind is bridged: what it joined stays joined, so no chain breaks.
            {hiddenHere > 0 && (
              <>
                {" "}
                <button
                  type="button"
                  onClick={onShowAll}
                  data-testid="graph-kind-show-all"
                  className="cursor-pointer font-medium text-foreground underline-offset-2 hover:underline"
                >
                  Show all
                </button>
              </>
            )}
          </p>
        </div>
      )}
    </section>
  );
}

/**
 * Whether the drawing also holds the described lineage: the steps other systems run, as lineage descriptions state
 * them. Off is the lineage the estate computed from the flows it runs; the control is only offered where turning it
 * on would add something.
 */
export function DescribedControl({ value, onChange }: { value: boolean; onChange: (value: boolean) => void }) {
  return (
    <ViewSection label="Other systems" testId="graph-described-control">
      <SwitchRow
        label="Described steps"
        description="Also draws the steps other systems run, dotted: where a landed file comes from, and what reads a table after this estate. Stated in lineage descriptions, never run or observed here."
        checked={value}
        onChange={onChange}
        testId="graph-described-toggle"
      />
    </ViewSection>
  );
}

const WINDOWS = [30, 60, 90] as const;

/**
 * How many days the data view reads: how far back every card's chart and a node's own days go. Set once in a while,
 * so it is here and not beside the day; offered only while the graph shows what moved, where it changes something.
 */
export function WindowControl({ value, onChange }: { value: number; onChange: (days: number) => void }) {
  return (
    <ViewSection label="Days charted" testId="graph-window-control">
      <ToggleGroup
        type="single"
        spacing={0.5}
        value={String(value)}
        onValueChange={(next) => {
          if (next !== "") {
            onChange(Number(next));
          }
        }}
        aria-label="How many days are charted"
        data-testid="graph-window"
        className={cn(SEGMENTS, "w-full")}
      >
        {WINDOWS.map((days) => (
          <ToggleGroupItem key={days} value={String(days)} data-testid={`graph-window-${days}`} className={cn(SEGMENT, PRESSED, "flex-1 tabular-nums")}>
            {days} days
          </ToggleGroupItem>
        ))}
      </ToggleGroup>
      <p className="text-[11px] leading-4 text-muted-foreground">
        How far back each card's chart and a node's own days go while What moved is on.
      </p>
    </ViewSection>
  );
}

/** Which way data runs on the canvas. Top to bottom is how the page opens; left to right suits a long, narrow chain. */
export function OrientationControl({ value, onChange }: { value: Orientation; onChange: (value: Orientation) => void }) {
  return (
    <ViewSection label="Layout">
      <ToggleGroup
        type="single"
        spacing={0.5}
        value={value}
        onValueChange={(next) => {
          if (next !== "") {
            onChange(next as Orientation);
          }
        }}
        aria-label="Which way data runs"
        data-testid="graph-orientation"
        className={cn(SEGMENTS, "w-full")}
      >
        <Tooltip>
          <TooltipTrigger asChild>
            <ToggleGroupItem value="down" data-testid="graph-orientation-down" className={cn(SEGMENT, PRESSED, "flex-1 gap-1.5")}>
              <ArrowDown aria-hidden />
              Top to bottom
            </ToggleGroupItem>
          </TooltipTrigger>
          <TooltipContent>What a node is made from is above it.</TooltipContent>
        </Tooltip>
        <Tooltip>
          <TooltipTrigger asChild>
            <ToggleGroupItem value="right" data-testid="graph-orientation-right" className={cn(SEGMENT, PRESSED, "flex-1 gap-1.5")}>
              <ArrowRight aria-hidden />
              Left to right
            </ToggleGroupItem>
          </TooltipTrigger>
          <TooltipContent>What a node is made from is on its left.</TooltipContent>
        </Tooltip>
      </ToggleGroup>
    </ViewSection>
  );
}

/**
 * What a trace draws, in the line under the bar and there only while a node is traced: the node, and the way out.
 * The line says what the canvas shows, so the node is named here; which side of it is drawn and how far are glyphs on
 * the canvas's own control, beside the zoom, where they act.
 */
export function TraceControl({ name, onClear }: { name: string; onClear: () => void }) {
  return (
    <span
      role="group"
      aria-label={`Trace of ${name}`}
      data-testid="graph-trace-control"
      className="inline-flex min-w-0 items-center gap-1.5"
    >
      <Crosshair aria-hidden className="size-3.5 shrink-0 text-primary" />
      <span className="text-muted-foreground">Tracing</span>
      <span className="max-w-[280px] truncate font-mono font-medium" title={name} data-testid="graph-trace-name">{name}</span>
      <Tooltip>
        <TooltipTrigger asChild>
          <button
            type="button"
            onClick={onClear}
            aria-label="Stop tracing"
            data-testid="graph-trace-clear"
            className="inline-flex size-5 shrink-0 cursor-pointer items-center justify-center rounded-sm text-muted-foreground hover:bg-accent hover:text-foreground"
          >
            <X aria-hidden className="size-3.5" />
          </button>
        </TooltipTrigger>
        <TooltipContent>Stop tracing and draw the whole scope again.</TooltipContent>
      </Tooltip>
    </span>
  );
}
