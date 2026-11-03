import { useCallback, useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { keepPreviousData, useInfiniteQuery, useQueryClient } from "@tanstack/react-query";
import { CircleCheck, CircleSlash, CircleX, RotateCcw, SkipForward, type LucideIcon } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { DataTable, type Column } from "@/components/DataTable";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import { useOwnedPanel } from "@/layout/workbench/useOwnedPanel";
import { cn } from "@/lib/utils";
import {
  GONE_FINDING, INVENTORY_REMOVAL_OUTCOMES, RESTORE_SCOPE, inventoryApi, type Inventory, type InventoryRemoval, type InventoryRemovalItem,
  type InventoryRemovalOutcome, type InventoryRemovalPolicy,
} from "../../../api/inventories";
import { RecordName } from "../RecordName";
import { RunRef } from "../DeliveryRefs";
import { RemovalDialog, type InventoryChangeQueued } from "../RemovalDialog";
import { ProblemView } from "../TemplateSheet";
import { GridFooter } from "../dimensions/DimensionGrid";
import { ExplainTip } from "@/components/ExplainTip";
import { FindingGlyph, RunStatusPill } from "./InventoryBadges";
import { counted, findingVisual } from "./inventoryFormat";
import { conditionSentence, useInventoryTerms } from "./inventorySelection";
import type { SearchTermView } from "../../../api/searchTerms";

/** The workbench's bottom panel content the removals raise: a removal's number after this prefix. */
const PANEL = "inventory-removal:";

/** The ids one page of a removal's outcomes reads. */
const PAGE = 200;

/** How often a running removal's outcomes are read again, so what it reached shows as it lands. */
const RUNNING_REFRESH_MS = 5000;

/** How an outcome is drawn: its glyph in its tone and its name. */
const OUTCOME_VISUALS: Record<InventoryRemovalOutcome, { icon: LucideIcon; tone: string; label: string }> = {
  removed: { icon: CircleCheck, tone: "text-success", label: "Removed" },
  restored: { icon: RotateCcw, tone: "text-success", label: "Restored" },
  gone: { icon: CircleSlash, tone: "text-muted-foreground", label: "Already gone" },
  skipped: { icon: SkipForward, tone: "text-warning", label: "Skipped" },
  failed: { icon: CircleX, tone: "text-destructive", label: "Failed" },
};

/** The outcomes a removal comes to, and those a restore does: a restore removes nothing, and a removal restores nothing. */
const REMOVAL_OUTCOMES = INVENTORY_REMOVAL_OUTCOMES.filter((outcome) => outcome !== "restored");
const RESTORE_OUTCOMES = INVENTORY_REMOVAL_OUTCOMES.filter((outcome) => outcome !== "removed");

function isRestore(removal: InventoryRemoval): boolean {
  return removal.scope === RESTORE_SCOPE;
}

/** How an outcome is drawn; a restore's gone id is one storage holds no record of, purged, so nothing could bring it back. */
function outcomeVisual(outcome: string, restore = false) {
  const visual = OUTCOME_VISUALS[outcome as InventoryRemovalOutcome] ?? { icon: CircleSlash, tone: "text-muted-foreground", label: outcome };
  return restore && outcome === "gone" ? { ...visual, label: "Gone for good" } : visual;
}

/** How many ids of a removal (or a restore) came to each outcome. */
function tallyOf(removal: InventoryRemoval): Record<InventoryRemovalOutcome, number> {
  return { removed: removal.removed, restored: removal.restored ?? 0, gone: removal.gone, skipped: removal.skipped, failed: removal.failed };
}

/** A removal's outcomes in a line: each that any id came to, glyph and count, its name on hover; a quiet dash for none yet. */
function Tally({ removal, testId }: { removal: InventoryRemoval; testId: string }) {
  const tally = tallyOf(removal);
  const reached = INVENTORY_REMOVAL_OUTCOMES.filter((outcome) => tally[outcome] > 0);
  if (reached.length === 0) {
    return <span className="text-[12px] text-muted-foreground" data-testid={testId}>none reached yet</span>;
  }

  return (
    <span className="inline-flex items-center gap-x-2.5 whitespace-nowrap text-[12px]" data-testid={testId}>
      {reached.map((outcome) => {
        const visual = outcomeVisual(outcome, isRestore(removal));
        const Icon = visual.icon;
        return (
          <span key={outcome} className="inline-flex items-center gap-1" title={visual.label} data-outcome={outcome}>
            <Icon className={cn("size-3.5", visual.tone)} aria-hidden />
            <span className="font-mono tabular-nums">{tally[outcome].toLocaleString("en-US")}</span>
            <span className="text-muted-foreground">{visual.label.toLowerCase()}</span>
          </span>
        );
      })}
    </span>
  );
}

/**
 * What a removal was asked to do, in a line: how many ids of which finding (picked, every one, those matching the conditions
 * of a selection, named by their sentence, or, for a restore, those an earlier removal soft deleted), and how much of each
 * record, or that they were brought back in OSDU.
 */
function asked(removal: InventoryRemoval, terms: SearchTermView[] | undefined): string {
  const label = findingVisual(removal.finding).label.toLowerCase();
  const how = removal.scope === "everything"
    ? "purged"
    : removal.scope === "inventory" ? "removed from the inventory" : isRestore(removal) ? "restored in OSDU" : "soft deleted";
  if (removal.restoresRemovalId !== undefined) {
    return `${counted(removal.requested, "id")} that removal ${removal.restoresRemovalId} soft deleted, ${how}`;
  }

  const which = removal.selection !== undefined
    ? ` matching ${conditionSentence(removal.selection.conditions, terms)}`
    : removal.namesIds ? " picked" : ", every one";
  return `${removal.requested.toLocaleString("en-US")} ${label}${which}, ${how}`;
}

/** What the selection a removal took its ids from was, as its explanation says it: the conditions, the query and the match. */
function selectionText(removal: InventoryRemoval, terms: SearchTermView[] | undefined): string | null {
  const selection = removal.selection;
  if (selection === undefined) {
    return null;
  }

  return [
    `The ids that held ${conditionSentence(selection.conditions, terms)} in OSDU, as selection ${selection.selectionId} matched them: ${selection.matched.toLocaleString("en-US")} id(s) of every finding.`,
    selection.query === undefined ? null : `Query: ${selection.query}`,
    selection.status === "expired" ? "The selection is older than a week: its conditions and counts are kept, its ids were retired." : null,
  ].filter((line): line is string => line !== null).join("\n\n");
}

/**
 * Whether a removal keeps a row of every id it reached. One from the inventory alone keeps only the ids it kept, with why: a
 * row of each id it removed would keep what it removed, so it keeps how many.
 */
function keepsEveryId(removal: InventoryRemoval): boolean {
  return removal.scope !== "inventory";
}

/**
 * What one removal (or restore) did to each id, in the workbench's bottom panel: the removal in a line, its outcomes to pick
 * from with their counts, and its ids of the outcome picked (every one at first), each with the version it found and why, a
 * page at a time. A removal from the inventory alone lists the ids it kept, and says how many it removed. A soft delete that
 * removed ids can be undone from here where the flow declares a removal: what it removed is restored in OSDU, as a run of the
 * flow, and the restores that did so already are named.
 */
function RemovalItemsPanel({ inventory, removal, restore, restoredBy }: {
  inventory: Inventory;
  removal: InventoryRemoval;
  /** What the flow lets an operator remove and restore; absent where it declares no removal, or the reader cannot run it. */
  restore?: InventoryRemovalPolicy;
  /** The restores that brought back what this removal removed, newest first. */
  restoredBy: readonly InventoryRemoval[];
}) {
  const [outcome, setOutcome] = useState<InventoryRemovalOutcome | null>(null);
  const [restoring, setRestoring] = useState(false);
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const restores = isRestore(removal);
  const undoable = restore !== undefined && removal.scope === "record" && removal.status !== "running" && removal.removed > 0;
  const terms = useInventoryTerms(inventory.kind, removal.selection?.conditions.some((condition) => condition.term !== undefined) ?? false);
  const pages = useInfiniteQuery({
    queryKey: ["delivery", "inventories", "removal-items", inventory.partition, removal.inventoryRemovalId, outcome],
    queryFn: ({ pageParam }) => inventoryApi.removalItems(inventory.partition, removal.inventoryRemovalId, { outcome: outcome ?? undefined, after: pageParam, limit: PAGE }),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: (last) => last.next,
    placeholderData: keepPreviousData,
    refetchInterval: removal.status === "running" ? RUNNING_REFRESH_MS : false,
  });
  const rows = useMemo(() => pages.data?.pages.flatMap((page) => page.items), [pages.data]);
  const tally = tallyOf(removal);
  const keeps = keepsEveryId(removal);
  // A removal from the inventory alone keeps only the ids it skipped, so its ids are one list.
  const listed: readonly InventoryRemovalOutcome[] = keeps ? restores ? RESTORE_OUTCOMES : REMOVAL_OUTCOMES : ["skipped"];
  const every = listed.reduce((sum, o) => sum + tally[o], 0);
  const total = outcome === null ? every : tally[outcome];
  const more = () => {
    if (pages.hasNextPage && !pages.isFetchingNextPage) {
      void pages.fetchNextPage();
    }
  };

  const columns: Column<InventoryRemovalItem>[] = [
    {
      id: "id",
      header: "Id",
      fill: true,
      floor: 200,
      render: (row) => <RecordName id={row.targetId} copy className="text-[12.5px]" testId="inventory-removal-item-id" />,
    },
    {
      id: "outcome",
      header: "Outcome",
      render: (row) => {
        const visual = outcomeVisual(row.outcome, restores);
        const Icon = visual.icon;
        return (
          <span className="inline-flex items-center gap-1 whitespace-nowrap text-[12px]" data-testid="inventory-removal-item-outcome" data-outcome={row.outcome}>
            <Icon className={cn("size-3.5", visual.tone)} aria-hidden />
            {visual.label.toLowerCase()}
          </span>
        );
      },
    },
    {
      id: "version",
      header: "Version",
      align: "right",
      render: (row) => (row.version === undefined
        ? <span className="text-[12px] text-muted-foreground">-</span>
        : <span className="font-mono text-[12px] tabular-nums">{row.version}</span>),
    },
    { id: "reason", header: "Why", fill: true, floor: 220, render: (row) => <TruncatedText text={row.reason ?? ""} maxWidth={900} className="text-[12px]" /> },
    { id: "recorded", header: "When", render: (row) => <span className="whitespace-nowrap text-[12px]"><RelativeTime value={row.recordedUtc} absolute={false} /></span> },
  ];

  const tab = (value: InventoryRemovalOutcome | null, label: string, count: number) => (
    <button
      key={value ?? "all"}
      type="button"
      onClick={() => setOutcome(value)}
      aria-pressed={outcome === value}
      className={cn(
        "relative inline-flex h-8 shrink-0 items-center gap-1.5 whitespace-nowrap rounded-sm px-2.5 text-[12.5px] outline-none transition-colors focus-visible:bg-accent/60",
        "after:absolute after:inset-x-2 after:bottom-0 after:h-0.5 after:rounded-full",
        outcome === value ? "text-foreground after:bg-primary" : "text-muted-foreground after:bg-transparent hover:text-foreground",
      )}
      data-testid={`inventory-removal-outcome-${value ?? "all"}`}
    >
      {label}
      <span className="rounded-full bg-muted px-1.5 font-mono text-[11px] tabular-nums leading-4 text-muted-foreground">{count.toLocaleString("en-US")}</span>
    </button>
  );

  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="inventory-removal-panel" data-removal={removal.inventoryRemovalId}>
      <div className="flex shrink-0 flex-wrap items-center gap-x-3 gap-y-1 border-b border-border px-3 py-1.5 text-[12.5px]">
        <span className="inline-flex min-w-0 items-center gap-1.5">
          {restores ? <RotateCcw className="size-3.5 shrink-0 text-success" aria-hidden /> : <FindingGlyph finding={removal.finding} />}
          {selectionText(removal, terms) === null
            ? <span className="font-medium">{asked(removal, terms)}</span>
            : (
              <ExplainTip title="Selected by" text={selectionText(removal, terms)!} testId="inventory-removal-panel-selection">
                <span className="cursor-help font-medium">{asked(removal, terms)}</span>
              </ExplainTip>
            )}
        </span>
        <span className="text-muted-foreground">
          {"by "}
          <span className="font-mono text-[12px] text-foreground">{removal.actor}</span>
          {", "}
          <RelativeTime value={removal.startedUtc} absolute={false} />
        </span>
        <RunStatusPill status={removal.status} testId="inventory-removal-panel-status" />
        {removal.runId !== undefined && <RunRef runId={removal.runId} />}
        {removal.error !== undefined && (
          <ExplainTip title="Why it stopped" text={removal.error} testId="inventory-removal-panel-error">
            <span className="min-w-0 max-w-[32rem] truncate text-destructive">{removal.error}</span>
          </ExplainTip>
        )}
        {!keeps && (
          <span className="text-muted-foreground" data-testid="inventory-removal-panel-unkept">
            {`${removal.removed.toLocaleString("en-US")} removed from the inventory: it keeps how many, not which`}
          </span>
        )}
        {restoredBy.length > 0 && (
          <span className="text-muted-foreground" data-testid="inventory-removal-panel-restored-by">
            {`Restored by ${restoredBy.map((r) => `restore ${r.inventoryRemovalId}`).join(", ")}`}
          </span>
        )}
        {undoable && (
          <Button
            variant="outline"
            size="sm"
            className="ml-auto h-7"
            onClick={() => setRestoring(true)}
            title="Bring back in OSDU every id this removal soft deleted, at the version it had: storage keeps a soft-deleted record and every version"
            data-testid="inventory-removal-restore"
          >
            <RotateCcw />
            Restore what it removed
          </Button>
        )}
      </div>
      <div className="flex shrink-0 items-center gap-0.5 border-b border-border px-1.5" role="toolbar" aria-label="Outcomes">
        {tab(null, keeps ? "Every id" : "Kept", every)}
        {keeps && listed.map((value) => tab(value, outcomeVisual(value, restores).label, tally[value]))}
      </div>
      <div className="min-h-0 flex-1 overflow-auto p-2">
        {pages.isError
          ? <ProblemView error={pages.error} testId="inventory-removal-items-error" />
          : (
            <DataTable
              columns={columns}
              rows={rows}
              rowKey={(row) => row.inventoryRemovalItemId}
              footer={(
                <GridFooter
                  shown={rows?.length ?? 0}
                  total={total}
                  noun="id"
                  hasMore={pages.hasNextPage}
                  loading={pages.isFetchingNextPage}
                  onMore={more}
                  testId="inventory-removal-items-footer"
                />
              )}
              emptyMessage={outcome === null
                ? keeps ? `The ${restores ? "restore" : "removal"} has reached no id yet.` : "The removal kept no id: every id it reached went from the inventory."
                : `No id came to ${outcomeVisual(outcome, restores).label.toLowerCase()}.`}
              data-testid="inventory-removal-items-table"
            />
          )}
      </div>
      {undoable && (
        <RemovalDialog
          open={restoring}
          onClose={() => setRestoring(false)}
          inventory={{ inventory, finding: GONE_FINDING, ids: null, count: removal.removed, policy: restore, restoresRemoval: removal.inventoryRemovalId }}
          onQueued={(queued: InventoryChangeQueued) => {
            toast.success(
              `Restore of ${counted(queued.expected, "id")} removal ${removal.inventoryRemovalId} removed, of ${queued.inventory}, queued as a run.`,
              { action: { label: "Open run", onClick: () => navigate(`/runs/${queued.runId}`) } });
            void queryClient.invalidateQueries({ queryKey: ["delivery", "inventories"] });
          }}
        />
      )}
    </div>
  );
}

/**
 * An inventory's removals and restores, newest first: what each was asked to remove (or bring back) and how much of each record,
 * where it stands and what it came to so far (its tallies grow a chunk at a time while it runs), who asked and when, and its
 * run. One opens in the workbench's bottom panel with what it did to each id, and why; a soft delete's panel restores what it
 * removed, where the flow declares a removal (`restore`).
 */
export function InventoryRemovals({ inventory, removals, error, restore }: {
  inventory: Inventory;
  removals: InventoryRemoval[] | undefined;
  error: unknown;
  /** What the flow lets an operator remove and restore; absent where it declares no removal, or the reader cannot run it. */
  restore?: InventoryRemovalPolicy;
}) {
  const { ownedId, show } = useOwnedPanel(PANEL);
  // The source columns a removal's conditions name, read once for the inventory's kind when any removal names one.
  const terms = useInventoryTerms(inventory.kind, (removals ?? []).some((r) => r.selection?.conditions.some((condition) => condition.term !== undefined) ?? false));
  const open = useCallback((removal: InventoryRemoval) => show(
    String(removal.inventoryRemovalId),
    `${isRestore(removal) ? "Restore" : "Removal"} · ${removal.inventoryRemovalId}`,
    <RemovalItemsPanel
      key={removal.inventoryRemovalId}
      inventory={inventory}
      removal={removal}
      restore={restore}
      restoredBy={(removals ?? []).filter((r) => r.restoresRemovalId === removal.inventoryRemovalId)}
    />,
  ), [show, inventory, restore, removals]);

  // The removal in the panel is raised again as the list is read again, so a running one's tallies and status keep up.
  const shown = ownedId === null ? undefined : removals?.find((removal) => String(removal.inventoryRemovalId) === ownedId);
  useEffect(() => {
    if (shown !== undefined) {
      open(shown);
    }
  }, [shown, open]);

  if (error !== null && error !== undefined) {
    return <ProblemView error={error} testId="inventory-removals-error" />;
  }

  const columns: Column<InventoryRemoval>[] = [
    { id: "removal", header: "Number", render: (row) => <span className="font-mono text-[12px] tabular-nums">{row.inventoryRemovalId}</span> },
    {
      id: "asked",
      header: "Asked",
      fill: true,
      floor: 220,
      render: (row) => (
        <span className="inline-flex min-w-0 items-center gap-1.5 text-[12.5px]" data-testid="inventory-removal-asked" data-selection={row.selection?.selectionId} data-scope={row.scope}>
          {isRestore(row) ? <RotateCcw className="size-3.5 shrink-0 text-success" aria-hidden /> : <FindingGlyph finding={row.finding} />}
          {selectionText(row, terms) === null
            ? <TruncatedText text={asked(row, terms)} maxWidth={420} />
            : (
              <ExplainTip title="Selected by" text={selectionText(row, terms)!} testId="inventory-removal-selection">
                <span className="min-w-0 max-w-[420px] cursor-help truncate">{asked(row, terms)}</span>
              </ExplainTip>
            )}
        </span>
      ),
    },
    {
      id: "status",
      header: "Status",
      render: (row) => (row.error === undefined
        ? <RunStatusPill status={row.status} testId="inventory-removal-status" />
        : (
          <ExplainTip title="Why it stopped" text={row.error} testId="inventory-removal-error">
            <span className="inline-flex"><RunStatusPill status={row.status} testId="inventory-removal-status" /></span>
          </ExplainTip>
        )),
    },
    { id: "tally", header: "Came to", render: (row) => <Tally removal={row} testId={`inventory-removal-tally-${row.inventoryRemovalId}`} /> },
    { id: "started", header: "Started", render: (row) => <span className="whitespace-nowrap text-[12px]"><RelativeTime value={row.startedUtc} absolute={false} /></span> },
    { id: "actor", header: "By", render: (row) => <TruncatedText text={row.actor} mono maxWidth={180} /> },
    { id: "run", header: "", align: "right", render: (row) => (row.runId === undefined ? null : <RunRef runId={row.runId} />) },
  ];

  return (
    <DataTable
      columns={columns}
      rows={removals}
      rowKey={(row) => row.inventoryRemovalId}
      onRowClick={open}
      rowSx={(row) => (ownedId === String(row.inventoryRemovalId) ? { backgroundColor: "var(--accent)" } : undefined)}
      emptyMessage="Nothing has been removed or restored through this inventory. Pick ids of a removable finding on the Ids tab to remove them from OSDU, or gone ids to restore them there or remove them from the inventory."
      data-testid="inventory-removals-table"
    />
  );
}
