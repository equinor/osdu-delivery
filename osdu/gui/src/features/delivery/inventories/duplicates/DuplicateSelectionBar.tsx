import { CircleX, Copy as CopiesIcon, TriangleAlert, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ExplainTip } from "@/components/ExplainTip";
import { RelativeTime } from "@/components/RelativeTime";
import { isApiError } from "@/api/client";
import type { InventorySelectionState } from "../inventorySelection";
import { duplicateSelectionSentence } from "./duplicateModel";

/**
 * What stands under the Ids tab's toolbar while it lists a selection made of a duplicate check's picks: which check picked them
 * and by what key, when, and what keeps the selection from being removed by (a build since, an age past a week, a failure to
 * read it); the way back to the check, and the way out of the selection.
 */
export function DuplicateSelectionBar({ state, onCheck, onClear }: { state: InventorySelectionState; onCheck: (checkId: number) => void; onClear: () => void }) {
  const selection = state.selection;
  const sentence = duplicateSelectionSentence(state);
  return (
    <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5 border-b border-border px-3 py-1.5 text-[12.5px]" data-testid="duplicate-selection-bar" data-phase={state.phase}>
      <CopiesIcon className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
      <span className="min-w-0 truncate" title={sentence}>
        {selection === undefined ? "Reading the selection" : `Selection ${selection.selectionId}: ${sentence}`}
      </span>
      {selection !== undefined && state.phase === "completed" && (
        <span className="shrink-0 text-muted-foreground">
          {"made "}
          <RelativeTime value={selection.createdUtc} absolute={false} />
          {` by ${selection.actor}`}
        </span>
      )}
      {state.phase === "stale" && (
        <span className="inline-flex shrink-0 items-center gap-1" data-testid="duplicate-selection-stale">
          <TriangleAlert className="size-3.5 text-warning" aria-hidden />
          The inventory was built again since the check: check again before removing what it picks
        </span>
      )}
      {state.phase === "expired" && <span className="shrink-0 text-muted-foreground">This selection is older than a week, and its ids were retired with it</span>}
      {state.phase === "refused" && (
        <span className="inline-flex min-w-0 items-center gap-1">
          <CircleX className="size-3.5 shrink-0 text-destructive" aria-hidden />
          <ExplainTip title="Why the selection cannot be read" text={isApiError(state.problem) ? state.problem.detail ?? state.problem.title : String(state.problem)} testId="duplicate-selection-problem">
            <span className="cursor-help">The selection cannot be read</span>
          </ExplainTip>
        </span>
      )}
      <span className="ml-auto flex shrink-0 items-center gap-1">
        {selection?.duplicateCheckId !== undefined && (
          <Button variant="link" size="sm" className="h-6 px-1 text-[12.5px]" onClick={() => onCheck(selection.duplicateCheckId!)} data-testid="duplicate-selection-check">
            {`Back to check ${selection.duplicateCheckId}`}
          </Button>
        )}
        <Button variant="ghost" size="icon-sm" className="size-6 text-muted-foreground" onClick={onClear} aria-label="List every id again" title="List every id again" data-testid="duplicate-selection-clear">
          <X className="size-3.5" />
        </Button>
      </span>
    </div>
  );
}
