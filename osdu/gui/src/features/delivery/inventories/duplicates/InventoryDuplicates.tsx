import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, CircleX, Download, History, Info, Layers, ListChecks, Loader2, PencilLine, Pin, RefreshCw, Search, TriangleAlert } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { Progress } from "@/components/ui/progress";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { EmptyState } from "@/components/EmptyState";
import { ExplainTip } from "@/components/ExplainTip";
import { RelativeTime } from "@/components/RelativeTime";
import { isApiError } from "@/api/client";
import { cn } from "@/lib/utils";
import {
  DUPLICATE_OUTCOMES, inventoryApi, type DuplicateKeyProperty, type DuplicateSize, type Inventory, type InventoryDetail, type InventoryDuplicateCheck,
  type InventorySelection,
} from "../../../../api/inventories";
import { RunRef } from "../../DeliveryRefs";
import { ProblemView } from "../../TemplateSheet";
import { fieldLabel } from "../../explorer/explorerModel";
import { counted, type InventoryRef } from "../inventoryFormat";
import { DuplicateGroupsGrid } from "./DuplicateGroupsGrid";
import { DuplicateKeyBuilder } from "./DuplicateKeyBuilder";
import { DuplicateKeySummary } from "./DuplicateKeyParts";
import {
  DOWNLOAD_PAGE, EVERY_GROUP, MAX_COPIES_PICKED, REFERENCES_PER_SEARCH, STORAGE_BATCH, downloadDuplicates, downloadedAll, groupsRead, isUnderWay, keyOf,
  keySentence, keyText, outcomeVisual, phaseOf, requestsFor, sameKey, useDuplicateCheck, useDuplicateChecks,
} from "./duplicateModel";

/** What a control plane refusal says, in its own words. */
function said(error: unknown): string {
  return isApiError(error) ? error.detail ?? error.title : String(error);
}

/** How many copies, as a line says it. */
function copiesText(count: number): string {
  return `${count.toLocaleString("en-US")} ${count === 1 ? "copy" : "copies"}`;
}

/** How many searches, as a line says it. */
function searchesText(count: number): string {
  return `${count.toLocaleString("en-US")} ${count === 1 ? "search" : "searches"}`;
}

/** How many of a check's groups came out one way. */
function groupsOf(check: InventoryDuplicateCheck, outcome: string): number {
  return check.outcomes?.find((o) => o.outcome === outcome)?.groups ?? 0;
}

/** Whether a size of groups is too broad to pick from. */
function isBroad(size: DuplicateSize): boolean {
  return size.from > MAX_COPIES_PICKED;
}

/** A size of groups, as its count is followed: pairs, from and to, or more than a pick reads. */
function sizeText(size: DuplicateSize): string {
  if (size.from === 2 && size.to === 2) {
    return size.groups === 1 ? "pair" : "pairs";
  }

  return size.to === undefined ? `of over ${(size.from - 1).toLocaleString("en-US")}` : `of ${size.from.toLocaleString("en-US")} to ${size.to.toLocaleString("en-US")}`;
}

/** What a download reads, before it is asked for: the requests it takes, whatever the key finds. */
function downloadCost(served: number, properties: number, flowName: string): string {
  const searches = requestsFor(served, DOWNLOAD_PAGE);
  return `Downloads ${properties === 1 ? "the key's property" : `the key's ${properties.toLocaleString("en-US")} properties`} of the ${counted(served, "id")} the inventory serves into a table of the check's own: about ${searchesText(searches)} through ${flowName}'s own source, ${DOWNLOAD_PAGE.toLocaleString("en-US")} ids each, however many duplicates the key finds. No copy is read until a pick is asked for.`;
}

/** What a pick reads, before it is asked for: the storage reads of the copies waiting, and the searches for the records naming them. */
function pickCost(check: InventoryDuplicateCheck, flowName: string): string {
  const reads = requestsFor(check.pendingCopies, STORAGE_BATCH);
  const searches = requestsFor(check.pendingCopies, REFERENCES_PER_SEARCH);
  return [
    `Reads the ${copiesText(check.pendingCopies)} of the ${counted(check.pendingGroups, "group")} waiting whole from storage, through ${flowName}'s own source: about ${counted(reads, "request")}, ${STORAGE_BATCH.toLocaleString("en-US")} copies each. Then asks the search which records name them: about ${searchesText(searches)}, ${REFERENCES_PER_SEARCH.toLocaleString("en-US")} copies each, and more where a copy is named. Then picks the copy each group keeps.`,
    "Nothing in OSDU changes. A pick that stops is asked again, and reads the groups it did not.",
  ].join("\n\n");
}

/** What grouping a check again lets go of its groups; null when it lets go of nothing an operator would miss. */
function letGoText(check: InventoryDuplicateCheck): string | null {
  const chosen = groupsOf(check, "chosen");
  const parts: string[] = [];
  const read = groupsRead(check);
  if (read > 0) {
    parts.push(`what the pick read of ${counted(read, "group")} is let go, and a pick reads the new groups' copies again`);
  }

  if (chosen > 0) {
    parts.push(`the copy an operator chose to keep in ${counted(chosen, "group")} is let go`);
  }

  if (check.selectionId !== undefined) {
    parts.push(`the check no longer names selection ${check.selectionId}, which keeps the ids it holds`);
  }

  return parts.length === 0 ? null : `The groups form again by the new key: ${parts.join("; ")}.`;
}

/** Everything a check has to say, as the mark beside it holds it: what it downloaded and found, how, who asked and when. */
function checkExplanation(inventory: Inventory, check: InventoryDuplicateCheck): string {
  const lines = [
    `Key: ${keySentence(check.key)}${check.keyedBy === undefined ? "" : `, set by ${check.keyedBy}`}.`,
    `${counted(check.keyed, "id")} of the ${counted(check.candidates, "id")} the inventory serves hold a value at the key; ${counted(check.unkeyed, "id")} hold none of it and were passed over.`,
  ];
  if (check.table !== undefined) {
    lines.push(`It downloaded ${check.attributes.map(fieldLabel).join(", ")} into ${check.table}: any key of them groups it again at once, without reading OSDU.`);
  }

  lines.push(`${counted(check.groups, "group")} of ids hold equal values at the key, ${counted(check.copies, "id")} in all: ${groupsOf(check, "picked").toLocaleString("en-US")} picked, ${groupsOf(check, "chosen").toLocaleString("en-US")} chosen, ${groupsOf(check, "review").toLocaleString("en-US")} for review, ${groupsOf(check, "settled").toLocaleString("en-US")} settled, ${check.pendingGroups.toLocaleString("en-US")} to pick.`);
  lines.push(`${copiesText(check.toRemove)} picked to remove: the copies holding fewer values than the copy their group keeps, of the findings the flow may remove.`);
  if (check.changed > 0) {
    lines.push(`${counted(check.changed, "id")} changed since the inventory's last build: OSDU's index holds another version than the inventory listed, so a removal leaves them for the next build.`);
  }

  if (check.outside > 0) {
    lines.push(`${counted(check.outside, "record")} of the kind that the inventory holds no row of were passed over: written since its last build, or outside its query.`);
  }

  lines.push(`${counted(check.requests, "request")} (searches and storage reads) through ${inventory.flowName}'s own source.`);
  lines.push(...check.notes);
  lines.push(`Check ${check.checkId}, asked by ${check.actor}${check.runId === undefined ? "" : `, run ${check.runId}`}.`);
  return lines.join("\n\n");
}

/** How far a check under way got, in a line: the ids it downloaded of those the inventory serves, or the groups a pick read. */
function progressOf(check: InventoryDuplicateCheck): { text: string; share: number } {
  const phase = phaseOf(check);
  if (phase === "queued") {
    return { text: "waiting for its run to start", share: 0 };
  }

  if (phase === "picking") {
    if (check.status === "queued") {
      return { text: "the pick waits for its run to start", share: 0 };
    }

    const read = groupsRead(check);
    const of = read + check.pendingGroups;
    return {
      text: `picking: ${read.toLocaleString("en-US")} of ${counted(of, "group")} read`,
      share: of === 0 ? 100 : Math.min(100, (read / of) * 100),
    };
  }

  return {
    text: `downloading: ${check.checked.toLocaleString("en-US")} of ${counted(check.candidates, "id")}`,
    share: check.candidates === 0 ? 0 : Math.min(100, (check.checked / check.candidates) * 100),
  };
}

/** The outcomes of a check as one line of tabs over its groups, each with its count, every group last. */
function OutcomeStrip({ check, selected, onSelect }: { check: InventoryDuplicateCheck; selected: string | null; onSelect: (outcome: string | null) => void }) {
  const tabs: { outcome: string | null; count: number }[] = [
    ...DUPLICATE_OUTCOMES.map((outcome) => ({ outcome, count: groupsOf(check, outcome) })).filter((tab) => tab.count > 0 || tab.outcome === "review"),
    { outcome: null, count: check.groups },
  ];
  return (
    <div className="flex min-w-0 items-center gap-0.5 overflow-hidden" role="tablist" aria-label="The groups by how they came out" data-testid="duplicate-outcomes">
      {tabs.map((tab) => {
        const isSelected = tab.outcome === selected;
        const visual = tab.outcome === null ? null : outcomeVisual(tab.outcome);
        const Icon = visual?.icon;
        return (
          <button
            key={tab.outcome ?? EVERY_GROUP}
            type="button"
            role="tab"
            aria-selected={isSelected}
            onClick={() => onSelect(tab.outcome)}
            title={visual?.hint ?? "Every group of ids that hold equal values at the key"}
            className={cn(
              "relative inline-flex h-9 shrink-0 items-center gap-1.5 whitespace-nowrap rounded-sm px-2.5 text-[12.5px] outline-none transition-colors",
              "focus-visible:bg-accent/60 after:absolute after:inset-x-2 after:bottom-0 after:h-0.5 after:rounded-full",
              isSelected ? "text-foreground after:bg-primary" : "text-muted-foreground after:bg-transparent hover:text-foreground",
            )}
            data-testid="duplicate-outcome-tab"
            data-outcome={tab.outcome ?? EVERY_GROUP}
          >
            {Icon !== undefined && <Icon className={cn("size-3.5", tab.count > 0 ? visual!.tone : "text-muted-foreground/60")} aria-hidden />}
            <span>{visual?.label ?? "Every group"}</span>
            <span className={cn("rounded-full px-1.5 font-mono text-[11px] tabular-nums leading-4", isSelected ? "bg-primary/10 text-foreground" : "bg-muted text-muted-foreground")}>
              {tab.count.toLocaleString("en-US")}
            </span>
          </button>
        );
      })}
    </div>
  );
}

/**
 * How large a check's groups are, as one quiet line of counts by size, what each means behind it. A size too broad to pick
 * from is marked, since a key that broad is not one record written more than once: it is the sign to narrow the key.
 */
function SizeStrip({ check }: { check: InventoryDuplicateCheck }) {
  const sizes = (check.sizes ?? []).filter((size) => size.groups > 0);
  if (sizes.length === 0) {
    return null;
  }

  const broad = sizes.filter(isBroad).reduce((sum, size) => sum + size.groups, 0);
  const text = [
    ...sizes.map((size) => `${counted(size.groups, "group")} ${size.from === 2 && size.to === 2 ? "of two copies" : `${sizeText(size)} copies`}, ${copiesText(size.copies)} in all.`),
    broad > 0
      ? `A group of more than ${MAX_COPIES_PICKED.toLocaleString("en-US")} copies is too broad to be one record written more than once: a pick does not read it, and it is left for review. Add a property to the key to narrow it; the key line adds any property the check downloaded at once.`
      : `Every group holds at most ${MAX_COPIES_PICKED.toLocaleString("en-US")} copies, so a pick reads each of them.`,
  ].join("\n\n");
  return (
    <ExplainTip title="The groups by size" text={text} testId="duplicate-sizes">
      <button type="button" className="inline-flex min-w-0 items-center gap-1.5 rounded-sm outline-none hover:text-foreground focus-visible:text-foreground" data-testid="duplicate-sizes">
        {sizes.map((size, index) => (
          <span key={size.from} className={cn("inline-flex items-center gap-1 whitespace-nowrap", isBroad(size) && "text-foreground")} data-testid="duplicate-size" data-from={size.from}>
            {index > 0 && <span className="text-muted-foreground/50" aria-hidden>·</span>}
            {isBroad(size) && <TriangleAlert className="size-3.5 text-warning" aria-hidden />}
            <span className="font-mono tabular-nums text-foreground">{size.groups.toLocaleString("en-US")}</span>
            {sizeText(size)}
          </span>
        ))}
      </button>
    </ExplainTip>
  );
}

/** The inventory's checks, newest first, to open one: its key, where it stands and what it found. */
function HistoryMenu({ checks, current, onOpen }: { checks: readonly InventoryDuplicateCheck[]; current: number | null; onOpen: (checkId: number) => void }) {
  if (checks.length === 0) {
    return null;
  }

  return (
    <DropdownMenu>
      <Tooltip>
        <TooltipTrigger asChild>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon-sm" className="size-7 text-muted-foreground" aria-label="Earlier checks" data-testid="duplicate-history">
              <History />
            </Button>
          </DropdownMenuTrigger>
        </TooltipTrigger>
        <TooltipContent>Earlier checks of this inventory</TooltipContent>
      </Tooltip>
      <DropdownMenuContent align="end" className="w-[30rem] max-w-[calc(100vw-2rem)]">
        <DropdownMenuLabel className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Checks, newest first</DropdownMenuLabel>
        <DropdownMenuSeparator />
        {checks.map((check) => (
          <DropdownMenuItem
            key={check.checkId}
            onSelect={() => onOpen(check.checkId)}
            className={cn("flex-col items-start gap-0.5", check.checkId === current && "bg-accent/60")}
            data-testid="duplicate-history-item"
          >
            <span className="flex w-full items-center gap-2 text-[12.5px]">
              <span className="font-mono text-[11.5px] tabular-nums text-muted-foreground">{`#${check.checkId}`}</span>
              <span className="min-w-0 flex-1 truncate">{keySentence(check.key)}</span>
              <span className="shrink-0 text-[11.5px] text-muted-foreground"><RelativeTime value={check.completedUtc ?? check.createdUtc} absolute={false} /></span>
            </span>
            <span className="text-[11.5px] text-muted-foreground">
              {check.status === "completed"
                ? `${counted(check.groups, "group")}, ${check.pendingGroups > 0 ? `${check.pendingGroups.toLocaleString("en-US")} to pick` : `${check.review.toLocaleString("en-US")} for review, ${copiesText(check.toRemove)} to remove`}${check.stale ? "; the inventory was built again since" : ""}`
                : check.status === "running" || check.status === "queued"
                  ? check.phase === "copies" ? "picking" : "downloading"
                  : check.status}
            </span>
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/**
 * The duplicate records of an inventory (osdu/docs/reference/flow/inventory-duplicates.md), on the report's Duplicates tab.
 * It is in one of two places at a time. Building the key: one of the inventory's records as JSON beside the key, where a click
 * on a value adds its property (DuplicateKeyBuilder), and the key's action: Group by this key when the check in view
 * downloaded every property of it, which groups its table again in SQL without reading OSDU, or Find duplicates, which opens a
 * check downloading the key's properties of every id, run by the inventory flow through its own source; each says what it
 * reads before it is asked for. The tab opens there while the inventory has no check, and goes back there to pick other
 * properties of the key.
 *
 * Reading a check: its key on one line among the properties it downloaded, a click on one adding it to the key or taking it
 * out, which groups the check again at once (asking first when that lets go of a pick or an operator's choice); where its run
 * stands (downloading, picking, how far it got) or what it found (behind the mark beside it), its groups by size, a size too
 * broad to pick from marked; and what can be done of it: pick the copies to keep, a run reading the copies of the groups
 * waiting whole from storage, its cost said first; select the copies picked to remove, which takes the operator to them on the
 * Ids tab, where a removal takes them; download every group as CSV; download again. Under them the outcomes, each with its
 * count, over the groups of the outcome picked, which show their copies as downloaded until a pick reads them. The check in
 * view, the key being built, the record it is built in and the outcome live in the page's address.
 */
export function InventoryDuplicates({
  inventory, detail, checkId, onCheck, draftText, onDraft, sampleId, onSampleId, outcome, onOutcome, openId, onOpenId, canAct, onSelection,
}: {
  inventory: Inventory;
  detail: InventoryDetail;
  /** The check the address names; null opens on the newest. */
  checkId: number | null;
  /** Shows a check, its own key in view (none being built). */
  onCheck: (checkId: number | null) => void;
  /** The key being built, as the address holds it (empty text for a key emptied while built); null takes the key of the check in view. */
  draftText: string | null;
  /** Keeps the key being built, an empty one included; null lets it go, back to the key of the check in view. */
  onDraft: (key: DuplicateKeyProperty[] | null) => void;
  /** The record the key is built in, as the address holds it; null shows the first id of the inventory OSDU serves. */
  sampleId: string | null;
  onSampleId: (id: string | null) => void;
  /** The outcome whose groups are listed (`all` for every group); null opens on those for review once picked, when there are any. */
  outcome: string | null;
  onOutcome: (outcome: string) => void;
  openId: string | null;
  onOpenId?: (id: string | null) => void;
  /** Whether the reader may ask for a check or a pick, choose the copy a group keeps and select the picks: the operate scope. */
  canAct: boolean;
  /** Takes the reader to a selection made of a check's picks. */
  onSelection: (selection: InventorySelection) => void;
}) {
  const reference: InventoryRef = { partition: inventory.partition, inventoryId: inventory.inventoryId };
  const queryClient = useQueryClient();
  const checks = useDuplicateChecks(reference);
  const shownId = checkId ?? checks.data?.[0]?.checkId ?? null;
  const read = useDuplicateCheck(reference, shownId);
  const check = read.data;
  const draft = useMemo(() => (draftText !== null ? keyOf(draftText) : check?.key ?? []), [draftText, check?.key]);
  // The key as last changed. The address the page draws the key from follows a change a render later, so each change builds
  // on this one rather than on the key last drawn, and clicks closer together than that add up.
  const asked = useRef<readonly DuplicateKeyProperty[]>(draft);
  useEffect(() => {
    asked.current = draft;
  }, [draft]);
  const changeDraft = useCallback((change: (current: readonly DuplicateKeyProperty[]) => readonly DuplicateKeyProperty[]) => {
    const next = change(asked.current);
    if (next !== asked.current) {
      asked.current = next;
      onDraft([...next]);
    }
  }, [onDraft]);
  // Whether the reader went back to the key of the check in view to change it; a key in the address that is not the check's
  // is being changed too, so a link to a key being built opens on it.
  const [editing, setEditing] = useState(false);
  // The key a regroup waits on the reader for, when it lets go of a pick or a choice.
  const [confirming, setConfirming] = useState<DuplicateKeyProperty[] | null>(null);

  // A check answered is the one in view: what was read of its groups is let go, since a check grouped again numbers new groups.
  const landed = (made: InventoryDuplicateCheck) => {
    queryClient.setQueryData(["delivery", "inventories", "duplicates", "check", inventory.partition, inventory.inventoryId, made.checkId], made);
    queryClient.removeQueries({ queryKey: ["delivery", "inventories", "duplicates", "groups", inventory.partition, inventory.inventoryId, made.checkId] });
    void queryClient.invalidateQueries({ queryKey: ["delivery", "inventories", "duplicates", "checks", inventory.partition, inventory.inventoryId] });
  };
  const open = useMutation({
    mutationFn: ({ key, refresh }: { key: readonly DuplicateKeyProperty[]; refresh: boolean }) =>
      inventoryApi.checkDuplicates(inventory.partition, inventory.inventoryId, { key: [...key], refresh }),
    onSuccess: (made) => {
      landed(made);
      setEditing(false);
      setConfirming(null);
      onCheck(made.checkId);
    },
    onError: (error) => toast.error(`The duplicates could not be found: ${said(error)}`),
  });
  const pick = useMutation({
    mutationFn: (made: InventoryDuplicateCheck) => inventoryApi.pickDuplicates(inventory.partition, inventory.inventoryId, made.checkId),
    onSuccess: (made) => {
      queryClient.setQueryData(["delivery", "inventories", "duplicates", "check", inventory.partition, inventory.inventoryId, made.checkId], made);
      void queryClient.invalidateQueries({ queryKey: ["delivery", "inventories", "duplicates", "checks", inventory.partition, inventory.inventoryId] });
    },
    onError: (error) => toast.error(`No pick was asked for: ${said(error)}`),
  });
  const select = useMutation({
    mutationFn: (made: InventoryDuplicateCheck) => inventoryApi.selectDuplicates(inventory.partition, inventory.inventoryId, made.checkId),
    onSuccess: (selection) => {
      void queryClient.invalidateQueries({ queryKey: ["delivery", "inventories", "duplicates", "check", inventory.partition, inventory.inventoryId] });
      onSelection(selection);
    },
    onError: (error) => toast.error(`No selection was made: ${said(error)}`),
  });
  const [downloading, setDownloading] = useState(false);
  const download = async (made: InventoryDuplicateCheck) => {
    setDownloading(true);
    try {
      await downloadDuplicates(inventory, made.checkId);
      toast.success(`Downloaded the groups of check ${made.checkId}`);
    } catch (error) {
      toast.error(`The groups could not be downloaded: ${said(error)}`);
    } finally {
      setDownloading(false);
    }
  };

  const phase = check === undefined ? null : phaseOf(check);
  const downloadedCheck = phase === "completed" || phase === "stale" || phase === "picking";
  const matches = check !== undefined && sameKey(check.key, draft);
  const runningSame = matches && isUnderWay(check);
  const again = matches && (phase === "completed" || phase === "stale" || phase === "failed" || phase === "expired");
  // A key of what the check downloaded groups it again at once: only a check downloaded against the inventory's last build,
  // with no pick under way, groups again; the control plane downloads afresh for a check it built again since.
  const grouping = !matches && phase === "completed" && downloadedAll(check, draft);
  const blocked = !matches && check !== undefined && downloadedAll(check, draft) && isUnderWay(check)
    ? `Check ${check.checkId} is ${phase === "picking" ? "picking the copies of its groups" : "downloading"}: group it by another key once that ends.`
    : null;
  const shownOutcome = outcome === EVERY_GROUP ? null : outcome ?? (check !== undefined && check.pickedUtc !== undefined && check.review > 0 ? "review" : null);
  const total = check === undefined ? null : shownOutcome === null ? check.groups : groupsOf(check, shownOutcome);
  const removalOff = detail.removal === undefined
    ? `${inventory.flowName} declares no removal, so the copies a check picks can be selected and looked at here, and not removed.`
    : null;
  const served = detail.counts.filter((count) => count.finding !== "missing" && count.finding !== "gone").reduce((sum, count) => sum + count.count, 0);
  const building = shownId === null
    ? !checks.isPending && !checks.isError
    : check !== undefined && (editing || (draftText !== null && !matches));
  const letGo = check === undefined ? null : letGoText(check);

  // Groups the check by another key of what it downloaded, asking first when that lets go of a pick or a choice.
  const regroup = (key: DuplicateKeyProperty[]) => {
    if (letGo !== null) {
      setConfirming(key);
    } else {
      open.mutate({ key, refresh: false });
    }
  };
  const toggle = (path: string) => {
    if (check === undefined) {
      return;
    }

    const next = check.key.some((p) => p.path === path) ? check.key.filter((p) => p.path !== path) : [...check.key, { path }];
    if (next.length > 0) {
      regroup(next);
    }
  };

  const history = (
    <HistoryMenu
      checks={checks.data ?? []}
      current={shownId}
      onOpen={(id) => {
        setEditing(false);
        onCheck(id);
      }}
    />
  );

  const confirm = check === undefined || confirming === null ? null : (
    <ConfirmDialog
      open
      title={`Group check ${check.checkId} by ${keySentence(confirming)}?`}
      message={`${letGo ?? ""} The check reads no OSDU to group again: it groups what it downloaded.`}
      confirmLabel="Group again"
      busy={open.isPending}
      onConfirm={() => open.mutate({ key: confirming, refresh: false })}
      onClose={() => setConfirming(null)}
    />
  );

  if (shownId === null && !building) {
    return (
      <div className="overflow-hidden rounded-lg border border-border bg-card" data-testid="inventory-duplicates">
        {checks.isError
          ? <div className="p-3"><ProblemView error={checks.error} testId="duplicate-checks-error" /></div>
          : <div className="flex h-40 items-center justify-center text-[12.5px] text-muted-foreground"><Loader2 className="mr-2 size-4 animate-spin" />Reading the inventory&apos;s checks</div>}
      </div>
    );
  }

  if (building) {
    const fresh = draft.filter((p) => check === undefined || !check.attributes.includes(p.path)).length;
    const findTitle = !canAct
      ? "Finding duplicates reads OSDU with the flow's own credentials, which needs the operate scope"
      : draft.length === 0
        ? "Pick at least one property of the key first"
        : blocked ?? (grouping
          ? `Check ${check!.checkId} downloaded every property of this key: its groups form again at once, reading no OSDU.`
          : again
            ? `Download the key's properties again: a new check of the inventory as its last build left it. ${downloadCost(served, draft.length, inventory.flowName)}`
            : downloadCost(served, draft.length, inventory.flowName));
    const label = grouping ? "Group by this key" : again ? "Download again" : "Find duplicates";
    const leaving = check === undefined ? null : (
      <Button
        variant="ghost"
        size="sm"
        className="h-7 w-full justify-center gap-1 text-[12.5px] text-muted-foreground hover:text-foreground"
        onClick={() => {
          setEditing(false);
          onDraft(null);
        }}
        title={`Leave the key as check ${check.checkId} has it, and go back to what it found`}
        data-testid="duplicate-key-back"
      >
        <ArrowLeft className="size-3.5" />
        {`Back to check ${check.checkId}`}
      </Button>
    );
    let hint: ReactNode = null;
    if (draft.length > 0) {
      if (runningSame && check !== undefined) {
        hint = `Check ${check.checkId} is ${phase === "picking" ? "picking the copies of" : "downloading"} this key now.`;
      } else if (blocked !== null) {
        hint = blocked;
      } else if (matches && check !== undefined && !again) {
        hint = `Check ${check.checkId} groups by this key.`;
      } else if (grouping) {
        hint = `Reads no OSDU: check ${check!.checkId} downloaded every property of it.`;
      } else {
        const cost = `about ${searchesText(requestsFor(served, DOWNLOAD_PAGE))} for the ${counted(served, "id")} the inventory serves. No copy is read yet.`;
        hint = fresh > 0 && check?.table !== undefined && !again
          ? `Check ${check.checkId} did not download ${fresh === 1 ? "one property" : `${fresh.toLocaleString("en-US")} properties`} of this key, so it is downloaded again: ${cost}`
          : `Downloads the key: ${cost}`;
      }
    }

    return (
      <div className="flex min-w-0 flex-col" data-testid="inventory-duplicates" data-check={check?.checkId} data-building="true">
        <div className="overflow-hidden rounded-lg border border-border bg-card">
          <DuplicateKeyBuilder
            inventory={inventory}
            served={served}
            draft={draft}
            downloaded={check !== undefined && downloadedCheck && check.table !== undefined ? { checkId: check.checkId, paths: new Set(check.attributes) } : null}
            onChange={changeDraft}
            disabled={!canAct}
            sampleId={sampleId}
            onSampleId={onSampleId}
            actions={(
              <>
                <div className="flex items-center gap-1">
                  <Button
                    size="sm"
                    className="h-8 flex-1"
                    variant={again ? "outline" : "default"}
                    disabled={!canAct || draft.length === 0 || runningSame || blocked !== null || open.isPending}
                    onClick={() => (grouping ? regroup([...draft]) : open.mutate({ key: draft, refresh: again }))}
                    title={findTitle}
                    data-testid="duplicate-find"
                    data-action={grouping ? "group" : again ? "again" : "download"}
                  >
                    {open.isPending ? <Loader2 className="animate-spin" /> : grouping ? <Layers /> : again ? <RefreshCw /> : <Search />}
                    {label}
                  </Button>
                  {history}
                </div>
                {hint !== null && <p className="px-1 text-[11.5px] leading-snug text-muted-foreground" data-testid="duplicate-find-cost">{hint}</p>}
                {leaving}
              </>
            )}
          />
        </div>
        {confirm}
      </div>
    );
  }

  const edit = (
    <Button
      variant="ghost"
      size="sm"
      className="h-7 gap-1 px-2 text-[12.5px] text-muted-foreground hover:text-foreground"
      onClick={() => setEditing(true)}
      disabled={check === undefined}
      title="Pick the key's properties in a record of the inventory: a property the check did not download is downloaded with the rest"
      data-testid="duplicate-key-edit"
    >
      <PencilLine className="size-3.5" />
      Edit the key
    </Button>
  );
  const stale = phase === "stale";
  const reload = check === undefined || !canAct ? null : (
    <Tooltip>
      <TooltipTrigger asChild>
        <Button
          variant={stale ? "outline" : "ghost"}
          size={stale ? "sm" : "icon-sm"}
          className={stale ? "h-7" : "size-7 text-muted-foreground"}
          disabled={isUnderWay(check) || open.isPending}
          onClick={() => open.mutate({ key: check.key, refresh: true })}
          aria-label="Download again"
          data-testid="duplicate-find"
          data-action="again"
        >
          {open.isPending ? <Loader2 className="animate-spin" /> : <RefreshCw />}
          {stale && "Download again"}
        </Button>
      </TooltipTrigger>
      <TooltipContent className="max-w-80">
        {`${stale ? "The inventory was built again since this check. " : ""}Download the key's properties again: a new check of the inventory as its last build left it. ${downloadCost(served, Math.max(check.attributes.length, check.key.length), inventory.flowName)}`}
      </TooltipContent>
    </Tooltip>
  );

  let status: ReactNode = null;
  let body: ReactNode;
  if (read.isError) {
    body = <div className="p-3"><ProblemView error={read.error} testId="duplicate-check-error" /></div>;
  } else if (check === undefined) {
    body = <div className="flex h-40 items-center justify-center text-[12.5px] text-muted-foreground"><Loader2 className="mr-2 size-4 animate-spin" />Reading the check</div>;
  } else if (phase === "queued" || phase === "keys") {
    const progress = progressOf(check);
    status = (
      <span className="inline-flex min-w-0 items-center gap-1.5 text-[12.5px] text-muted-foreground" data-testid="duplicate-status" data-phase={phase}>
        <Loader2 className="size-3.5 shrink-0 animate-spin text-info" aria-hidden />
        <span className="text-foreground">{`Check ${check.checkId}`}</span>
        <span className="truncate">{`· ${progress.text}`}</span>
        {check.runId !== undefined && <RunRef runId={check.runId} />}
      </span>
    );
    body = (
      <EmptyState
        icon={<Download />}
        title="Downloading the key's properties"
        description={(
          <span className="flex flex-col items-center gap-2">
            <span>{`Downloading ${(check.attributes.length > 0 ? check.attributes : check.key.map((p) => p.path)).map(fieldLabel).join(", ")} of every id the inventory serves into a table of the check's own, through ${inventory.flowName}'s own source, ${DOWNLOAD_PAGE.toLocaleString("en-US")} ids a search; then grouping them by the key in SQL. No copy is read until a pick is asked for.`}</span>
            <Progress value={progress.share} className="h-1.5 w-64" aria-label="How far the download got" data-testid="duplicate-progress" />
            <span className="text-[12px]">{progress.text}</span>
          </span>
        )}
        data-testid="duplicate-running"
      />
    );
  } else if (phase === "failed") {
    status = (
      <span className="inline-flex min-w-0 items-center gap-1.5 text-[12.5px]" data-testid="duplicate-status" data-phase="failed">
        <CircleX className="size-3.5 shrink-0 text-destructive" aria-hidden />
        <ExplainTip title="Why the check failed" text={check.error ?? "The check ended without saying why."} testId="duplicate-status-error">
          <span className="cursor-help">{`Check ${check.checkId} failed`}</span>
        </ExplainTip>
        {check.runId !== undefined && <RunRef runId={check.runId} />}
      </span>
    );
    body = (
      <EmptyState
        icon={<CircleX />}
        title="The download failed"
        description={`${check.error ?? "The check ended without saying why."} A check that failed holds no group and no table.`}
        action={canAct ? <Button size="sm" onClick={() => open.mutate({ key: check.key, refresh: true })} disabled={open.isPending} data-testid="duplicate-retry">Try again</Button> : undefined}
        data-testid="duplicate-failed"
      />
    );
  } else if (phase === "expired") {
    status = <span className="text-[12.5px] text-muted-foreground" data-testid="duplicate-status" data-phase="expired">{`Check ${check.checkId} is older than a week`}</span>;
    body = (
      <EmptyState
        icon={<History />}
        title="This check is older than a week"
        description="Its groups and its table were retired with it; a removal that took its picks still names it. Download the key again to see which ids share it now."
        action={canAct ? <Button size="sm" onClick={() => open.mutate({ key: check.key, refresh: true })} disabled={open.isPending} data-testid="duplicate-retry">Download again</Button> : undefined}
        data-testid="duplicate-expired"
      />
    );
  } else {
    const picking = phase === "picking";
    const progress = picking ? progressOf(check) : null;
    // The copies picked to remove are counted on the line where no button to select them says it.
    const picked = (groupsRead(check) > 0 || check.pickedUtc !== undefined) && (!canAct || picking);
    const pickTitle = stale
      ? "The inventory was built again since this check: download again before picking"
      : pickCost(check, inventory.flowName);
    status = (
      <span className="flex min-w-0 flex-1 flex-wrap items-center gap-x-2 gap-y-0.5 text-[12.5px] text-muted-foreground" data-testid="duplicate-status" data-phase={phase}>
        <ExplainTip title={`Check ${check.checkId}`} text={checkExplanation(inventory, check)} testId="duplicate-status-about">
          <button type="button" className="inline-flex items-center gap-1 rounded-sm outline-none hover:text-foreground focus-visible:text-foreground" data-testid="duplicate-status-checked">
            <Info className="size-3.5" aria-hidden />
            {`Check ${check.checkId}, `}
            <RelativeTime value={check.keyedUtc ?? check.completedUtc ?? check.createdUtc} absolute={false} />
          </button>
        </ExplainTip>
        <span className="inline-flex min-w-0 items-center gap-1.5">
          <span data-testid="duplicate-status-groups">
            <span className="font-mono tabular-nums text-foreground">{check.groups.toLocaleString("en-US")}</span>
            {` ${check.groups === 1 ? "group" : "groups"} of `}
            <span className="font-mono tabular-nums text-foreground">{check.copies.toLocaleString("en-US")}</span>
            {(check.sizes ?? []).some((size) => size.groups > 0) ? " ids:" : " ids"}
          </span>
          <SizeStrip check={check} />
        </span>
        <span className="h-4 w-px bg-border" aria-hidden />
        {progress !== null && (
          <span className="inline-flex min-w-0 items-center gap-1.5" data-testid="duplicate-status-picking">
            <Loader2 className="size-3.5 shrink-0 animate-spin text-info" aria-hidden />
            <span className="truncate text-foreground">{progress.text}</span>
            <Progress value={progress.share} className="h-1.5 w-24" aria-label="How far the pick got" data-testid="duplicate-progress" />
            {check.runId !== undefined && <RunRef runId={check.runId} />}
          </span>
        )}
        {!picking && check.pendingGroups > 0 && check.error !== undefined && (
          <ExplainTip title="Why the pick stopped" text={`${check.error}\n\nThe groups it read keep what it found; a pick asked again reads the others.`} testId="duplicate-status-stopped">
            <span className="inline-flex cursor-help items-center gap-1 text-foreground" data-testid="duplicate-status-stopped">
              <TriangleAlert className="size-3.5 text-warning" aria-hidden />
              The pick stopped
            </span>
          </ExplainTip>
        )}
        {!picking && check.pendingGroups > 0 && (
          <span data-testid="duplicate-status-pending"><span className="font-mono tabular-nums text-foreground">{check.pendingGroups.toLocaleString("en-US")}</span> to pick</span>
        )}
        {check.review > 0 && (
          <span className="inline-flex items-center gap-1" data-testid="duplicate-status-review">
            {(() => { const Icon = outcomeVisual("review").icon; return <Icon className="size-3.5 text-warning" aria-hidden />; })()}
            <span className="font-mono tabular-nums text-foreground">{check.review.toLocaleString("en-US")}</span>
            for review
          </span>
        )}
        {picked && (
          <span data-testid="duplicate-status-remove"><span className="font-mono tabular-nums text-foreground">{check.toRemove.toLocaleString("en-US")}</span>{` ${check.toRemove === 1 ? "copy" : "copies"} to remove`}</span>
        )}
        {check.notes.length > 0 && (
          <ExplainTip title="What the check noted" text={check.notes.join("\n\n")} testId="duplicate-status-notes">
            <span className="inline-flex cursor-help items-center gap-1 text-warning" data-testid="duplicate-status-noted">
              <TriangleAlert className="size-3.5" aria-hidden />
              {counted(check.notes.length, "note")}
            </span>
          </ExplainTip>
        )}
        {stale && (
          <span className="inline-flex items-center gap-1 text-foreground" data-testid="duplicate-status-stale">
            <TriangleAlert className="size-3.5 text-warning" aria-hidden />
            The inventory was built again since this check
          </span>
        )}
        <span className="ml-auto flex shrink-0 items-center gap-1">
          {removalOff !== null && (
            <ExplainTip title="Removal is off" text={removalOff} testId="duplicate-removal-off">
              <span className="cursor-help text-[12px]">removal off</span>
            </ExplainTip>
          )}
          <Tooltip>
            <TooltipTrigger asChild>
              <Button variant="ghost" size="icon-sm" className="size-7 text-muted-foreground" disabled={downloading} onClick={() => void download(check)} aria-label="Download every group as CSV" data-testid="duplicate-export">
                {downloading ? <Loader2 className="animate-spin" /> : <Download />}
              </Button>
            </TooltipTrigger>
            <TooltipContent>Every copy of every group as CSV</TooltipContent>
          </Tooltip>
          {canAct && !picking && check.pendingGroups > 0 && (
            <ExplainTip title={stale ? "Download again first" : "What a pick reads"} text={pickTitle} testId="duplicate-pick-cost">
              <span className="inline-flex">
                <Button
                  size="sm"
                  className="h-7"
                  disabled={stale || pick.isPending}
                  onClick={() => pick.mutate(check)}
                  data-testid="duplicate-pick"
                >
                  {pick.isPending ? <Loader2 className="animate-spin" /> : <Pin />}
                  {groupsRead(check) > 0 ? "Pick the rest" : "Pick the copies to keep"}
                </Button>
              </span>
            </ExplainTip>
          )}
          {canAct && !picking && (check.pendingGroups === 0 || check.toRemove > 0) && (
            <Button
              size="sm"
              variant="outline"
              className="h-7"
              disabled={stale || check.toRemove === 0 || select.isPending}
              onClick={() => select.mutate(check)}
              title={stale
                ? "The inventory was built again since this check: download again before selecting what it picks"
                : check.toRemove === 0
                    ? "The check picked no copy to remove: choose the copy to keep in the groups for review"
                    : check.selectionCurrent
                      ? `Open selection ${check.selectionId}, made of these picks; the Ids tab removes what it holds`
                      : "Make the copies picked to remove a selection, and open it on the Ids tab, where a removal takes them, each checked again first"}
              data-testid="duplicate-select"
            >
              {select.isPending ? <Loader2 className="animate-spin" /> : <ListChecks />}
              {check.selectionCurrent && check.selectionId !== undefined
                ? `Open selection ${check.selectionId}`
                : `Select the ${check.toRemove.toLocaleString("en-US")} ${check.toRemove === 1 ? "copy" : "copies"} to remove`}
            </Button>
          )}
        </span>
      </span>
    );
    body = (
      <DuplicateGroupsGrid
        key={`${check.checkId}-${keyText(check.key) ?? ""}-${check.keyedUtc ?? ""}-${shownOutcome ?? EVERY_GROUP}`}
        inventory={inventory}
        check={check}
        outcome={shownOutcome}
        total={total}
        leading={<OutcomeStrip check={check} selected={shownOutcome} onSelect={(chosen) => onOutcome(chosen ?? EVERY_GROUP)} />}
        canChoose={canAct && !stale && !picking}
        live={picking}
        openId={openId}
        onOpenId={onOpenId}
      />
    );
  }

  const busy = check === undefined || !downloadedCheck
    ? null
    : phase === "picking"
      ? "The check is picking the copies of its groups: change the key once the pick ends."
      : stale
        ? "The inventory was built again since this check: download again to group what it holds now."
        : null;
  return (
    <div className="flex min-w-0 flex-col gap-2" data-testid="inventory-duplicates" data-check={check?.checkId}>
      <div className="overflow-hidden rounded-lg border border-border bg-card">
        <DuplicateKeySummary
          keyOfCheck={check?.key ?? []}
          attributes={check?.attributes ?? []}
          onToggle={canAct && check !== undefined && downloadedCheck && check.table !== undefined ? toggle : undefined}
          busy={busy}
          letGo={letGo}
          regrouping={open.isPending && open.variables?.refresh === false}
          trailing={(
            <>
              {edit}
              {reload}
              {history}
            </>
          )}
        />
        {status !== null && <div className="flex min-w-0 items-center gap-2 border-t border-border px-3 py-1.5">{status}</div>}
        {!downloadedCheck && <div className="border-t border-border">{body}</div>}
      </div>
      {downloadedCheck && body}
      {confirm}
    </div>
  );
}
