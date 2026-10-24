import { useQuery } from "@tanstack/react-query";
import {
  CircleCheck, CircleDashed, CircleHelp, CircleMinus, CircleSlash, Hand, Pin, Trash2, type LucideIcon,
} from "lucide-react";
import {
  DUPLICATE_COMPARISONS, MAX_DUPLICATE_KEY, inventoryApi, type DuplicateComparison, type DuplicateKeyProperty, type DuplicateOutcome, type DuplicateRole,
  type Inventory, type InventoryDuplicateCheck,
} from "../../../../api/inventories";
import { fieldLabel } from "../../explorer/explorerModel";
import type { InventoryRef } from "../inventoryFormat";
import type { InventorySelectionState } from "../inventorySelection";

// Finding the duplicate records of an inventory (osdu/docs/reference/flow/inventory-duplicates.md): the key an
// operator builds lives in the page's address while it is built, a check opened with it is followed while its run reads OSDU,
// and its groups are read a page at a time once it completed.

/** How often a check under way is read again: often enough that its progress moves, rarely enough that a long one costs little. */
const UNDER_WAY_MS = 1500;

/** How often a check that ended is read again, so its counts follow an operator's choices and a build since shows it stale. */
const SETTLED_MS = 30_000;

/** The value the address carries for every group. */
export const EVERY_GROUP = "all";

/** The checks a tab's history lists. */
export const CHECKS_LISTED = 20;

/** How a comparison is said, and what it does to a value. */
export const COMPARISON_VISUALS: Record<DuplicateComparison, { label: string; hint: string }> = {
  exact: {
    label: "Exact",
    hint: "The values as the records hold them: text exactly, case and spaces included; a number as a number, however it is written. Text and a number are never alike.",
  },
  ignoreCase: {
    label: "Ignore case",
    hint: "Text ignoring case, in Unicode's composed form: Wellbore A-1 and WELLBORE A-1 are alike. Spaces still count.",
  },
  loose: {
    label: "Loose",
    hint: "Text ignoring case, the spaces at either end and how many stand between words; empty text is no value; text that is a number is that number, so \"1520.50\" and 1520.5 are alike.",
  },
};

/** A comparison this page knows, or exact for one a newer control plane added. */
export function comparisonOf(compare: string): DuplicateComparison {
  return DUPLICATE_COMPARISONS.find((known) => known === compare) ?? "exact";
}

/**
 * The comparison a property is given when it is added to a key: loose for text, which copies written by different hands most
 * often spell differently, and exact for anything else.
 */
export function defaultComparison(index: string | undefined): DuplicateComparison {
  return index === "text" ? "loose" : "exact";
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
    icon: CircleDashed, label: "Not read", tone: "text-muted-foreground",
    hint: "The check has not read the group's copies yet.",
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
    ? { icon: CircleDashed, label: role ?? "Not read", tone: "text-muted-foreground", hint: "The check has not read this copy." }
    : ROLE_VISUALS[known];
}

/** The key in the page's address: each property's path and comparison, `path:compare`, joined by commas. */
export function keyText(key: readonly DuplicateKeyProperty[]): string | null {
  return key.length === 0 ? null : key.map((p) => `${p.path}:${comparisonOf(p.compare)}`).join(",");
}

/** The key the page's address holds; what is not a key's property (a path a query cannot write, a property named twice) is left out. */
export function keyOf(text: string | null): DuplicateKeyProperty[] {
  if (text === null || text.trim() === "") {
    return [];
  }

  const seen = new Set<string>();
  const key: DuplicateKeyProperty[] = [];
  for (const part of text.split(",")) {
    const [path, compare] = part.split(":");
    const trimmed = path?.trim() ?? "";
    if (/^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$/.test(trimmed) && !seen.has(trimmed) && key.length < MAX_DUPLICATE_KEY) {
      seen.add(trimmed);
      key.push({ path: trimmed, compare: comparisonOf(compare?.trim() ?? "exact") });
    }
  }

  return key;
}

/** Whether two keys are one: the same properties in the same order, each compared the same way. */
export function sameKey(a: readonly DuplicateKeyProperty[], b: readonly DuplicateKeyProperty[]): boolean {
  return keyText(a) === keyText(b);
}

/** The key as a sentence a reader reads: each property by its name and how it compares, joined by "and". */
export function keySentence(key: readonly DuplicateKeyProperty[]): string {
  return key.map((p) => `${fieldLabel(p.path)} (${COMPARISON_VISUALS[comparisonOf(p.compare)].label.toLowerCase()})`).join(" and ");
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

/** Where a check stands, as the tab draws it. */
export type CheckPhase = "queued" | "keys" | "copies" | "completed" | "stale" | "failed" | "expired";

export function phaseOf(check: InventoryDuplicateCheck): CheckPhase {
  switch (check.status) {
    case "queued":
      return "queued";
    case "running":
      return check.phase === "copies" ? "copies" : "keys";
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
