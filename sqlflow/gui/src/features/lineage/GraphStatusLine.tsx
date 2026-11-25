import type { ReactNode } from "react";
import { RotateCcw, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";

/** One reduction the reader made to the drawing. Removing it undoes just that one. */
export interface StatusPart {
  key: string;
  label: string;
  /** What removing it does, for the remove button's name and tooltip. */
  removeHint: string;
  onRemove: () => void;
}

/**
 * What the canvas is showing, in as few words as say it: how much of the scope is drawn, the node the drawing is
 * traced around with the trace's own controls, and each reduction the reader made, which is taken back from here on
 * its own; Reset takes every choice back. What the bar already says (the scope, how much is drawn, the day looked
 * at) is not said a second time here, so on a drawing nothing was done to and nothing is wrong with, the line is one
 * quiet count. It has no edge of its own: it belongs to the canvas under it.
 */
export function GraphStatusLine({ drawn, total, trace, parts, attention, canReset, onReset }: {
  drawn: number;
  total: number;
  /** The trace's controls, while a node is traced. */
  trace?: ReactNode;
  parts: readonly StatusPart[];
  /** Things the reader should know about the drawing's truth: a truncated walk, lineage not derived, stalled streams. */
  attention?: ReactNode;
  /** Whether anything about the drawing differs from how its scope opens. */
  canReset: boolean;
  onReset: () => void;
}) {
  return (
    <div
      data-testid="graph-status-line"
      className="flex min-h-7 shrink-0 flex-wrap items-center gap-x-2 gap-y-1 bg-background px-3 py-0.5 text-[12px]"
    >
      <span className="whitespace-nowrap text-muted-foreground" data-testid="graph-status-count">
        <span className="font-medium text-foreground tabular-nums">{drawn}</span>
        {" of "}
        <span className="font-medium text-foreground tabular-nums">{total}</span>
        {" drawn"}
      </span>
      {trace && (
        <>
          <span aria-hidden className="h-3.5 w-px shrink-0 bg-border" />
          {trace}
        </>
      )}
      {parts.map((part) => (
        <span
          key={part.key}
          data-testid={`graph-status-${part.key}`}
          className="inline-flex h-5 max-w-[320px] items-center gap-0.5 rounded-sm border border-border bg-card pr-0.5 pl-1.5"
        >
          <span className="truncate" title={part.label}>{part.label}</span>
          <Tooltip>
            <TooltipTrigger asChild>
              <button
                type="button"
                onClick={part.onRemove}
                aria-label={part.removeHint}
                data-testid={`graph-status-remove-${part.key}`}
                className="inline-flex size-4 shrink-0 cursor-pointer items-center justify-center rounded-sm text-muted-foreground hover:bg-accent hover:text-foreground"
              >
                <X aria-hidden className="size-3" />
              </button>
            </TooltipTrigger>
            <TooltipContent>{part.removeHint}</TooltipContent>
          </Tooltip>
        </span>
      ))}
      {canReset && (
        <Tooltip>
          <TooltipTrigger asChild>
            <Button variant="ghost" size="xs" onClick={onReset} className="h-5 gap-1 px-1.5 text-muted-foreground" data-testid="graph-reset">
              <RotateCcw className="size-3" />
              Reset
            </Button>
          </TooltipTrigger>
          <TooltipContent>Draw the scope as it opens: every choice of the bar, the View menu and the trace taken back.</TooltipContent>
        </Tooltip>
      )}
      {attention && <span className="ml-auto flex flex-wrap items-center gap-x-3 gap-y-1">{attention}</span>}
    </div>
  );
}
