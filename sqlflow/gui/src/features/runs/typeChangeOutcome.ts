import type { ColumnTypeChange, RunStatus, TypeChangeStatus, TypeChangeTier } from "../../api/types.ts";

// How a run's warnings and its changes to existing columns' types read on a page. Pure and framework-free, so the words
// and tones are tested without a DOM. A status color marks a status (DESIGN.md 3.2): an outcome that needs an operator
// takes the warning tone (the destructive one when stored values refused the change), one that went through takes the
// success tone, and one that changed nothing stays quiet.

/** The status tones a type change's outcome renders in. */
export type TypeChangeTone = "success" | "warning" | "destructive" | "muted";

/** A type change's outcome as a page says it: the word and the tone it wears. */
export interface TypeChangeOutcome {
  label: string;
  tone: TypeChangeTone;
}

/** The word and tone of each outcome a run can report. */
export function typeChangeOutcome(status: TypeChangeStatus): TypeChangeOutcome {
  switch (status) {
    case "Applied":
      return { label: "applied", tone: "success" };
    case "Resumed":
      return { label: "finished", tone: "success" };
    case "Pending":
      return { label: "pending opt-in", tone: "warning" };
    case "Manual":
      return { label: "manual", tone: "warning" };
    case "RolledBack":
      return { label: "rolled back", tone: "warning" };
    case "Refused":
      return { label: "refused", tone: "destructive" };
    case "KeptWider":
      return { label: "kept wider", tone: "muted" };
    case "Planned":
      return { label: "planned", tone: "muted" };
  }
}

/** A tier as a page names it. */
export function typeChangeTierLabel(tier: TypeChangeTier): string {
  switch (tier) {
    case "Widening":
      return "widening";
    case "CheckedConversion":
      return "checked conversion";
    case "Manual":
      return "manual";
  }
}

/** The changes an operator has to act on, in the order the run reported them. */
export function needingAttention(changes: readonly ColumnTypeChange[]): ColumnTypeChange[] {
  return changes.filter((change) => change.needsAttention);
}

/** How many warnings a run raised, in words: "1 warning", "3 warnings". */
export function warningsLabel(count: number): string {
  return `${count.toLocaleString("en-US")} warning${count === 1 ? "" : "s"}`;
}

/** Whether a run succeeded with warnings: it finished, and something in it needs an operator. */
export function succeededWithWarnings(status: RunStatus | string, warningCount: number | undefined): boolean {
  return status === "succeeded" && (warningCount ?? 0) > 0;
}
