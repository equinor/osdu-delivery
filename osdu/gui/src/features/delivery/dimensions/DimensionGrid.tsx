import { useRef, type ReactNode, type UIEvent } from "react";
import { Loader2, SlidersHorizontal } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Label } from "@/components/ui/label";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Switch } from "@/components/ui/switch";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { useWindowFit } from "../useWindowFit";
import type { GridColumnChoice } from "./dimensionGridState";

/** The least height a grid keeps, so a short window still shows a few rows. */
const MIN_GRID_HEIGHT = 240;

/** What stays under a grid's rows: its footer line, the page's padding and the workbench's status bar. */
const BELOW_GRID = 84;

/** How near its end a grid is scrolled, in pixels, before the next page is asked for. */
const NEAR_END = 320;

/** The table's own scrolling container, which the grid sizes to the window. */
const tableContainer = (element: HTMLElement) => element.querySelector<HTMLElement>('[data-slot="table-container"]');

/**
 * A table of a dimension as a grid that stays on the screen: its rows scroll inside it, under column headers that stay in
 * place, in rows a little denser than a list's, and it is as tall as the window leaves below where it starts, so the page
 * itself does not scroll for the rows. Scrolling near the end asks for the next page (`onNearEnd`).
 */
export function DimensionGrid({ children, onNearEnd, className, testId = "dimension-grid" }: {
  children: ReactNode;
  /** Called when the rows are scrolled near their end; the caller reads the next page when there is one. */
  onNearEnd?: () => void;
  className?: string;
  /** The grid's test id, for a page that is not a dimension's. */
  testId?: string;
}) {
  const ref = useRef<HTMLDivElement>(null);

  useWindowFit(ref, BELOW_GRID, MIN_GRID_HEIGHT, tableContainer);

  const scrolled = (event: UIEvent<HTMLDivElement>) => {
    const target = event.target as HTMLElement;
    if (onNearEnd !== undefined && target.dataset.slot === "table-container" && target.scrollHeight - target.scrollTop - target.clientHeight < NEAR_END) {
      onNearEnd();
    }
  };

  return (
    <div
      ref={ref}
      onScrollCapture={scrolled}
      className={cn(
        "min-w-0 [&_[data-slot=table-container]]:overflow-y-auto",
        "[&_th]:sticky [&_th]:top-0 [&_th]:z-10 [&_th]:bg-card [&_th]:shadow-[inset_0_-1px_0_var(--border)]",
        // Rows a little denser than a list's, and columns a little closer, with the card's own margin kept at both edges.
        "[&_td]:px-2 [&_td]:py-1 [&_th]:px-2",
        "[&_td:first-child]:pl-3 [&_th:first-child]:pl-3 [&_td:last-child]:pr-3 [&_th:last-child]:pr-3",
        className,
      )}
      data-testid={testId}
    >
      {children}
    </div>
  );
}

/**
 * What a grid shows, behind one button: the columns a reader can leave out, and, for a grid that can list them, whether
 * the rows no build finds any more are listed too. A grid whose toolbar is one line of its own (`compact`) shows the button
 * as its glyph alone, named on hover.
 */
export function GridViewMenu({ columns, hidden, atFirst = [], onToggle, removed = false, onRemoved, compact = false, testId }: {
  columns: GridColumnChoice[];
  hidden: ReadonlySet<string>;
  /** The columns the grid starts without, so the button marks only what a reader changed. */
  atFirst?: readonly string[];
  onToggle: (id: string) => void;
  removed?: boolean;
  /** Given by a grid that can list what builds no longer find; a grid without such rows leaves it out. */
  onRemoved?: (shown: boolean) => void;
  compact?: boolean;
  testId: string;
}) {
  const changed = removed || hidden.size !== atFirst.length || atFirst.some((id) => !hidden.has(id));
  const mark = <span className={cn("size-1.5 rounded-full bg-primary", compact && "absolute right-1 top-1")} aria-label="Changed from the default" />;
  return (
    <Popover>
      {compact
        ? (
          <Tooltip>
            <TooltipTrigger asChild>
              <PopoverTrigger asChild>
                <Button variant="ghost" size="icon-sm" className="relative size-7 text-muted-foreground" aria-label="View: the columns shown" data-testid={testId}>
                  <SlidersHorizontal />
                  {changed && mark}
                </Button>
              </PopoverTrigger>
            </TooltipTrigger>
            <TooltipContent>View: the columns shown</TooltipContent>
          </Tooltip>
        )
        : (
          <PopoverTrigger asChild>
            <Button variant="outline" size="sm" className="h-8 gap-1.5 px-2.5 text-[13px]" data-testid={testId}>
              <SlidersHorizontal />
              View
              {changed && mark}
            </Button>
          </PopoverTrigger>
        )}
      <PopoverContent align="end" className="w-64 p-0" data-testid={`${testId}-menu`}>
        {columns.length > 0 && (
          <div className={cn("flex max-h-72 flex-col gap-0.5 overflow-y-auto p-2", onRemoved !== undefined && "border-b border-border")}>
            <span className="px-1 pb-1 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Columns</span>
            {columns.map((column) => (
              <label key={column.id} className="flex cursor-pointer items-center gap-2 rounded-sm px-1 py-1 text-[13px] hover:bg-accent/60">
                <Checkbox checked={!hidden.has(column.id)} onCheckedChange={() => onToggle(column.id)} data-testid={`${testId}-column-${column.id}`} />
                <span className="min-w-0 truncate">{column.label}</span>
              </label>
            ))}
          </div>
        )}
        {onRemoved !== undefined && (
          <div className="flex items-center justify-between gap-2 p-3">
            <Label htmlFor={`${testId}-removed`} className="text-[13px] font-normal">Include what builds no longer find</Label>
            <Switch id={`${testId}-removed`} checked={removed} onCheckedChange={onRemoved} data-testid={`${testId}-removed`} />
          </div>
        )}
      </PopoverContent>
    </Popover>
  );
}

/**
 * The foot of a grid read a page at a time: how many rows are in view, of how many when that is known, and what it is
 * doing while it reads the next page. The rows load as the grid is scrolled; the button reads the next page by hand.
 */
export function GridFooter({ shown, total, noun, hasMore, loading, onMore, testId }: {
  shown: number;
  /** Every row there is, when the grid is not narrowed and the dimension says how many it holds. */
  total: number | null;
  noun: string;
  hasMore: boolean;
  loading: boolean;
  onMore: () => void;
  testId: string;
}) {
  if (shown === 0) {
    return null;
  }

  const plural = `${noun}${shown === 1 && total === null ? "" : "s"}`;
  return (
    <div className="flex h-8 items-center justify-between gap-3 border-t border-border px-3 text-xs text-muted-foreground" data-testid={testId}>
      <span className="font-mono tabular-nums">
        {total !== null && total > shown
          ? `${shown.toLocaleString("en-US")} of ${total.toLocaleString("en-US")} ${plural}`
          : `${shown.toLocaleString("en-US")} ${plural}${hasMore ? " so far" : ""}`}
      </span>
      {hasMore && (
        <Button variant="ghost" size="xs" onClick={onMore} disabled={loading} data-testid={`${testId}-more`}>
          {loading && <Loader2 className="animate-spin" />}
          {loading ? "Loading" : "Load more"}
        </Button>
      )}
    </div>
  );
}
