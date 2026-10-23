import { OutcomePill } from "@/components/StatusBadge";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import { REMOVED_KEY_STATES, type RemovedKeyState } from "../../../api/removals";
import { outcomeVisual, runStatusVisual, stateVisual } from "./removalFormat";

/** A deletion's state as its glyph: in its tone, or muted when quiet (a count of none). */
export function StateGlyph({ state, quiet = false, className }: { state: string; quiet?: boolean; className?: string }) {
  const visual = stateVisual(state);
  const Icon = visual.icon;
  return <Icon className={cn("size-3.5 shrink-0", quiet ? "text-muted-foreground/60" : visual.tone, className)} aria-hidden />;
}

/** What a try came to: the outcome's glyph in its tone and its name in the text tokens, so a page of them reads quietly. */
export function OutcomeMark({ outcome, testId }: { outcome: string; testId?: string }) {
  const visual = outcomeVisual(outcome);
  const Icon = visual.icon;
  return (
    <span className="inline-flex items-center gap-1 whitespace-nowrap text-[12px]" data-outcome={outcome} data-testid={testId}>
      <Icon className={cn("size-3.5 shrink-0", visual.tone)} aria-hidden />
      {visual.label}
    </span>
  );
}

/** How a removal run ended, as the outcome pill every run status wears, with what it means on hover. */
export function RemovalRunPill({ status, error, testId }: { status: string; error?: string; testId: string }) {
  const visual = runStatusVisual(status);
  const pill = <OutcomePill tone={visual.pill} label={visual.label} icon={visual.icon} testId={testId} />;
  return (
    <RichTooltip title={visual.label} body={error === undefined ? visual.hint : `${visual.hint}\n\n${error}`}>
      <span className="inline-flex">{pill}</span>
    </RichTooltip>
  );
}

/**
 * A ledger's deletions by state, each a glyph and a count on one line, its meaning on hover; a state with none is drawn
 * quiet, so the ones that need a look stand out.
 */
export function DeletionCounts({ counts, testId }: { counts: Partial<Record<RemovedKeyState, number>>; testId: string }) {
  return (
    <span className="inline-flex items-center gap-x-3 whitespace-nowrap" data-testid={testId}>
      {REMOVED_KEY_STATES.map((state) => {
        const count = counts[state] ?? 0;
        const visual = stateVisual(state);
        return (
          <RichTooltip key={state} title={visual.label} body={visual.hint}>
            <span
              className={cn("inline-flex items-center gap-1 text-[12px]", count === 0 && "text-muted-foreground")}
              data-state={state}
              data-count={count}
              aria-label={`${count} ${visual.label.toLowerCase()}`}
            >
              <StateGlyph state={state} quiet={count === 0} />
              <span className="font-mono tabular-nums">{count.toLocaleString("en-US")}</span>
            </span>
          </RichTooltip>
        );
      })}
    </span>
  );
}

const tabClass = (selected: boolean) => cn(
  "relative inline-flex h-9 shrink-0 items-center gap-1.5 whitespace-nowrap rounded-sm px-2.5 text-[12.5px] outline-none transition-colors",
  "focus-visible:bg-accent/60 after:absolute after:inset-x-2 after:bottom-0 after:h-0.5 after:rounded-full",
  selected ? "text-foreground after:bg-primary" : "text-muted-foreground after:bg-transparent hover:text-foreground",
);

function CountPill({ count, selected }: { count: number; selected: boolean }) {
  return (
    <span className={cn("rounded-full px-1.5 font-mono text-[11px] tabular-nums leading-4", selected ? "bg-primary/10 text-foreground" : "bg-muted text-muted-foreground")}>
      {count.toLocaleString("en-US")}
    </span>
  );
}

/**
 * A ledger's deletions as one line of tabs over its grid: every deletion first, then each state with its count and what it
 * means on hover. The line never wraps; what it has no room for scrolls sideways within it.
 */
export function RemovalStateStrip({ counts, selected, onSelect, testId = "removal-states" }: {
  counts: Partial<Record<RemovedKeyState, number>>;
  /** The state in view; null for every deletion. */
  selected: RemovedKeyState | null;
  onSelect: (state: RemovedKeyState | null) => void;
  testId?: string;
}) {
  const total = REMOVED_KEY_STATES.reduce((sum, state) => sum + (counts[state] ?? 0), 0);
  return (
    <div className="flex min-w-0 flex-1 items-center gap-0.5 overflow-x-auto [scrollbar-width:none]" role="toolbar" aria-label="States" data-testid={testId}>
      <button type="button" onClick={() => onSelect(null)} aria-pressed={selected === null} className={tabClass(selected === null)} data-testid={`${testId}-all`} data-count={total}>
        <span>Every deletion</span>
        <CountPill count={total} selected={selected === null} />
      </button>
      {REMOVED_KEY_STATES.map((state) => {
        const visual = stateVisual(state);
        const count = counts[state] ?? 0;
        return (
          <RichTooltip key={state} title={visual.label} body={visual.hint}>
            <button
              type="button"
              onClick={() => onSelect(state)}
              aria-pressed={selected === state}
              className={tabClass(selected === state)}
              data-testid={`${testId}-${state}`}
              data-count={count}
            >
              <StateGlyph state={state} quiet={count === 0} />
              <span>{visual.label}</span>
              <CountPill count={count} selected={selected === state} />
            </button>
          </RichTooltip>
        );
      })}
    </div>
  );
}
