// What the probe of a page's delivery runs reads off the runs it lists, kept free of React so the rules are tested on
// their own (unit/followRunsState.test.ts); followRuns.ts asks for the runs and acts on what this says.

/** What the probe needs of a run: which it is, whose, and where it stands. */
export interface FollowedRun {
  runId: string;
  pipelineId: string;
  status: string;
}

/** Whether a run has still to settle what it does to its flow's ledger. */
export function underWay(run: FollowedRun): boolean {
  return run.status === "queued" || run.status === "running";
}

/**
 * Each flow's runs as one signature: which runs the listing holds for it and where each stands. A run that arrives, moves
 * on or ends changes its flow's signature, whichever of the listed runs it is (a part of a split delivery ending before
 * the run that split it, say).
 */
export function signatures(runs: readonly FollowedRun[]): Map<string, string> {
  const byFlow = new Map<string, string>();
  for (const run of runs) {
    const mark = `${run.runId}:${run.status}`;
    const before = byFlow.get(run.pipelineId);
    byFlow.set(run.pipelineId, before === undefined ? mark : `${before},${mark}`);
  }

  return byFlow;
}

/** The flows whose signature moved since the last probe: never one seen for the first time, which nothing has shown yet. */
export function moved(before: ReadonlyMap<string, string>, now: ReadonlyMap<string, string>): string[] {
  return [...now].filter(([flow, signature]) => before.has(flow) && before.get(flow) !== signature).map(([flow]) => flow);
}

/** The runs under way, in the listing's order (newest first). */
export function runsUnderWay<T extends FollowedRun>(runs: readonly T[]): T[] {
  return runs.filter(underWay);
}

/**
 * How old a flow's counts may grow while one of its runs is under way before the probe reads them again: the counts move
 * with the run often enough to watch, and a page following several running flows asks for them no oftener than this. A run
 * that starts, moves on or ends has its flow's views read again at once, whatever their age.
 */
export const RUNNING_COUNTS_MS = 10_000;

/** Whether what was last read at `updatedAtMs` (0 when never) is old enough at `nowMs` to read again while a run is under way. */
export function countsDue(updatedAtMs: number, nowMs: number): boolean {
  return nowMs - updatedAtMs >= RUNNING_COUNTS_MS;
}
