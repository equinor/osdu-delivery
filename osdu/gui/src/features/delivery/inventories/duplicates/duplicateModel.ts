import { useQuery } from "@tanstack/react-query";
import {
  CircleCheck, CircleDashed, CircleHelp, CircleMinus, CircleSlash, Hand, Pin, Trash2, type LucideIcon,
} from "lucide-react";
import {
  MAX_DUPLICATE_KEY, inventoryApi, type DuplicateKeyProperty, type DuplicateOutcome, type DuplicateRole, type Inventory, type InventoryDuplicateCheck,
} from "../../../../api/inventories";
import { fieldLabel } from "../../explorer/explorerModel";
import type { InventoryRef } from "../inventoryFormat";
import type { InventorySelectionState } from "../inventorySelection";

// Finding the duplicate records of an inventory (osdu/docs/reference/flow/inventory-duplicates.md): the key an
// operator builds lives in the page's address while it is built, a check opened with it is followed while its run downloads
// the key's properties and while a pick reads its groups' copies, and its groups are read a page at a time once it downloaded.

/** How often a check under way is read again: often enough that its progress moves, rarely enough that a long one costs little. */
const UNDER_WAY_MS = 1500;

/** How often a check that ended is read again, so its counts follow an operator's choices and a build since shows it stale. */
const SETTLED_MS = 30_000;

/** The value the address carries for every group. */
export const EVERY_GROUP = "all";

/** The checks a tab's history lists. */
export const CHECKS_LISTED = 20;

/** How many records one search of a download returns, one storage read of a pick reads, and one search of a pick asks about. */
export const DOWNLOAD_PAGE = 1000;
export const STORAGE_BATCH = 100;
export const REFERENCES_PER_SEARCH = 40;

/** The most copies a group holds that a pick reads; a group holding more is too broad to be one record written more than once. */
export const MAX_COPIES_PICKED = 100;

/** What a key is, as the mark beside its name says it wherever the key is shown. */
export const KEY_HINT = [
  "Two records are copies of one another when they hold equal values at every property of the key: text exactly as written, case and spaces included; a number as a number, however it is written; a list as the values it holds, in any order. A property one record holds nothing at matches only a record holding nothing there too, and a record holding no value at any of them is passed over.",
  "Pick the properties in a record of the inventory: click a value to add its property, and click it again to take it out. A value of a list of values adds the list; a value inside the items of a list adds its property across every item. Pick what every copy of a record repeats (its name, the record it belongs to, its source's own id), not what each copy writes for itself (when it was written, by whom).",
  `A check downloads the key's properties of every id into a table of its own, through OSDU's search, ${DOWNLOAD_PAGE.toLocaleString("en-US")} ids a request, and groups them there. Any key of the properties it downloaded groups it again at once, without reading OSDU; a key naming another property downloads again. The search returns only what its index keeps of a record; a property no record holds a value at is named once the download ends.`,
].join("\n\n");

/**
 * How many of a check's groups a pick read: the groups it judged, less those too broad to read, which a grouping judges at
 * once (osdu/docs/reference/tables/inventory-duplicate-check.md, GroupsJudged).
 */
export function groupsRead(check: InventoryDuplicateCheck): number {
  const broad = (check.sizes ?? []).filter((size) => size.from > MAX_COPIES_PICKED).reduce((sum, size) => sum + size.groups, 0);
  return Math.max(0, check.groupsJudged - broad);
}

/** How many requests a number of records takes, so many to a request. */
export function requestsFor(records: number, perRequest: number): number {
  return records <= 0 ? 0 : Math.ceil(records / perRequest);
}

/** How an outcome is drawn and said: its glyph, the glyph's tone, its name, and what it means. */
export interface OutcomeVisual {
  icon: LucideIcon;
  label: string;
  tone: string;
  hint: string;
}

export const OUTCOME_VISUALS: Record<DuplicateOutcome, OutcomeVisual> = {
  review: {
    icon: CircleHelp, label: "For review", tone: "text-warning",
    hint: "The check would not pick the copy to keep: two copies hold as many values, the copy a ledger holds is the less complete, a copy picked to remove is named by another record, or the key is too broad. Nothing of it is removed until an operator chooses.",
  },
  picked: {
    icon: CircleCheck, label: "Picked", tone: "text-success",
    hint: "The check picked the copy to keep: the one holding the most values, or the copy a ledger holds when none holds more. The others the flow may remove are picked to remove.",
  },
  chosen: {
    icon: Hand, label: "Chosen", tone: "text-info",
    hint: "An operator chose the copy to keep. The others the flow may remove are picked to remove.",
  },
  settled: {
    icon: CircleSlash, label: "Settled", tone: "text-muted-foreground",
    hint: "Storage holds one copy of the key, or none, since the inventory's build: there is nothing left to choose between.",
  },
  pending: {
    icon: CircleDashed, label: "To pick", tone: "text-muted-foreground",
    hint: "The group's copies hold equal values at the key, as the check downloaded them, and storage has not been read for them yet: a pick reads them whole and picks the copy to keep.",
  },
};

/** How an outcome is drawn: its own visual, or a neutral one for an outcome this page does not know. */
export function outcomeVisual(outcome: string): OutcomeVisual {
  const known = (Object.keys(OUTCOME_VISUALS) as DuplicateOutcome[]).find((o) => o === outcome);
  return known === undefined
    ? { icon: CircleDashed, label: outcome, tone: "text-muted-foreground", hint: `The control plane reports '${outcome}', an outcome this page does not describe.` }
    : OUTCOME_VISUALS[known];
}

export const ROLE_VISUALS: Record<DuplicateRole, OutcomeVisual> = {
  keep: { icon: Pin, label: "Keep", tone: "text-success", hint: "The copy the group keeps." },
  remove: { icon: Trash2, label: "Remove", tone: "text-destructive", hint: "Picked to remove: it holds fewer values than the copy kept, and the flow may remove it." },
  leave: { icon: CircleMinus, label: "Leave", tone: "text-muted-foreground", hint: "Not kept, and it stays: the inventory may not remove it (another identity's, a ledger's, or a finding the flow does not remove)." },
  review: { icon: CircleHelp, label: "Review", tone: "text-warning", hint: "Its group is left for review: nothing of it is decided until an operator chooses." },
  gone: { icon: CircleSlash, label: "Gone", tone: "text-muted-foreground", hint: "Storage no longer holds it: there is nothing of it to keep or remove." },
};

/** How a role is drawn: its own visual, or a neutral one for a role this page does not know, or none yet. */
export function roleVisual(role: string | undefined): OutcomeVisual {
  const known = (Object.keys(ROLE_VISUALS) as DuplicateRole[]).find((r) => r === role);
  return known === undefined
    ? { icon: CircleDashed, label: role ?? "To pick", tone: "text-muted-foreground", hint: "Its group waits to be picked: a pick reads its copies from storage and gives each its role." }
    : ROLE_VISUALS[known];
}

/** The key in the page's address: its properties' paths, joined by commas. */
export function keyText(key: readonly DuplicateKeyProperty[]): string | null {
  return key.length === 0 ? null : key.map((p) => p.path).join(",");
}

/**
 * The key the page's address holds; what is not a key's property (a path a query cannot write, a property named twice) is left
 * out. An address written when a key named how each property compared (`path:compare`) reads its paths.
 */
export function keyOf(text: string | null): DuplicateKeyProperty[] {
  if (text === null || text.trim() === "") {
    return [];
  }

  const seen = new Set<string>();
  const key: DuplicateKeyProperty[] = [];
  for (const part of text.split(",")) {
    const trimmed = part.split(":")[0]?.trim() ?? "";
    if (/^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$/.test(trimmed) && !seen.has(trimmed) && key.length < MAX_DUPLICATE_KEY) {
      seen.add(trimmed);
      key.push({ path: trimmed });
    }
  }

  return key;
}

/** Whether two keys are one: the same properties in the same order. */
export function sameKey(a: readonly DuplicateKeyProperty[], b: readonly DuplicateKeyProperty[]): boolean {
  return keyText(a) === keyText(b);
}

/** The key as a sentence a reader reads: each property by its name, joined by "and". */
export function keySentence(key: readonly DuplicateKeyProperty[]): string {
  return key.map((p) => fieldLabel(p.path)).join(" and ");
}

/** Whether a check downloaded every property of a key, so grouping it by the key reads no OSDU. */
export function downloadedAll(check: InventoryDuplicateCheck | undefined, key: readonly DuplicateKeyProperty[]): boolean {
  return check !== undefined && check.table !== undefined && key.length > 0 && key.every((p) => check.attributes.includes(p.path));
}

/** What a property of a key cannot be, in the words the control plane refuses it with; null when it can be one. */
export function keyPathProblem(path: string, key: readonly DuplicateKeyProperty[]): string | null {
  const root = path.split(".")[0];
  if (root === "id" || root === "version") {
    return `Every record holds its own ${root}, so no two share it.`;
  }

  if (key.some((p) => p.path === path)) {
    return "The key compares it already.";
  }

  return key.length >= MAX_DUPLICATE_KEY ? `A key compares at most ${MAX_DUPLICATE_KEY} properties.` : null;
}

/** The key with a property added after the others, or why the key cannot take it. */
export function withProperty(key: readonly DuplicateKeyProperty[], path: string): { key: DuplicateKeyProperty[] } | { problem: string } {
  const problem = keyPathProblem(path, key);
  return problem === null ? { key: [...key, { path }] } : { problem };
}

/**
 * Where a check stands, as the tab draws it: its download waiting for its run (`queued`) or under way (`keys`); a pick asked
 * for or under way over the groups it downloaded (`picking`); downloaded, against the inventory's last build or an earlier one
 * (`stale`); failed, which holds no group; or expired.
 */
export type CheckPhase = "queued" | "keys" | "picking" | "completed" | "stale" | "failed" | "expired";

export function phaseOf(check: InventoryDuplicateCheck): CheckPhase {
  switch (check.status) {
    case "queued":
    case "running":
      return check.phase === "copies" ? "picking" : check.status === "queued" ? "queued" : "keys";
    case "completed":
      return check.stale ? "stale" : "completed";
    case "expired":
      return "expired";
    default:
      return "failed";
  }
}

/** Whether a check has yet to end. */
export function isUnderWay(check: InventoryDuplicateCheck | undefined): boolean {
  return check?.status === "queued" || check?.status === "running";
}

/** One check of the inventory, followed while it runs. */
export function useDuplicateCheck(inventory: InventoryRef, checkId: number | null) {
  return useQuery({
    queryKey: ["delivery", "inventories", "duplicates", "check", inventory.partition, inventory.inventoryId, checkId],
    queryFn: () => inventoryApi.duplicateCheck(inventory.partition, inventory.inventoryId, checkId!),
    enabled: checkId !== null,
    refetchInterval: (query) => (isUnderWay(query.state.data) ? UNDER_WAY_MS : SETTLED_MS),
  });
}

/** The inventory's checks, newest first: the tab's history, and the check it opens on when the address names none. */
export function useDuplicateChecks(inventory: InventoryRef) {
  return useQuery({
    queryKey: ["delivery", "inventories", "duplicates", "checks", inventory.partition, inventory.inventoryId],
    queryFn: () => inventoryApi.duplicateChecks(inventory.partition, inventory.inventoryId, CHECKS_LISTED),
    refetchInterval: (query) => ((query.state.data ?? []).some(isUnderWay) ? UNDER_WAY_MS * 4 : SETTLED_MS * 2),
  });
}

/** Downloads every copy of every group of a check as CSV, the file named as the control plane names it. */
export async function downloadDuplicates(inventory: Inventory, checkId: number): Promise<void> {
  const text = await inventoryApi.exportDuplicates(inventory.partition, inventory.inventoryId, checkId);
  const url = URL.createObjectURL(new Blob([text], { type: "text/csv;charset=utf-8" }));
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = `${[inventory.flowName, inventory.partition, inventory.name, "duplicates", String(checkId)].map((part) => part.replace(/[^A-Za-z0-9._-]/g, "-") || "inventory").join("-")}.csv`;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}

/** The sentence a selection made of a duplicate check's picks reads as, wherever a selection's sentence is said. */
export function duplicateSelectionSentence(state: InventorySelectionState): string {
  const selection = state.selection;
  if (selection?.duplicateCheckId === undefined) {
    return "the copies a duplicate check picked to remove";
  }

  const key = selection.duplicateKey === undefined || selection.duplicateKey.length === 0 ? "" : ` by ${keySentence(selection.duplicateKey)}`;
  return `the copies duplicate check ${selection.duplicateCheckId} picked to remove${key}`;
}
