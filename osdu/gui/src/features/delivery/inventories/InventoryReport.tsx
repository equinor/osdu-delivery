import { useEffect, useRef, useState, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ChevronRight, ChevronsUpDown, CircleX, ClipboardList, Download, Info, Loader2, Play, Trash2 } from "lucide-react";
import { toast } from "sonner";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { HoverCard, HoverCardContent, HoverCardTrigger } from "@/components/ui/hover-card";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { useAuth } from "@/auth/AuthContext";
import { CopyButton } from "@/components/CopyButton";
import { DataTable, type Column } from "@/components/DataTable";
import { EmptyState } from "@/components/EmptyState";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { TruncatedText } from "@/components/TruncatedText";
import { isApiError } from "@/api/client";
import { TriggerRunDialog } from "@/features/runs/TriggerRunDialog";
import { formatDurationSeconds, parseUtc } from "@/lib/time";
import { cn } from "@/lib/utils";
import type { ExplorerFilter } from "../../../api/explorer";
import {
  REMOVABLE_FINDINGS, inventoryApi, type DuplicateKeyProperty, type Inventory, type InventoryDetail, type InventoryRun, type InventorySelection,
} from "../../../api/inventories";
import { KindText } from "../KindText";
import { useActivePartition } from "../activePartition";
import { RunRef } from "../DeliveryRefs";
import { ProblemView } from "../TemplateSheet";
import { ExplainTip } from "@/components/ExplainTip";
import { ExplorerAddFilter } from "../explorer/ExplorerFilters";
import { sameFilter } from "../explorer/explorerModel";
import { RunStatusPill, StandingGlyph } from "./InventoryBadges";
import { InventoryFindingStrip } from "./InventoryFindingStrip";
import { InventoryLookupBox } from "./InventoryLookup";
import { InventoryRecordsGrid } from "./InventoryRecordsGrid";
import { InventoryRemovals } from "./InventoryRemovals";
import { InventoryConditionsBar } from "./InventorySelectionParts";
import { matchedCounts, useConditionSentence, useInventorySelection, useInventorySelectionById } from "./inventorySelection";
import { DuplicateSelectionBar } from "./duplicates/DuplicateSelectionBar";
import { duplicateSelectionSentence } from "./duplicates/duplicateModel";
import { InventoryDuplicates } from "./duplicates/InventoryDuplicates";
import {
  EVERY_ID, allCounts, counted, downloadInventory, findingVisual, inventoriesPayload, openingFinding, ownersSourceText, runOutcome, standingOf,
  type FindingPick, type InventoryRef, type InventoryView,
} from "./inventoryFormat";

/**
 * How often an inventory is read again while one of its runs is under way, so its outcome shows as it lands. Its counts are
 * read from every row it holds, and the rows change only while a run is going, so an inventory no run is working on is read
 * again only once a minute, to notice a run started elsewhere.
 */
const REFRESH_MS = 30000;

/** How often an inventory no run is working on is read again. */
const IDLE_REFRESH_MS = 60000;

/** The runs the Runs tab lists, and the removals the Removals tab lists. */
const RUNS_SHOWN = 50;

/** How often an inventory's removals are read while one runs, so its tallies grow as it goes; once a minute otherwise. */
const REMOVAL_REFRESH_MS = 5000;

/** The way back to every inventory, as the first words of the report. */
function InventoriesCrumb({ onBack }: { onBack: () => void }) {
  return (
    <>
      <button
        type="button"
        onClick={onBack}
        className="rounded-sm font-normal text-muted-foreground outline-none hover:text-foreground hover:underline focus-visible:underline"
        data-testid="inventories-back"
      >
        Inventories
      </button>
      <ChevronRight className="size-4 shrink-0 text-muted-foreground/60" />
    </>
  );
}

/** The other inventories of the partition, one click away from the one in view. */
function Switcher({ current, siblings, onOpen }: { current: InventoryRef; siblings: readonly Inventory[]; onOpen: (ref: InventoryRef) => void }) {
  if (siblings.filter((sibling) => sibling.inventoryId !== current.inventoryId || sibling.partition !== current.partition).length === 0) {
    return null;
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="icon" className="size-6 text-muted-foreground" aria-label="Open another inventory" data-testid="inventory-switcher">
          <ChevronsUpDown />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="max-h-80 min-w-72 overflow-y-auto">
        {siblings.map((sibling) => (
          <DropdownMenuItem
            key={`${sibling.partition}-${sibling.inventoryId}`}
            onSelect={() => onOpen({ partition: sibling.partition, inventoryId: sibling.inventoryId })}
            className={cn("gap-2", sibling.inventoryId === current.inventoryId && sibling.partition === current.partition && "bg-accent/60")}
            data-testid="inventory-switcher-item"
          >
            <StandingGlyph standing={standingOf(sibling)} />
            <span className="min-w-0 flex-1 truncate">{sibling.name}</span>
            <span className="shrink-0 truncate font-mono text-[11px] text-muted-foreground">{sibling.flowName}</span>
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/** A run in a line of facts: when it ended (or started, while it runs), what it did on hover, to copy. */
function RunFact({ label, run, at, testId }: { label: string; run: InventoryRun | undefined; at: string | undefined; testId: string }) {
  if (at === undefined && run === undefined) {
    return <span data-testid={testId}>never {label}</span>;
  }

  const when = run?.completedUtc ?? at ?? run?.startedUtc;
  const shown = <span data-testid={testId}>{label} {when === undefined ? "" : <RelativeTime value={when} absolute={false} />}</span>;
  if (run === undefined) {
    return shown;
  }

  const text = `Run ${run.inventoryRunId} (${run.operation}, ${run.status}) by ${run.actor}, through ${run.read}: ${runOutcome(run)}.${run.error === undefined ? "" : `\n${run.error}`}`;
  return (
    <ExplainTip title={`Last ${run.operation}`} text={text} testId={testId}>
      <button type="button" className="rounded-sm outline-none hover:text-foreground hover:underline focus-visible:underline">{shown}</button>
    </ExplainTip>
  );
}

/** A run in the About card's words: when it ended (or started, while it runs), its number and who ran it. */
function RunLine({ run, at }: { run: InventoryRun | undefined; at: string | undefined }) {
  const when = run?.completedUtc ?? at ?? run?.startedUtc;
  if (when === undefined) {
    return <span className="text-muted-foreground">never</span>;
  }

  return (
    <span>
      <RelativeTime value={when} absolute={false} />
      {run === undefined ? "" : `, run ${run.inventoryRunId} by ${run.actor}`}
    </span>
  );
}

/**
 * What the inventory is and how it reads, behind the mark beside its name, so the heading keeps to its name and a line
 * of facts: what it is for, the kind it lists (narrowed by its query) and how, the flow that keeps it, its last build and
 * reconcile, and the owners the last reconcile used, by which an id no ledger knows is an orphan or foreign.
 */
function AboutTip({ detail }: { detail: InventoryDetail }) {
  const inventory = detail.inventory;
  const owners = detail.owners;
  const identities = owners?.identities ?? [];
  const row = (label: string, value: ReactNode) => (
    <>
      <dt className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">{label}</dt>
      <dd className="min-w-0">{value}</dd>
    </>
  );
  const known = owners === undefined ? "Not known until a reconcile." : `${ownersSourceText(owners.source)}.`;
  return (
    <HoverCard openDelay={250} closeDelay={150}>
      <HoverCardTrigger asChild>
        <button
          type="button"
          className="inline-flex shrink-0 rounded-sm text-muted-foreground outline-none hover:text-foreground focus-visible:text-foreground"
          aria-label="About the inventory"
          data-testid="inventory-about"
        >
          <Info className="size-4" />
        </button>
      </HoverCardTrigger>
      <HoverCardContent align="start" className="w-[30rem] max-w-[calc(100vw-2rem)] p-0" data-testid="inventory-about-tip">
        {detail.description !== undefined && (
          <p className="border-b border-border px-3 py-2 text-[12.5px] leading-snug" data-testid="inventory-description">{detail.description}</p>
        )}
        <dl className="grid grid-cols-[5.75rem_minmax(0,1fr)] items-baseline gap-x-3 gap-y-1.5 px-3 py-2.5 text-[12.5px] font-normal">
          {row("Kind", (
            <span className="flex items-start gap-1">
              <span className="min-w-0 font-mono text-[11.5px] [overflow-wrap:anywhere]">{inventory.kind}</span>
              <CopyButton iconOnly label="Copy the kind" text={inventory.kind} testId="inventory-about-copy-kind" />
            </span>
          ))}
          {inventory.query !== undefined && row("Narrowed by", <span className="font-mono text-[11.5px] [overflow-wrap:anywhere]">{inventory.query}</span>)}
          {row("Reads", `through ${inventory.read}, ${inventory.versions === "all" ? "every version" : "the latest version"}`)}
          {row("Kept by", (
            <span>
              {detail.pipelineId === undefined
                ? <span className="font-mono text-[12px]">{inventory.flowName}</span>
                : <Link to={`/pipelines/${detail.pipelineId}?tab=inventories`} className="font-mono text-[12px] text-primary hover:underline">{inventory.flowName}</Link>}
              {detail.declared === false ? ", which no longer declares it," : ""}
              {` in ${inventory.partition}`}
            </span>
          ))}
          {row("Built", <RunLine run={detail.lastBuild} at={inventory.lastBuiltUtc} />)}
          {row("Reconciled", <RunLine run={detail.lastReconcile} at={inventory.lastReconciledUtc} />)}
          {row("Owners", (
            <span className="flex flex-col gap-0.5" data-testid="inventory-owners">
              <span>{known.charAt(0).toUpperCase() + known.slice(1)}</span>
              {identities.map((owner) => (
                <span key={owner.identity} className="min-w-0 text-muted-foreground [overflow-wrap:anywhere]">
                  <span className="font-mono text-[11.5px] text-foreground">{owner.identity}</span>
                  {`: created ${counted(owner.records, "id")} a ledger claims`}
                </span>
              ))}
              <span className="text-[11.5px] text-muted-foreground">An id no ledger knows is an orphan when one of them created it, else foreign.</span>
            </span>
          ))}
        </dl>
      </HoverCardContent>
    </HoverCard>
  );
}

/**
 * The whole of an inventory's ids, or those of the finding in view, downloaded as CSV; while conditions select ids, those that
 * match come first, each with what it held at the properties the conditions ask.
 */
function ExportMenu({ inventory, finding, selection }: { inventory: Inventory; finding: string | null; selection: InventorySelection | undefined }) {
  const [busy, setBusy] = useState(false);
  const download = async (what: string | null, matching: boolean) => {
    setBusy(true);
    try {
      await downloadInventory(inventory, what, matching ? selection?.selectionId : undefined);
      toast.success(`Downloaded the ${matching ? "matching " : ""}${what === null ? "" : `${findingVisual(what).label.toLowerCase()} `}ids of ${inventory.name}`);
    } catch (error) {
      toast.error(`The ids of ${inventory.name} could not be downloaded: ${isApiError(error) ? error.detail ?? error.title : String(error)}`);
    } finally {
      setBusy(false);
    }
  };

  return (
    <DropdownMenu>
      <Tooltip>
        <TooltipTrigger asChild>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon-sm" className="size-7 text-muted-foreground" disabled={busy} aria-label="Export the ids as CSV" data-testid="inventory-export">
              {busy ? <Loader2 className="animate-spin" /> : <Download />}
            </Button>
          </DropdownMenuTrigger>
        </TooltipTrigger>
        <TooltipContent>Export the ids as CSV</TooltipContent>
      </Tooltip>
      <DropdownMenuContent align="end" className="w-80">
        {selection !== undefined && finding !== null && (
          <DropdownMenuItem onSelect={() => void download(finding, true)} data-testid="inventory-export-matching-finding">
            <Download />
            The matching {findingVisual(finding).label.toLowerCase()} ids as CSV, with their values
          </DropdownMenuItem>
        )}
        {selection !== undefined && (
          <DropdownMenuItem onSelect={() => void download(null, true)} data-testid="inventory-export-matching">
            <Download />
            Every matching id as CSV, with its finding and values
          </DropdownMenuItem>
        )}
        {finding !== null && (
          <DropdownMenuItem onSelect={() => void download(finding, false)} data-testid="inventory-export-finding">
            <Download />
            {`${selection === undefined ? "The" : "Every"} ${findingVisual(finding).label.toLowerCase()} id as CSV`}
          </DropdownMenuItem>
        )}
        <DropdownMenuItem onSelect={() => void download(null, false)} data-testid="inventory-export-all">
          <Download />
          Every id as CSV, with its finding
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/**
 * Why the ids of a removable finding in view cannot be picked and removed here: the flow declares no removal, or not of this
 * finding. Null where they can be, and for a finding no inventory removes.
 */
function removalOff(detail: InventoryDetail, finding: string | null): string | null {
  if (finding === null || !REMOVABLE_FINDINGS.some((removable) => removable === finding)) {
    return null;
  }

  const label = findingVisual(finding).label.toLowerCase();
  const flow = detail.inventory.flowName;
  if (detail.removal === undefined) {
    return `Removing ${label} ids is off: ${flow} declares no removal, so it only reads OSDU. Add removal: { findings: [orphan, stale, forgotten] } to its YAML (and purge: true to allow purging as well) and sync the repository; the ids can then be picked here and removed from OSDU.`;
  }

  return detail.removal.findings.includes(finding)
    ? null
    : `${flow} allows removing ${detail.removal.findings.join(", ")} ids, not ${label} ones: add ${finding} to removal.findings in its YAML and sync the repository.`;
}

/** The inventory's builds and reconciles, newest first: what each read, changed and raised, who ran it and why one failed. */
function InventoryRuns({ inventory }: { inventory: Inventory }) {
  const runs = useQuery({
    queryKey: ["delivery", "inventories", "runs", inventory.partition, inventory.inventoryId],
    queryFn: () => inventoryApi.runs(inventory.partition, inventory.inventoryId, RUNS_SHOWN),
    refetchInterval: REFRESH_MS,
  });
  if (runs.isError) {
    return <ProblemView error={runs.error} testId="inventory-runs-error" />;
  }

  const columns: Column<InventoryRun>[] = [
    { id: "run", header: "Run", render: (row) => <span className="font-mono text-[12px] tabular-nums">{row.inventoryRunId}</span> },
    { id: "operation", header: "Operation", render: (row) => <span className="font-mono text-[12px]">{row.operation}</span> },
    {
      id: "status",
      header: "Status",
      render: (row) => (row.error === undefined
        ? <RunStatusPill status={row.status} testId="inventory-run-status" />
        : (
          <ExplainTip title="Why it failed" text={row.error} testId="inventory-run-error">
            <span className="inline-flex"><RunStatusPill status={row.status} testId="inventory-run-status" /></span>
          </ExplainTip>
        )),
    },
    { id: "started", header: "Started", render: (row) => <span className="whitespace-nowrap text-[12px]"><RelativeTime value={row.startedUtc} absolute={false} /></span> },
    {
      id: "took",
      header: "Took",
      align: "right",
      render: (row) => (row.completedUtc === undefined
        ? <span className="text-[12px] text-muted-foreground">-</span>
        : (
          <span className="font-mono text-[12px] tabular-nums">
            {formatDurationSeconds(Math.max(0, (parseUtc(row.completedUtc).getTime() - parseUtc(row.startedUtc).getTime()) / 1000))}
          </span>
        )),
    },
    { id: "outcome", header: "What it did", fill: true, floor: 260, render: (row) => <TruncatedText text={runOutcome(row, false)} maxWidth={640} /> },
    {
      id: "raised",
      header: "Raised",
      align: "right",
      render: (row) => (row.raised === undefined
        ? <span className="text-[12px] text-muted-foreground">-</span>
        : <span className={cn("font-mono text-[12px] tabular-nums", row.raised === 0 && "text-muted-foreground")}>{row.raised.toLocaleString("en-US")}</span>),
    },
    { id: "actor", header: "By", render: (row) => <TruncatedText text={row.actor} mono maxWidth={180} /> },
    { id: "platform", header: "", align: "right", render: (row) => (row.runId === undefined ? null : <RunRef runId={row.runId} />) },
  ];

  return (
    <DataTable
      columns={columns}
      rows={runs.data}
      rowKey={(row) => row.inventoryRunId}
      emptyMessage="No build or reconcile has run yet."
      data-testid="inventory-runs-table"
    />
  );
}

/**
 * One inventory of a partition, on the whole width of the page. Its heading is one line: the way back to every inventory,
 * its name and where it stands, the others a click away, what it is on hover over the mark beside it, and on the right the
 * lookup and Run pipeline; under it, one line of facts (its ids, how many are raised, its last reconcile and the kind it
 * lists). Then what its newest run asks of the reader, and its tabs: its ids, whose findings lead the grid's toolbar as one
 * line of tabs with the export and the columns at its end, paged in place in a grid that fills the window; and its runs. An
 * id opens in a panel under the grid, as OSDU holds it. The finding, the tab and the id open live in the address, so a
 * link lands on the same view.
 *
 * Its ids can be selected by what OSDU holds (osdu/docs/reference/flow/inventory-selections.md, Selecting ids by what OSDU holds): a
 * condition on a property of the records or a column of a source system, made in the explorer's editor from the grid's
 * Filter, opens a match the flow runs; while it holds, every tab counts its matching ids before its own and the grid lists
 * them. The conditions live in the address too. A selection made of a duplicate check's picks (Finding duplicate records)
 * is listed the same way, by its number in the address, and its Duplicates tab finds and decides the copies.
 */
export function InventoryReport({
  reference, siblings, finding, onFinding, view, onView, onOpen, openId, onOpenId, conditions, onConditions, selectionId, onSelectionId,
  duplicateCheck, onDuplicateCheck, duplicateKey, onDuplicateKey, duplicateRecord, onDuplicateRecord, duplicateOutcome, onDuplicateOutcome,
}: {
  reference: InventoryRef;
  /** The inventories the switcher lists. */
  siblings: readonly Inventory[];
  finding: FindingPick;
  onFinding: (finding: string) => void;
  view: InventoryView;
  onView: (view: InventoryView) => void;
  /** Opens another inventory, or every inventory for null. */
  onOpen: (ref: InventoryRef | null) => void;
  /** The OSDU id open in the panel under the grid; null when none is. */
  openId: string | null;
  /** Opens an id in the panel, or closes it with null. */
  onOpenId: (id: string | null) => void;
  /** The conditions the ids are selected by; none lists every id of the finding. */
  conditions: ExplorerFilter[];
  /** Sets the conditions; none drops the selection. */
  onConditions: (conditions: ExplorerFilter[]) => void;
  /** The selection the Ids tab lists by its number (a duplicate check's picks), while no conditions select ids. */
  selectionId: number | null;
  /** Lists a selection by its number on the Ids tab, opening on the finding given; null lists every id again. */
  onSelectionId: (selectionId: number | null, finding?: string | null) => void;
  /** The duplicate check the Duplicates tab shows; null shows the newest. */
  duplicateCheck: number | null;
  onDuplicateCheck: (checkId: number | null) => void;
  /** The key being built on the Duplicates tab, as the address holds it; null takes the key of the check shown. */
  duplicateKey: string | null;
  onDuplicateKey: (key: DuplicateKeyProperty[]) => void;
  /** The record the Duplicates tab builds the key in; null shows the first id of the inventory OSDU serves. */
  duplicateRecord: string | null;
  onDuplicateRecord: (id: string | null) => void;
  /** The outcome whose groups the Duplicates tab lists. */
  duplicateOutcome: string | null;
  onDuplicateOutcome: (outcome: string) => void;
}) {
  const [launching, setLaunching] = useState(false);
  const matchedBy = useInventorySelection(reference, conditions);
  // A duplicate check's picks are matched again by checking again, on their own tab.
  const picked = useInventorySelectionById(reference, conditions.length === 0 ? selectionId : null, () => onView("duplicates"));
  const byNumber = conditions.length === 0 && selectionId !== null;
  const selection = byNumber ? picked : matchedBy;
  const [active] = useActivePartition();
  const queryClient = useQueryClient();
  const { hasScope } = useAuth();
  // The panel reads OSDU through the partition's own connection, as the explorer does, under the operate policy, which
  // every signed-in user holds (only admin checks a scope).
  const canReadOsdu = hasScope("operate");
  const detail = useQuery({
    queryKey: ["delivery", "inventories", "detail", reference.partition, reference.inventoryId],
    queryFn: () => inventoryApi.inventory(reference.partition, reference.inventoryId),
    refetchInterval: (query) => (query.state.data?.inventory.latest?.status === "running" ? REFRESH_MS : IDLE_REFRESH_MS),
  });
  const removals = useQuery({
    queryKey: ["delivery", "inventories", "removals", reference.partition, reference.inventoryId],
    queryFn: () => inventoryApi.removals(reference.partition, reference.inventoryId, RUNS_SHOWN),
    refetchInterval: (query) => ((query.state.data ?? []).some((r) => r.status === "running") ? REMOVAL_REFRESH_MS : IDLE_REFRESH_MS),
  });
  const conditionSentence = useConditionSentence(detail.data?.inventory.kind, conditions);
  const sentence = byNumber ? duplicateSelectionSentence(picked) : conditionSentence;

  // A removal that finishes changed the inventory's rows: its counts and its grid are read again, rather than a minute later.
  const running = (removals.data ?? []).filter((r) => r.status === "running").map((r) => r.inventoryRemovalId).join(",");
  const wasRunning = useRef(running);
  useEffect(() => {
    const finished = wasRunning.current.split(",").filter((id) => id !== "" && !running.split(",").includes(id));
    wasRunning.current = running;
    if (finished.length > 0) {
      void queryClient.invalidateQueries({ queryKey: ["delivery", "inventories", "detail", reference.partition, reference.inventoryId] });
      void queryClient.invalidateQueries({ queryKey: ["delivery", "inventories", "records", reference.partition, reference.inventoryId] });
      void queryClient.invalidateQueries({ queryKey: ["delivery", "inventories", "selection", reference.partition, reference.inventoryId] });
    }
  }, [running, queryClient, reference.partition, reference.inventoryId]);

  if (detail.isError) {
    return (
      <div className="flex flex-col gap-3" data-testid="inventory-report">
        <PageHeader title={<span className="flex items-center gap-1.5"><InventoriesCrumb onBack={() => onOpen(null)} />Inventory {reference.inventoryId}</span>} />
        <ProblemView error={detail.error} testId="inventory-error" />
      </div>
    );
  }

  if (detail.data === undefined) {
    return (
      <div className="flex flex-col gap-3" data-testid="inventory-report">
        <Skeleton className="h-14 w-full rounded-lg" />
        <Skeleton className="h-96 w-full rounded-lg" />
      </div>
    );
  }

  const data = detail.data;
  const inventory = data.inventory;
  const standing = standingOf(inventory, data.declared !== false);
  const counts = allCounts(data.counts);
  const chosen = finding === EVERY_ID ? null : finding ?? openingFinding(counts);
  // While a match holds ids, the tabs count its ids of each finding and the grid lists only those.
  const matched = selection.phase === "completed" || selection.phase === "stale" ? matchedCounts(selection.selection) : null;
  const total = matched !== null
    ? chosen === null ? [...matched.values()].reduce((sum, count) => sum + count, 0) : matched.get(chosen) ?? 0
    : chosen === null ? data.ids : counts.find((count) => count.finding === chosen)?.count ?? null;
  const dot = <span className="text-muted-foreground/50" aria-hidden>·</span>;
  const latest = inventory.latest;
  const failedAfter = latest !== undefined && latest.status === "failed" && latest.inventoryRunId !== inventory.lastReconcileRunId ? latest : undefined;
  const canRun = data.pipelineId !== undefined && data.repoId !== undefined && data.declared !== false;
  const built = inventory.lastBuiltUtc !== undefined || inventory.lastReconcileRunId !== undefined;
  const reconciled = data.lastReconcile !== undefined || inventory.lastReconciledUtc !== undefined;
  const underWay = (removals.data ?? []).find((r) => r.status === "running");

  return (
    <div className="flex min-w-0 flex-col gap-3" data-testid="inventory-report" data-inventory={inventory.name}>
      {/* The facts have the whole width under the title and its actions, on one line whose kind gives way first. */}
      <div className="flex min-w-0 flex-col gap-0.5">
        <PageHeader
          title={(
            <span className="flex min-w-0 items-center gap-x-1.5">
              <InventoriesCrumb onBack={() => onOpen(null)} />
              <StandingGlyph standing={standing} testId="inventory-standing" />
              <span className="min-w-0 truncate" data-testid="inventory-name">{inventory.name}</span>
              <Switcher current={reference} siblings={siblings} onOpen={onOpen} />
              <AboutTip detail={data} />
            </span>
          )}
          actions={(
            <>
              <InventoryLookupBox />
              {canRun && (
                <RichTooltip body={`Runs ${inventory.flowName} with this inventory picked: a build reads every id of its kind again and reconciles it; the dialog can pick a reconcile, a plan, or the flow's other inventories.`}>
                  <Button size="sm" onClick={() => setLaunching(true)} disabled={latest?.status === "running"} data-testid="inventory-run">
                    <Play />
                    Run pipeline
                  </Button>
                </RichTooltip>
              )}
            </>
          )}
        />
        <div className="flex min-w-0 items-center gap-x-1.5 overflow-hidden whitespace-nowrap text-[13px] text-muted-foreground" data-testid="inventory-summary">
          <span className="flex shrink-0 items-center gap-x-1.5">
            <span><span className="font-mono font-medium tabular-nums text-foreground">{data.ids.toLocaleString("en-US")}</span> {data.ids === 1 ? "id" : "ids"}</span>
            {dot}
            <span className={data.raised > 0 ? "text-foreground" : undefined} data-testid="inventory-summary-raised">
              <span className="font-mono font-medium tabular-nums">{data.raised.toLocaleString("en-US")}</span> raised
            </span>
            {dot}
            {reconciled || !built
              ? <RunFact label="reconciled" run={data.lastReconcile} at={inventory.lastReconciledUtc} testId="inventory-summary-reconciled" />
              : <RunFact label="built" run={data.lastBuild} at={inventory.lastBuiltUtc} testId="inventory-summary-built" />}
            {dot}
          </span>
          <span className="min-w-0 max-w-[26rem]"><KindText kind={inventory.kind} /></span>
          {inventory.query !== undefined && (
            <RichTooltip title="Narrowed by" body={inventory.query} mono>
              <span className="shrink-0 rounded-sm border border-border px-1 text-[10.5px]">query</span>
            </RichTooltip>
          )}
          {inventory.partition !== active && <span className="shrink-0">{`in ${inventory.partition}`}</span>}
          {data.declared === false && <span className="shrink-0">(no longer declared)</span>}
        </div>
      </div>

      {failedAfter !== undefined && (
        <Alert variant="destructive" data-testid="inventory-failed">
          <CircleX />
          <AlertTitle className="flex flex-wrap items-center gap-x-1.5">
            The newest {failedAfter.operation} failed <RelativeTime value={failedAfter.completedUtc ?? failedAfter.startedUtc} absolute={false} />
          </AlertTitle>
          <AlertDescription>
            <ExplainTip title="Why it failed" text={failedAfter.error ?? "The run kept no reason."} testId="inventory-failed-why">
              <span className="line-clamp-1 break-all">{failedAfter.error ?? "The run kept no reason."}</span>
            </ExplainTip>
          </AlertDescription>
        </Alert>
      )}

      {underWay !== undefined && (
        <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5 rounded-md border border-info/40 bg-info/5 px-3 py-1.5 text-[12.5px]" data-testid="inventory-removal-running">
          <Loader2 className="size-3.5 shrink-0 animate-spin text-info" aria-hidden />
          <span>
            {`Removal ${underWay.inventoryRemovalId} is removing ${underWay.requested.toLocaleString("en-US")} ${findingVisual(underWay.finding).label.toLowerCase()} id${underWay.requested === 1 ? "" : "s"}: `}
            <span className="font-mono tabular-nums">{(underWay.removed + underWay.gone + underWay.skipped + underWay.failed).toLocaleString("en-US")}</span>
            {" reached so far."}
          </span>
          <button type="button" onClick={() => onView("removals")} className="text-primary hover:underline" data-testid="inventory-removal-running-open">
            Its progress
          </button>
        </div>
      )}

      {!built
        ? (
          <Card className="gap-0 rounded-lg p-0">
            <EmptyState
              icon={<ClipboardList />}
              title={`${inventory.name} has not been built in ${inventory.partition}`}
              description="A build reads every id its kind holds, with who created and changed each, and compares every id with the ledgers of the partition."
              action={canRun ? <Button size="sm" onClick={() => setLaunching(true)} data-testid="inventory-run-first"><Play />Run pipeline</Button> : undefined}
              data-testid="inventory-not-built"
            />
          </Card>
        )
        : (
          <Tabs value={view} onValueChange={(next) => onView(next as InventoryView)} className="min-w-0 gap-2">
            <div className="border-b border-border">
              <TabsList variant="line" data-testid="inventory-tabs">
                <TabsTrigger value="ids" data-testid="inventory-tab-ids">Ids</TabsTrigger>
                <TabsTrigger value="duplicates" data-testid="inventory-tab-duplicates">Duplicates</TabsTrigger>
                <TabsTrigger value="runs" data-testid="inventory-tab-runs">Runs</TabsTrigger>
                {(data.removal !== undefined || (removals.data?.length ?? 0) > 0) && (
                  <TabsTrigger value="removals" data-testid="inventory-tab-removals">Removals</TabsTrigger>
                )}
              </TabsList>
            </div>
            <TabsContent value="ids">
              <InventoryRecordsGrid
                key={chosen ?? EVERY_ID}
                inventory={inventory}
                finding={chosen}
                total={total}
                leading={<InventoryFindingStrip total={data.ids} counts={counts} selected={chosen} onSelect={(picked) => onFinding(picked ?? EVERY_ID)} matched={matched} />}
                trailing={(
                  <>
                    {canReadOsdu && removalOff(data, chosen) !== null && (
                      <RichTooltip title="Removal is off" body={removalOff(data, chosen)!}>
                        <span className="inline-flex h-7 cursor-help items-center gap-1 rounded-md px-2 text-[12px] text-muted-foreground" data-testid="inventory-removal-off">
                          <Trash2 className="size-3.5" aria-hidden />
                          Removal off
                        </span>
                      </RichTooltip>
                    )}
                    {canReadOsdu && (
                      <ExplorerAddFilter
                        compact
                        partition={inventory.partition}
                        request={{ kind: inventory.kind, filters: conditions }}
                        onAdd={(filter) => onConditions([...conditions.filter((kept) => !sameFilter(kept, filter)), filter])}
                        title="Select the ids by what OSDU holds: a property of the records, or a column of a source system the flows read"
                        testId="inventory-add-condition"
                      />
                    )}
                    <ExportMenu inventory={inventory} finding={chosen} selection={matched === null ? undefined : selection.selection} />
                  </>
                )}
                below={byNumber
                  ? <DuplicateSelectionBar state={picked} onCheck={(checkId) => { onDuplicateCheck(checkId); onView("duplicates"); }} onClear={() => onSelectionId(null)} />
                  : <InventoryConditionsBar inventory={inventory} conditions={conditions} onConditions={onConditions} state={selection} sentence={sentence} />}
                selection={conditions.length === 0 && !byNumber
                  ? undefined
                  : { state: selection, sentence, onClear: () => (byNumber ? onSelectionId(null) : onConditions([])) }}
                openId={openId}
                onOpen={canReadOsdu ? onOpenId : undefined}
                removal={canReadOsdu ? data.removal : undefined}
                removesGone={canReadOsdu && canRun}
                repairsMissing={canReadOsdu}
              />
            </TabsContent>
            <TabsContent value="duplicates">
              <InventoryDuplicates
                inventory={inventory}
                detail={data}
                checkId={duplicateCheck}
                onCheck={onDuplicateCheck}
                draftText={duplicateKey}
                onDraft={onDuplicateKey}
                sampleId={duplicateRecord}
                onSampleId={onDuplicateRecord}
                outcome={duplicateOutcome}
                onOutcome={onDuplicateOutcome}
                openId={openId}
                onOpenId={canReadOsdu ? onOpenId : undefined}
                canAct={canReadOsdu}
                onSelection={(made) => {
                  const removable = (made.counts ?? []).find((count) => REMOVABLE_FINDINGS.some((f) => f === count.finding) && count.count > 0);
                  onSelectionId(made.selectionId, removable?.finding ?? null);
                }}
              />
            </TabsContent>
            <TabsContent value="removals">
              <InventoryRemovals inventory={inventory} removals={removals.data} error={removals.error} />
            </TabsContent>
            <TabsContent value="runs">
              <InventoryRuns inventory={inventory} />
            </TabsContent>
          </Tabs>
        )}

      {launching && canRun && (
        <TriggerRunDialog
          open
          onClose={() => setLaunching(false)}
          repoId={data.repoId}
          flowName={inventory.flowName}
          flowId={data.pipelineId}
          flowKind="inventory"
          initialOperation="build"
          initialValues={null}
          initialPayload={inventoriesPayload([inventory.name])}
        />
      )}
    </div>
  );
}
