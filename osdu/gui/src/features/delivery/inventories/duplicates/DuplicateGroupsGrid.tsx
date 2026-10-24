import { useEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from "react";
import { useInfiniteQuery, useQueryClient, type InfiniteData } from "@tanstack/react-query";
import { GitCompare, Loader2, Pin, Undo2 } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { DataTable, type Column } from "@/components/DataTable";
import { ExplainTip } from "@/components/ExplainTip";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { TruncatedText } from "@/components/TruncatedText";
import { isApiError } from "@/api/client";
import { useOwnedPanel } from "@/layout/workbench/useOwnedPanel";
import { cn } from "@/lib/utils";
import {
  inventoryApi, type DuplicateCopy, type DuplicateGroup, type DuplicateGroupPage, type Inventory, type InventoryDuplicateCheck,
} from "../../../../api/inventories";
import { RecordName } from "../../RecordName";
import { ProblemView } from "../../TemplateSheet";
import { DimensionGrid, GridFooter, GridViewMenu } from "../../dimensions/DimensionGrid";
import { useHiddenColumns, type GridColumnChoice } from "../../dimensions/dimensionGridState";
import { fieldLabel } from "../../explorer/explorerModel";
import { isRecordReference } from "../../osduDocument";
import { idParts } from "../../osduRecordModel";
import { FindingGlyph } from "../InventoryBadges";
import { InventoryIdPanel } from "../InventoryIdPanel";
import { SelectionValueCell } from "../InventorySelectionParts";
import { findingVisual, findingWhy } from "../inventoryFormat";
import { DuplicateCompareDialog } from "./DuplicateCompareDialog";
import { outcomeVisual, roleVisual } from "./duplicateModel";

/** The groups a page of the grid reads. */
const PAGE = 50;

/** The copies of a group too broad to pick from that one ask lists. */
const BROAD_PAGE = 100;

/** The key's properties the grid shows a column of at first; the rest are a click away under View. */
const KEY_COLUMNS_SHOWN = 4;

/** The workbench's bottom panel content this grid raises: an OSDU id after this prefix. */
const PANEL = "inventory-duplicate:";

/** The row whose copy is open in the panel. */
const OPEN_ROW: CSSProperties = { backgroundColor: "var(--accent)" };

/** One row of the grid: a copy of its group, or the line standing for the copies of a group too broad to list with it. */
interface GridRow {
  key: string;
  group: DuplicateGroup;
  copy?: DuplicateCopy;
}

/** What a control plane refusal says, in its own words. */
function said(error: unknown): string {
  return isApiError(error) ? error.detail ?? error.title : String(error);
}

/** What the copies of a group share at the key, read off its first copy: each property's values, joined, as a line's title says it. */
function sharedText(group: DuplicateGroup, paths: readonly string[]): string {
  const values = group.values ?? {};
  const parts = paths.map((path) => values[path]?.join(", ")).filter((part): part is string => part !== undefined && part !== "");
  return parts.length === 0 ? "(no value at the key)" : parts.join(" · ");
}

/**
 * What the copies of a group share at the key, as its node shows it: each property's values in turn, an OSDU id named as a
 * reader knows it (its type and the unique part), separated by a quiet dot; the whole line on hover.
 */
function SharedValues({ group, paths }: { group: DuplicateGroup; paths: readonly string[] }) {
  const values = group.values ?? {};
  const parts = paths.map((path) => ({ path, held: values[path] ?? [] })).filter((part) => part.held.length > 0);
  if (parts.length === 0) {
    return <span className="text-muted-foreground">(no value at the key)</span>;
  }

  return (
    <span className="flex min-w-0 items-center gap-1.5 overflow-hidden" title={sharedText(group, paths)} data-testid="duplicate-group-values">
      {parts.map((part, index) => (
        <span key={part.path} className="flex min-w-0 items-center gap-1.5">
          {index > 0 && <span className="text-muted-foreground/50" aria-hidden>·</span>}
          {part.held.length === 1 && isRecordReference(part.held[0])
            ? <RecordName id={part.held[0]} className="min-w-0 text-[12.5px]" />
            : <span className="min-w-0 truncate font-medium">{part.held.join(", ")}</span>}
        </span>
      ))}
    </span>
  );
}

/** A copy's role, as a glyph in its tone and the word, why it has it on hover. */
function RoleCell({ copy }: { copy: DuplicateCopy }) {
  const visual = roleVisual(copy.role);
  const Icon = visual.icon;
  return (
    <ExplainTip title={visual.label} text={copy.reason === undefined ? visual.hint : `${copy.reason}\n\n${visual.hint}`} testId="duplicate-copy-why">
      <button
        type="button"
        className="inline-flex items-center gap-1 whitespace-nowrap rounded-sm text-[12px] outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
        data-testid="duplicate-copy-role"
        data-role={copy.role ?? "none"}
        onClick={(event) => event.stopPropagation()}
      >
        <Icon className={cn("size-3.5 shrink-0", visual.tone)} aria-hidden />
        {visual.label}
      </button>
    </ExplainTip>
  );
}

/** How complete a copy is: how many values it holds, with a bar against the fullest copy of its group, the copy kept's in the primary tone. */
function CompletenessCell({ copy, most }: { copy: DuplicateCopy; most: number }) {
  if (copy.attributes === undefined) {
    return <span className="text-[12px] text-muted-foreground">-</span>;
  }

  const share = most <= 0 ? 0 : Math.max(4, Math.round((copy.attributes / most) * 100));
  return (
    <RichTooltip body={`It holds ${copy.attributes.toLocaleString("en-US")} values under data, each item of a list counted on its own; the fullest copy of the group holds ${most.toLocaleString("en-US")}.`}>
      <span className="inline-flex items-center justify-end gap-1.5" data-testid="duplicate-copy-attributes">
        <span className="h-1.5 w-12 overflow-hidden rounded-full bg-muted" aria-hidden>
          <span className={cn("block h-full rounded-full", copy.role === "keep" ? "bg-primary" : "bg-muted-foreground/40")} style={{ width: `${share}%` }} />
        </span>
        <span className="w-10 text-right font-mono text-[12px] tabular-nums">{copy.attributes.toLocaleString("en-US")}</span>
      </span>
    </RichTooltip>
  );
}

/**
 * The groups of a completed duplicate check (osdu/docs/reference/flow/inventory-duplicates.md), the largest
 * first, in a grid that fits the window and scrolls in place, a page of groups at a time. Each group is a node of the grid's
 * tree: how it came out (its reason on hover), what its copies share at the key, how many there are, the copy it keeps and who
 * chose it; under it its copies, each with its role (why on hover), its finding, what it holds at the key's properties, how
 * complete it is against the fullest copy, how many records name it, its version and who created it. A copy opens in the
 * workbench's bottom panel, where its values' properties can be added to the key being built. Where the reader may act, a
 * copy can be kept (the group judged again with it kept, and recorded as their choice) and a choice taken back; two copies of
 * a group, ticked or the two fullest, are compared side by side. A group too broad to pick from lists its copies on asking.
 */
export function DuplicateGroupsGrid({ inventory, check, outcome, total, leading, canChoose, openId, onOpenId }: {
  inventory: Inventory;
  check: InventoryDuplicateCheck;
  /** The outcome whose groups are listed; null lists every group. */
  outcome: string | null;
  /** How many groups the outcome holds, as the check counts them; null when unknown. */
  total: number | null;
  /** What leads the grid's toolbar: the outcomes to pick from. */
  leading: ReactNode;
  /** Whether the reader may choose the copy a group keeps: the check completed against the inventory's last build. */
  canChoose: boolean;
  openId: string | null;
  onOpenId?: (id: string | null) => void;
}) {
  const queryClient = useQueryClient();
  const paths = check.key.map((p) => p.path);
  const groupsKey = useMemo(
    () => ["delivery", "inventories", "duplicates", "groups", inventory.partition, inventory.inventoryId, check.checkId, outcome] as const,
    [inventory.partition, inventory.inventoryId, check.checkId, outcome]);
  const pages = useInfiniteQuery({
    queryKey: groupsKey,
    queryFn: ({ pageParam }) => inventoryApi.duplicateGroups(inventory.partition, inventory.inventoryId, check.checkId, { outcome: outcome ?? undefined, after: pageParam, limit: PAGE }),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: (last) => last.next,
  });
  const more = () => {
    if (pages.hasNextPage && !pages.isFetchingNextPage) {
      void pages.fetchNextPage();
    }
  };

  // The copies of the groups too broad to list with their page, read on asking, a page at a time.
  const [listed, setListed] = useState<Readonly<Record<number, { copies: DuplicateCopy[]; next?: number; loading: boolean }>>>({});
  const listCopies = async (group: DuplicateGroup) => {
    const had = listed[group.groupNumber];
    setListed((was) => ({ ...was, [group.groupNumber]: { copies: had?.copies ?? [], next: had?.next, loading: true } }));
    try {
      const page = await inventoryApi.duplicateCopies(inventory.partition, inventory.inventoryId, check.checkId, group.groupNumber, { after: had?.next, limit: BROAD_PAGE });
      setListed((was) => ({ ...was, [group.groupNumber]: { copies: [...(had?.copies ?? []), ...page.items], next: page.next, loading: false } }));
    } catch (error) {
      setListed((was) => ({ ...was, [group.groupNumber]: { copies: had?.copies ?? [], next: had?.next, loading: false } }));
      toast.error(`The copies of group ${group.groupNumber} could not be read: ${said(error)}`);
    }
  };

  const groups = useMemo(() => pages.data?.pages.flatMap((page) => page.items), [pages.data]);
  const rows = useMemo<GridRow[] | undefined>(() => groups?.flatMap((group) => {
    const copies = group.members ?? listed[group.groupNumber]?.copies ?? [];
    const own = copies.map((copy) => ({ key: `${group.groupNumber}:${copy.record.inventoryRecordId}`, group, copy }));
    return group.members === undefined ? [{ key: `${group.groupNumber}:broad`, group }, ...own] : own;
  }), [groups, listed]);
  const copies = useMemo(() => (rows ?? []).filter((row) => row.copy !== undefined), [rows]);

  // The copies ticked for a comparison, by their group: a tick in another group starts afresh, since only copies of one group compare.
  const [ticked, setTicked] = useState<{ group: number; rows: number[] } | null>(null);
  const [comparing, setComparing] = useState<{ group: DuplicateGroup; pair: [DuplicateCopy, DuplicateCopy] } | null>(null);
  const tick = (group: DuplicateGroup, copy: DuplicateCopy, on: boolean) => {
    const row = copy.record.inventoryRecordId;
    setTicked((was) => {
      const kept = was !== null && was.group === group.groupNumber ? was.rows.filter((r) => r !== row) : [];
      const next = on ? [...kept, row].slice(-2) : kept;
      return next.length === 0 ? null : { group: group.groupNumber, rows: next };
    });
  };

  /** The pair a group's Compare compares: the two copies ticked, or else the copy kept and the fullest beside it, or the two fullest. */
  const pairOf = (group: DuplicateGroup): [DuplicateCopy, DuplicateCopy] | null => {
    const held = (group.members ?? listed[group.groupNumber]?.copies ?? []).filter((c) => c.storageState === "held");
    if (ticked !== null && ticked.group === group.groupNumber && ticked.rows.length === 2) {
      const pair = ticked.rows.map((row) => held.find((c) => c.record.inventoryRecordId === row)).filter((c): c is DuplicateCopy => c !== undefined);
      if (pair.length === 2) {
        return [pair[0], pair[1]];
      }
    }

    const fullest = [...held].sort((a, b) => (b.role === "keep" ? 1 : 0) - (a.role === "keep" ? 1 : 0) || (b.attributes ?? 0) - (a.attributes ?? 0));
    return fullest.length >= 2 ? [fullest[0], fullest[1]] : null;
  };

  // A choice written comes back as the group it made: the page holding it shows it at once, and the check's counts are read again.
  const [choosing, setChoosing] = useState<number | null>(null);
  const choose = async (group: DuplicateGroup, copy: DuplicateCopy | null) => {
    setChoosing(group.groupNumber);
    try {
      const written = await inventoryApi.keepDuplicate(inventory.partition, inventory.inventoryId, check.checkId, group.groupNumber, copy?.record.inventoryRecordId ?? null);
      queryClient.setQueryData<InfiniteData<DuplicateGroupPage, number | undefined>>(groupsKey, (was) => (was === undefined
        ? was
        : { ...was, pages: was.pages.map((page) => ({ ...page, items: page.items.map((g) => (g.groupNumber === written.groupNumber ? written : g)) })) }));
      void queryClient.invalidateQueries({ queryKey: ["delivery", "inventories", "duplicates", "check", inventory.partition, inventory.inventoryId, check.checkId] });
      void queryClient.invalidateQueries({ queryKey: ["delivery", "inventories", "duplicates", "groups", inventory.partition, inventory.inventoryId, check.checkId], refetchType: "none" });
      toast.success(copy === null
        ? `Group ${group.groupNumber} is the check's to pick again: ${outcomeVisual(written.outcome).label.toLowerCase()}.`
        : `Group ${group.groupNumber} keeps ${idParts(copy.record.targetId).unique || copy.record.targetId}.`);
    } catch (error) {
      toast.error(`The choice for group ${group.groupNumber} was not written: ${said(error)}`);
    } finally {
      setChoosing(null);
    }
  };

  // The copy open in the address is raised in the panel, stepping through the copies the grid lists.
  const { ownedId, show, close } = useOwnedPanel(PANEL);
  const wanted = openId !== null && onOpenId !== undefined ? openId : null;
  const at = wanted === null ? -1 : copies.findIndex((row) => row.copy!.record.targetId === wanted);
  const latest = useRef<{ step: (by: -1 | 1) => void; open: (id: string | null) => void; openId: string | null }>({ step: () => undefined, open: () => undefined, openId });
  useEffect(() => {
    latest.current = {
      step: (by) => {
        const next = at + by;
        if (at >= 0 && next >= 0 && next < copies.length) {
          onOpenId?.(copies[next].copy!.record.targetId);
        }
      },
      open: (id) => onOpenId?.(id),
      openId,
    };
  });
  const showing = ownedId !== null;
  const showingNow = useRef(showing);
  useEffect(() => {
    showingNow.current = showing;
  });
  const opened = at >= 0 ? copies[at].copy!.record : undefined;
  useEffect(() => {
    if (wanted === null) {
      if (showingNow.current) {
        close();
      }

      return;
    }

    show(wanted, `Copy · group ${at >= 0 ? copies[at].group.groupNumber : "?"}`, (
      <InventoryIdPanel
        partition={inventory.partition}
        id={wanted}
        record={opened}
        place={at >= 0 ? { at: at + 1, of: copies.length } : null}
        canBack={at > 0}
        canForward={at >= 0 && at < copies.length - 1}
        onStep={(by) => latest.current.step(by)}
        onOpenId={(id) => latest.current.open(id)}
      />
    ));
  }, [wanted, opened, at, copies, inventory.partition, show, close]);
  const wasShowing = useRef(false);
  useEffect(() => {
    if (wasShowing.current && !showing && latest.current.openId !== null) {
      latest.current.open(null);
    }

    wasShowing.current = showing;
  }, [showing]);

  const valueChoices = paths.map((path) => ({ id: `value:${path}`, label: fieldLabel(path) }));
  const choices: GridColumnChoice[] = [
    ...valueChoices,
    { id: "finding", label: "Finding" },
    { id: "named", label: "Named by" },
    { id: "version", label: "Version" },
    { id: "created", label: "Created by" },
  ];
  // A property compared exactly holds one value in every copy of a group, which its node shows already: its column starts left
  // out, as the version does; a property compared loosely shows how each copy spells it.
  const atFirst = [
    ...check.key.filter((p, index) => p.compare === "exact" || index >= KEY_COLUMNS_SHOWN).map((p) => `value:${p.path}`),
    "version",
  ];
  const [hidden, toggleColumn] = useHiddenColumns(`osdu.inventories.duplicates.columns.${paths.join(",")}`, atFirst);

  const all: Column<GridRow>[] = [
    {
      id: "copy",
      header: "Copy",
      fill: true,
      floor: 260,
      render: (row) => {
        if (row.copy === undefined) {
          const had = listed[row.group.groupNumber];
          return (
            <span className="flex min-w-0 items-center gap-2 text-[12.5px] text-muted-foreground" data-testid="duplicate-broad">
              <span className="truncate">{`${row.group.copies.toLocaleString("en-US")} copies share this key: too broad to be one record written more than once, so the check read none of them.`}</span>
              {(had === undefined || had.next !== undefined) && (
                <Button
                  variant="link"
                  size="sm"
                  className="h-6 shrink-0 px-0 text-[12.5px]"
                  disabled={had?.loading === true}
                  onClick={(event) => { event.stopPropagation(); void listCopies(row.group); }}
                  data-testid="duplicate-broad-list"
                >
                  {had?.loading === true && <Loader2 className="animate-spin" />}
                  {had === undefined ? `List the first ${BROAD_PAGE}` : "List more"}
                </Button>
              )}
            </span>
          );
        }

        const copy = row.copy;
        const comparable = copy.storageState === "held";
        const isTicked = ticked !== null && ticked.group === row.group.groupNumber && ticked.rows.includes(copy.record.inventoryRecordId);
        return (
          <span className="flex min-w-0 items-center gap-2">
            <span onClick={(event) => event.stopPropagation()} className="inline-flex">
              <Checkbox
                checked={isTicked}
                disabled={!comparable}
                onCheckedChange={(on) => tick(row.group, copy, on === true)}
                aria-label={comparable ? "Tick to compare with another copy of the group" : "Storage no longer holds it, so it cannot be compared"}
                data-testid="duplicate-copy-tick"
              />
            </span>
            <RoleCell copy={copy} />
            <RecordName id={copy.record.targetId} kind={copy.record.kind} copy className="min-w-0 text-[12.5px]" testId="duplicate-copy-id" copyTestId="duplicate-copy-copy-id" />
          </span>
        );
      },
    },
    ...paths.map((path): Column<GridRow> => ({
      id: `value:${path}`,
      header: fieldLabel(path),
      render: (row) => (row.copy === undefined ? null : <SelectionValueCell values={row.copy.values?.[path]} testId="duplicate-copy-value" />),
    })),
    {
      id: "finding",
      header: "Finding",
      render: (row) => (row.copy === undefined ? null : (
        <ExplainTip title={`Why ${findingVisual(row.copy.record.finding).label.toLowerCase()}`} text={findingWhy(row.copy.record)} testId="duplicate-copy-finding-why">
          <span className="inline-flex items-center gap-1 whitespace-nowrap text-[12px]" data-testid="duplicate-copy-finding" data-finding={row.copy.record.finding}>
            <FindingGlyph finding={row.copy.record.finding} />
            {findingVisual(row.copy.record.finding).label.toLowerCase()}
          </span>
        </ExplainTip>
      )),
    },
    {
      id: "values",
      header: "Values",
      align: "right",
      render: (row) => {
        if (row.copy === undefined) {
          return null;
        }

        const most = Math.max(0, ...(row.group.members ?? []).map((c) => c.attributes ?? 0));
        return <CompletenessCell copy={row.copy} most={most} />;
      },
    },
    {
      id: "named",
      header: "Named by",
      align: "right",
      render: (row) => (row.copy === undefined || row.copy.mentionedBy === undefined ? null : (
        <RichTooltip body={row.copy.mentionedBy === 0 ? "No record names it." : `${row.copy.mentionedBy.toLocaleString("en-US")} record(s) name it: removing it would leave them pointing at nothing.`}>
          <span className={cn("font-mono text-[12px] tabular-nums", row.copy.mentionedBy === 0 && "text-muted-foreground")} data-testid="duplicate-copy-named">
            {row.copy.mentionedBy.toLocaleString("en-US")}
          </span>
        </RichTooltip>
      )),
    },
    {
      id: "version",
      header: "Version",
      align: "right",
      render: (row) => {
        if (row.copy === undefined) {
          return null;
        }

        const version = row.copy.storedVersion ?? row.copy.indexedVersion ?? row.copy.record.version;
        return version === undefined ? <span className="text-[12px] text-muted-foreground">-</span> : <span className="font-mono text-[12px] tabular-nums">{version}</span>;
      },
    },
    {
      id: "created",
      header: "Created by",
      render: (row) => {
        const created = row.copy?.record;
        if (created?.createUser === undefined) {
          return null;
        }

        return created.createTime === undefined
          ? <TruncatedText text={created.createUser} mono maxWidth={170} />
          : (
            <RichTooltip title="Created" body={`${created.createUser}\n${new Date(created.createTime).toUTCString()}`} mono>
              <span className="inline-flex min-w-0 max-w-[170px]" data-testid="duplicate-copy-created">
                <span className="min-w-0 truncate font-mono text-[12px]">{created.createUser}</span>
              </span>
            </RichTooltip>
          );
      },
    },
    {
      id: "act",
      header: "",
      align: "right",
      render: (row) => {
        const copy = row.copy;
        if (copy === undefined || !canChoose || row.group.members === undefined || copy.storageState !== "held" || copy.role === "keep") {
          return null;
        }

        return (
          <Button
            variant="ghost"
            size="sm"
            // A group left for review asks for its copy to be chosen, so the way to keep one stands on every copy; a decided group
            // offers it on the row's hover, as a change of mind.
            className={cn(
              "h-6 gap-1 px-1.5 text-[12px] text-muted-foreground hover:text-foreground",
              row.group.outcome !== "review" && "opacity-0 transition-opacity group-hover/row:opacity-100 focus-visible:opacity-100",
            )}
            disabled={choosing === row.group.groupNumber}
            onClick={(event) => { event.stopPropagation(); void choose(row.group, copy); }}
            title="Keep this copy: the group is judged again with it kept, and the others the flow may remove are picked to remove"
            data-testid="duplicate-copy-keep"
          >
            <Pin className="size-3.5" />
            Keep
          </Button>
        );
      },
    },
  ];
  const columns = all.filter((column) => !hidden.has(column.id));

  const header = (cluster: GridRow[]): ReactNode => {
    const group = cluster[0].group;
    const visual = outcomeVisual(group.outcome);
    const Icon = visual.icon;
    const kept = (group.members ?? []).find((c) => c.record.inventoryRecordId === group.keptInventoryRecordId);
    const pair = pairOf(group);
    const tickedHere = ticked !== null && ticked.group === group.groupNumber ? ticked.rows.length : 0;
    return (
      // Width zero and growing: the node's line never widens the grid, whatever its reason says; it is cut to the grid instead.
      <span className="flex w-0 min-w-0 flex-1 items-center gap-2 overflow-hidden text-[12.5px]" data-testid="duplicate-group" data-group={group.groupNumber} data-outcome={group.outcome}>
        <span className="shrink-0 font-mono text-[11.5px] tabular-nums text-muted-foreground">{`#${group.groupNumber}`}</span>
        <ExplainTip title={visual.label} text={group.reason === undefined ? visual.hint : `${group.reason}\n\n${visual.hint}`} testId="duplicate-group-why">
          <button
            type="button"
            className="inline-flex shrink-0 items-center gap-1 rounded-md border border-border bg-muted/60 px-1.5 py-px text-[11.5px] outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
            onClick={(event) => event.stopPropagation()}
            data-testid="duplicate-group-outcome"
          >
            <Icon className={cn("size-3.5", visual.tone)} aria-hidden />
            {visual.label}
          </button>
        </ExplainTip>
        <span className="min-w-0 max-w-[30rem] shrink"><SharedValues group={group} paths={paths} /></span>
        <span className="shrink-0 text-muted-foreground">{`${group.copies.toLocaleString("en-US")} copies`}</span>
        {kept !== undefined && (
          <span className="flex min-w-0 shrink items-center gap-1 text-muted-foreground">
            <span className="shrink-0">keeps</span>
            <span className="min-w-0 truncate font-mono text-[11.5px] text-foreground">{idParts(kept.record.targetId).unique || kept.record.targetId}</span>
          </span>
        )}
        {group.reason !== undefined && group.outcome !== "picked" && group.outcome !== "chosen" && (
          <span className="min-w-0 flex-1 truncate text-[12px] text-muted-foreground" title={group.reason}>{group.reason}</span>
        )}
        {group.chosenBy !== undefined && (
          <span className="shrink-0 text-[12px] text-muted-foreground">
            {`chosen by ${group.chosenBy} `}
            {group.chosenUtc !== undefined && <RelativeTime value={group.chosenUtc} absolute={false} />}
          </span>
        )}
        <span className="ml-auto flex shrink-0 items-center gap-0.5" onClick={(event) => event.stopPropagation()}>
          {pair !== null && (
            <Button
              variant="ghost"
              size="sm"
              className="h-6 gap-1 px-1.5 text-[12px] text-muted-foreground hover:text-foreground"
              onClick={() => setComparing({ group, pair })}
              title={tickedHere === 2 ? "Compare the two copies ticked" : "Compare the copy kept with the fullest beside it, or tick two copies to compare them"}
              data-testid="duplicate-group-compare"
            >
              <GitCompare className="size-3.5" />
              {tickedHere === 2 ? "Compare the two ticked" : "Compare"}
            </Button>
          )}
          {canChoose && group.outcome === "chosen" && (
            <Button
              variant="ghost"
              size="sm"
              className="h-6 gap-1 px-1.5 text-[12px] text-muted-foreground hover:text-foreground"
              disabled={choosing === group.groupNumber}
              onClick={() => void choose(group, null)}
              title="Take the choice back: the check's own rules judge the group again"
              data-testid="duplicate-group-unchoose"
            >
              <Undo2 className="size-3.5" />
              Use the check's pick
            </Button>
          )}
        </span>
      </span>
    );
  };

  const noun = "group";
  const toolbar = (
    <div className="flex min-w-0 items-center gap-1 border-b border-border pl-1.5 pr-2">
      {leading}
      <div className="ml-auto flex shrink-0 items-center gap-0.5">
        <GridViewMenu columns={choices} hidden={hidden} atFirst={atFirst} onToggle={toggleColumn} compact testId="duplicate-groups-view" />
      </div>
    </div>
  );

  return (
    <div className="flex min-w-0 flex-col" data-testid="duplicate-groups">
      {pages.isError
        ? (
          <div className="flex flex-col overflow-hidden rounded-lg border border-border">
            {toolbar}
            <div className="p-3"><ProblemView error={pages.error} testId="duplicate-groups-error" /></div>
          </div>
        )
        : (
          <DimensionGrid onNearEnd={more} className="[&_tbody_tr:not(:hover):not(:focus-within)_[data-testid=duplicate-copy-copy-id]]:opacity-0" testId="duplicate-grid">
            <DataTable
              toolbar={toolbar}
              columns={columns}
              rows={rows}
              rowKey={(row) => row.key}
              grouping={{ levels: [{ key: (row) => String(row.group.groupNumber), renderHeader: header }] }}
              onRowClick={onOpenId === undefined ? undefined : (row) => { if (row.copy !== undefined) onOpenId(row.copy.record.targetId); }}
              rowClickable={(row) => row.copy !== undefined}
              rowSx={(row) => (showing && row.copy !== undefined && row.copy.record.targetId === wanted ? OPEN_ROW : undefined)}
              footer={<GridFooter shown={groups?.length ?? 0} total={total} noun={noun} hasMore={pages.hasNextPage} loading={pages.isFetchingNextPage} onMore={more} testId="duplicate-groups-footer" />}
              emptyMessage={outcome === null ? "No two ids of the inventory share the key." : `No group is ${outcomeVisual(outcome).label.toLowerCase()}.`}
              data-testid="duplicate-groups-table"
            />
          </DimensionGrid>
        )}
      {comparing !== null && (
        <DuplicateCompareDialog open onOpenChange={(open) => { if (!open) setComparing(null); }} partition={inventory.partition} group={comparing.group} pair={comparing.pair} />
      )}
    </div>
  );
}
