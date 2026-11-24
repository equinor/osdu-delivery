import type {
  RunSummary, StreamPath, StreamPathEdge, StreamPathFiles, StreamPathFire, StreamPathNode, StreamPathRun,
} from "../../api/types.ts";
import { formatBytes, parseUtc } from "../../lib/time.ts";

// The rules of a stream's path page that hold no React: what one flow's run did in a fire, how the canvas inks the
// hand-overs, and the words the cards say it in. The page and its canvas draw; this decides.

/**
 * What one flow's run did in a fire, as the canvas inks it. "moved": it wrote something. "empty": it ran and wrote
 * nothing. "absent": the flow had no run in this fire, which is not a fault (a source's fire need not run every flow
 * on a path, and a flow on another schedule runs in fires of its own).
 */
export type RunOutcome = "moved" | "empty" | "failed" | "running" | "queued" | "cancelled" | "absent";

function hasBreakdown(run: RunSummary): boolean {
  return run.rowsInserted !== null || run.rowsUpdated !== null || run.rowsDeleted !== null;
}

function changed(run: RunSummary): number {
  return (run.rowsInserted ?? 0) + (run.rowsUpdated ?? 0) + (run.rowsDeleted ?? 0);
}

/** True when a run's own result reports a positive count: how a flow kind that counts something other than rows (a
 * module's) says it did something. */
function resultMoved(run: RunSummary): boolean {
  return Object.values(run.resultCounts ?? {}).some((count) => count > 0);
}

function lifecycle(run: RunSummary): RunOutcome | null {
  switch (run.status) {
    case "queued":
      return "queued";
    case "running":
      return "running";
    case "failed":
      return "failed";
    case "cancelled":
      return "cancelled";
    default:
      return null;
  }
}

/**
 * What a run WROTE: a run that reports an insert/update/delete breakdown moved data when any of it is non-zero (an
 * ingestion that read rows and changed none moved nothing on); one that reports only a row count moved data when that
 * count is positive; and one of a kind that counts in its result moved data when the result reports a positive count.
 */
export function runOutcome(run: RunSummary | undefined): RunOutcome {
  if (run === undefined) {
    return "absent";
  }

  const state = lifecycle(run);
  if (state !== null) {
    return state;
  }

  if (hasBreakdown(run)) {
    return changed(run) > 0 ? "moved" : "empty";
  }

  return (run.rowsLoaded ?? 0) > 0 || resultMoved(run) ? "moved" : "empty";
}

/** What a run READ: whatever it wrote, and also rows or files it took in, so the line into an ingestion that found
 * nothing new still shows the data reached it. */
export function readOutcome(entry: StreamPathRun | undefined): RunOutcome {
  if (entry === undefined) {
    return "absent";
  }

  const wrote = runOutcome(entry.run);
  if (wrote !== "empty") {
    return wrote;
  }

  return (entry.run.rowsLoaded ?? 0) > 0 || (entry.files?.files ?? 0) > 0 ? "moved" : "empty";
}

/** Each flow's run in a fire, by pipeline id. A flow run twice in one fire (a retry) is represented by its later run. */
export function runsByFlow(fire: StreamPathFire | null): Map<string, StreamPathRun> {
  const byFlow = new Map<string, StreamPathRun>();
  for (const entry of fire?.runs ?? []) {
    const known = byFlow.get(entry.run.pipelineId);
    if (known === undefined || known.run.writtenUtc < entry.run.writtenUtc) {
      byFlow.set(entry.run.pipelineId, entry);
    }
  }
  return byFlow;
}

/**
 * How the canvas inks one hand-over in a fire. A flow writing an object carries what the flow's run wrote; an object
 * read by a flow carries what the reading run took in. So a chain whose ingestion read rows and changed none shows data
 * reaching the ingestion and stopping there.
 */
export function edgeOutcome(edge: StreamPathEdge, nodes: ReadonlyMap<string, StreamPathNode>, runs: ReadonlyMap<string, StreamPathRun>): RunOutcome {
  const from = nodes.get(edge.from);
  const to = nodes.get(edge.to);
  if (from?.type === "flow" && from.pipelineId !== null) {
    return runOutcome(runs.get(from.pipelineId)?.run);
  }

  if (to?.type === "flow" && to.pipelineId !== null) {
    return readOutcome(runs.get(to.pipelineId));
  }

  return "absent";
}

/** A row count as a card says it: grouped digits, compact past a hundred thousand so a card stays one line. */
export function formatCount(value: number): string {
  if (Math.abs(value) < 100_000) {
    return Math.round(value).toLocaleString();
  }

  const scaled = (amount: number, unit: string) => `${amount.toFixed(amount < 10 ? 1 : 0).replace(/\.0$/, "")}${unit}`;
  return Math.abs(value) >= 1_000_000 ? scaled(value / 1_000_000, "M") : scaled(value / 1_000, "k");
}

/**
 * What a run of one of SQLFlow's own kinds did, in a card's words: what it took in, then what it did to its table.
 * "15 in · 3 new, 2 changed"; "5 in · nothing changed" for a run that found nothing new; "1,204 rows" for a kind that
 * reports only a row count.
 */
export function runFigures(run: RunSummary): string {
  if (hasBreakdown(run)) {
    const parts = [
      [run.rowsInserted ?? 0, "new"],
      [run.rowsUpdated ?? 0, "changed"],
      [run.rowsDeleted ?? 0, "removed"],
    ]
      .filter(([count]) => (count as number) > 0)
      .map(([count, word]) => `${formatCount(count as number)} ${word as string}`);
    const did = parts.length === 0 ? "nothing changed" : parts.join(", ");
    return run.rowsLoaded === null ? did : `${formatCount(run.rowsLoaded)} in · ${did}`;
  }

  if (run.rowsLoaded !== null) {
    return run.rowsLoaded === 1 ? "1 row" : `${formatCount(run.rowsLoaded)} rows`;
  }

  return "no row counts";
}

/** What an object took in from the run that writes it, in a card's words; null when its writer reports no breakdown
 * (a kind counting in its result says so on its own card). */
export function landedFigures(run: RunSummary): string | null {
  if (!hasBreakdown(run)) {
    return null;
  }

  const total = changed(run);
  if (total === 0) {
    return "no change";
  }

  const parts = [
    [run.rowsInserted ?? 0, "+"],
    [run.rowsUpdated ?? 0, "~"],
    [run.rowsDeleted ?? 0, "-"],
  ]
    .filter(([count]) => (count as number) > 0)
    .map(([count, sign]) => `${sign as string}${formatCount(count as number)}`);
  return parts.join(" ");
}

/** The files a run took in, as the card for where data enters says it: "2 files · 15 rows · 150 B". */
export function filesFigures(files: StreamPathFiles): string {
  const count = files.files === 1 ? "1 file" : `${formatCount(files.files)} files`;
  const rows = files.rows === 1 ? "1 row" : `${formatCount(files.rows)} rows`;
  return `${count} · ${rows} · ${formatBytes(files.bytes)}`;
}

/** A fire's start as the page names it: weekday, day, month and time, in the reader's own zone. */
export function fireTime(startedUtc: string): string {
  return parseUtc(startedUtc).toLocaleString(undefined, {
    weekday: "short", day: "numeric", month: "short", hour: "2-digit", minute: "2-digit",
  });
}

/** The fires oldest first, the order a strip of them reads left to right. */
export function firesOldestFirst(fires: readonly StreamPathFire[]): StreamPathFire[] {
  return [...fires].sort((a, b) => a.startedUtc.localeCompare(b.startedUtc) || a.key.localeCompare(b.key));
}

/** The flows of a path in the order the canvas runs them, top to bottom: by wave, then by name. A flow with no wave
 * goes last. */
export function flowsInRunningOrder(path: StreamPath): StreamPathNode[] {
  return path.nodes
    .filter((node) => node.type === "flow")
    .sort((a, b) => (a.wave ?? Number.MAX_SAFE_INTEGER) - (b.wave ?? Number.MAX_SAFE_INTEGER) || a.name.localeCompare(b.name));
}

/**
 * How a card names a node: a flow by its name; a table or a view by its schema and its name, because one path often
 * holds the same name twice (the pre-ingestion table and the archive table it is keyed into); anything else (a file
 * location, a dataset) by its name, which already says where it is.
 */
export function nodeLabel(node: StreamPathNode): string {
  const qualified = node.qualifiedName;
  if (node.type === "flow" || (node.kind !== "table" && node.kind !== "view")
    || qualified === null || !qualified.endsWith(`.${node.name}`)) {
    return node.name;
  }

  const schema = qualified.slice(0, -(node.name.length + 1)).split(".").pop() ?? "";
  return schema === "" ? node.name : `${schema}.${node.name}`;
}

/** Where a path begins and ends: the objects data enters through, and the objects nothing on the path reads on from
 * (downstream of the selected flow), each as its card names it. */
export function pathEnds(path: StreamPath): { from: string[]; to: string[] } {
  const leaving = new Set(path.edges.map((edge) => edge.from));
  const from = path.nodes.filter((node) => node.external).map(nodeLabel);
  const to = path.nodes
    .filter((node) => node.type === "object" && node.role === "downstream" && !leaving.has(node.id))
    .map(nodeLabel);
  return { from, to };
}

/** How a fire went, worst first: the count of runs that failed, are still under way, and that ran. */
export function fireTally(fire: StreamPathFire): { runs: number; failed: number; underWay: number; cancelled: number } {
  let failed = 0;
  let underWay = 0;
  let cancelled = 0;
  for (const entry of fire.runs) {
    const outcome = runOutcome(entry.run);
    failed += outcome === "failed" ? 1 : 0;
    underWay += outcome === "running" || outcome === "queued" ? 1 : 0;
    cancelled += outcome === "cancelled" ? 1 : 0;
  }
  return { runs: fire.runs.length, failed, underWay, cancelled };
}

/** "3 runs, 1 failed": a fire's tally in words, the bad news first after the size. */
export function fireTallyText(fire: StreamPathFire): string {
  const tally = fireTally(fire);
  const parts = [tally.runs === 1 ? "1 run" : `${tally.runs} runs`];
  if (tally.failed > 0) {
    parts.push(`${tally.failed} failed`);
  }
  if (tally.underWay > 0) {
    parts.push(`${tally.underWay} under way`);
  }
  if (tally.cancelled > 0) {
    parts.push(`${tally.cancelled} cancelled`);
  }
  if (parts.length === 1) {
    parts.push(tally.runs === 1 ? "succeeded" : "all succeeded");
  }
  return parts.join(", ");
}

/** The words for what is counted on a node, by what it is: "2 more inputs", "24 more readers". */
export function omissionText(what: StreamPathNode["omitted"][number]["what"], count: number): string {
  const noun: Record<typeof what, [string, string]> = {
    producers: ["more flow feeds this", "more flows feed this"],
    inputs: ["more input", "more inputs"],
    outputs: ["more output", "more outputs"],
    readers: ["more reader", "more readers"],
    upstream: ["flow further up", "flows further up"],
    downstream: ["flow further on", "flows further on"],
  };
  const [one, many] = noun[what];
  return `${count} ${count === 1 ? one : many}`;
}
