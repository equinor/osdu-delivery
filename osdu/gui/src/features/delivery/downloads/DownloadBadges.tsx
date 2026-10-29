import { OutcomePill } from "@/components/StatusBadge";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import { DOWNLOAD_RECORD_STATES, type DownloadCounts, type DownloadRecordState } from "../../../api/downloads";
import { runStatusVisual, stateVisual } from "./downloadFormat";

/** Where a record stands, as its glyph: in its tone, or muted when quiet (a count of none). */
export function StateGlyph({ state, quiet = false, className }: { state: string; quiet?: boolean; className?: string }) {
  const visual = stateVisual(state);
  const Icon = visual.icon;
  return <Icon className={cn("size-3.5 shrink-0", quiet ? "text-muted-foreground/60" : visual.tone, className)} aria-hidden />;
}

/**
 * Where a record stands: the state's glyph in its tone and its name in the text tokens, so a page of them reads quietly; what
 * the state means on hover, with the record's own `detail` (why its last write did not happen) under it when it has one.
 */
export function StateMark({ state, detail, testId }: { state: string; detail?: string; testId?: string }) {
  const visual = stateVisual(state);
  return (
    <RichTooltip title={visual.label} body={detail === undefined ? visual.hint : `${visual.hint}\n\n${detail}`}>
      <span className="inline-flex items-center gap-1 whitespace-nowrap text-[12px]" data-state={state} data-testid={testId}>
        <StateGlyph state={state} />
        {visual.label}
      </span>
    </RichTooltip>
  );
}

/** How a download run ended, as the outcome pill every run status wears, with what it means on hover. */
export function DownloadRunPill({ status, error, testId }: { status: string; error?: string; testId: string }) {
  const visual = runStatusVisual(status);
  const pill = <OutcomePill tone={visual.pill} label={visual.label} icon={visual.icon} testId={testId} />;
  return (
    <RichTooltip title={visual.label} body={error === undefined ? visual.hint : `${visual.hint}\n\n${error}`}>
      <span className="inline-flex">{pill}</span>
    </RichTooltip>
  );
}

/**
 * A download's records by state, each a glyph and a count on one line, its meaning on hover; a state with none is drawn quiet,
 * so the ones that need a look stand out.
 */
export function RecordCounts({ counts, testId }: { counts: DownloadCounts | undefined; testId: string }) {
  return (
    <span className="inline-flex items-center gap-x-3 whitespace-nowrap" data-testid={testId}>
      {DOWNLOAD_RECORD_STATES.map((state) => {
        const count = counts?.[state] ?? 0;
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
 * A download's records as one line of tabs over its grid: every record first, then each state with its count and what it means
 * on hover. The line never wraps; what it has no room for scrolls sideways within it.
 */
export function RecordStateStrip({ counts, selected, onSelect, testId }: {
  counts: DownloadCounts | undefined;
  /** The state in view; null for every record. */
  selected: DownloadRecordState | null;
  onSelect: (state: DownloadRecordState | null) => void;
  testId: string;
}) {
  const total = DOWNLOAD_RECORD_STATES.reduce((sum, state) => sum + (counts?.[state] ?? 0), 0);
  return (
    <div className="flex min-w-0 flex-1 items-center gap-0.5 overflow-x-auto [scrollbar-width:none]" role="toolbar" aria-label="States" data-testid={testId}>
      <button type="button" onClick={() => onSelect(null)} aria-pressed={selected === null} className={tabClass(selected === null)} data-testid={`${testId}-all`} data-count={total}>
        <span>Every record</span>
        <CountPill count={total} selected={selected === null} />
      </button>
      {DOWNLOAD_RECORD_STATES.map((state) => {
        const visual = stateVisual(state);
        const count = counts?.[state] ?? 0;
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
