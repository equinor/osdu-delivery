import { useMemo, useState } from "react";
import { useInfiniteQuery } from "@tanstack/react-query";
import { ArrowDownWideNarrow, ChevronRight, Info, Loader2, Pencil, RefreshCw, SearchCode, SearchX, ShieldCheck, Waypoints } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuLabel, DropdownMenuRadioGroup, DropdownMenuRadioItem, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Skeleton } from "@/components/ui/skeleton";
import { CopyButton } from "@/components/CopyButton";
import { EmptyState } from "@/components/EmptyState";
import { IconAction } from "@/components/IconAction";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import {
  EXPLORER_PAGE, EXPLORER_WINDOW, explorerApi, type ExplorerAnswer, type ExplorerFilter, type ExplorerHit, type ExplorerPage, type ExplorerReading,
  type ExplorerSearchRequest, type ExplorerSort,
} from "../../../api/explorer";
import { isRecordReference } from "../osduDocument";
import { idParts } from "../osduRecordModel";
import { RecordName } from "../RecordName";
import { ExplorerAddFilter, ExplorerFilterChips } from "./ExplorerFilters";
import { ExplorerGroupBy } from "./ExplorerGroupBy";
import { ExplorerGrid, type GridColumn } from "./ExplorerGrid";
import { ExplorerProblem } from "./ExplorerProblem";
import { ExplorerReferencesDialog } from "./ExplorerReferences";
import { ExplorerValidateDialog } from "./ExplorerValidation";
import { ReadingBar } from "./ReadingBar";
import { counted, fieldLabel, isNamespace, kindParts, sameFilter, scopeKind, SORT_LABELS, typeScopeOf, type ExplorerScope } from "./explorerModel";
import { termOf, termSource, useSearchTerms } from "./explorerTerms";

/** How a search was read, as a word after the count; nothing for the plain cases. */
const READINGS: Partial<Record<ExplorerReading, string>> = {
  id: "by id",
  idPrefix: "ids starting with it",
  lucene: "Lucene query",
  mentions: "mentioning the record",
};

/**
 * What the list is read by, exactly as the explorer sends it to the search service, one click away rather than over every
 * list: the kind and the Lucene query (none where the list is every record of the kind). The search field, the place
 * picked and the conditions all make it, so it is the expression to reuse elsewhere: copied as written or as a search
 * request, or taken into the search field as Lucene to be changed there.
 */
function SentQuery({ kind, query, onEdit }: { kind: string; query: string | null; onEdit: (query: string) => void }) {
  const [open, setOpen] = useState(false);
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button
          variant="ghost"
          size="icon"
          className="size-8"
          title="The query: the kind and the Lucene query this list is read by, as sent to the search service"
          aria-label="The query this list is read by"
          data-testid="explorer-sent-toggle"
        >
          <SearchCode />
        </Button>
      </PopoverTrigger>
      <PopoverContent
        align="end"
        className="w-[min(560px,90vw)] p-0"
        // The focus stays on the glyph, so no button's tooltip opens with the query and Escape closes it at once.
        onOpenAutoFocus={(event) => event.preventDefault()}
        data-testid="explorer-sent"
      >
        <div className="flex items-center gap-1 border-b px-3 py-1.5">
          <span className="min-w-0 flex-1 truncate text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Sent to the search service</span>
          {query && <CopyButton iconOnly label="Copy the query" text={query} testId="explorer-sent-copy" />}
          <CopyButton label="As a request" text={JSON.stringify(query ? { kind, query, limit: 10 } : { kind, limit: 10 }, null, 2)} testId="explorer-sent-copy-request" />
          {query && (
            <Button variant="ghost" size="sm" className="h-6 px-2 text-[12px]" onClick={() => { setOpen(false); onEdit(query); }} data-testid="explorer-sent-edit">
              <Pencil />
              Edit
            </Button>
          )}
        </div>
        <div className="grid min-w-0 grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1 px-3 py-2.5 text-[12px]">
          <span className="text-muted-foreground">kind</span>
          <span className="min-w-0 break-all font-mono" data-testid="explorer-sent-kind">{kind}</span>
          <span className="text-muted-foreground">query</span>
          {query
            ? <span className="max-h-40 min-w-0 overflow-auto break-all font-mono text-foreground" data-testid="explorer-sent-query">{query}</span>
            : <span className="text-muted-foreground" data-testid="explorer-sent-query">none: every record of the kind</span>}
        </div>
      </PopoverContent>
    </Popover>
  );
}

/** The type a place is of, when it is one type or a kind of one: what Referenced by asks about; null for a group or every type. */
function placeType(scope: ExplorerScope): string | null {
  if (scope.level === "type") {
    return scope.entityType;
  }

  const entityType = scope.level === "kind" ? kindParts(scope.kind).entityType : "";
  return /^[\w.-]+--[\w.-]+$/.test(entityType) ? entityType : null;
}

/** One step of the place in view: the partition, a group, a type or a kind, the last the current one. */
function Crumb({ label, current, onClick, mono = false, testId }: { label: string; current: boolean; onClick: () => void; mono?: boolean; testId: string }) {
  return current
    ? <span className={cn("min-w-0 truncate font-medium text-foreground", mono && "font-mono text-[12px]")} aria-current="location" data-testid={testId}>{label}</span>
    : (
      <button type="button" onClick={onClick} className={cn("min-w-0 truncate text-muted-foreground hover:text-foreground hover:underline", mono && "font-mono text-[12px]")} data-testid={testId}>
        {label}
      </button>
    );
}

/**
 * Where the records in view are, as steps to broaden by: the partition (every type), the group, the type, the kind. The
 * same place leads the record view, so a record is always read as somewhere in its partition.
 */
export function ScopeCrumbs({ partition, scope, onScope, last = true }: {
  partition: string | null;
  scope: ExplorerScope;
  onScope: (scope: ExplorerScope) => void;
  /** Whether the scope is the last step shown; a record view puts the record after it. */
  last?: boolean;
}) {
  const steps: { label: string; scope: ExplorerScope; mono?: boolean }[] = [{ label: partition ?? "OSDU", scope: { level: "all" } }];
  if (scope.level === "group") {
    steps.push({ label: scope.group, scope });
  } else if (scope.level === "type" || scope.level === "kind") {
    // A type naming no group sits in its namespace (authority:source), as the list of types shows it.
    const typeScope = scope.level === "type" ? scope : typeScopeOf(scope.kind);
    const parts = kindParts(scopeKind(scope) ?? "");
    const group = parts.group !== "" ? parts.group : typeScope.level === "type" ? typeScope.namespace : undefined;
    if (group !== undefined) {
      steps.push({ label: group, scope: { level: "group", group }, mono: isNamespace(group) });
    }

    if (typeScope.level === "type") {
      steps.push({ label: parts.type, scope: typeScope });
    }

    if (scope.level === "kind") {
      steps.push({ label: `${parts.version} ${parts.authority}:${parts.source}`, scope, mono: true });
    }
  }

  return (
    <>
      {steps.map((step, index) => (
        <span key={`${step.label}:${index}`} className="inline-flex min-w-0 items-center gap-1">
          {index > 0 && <ChevronRight className="size-3.5 shrink-0 text-muted-foreground/50" aria-hidden />}
          <Crumb label={step.label} current={last && index === steps.length - 1} onClick={() => onScope(step.scope)} mono={step.mono} testId="explorer-crumb" />
        </span>
      ))}
    </>
  );
}

/**
 * What a record holds at a column: its first value, a record named by its id as a record is, and how many more it holds
 * after it, every value on hover.
 */
function ColumnValues({ values }: { values: string[] | undefined }) {
  if (values === undefined || values.length === 0) {
    return <span className="text-[12px] text-muted-foreground/60" aria-label="No value">-</span>;
  }

  const [first] = values;
  return (
    <span className="flex min-w-0 items-center gap-1" title={values.join("\n")}>
      <span className="min-w-0 truncate text-[12px]">{isRecordReference(first) ? <RecordName id={first} /> : first}</span>
      {values.length > 1 && <span className="shrink-0 text-[11px] text-muted-foreground">+{values.length - 1}</span>}
    </span>
  );
}

/**
 * The records a search finds in the place picked, a page at a time as the grid is scrolled, under one line that says
 * where they are, how many there are and how they were read, with the order, the conditions and the grouping beside it,
 * the query sent and the checks one click away, and the conditions the records are narrowed to under it. Each property a
 * condition asks is a column, so the grid shows why each record is listed. A partition of millions reads as fast as one
 * of hundreds: each page is one query of the search index, and the total is the index's own count. The service pages
 * through the first ten thousand records a query matches; past them the reader narrows, and the foot of the grid says so.
 */
export function ExplorerResults({ partition, request, scope, onScope, onOpen, onFilters, onSort, onSearchEverywhere, onEditQuery }: {
  partition: string | null;
  /** The search, without its page. */
  request: ExplorerSearchRequest & { sort: ExplorerSort; filters: ExplorerFilter[]; columns: string[] };
  scope: ExplorerScope;
  onScope: (scope: ExplorerScope) => void;
  onOpen: (hit: ExplorerHit) => void;
  onFilters: (filters: ExplorerFilter[]) => void;
  onSort: (sort: ExplorerSort) => void;
  /** Takes the search out of the type in view, to every type. */
  onSearchEverywhere: () => void;
  /** Puts the query sent in the search box as a Lucene query, to be changed there; the place stays. */
  onEditQuery: (query: string) => void;
}) {
  const pages = useInfiniteQuery({
    queryKey: ["explorer", "search", partition, request],
    queryFn: ({ pageParam }): Promise<ExplorerAnswer<ExplorerPage>> => explorerApi.search(partition, { ...request, offset: pageParam, limit: EXPLORER_PAGE }),
    initialPageParam: 0,
    getNextPageParam: (last) => {
      const next = last.answer.offset + EXPLORER_PAGE;
      return !last.answer.refusal && last.answer.hits.length > 0 && next < Math.min(last.answer.total, EXPLORER_WINDOW) ? next : undefined;
    },
    staleTime: 60_000,
    gcTime: 10 * 60_000,
    retry: false,
    refetchOnWindowFocus: false,
  });

  const first = pages.data?.pages[0]?.answer;
  const [validating, setValidating] = useState(false);
  const [referencing, setReferencing] = useState(false);
  const referencedType = placeType(scope);
  // A record can move between two pages while they are read (the index changes under the search); it is listed once.
  const hits = useMemo(() => {
    const byId = new Map<string, ExplorerHit>();
    for (const hit of (pages.data?.pages ?? []).flatMap((page) => page.answer.hits)) {
      if (!byId.has(hit.id)) {
        byId.set(hit.id, hit);
      }
    }

    return [...byId.values()];
  }, [pages.data]);
  const notes = [...new Set((pages.data?.pages ?? []).flatMap((page) => page.answer.notes))];
  // The source columns the conditions name, said on the columns of the properties they are searched in.
  const terms = useSearchTerms(request.kind, request.filters.some((filter) => filter.term !== undefined), true);
  const sourcesOf = (path: string) => [...new Set(request.filters
    .filter((filter) => filter.path === path)
    .map((filter) => termOf(filter, terms.data?.terms))
    .filter((term) => term !== undefined)
    .map(termSource))];
  const oneType = scope.level === "type" || scope.level === "kind";
  const reading = first === undefined ? undefined : READINGS[first.reading];
  const capped = first !== undefined && first.total > EXPLORER_WINDOW && !pages.hasNextPage && hits.length > 0;

  const columns: GridColumn<ExplorerHit>[] = [
    {
      id: "name",
      header: "Name",
      flex: 2.2,
      render: (hit) => (hit.name !== null
        ? <span className="min-w-0 truncate" title={`${hit.name}\n${hit.nameField ?? ""}`}>{hit.name}</span>
        : <span className="min-w-0 truncate font-mono text-[12px] text-muted-foreground" title={hit.id}>{idParts(hit.id).unique}</span>),
    },
    ...(oneType ? [] : [{
      id: "type",
      header: "Type",
      flex: 1.1,
      render: (hit: ExplorerHit) => {
        const parts = kindParts(hit.kind ?? "");
        return (
          <span className="min-w-0 truncate" title={hit.kind ?? undefined}>
            {parts.type}
            <span className="ml-1.5 font-mono text-[11px] text-muted-foreground">{parts.version}</span>
          </span>
        );
      },
    } satisfies GridColumn<ExplorerHit>]),
    {
      id: "id",
      header: "Id",
      flex: 1.6,
      render: (hit) => (
        <span className="flex min-w-0 flex-1 items-center gap-1">
          {/* The end of an id is what tells it apart, so a narrow cell keeps the end and cuts the start. */}
          <span dir="rtl" className="min-w-0 flex-1 truncate text-left font-mono text-[12px] text-muted-foreground" title={hit.id}>
            <bdi dir="ltr">{idParts(hit.id).unique}</bdi>
          </span>
          <span className="opacity-0 transition-opacity group-hover/row:opacity-100 focus-within:opacity-100" onClick={(event) => event.stopPropagation()}>
            <CopyButton iconOnly label="Copy the id" text={hit.id} testId="explorer-copy-id" />
          </span>
        </span>
      ),
    },
    ...request.columns.map((path): GridColumn<ExplorerHit> => ({
      id: `column:${path}`,
      header: (
        <span
          className="block truncate font-mono text-[11px] normal-case"
          title={[path, ...sourcesOf(path).map((source) => `Searched by the column ${source}`)].join("\n")}
        >
          {fieldLabel(path)}
        </span>
      ),
      flex: 1.3,
      render: (hit) => <ColumnValues values={hit.values?.[path]} />,
    })),
    {
      id: "modified",
      header: "Changed",
      width: 136,
      align: "right",
      render: (hit) => (
        <span className="min-w-0 truncate text-[12px] text-muted-foreground" title={hit.modifyUser ?? hit.createUser ?? undefined}>
          <RelativeTime value={hit.modifyTime ?? hit.createTime} absolute={false} />
        </span>
      ),
    },
  ];

  const sortMenu = (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="sm" className="h-8 gap-1.5 px-2 text-[13px] font-normal" data-testid="explorer-sort">
          <ArrowDownWideNarrow />
          {request.sort === "relevance" && !request.text ? "Index order" : SORT_LABELS[request.sort]}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        <DropdownMenuLabel className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Order</DropdownMenuLabel>
        <DropdownMenuSeparator />
        <DropdownMenuRadioGroup value={request.sort} onValueChange={(value) => onSort(value as ExplorerSort)}>
          {(Object.keys(SORT_LABELS) as ExplorerSort[]).map((sort) => (
            <DropdownMenuRadioItem key={sort} value={sort} className="text-[13px]" data-testid="explorer-sort-option">
              {sort === "relevance" && !request.text ? "Index order" : SORT_LABELS[sort]}
            </DropdownMenuRadioItem>
          ))}
        </DropdownMenuRadioGroup>
      </DropdownMenuContent>
    </DropdownMenu>
  );

  let body;
  if (pages.isPending) {
    body = (
      <div className="flex flex-col gap-2 p-3" data-testid="explorer-results-loading">
        {Array.from({ length: 10 }, (_, index) => <Skeleton key={index} className="h-5 rounded" style={{ width: `${70 + ((index * 13) % 30)}%` }} />)}
      </div>
    );
  } else if (pages.isError) {
    body = (
      <div className="flex flex-col items-start gap-3 p-4">
        <ExplorerProblem error={pages.error} />
        <Button variant="outline" size="sm" onClick={() => void pages.refetch()}>Try again</Button>
      </div>
    );
  } else if (first?.refusal) {
    body = (
      <Alert variant="destructive" className="m-3 w-auto" data-testid="explorer-refused">
        <AlertTitle>The search service refused this search</AlertTitle>
        <AlertDescription className="whitespace-pre-wrap">
          {first.refusal}
          {request.lucene ? "\nA Lucene query is sent as written: check its fields, quotes and parentheses." : ""}
        </AlertDescription>
      </Alert>
    );
  } else if (hits.length === 0) {
    body = (
      <EmptyState
        icon={<SearchX />}
        title="Nothing found"
        description={request.text
          ? `No record ${oneType || scope.level === "group" ? "of this type " : ""}holds "${request.text}"${request.filters.length > 0 ? " with these values" : ""}.`
          : "No record here holds these values."}
        action={(
          <div className="flex flex-wrap justify-center gap-2">
            {scope.level !== "all" && <Button variant="outline" size="sm" onClick={onSearchEverywhere} data-testid="explorer-search-everywhere">Search every type</Button>}
            {request.filters.length > 0 && <Button variant="outline" size="sm" onClick={() => onFilters([])}>Clear the values</Button>}
          </div>
        )}
        data-testid="explorer-empty"
      />
    );
  } else {
    body = (
      <ExplorerGrid
        rows={hits}
        columns={columns}
        rowKey={(hit) => hit.id}
        onOpen={onOpen}
        onNearEnd={() => { if (pages.hasNextPage && !pages.isFetchingNextPage) { void pages.fetchNextPage(); } }}
        label="Records"
        testId="explorer-grid"
        footer={(pages.isFetchingNextPage || capped) && (
          <div className="flex h-9 items-center justify-center gap-2 text-[12px] text-muted-foreground" data-testid="explorer-grid-more">
            {pages.isFetchingNextPage
              ? <><Loader2 className="size-3.5 animate-spin" />Reading the next records</>
              : `The search service lists the first ${EXPLORER_WINDOW.toLocaleString("en-US")}. Narrow by type, text or a value to reach the others.`}
          </div>
        )}
      />
    );
  }

  return (
    <div className="relative flex min-h-0 flex-1 flex-col" data-testid="explorer-results">
      {pages.isFetching && <ReadingBar label="Reading the records from OSDU" />}
      <div className="flex min-h-11 flex-wrap items-center gap-x-3 gap-y-1 border-b px-3 py-1.5">
        <nav className="flex min-w-0 flex-wrap items-center gap-1 text-[13px]" aria-label="Where the records are" data-testid="explorer-place">
          <ScopeCrumbs partition={partition} scope={scope} onScope={onScope} />
        </nav>
        <span className="flex items-center gap-1.5 text-[12px] text-muted-foreground" data-testid="explorer-count">
          {pages.isPending || (pages.isFetching && !pages.isFetchingNextPage)
            ? <><Loader2 className="size-3.5 animate-spin" />Searching</>
            : first !== undefined && !first.refusal && (
              <>
                <span className="font-mono tabular-nums text-foreground">{counted(first.total, "record")}</span>
                {reading !== undefined && <span>{reading}</span>}
              </>
            )}
          {notes.length > 0 && (
            <RichTooltip title="What the search did" body={notes.join("\n\n")}>
              <Info className="size-3.5 text-warning" aria-label="What the search did" data-testid="explorer-notes" />
            </RichTooltip>
          )}
        </span>
        <div className="ml-auto flex items-center gap-1">
          {sortMenu}
          <ExplorerAddFilter
            partition={partition}
            request={request}
            onAdd={(filter) => onFilters([...request.filters.filter((f) => !sameFilter(f, filter)), filter])}
          />
          <ExplorerGroupBy partition={partition} base={request} onFilter={(filter) => onFilters([...request.filters.filter((f) => !sameFilter(f, filter)), filter])} />
          <span className="mx-0.5 h-5 w-px bg-border" aria-hidden />
          {first !== undefined && <SentQuery kind={first.kind} query={first.query} onEdit={onEditQuery} />}
          {referencedType !== null && (
            <IconAction
              label={`Referenced by: the types whose schemas have a property naming ${kindParts(`*:*:${referencedType}:*`).type} records, and in which versions`}
              icon={<Waypoints />}
              variant="ghost"
              className="size-8"
              onClick={() => setReferencing(true)}
              data-testid="explorer-referenced-by"
            />
          )}
          <IconAction
            label="Validate: check these records against the schemas of their kinds"
            icon={<ShieldCheck />}
            variant="ghost"
            className="size-8"
            onClick={() => setValidating(true)}
            data-testid="explorer-validate-records"
          />
          <IconAction label="Read again from OSDU" icon={<RefreshCw />} variant="ghost" className="size-8" onClick={() => void pages.refetch()} data-testid="explorer-refresh" />
        </div>
      </div>
      <ExplorerFilterChips partition={partition} request={request} onFilters={onFilters} />
      {body}
      {referencedType !== null && (
        <ExplorerReferencesDialog
          partition={partition}
          entityType={referencedType}
          open={referencing}
          onOpenChange={setReferencing}
          onScope={onScope}
        />
      )}
      <ExplorerValidateDialog
        partition={partition}
        request={request}
        open={validating}
        onOpenChange={setValidating}
        onOpenRecord={(id) => {
          setValidating(false);
          onOpen({ id, kind: null, version: null, name: null, nameField: null, createTime: null, createUser: null, modifyTime: null, modifyUser: null });
        }}
      />
      <span className="sr-only" aria-live="polite">{first === undefined ? "" : counted(first.total, "record")}</span>
    </div>
  );
}
