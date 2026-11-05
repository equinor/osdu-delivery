import { useMemo, useState } from "react";
import { useQueries, useQuery } from "@tanstack/react-query";
import { format } from "date-fns";
import { ArrowRight, BookOpen, DatabaseZap, GitCompare, History, Pin, Search, Shapes, ShieldCheck, Table2 } from "lucide-react";
import { Link } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { isApiError } from "@/api/client";
import { deliveryApi, type DeliveryCacheTypeSource, type DeliveryCacheVersion, type DeliveryCachedItem } from "../../api/delivery";
import { RecordContentView } from "./RecordContentView";
import { CopyButton } from "@/components/CopyButton";
import { CorrelationError } from "@/components/CorrelationError";
import { DataTable } from "@/components/DataTable";
import { DetailPair } from "@/components/DetailPair";
import { EmptyState } from "@/components/EmptyState";
import { LineageJumpButton } from "@/components/LineageJumpButton";
import { PagedTable, type Column } from "@/components/PagedTable";
import { RichTooltip } from "@/components/RichTooltip";
import { SearchInput } from "@/components/SearchInput";
import { StatePill } from "@/components/StatusBadge";
import { TruncatedText } from "@/components/TruncatedText";
import { useClipped } from "@/components/useClipped";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { parseUtc } from "@/lib/time";
import { RecordMappingReference } from "./CacheMappingReference";
import { CacheTypeRail } from "./CacheTypeRail";
import { CachedValueText } from "./CachedValueText";
import { KindText } from "./KindText";
import { cacheTypeLineageTarget } from "./lineageTargets";
import { cachedCell, isLookupEntityType, recordIdentity, splitRecordId, type CachedTypeSummary } from "./cacheFormat";
import { SECTION_ROWS, browsedTypes, columnNames, typeSampleQuery, type BrowsedType } from "./cacheRecordsModel";
import { earlierTypeVersions, isPruned, shortHash, typeVersions, type CacheTypeVersion } from "./cacheTypeVersions";
import { useNearViewport } from "./useNearViewport";

/** The picker's value for "whichever version is current", which is what the records open on. */
export const CURRENT = "current";

/** How long a typed search rests before it is asked: every type is counted for it, so a keystroke is not a request. */
const SEARCH_DELAY_MS = 300;

/**
 * An OSDU record id with its repeated part stepped back: the partition and entity type in muted text, the part that
 * tells records apart in full. The prefix gives way first when the cell runs out of room, so a column of ids stays
 * readable by its tails, and the whole id is revealed on hover only when something is actually hidden.
 */
export function RecordId({ id, maxWidth = 360, tailOnly = false }: {
  id: string;
  maxWidth?: number;
  /** Shows only the part that tells records apart, for a row that already names the type; the whole id is on hover. */
  tailOnly?: boolean;
}) {
  const { prefix, tail } = splitRecordId(id);
  const [prefixRef, prefixClipped] = useClipped(prefix);
  const [tailRef, tailClipped] = useClipped(tail);

  if (tailOnly) {
    return (
      <RichTooltip body={id} mono>
        <span className="inline-block truncate align-bottom font-mono text-[12px]" style={{ maxWidth }}>{tail}</span>
      </RichTooltip>
    );
  }

  const body = (
    <span className="inline-flex max-w-full items-baseline align-bottom font-mono text-[12px]" style={{ maxWidth }}>
      {prefix !== "" && (
        <span ref={prefixRef} className="min-w-0 shrink-[1000] truncate text-muted-foreground">{prefix}</span>
      )}
      <span ref={tailRef} className="min-w-0 truncate">{tail}</span>
    </span>
  );

  return prefixClipped || tailClipped ? <RichTooltip body={id} mono>{body}</RichTooltip> : body;
}

/**
 * A cached record's identity as its type keeps it: a lookup row's key whole, since it has no repeated prefix to step back,
 * and an OSDU record's id with its partition and entity type stepped back.
 */
export function CachedRecordId({ id, entityType, maxWidth = 360, tailOnly = false }: {
  id: string;
  entityType: string;
  maxWidth?: number;
  tailOnly?: boolean;
}) {
  return isLookupEntityType(entityType)
    ? <TruncatedText text={id} mono maxWidth={maxWidth} />
    : <RecordId id={id} maxWidth={maxWidth} tailOnly={tailOnly} />;
}

/**
 * What a version picker says of the versions it leaves out because the cache's retention pruned their records: how many,
 * and that History still lists them.
 */
function PrunedNote({ count }: { count: number }) {
  if (count === 0) {
    return null;
  }

  return (
    <div className="border-t border-border px-2 py-1.5 text-[12px] text-muted-foreground" data-testid="delivery-cache-version-pruned">
      {count.toLocaleString()} earlier version{count === 1 ? "" : "s"} pruned by the cache's retention: listed under History, no
      longer readable
    </div>
  );
}

/**
 * The version picker: the current version by default, or any version of the cache whose records are kept, by label, with
 * when it was captured. A version the cache's retention pruned cannot be read, so it is counted rather than offered.
 */
export function CacheVersionPicker({ versions, value, onChange, className }: {
  versions: DeliveryCacheVersion[];
  value: string;
  onChange: (version: string) => void;
  className?: string;
}) {
  const readable = versions.filter((option) => !isPruned(option));
  return (
    <Select value={value} onValueChange={onChange} disabled={versions.length === 0}>
      <SelectTrigger
        size="sm"
        // The trigger centres its text as any button does; a picker reads left to right like the search beside it.
        className={cn("h-8 text-left *:data-[slot=select-value]:flex-1 *:data-[slot=select-value]:justify-start", className)}
        active={value !== CURRENT}
        aria-label="Version to read"
        data-testid="delivery-cache-version"
      >
        <History />
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value={CURRENT}>Current version</SelectItem>
        {readable.map((option) => (
          <SelectItem key={option.version} value={option.version}>
            <span className="font-mono text-[12px]">{option.version}</span>
            <span className="text-[11px] text-muted-foreground">{format(parseUtc(option.capturedUtc), "MMM d, HH:mm")}</span>
            {option.current && <span className="text-[11px] font-medium text-primary">current</span>}
          </SelectItem>
        ))}
        <PrunedNote count={versions.length - readable.length} />
      </SelectContent>
    </Select>
  );
}

/**
 * The version picker for one type: the current version, and the type's own earlier versions, the versions of the cache
 * at which its content hash moved. The newest of those holds what the current version holds, so Current version stands
 * for it; any version picked here holds other records or values of the type than the current one, so comparing the two
 * shows a change, except for a version marked the same as current, whose content came back.
 */
export function TypeVersionPicker({ type, versions, value, onChange, className }: {
  type: string;
  /** The type's versions, newest first. */
  versions: CacheTypeVersion[];
  value: string;
  onChange: (version: string) => void;
  className?: string;
}) {
  const head = versions.find((candidate) => candidate.holdsCurrent);
  const all = earlierTypeVersions(versions);
  const earlier = all.filter((candidate) => !candidate.pruned);
  return (
    <Select value={value} onValueChange={onChange}>
      <SelectTrigger
        size="sm"
        className={cn("h-8 text-left *:data-[slot=select-value]:flex-1 *:data-[slot=select-value]:justify-start", className)}
        active={value !== CURRENT}
        aria-label={`Version of ${type} to read`}
        data-testid="delivery-cache-version"
      >
        <History />
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value={CURRENT}>
          Current version
          {head?.hash != null && <span className="font-mono text-[11px] text-muted-foreground">{shortHash(head.hash)}</span>}
        </SelectItem>
        {all.length === 0 && (
          <div className="px-2 py-1.5 text-[12px] text-muted-foreground" data-testid="delivery-cache-type-versions-none">
            No earlier version of {type}
          </div>
        )}
        {earlier.map((option) => (
          <SelectItem key={option.version} value={option.version} data-testid="delivery-cache-type-version-option">
            <span className="font-mono text-[12px]">{option.version}</span>
            {option.hash !== null && <span className="font-mono text-[11px] text-muted-foreground">{shortHash(option.hash)}</span>}
            {/* The label is the capture instant already, so the option says how the version moved the type instead. */}
            <span className="text-[11px] text-muted-foreground">{option.change}</span>
            {option.sameAsCurrent && <span className="text-[11px] text-muted-foreground">same as current</span>}
          </SelectItem>
        ))}
        <PrunedNote count={all.length - earlier.length} />
      </SelectContent>
    </Select>
  );
}

/** A count and the word for what it counts: a lookup table holds rows, an OSDU type records. */
function counted(count: number, type: BrowsedType | null): string {
  const noun = type !== null && type.key !== null ? "row" : "record";
  return `${count.toLocaleString()} ${noun}${count === 1 ? "" : "s"}`;
}

/**
 * The columns a type's records read in: what tells the records apart first (an OSDU id past its repeated partition and
 * type, or a lookup row's key), then one column per captured name. A value that only repeats the record's identity or a
 * value to its left steps back, and the search's matches are marked, so each row says what is new in it and why the search
 * found it.
 */
function recordColumns(
  type: BrowsedType, names: string[], search: string, rows: DeliveryCachedItem[] | undefined,
): Column<DeliveryCachedItem>[] {
  const identity: Column<DeliveryCachedItem> = {
    id: "recordId",
    header: type.key === null ? "OSDU id" : `${type.key} (key)`,
    render: (row) => (
      <CachedValueText
        value={type.key === null ? splitRecordId(row.recordId).tail : row.recordId}
        search={search}
        strong
        maxWidth={260}
        testId="delivery-cache-item-id"
      />
    ),
  };

  // Each value column is as wide as the values it holds need, up to a cap, and packs to the left, so a row reads across in
  // one sweep; the last takes what the others leave. A lookup table with many names fits its card rather than pushing its
  // last columns out of view, and a value longer than its column clips, whole on hover.
  const values = names.map((name, index): Column<DeliveryCachedItem> => {
    const width = columnWidth(name, rows);
    const last = index === names.length - 1;
    return {
      id: `field:${name}`,
      header: name,
      fill: last,
      floor: last ? width : undefined,
      render: (row) => (
        <CachedValueText
          value={row.fields[name]}
          seen={said(row, names.slice(0, index))}
          search={search}
          maxWidth={width - CELL_PADDING}
          fill={last}
        />
      ),
    };
  });

  return values.length === 0 ? [{ ...identity, fill: true, floor: 200 }] : [identity, ...values];
}

/** A table cell's horizontal padding, in pixels. */
const CELL_PADDING = 24;

/**
 * How wide a value column is: room for the longest value the rows given hold under it, or its name, between bounds that
 * keep a short column legible and a long one from crowding out the rest. A mono character at 12px is about 7.2px wide.
 */
function columnWidth(name: string, rows: DeliveryCachedItem[] | undefined): number {
  const longest = Math.max(name.length, ...(rows ?? []).map((row) => cachedCell(row.fields[name])?.length ?? 0));
  return Math.min(240, Math.max(88, Math.round(longest * 7.2) + CELL_PADDING));
}

/** What a row says before the values of `names`: the record's identity, then those values, each as a cell reads it. */
function said(row: DeliveryCachedItem, earlier: string[]): string[] {
  const values = earlier.map((name) => cachedCell(row.fields[name])).filter((value): value is string => value !== null);
  return [recordIdentity(row.recordId, row.entityType), ...values];
}

/**
 * Where one cache flow's declaration of a type takes its records from, in a line: the kind it searches on OSDU, or the
 * ingestion table, dictionary or dimension it holds, with the name a lookup row is keyed by.
 */
function SourceLine({ source }: { source: DeliveryCacheTypeSource }) {
  switch (source.origin) {
    case "table":
      return (
        <>
          <Table2 className="size-3.5 shrink-0" />
          <span>Ingestion table</span>
          <span className="font-mono text-foreground">{source.sourceObject}</span>
          <span>keyed by</span>
          <span className="font-mono text-foreground">{source.keyField}</span>
        </>
      );
    case "dictionary":
      return (
        <>
          <BookOpen className="size-3.5 shrink-0" />
          <span>Dictionary</span>
          <span className="font-mono text-foreground">{source.dictionaryPath}</span>
          <span>keyed by</span>
          <span className="font-mono text-foreground">{source.keyField}</span>
        </>
      );
    case "dimension":
      return (
        <>
          <Shapes className="size-3.5 shrink-0" />
          <span>Dimension</span>
          <Link
            to="/delivery/dimensions"
            className="font-mono text-foreground hover:underline"
            title="The dimension's members, as its last build in this partition left them"
          >
            {source.sourceObject}
          </Link>
          <span>keyed by</span>
          <span className="font-mono text-foreground">{source.keyField}</span>
        </>
      );
    default:
      return (
        <>
          <Search className="size-3.5 shrink-0" />
          <span>Searched on OSDU</span>
          {source.kind !== null && <span className="min-w-0 max-w-[420px] text-foreground"><KindText kind={source.kind} /></span>}
        </>
      );
  }
}

/**
 * The head of a type's table: its name and entity type, how many records the version being read holds (and, with a
 * search, how many of them match), where the records come from and which cache flow declares the type, and the prefix
 * every id repeats, which the id column leaves out. Its lineage jump opens the type's node: the cache flows writing it and
 * the mappings reading it.
 */
function TypeHeader({ scope, type, matches, idPrefix }: {
  /** The partition whose cache holds the type. */
  scope: string;
  type: BrowsedType;
  /** How many records match the search; undefined while counting, null without a search. */
  matches: number | null | undefined;
  /** The partition and entity type every id of the type starts with, when the records read show one. */
  idPrefix: string | null;
}) {
  const sources = type.declared?.sources ?? [];
  const source = sources.at(0);
  return (
    <div className="flex flex-col gap-1" data-testid="delivery-cache-type-header">
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <h2 className="text-base font-medium" data-testid="delivery-cache-type-name">{type.name}</h2>
        <span className="font-mono text-[12px] text-muted-foreground">{type.entityType}</span>
        {type.declared?.onChange === "approve" && (
          <StatePill tone="warning" label="changes need approval" icon={ShieldCheck} testId="delivery-cache-type-approve" />
        )}
        <span className="ml-auto font-mono text-[12px] tabular-nums text-muted-foreground" data-testid="delivery-cache-type-count">
          {matches === null
            ? counted(type.items, type)
            : matches === undefined
              ? <Skeleton className="inline-block h-3 w-24 align-middle" />
              : `${matches.toLocaleString()} of ${counted(type.items, type)} match`}
        </span>
        <span className="self-center">
          <LineageJumpButton target={cacheTypeLineageTarget(type, scope)} iconOnly />
        </span>
      </div>
      <div className="flex min-w-0 flex-wrap items-center gap-x-1.5 gap-y-0.5 text-[12px] text-muted-foreground">
        {source === undefined
          ? <span>Only older versions hold this type: no cache flow declares it now.</span>
          : (
            <>
              <SourceLine source={source} />
              <span className="text-muted-foreground/60">·</span>
              <span>declared by</span>
              <span className="font-mono text-foreground">{source.flow}</span>
              {sources.length > 1 && (
                <RichTooltip body={sources.slice(1).map((other) => other.flow).join("\n")} title="Also declared by" mono>
                  <span className="underline decoration-dotted underline-offset-2">and {sources.length - 1} more</span>
                </RichTooltip>
              )}
            </>
          )}
        {idPrefix !== null && (
          <>
            <span className="text-muted-foreground/60">·</span>
            <span>ids</span>
            <span className="font-mono">{idPrefix}</span>
          </>
        )}
        {type.change !== null && (
          <>
            <span className="text-muted-foreground/60">·</span>
            <RichTooltip title="Content" body={contentNote(type)}>
              <span className="underline decoration-dotted underline-offset-2" data-testid="delivery-cache-type-since">
                {type.change === "unchanged"
                  ? (type.since === null ? "unchanged in this version" : <>unchanged since <span className="font-mono text-foreground">{type.since}</span></>)
                  : `${type.change} in this version`}
              </span>
            </RichTooltip>
          </>
        )}
      </div>
    </div>
  );
}

/**
 * What the version being read holds of a type, as the hover over its change explains it: a version is the whole partition's
 * cache and moves when anything in it does, so a type unchanged in it holds exactly what the version it dates from captured.
 */
function contentNote(type: BrowsedType): string {
  const hash = type.hash === null ? "" : `\nContent hash ${type.hash}`;
  if (type.change === "unchanged") {
    const since = type.since === null ? "the version before" : `version ${type.since}`;
    return `${type.name} holds exactly the records and values ${since} held of it: its content hash did not move. `
      + `The versions written since changed other types or the partition's OSDU feature flags, so nothing built from ${type.name} renders differently.${hash}`;
  }

  return type.change === "added"
    ? `${type.name} arrived with this version: the version before held none of it.${hash}`
    : `A record of ${type.name} arrived, left or holds other values in this version.${hash}`;
}

/**
 * One type among every type's records: its name (which opens its table), how many records it holds or match the search,
 * and its first records in its own columns. It fetches them as it scrolls into view, or at once when the search is
 * counting every type anyway.
 */
function TypeSection({ scope, type, search, version, eager, onOpen, onItem }: {
  scope: string;
  type: BrowsedType;
  search: string;
  version: string | undefined;
  /** Fetch now rather than when the section nears the viewport. */
  eager: boolean;
  onOpen: () => void;
  onItem: (item: DeliveryCachedItem) => void;
}) {
  const [ref, near] = useNearViewport<HTMLDivElement>();
  const sample = useQuery({ ...typeSampleQuery(scope, type.name, search, version), enabled: eager || near });
  const rows = sample.data?.items;
  const total = sample.data?.total;
  const columns = useMemo(() => recordColumns(type, columnNames(type, rows), search, rows), [type, rows, search]);
  const searching = search !== "";

  const header = (
    <div className="flex min-w-0 items-center gap-2 border-b border-border px-3 py-2">
      <button
        type="button"
        onClick={onOpen}
        className="truncate text-[13px] font-medium outline-none underline-offset-4 hover:text-primary hover:underline focus-visible:underline"
        data-testid="delivery-cache-section-open"
      >
        {type.name}
      </button>
      <span className="hidden min-w-0 truncate font-mono text-[11px] text-muted-foreground sm:inline">{type.entityType}</span>
      <span className="ml-auto shrink-0 font-mono text-[12px] tabular-nums text-muted-foreground" data-testid="delivery-cache-section-count">
        {!searching
          ? counted(type.items, type)
          : total === undefined
            ? <Skeleton className="inline-block h-3 w-16 align-middle" />
            : `${total.toLocaleString()} match${total === 1 ? "" : "es"}`}
      </span>
      <LineageJumpButton target={cacheTypeLineageTarget(type, scope)} iconOnly />
    </div>
  );

  const more = rows !== undefined && total !== undefined && total > rows.length;
  const footer = more
    ? (
      <div className="flex items-center justify-between gap-3 border-t border-border px-3 py-1 text-xs text-muted-foreground">
        <span className="font-mono tabular-nums">{rows.length} of {total.toLocaleString()}</span>
        <Button variant="ghost" size="xs" onClick={onOpen} data-testid="delivery-cache-section-more">
          Open {type.name}
          <ArrowRight />
        </Button>
      </div>
    )
    : undefined;

  return (
    <div ref={ref} data-testid="delivery-cache-section" data-type={type.name}>
      {sample.isError
        ? (
          <Card className="gap-0 overflow-hidden rounded-lg p-0">
            {header}
            <div className="p-3">
              {isApiError(sample.error)
                ? <CorrelationError error={sample.error} />
                : <p className="text-[13px] text-destructive">{String(sample.error)}</p>}
            </div>
          </Card>
        )
        : (
          <DataTable
            columns={columns}
            rows={rows}
            rowKey={(row) => row.itemId}
            onRowClick={onItem}
            emptyMessage={searching ? `No ${type.name} record matches the search.` : `The version being read holds no ${type.name} records.`}
            toolbar={header}
            footer={footer}
            skeletonRows={Math.max(1, Math.min(SECTION_ROWS, type.items))}
            data-testid="delivery-cache-section-table"
          />
        )}
    </div>
  );
}

/**
 * The partition and entity type every id among `rows` starts with, which a type's id column leaves out; null when the rows
 * do not agree on one or have none.
 */
function sharedIdPrefix(rows: DeliveryCachedItem[] | undefined): string | null {
  const prefixes = new Set((rows ?? []).map((row) => splitRecordId(row.recordId).prefix));
  const [only] = prefixes;
  return prefixes.size === 1 && only !== "" ? only : null;
}

/**
 * One type's records, a page at a time, in the type's own columns: the view a type opens on, whether it was picked in the
 * list or opened from its section.
 */
function TypeTable({ scope, type, search, version, sampleRows, onItem }: {
  scope: string;
  type: BrowsedType;
  search: string;
  version: string | undefined;
  /** The type's first records, for the names its table needs a column for beyond the declared ones. */
  sampleRows: DeliveryCachedItem[] | undefined;
  onItem: (item: DeliveryCachedItem) => void;
}) {
  const columns = useMemo(() => recordColumns(type, columnNames(type, sampleRows), search, sampleRows), [type, sampleRows, search]);
  return (
    <PagedTable
      queryKey={["delivery", "cache", "items", scope, type.name, search, version ?? null]}
      fetchPage={(page, pageSize) => deliveryApi.cachedItems({
        page, pageSize, scope, type: type.name, search: search === "" ? undefined : search, version,
      })}
      columns={columns}
      rowKey={(row) => row.itemId}
      onRowClick={onItem}
      emptyMessage={search !== ""
        ? `No ${type.name} record holds a value, id or alias matching the search.`
        : `The version being read holds no ${type.name} records: the search the cache flow declares for the type matched none.`}
      data-testid="delivery-cache-items-table"
    />
  );
}

/** A quiet line naming the types a view leaves out, so an absent type reads as nothing to show rather than as missing. */
function LeftOut({ label, types, testId }: { label: string; types: BrowsedType[]; testId: string }) {
  if (types.length === 0) {
    return null;
  }

  return (
    <p className="text-[12px] text-muted-foreground" data-testid={testId}>
      {label}{" "}
      {types.map((type, index) => (
        <span key={type.name}>
          {index > 0 && ", "}
          <span className="font-mono">{type.name}</span>
        </span>
      ))}
    </p>
  );
}

/**
 * The records of a partition's cache, browsed a type at a time. The types are listed beside the records, grouped by family,
 * with how many records the version being read holds of each; every type's records share one search and one version,
 * both in the toolbar over them. All types shows each type as a section of its own, its first records in its own columns,
 * rather than one list whose rows cannot share columns; a type opens its whole table. While a search is typed the list
 * counts the matches in every type, the sections of types nothing matches in give way, and every match is marked.
 */
export function DeliveryCacheRecords({ scope, types, type, onType, versions, onCompare }: {
  /** The partition whose cache the records belong to. */
  scope: string;
  /** The types the cache flows declare, as the page summarizes them. */
  types: CachedTypeSummary[];
  /** The type in scope; null for every type. */
  type: string | null;
  onType: (type: string | null) => void;
  /** The cache's versions, newest first, for the version picker. */
  versions: DeliveryCacheVersion[];
  /** Compares an earlier version, the one being read, with the current one. */
  onCompare: (version: string) => void;
}) {
  const [item, setItem] = useState<DeliveryCachedItem | null>(null);
  const [typed, setTyped] = useState("");
  const search = useDebouncedValue(typed.trim(), SEARCH_DELAY_MS);
  // The picker holds a version label; CURRENT follows whichever version is current rather than freezing on one.
  const [picked, setPicked] = useState<string>(CURRENT);

  // With a type in view the picker offers the type's own versions, the versions of the cache at which its content hash
  // moved, so any version picked holds something of the type the current version does not. The versions tab reads the
  // same history, so the two share one request.
  const typeHistory = useQuery({
    queryKey: ["delivery", "cache", "history", scope, type],
    queryFn: () => deliveryApi.cacheHistory(scope, type ?? undefined),
    enabled: type !== null,
  });
  const ofType = useMemo(
    () => (type === null || typeHistory.data === undefined ? null : typeVersions(typeHistory.data, type)),
    [type, typeHistory.data],
  );
  const earlier = ofType === null ? null : earlierTypeVersions(ofType);

  // A label the picker does not offer (one the cache no longer lists, one whose records the retention pruned since it was
  // picked, or a version of the cache that left the type in view as the current one holds it) would read as a stale pick,
  // so it falls back to current.
  const offered = earlier === null
    ? versions.filter((v) => !isPruned(v)).map((v) => v.version)
    : earlier.filter((v) => !v.pruned).map((v) => v.version);
  const versionFilter = picked === CURRENT || offered.includes(picked) ? picked : CURRENT;
  const typeVersion = earlier?.find((candidate) => candidate.version === versionFilter) ?? null;
  const reading = versions.find((v) => (versionFilter === CURRENT ? v.current : v.version === versionFilter));
  const historic = reading !== undefined && !reading.current ? reading : null;
  const hasCurrent = versions.some((v) => v.current);
  const version = versionFilter === CURRENT ? undefined : versionFilter;

  const browsed = useMemo(() => browsedTypes(types, reading), [types, reading]);
  const selected = type === null ? null : browsed.find((candidate) => candidate.name === type) ?? null;
  const searching = search !== "";

  // While a search is typed, every type is asked how many of its records match: the list counts them, and each type's
  // section and table reads the same answer rather than asking again.
  const counts = useQueries({
    queries: browsed.map((candidate) => ({ ...typeSampleQuery(scope, candidate.name, search, version), enabled: searching })),
  });
  const matches = useMemo(() => {
    if (!searching) {
      return null;
    }

    return new Map(browsed.map((candidate, index) => [candidate.name, counts[index]?.data?.total]));
  }, [searching, browsed, counts]);

  // The first records of the type in view: the id prefix its header states, and any name its table needs a column for
  // that no cache flow declares. The same request the list's count makes while a search is typed.
  const typeSample = useQuery({ ...typeSampleQuery(scope, selected?.name ?? "", search, version), enabled: selected !== null });
  const sampleRows = selected === null ? undefined : typeSample.data?.items;

  // The sheet lists the declared names first, in their order, then anything else the record carries.
  const itemType = item === null ? null : browsed.find((candidate) => candidate.name === item.typeName) ?? null;
  const declaredNames = itemType?.declared?.fields.map((field) => field.as) ?? [];
  const detailNames = item === null
    ? []
    : [...declaredNames, ...Object.keys(item.fields).filter((name) => !declaredNames.includes(name))];

  const shown = browsed.filter((candidate) => (searching ? matches?.get(candidate.name) !== 0 : candidate.items > 0));
  const unmatched = searching ? browsed.filter((candidate) => matches?.get(candidate.name) === 0) : [];
  const empty = searching ? [] : browsed.filter((candidate) => candidate.items === 0);
  const totalRecords = browsed.reduce((sum, candidate) => sum + candidate.items, 0);
  const matchTotal = matches === null || [...matches.values()].some((value) => value === undefined)
    ? undefined
    : [...matches.values()].reduce<number>((sum, value) => sum + (value ?? 0), 0);

  return (
    <div className="grid items-start gap-4 lg:grid-cols-[15rem_minmax(0,1fr)]">
      <CacheTypeRail
        types={browsed}
        selected={selected?.name ?? null}
        onSelect={onType}
        matches={matches}
        className="hidden lg:sticky lg:top-0 lg:flex lg:max-h-[calc(100dvh-14rem)]"
      />

      <div className="flex min-w-0 flex-col gap-3">
        {selected !== null && (
          <TypeHeader
            scope={scope}
            type={selected}
            matches={matches === null ? null : matches.get(selected.name)}
            idPrefix={selected.key === null ? sharedIdPrefix(sampleRows) : null}
          />
        )}

        <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
          <SearchInput
            value={typed}
            onChange={setTyped}
            placeholder={selected === null ? "Search every type: values, ids and aliases" : `Search ${selected.name}: values, ids and aliases`}
            label="Search cached records"
            className="sm:w-96"
            testId="delivery-cache-search"
          />
          {type !== null && ofType !== null
            ? <TypeVersionPicker type={type} versions={ofType} value={versionFilter} onChange={setPicked} className="w-full sm:w-96" />
            : <CacheVersionPicker versions={versions} value={versionFilter} onChange={setPicked} className="w-full sm:w-64" />}
          {selected === null && (
            <span className="whitespace-nowrap font-mono text-[12px] tabular-nums text-muted-foreground sm:ml-auto" data-testid="delivery-cache-records-count">
              {!searching
                ? `${totalRecords.toLocaleString()} in ${browsed.length} type${browsed.length === 1 ? "" : "s"}`
                : matchTotal === undefined
                  ? <Skeleton className="inline-block h-3 w-32 align-middle" />
                  : `${matchTotal.toLocaleString()} match${matchTotal === 1 ? "" : "es"} in ${shown.length} type${shown.length === 1 ? "" : "s"}`}
            </span>
          )}
        </div>

        {historic !== null && (
          <div
            role="status"
            className="flex flex-wrap items-center gap-2 rounded-md border border-warning/40 bg-warning/8 px-3 py-2 text-[12.5px]"
            data-testid="delivery-cache-historic"
          >
            <History className="size-4 shrink-0 text-warning" />
            {typeVersion === null
              ? (
                <span>
                  Reading the cache as it stood at <span className="font-mono">{historic.version}</span>. Deliveries read the
                  current version.
                </span>
              )
              : (
                <span>
                  Reading <span className="font-mono">{type}</span> as version <span className="font-mono">{historic.version}</span>{" "}
                  {typeVersion.change} it
                  {typeVersion.hash !== null && <> (<span className="font-mono">{shortHash(typeVersion.hash)}</span>)</>}.
                  Deliveries read the current version.
                </span>
              )}
            {typeVersion?.sameAsCurrent === true && (
              <span className="ml-auto text-muted-foreground" data-testid="delivery-cache-same-as-current">
                The same {type} records and values as the current version
              </span>
            )}
            {hasCurrent && typeVersion?.sameAsCurrent !== true && (
              <Button
                variant="outline"
                size="xs"
                className="ml-auto"
                onClick={() => onCompare(historic.version)}
                title={selected === null
                  ? "What changed in the cache between this version and the current one"
                  : `What changed in ${selected.name} between this version and the current one`}
                data-testid="delivery-cache-compare-current"
              >
                <GitCompare />
                Compare with current
              </Button>
            )}
            <Button
              variant="outline"
              size="xs"
              className={hasCurrent ? undefined : "ml-auto"}
              onClick={() => setPicked(CURRENT)}
              data-testid="delivery-cache-back-to-current"
            >
              Back to current
            </Button>
          </div>
        )}

        {selected !== null
          ? (
            <TypeTable
              scope={scope}
              type={selected}
              search={search}
              version={version}
              sampleRows={sampleRows}
              onItem={setItem}
            />
          )
          : shown.length === 0
            ? (
              <Card className="gap-0 rounded-lg p-0">
                <EmptyState
                  icon={<DatabaseZap />}
                  title={searching ? "No cached record matches the search" : "No cached records yet"}
                  description={searching
                    ? "Nothing in any type holds a value, id or alias containing it."
                    : "Refresh a cache flow of the partition to capture its first version."}
                  data-testid="delivery-cache-records-empty"
                />
              </Card>
            )
            : (
              <div className="flex flex-col gap-3" data-testid="delivery-cache-sections">
                {shown.map((candidate) => (
                  <TypeSection
                    key={candidate.name}
                    scope={scope}
                    type={candidate}
                    search={search}
                    version={version}
                    eager={searching}
                    onOpen={() => onType(candidate.name)}
                    onItem={setItem}
                  />
                ))}
                <LeftOut label="Nothing matches in" types={unmatched} testId="delivery-cache-unmatched" />
                <LeftOut label="The version being read holds nothing yet of" types={empty} testId="delivery-cache-empty-types" />
              </div>
            )}
      </div>

      <Sheet open={item !== null} onOpenChange={(open) => { if (!open) { setItem(null); } }}>
        <SheetContent
          className="w-full gap-0 sm:max-w-2xl"
          // Focus lands on the sheet itself rather than its first button, whose tooltip would otherwise open with it.
          onOpenAutoFocus={(event) => { event.preventDefault(); (event.currentTarget as HTMLElement).focus(); }}
          data-testid="delivery-cache-item-detail"
        >
          {item !== null && (
            <>
              <SheetHeader className="border-b border-border">
                <SheetTitle className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5">
                  <span className="font-mono">{item.typeName}</span>
                  <span className="font-mono text-[12px] font-normal text-muted-foreground">{item.entityType}</span>
                </SheetTitle>
                <SheetDescription className="flex items-center gap-1 font-mono text-[12px] text-foreground">
                  <span className="break-all">{item.recordId}</span>
                  <CopyButton iconOnly label={isLookupEntityType(item.entityType) ? "Copy the key" : "Copy the OSDU id"} text={item.recordId} testId="delivery-cache-item-copy-id" />
                </SheetDescription>
                <div className="flex flex-wrap items-center gap-2 text-[12px] text-muted-foreground" data-testid="delivery-cache-item-version">
                  <span>
                    Version <span className="font-mono text-foreground">{item.version}</span>
                  </span>
                  {historic === null
                    ? <StatePill tone="success" label="current" icon={Pin} testId="delivery-cache-item-state" />
                    : <StatePill tone="warning" label="historic" icon={History} testId="delivery-cache-item-state" />}
                </div>
              </SheetHeader>
              <div className="flex flex-1 flex-col gap-5 overflow-y-auto p-4">
                <section className="flex flex-col gap-2">
                  <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Captured values</h3>
                  {detailNames.length === 0
                    ? <p className="text-[13px] text-muted-foreground">The record carries no captured values.</p>
                    : (
                      <div className="grid gap-3 [grid-template-columns:repeat(auto-fill,minmax(160px,1fr))]">
                        {detailNames.map((name) => (
                          <DetailPair key={name} label={name}>
                            <span className="break-all font-mono text-[12px]">{cachedCell(item.fields[name]) ?? "-"}</span>
                          </DetailPair>
                        ))}
                      </div>
                    )}
                </section>
                <RecordMappingReference item={item} names={detailNames} />
                <section className="flex flex-col gap-2">
                  <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">As captured</h3>
                  <RecordContentView value={JSON.stringify(item.fields, null, 2)} language="json" height={360} data-testid="delivery-cache-item-json" />
                </section>
              </div>
            </>
          )}
        </SheetContent>
      </Sheet>
    </div>
  );
}
