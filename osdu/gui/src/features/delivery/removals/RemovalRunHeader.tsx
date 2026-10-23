import { useNavigate } from "react-router-dom";
import { Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import type { RunDetail } from "@/api/types";
import { DetailPair } from "@/components/DetailPair";
import { runResult } from "../runOutcome";

/** A count a removal run reported, or a muted dash when it reported none (a run that stopped before its removals). */
function Count({ value, testId }: { value: number | null; testId: string }) {
  return value !== null
    ? <span className="font-mono tabular-nums" data-testid={testId}>{value.toLocaleString("en-US")}</span>
    : <span className="text-muted-foreground" data-testid={testId}>-</span>;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

/** The removals a run's result names, each as the object it reported; none for a result that names none. */
function removalsOf(result: Record<string, unknown> | null): Record<string, unknown>[] {
  const removals = result?.removals;
  return Array.isArray(removals) ? removals.filter(isRecord) : [];
}

function numberIn(source: Record<string, unknown> | undefined, field: string): number {
  const value = source?.[field];
  return typeof value === "number" && Number.isFinite(value) ? value : 0;
}

/** A tally field summed over the run's removals; null for a run that reported none. */
function tallied(result: Record<string, unknown> | null, ...fields: string[]): number | null {
  const removals = removalsOf(result);
  if (removals.length === 0) {
    return null;
  }

  return removals.reduce<number>((sum, removal) => {
    const tally = isRecord(removal.tally) ? removal.tally : undefined;
    return sum + fields.reduce<number>((own, field) => own + numberIn(tally, field), 0);
  }, 0);
}

/** A field of each removal summed over the run; null for a run that reported none. */
function summed(result: Record<string, unknown> | null, field: string): number | null {
  const removals = removalsOf(result);
  return removals.length === 0 ? null : removals.reduce<number>((sum, removal) => sum + numberIn(removal, field), 0);
}

/** A removal run's way to the removals it ran, on its flow's Removals tab, where the held keys are approved. */
export function RemovalRunActions({ run }: { run: RunDetail }) {
  const navigate = useNavigate();
  return (
    <Button variant="outline" size="sm" onClick={() => navigate(`/pipelines/${run.pipelineId}?tab=removals`)} data-testid="run-open-removals">
      <Trash2 />
      Open removals
    </Button>
  );
}

/** How the run's removals came out, in place of the row counts SQLFlow's own runs carry. */
export function RemovalRunCounts({ run }: { run: RunDetail }) {
  const result = runResult(run);
  if (result?.operation === "plan") {
    const removals = removalsOf(result);
    return (
      <>
        <DetailPair label="Would find"><Count value={tallied(result, "detected")} testId="run-removals-would-find" /></DetailPair>
        <DetailPair label="Would hold">
          <Count value={removals.length === 0 ? null : removals.filter((removal) => removal.wouldHold === true).length} testId="run-removals-would-hold" />
        </DetailPair>
      </>
    );
  }

  return (
    <>
      <DetailPair label="Found removed"><Count value={tallied(result, "detected")} testId="run-removals-found" /></DetailPair>
      <DetailPair label="Removed"><Count value={tallied(result, "removed")} testId="run-removals-removed" /></DetailPair>
      <DetailPair label="Held"><Count value={summed(result, "awaitingApproval")} testId="run-removals-held" /></DetailPair>
      <DetailPair label="Left"><Count value={tallied(result, "referenced", "shared", "busy", "failed")} testId="run-removals-left" /></DetailPair>
    </>
  );
}
