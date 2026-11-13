import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import { Link as RouterLink } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { format } from "date-fns";
import { ArrowRight, Archive, GitCommitHorizontal } from "lucide-react";
import { Card } from "@/components/ui/card";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { cn } from "@/lib/utils";
import { isApiError } from "@/api/client";
import {
  deliveryApi, type DeliveryCacheChange, type DeliveryCacheDiffItem, type DeliveryCacheHistoryEntry, type DeliveryCacheHistoryType,
} from "../../api/delivery";
import { RecordContentView } from "./RecordContentView";
import { CorrelationError } from "@/components/CorrelationError";
import { DataTable, type Column } from "@/components/DataTable";
import { EmptyState } from "@/components/EmptyState";
import { PagedTable } from "@/components/PagedTable";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import { useOwnedPanel } from "@/layout/workbench/useOwnedPanel";
import { parseUtc } from "@/lib/time";
import { cachedFieldsText, cachedText } from "./cacheFormat";
import { CachedRecordId } from "./DeliveryCacheRecords";
import { CachePruneButton } from "./CachePruneButton";
import { versionsText } from "./cacheVersionsKept";
import { ChangeBadge, ChangeCount } from "./ChangeMark";
import { isPruned, shortHash, typeVersions, type CacheTypeVersion } from "./cacheTypeVersions";
import { shortId } from "./idTail";

const ALL = "all";

/** The bottom panel content ids this surface owns. */
const PANEL = "cache-changes:";

type ChangeFilter = DeliveryCacheChange | typeof ALL;

const changeFilters: { value: ChangeFilter; label: string }[] = [
  { value: ALL, label: "All" },
  { value: "changed", label: "Changed" },
  { value: "added", label: "Added" },
  { value: "removed", label: "Removed" },
];

function keyOf(entry: DeliveryCacheHistoryEntry): string {
  return `${entry.version.scope}:${entry.version.version}`;
}

/** How many type chips the versions list shows before it counts the rest. */
const SHOWN_TYPES = 3;

/** One type a version moved and how, as a hover names it. */
function movedText(type: DeliveryCacheHistoryType): string {
  const counts = (["changed", "added", "removed"] as const)
    .filter((kind) => type[kind] > 0)
    .map((kind) => `${type[kind].toLocaleString()} ${kind}`);
  return `${type.name}: ${type.change}${counts.length > 0 ? ` (${counts.join(", ")} records)` : ""}`;
}

/**
 * The types one version moved, each led by the glyph of how (added, changed, removed); the rest are counted, and named
 * on hover. A version that moved no type says so, with why it was written on hover.
 */
function TypesMoved({ entry }: { entry: DeliveryCacheHistoryEntry }) {
  if (entry.types.length === 0) {
    return (
      <RichTooltip
        title="No type changed"
        body={"Every type holds exactly the records and values the version before held. The version was written for a change outside the cached records, such as the partition's OSDU feature flags, so nothing built from these types renders differently."}
      >
        <span className="text-[12px] text-muted-foreground" data-testid="delivery-cache-history-no-type">no type changed</span>
      </RichTooltip>
    );
  }

  const shown = entry.types.slice(0, SHOWN_TYPES);
  const rest = entry.types.length - shown.length;
  return (
    <span className="flex flex-wrap items-center gap-1" data-testid="delivery-cache-history-types">
      {shown.map((type) => (
        <RichTooltip key={type.name} body={movedText(type)} mono>
          <ChangeBadge change={type.change} className="font-mono text-[11px]" data-testid="delivery-cache-history-type">
            {type.name}
          </ChangeBadge>
        </RichTooltip>
      ))}
      {rest > 0 && (
        <RichTooltip title="Also moved" body={entry.types.slice(SHOWN_TYPES).map(movedText).join("\n")} mono>
          <span className="font-mono text-[11px] tabular-nums text-muted-foreground">+{rest}</span>
        </RichTooltip>
      )}
    </span>
  );
}

/** What one version changed, as counts each led by the glyph of its kind. */
function ChangeCounts({ entry }: { entry: DeliveryCacheHistoryEntry }) {
  if (entry.before === null) {
    return (
      <span className="text-[12px] text-muted-foreground">
        first version{entry.added > 0 ? `, ${entry.added.toLocaleString()} records arrived` : ""}
      </span>
    );
  }

  const parts = (["changed", "added", "removed"] as const).filter((kind) => entry[kind] > 0);
  if (parts.length === 0) {
    return <span className="text-[12px] text-muted-foreground">no changes</span>;
  }

  return (
    <span className="inline-flex gap-3">
      {parts.map((kind) => <ChangeCount key={kind} change={kind} count={entry[kind]} />)}
    </span>
  );
}

/** What differs about one record: the values that moved for a changed record, everything it held otherwise. */
function Difference({ row }: { row: DeliveryCacheDiffItem }) {
  if (row.change !== "changed") {
    const values = row.change === "added" ? row.after : row.before;
    return (
      <TruncatedText
        text={cachedFieldsText(values)}
        mono
        maxWidth={480}
        className={row.change === "removed" ? "text-muted-foreground line-through" : undefined}
      />
    );
  }

  if (row.changedFields.length === 0) {
    return <span className="text-[12px] text-muted-foreground">the captured values differ; open the row to see both sides</span>;
  }

  return (
    <span className="flex flex-col gap-0.5 font-mono text-[12px]">
      {row.changedFields.map((name) => (
        <span key={name} className="inline-flex items-center gap-1.5">
          <span className="text-[11px] text-muted-foreground">{name}</span>
          <TruncatedText text={cachedText(row.before?.[name])} mono maxWidth={200} className="text-muted-foreground line-through" />
          <ArrowRight className="size-3.5 shrink-0 text-muted-foreground" />
          <TruncatedText text={cachedText(row.after?.[name])} mono maxWidth={200} />
        </span>
      ))}
    </span>
  );
}

const changeColumn: Column<DeliveryCacheDiffItem> = {
  id: "change",
  header: "Change",
  render: (row) => <ChangeBadge change={row.change} />,
};

const typeColumn: Column<DeliveryCacheDiffItem> = {
  id: "type", header: "Type", render: (row) => <span className="font-mono text-[12px]">{row.typeName}</span>,
};

const recordColumns: Column<DeliveryCacheDiffItem>[] = [
  { id: "recordId", header: "Record", render: (row) => <CachedRecordId id={row.recordId} entityType={row.entityType} maxWidth={300} /> },
  { id: "difference", header: "What differs", render: (row) => <Difference row={row} /> },
];

/** One side of a record in the detail sheet: its captured values, or that the version does not hold it. */
function Side({ title, version, values, testId }: {
  title: string;
  version: string;
  values: Record<string, unknown> | null;
  testId: string;
}) {
  return (
    <div className="flex min-w-0 flex-col gap-2" data-testid={testId}>
      <h3 className="flex items-baseline gap-2 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
        {title}
        <span className="font-mono text-[11px] normal-case tracking-normal text-foreground">{version}</span>
      </h3>
      {values === null
        ? (
          <Card className="gap-0 rounded-lg p-0">
            <EmptyState title="This version does not hold the record" />
          </Card>
        )
        : <RecordContentView value={JSON.stringify(values, null, 2)} language="json" height={420} />}
    </div>
  );
}

/** How many records changed, arrived and left between two versions. */
export interface CacheChangeCounts {
  changed: number;
  added: number;
  removed: number;
}

/**
 * The records that differ between two versions of a partition's cache (of one type, when one is given): filtered by kind
 * of change, searched, and opened record by record with both sides beside each other.
 */
export function CacheChanges({ scope, from, to, type, counts, header, unchanged, testId = "delivery-cache-history" }: {
  scope: string;
  /** The earlier version, read as the before side. */
  from: string;
  /** The later version, read as the after side. */
  to: string;
  type: string | null;
  /** The counts beside each kind of change; undefined while they are fetched. */
  counts: CacheChangeCounts | undefined;
  /** What leads the toolbar, before the search. */
  header?: ReactNode;
  /** What the table says when the two versions hold the same records with the same values. */
  unchanged: string;
  /** The prefix of every test id within. */
  testId?: string;
}) {
  const [change, setChange] = useState<ChangeFilter>(ALL);
  const [search, setSearch] = useState("");
  const [row, setRow] = useState<DeliveryCacheDiffItem | null>(null);

  const count = (filter: ChangeFilter) => (counts === undefined
    ? null
    : (filter === ALL ? counts.changed + counts.added + counts.removed : counts[filter]).toLocaleString());
  const compared = { scope, from, to, type: type ?? undefined };

  return (
    <div className="flex min-h-0 flex-col gap-2">
      <div className="flex flex-col flex-wrap gap-2 sm:flex-row sm:items-center">
        {header}
        <div className="grow" />
        <SearchInput
          value={search}
          onChange={setSearch}
          placeholder="Search ids and cached values"
          label="Search the changes"
          className="sm:w-64"
          testId={`${testId}-search`}
        />
        <ToggleGroup
          type="single"
          variant="outline"
          size="sm"
          value={change}
          onValueChange={(value) => {
            if (value !== "") {
              setChange(value as ChangeFilter);
            }
          }}
          aria-label="Kind of change"
          data-testid={`${testId}-summary`}
        >
          {changeFilters.map((filter) => (
            <ToggleGroupItem
              key={filter.value}
              value={filter.value}
              className="h-8 gap-1.5 text-[13px]"
              data-testid={`${testId}-change-${filter.value}`}
            >
              {filter.label}
              <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{count(filter.value)}</span>
            </ToggleGroupItem>
          ))}
        </ToggleGroup>
      </div>

      <PagedTable
        queryKey={["delivery", "cache", "diff", "items", compared, search, change]}
        fetchPage={(page, pageSize) => deliveryApi
          .cacheDiff({ ...compared, search: search || undefined, change: change === ALL ? undefined : change, page, pageSize })
          .then((result) => result.items)}
        // The type column says nothing a scoped listing does not already say in its title.
        columns={type === null ? [changeColumn, typeColumn, ...recordColumns] : [changeColumn, ...recordColumns]}
        rowKey={(item) => `${item.typeName}:${item.recordId}`}
        onRowClick={setRow}
        emptyMessage={search || change !== ALL ? "No changes match these filters." : unchanged}
        data-testid={`${testId}-table`}
      />

      <Sheet open={row !== null} onOpenChange={(open) => { if (!open) { setRow(null); } }}>
        <SheetContent
          className="w-full gap-0 sm:max-w-4xl"
          onOpenAutoFocus={(event) => { event.preventDefault(); (event.currentTarget as HTMLElement).focus(); }}
          data-testid={`${testId}-record`}
        >
          {row !== null && (
            <>
              <SheetHeader className="border-b border-border">
                <SheetTitle className="flex flex-wrap items-center gap-2">
                  <span className="font-mono">{row.typeName}</span>
                  <ChangeBadge change={row.change} />
                </SheetTitle>
                <SheetDescription className="font-mono text-[12px] text-foreground">{row.recordId}</SheetDescription>
                <p className="text-[12px] text-muted-foreground">{row.entityType}</p>
              </SheetHeader>
              <div className="grid flex-1 gap-4 overflow-y-auto p-4 md:grid-cols-2">
                <Side title="Before" version={from} values={row.before} testId={`${testId}-before`} />
                <Side title="After" version={to} values={row.after} testId={`${testId}-after`} />
              </div>
            </>
          )}
        </SheetContent>
      </Sheet>
    </div>
  );
}

/**
 * An earlier version of the cache (of the type in scope, when one is) compared with the current one, in a dialog over the
 * records: what the refreshes since then changed, added and removed.
 */
export function CacheCompareDialog({ scope, from, to, type, onClose }: {
  scope: string;
  /** The earlier version picked. */
  from: string;
  /** The current version. */
  to: string;
  type: string | null;
  onClose: () => void;
}) {
  const summary = useQuery({
    queryKey: ["delivery", "cache", "diff", "counts", scope, from, to, type],
    queryFn: () => deliveryApi.cacheDiff({ scope, from, to, type: type ?? undefined, pageSize: 1 }),
  });
  const held = type ? `${type} records` : "cached records";

  return (
    <Dialog open onOpenChange={(open) => { if (!open) { onClose(); } }}>
      <DialogContent className="flex max-h-[90vh] flex-col gap-3 overflow-hidden sm:max-w-6xl" data-testid="delivery-cache-compare-dialog">
        <DialogHeader>
          <DialogTitle>
            {type ? <><span className="font-mono">{type}</span> at </> : "Version "}
            <span className="font-mono">{from}</span> compared with the current version
          </DialogTitle>
          <DialogDescription>
            What the refreshes since <span className="font-mono">{from}</span> changed, added and removed among the {held},
            up to <span className="font-mono">{to}</span>, the version deliveries read.
          </DialogDescription>
        </DialogHeader>
        {summary.isError && (isApiError(summary.error)
          ? <CorrelationError error={summary.error} />
          : <p className="text-[13px] text-destructive">{String(summary.error)}</p>)}
        <div className="min-h-0 flex-1 overflow-y-auto">
          <CacheChanges
            scope={scope}
            from={from}
            to={to}
            type={type}
            counts={summary.data}
            unchanged={`Version ${from} holds the same ${held} with the same values as the current version.`}
            testId="delivery-cache-compare"
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}

/** When a version's records were pruned, as the History tab says it. */
function prunedAt(prunedUtc: string): string {
  return format(parseUtc(prunedUtc), "yyyy-MM-dd HH:mm");
}

/** The mark of a version whose records the cache's retention pruned, with when, by whom and what that means on hover. */
function PrunedChip({ prunedUtc, prunedBy }: { prunedUtc: string; prunedBy?: string | null }) {
  return (
    <RichTooltip
      title="Records pruned"
      body={`The cache's retention pruned this version's records at ${prunedAt(prunedUtc)} UTC${prunedBy ? ` by ${prunedBy}` : ""}. The version stays listed with who captured it, when, and what it changed; its records can no longer be read or compared. A delivered record keeps the cached values it was built from.`}
    >
      <span className="rounded-sm bg-muted px-1.5 text-[11px] text-muted-foreground" data-testid="delivery-cache-history-pruned">pruned</span>
    </RichTooltip>
  );
}

/**
 * What the changes panel shows when the records that differ cannot be listed, because the cache's retention pruned the
 * version or the one it is compared with. The counts in the list were recorded before the records went.
 */
function PrunedChanges({ header, version, against }: { header: ReactNode; version: string; against: string | null }) {
  const description = against === null
    ? `The cache's retention pruned the records of ${version}, so the records it changed can no longer be listed. The counts in the list were recorded before they went.`
    : `The cache's retention pruned the records of ${against}, the version it is compared with, so the records that differ can no longer be listed. The counts in the list were recorded before they went.`;
  return (
    <div className="flex flex-col gap-3 p-3" data-testid="delivery-cache-history-detail">
      {header}
      <Card className="gap-0 rounded-lg p-0">
        <EmptyState icon={<Archive />} title="Records pruned" description={description} data-testid="delivery-cache-history-pruned-changes" />
      </Card>
    </div>
  );
}

/**
 * The changes one version made to the cache, compared with the version before it: the bottom panel's content once a
 * version is picked.
 */
function VersionChanges({ entry }: { entry: DeliveryCacheHistoryEntry }) {
  const { version, before } = entry;

  const header = (
    <div className="flex flex-wrap items-baseline gap-x-3 gap-y-0.5">
      <h3 className="text-[13px] font-medium">
        Changes in <span className="font-mono">{version.version}</span>
      </h3>
      {before && (
        <span className="text-[12px] text-muted-foreground">
          compared with <span className="font-mono">{before}</span>, the version captured before it
        </span>
      )}
    </div>
  );

  // The first version has nothing to compare with, whether its records are kept or not.
  if (before === null) {
    return (
      <div className="flex flex-col gap-3 p-3" data-testid="delivery-cache-history-detail">
        {header}
        <Card className="gap-0 rounded-lg p-0">
          <EmptyState
            icon={<GitCommitHorizontal />}
            title="Nothing to compare"
            description={isPruned(version)
              ? "This is the first version of the cache, so there is nothing before it to compare with, and the cache's retention pruned its records. The counts in the list were recorded before they went."
              : "This is the first version of the cache, so there is nothing before it to compare with. Its records are under Records."}
            data-testid="delivery-cache-history-uncomparable"
          />
        </Card>
      </div>
    );
  }

  if (isPruned(version)) {
    return <PrunedChanges header={header} version={version.version} against={null} />;
  }

  if (entry.beforePruned === true) {
    return <PrunedChanges header={header} version={version.version} against={before} />;
  }

  return (
    <div className="flex flex-col p-3" data-testid="delivery-cache-history-detail">
      <CacheChanges
        scope={version.scope}
        from={before}
        to={version.version}
        type={null}
        counts={entry}
        header={header}
        unchanged="This version holds the same cached records with the same values as the one before it."
      />
    </div>
  );
}

/**
 * What one version of a type changed in it, compared with the version of the type before it: the bottom panel's content
 * once a version of the type is picked. The type's hash moved between the two, so there is always something to show.
 */
function TypeVersionChanges({ scope, type, version }: { scope: string; type: string; version: CacheTypeVersion }) {
  const header = (
    <div className="flex flex-wrap items-baseline gap-x-3 gap-y-0.5">
      <h3 className="text-[13px] font-medium">
        Changes to <span className="font-mono">{type}</span> in <span className="font-mono">{version.version}</span>
      </h3>
      {version.previous && (
        <span className="text-[12px] text-muted-foreground">
          compared with <span className="font-mono">{version.previous}</span>,{" "}
          {version.previousOfType ? `the version of ${type} before it` : "the version of the cache before it"}
        </span>
      )}
    </div>
  );

  // The cache's first version has nothing to compare with, whether its records are kept or not.
  if (version.previous === null) {
    return (
      <div className="flex flex-col gap-3 p-3" data-testid="delivery-cache-history-detail">
        {header}
        <Card className="gap-0 rounded-lg p-0">
          <EmptyState
            icon={<GitCommitHorizontal />}
            title="Nothing to compare"
            description={version.pruned
              ? `This is the first version of the cache, so there is nothing before it to compare with, and the cache's retention pruned its ${type} records. The counts in the list were recorded before they went.`
              : `This is the first version of the cache, so there is nothing before it to compare with. Its ${type} records are under Records.`}
            data-testid="delivery-cache-history-uncomparable"
          />
        </Card>
      </div>
    );
  }

  if (version.pruned) {
    return <PrunedChanges header={header} version={version.version} against={null} />;
  }

  if (version.previousPruned) {
    return <PrunedChanges header={header} version={version.version} against={version.previous} />;
  }

  return (
    <div className="flex flex-col p-3" data-testid="delivery-cache-history-detail">
      <CacheChanges
        scope={scope}
        from={version.previous}
        to={version.version}
        type={type}
        counts={version}
        header={header}
        unchanged={`This version holds the same ${type} records with the same values as the version of ${type} before it.`}
      />
    </div>
  );
}

/** Who wrote a version: the run and who asked, or the import from files, and the cache flow. */
function WrittenBy({ entry }: { entry: DeliveryCacheHistoryEntry }) {
  return (
    <span className="flex flex-col">
      {entry.version.runId !== null
        ? (
          <RouterLink
            to={`/runs/${entry.version.runId}`}
            className="font-mono text-[12px] text-primary hover:underline"
            onClick={(event) => event.stopPropagation()}
            data-testid="delivery-cache-history-run"
          >
            run {shortId(entry.version.runId)}
          </RouterLink>
        )
        : <TruncatedText text={entry.version.origin} maxWidth={1200} className="text-[12px]" />}
      <span className="text-[11px] text-muted-foreground">{entry.version.flow} · {entry.version.capturedBy}</span>
    </span>
  );
}

/** A version's label with a dot, marked when it is the one deliveries read. */
function VersionLabel({ label, current, note }: { label: string; current: boolean; note?: ReactNode }) {
  return (
    <span className="inline-flex items-center gap-2">
      <span aria-hidden className={cn("size-2 shrink-0 rounded-full", current ? "bg-primary" : "bg-muted-foreground/40")} />
      <span className="font-mono text-[12.5px]">{label}</span>
      {current && <span className="rounded-sm bg-primary/12 px-1.5 text-[11px] font-medium text-primary">current</span>}
      {note}
    </span>
  );
}

/**
 * One cache's version history: every version, newest first, with what captured it (the run and who asked, or the import
 * from files), the types it moved and what it changed compared with the version captured before it. Picking a version
 * raises its changes in the workbench bottom panel, where they can be filtered, searched and opened record by record while
 * the list stays in view. With a type in scope the list is that type's own versions instead: the versions at which its
 * content hash moved, each compared with the version of the type before it. It covers the whole cache, where Approvals
 * covers only the changes that reach records already delivered.
 */
export function DeliveryCacheHistory({ scope, type, retentionDays, versionsKept }: {
  scope: string;
  type: string | null;
  /** The partition's retention; given, an admin can purge the history from the list, starting from it. */
  retentionDays?: number;
  /** How many versions the partition keeps, the current one counted; given, the list says it. */
  versionsKept?: number;
}) {
  const history = useQuery({
    queryKey: ["delivery", "cache", "history", scope, type],
    queryFn: () => deliveryApi.cacheHistory(scope, type ?? undefined),
  });

  if (history.isPending) {
    return <Skeleton className="h-40 w-full rounded-lg" />;
  }

  if (history.isError) {
    return isApiError(history.error)
      ? <CorrelationError error={history.error} />
      : <p className="text-[13px] text-destructive">{String(history.error)}</p>;
  }

  if (history.data.length === 0) {
    return (
      <Card className="gap-0 rounded-lg p-0">
        <EmptyState
          icon={<GitCommitHorizontal />}
          title="No version captured yet"
          description="Refreshing the cache captures the first one: run its cache flow with the refresh operation."
          data-testid="delivery-cache-history-empty"
        />
      </Card>
    );
  }

  return type === null
    ? (
      <CacheVersionList
        entries={history.data}
        keeps={versionsKept}
        prune={retentionDays === undefined ? null : <CachePruneButton scope={scope} retentionDays={retentionDays} versionsKept={versionsKept} />}
      />
    )
    : <TypeVersionList scope={scope} type={type} entries={history.data} />;
}

/**
 * The raised panel's state for one list: which row is picked, and the panel kept on it with the data as it is now. While
 * the panel shows this surface's content, a scope change or a refetch re-raises it rather than leaving a stale copy open.
 */
function usePickedPanel<T>(rows: T[], keyOf: (row: T) => string, content: (row: T) => { title: string; body: ReactNode }) {
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const { ownedId, show } = useOwnedPanel(PANEL);
  const selected = rows.find((row) => keyOf(row) === selectedKey);

  const raise = useCallback((row: T) => {
    const { title, body } = content(row);
    show(keyOf(row), title, body);
  }, [show, keyOf, content]);

  const open = ownedId !== null;
  useEffect(() => {
    if (open && selected !== undefined) {
      raise(selected);
    }
  }, [open, selected, raise]);

  return {
    highlighted: open ? selectedKey : null,
    pick: (row: T) => {
      setSelectedKey(keyOf(row));
      raise(row);
    },
  };
}

/**
 * Every version of the cache, with the types each moved and what it changed against the version before it, how many the
 * cache keeps, and the purge of the history beside the count, for an admin.
 */
function CacheVersionList({ entries, keeps, prune }: { entries: DeliveryCacheHistoryEntry[]; keeps?: number; prune: ReactNode }) {
  const content = useCallback((entry: DeliveryCacheHistoryEntry) => ({
    title: `Changes · ${entry.version.version}`,
    body: <VersionChanges entry={entry} />,
  }), []);
  const { highlighted, pick } = usePickedPanel(entries, keyOf, content);

  const pruned = entries.filter((entry) => isPruned(entry.version)).length;
  const columns: Column<DeliveryCacheHistoryEntry>[] = [
    {
      id: "version",
      header: "Version",
      render: (entry) => (
        <VersionLabel
          label={entry.version.version}
          current={entry.version.current}
          note={entry.version.prunedUtc ? <PrunedChip prunedUtc={entry.version.prunedUtc} prunedBy={entry.version.prunedBy} /> : undefined}
        />
      ),
    },
    { id: "captured", header: "Captured", render: (entry) => <RelativeTime value={entry.version.capturedUtc} /> },
    { id: "capturedBy", header: "Written by", fill: true, floor: 160, render: (entry) => <WrittenBy entry={entry} /> },
    {
      id: "records",
      header: "Records",
      align: "right",
      render: (entry) => <span className="font-mono tabular-nums">{entry.version.items.toLocaleString()}</span>,
    },
    { id: "types", header: "Types changed", render: (entry) => <TypesMoved entry={entry} /> },
    { id: "changes", header: "Changes", render: (entry) => <ChangeCounts entry={entry} /> },
  ];

  return (
    <div className="flex flex-col gap-2" data-testid="delivery-cache-history">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="text-[12px] text-muted-foreground" data-testid="delivery-cache-history-count">
          {entries.length.toLocaleString()} version{entries.length === 1 ? "" : "s"}
          {pruned > 0 && `, ${pruned.toLocaleString()} of them pruned by the cache's retention`}
          {keeps !== undefined && `; the cache keeps the newest ${versionsText(keeps)}`}. Pick one to see what it changed.
        </span>
        {prune}
      </div>
      <DataTable
        columns={columns}
        rows={entries}
        rowKey={keyOf}
        onRowClick={pick}
        rowSx={(entry) => (keyOf(entry) === highlighted ? { backgroundColor: "var(--accent)" } : undefined)}
        emptyMessage="No versions."
        data-testid="delivery-cache-history-versions"
      />
    </div>
  );
}

function typeVersionKey(version: CacheTypeVersion): string {
  return `${version.entry.version.scope}:${version.version}`;
}

/** A type's content hash, shortened, with the whole on hover; what a version that removed the type or predates hashes shows instead. */
function TypeHash({ version }: { version: CacheTypeVersion }) {
  if (version.change === "removed") {
    return <span className="text-[12px] text-muted-foreground">removed</span>;
  }

  if (version.hash === null) {
    return (
      <RichTooltip title="Content hash" body="Written before types were hashed: the change is read from the records the version wrote.">
        <span className="text-[12px] text-muted-foreground">not recorded</span>
      </RichTooltip>
    );
  }

  return (
    <RichTooltip title="Content hash" body={version.hash} mono>
      <span className="font-mono text-[12px]" data-testid="delivery-cache-type-version-hash">{shortHash(version.hash)}</span>
    </RichTooltip>
  );
}

/** How a version of a type moved it, and how many of its records. */
function TypeChange({ version }: { version: CacheTypeVersion }) {
  const counts = (["changed", "added", "removed"] as const).filter((kind) => version[kind] > 0);
  return (
    <span className="inline-flex items-center gap-2 whitespace-nowrap">
      <ChangeBadge change={version.change} />
      {counts.map((kind) => <ChangeCount key={kind} change={kind} count={version[kind]} />)}
    </span>
  );
}

/**
 * One type's own versions, newest first: the versions of the cache at which the type's content hash moved, each with the
 * hash, how many records of the type it holds, and what it changed against the version of the type before it. The
 * versions of the cache that held the type unchanged are counted and not listed, since comparing them would show nothing.
 */
function TypeVersionList({ scope, type, entries }: { scope: string; type: string; entries: DeliveryCacheHistoryEntry[] }) {
  const versions = useMemo(() => typeVersions(entries, type), [entries, type]);
  const content = useCallback((version: CacheTypeVersion) => ({
    title: `${type} · ${version.version}`,
    body: <TypeVersionChanges scope={scope} type={type} version={version} />,
  }), [scope, type]);
  const { highlighted, pick } = usePickedPanel(versions, typeVersionKey, content);
  const rodeAlong = entries.length - versions.length;

  const columns: Column<CacheTypeVersion>[] = [
    {
      id: "version",
      header: `Version of ${type}`,
      render: (version) => (
        <VersionLabel
          label={version.version}
          current={version.holdsCurrent}
          note={(
            <>
              {version.sameAsCurrent && (
                <RichTooltip body={`This version holds the same ${type} records and values as the current version: its content came back.`}>
                  <span className="rounded-sm bg-muted px-1.5 text-[11px] text-muted-foreground">same as current</span>
                </RichTooltip>
              )}
              {version.entry.version.prunedUtc && <PrunedChip prunedUtc={version.entry.version.prunedUtc} prunedBy={version.entry.version.prunedBy} />}
            </>
          )}
        />
      ),
    },
    { id: "hash", header: "Content hash", render: (version) => <TypeHash version={version} /> },
    { id: "captured", header: "Captured", render: (version) => <RelativeTime value={version.capturedUtc} /> },
    { id: "capturedBy", header: "Written by", fill: true, floor: 160, render: (version) => <WrittenBy entry={version.entry} /> },
    {
      id: "records",
      header: "Records",
      align: "right",
      render: (version) => <span className="font-mono tabular-nums">{version.items.toLocaleString()}</span>,
    },
    { id: "change", header: "Change", render: (version) => <TypeChange version={version} /> },
  ];

  return (
    <div className="flex flex-col gap-2" data-testid="delivery-cache-history">
      <div className="flex flex-wrap items-center gap-2 text-[12px] text-muted-foreground">
        <span data-testid="delivery-cache-type-versions-count">
          {versions.length === 0
            ? `No version changed ${type}.`
            : `${versions.length.toLocaleString()} version${versions.length === 1 ? "" : "s"} of ${type}; pick one to see what it changed.`}
        </span>
        {rodeAlong > 0 && (
          <RichTooltip
            title={`Versions that held ${type} unchanged`}
            body={`The cache writes a version whenever anything in it moves. These ${rodeAlong.toLocaleString()} held ${type} exactly as the version before them did (its content hash did not move), so they are not versions of ${type}, and comparing them would show no change.`}
          >
            <span className="underline decoration-dotted underline-offset-2" data-testid="delivery-cache-type-versions-rode-along">
              {rodeAlong.toLocaleString()} other version{rodeAlong === 1 ? "" : "s"} of the cache held it unchanged
            </span>
          </RichTooltip>
        )}
      </div>
      <DataTable
        columns={columns}
        rows={versions}
        rowKey={typeVersionKey}
        onRowClick={pick}
        rowSx={(version) => (typeVersionKey(version) === highlighted ? { backgroundColor: "var(--accent)" } : undefined)}
        emptyMessage={`No version changed ${type}.`}
        data-testid="delivery-cache-history-versions"
      />
    </div>
  );
}
