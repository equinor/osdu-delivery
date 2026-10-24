import { useCallback, useMemo, useState, type ReactNode } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Copy as CopiesIcon, CircleX, Download, History, Info, ListChecks, Loader2, RefreshCw, Search, TriangleAlert } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { Progress } from "@/components/ui/progress";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { EmptyState } from "@/components/EmptyState";
import { ExplainTip } from "@/components/ExplainTip";
import { RelativeTime } from "@/components/RelativeTime";
import { isApiError } from "@/api/client";
import { cn } from "@/lib/utils";
import {
  DUPLICATE_OUTCOMES, inventoryApi, type DuplicateKeyProperty, type Inventory, type InventoryDetail, type InventoryDuplicateCheck, type InventorySelection,
} from "../../../../api/inventories";
import type { InspectorField } from "../../OsduRecordInspector";
import { RunRef } from "../../DeliveryRefs";
import { ProblemView } from "../../TemplateSheet";
import { counted, type InventoryRef } from "../inventoryFormat";
import { DuplicateGroupsGrid } from "./DuplicateGroupsGrid";
import { DuplicateKeyEditor, DuplicateKeyFieldAction } from "./DuplicateKeyEditor";
import {
  EVERY_GROUP, downloadDuplicates, isUnderWay, keyOf, keySentence, outcomeVisual, phaseOf, sameKey, useDuplicateCheck, useDuplicateChecks,
} from "./duplicateModel";

/** What a control plane refusal says, in its own words. */
function said(error: unknown): string {
  return isApiError(error) ? error.detail ?? error.title : String(error);
}

/** How many copies, as a line says it. */
function copiesText(count: number): string {
  return `${count.toLocaleString("en-US")} ${count === 1 ? "copy" : "copies"}`;
}

/** How many of a check's groups came out one way. */
function groupsOf(check: InventoryDuplicateCheck, outcome: string): number {
  return check.outcomes?.find((o) => o.outcome === outcome)?.groups ?? 0;
}

/** Everything a check has to say, as the mark beside it holds it: what it read and found, how, who asked and when. */
function checkExplanation(inventory: Inventory, check: InventoryDuplicateCheck): string {
  const lines = [
    `Key: ${keySentence(check.key)}.`,
    `${counted(check.keyed, "id")} of the ${counted(check.candidates, "id")} the inventory serves hold a value at the key; ${counted(check.unkeyed, "id")} hold none of it and were passed over.`,
    `${counted(check.groups, "group")} of ids share a key, ${counted(check.copies, "id")} in all: ${groupsOf(check, "picked").toLocaleString("en-US")} picked, ${groupsOf(check, "chosen").toLocaleString("en-US")} chosen, ${groupsOf(check, "review").toLocaleString("en-US")} for review, ${groupsOf(check, "settled").toLocaleString("en-US")} settled.`,
    `${copiesText(check.toRemove)} picked to remove: the copies holding fewer values than the copy their group keeps, of the findings the flow may remove.`,
  ];
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

/** How far a check under way got, in a line: the keys it read of the ids the inventory serves, or the groups whose copies it read. */
function progressOf(check: InventoryDuplicateCheck): { text: string; share: number } {
  const phase = phaseOf(check);
  if (phase === "queued") {
    return { text: "waiting for its run to start", share: 0 };
  }

  if (phase === "copies") {
    return {
      text: `reading the copies of each group: ${check.groupsJudged.toLocaleString("en-US")} of ${counted(check.groups, "group")}`,
      share: check.groups === 0 ? 100 : Math.min(100, (check.groupsJudged / check.groups) * 100),
    };
  }

  return {
    text: `reading every id's key: ${check.checked.toLocaleString("en-US")} of ${counted(check.candidates, "id")}`,
    share: check.candidates === 0 ? 0 : Math.min(100, (check.checked / check.candidates) * 100),
  };
}

/** The outcomes of a completed check as one line of tabs over its groups, each with its count, every group last. */
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
            title={visual?.hint ?? "Every group of ids that share the key"}
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
                ? `${counted(check.groups, "group")}, ${check.review.toLocaleString("en-US")} for review, ${copiesText(check.toRemove)} to remove${check.stale ? "; the inventory was built again since" : ""}`
                : check.status}
            </span>
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/**
 * The duplicate records of an inventory (osdu/docs/reference/flow/inventory-duplicates.md), on the report's
 * Duplicates tab. Its first line builds the key: the properties two records must hold alike to be copies of one another,
 * each with how it compares, picked from the kind's properties or from the values of a record open in the bottom panel; and
 * Find duplicates, which opens a check of the key, run by the inventory flow through its own source. Its second line says
 * where the check in view stands (reading the keys, reading the copies, how far it got and its run; or what it found, behind
 * the mark beside it) and what can be done of it: select the copies picked to remove, which takes the operator to them on the
 * Ids tab, where a removal takes them; download every group as CSV; check again. Under them the outcomes, each with its count,
 * over the groups of the outcome picked. The check in view, the key being built and the outcome live in the page's address.
 */
export function InventoryDuplicates({ inventory, detail, checkId, onCheck, draftText, onDraft, outcome, onOutcome, openId, onOpenId, canAct, onSelection }: {
  inventory: Inventory;
  detail: InventoryDetail;
  /** The check the address names; null opens on the newest. */
  checkId: number | null;
  onCheck: (checkId: number | null) => void;
  /** The key being built, as the address holds it; null takes the key of the check in view. */
  draftText: string | null;
  onDraft: (key: DuplicateKeyProperty[]) => void;
  /** The outcome whose groups are listed (`all` for every group); null opens on those for review, when there are any. */
  outcome: string | null;
  onOutcome: (outcome: string) => void;
  openId: string | null;
  onOpenId?: (id: string | null) => void;
  /** Whether the reader may ask for a check, choose the copy a group keeps and select the picks: the operate scope. */
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

  const open = useMutation({
    mutationFn: (refresh: boolean) => inventoryApi.checkDuplicates(inventory.partition, inventory.inventoryId, { key: draft, refresh }),
    onSuccess: (made) => {
      queryClient.setQueryData(["delivery", "inventories", "duplicates", "check", inventory.partition, inventory.inventoryId, made.checkId], made);
      void queryClient.invalidateQueries({ queryKey: ["delivery", "inventories", "duplicates", "checks", inventory.partition, inventory.inventoryId] });
      onCheck(made.checkId);
    },
    onError: (error) => toast.error(`The check could not be asked for: ${said(error)}`),
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

  const fieldActions = useCallback(
    (field: InspectorField) => <DuplicateKeyFieldAction field={field} draft={draft} onDraft={onDraft} />,
    [draft, onDraft]);

  const phase = check === undefined ? null : phaseOf(check);
  const matches = check !== undefined && sameKey(check.key, draft);
  const runningSame = matches && isUnderWay(check);
  const again = matches && (phase === "completed" || phase === "stale" || phase === "failed" || phase === "expired");
  const shownOutcome = outcome === EVERY_GROUP ? null : outcome ?? (check !== undefined && check.review > 0 ? "review" : null);
  const total = check === undefined ? null : shownOutcome === null ? check.groups : groupsOf(check, shownOutcome);
  const removalOff = detail.removal === undefined
    ? `${inventory.flowName} declares no removal, so the copies a check picks can be selected and looked at here, and not removed.`
    : null;

  const find = (
    <>
      <Button
        size="sm"
        className="h-7"
        variant={again ? "outline" : "default"}
        disabled={!canAct || draft.length === 0 || runningSame || open.isPending}
        onClick={() => open.mutate(again)}
        title={!canAct
          ? "Asking for a check reads OSDU with the flow's own credentials, which needs the operate scope"
          : again ? "Read OSDU again for this key: a new check of the inventory as its last build left it" : "Find the ids that hold alike values at every property of the key"}
        data-testid="duplicate-find"
      >
        {open.isPending ? <Loader2 className="animate-spin" /> : again ? <RefreshCw /> : <Search />}
        {again ? "Check again" : "Find duplicates"}
      </Button>
      <HistoryMenu checks={checks.data ?? []} current={shownId} onOpen={(id) => { onCheck(id); onDraft([]); }} />
    </>
  );

  let status: ReactNode = null;
  let body: ReactNode;
  if (read.isError) {
    body = <div className="p-3"><ProblemView error={read.error} testId="duplicate-check-error" /></div>;
  } else if (shownId === null) {
    body = checks.isPending
      ? <div className="flex h-40 items-center justify-center text-[12.5px] text-muted-foreground"><Loader2 className="mr-2 size-4 animate-spin" />Reading the inventory's checks</div>
      : (
        <EmptyState
          icon={<CopiesIcon />}
          title={`Find the duplicate records of ${inventory.name}`}
          description="Build a key of the properties the copies of one record hold alike. Find duplicates reads it for every id of the inventory, groups the ids that share it, and keeps the most complete copy of each group; the others are picked to remove, for you to look at, change and select."
          data-testid="duplicate-empty"
        />
      );
  } else if (check === undefined) {
    body = <div className="flex h-40 items-center justify-center text-[12.5px] text-muted-foreground"><Loader2 className="mr-2 size-4 animate-spin" />Reading the check</div>;
  } else if (phase === "queued" || phase === "keys" || phase === "copies") {
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
        icon={<Search />}
        title="Finding the duplicates in OSDU"
        description={(
          <span className="flex flex-col items-center gap-2">
            <span>{`Reading ${keySentence(check.key)} for every id the inventory serves, through ${inventory.flowName}'s own source; then each group's copies, whole.`}</span>
            <Progress value={progress.share} className="h-1.5 w-64" aria-label="How far the check got" data-testid="duplicate-progress" />
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
        title="The check failed"
        description={check.error ?? "The check ended without saying why."}
        action={canAct ? <Button size="sm" onClick={() => { onDraft(check.key); open.mutate(true); }} disabled={open.isPending} data-testid="duplicate-retry">Try again</Button> : undefined}
        data-testid="duplicate-failed"
      />
    );
  } else if (phase === "expired") {
    status = <span className="text-[12.5px] text-muted-foreground" data-testid="duplicate-status" data-phase="expired">{`Check ${check.checkId} is older than a week`}</span>;
    body = (
      <EmptyState
        icon={<History />}
        title="This check is older than a week"
        description="Its groups were retired with it; a removal that took its picks still names it. Check the key again to see which ids share it now."
        action={canAct ? <Button size="sm" onClick={() => { onDraft(check.key); open.mutate(true); }} disabled={open.isPending} data-testid="duplicate-retry">Check again</Button> : undefined}
        data-testid="duplicate-expired"
      />
    );
  } else {
    const stale = phase === "stale";
    status = (
      <span className="flex min-w-0 flex-1 flex-wrap items-center gap-x-2 gap-y-0.5 text-[12.5px] text-muted-foreground" data-testid="duplicate-status" data-phase={phase}>
        <ExplainTip title={`Check ${check.checkId}`} text={checkExplanation(inventory, check)} testId="duplicate-status-about">
          <button type="button" className="inline-flex items-center gap-1 rounded-sm outline-none hover:text-foreground focus-visible:text-foreground" data-testid="duplicate-status-checked">
            <Info className="size-3.5" aria-hidden />
            {`Check ${check.checkId}, `}
            <RelativeTime value={check.completedUtc ?? check.createdUtc} absolute={false} />
          </button>
        </ExplainTip>
        <span><span className="font-mono tabular-nums text-foreground">{check.groups.toLocaleString("en-US")}</span>{` ${check.groups === 1 ? "group" : "groups"} of `}<span className="font-mono tabular-nums text-foreground">{check.copies.toLocaleString("en-US")}</span> ids</span>
        {check.review > 0 && (
          <span className="inline-flex items-center gap-1" data-testid="duplicate-status-review">
            {(() => { const Icon = outcomeVisual("review").icon; return <Icon className="size-3.5 text-warning" aria-hidden />; })()}
            <span className="font-mono tabular-nums text-foreground">{check.review.toLocaleString("en-US")}</span>
            for review
          </span>
        )}
        <span data-testid="duplicate-status-remove"><span className="font-mono tabular-nums text-foreground">{check.toRemove.toLocaleString("en-US")}</span>{` ${check.toRemove === 1 ? "copy" : "copies"} to remove`}</span>
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
          {canAct && (
            <Button
              size="sm"
              variant="outline"
              className="h-7"
              disabled={stale || check.toRemove === 0 || select.isPending}
              onClick={() => select.mutate(check)}
              title={stale
                ? "The inventory was built again since this check: check again before selecting what it picks"
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
        key={`${check.checkId}-${shownOutcome ?? EVERY_GROUP}`}
        inventory={inventory}
        check={check}
        outcome={shownOutcome}
        total={total}
        leading={<OutcomeStrip check={check} selected={shownOutcome} onSelect={(picked) => onOutcome(picked ?? EVERY_GROUP)} />}
        canChoose={canAct && !stale}
        openId={openId}
        onOpenId={onOpenId}
        fieldActions={fieldActions}
      />
    );
  }

  const grid = check !== undefined && (phase === "completed" || phase === "stale");
  return (
    <div className="flex min-w-0 flex-col gap-2" data-testid="inventory-duplicates" data-check={check?.checkId}>
      <div className="overflow-hidden rounded-lg border border-border bg-card">
        <DuplicateKeyEditor inventory={inventory} draft={draft} onDraft={onDraft} disabled={!canAct} trailing={find} />
        {status !== null && <div className="flex min-w-0 items-center gap-2 border-t border-border px-3 py-1.5">{status}</div>}
        {!grid && <div className="border-t border-border">{body}</div>}
      </div>
      {grid && body}
    </div>
  );
}
