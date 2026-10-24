import {
  ArchiveX, CircleAlert, CircleCheck, CircleDashed, CircleHelp, CircleSlash, CircleX, Ghost, GitCompare, ListX, Loader2, Replace, SearchX, Trash2,
  Undo2, Unlink, UserX, type LucideIcon,
} from "lucide-react";
import {
  INVENTORY_FINDINGS, RAISED_FINDINGS, inventoryApi, type Inventory, type InventoryCount, type InventoryFinding, type InventoryRecord, type InventoryRun,
} from "../../../api/inventories";

/** How a finding is drawn and said: its glyph, the tone the glyph takes when any id has it, its name, and what it means. */
export interface FindingVisual {
  icon: LucideIcon;
  label: string;
  /** The glyph's colour when the finding counts any id: a status token, or muted for one that needs no look. */
  tone: string;
  /** What the finding means, as its tooltip says it. */
  hint: string;
}

export const FINDING_VISUALS: Record<InventoryFinding, FindingVisual> = {
  orphan: {
    icon: Ghost, label: "Orphan", tone: "text-destructive",
    hint: "OSDU serves it, no ledger of the partition knows it, and an identity this estate writes as created it: a write that landed and was never recorded, or one made outside the flows.",
  },
  missing: {
    icon: SearchX, label: "Missing", tone: "text-destructive",
    hint: "A ledger expects it (a delivered record, or an id a delivery minted and kept) and storage does not hold it: removed or purged outside the flows, or lost in a restore.",
  },
  undoing: {
    icon: Undo2, label: "Undoing", tone: "text-warning",
    hint: "An unfinished delivery left it in OSDU, and its undo is due or failed: it has not been taken back yet.",
  },
  forgotten: {
    icon: ArchiveX, label: "Forgotten", tone: "text-warning",
    hint: "OSDU serves it, and the record that claimed it was purged from its ledger, or a delivery of such a record minted it.",
  },
  stale: {
    icon: Trash2, label: "Stale", tone: "text-warning",
    hint: "A ledger marks its record removed, or an undo removed or left the id, and OSDU still serves it.",
  },
  unconfirmed: {
    icon: CircleHelp, label: "Unconfirmed", tone: "text-warning",
    hint: "OSDU serves it, and the ledger's record never confirmed a delivery (pending, held or failed, no version): a write that landed and was not acknowledged.",
  },
  drifted: {
    icon: GitCompare, label: "Drifted", tone: "text-warning",
    hint: "A ledger delivered it, and OSDU serves another version: it was changed after the delivery, outside the flow.",
  },
  unlisted: {
    icon: ListX, label: "Unlisted", tone: "text-info",
    hint: "A ledger expects it and storage holds it, but the inventory's read did not list it: the search index has not caught up, or it is outside the inventory's query.",
  },
  foreign: {
    icon: UserX, label: "Foreign", tone: "text-muted-foreground",
    hint: "No ledger knows it, and another identity created it: not this estate's.",
  },
  superseded: {
    icon: Replace, label: "Superseded", tone: "text-muted-foreground",
    hint: "A dataset a later delivery of its record replaced, kept live on purpose since earlier versions of the record name it.",
  },
  tracked: {
    icon: CircleCheck, label: "Tracked", tone: "text-success",
    hint: "A ledger delivered it at the version OSDU serves, or a delivery minted it and committed: the two agree.",
  },
  gone: {
    icon: CircleSlash, label: "Gone", tone: "text-muted-foreground",
    hint: "OSDU no longer serves it and no ledger expects it (removed, as the ledgers say), or a ledger expects it and storage has not been asked for it yet: its reason says which.",
  },
  unreconciled: {
    icon: CircleDashed, label: "Unreconciled", tone: "text-muted-foreground",
    hint: "A build listed it and no reconcile has compared it with the ledgers yet.",
  },
};

/** A finding this page knows, or null for one a newer control plane added. */
export function knownFinding(finding: string | null | undefined): InventoryFinding | null {
  return INVENTORY_FINDINGS.find((known) => known === finding) ?? null;
}

/** How a finding is drawn: its own visual, or a neutral one for a finding this page does not know. */
export function findingVisual(finding: string): FindingVisual {
  const known = knownFinding(finding);
  return known === null
    ? { icon: CircleDashed, label: finding, tone: "text-muted-foreground", hint: `The control plane reports '${finding}', a finding this page does not describe.` }
    : FINDING_VISUALS[known];
}

/** Why an id has its finding: what the reconcile said of this id, then what the finding means. */
export function findingWhy(record: Pick<InventoryRecord, "finding" | "detail">): string {
  const meaning = findingVisual(record.finding).hint;
  return record.detail === undefined || record.detail === "" ? meaning : `${record.detail}\n\n${meaning}`;
}

export function isRaised(finding: string): boolean {
  return RAISED_FINDINGS.some((raised) => raised === finding);
}

/** The counts a page shows: every finding in report order, a finding the answer left out counted as zero. */
export function allCounts(counts: readonly InventoryCount[] | undefined): InventoryCount[] {
  const byFinding = new Map((counts ?? []).map((count) => [count.finding, count]));
  const listed: InventoryCount[] = INVENTORY_FINDINGS.map((finding) => byFinding.get(finding) ?? { finding, count: 0, raised: isRaised(finding) });
  return [...listed, ...(counts ?? []).filter((count) => knownFinding(count.finding) === null)];
}

/** The finding a report opens on: the first raised one any id has, else every id. */
export function openingFinding(counts: readonly InventoryCount[]): string | null {
  return counts.find((count) => count.raised && count.count > 0)?.finding ?? null;
}

/** Where an inventory stands, as a listing shows it at a glance. */
export type InventoryStanding = "running" | "failed" | "notBuilt" | "raised" | "clear" | "undeclared";

export interface InventoryStandingVisual {
  icon: LucideIcon;
  label: string;
  tone: string;
  hint: string;
  spin?: boolean;
}

export const STANDING_VISUALS: Record<InventoryStanding, InventoryStandingVisual> = {
  running: { icon: Loader2, label: "Running", tone: "text-info", hint: "A build or reconcile is reading now; the counts are those of the last reconcile.", spin: true },
  failed: {
    icon: CircleX, label: "Failed", tone: "text-destructive",
    hint: "Its newest run failed and changed nothing: the counts are those of the last reconcile that completed.",
  },
  notBuilt: { icon: CircleDashed, label: "Not built", tone: "text-muted-foreground", hint: "No build has read it in this partition yet." },
  raised: { icon: CircleAlert, label: "Raised", tone: "text-warning", hint: "Its last reconcile found ids where the ledgers and OSDU disagree." },
  clear: { icon: CircleCheck, label: "Clear", tone: "text-success", hint: "Its last reconcile found the ledgers and OSDU in agreement." },
  undeclared: {
    icon: Unlink, label: "No longer declared", tone: "text-muted-foreground",
    hint: "Its flow no longer declares it, so no build reads it again; it keeps what its last build found.",
  },
};

/** Where an inventory stands: what its newest run is doing first, since that is what needs a look. */
export function standingOf(inventory: Inventory | undefined, declared = true): InventoryStanding {
  if (!declared) {
    return "undeclared";
  }

  if (inventory === undefined) {
    return "notBuilt";
  }

  const latest = inventory.latest;
  if (latest?.status === "running") {
    return "running";
  }

  if (latest?.status === "failed" && latest.inventoryRunId !== inventory.lastReconcileRunId) {
    return "failed";
  }

  if (inventory.lastBuiltUtc === undefined && inventory.lastReconcileRunId === undefined) {
    return "notBuilt";
  }

  return (inventory.raised ?? 0) > 0 ? "raised" : "clear";
}

/** What a run read and changed, in a line, with how many ids it raised unless `withRaised` leaves that to a column of its own. */
export function runOutcome(run: InventoryRun, withRaised = true): string {
  const read = run.operation === "build"
    ? `listed ${counted(run.listed, "id")}: ${run.added.toLocaleString("en-US")} new, ${run.changed.toLocaleString("en-US")} changed, ${run.gone.toLocaleString("en-US")} gone, ${run.returned.toLocaleString("en-US")} served again`
    : `checked ${counted(run.missingChecked, "expected id")} in storage`;
  return run.raised === undefined || !withRaised ? read : `${read}; ${run.raised.toLocaleString("en-US")} raised`;
}

/** A count and its noun, plural when it is not one. */
export function counted(count: number, noun: string): string {
  return `${count.toLocaleString("en-US")} ${noun}${count === 1 ? "" : "s"}`;
}

/** How the owners were known, in words. */
export function ownersSourceText(source: string | undefined): string {
  switch (source) {
    case "declared":
      return "named by the flow";
    case "inferred":
      return "inferred from the ids a ledger claims";
    case "none":
      return "none known: the flow names none and no ledger claims an id";
    default:
      return "not known until a reconcile";
  }
}

/** The run payload that builds or reconciles only the inventories named; none takes every one. */
export function inventoriesPayload(names: readonly string[]): Record<string, unknown> {
  return names.length === 0 ? {} : { inventories: [...names] };
}

/** The questions an inventory's report answers, a tab each: which ids have which finding, what its runs did, and what was removed. */
export type InventoryView = "ids" | "duplicates" | "runs" | "removals";

/** What the address names as the finding in view: one finding, every id (`all`), or none, which opens on the first raised. */
export type FindingPick = string | null;

/** The value the address carries for every id. */
export const EVERY_ID = "all";

/** How the page's address names an inventory: its partition and its number. */
export interface InventoryRef {
  partition: string;
  inventoryId: number;
}

/** The address of an inventory's report, opened on a finding when one is named, with an id open in its panel when one is named. */
export function inventoryRoute(ref: InventoryRef, finding?: string | null, id?: string | null): string {
  const params = new URLSearchParams({ partition: ref.partition, inventory: String(ref.inventoryId) });
  if (finding !== undefined && finding !== null) {
    params.set("finding", finding);
  }

  if (id !== undefined && id !== null) {
    params.set("id", id);
  }

  return `/delivery/inventories?${params.toString()}`;
}

/** The longest OSDU id the page reads from its address or sends to a lookup, as the control plane takes it. */
export const MAX_ID_LENGTH = 1024;

/** A part of a file name: letters, digits, dots, dashes and underscores, anything else a dash, as the control plane names it. */
function safe(part: string): string {
  const cleaned = part.replace(/[^A-Za-z0-9._-]/g, "-");
  return cleaned === "" ? "inventory" : cleaned;
}

/**
 * Downloads an inventory's ids of one finding (every id with none) as CSV, only those a selection holds when one is named, the
 * file named as the control plane names it.
 */
export async function downloadInventory(inventory: Inventory, finding: string | null, selection?: number): Promise<void> {
  const text = await inventoryApi.exportCsv(inventory.partition, inventory.inventoryId, finding ?? undefined, selection);
  const url = URL.createObjectURL(new Blob([text], { type: "text/csv;charset=utf-8" }));
  const anchor = document.createElement("a");
  anchor.href = url;
  const parts = selection === undefined
    ? [inventory.flowName, inventory.partition, inventory.name, finding ?? "all"]
    : [inventory.flowName, inventory.partition, inventory.name, "selection", String(selection), finding ?? "all"];
  anchor.download = `${parts.map(safe).join("-")}.csv`;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}
