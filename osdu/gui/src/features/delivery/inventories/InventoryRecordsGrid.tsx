import { useEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from "react";
import { Link, useNavigate } from "react-router-dom";
import { keepPreviousData, useInfiniteQuery, useQueryClient } from "@tanstack/react-query";
import { RotateCcw, Trash2 } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { DataTable, type Column } from "@/components/DataTable";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { TruncatedText } from "@/components/TruncatedText";
import { useOwnedPanel } from "@/layout/workbench/useOwnedPanel";
import { cn } from "@/lib/utils";
import { deliveryRecordRoute } from "../../../api/delivery";
import {
  GONE_FINDING, MISSING_FINDING, REMOVABLE_FINDINGS, inventoryApi, type Inventory, type InventoryRecord, type InventoryRemovalAccepted, type InventoryRemovalPolicy,
} from "../../../api/inventories";
import { RecordName } from "../RecordName";
import { idParts } from "../osduRecordModel";
import { ProblemView } from "../TemplateSheet";
import { DimensionGrid, GridFooter, GridViewMenu } from "../dimensions/DimensionGrid";
import { useHiddenColumns, type GridColumnChoice } from "../dimensions/dimensionGridState";
import { ExplainTip } from "@/components/ExplainTip";
import { FindingGlyph } from "./InventoryBadges";
import { InventoryIdPanel } from "./InventoryIdPanel";
import { InventoryRepairDialog } from "./InventoryRepairDialog";
import { RemovalDialog } from "../RemovalDialog";
import { fieldLabel } from "../explorer/explorerModel";
import { arrowsKeptByTarget, typedInField } from "../panelKeys";
import { findingVisual, findingWhy } from "./inventoryFormat";
import { SelectionPanel, SelectionValueCell } from "./InventorySelectionParts";
import type { InventorySelectionState } from "./inventorySelection";

const PAGE = 200;

/** The columns a grid of one finding starts without. */
const AT_FIRST_FINDING: readonly string[] = ["created"];

/** The columns a grid of every id starts without. */
const AT_FIRST_EVERY_ID: readonly string[] = ["created", "since"];

/** The workbench's bottom panel content this grid raises: an OSDU id after this prefix. */
const PANEL = "inventory-id:";

/** The most ids one removal names; past it, a removal takes every id of the finding. */
const MAX_PICKED = 1000;

/** The row whose id is open in the panel, marked as the cache history marks the row its panel shows. */
const OPEN_ROW: CSSProperties = { backgroundColor: "var(--accent)" };

/** The findings whose ids no condition reaches: OSDU's search does not serve them, so a match never selects one. */
const UNSEARCHED: readonly string[] = ["missing", "unlisted", "gone"];

/**
 * The ids an operator's conditions select (osdu/docs/reference/flow/inventory-selections.md, Selecting ids by what OSDU holds): where
 * their match stands, the conditions as a sentence, and how to drop them.
 */
export interface GridSelection {
  state: InventorySelectionState;
  sentence: string;
  onClear: () => void;
}

/**
 * The ledger that holds the id, as the record's status there (or the state of the artifact it rests on), which opens the
 * record, or the ledger's records when the id rests on none; the ledger's name and the whole of what it holds of the id,
 * versions included, on hover.
 */
function LedgerCell({ row }: { row: InventoryRecord }) {
  if (row.ledgerFlowId === undefined) {
    return <span className="text-[12px] text-muted-foreground">none</span>;
  }

  const name = row.ledger ?? row.ledgerFlowId;
  const state = row.ledgerStatus ?? (row.artifactState === undefined ? "held" : `artifact ${row.artifactState}`);
  const held = [
    `Ledger ${name}`,
    row.ledgerStatus === undefined ? undefined : `record ${row.ledgerStatus}${row.ledgerVersion === undefined ? ", no version" : ` at version ${row.ledgerVersion}`}`,
    row.deliveryKey === undefined ? undefined : `delivery key ${row.deliveryKey}`,
    row.artifactId === undefined ? undefined : `artifact ${row.artifactId}${row.artifactState === undefined ? "" : ` ${row.artifactState}`}`,
  ].filter((part): part is string => part !== undefined).join("\n");
  const to = row.deliveryKey === undefined
    ? `/delivery/records?flow=${encodeURIComponent(row.ledgerFlowId)}`
    : deliveryRecordRoute({ flowId: row.ledgerFlowId, deliveryKey: row.deliveryKey });
  return (
    <RichTooltip title="What the ledger holds" body={held} mono>
      <Link
        to={to}
        className="whitespace-nowrap text-[12px] text-primary hover:underline"
        onClick={(event) => event.stopPropagation()}
        data-testid="inventory-record-ledger"
      >
        {state}
      </Link>
    </RichTooltip>
  );
}

/**
 * The ids of an inventory with one finding, or every id, as a grid that fits the window and scrolls inside the page: the
 * findings to pick from lead its toolbar, and more ids are read as it is scrolled, a page at a time after the last id
 * read. Each id is named as a reader knows it (the whole id on hover, its copy on the row's hover), with its finding (why
 * it has it on hover, to copy), its version, the ledger that holds it (which opens its record), who created it and when it
 * last changed, and since when it has its finding; columns are left out under View. A reader who can read OSDU opens an
 * id by its row in the workbench's bottom panel, over the page as a run's trace is, and steps through the ids there with
 * Up and Down; Escape or the panel's own close closes it. The id open is the page's, so a link lands on it. Where the flow
 * lets an operator remove the finding in view, its ids can be picked, or every one of them at once, however many, and removed
 * from OSDU through the removal dialog, which queues a run of the flow. Gone ids, which OSDU no longer serves, are picked and
 * removed the same way from the inventory alone, whatever the flow declares; missing ids are picked the same way and their
 * records delivered again through their own flows.
 *
 * While an operator's conditions select ids, the grid lists only the ids their match found, each with what it held at the
 * properties the conditions ask, a column each; every one of them can be picked at once and removed by the selection itself,
 * however many. While the match is asked for or reads OSDU, or when it cannot hold any id, a panel saying so stands in the
 * grid's place under the same toolbar.
 */
export function InventoryRecordsGrid({ inventory, finding, total, leading, trailing, below, selection, openId, onOpen, removal, removesGone = false, repairsMissing = false }: {
  inventory: Inventory;
  /** The finding whose ids are listed; null lists every id, each with its finding. */
  finding: string | null;
  /** How many ids the grid can show, as the counts say (of the selection's, while one holds); null when unknown. */
  total: number | null;
  /** What leads the grid's toolbar: the findings to pick from. */
  leading?: ReactNode;
  /** What the toolbar holds before View at its end (the export). */
  trailing?: ReactNode;
  /** What stands under the toolbar's line: the conditions the ids are selected by. */
  below?: ReactNode;
  /** The ids the conditions select; absent while there are none. */
  selection?: GridSelection;
  /** The OSDU id open in the bottom panel; null when none is. */
  openId: string | null;
  /** Opens an id in the panel, or closes it with null; absent where the reader cannot read OSDU. */
  onOpen?: (id: string | null) => void;
  /** What the flow lets an operator remove; absent where it allows nothing, or the reader cannot act on OSDU. */
  removal?: InventoryRemovalPolicy;
  /** Whether the reader may remove gone ids from the inventory, which asks nothing of OSDU and so no removal of the flow's. */
  removesGone?: boolean;
  /** Whether the reader may deliver again the records behind missing ids, through the flows that delivered them. */
  repairsMissing?: boolean;
}) {
  // Every id adds a Finding column, so that view starts without the time each id has had its finding too.
  const atFirst = finding === null ? AT_FIRST_EVERY_ID : AT_FIRST_FINDING;
  const [hidden, toggleColumn] = useHiddenColumns(`osdu.inventories.records.columns.${finding === null ? "all" : "finding"}`, atFirst);
  // The ids a match holds are listed once it read OSDU whole; a match made against an earlier build still lists what it found.
  const phase = selection?.state.phase ?? "none";
  const selected = phase === "completed" || phase === "stale" ? selection?.state.selection : undefined;
  const awaiting = selection !== undefined && selected === undefined;
  // The rows the grid lists come from the inventory, or from one match of it: a new source never shows the old one's rows
  // while its own are read, since those are not the ids its counts are of.
  const source = awaiting ? "awaiting" : selected === undefined ? "inventory" : `selection-${selected.selectionId}`;
  const pages = useInfiniteQuery({
    queryKey: ["delivery", "inventories", "records", inventory.partition, inventory.inventoryId, finding, selected?.selectionId ?? null],
    queryFn: ({ pageParam }) => inventoryApi.records(inventory.partition, inventory.inventoryId, {
      finding: finding ?? undefined, after: pageParam, limit: PAGE, selection: selected?.selectionId,
    }),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: (last) => last.next,
    placeholderData: (previous, previousQuery) => (previousQuery?.queryKey[6] === (selected?.selectionId ?? null) ? keepPreviousData(previous) : undefined),
    enabled: !awaiting,
  });
  const rows = useMemo(() => pages.data?.pages.flatMap((page) => page.items), [pages.data]);
  const more = () => {
    if (pages.hasNextPage && !pages.isFetchingNextPage) {
      void pages.fetchNextPage();
    }
  };

  // The ids picked for a removal or a repair, by their row's number, or every id of the finding; a finding's grid offers it
  // where something can be done of its ids: one the flow lets an operator remove from OSDU, gone ids, which go from the
  // inventory alone, and missing ids, whose records are delivered again.
  const gone = finding === GONE_FINDING;
  const missing = finding === MISSING_FINDING;
  const removable = removal !== undefined && finding !== null && removal.findings.includes(finding) ? removal : undefined;
  const removes = gone ? removesGone : missing ? repairsMissing : removable !== undefined;
  // Every id of a selection is removed by the selection itself, which only the findings OSDU serves and a removal takes from
  // OSDU allow; the ids of any other finding a selection lists are picked one by one.
  const bySelection = selected !== undefined && finding !== null && REMOVABLE_FINDINGS.some((f) => f === finding);
  const everyAllowed = selected === undefined || bySelection;
  const [picked, setPicked] = useState<ReadonlySet<string>>(() => new Set());
  const [everyOne, setEveryOne] = useState(false);
  const [removing, setRemoving] = useState(false);
  // What was picked belongs to the rows it was picked from: conditions added, changed or dropped, or OSDU read again, start
  // with nothing picked, so nothing out of sight is ever removed.
  const [pickedFrom, setPickedFrom] = useState(source);
  if (pickedFrom !== source) {
    setPickedFrom(source);
    setPicked(new Set());
    setEveryOne(false);
  }
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const clearPicked = () => {
    setPicked(new Set());
    setEveryOne(false);
  };
  const pickedIds = rows === undefined ? [] : rows.filter((row) => picked.has(String(row.inventoryRecordId))).map((row) => row.targetId);
  const removalCount = everyOne ? total ?? 0 : pickedIds.length;

  const { ownedId, show, close } = useOwnedPanel(PANEL);
  const wanted = openId !== null && onOpen !== undefined ? openId : null;
  const at = wanted !== null && rows !== undefined ? rows.findIndex((row) => row.targetId === wanted) : -1;
  const record = at >= 0 ? rows?.[at] : undefined;
  const canBack = at > 0;
  const canForward = at >= 0 && (at < (rows?.length ?? 0) - 1 || pages.hasNextPage);
  // The id stepped to last, ahead of the address that follows it, so keys pressed faster than the page renders (a key
  // held down) each step on from the one before rather than from the same row.
  const stepped = useRef(openId);
  useEffect(() => {
    stepped.current = openId;
  }, [openId]);
  const step = (by: -1 | 1) => {
    if (rows === undefined || onOpen === undefined) {
      return;
    }

    const from = rows.findIndex((row) => row.targetId === stepped.current);
    const next = from + by;
    if (from < 0 || next < 0) {
      return;
    }

    if (next >= rows.length - 1) {
      // The panel keeps going where the grid's scrolling would: the next page is read as the last row read is reached.
      more();
    }

    if (next < rows.length) {
      stepped.current = rows[next].targetId;
      onOpen(rows[next].targetId);
    }
  };

  // What the panel's content and the keys reach for: the latest rows and the page's own way to open an id, so a panel
  // raised a while ago, or a listener set once, holds nothing stale.
  const latest = useRef({ step, open: (id: string | null) => onOpen?.(id), openId });
  useEffect(() => {
    latest.current = { step, open: (id: string | null) => onOpen?.(id), openId };
  });

  // The id open in the address is raised in the panel, and raised again as what the panel says of it changes (its row
  // read, the rows around it, a step to another id); with none open, the panel is closed when it shows this grid's.
  // Whether it does is read, not followed: a panel closed by its own close must not be raised again on the way to the
  // address letting go of the id.
  const showing = ownedId !== null;
  const showingNow = useRef(showing);
  useEffect(() => {
    showingNow.current = showing;
  });
  useEffect(() => {
    if (wanted === null) {
      if (showingNow.current) {
        close();
      }

      return;
    }

    const type = idParts(wanted).type;
    show(wanted, `Explorer · ${type === "" ? "record" : type}`, (
      <InventoryIdPanel
        partition={inventory.partition}
        id={wanted}
        record={record}
        place={at >= 0 ? { at: at + 1, of: total } : null}
        canBack={canBack}
        canForward={canForward}
        onStep={(by) => latest.current.step(by)}
        onOpenId={(id) => latest.current.open(id)}
      />
    ));
  }, [wanted, record, at, total, canBack, canForward, inventory.partition, show, close]);

  // The panel closed by its own close, or taken by another surface (a run's trace), lets go of the id in the address. Only
  // a panel that showed this grid's id and no longer does counts, so the first raise is never mistaken for a close.
  const wasShowing = useRef(false);
  useEffect(() => {
    if (wasShowing.current && !showing && latest.current.openId !== null) {
      latest.current.open(null);
    }

    wasShowing.current = showing;
  }, [showing]);

  // The row of the id open stays in view as the panel steps through the ids.
  const grid = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (at < 0 || grid.current === null) {
      return;
    }

    const row = grid.current.querySelectorAll<HTMLTableRowElement>('[data-slot="table-container"] tbody tr')[at];
    row?.scrollIntoView({ block: "nearest" });
  }, [at]);

  // Up and Down step through the ids while the panel shows one, unless they are for something that moves by arrows of
  // its own (the panel's record view among them); Escape closes it from anywhere but a field, once whatever it would
  // close first (a menu, a hover card) has taken it.
  useEffect(() => {
    if (!showing) {
      return undefined;
    }

    const keyed = (event: KeyboardEvent) => {
      if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey || typedInField(event.target)) {
        return;
      }

      if ((event.key === "ArrowDown" || event.key === "ArrowUp") && !arrowsKeptByTarget(event.target)) {
        event.preventDefault();
        latest.current.step(event.key === "ArrowDown" ? 1 : -1);
      } else if (event.key === "Escape") {
        latest.current.open(null);
      }
    };
    window.addEventListener("keydown", keyed);
    return () => window.removeEventListener("keydown", keyed);
  }, [showing]);

  const choices: GridColumnChoice[] = [
    { id: "version", label: "Version" },
    { id: "ledger", label: "Ledger" },
    { id: "created", label: "Created by" },
    { id: "changed", label: "Changed" },
    { id: "since", label: "Since" },
  ];
  const all: Column<InventoryRecord>[] = [
    {
      id: "id",
      header: "Id",
      fill: true,
      floor: 200,
      render: (row) => (
        <span className="flex min-w-0 items-center gap-1.5">
          <RecordName id={row.targetId} kind={row.kind} copy className="text-[12.5px]" testId="inventory-record-id" copyTestId="inventory-record-copy-id" />
          {row.goneUtc !== undefined && (
            <RichTooltip body="OSDU no longer serves it: the inventory's read did not list it.">
              <span className="shrink-0 rounded-sm border border-border px-1 text-[10.5px] text-muted-foreground">gone <RelativeTime value={row.goneUtc} absolute={false} /></span>
            </RichTooltip>
          )}
        </span>
      ),
    },
    {
      id: "why",
      header: finding === null ? "Finding" : "",
      render: (row) => (
        <ExplainTip title={`Why ${findingVisual(row.finding).label.toLowerCase()}`} text={findingWhy(row)} testId="inventory-record-why">
          {finding === null
            ? (
              <span className="inline-flex items-center gap-1 whitespace-nowrap text-[12px]" data-testid="inventory-record-finding" data-finding={row.finding}>
                <FindingGlyph finding={row.finding} />
                {findingVisual(row.finding).label.toLowerCase()}
              </span>
            )
            : (
              <button type="button" className="inline-flex rounded-sm outline-none focus-visible:ring-2 focus-visible:ring-ring/50" aria-label="Why it has its finding" data-testid="inventory-record-why">
                <FindingGlyph finding={row.finding} />
              </button>
            )}
        </ExplainTip>
      ),
    },
    {
      id: "version",
      header: "Version",
      align: "right",
      render: (row) => (row.version === undefined
        ? <span className="text-[12px] text-muted-foreground">-</span>
        : <span className="font-mono text-[12px] tabular-nums">{row.version}</span>),
    },
    { id: "ledger", header: "Ledger", render: (row) => <LedgerCell row={row} /> },
    {
      id: "created",
      header: "Created by",
      render: (row) => (row.createUser === undefined
        ? <span className="text-[12px] text-muted-foreground">-</span>
        : <TruncatedText text={row.createUser} mono maxWidth={200} />),
    },
    {
      id: "changed",
      header: "Changed",
      render: (row) => {
        const changedAt = row.modifyTime ?? row.createTime;
        if (changedAt === undefined) {
          return <span className="text-[12px] text-muted-foreground">-</span>;
        }

        const by = row.modifyUser ?? row.createUser;
        const time = <span className="whitespace-nowrap text-[12px]"><RelativeTime value={changedAt} absolute={false} /></span>;
        return by === undefined ? time : <RichTooltip title="Changed by" body={by} mono>{time}</RichTooltip>;
      },
    },
    {
      id: "since",
      header: "Since",
      render: (row) => (
        <RichTooltip body={`It has had this finding since this reconcile${row.firstSeenUtc === undefined ? "." : "; the inventory first listed it earlier or then."}`}>
          <span className="whitespace-nowrap text-[12px] text-muted-foreground"><RelativeTime value={row.findingUtc} absolute={false} /></span>
        </RichTooltip>
      ),
    },
  ];
  // What each id held at the properties the conditions ask, a column each after the id: why the row is there.
  const valueColumns: Column<InventoryRecord>[] = (selected?.columns ?? []).map((path) => ({
    id: `value:${path}`,
    header: fieldLabel(path),
    render: (row) => <SelectionValueCell values={row.values?.[path]} testId="inventory-record-value" />,
  }));
  const columns = [all[0], ...valueColumns, ...all.slice(1)].filter((column) => !hidden.has(column.id));
  const findingNoun = finding === null ? "id" : `${findingVisual(finding).label.toLowerCase()} id`;
  const noun = selected === undefined ? findingNoun : `matching ${findingNoun}`;
  const picking = removes && (picked.size > 0 || everyOne);
  const tooMany = !everyOne && pickedIds.length > MAX_PICKED;
  const staleNote = phase === "stale" ? "The inventory was built again since this match: match the conditions again before removing what it holds" : null;
  const toolbar = (
    <div className="flex min-w-0 flex-col">
      <div className="flex min-w-0 items-center gap-1 border-b border-border pl-1.5 pr-2">
        {leading}
        <div className="ml-auto flex shrink-0 items-center gap-0.5">
          {trailing}
          <GridViewMenu columns={choices} hidden={hidden} atFirst={atFirst} onToggle={toggleColumn} compact testId="inventory-records-view" />
        </div>
      </div>
      {below}
      {picking && (
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1 border-b border-border bg-accent/40 px-3 py-1.5" data-testid="inventory-selection-bar">
          <span className="text-[13px] font-medium tabular-nums" data-testid="inventory-selection-count">
            {everyOne ? `All ${(total ?? 0).toLocaleString("en-US")} ${noun}s selected` : `${pickedIds.length.toLocaleString("en-US")} selected`}
          </span>
          {!everyOne && everyAllowed && total !== null && total > pickedIds.length && (
            <Button variant="link" size="sm" className="h-6 px-0 text-[13px]" onClick={() => setEveryOne(true)} data-testid="inventory-select-every">
              {`Select all ${total.toLocaleString("en-US")} ${noun}s`}
            </Button>
          )}
          {tooMany && (
            <span className="text-[12.5px] text-muted-foreground" data-testid="inventory-selection-too-many">
              {missing
                ? `A repair names at most ${MAX_PICKED.toLocaleString("en-US")} ids; select all to deliver every one again`
                : `A removal names at most ${MAX_PICKED.toLocaleString("en-US")} ids; select all to remove every one`}
            </span>
          )}
          <Button variant="link" size="sm" className="h-6 px-0 text-[13px] text-muted-foreground" onClick={clearPicked} data-testid="inventory-clear-selection">
            Clear
          </Button>
          {missing
            ? (
              <Button
                variant="outline"
                size="sm"
                className="ml-auto h-7"
                onClick={() => setRemoving(true)}
                disabled={tooMany || removalCount === 0}
                title="Deliver the selected records again through the flows that delivered them, under their own ids or new ones"
                data-testid="inventory-repair-selected"
              >
                <RotateCcw />
                Deliver again
              </Button>
            )
            : (
              <Button
                variant="destructive-outline"
                size="sm"
                className="ml-auto h-7"
                onClick={() => setRemoving(true)}
                disabled={tooMany || removalCount === 0 || (everyOne && staleNote !== null)}
                title={everyOne && staleNote !== null
                  ? staleNote
                  : gone ? "Remove the selected ids from the inventory: OSDU no longer serves them, and nothing is asked of it" : "Remove the selected ids from OSDU, each checked again first"}
                data-testid="inventory-remove-selected"
              >
                <Trash2 />
                Remove
              </Button>
            )}
        </div>
      )}
    </div>
  );

  // With conditions selecting ids, the grid says why an empty finding holds none: no condition reaches an id OSDU's search
  // does not serve.
  const emptyMessage = selected !== undefined
    ? finding !== null && UNSEARCHED.includes(finding)
      ? `No ${findingNoun} matches: OSDU's search does not serve ${findingVisual(finding).label.toLowerCase()} ids, so no condition reaches them.`
      : `No ${findingNoun} holds what the conditions ask.`
    : finding === null ? "The inventory holds no id yet." : `No id is ${findingVisual(finding).label.toLowerCase()}.`;

  return (
    <div ref={grid} className="flex min-w-0 flex-col" data-testid="inventory-records" data-selection={selected?.selectionId}>
      {awaiting && selection !== undefined
        ? (
          // The toolbar and the conditions stay over the match, so a finding can still be picked and a condition changed.
          <Card className="gap-0 overflow-hidden rounded-lg p-0" data-testid="inventory-grid">
            {toolbar}
            <SelectionPanel state={selection.state} sentence={selection.sentence} onClear={selection.onClear} />
          </Card>
        )
        : pages.isError
        ? (
          // The findings stay over the problem, so another finding can still be picked.
          <Card className="gap-0 overflow-hidden rounded-lg p-0">
            {toolbar}
            <div className="p-3"><ProblemView error={pages.error} testId="inventory-records-error" /></div>
          </Card>
        )
        : (
          <DimensionGrid
            onNearEnd={more}
            // The copy of an id shows on its row's hover, or while it has the focus, so a page of ids reads as ids.
            className={cn(
              "[&_tbody_tr:not(:hover):not(:focus-within)_[data-testid=inventory-record-copy-id]]:opacity-0",
              // A row stepped to scrolls clear of the headers that stay over the rows.
              "[&_tbody_tr]:scroll-mt-8",
            )}
            testId="inventory-grid"
          >
            <DataTable
              toolbar={toolbar}
              columns={columns}
              rows={rows}
              rowKey={(row) => row.inventoryRecordId}
              selection={!removes ? undefined : {
                selected: everyOne ? new Set((rows ?? []).map((row) => String(row.inventoryRecordId))) : picked,
                onChange: (next) => {
                  setPicked(next);
                  setEveryOne(false);
                },
                // A gone id is picked to go from the inventory and a missing one to be delivered again, neither of which OSDU
                // serves; any other only while OSDU serves it, to go from OSDU.
                isSelectable: (row) => gone || missing || row.goneUtc === undefined,
              }}
              onRowClick={onOpen === undefined ? undefined : (row) => onOpen(row.targetId)}
              rowSx={(row) => (showing && row.targetId === wanted ? OPEN_ROW : undefined)}
              footer={(
                <GridFooter
                  shown={rows?.length ?? 0}
                  total={total}
                  noun={noun}
                  hasMore={pages.hasNextPage}
                  loading={pages.isFetchingNextPage}
                  onMore={more}
                  testId="inventory-records-footer"
                />
              )}
              emptyMessage={emptyMessage}
              data-testid="inventory-records-table"
            />
          </DimensionGrid>
        )}
      {removes && missing && (
        <InventoryRepairDialog
          open={removing}
          onClose={() => setRemoving(false)}
          inventory={inventory}
          ids={everyOne ? null : pickedIds}
          count={removalCount}
          onRepaired={() => {
            clearPicked();
            void queryClient.invalidateQueries({ queryKey: ["delivery", "inventories"] });
          }}
        />
      )}
      {removes && !missing && finding !== null && (
        <RemovalDialog
          open={removing}
          onClose={() => setRemoving(false)}
          inventory={{
            inventory,
            finding,
            ids: everyOne ? null : pickedIds,
            count: removalCount,
            policy: removable,
            selection: everyOne && bySelection && selected !== undefined ? { id: selected.selectionId, sentence: selection?.sentence ?? "" } : undefined,
          }}
          onQueued={(accepted: InventoryRemovalAccepted) => {
            clearPicked();
            toast.success(
              `Removal of ${accepted.expected.toLocaleString("en-US")} ${noun}${accepted.expected === 1 ? "" : "s"} of ${accepted.inventory} queued as a run.`,
              { action: { label: "Open run", onClick: () => navigate(`/runs/${accepted.runId}`) } });
            void queryClient.invalidateQueries({ queryKey: ["delivery", "inventories"] });
          }}
        />
      )}
    </div>
  );
}
