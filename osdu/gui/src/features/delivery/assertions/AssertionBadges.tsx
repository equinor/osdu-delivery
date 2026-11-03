import { IconBadge, OutcomePill } from "@/components/StatusBadge";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import type { AssertionRunStatus, AssertionSeverity } from "../../../api/delivery";
import { OUTCOME_VISUALS, SEVERITY_TONES, runStatusVisual, type TestStanding } from "./assertionFormat";

/** Where a test stands, as the outcome pill every board, sheet and report shows it with. */
export function TestOutcomeBadge({ outcome, testId = "test-outcome" }: { outcome: TestStanding; testId?: string }) {
  const visual = OUTCOME_VISUALS[outcome];
  return <OutcomePill tone={visual.tone} label={visual.label} icon={visual.icon} testId={testId} />;
}

/** The same, as the tinted glyph alone, for a dense cell whose column already says what it holds. */
export function TestOutcomeIcon({ outcome, testId = "test-outcome-icon" }: { outcome: TestStanding; testId?: string }) {
  const visual = OUTCOME_VISUALS[outcome];
  return <IconBadge tone={visual.tone} label={visual.label} icon={visual.icon} testId={testId} />;
}

/** An assertion run's status. */
export function AssertionRunStatusBadge({ status, testId = "assertion-run-status" }: { status: AssertionRunStatus; testId?: string }) {
  const visual = runStatusVisual(status);
  return <OutcomePill tone={visual.tone} label={visual.label} icon={visual.icon} testId={testId} />;
}

/** The weight an assertion's failure carries: an outlined chip, so it never reads as an outcome. */
export function SeverityChip({ severity }: { severity: AssertionSeverity }) {
  return (
    <span
      className={cn("inline-flex items-center rounded-sm px-1.5 py-px font-mono text-[10.5px] leading-4 ring-1 ring-inset", SEVERITY_TONES[severity])}
      data-testid="assertion-severity"
    >
      {severity}
    </span>
  );
}

/** A test's tag as a toggle: pressed, it picks or filters by the tests carrying it. */
export function TagChip({ tag, pressed, onToggle, count }: { tag: string; pressed: boolean; onToggle: () => void; count?: number }) {
  return (
    <button
      type="button"
      aria-pressed={pressed}
      onClick={onToggle}
      className={cn(
        "inline-flex h-6 items-center gap-1 rounded-full border px-2 font-mono text-[11px] transition-colors",
        pressed ? "border-primary/70 bg-primary/10 text-foreground dark:bg-primary/20" : "text-muted-foreground hover:bg-muted",
      )}
      data-testid={`tag-${tag}`}
    >
      #{tag}
      {count !== undefined && <span className="tabular-nums text-muted-foreground">{count}</span>}
    </button>
  );
}

/** The fill of an outcome square; the square is always paired with its word on hover and for assistive tech. */
const SQUARE_CLASSES: Record<TestStanding, string> = {
  passed: "bg-success/70",
  failed: "bg-destructive",
  errored: "bg-destructive/45 ring-1 ring-inset ring-destructive",
  warned: "bg-warning/80",
  skipped: "bg-muted-foreground/25",
  notRun: "border border-dashed border-border",
  disabled: "bg-muted-foreground/40",
  elsewhere: "border border-dashed border-border",
  noted: "bg-info/60",
};

/**
 * One outcome as a small square, for a strip or a grid of many: the fill says the family at a glance, the tooltip and the
 * accessible name say the word and what it was, and a click opens it when there is something to open.
 */
export function OutcomeSquare({ outcome, title, onClick, size = "size-2.5", testId = "outcome-square" }: {
  outcome: TestStanding;
  title: string;
  onClick?: () => void;
  size?: string;
  testId?: string;
}) {
  const label = `${OUTCOME_VISUALS[outcome].label}: ${title}`;
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <button
          type="button"
          aria-label={label}
          className={cn(
            "shrink-0 rounded-[2px] transition-transform focus-visible:outline-2",
            onClick !== undefined ? "cursor-pointer hover:scale-125" : "cursor-default",
            size,
            SQUARE_CLASSES[outcome],
          )}
          onClick={onClick}
          data-testid={testId}
          data-outcome={outcome}
        />
      </TooltipTrigger>
      <TooltipContent>{label}</TooltipContent>
    </Tooltip>
  );
}

/**
 * How a set of tests came out, as a stacked bar: each outcome's share in its fill, the counts on hover and in its label.
 * `skipped` counts what is not run as it is now (or skipped), `disabled` what the documents switch off.
 */
export function OutcomeBar({ counts, className, testId = "outcome-bar" }: {
  counts: { passed: number; warned: number; failed: number; errored: number; skipped: number; disabled?: number };
  className?: string;
  testId?: string;
}) {
  const parts: { outcome: TestStanding; count: number }[] = [
    { outcome: "passed", count: counts.passed },
    { outcome: "warned", count: counts.warned },
    { outcome: "failed", count: counts.failed },
    { outcome: "errored", count: counts.errored },
    { outcome: "skipped", count: counts.skipped },
    { outcome: "disabled", count: counts.disabled ?? 0 },
  ];
  const total = parts.reduce((sum, part) => sum + part.count, 0);
  const label = total === 0 ? "no assertion" : parts.filter((p) => p.count > 0).map((p) => `${p.count} ${OUTCOME_VISUALS[p.outcome].label}`).join(", ");
  return (
    <div className={cn("flex h-2 w-full overflow-hidden rounded-full bg-muted", className)} role="img" aria-label={label} title={label} data-testid={testId}>
      {total > 0 && parts.filter((p) => p.count > 0).map((part) => (
        <span key={part.outcome} className={cn("h-full", SQUARE_CLASSES[part.outcome], "rounded-none ring-0")} style={{ width: `${(100 * part.count) / total}%` }} />
      ))}
    </div>
  );
}

/** One count of a status strip: what it counts, how many, the standing its number wears, and what it means. */
export interface StripCell {
  key: string;
  label: string;
  count: number;
  /** The standing the count wears when it is not zero; null for a neutral count (all of them). */
  standing: TestStanding | null;
  hint: string;
}

const STRIP_TEXT: Partial<Record<TestStanding, string>> = {
  passed: "text-success",
  failed: "text-destructive",
  errored: "text-destructive",
  warned: "text-warning",
  noted: "text-info",
};

/**
 * The counts a list is read by, on one line (DESIGN.md 7.7): first the share of what was evaluated that passed, with its
 * bar, then one cell per standing, each the filter to what it counts. A count of zero is quiet, and the selected cell
 * wears the accent ring; choosing it again clears the filter.
 */
export function StatusStrip({ passRate, evaluated, bar, cells, selected, onSelect, testId = "status-strip" }: {
  passRate: number | null;
  evaluated: number;
  bar: { passed: number; warned: number; failed: number; errored: number; skipped: number; disabled?: number };
  cells: readonly StripCell[];
  selected: string | null;
  onSelect: (key: string | null) => void;
  testId?: string;
}) {
  return (
    <div className="flex flex-wrap items-stretch overflow-hidden rounded-lg border bg-card" data-testid={testId}>
      <div className="flex min-w-44 flex-col justify-center gap-1 border-r px-3 py-2" data-testid="pass-rate">
        <div className="flex items-baseline gap-2">
          <span className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Pass rate</span>
          <span className="font-mono text-[15px] font-semibold tabular-nums">{passRate === null ? "-" : `${Math.round(passRate)}%`}</span>
        </div>
        <OutcomeBar counts={bar} className="h-1.5" />
        <span className="text-[11px] text-muted-foreground">
          {passRate === null ? "nothing evaluated yet" : `of ${evaluated.toLocaleString("en-US")} evaluated`}
        </span>
      </div>
      {cells.map((cell) => {
        const pressed = selected === cell.key;
        return (
          <Tooltip key={cell.key}>
            <TooltipTrigger asChild>
              <button
                type="button"
                aria-pressed={pressed}
                onClick={() => onSelect(pressed ? null : cell.key)}
                className={cn(
                  "flex min-w-24 flex-1 flex-col justify-center border-r px-3 py-2 text-left outline-none transition-colors last:border-r-0 hover:bg-accent/50 focus-visible:bg-accent/50",
                  pressed && "bg-primary/10 ring-1 ring-inset ring-primary dark:bg-primary/20",
                )}
                data-testid={`${testId}-${cell.key}`}
              >
                <span className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">{cell.label}</span>
                <span
                  className={cn(
                    "font-mono text-[15px] font-semibold leading-6 tabular-nums",
                    cell.count === 0 || cell.standing === null ? (cell.count === 0 ? "text-muted-foreground" : "") : STRIP_TEXT[cell.standing],
                  )}
                  data-testid={`${testId}-${cell.key}-value`}
                >
                  {cell.count.toLocaleString("en-US")}
                </span>
              </button>
            </TooltipTrigger>
            <TooltipContent>{cell.hint}</TooltipContent>
          </Tooltip>
        );
      })}
    </div>
  );
}
