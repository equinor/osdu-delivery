import {
  Ban, CircleCheck, CircleDashed, CircleSlash, CircleX, Hourglass, Link2, ListX, Loader2, SearchX, ShieldAlert, Trash2, Users, type LucideIcon,
} from "lucide-react";
import { parseUtc } from "@/lib/time";
import type { RemovalEntry, RemovalFlow, RemovalRun, RemovalSource, RemovalTally, RemovedKey, RemovedKeyState } from "../../../api/removals";

/** How a state, an outcome or a reason is drawn and said: its glyph, the tone the glyph takes, its name, and what it means. */
export interface RemovalVisual {
  icon: LucideIcon;
  label: string;
  /** The glyph's colour: a status token, or muted for what needs no look. */
  tone: string;
  hint: string;
  spin?: boolean;
}

const UNKNOWN = (what: string, value: string): RemovalVisual => ({
  icon: CircleDashed,
  label: value,
  tone: "text-muted-foreground",
  hint: `The control plane reports '${value}', ${what} this page does not describe.`,
});

export const STATE_VISUALS: Record<RemovedKeyState, RemovalVisual> = {
  pending: {
    icon: Hourglass,
    label: "Pending",
    tone: "text-warning",
    hint: "Found removed and not yet taken out: the next removal run takes it. Until then no row of the key older than its removal is delivered.",
  },
  "awaiting-approval": {
    icon: ShieldAlert,
    label: "Waiting for approval",
    tone: "text-warning",
    hint: "Found by a run whose guard held what it found: more keys than the guard allows. Nothing is done with it, and no row of it is kept out, until an operator approves the count.",
  },
  removed: {
    icon: Trash2,
    label: "Removed",
    tone: "text-muted-foreground",
    hint: "Done: taken out of OSDU (reversibly), settled in the ledger, or kept out. It is never asked of OSDU again; a row of the key newer than its removal is delivered again.",
  },
};

export const OUTCOME_VISUALS: Record<string, RemovalVisual> = {
  removed: { icon: Trash2, label: "Removed from OSDU", tone: "text-success", hint: "Taken out of OSDU with a soft delete, which OSDU can undo, and marked removed in the ledger." },
  "already-gone": { icon: CircleSlash, label: "Already gone", tone: "text-muted-foreground", hint: "OSDU no longer held it when the removal reached it; the ledger was settled as for a removal." },
  settled: { icon: CircleCheck, label: "Settled", tone: "text-muted-foreground", hint: "The ledger held it and OSDU did not: its queued work was dropped and it was marked removed, nothing asked of OSDU." },
  "kept-out": { icon: Ban, label: "Kept out", tone: "text-muted-foreground", hint: "Only the record table held it, never delivered: it is kept out, so no run delivers it." },
  referenced: { icon: Link2, label: "Referenced", tone: "text-warning", hint: "Left in OSDU: other records there still name it, and the removal keeps referenced records. The next run tries again." },
  shared: { icon: Users, label: "Shared", tone: "text-warning", hint: "Left in OSDU: another ledger of the partition holds the same id live. The next run tries again." },
  busy: { icon: Loader2, label: "Busy", tone: "text-info", hint: "A worker was sending it, or another removal held it: the next run takes it." },
  failed: { icon: CircleX, label: "Failed", tone: "text-destructive", hint: "OSDU refused or failed the removal; the detail says what it answered. The next run tries again." },
};

export const REASON_VISUALS: Record<string, RemovalVisual> = {
  absent: { icon: SearchX, label: "Not in the source", tone: "text-muted-foreground", hint: "The source's current keys, read whole, did not hold it." },
  listed: { icon: ListX, label: "Listed as removed", tone: "text-muted-foreground", hint: "The source's list of removed keys names it." },
};

export const RUN_STATUS_VISUALS: Record<string, RemovalVisual & { pill: "success" | "info" | "warning" | "destructive" | "muted" }> = {
  running: { icon: Loader2, label: "running", tone: "text-info", hint: "The run is reading, comparing or taking out.", spin: true, pill: "info" },
  completed: {
    icon: CircleCheck, label: "completed", tone: "text-success",
    hint: "Read and compared its sources and took out every pending deletion it could; what it could not, the next run takes.", pill: "success",
  },
  guarded: {
    icon: ShieldAlert, label: "held", tone: "text-warning",
    hint: "Its guard held what it found for an operator's approval; the deletions already pending it took out as any run does.", pill: "warning",
  },
  failed: { icon: CircleX, label: "failed", tone: "text-destructive", hint: "Stopped on an error; what it recorded stands.", pill: "destructive" },
};

export function stateVisual(state: string): RemovalVisual {
  return (STATE_VISUALS as Record<string, RemovalVisual>)[state] ?? UNKNOWN("a state", state);
}

export function outcomeVisual(outcome: string): RemovalVisual {
  return OUTCOME_VISUALS[outcome] ?? UNKNOWN("an outcome", outcome);
}

export function reasonVisual(reason: string): RemovalVisual {
  return REASON_VISUALS[reason] ?? UNKNOWN("a reason", reason);
}

export function runStatusVisual(status: string) {
  return RUN_STATUS_VISUALS[status] ?? { ...UNKNOWN("a run status", status), pill: "muted" as const };
}

/** The key tuple's values, in the record key's order; empty for a tuple that does not parse. */
export function keyParts(json: string | undefined): string[] {
  if (json === undefined || json === "") {
    return [];
  }

  try {
    const parsed: unknown = JSON.parse(json);
    return Array.isArray(parsed) ? parsed.map((part) => (part === null ? "" : String(part))) : [];
  } catch {
    return [];
  }
}

/** A key as a reader knows it: its values joined, each named by its column when the columns are known. */
export function keyText(key: Pick<RemovedKey, "sourceKeyJson" | "deliveryKey">, columns?: readonly string[]): string {
  const parts = keyParts(key.sourceKeyJson);
  if (parts.length === 0) {
    return key.deliveryKey;
  }

  return columns !== undefined && columns.length === parts.length
    ? parts.map((part, index) => `${columns[index]}=${part}`).join(", ")
    : parts.join(" / ");
}

/** An instant in the words a tooltip uses. */
export function instant(value: string): string {
  const date = parseUtc(value);
  return Number.isNaN(date.getTime()) ? value : `${date.toISOString().slice(0, 16).replace("T", " ")} UTC`;
}

/** Why a key counts as removed, where it was found, and what taking it out came to, as its tooltip says it. */
export function removalWhy(key: RemovedKey): string {
  const origin = key.fileName === undefined ? "" : ` (${key.fileName}${key.rowNumber === undefined ? "" : `, row ${key.rowNumber.toLocaleString("en-US")}`})`;
  const why = key.reason === "listed"
    ? `The source listed the key as removed at ${instant(key.removedUtc)}${origin}.`
    : `The source's keys, read at ${instant(key.removedUtc)}, no longer held it.`;
  const found = `Found by ${key.removalFlow} (${key.entry}), removal run ${key.detectedRunId}, ${instant(key.createdUtc)}.`;
  const approved = key.approvedBy === undefined ? "" : `\nApproved by ${key.approvedBy}${key.approvedUtc === undefined ? "" : ` at ${instant(key.approvedUtc)}`}.`;
  const outcome = key.outcome === undefined
    ? ""
    : `\n\n${outcomeVisual(key.outcome).label} after ${key.tries} ${key.tries === 1 ? "try" : "tries"}${key.doneRunId === undefined ? "" : ` (removal run ${key.doneRunId})`}: ${outcomeVisual(key.outcome).hint}`
      + (key.detail === undefined ? "" : `\n${key.detail}`);
  return `${why}\n${found}${approved}${outcome}\n\n${stateVisual(key.state).hint}`;
}

/** Where a removal reads keys from, in a line: the reader and its location, or the table and its source system. */
export function sourceLine(source: RemovalSource): string {
  return source.reads === "table"
    ? `${source.where ?? "a table"} (${source.type})`
    : `${source.type} at ${source.where ?? "the flow's folder"}`;
}

/** The columns a key is read from, in a line; none says each key column is read by its name. */
export function columnsLine(source: RemovalSource): string {
  const columns = source.columns.length === 0
    ? "each key column by its name"
    : source.columns.map((column) => {
      const into = column.as === undefined || column.as === column.name ? column.name : `${column.name} as ${column.as}`;
      return column.expr === undefined ? into : `${into} = ${column.expr}`;
    }).join(", ");
  const moment = source.removedAt === undefined
    ? ""
    : `; removed at ${source.removedAt.name}${source.removedAt.expr === undefined ? "" : ` = ${source.removedAt.expr}`}`;
  return `${columns}${moment}${source.filter === undefined ? "" : `; rows ${source.filter}`}`;
}

/** The delivery flow a removal writes to, as a page names it: the flow and its interface. */
export function ledgerName(entry: Pick<RemovalEntry, "flow" | "interface">): string {
  return entry.interface === undefined ? entry.flow : `${entry.flow} / ${entry.interface}`;
}

/** How many deletions a run finished (taken out, settled, kept out or already gone). */
export function finished(tally: RemovalTally): number {
  return tally.removed + tally.alreadyGone + tally.settled + tally.keptOut;
}

/** How many deletions a run left for the next one (referenced, shared, busy or failed). */
export function leftOver(tally: RemovalTally): number {
  return tally.referenced + tally.shared + tally.busy + tally.failed;
}

/** What a run read, in the words a tooltip uses. */
export function readText(run: RemovalRun): string {
  const t = run.tally;
  const parts = [
    `${t.rowsRead.toLocaleString("en-US")} row(s) read`,
    `${t.sourceKeys.toLocaleString("en-US")} key(s) the source holds now`,
    `${t.listedKeys.toLocaleString("en-US")} listed as removed`,
    `${t.ingestionKeys.toLocaleString("en-US")} in the record table`,
    `${t.compared.toLocaleString("en-US")} compared`,
  ];
  return parts.join("\n");
}

/** What a run left for the next one, in the words a tooltip uses. */
export function leftText(tally: RemovalTally): string {
  return [
    `${tally.referenced.toLocaleString("en-US")} referenced: other records in OSDU still name them`,
    `${tally.shared.toLocaleString("en-US")} shared: another ledger holds the same id`,
    `${tally.busy.toLocaleString("en-US")} busy: a worker or another removal held them`,
    `${tally.failed.toLocaleString("en-US")} failed: OSDU refused or failed`,
  ].join("\n");
}

/** What a run finished, in the words a tooltip uses. */
export function finishedText(tally: RemovalTally): string {
  return [
    `${tally.removed.toLocaleString("en-US")} removed from OSDU (reversibly)`,
    `${tally.alreadyGone.toLocaleString("en-US")} already gone from OSDU`,
    `${tally.settled.toLocaleString("en-US")} settled in the ledger (OSDU did not hold them)`,
    `${tally.keptOut.toLocaleString("en-US")} kept out (only the record table held them)`,
  ].join("\n");
}

/** The guard in a sentence: how much a removal may find removed before it waits for an operator's approval. */
export function guardLine(flow: Pick<RemovalFlow, "guardPercent" | "guardRecords">): string {
  return flow.guardRecords === undefined
    ? `a removal that finds more than ${flow.guardPercent}% of the keys it compares removed waits for approval`
    : `a removal that finds more than ${flow.guardPercent}% of the keys it compares removed, or more than ${flow.guardRecords.toLocaleString("en-US")}, waits for approval`;
}

/** The delivery flow's Deletions tab, on the ledger the removal writes to. */
export function deletionsRoute(entry: RemovalEntry, partition: string | undefined): string | null {
  if (entry.deliveryPipelineId === undefined) {
    return null;
  }

  const query = new URLSearchParams({ tab: "deletions" });
  if (entry.interface !== undefined) {
    query.set("interface", entry.interface);
  }

  if (partition !== undefined) {
    query.set("partition", partition);
  }

  return `/pipelines/${entry.deliveryPipelineId}?${query.toString()}`;
}

/** Where the Removals page shows one removal of a flow. */
export function removalRoute(pipelineId: string, entry?: string): string {
  const query = new URLSearchParams({ flow: pipelineId });
  if (entry !== undefined) {
    query.set("removal", entry);
  }

  return `/delivery/removals?${query.toString()}`;
}

/** Why a run held what it found, in the words its banner uses. */
export function heldText(run: RemovalRun): string {
  const t = run.tally;
  const share = t.compared > 0 ? Math.round((t.detected / t.compared) * 1000) / 10 : null;
  if (run.sourceReadUtc !== undefined && t.sourceKeys === 0 && t.detected > 0) {
    return "its read of the source's keys came back empty, which is more often a broken read than a source emptied";
  }

  const bound = run.guardRecords === undefined
    ? `${run.guardPercent}% of the compared keys`
    : `${run.guardPercent}% of the compared keys, or ${run.guardRecords.toLocaleString("en-US")} keys`;
  return share === null
    ? `it found ${t.detected.toLocaleString("en-US")} removed, more than the guard allows (${bound})`
    : `it found ${t.detected.toLocaleString("en-US")} of ${t.compared.toLocaleString("en-US")} compared keys removed (${share}%), more than the guard allows (${bound})`;
}
