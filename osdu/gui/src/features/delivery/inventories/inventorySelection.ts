import { useCallback, useMemo } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ExplorerFilter } from "../../../api/explorer";
import { inventoryApi, type InventoryCount, type InventorySelection } from "../../../api/inventories";
import type { SearchTermView } from "../../../api/searchTerms";
import { filtersText, filterSentence } from "../explorer/explorerModel";
import { termOf, useSearchTerms } from "../explorer/explorerTerms";
import type { InventoryRef } from "./inventoryFormat";

// Selecting an inventory's ids by what OSDU holds (osdu/docs/reference/flow/inventory-selections.md, Selecting ids by what OSDU holds):
// the conditions in the page's address open a selection, the match made of them already or a new one the control plane queues
// as a run of the flow, and the page follows it until it ends. Its ids are then the grid's, counted by finding on the tabs.

/** How often a match under way is read again: often enough that its progress moves, rarely enough that a long one costs little. */
const UNDER_WAY_MS = 1500;

/** How often a match that ended is read again, so its counts follow a removal and a build since shows it stale. */
const SETTLED_MS = 30_000;

/**
 * Where the page's conditions stand: none asked; the match being asked for; matching (queued or reading OSDU); completed;
 * stale (completed against a build the inventory has since replaced); failed; expired; or refused (the control plane would
 * not open it: a condition it cannot ask, an inventory never built, no flow declaring it).
 */
export type SelectionPhase = "none" | "asking" | "matching" | "completed" | "stale" | "failed" | "expired" | "refused";

export interface InventorySelectionState {
  phase: SelectionPhase;
  /** The selection the conditions opened, as last read. */
  selection?: InventorySelection;
  /** Why the match could not be asked for, when the phase is refused. */
  problem?: unknown;
  /** Reads OSDU again for the same conditions: a new match, whatever the one before came to. */
  matchAgain: () => void;
  /** A new match is being asked for. */
  matchingAgain: boolean;
}

/** Whether a selection's match has yet to end. */
export function isUnderWay(selection: InventorySelection | undefined): boolean {
  return selection?.status === "queued" || selection?.status === "running";
}

/** The phase a selection read stands in. */
function phaseOf(selection: InventorySelection): SelectionPhase {
  switch (selection.status) {
    case "queued":
    case "running":
      return "matching";
    case "completed":
      return selection.stale ? "stale" : "completed";
    case "expired":
      return "expired";
    default:
      return "failed";
  }
}

/**
 * The selection the conditions open in the inventory, followed while its match runs; none while there are no conditions. The
 * same conditions open the same match until the inventory is built again or the operator asks to match again, so a link or a
 * refresh lands on what was matched rather than reading OSDU again.
 */
export function useInventorySelection(inventory: InventoryRef, conditions: readonly ExplorerFilter[]): InventorySelectionState {
  const queryClient = useQueryClient();
  const asked = conditions.length === 0 ? null : filtersText([...conditions]);
  // Kept apart from the inventories' own keys: a page refreshing what it shows of the inventory (after a removal, say) reads
  // the selection again, and must never ask for a new match the operator did not ask for.
  const openedKey = useMemo(
    () => ["delivery", "inventory-selection-opened", inventory.partition, inventory.inventoryId, asked] as const,
    [inventory.partition, inventory.inventoryId, asked]);
  const opened = useQuery({
    queryKey: openedKey,
    queryFn: () => inventoryApi.select(inventory.partition, inventory.inventoryId, { filters: [...conditions] }),
    enabled: asked !== null,
    // The match the conditions opened stands until a new one is asked for: the selection itself is what is followed.
    staleTime: Infinity,
    gcTime: 10 * 60_000,
    retry: false,
    refetchOnWindowFocus: false,
  });
  const selectionId = asked === null ? undefined : opened.data?.selectionId;
  const followed = useQuery({
    queryKey: ["delivery", "inventories", "selection", inventory.partition, inventory.inventoryId, selectionId ?? null],
    queryFn: () => inventoryApi.selection(inventory.partition, inventory.inventoryId, selectionId!),
    enabled: selectionId !== undefined,
    placeholderData: opened.data?.selectionId === selectionId ? opened.data : undefined,
    refetchInterval: (query) => (isUnderWay(query.state.data) ? UNDER_WAY_MS : SETTLED_MS),
  });
  const again = useMutation({
    mutationFn: () => inventoryApi.select(inventory.partition, inventory.inventoryId, { filters: [...conditions], refresh: true }),
    onSuccess: (fresh) => queryClient.setQueryData(openedKey, fresh),
  });
  const { mutate } = again;
  const matchAgain = useCallback(() => mutate(), [mutate]);

  if (asked === null) {
    return { phase: "none", matchAgain, matchingAgain: false };
  }

  if (opened.isError) {
    return { phase: "refused", problem: opened.error, matchAgain, matchingAgain: again.isPending };
  }

  const selection = followed.data ?? opened.data;
  if (selection === undefined || again.isPending) {
    return { phase: "asking", selection, matchAgain, matchingAgain: again.isPending };
  }

  return { phase: phaseOf(selection), selection, matchAgain, matchingAgain: false };
}

/**
 * A selection named by its number (a duplicate check's picks, made a selection), followed as one a match fills is; none while
 * no number is given. Its way to read OSDU again is the one given (a duplicate check's, on its own tab), since no conditions
 * made it.
 */
export function useInventorySelectionById(inventory: InventoryRef, selectionId: number | null, again: () => void): InventorySelectionState {
  const followed = useQuery({
    queryKey: ["delivery", "inventories", "selection", inventory.partition, inventory.inventoryId, selectionId],
    queryFn: () => inventoryApi.selection(inventory.partition, inventory.inventoryId, selectionId!),
    enabled: selectionId !== null,
    refetchInterval: (query) => (isUnderWay(query.state.data) ? UNDER_WAY_MS : SETTLED_MS),
    retry: false,
  });

  if (selectionId === null) {
    return { phase: "none", matchAgain: again, matchingAgain: false };
  }

  if (followed.isError) {
    return { phase: "refused", problem: followed.error, matchAgain: again, matchingAgain: false };
  }

  const selection = followed.data;
  return selection === undefined
    ? { phase: "asking", matchAgain: again, matchingAgain: false }
    : { phase: phaseOf(selection), selection, matchAgain: again, matchingAgain: false };
}

/** A selection's ids counted by finding, every finding the inventory counts, zero for one it holds none of; null before it completed. */
export function matchedCounts(selection: InventorySelection | undefined): ReadonlyMap<string, number> | null {
  if (selection?.counts === undefined) {
    return null;
  }

  return new Map(selection.counts.map((count: InventoryCount) => [count.finding, count.count]));
}

/** The conditions as a sentence a reader reads: each as its chip says it, a source column by its name among `terms`, joined by "and". */
export function conditionSentence(conditions: readonly ExplorerFilter[] | undefined, terms: SearchTermView[] | undefined): string {
  return (conditions ?? []).map((condition) => filterSentence(condition, termOf(condition, terms)?.name)).join(" and ");
}

/**
 * The source columns conditions of the inventory's kind may name, read once for the kind and kept as the explorer keeps them;
 * read only when `needed`, since most conditions name a property.
 */
export function useInventoryTerms(kind: string | undefined, needed: boolean): SearchTermView[] | undefined {
  return useSearchTerms(kind, needed, true).data?.terms;
}

/** The conditions as a sentence, a source column by its name. */
export function useConditionSentence(kind: string | undefined, conditions: readonly ExplorerFilter[] | undefined): string {
  const terms = useInventoryTerms(kind, (conditions ?? []).some((condition) => condition.term !== undefined));
  return useMemo(() => conditionSentence(conditions, terms), [conditions, terms]);
}
