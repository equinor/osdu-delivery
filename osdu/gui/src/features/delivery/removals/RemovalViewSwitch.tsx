import { cn } from "@/lib/utils";
import type { RemovedKeyState } from "../../../api/removals";

/** What a removal view shows: the ledger's deletions, or the removal runs. */
export type RemovalView = "deletions" | "runs";

/** A change of what the view shows, made in one change of the page's address. */
export interface RemovalShow {
  view?: RemovalView;
  state?: RemovedKeyState | null;
}

const switchClass = (selected: boolean) => cn(
  "inline-flex h-6 items-center rounded px-2 text-[12px] font-medium outline-none transition-colors focus-visible:ring-2 focus-visible:ring-ring/50",
  selected ? "bg-background text-foreground shadow-sm" : "text-muted-foreground hover:text-foreground",
);

/** The switch between the ledger's deletions and the removal runs, at the start of the grid's toolbar. */
export function RemovalViewSwitch({ view, onShow, runsLabel = "Runs" }: { view: RemovalView; onShow: (show: RemovalShow) => void; runsLabel?: string }) {
  return (
    <span className="mr-1 inline-flex shrink-0 items-center gap-0.5 rounded-md bg-muted p-0.5" role="group" aria-label="View" data-testid="removal-views">
      <button type="button" className={switchClass(view === "deletions")} aria-pressed={view === "deletions"} onClick={() => onShow({ view: "deletions" })} data-testid="removal-view-deletions">
        Deletions
      </button>
      <button type="button" className={switchClass(view === "runs")} aria-pressed={view === "runs"} onClick={() => onShow({ view: "runs" })} data-testid="removal-view-runs">
        {runsLabel}
      </button>
    </span>
  );
}

/** The toolbar line over a table of runs, holding the switch back to the deletions. */
export function RemovalRunsToolbar({ onShow, runsLabel }: { onShow: (show: RemovalShow) => void; runsLabel?: string }) {
  return (
    <div className="flex h-9 items-center border-b border-border px-1.5">
      <RemovalViewSwitch view="runs" onShow={onShow} runsLabel={runsLabel} />
    </div>
  );
}
