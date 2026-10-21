import { CircleCheck, CircleX, TriangleAlert } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import type { DeliveryDimensionView, DeliveryDimensionViewCheck, DeliveryDimensionViewJoinVerdict, DimensionViewCheckStatus } from "../../../api/delivery";
import { ExplainTip } from "@/components/ExplainTip";
import {
  VERDICT_VISUALS, VIEW_STANDING_VISUALS, checkWhen, utcText, verdictText, viewStandingOf, type ViewStanding,
} from "./dimensionViewFormat";

/** Why a view stands where it does, in the words a tooltip gives: what the standing means, the note a drop left, and the last write. */
function standingText(view: DeliveryDimensionView, standing: ViewStanding): string {
  const lines = [VIEW_STANDING_VISUALS[standing].hint];
  if (view.note != null) {
    lines.push(view.note);
  }

  if (view.writtenUtc != null) {
    lines.push(`Last written ${utcText(view.writtenUtc)}${view.writtenBy != null ? ` by ${view.writtenBy}` : ""}.`);
  }

  return lines.join("\n\n");
}

/** Where a view stands, as a glyph in its status colour with the words on hover. */
export function ViewStandingGlyph({ view, testId }: { view: DeliveryDimensionView; testId?: string }) {
  const standing = viewStandingOf(view);
  const visual = VIEW_STANDING_VISUALS[standing];
  const Icon = visual.icon;
  return (
    <RichTooltip title={visual.label} body={standingText(view, standing)}>
      <span className="inline-flex shrink-0" data-testid={testId} data-standing={standing}>
        <Icon className={cn("size-4", visual.tone)} aria-label={visual.label} />
      </span>
    </RichTooltip>
  );
}

/** Where a view stands, as its glyph and its word; what it means, the note a drop left and the last write on hover, with Copy. */
export function ViewStandingMark({ view, testId }: { view: DeliveryDimensionView; testId: string }) {
  const standing = viewStandingOf(view);
  const visual = VIEW_STANDING_VISUALS[standing];
  const Icon = visual.icon;
  return (
    <ExplainTip title={visual.label} text={standingText(view, standing)} testId={testId}>
      <span className="inline-flex items-center gap-1.5 align-middle whitespace-nowrap text-[12px]" data-testid={testId} data-standing={standing}>
        <Icon className={cn("size-3.5 shrink-0", visual.tone)} aria-hidden />
        {visual.label}
      </span>
    </ExplainTip>
  );
}

/**
 * What the flow's templates say of a join, as a neutral chip led by a glyph in its tone: it agrees, it joins a dimension of
 * another entity type than its column names (so it finds nothing), or no template says. The sentence on hover, with Copy.
 */
export function JoinVerdictBadge({ verdict, testId }: { verdict: DeliveryDimensionViewJoinVerdict; testId: string }) {
  const visual = VERDICT_VISUALS[verdict.verdict] ?? VERDICT_VISUALS.unchecked;
  const Icon = visual.icon;
  return (
    <ExplainTip title={`Template: ${visual.label}`} text={verdictText(verdict)} testId={testId}>
      <Badge
        variant="secondary"
        className="rounded-md border-border bg-muted/60 font-normal text-foreground"
        data-testid={testId}
        data-verdict={verdict.verdict}
      >
        <Icon className={cn("size-3.5 shrink-0", visual.tone)} aria-hidden />
        {visual.label}
      </Badge>
    </ExplainTip>
  );
}

/** What a check came to, as a glyph in its tone. */
export function CheckGlyph({ status, className }: { status: DimensionViewCheckStatus; className?: string }) {
  return status === "passed"
    ? <CircleCheck className={cn("size-3.5 shrink-0 text-success", className)} aria-label="Passed" />
    : <CircleX className={cn("size-3.5 shrink-0 text-destructive", className)} aria-label="Failed" />;
}

/**
 * The notes a check or a run left of a view, as a glyph and their count (and the word, unless `compact`); the notes
 * themselves on hover, with Copy. Nothing for none.
 */
export function NotesMark({ notes, title = "Notes", compact = false, testId }: { notes: readonly string[]; title?: string; compact?: boolean; testId: string }) {
  if (notes.length === 0) {
    return null;
  }

  return (
    <ExplainTip title={title} text={notes.join("\n")} testId={testId}>
      <span className="inline-flex items-center gap-1 align-middle whitespace-nowrap text-[12px]" data-testid={testId} aria-label={`${notes.length} ${notes.length === 1 ? "note" : "notes"}`}>
        <TriangleAlert className="size-3.5 shrink-0 text-warning" aria-hidden />
        <span className="font-mono tabular-nums">{notes.length.toLocaleString("en-US")}</span>
        {!compact && (
          <>
            {" "}
            <span className="text-muted-foreground">{notes.length === 1 ? "note" : "notes"}</span>
          </>
        )}
      </span>
    </ExplainTip>
  );
}

/**
 * A view's newest check in a line: passed with the rows it read and the notes it left, or failed with SQL Server's message
 * on hover; a quiet word when no build has checked it.
 */
export function ViewCheckMark({ check, testId }: { check: DeliveryDimensionViewCheck | null | undefined; testId: string }) {
  if (check == null) {
    return <span className="text-[12px] text-muted-foreground" data-testid={testId}>not checked</span>;
  }

  if (check.status === "failed") {
    return (
      <ExplainTip title="Why the check failed" text={`${check.error ?? "The check kept no reason."}\n\n${checkWhen(check)}`} testId={testId}>
        <span className="inline-flex items-center gap-1.5 align-middle whitespace-nowrap text-[12px]" data-testid={testId} data-status="failed">
          <CheckGlyph status="failed" />
          failed
        </span>
      </ExplainTip>
    );
  }

  return (
    <span className="inline-flex items-center gap-2.5 align-middle whitespace-nowrap" data-testid={testId} data-status="passed">
      <RichTooltip body={checkWhen(check)}>
        <span className="inline-flex items-center gap-1.5 align-middle text-[12px]">
          <CheckGlyph status="passed" />
          <span className="font-mono tabular-nums">{check.rows.toLocaleString("en-US")}</span>
          <span className="text-muted-foreground">{check.rows === 1 ? "row" : "rows"}</span>
        </span>
      </RichTooltip>
      <NotesMark notes={check.notes} title="What the check found" compact testId={`${testId}-notes`} />
    </span>
  );
}
