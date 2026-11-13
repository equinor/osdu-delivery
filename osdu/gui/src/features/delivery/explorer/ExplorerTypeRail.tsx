import { useMemo, useState, type ReactNode } from "react";
import { Boxes, ChevronDown, ChevronRight, Layers, Loader2, RefreshCw } from "lucide-react";
import { Skeleton } from "@/components/ui/skeleton";
import { IconAction } from "@/components/IconAction";
import { SearchInput } from "@/components/SearchInput";
import { cn } from "@/lib/utils";
import type { ExplorerKindCount, ExplorerTypes } from "../../../api/explorer";
import { GroupGlyph } from "./ExplorerGlyphs";
import { kindInScope, kindTitle, sameScope, scopeKind, typeTree, type ExplorerScope, type GroupNode } from "./explorerModel";
import { ReadingBar } from "./ReadingBar";

/** A group with more types than this stays folded while a search is in view, so a partition's long reference lists do not bury the rest. */
const OPEN_UP_TO = 24;

/** No group opened or folded by the reader yet. */
const UNDECIDED: ReadonlyMap<string, boolean> = new Map();

const compact = new Intl.NumberFormat("en-US", { notation: "compact", maximumFractionDigits: 1 });

/** A count as the rail shows it: exact up to a hundred thousand, compact past it, the exact count on hover. */
function Count({ value, quiet = false }: { value: number; quiet?: boolean }) {
  return (
    <span className={cn("ml-auto shrink-0 pl-2 font-mono text-[11px] tabular-nums", quiet ? "text-muted-foreground/70" : "text-muted-foreground")} title={value.toLocaleString("en-US")}>
      {value >= 100_000 ? compact.format(value) : value.toLocaleString("en-US")}
    </span>
  );
}

/**
 * One row of the rail: a caret where it folds, a glyph where it has one, the name, the count; the whole row picks what it
 * names. A row of no records (a type the Schema service lists that the search finds none of) is quiet.
 */
function Row({ depth, icon, label, title, count, selected, open, onToggle, onSelect, mono = false, testId }: {
  depth: number;
  icon?: ReactNode;
  label: string;
  title?: string;
  count: number;
  selected: boolean;
  open?: boolean;
  onToggle?: () => void;
  onSelect: () => void;
  mono?: boolean;
  testId: string;
}) {
  const empty = count === 0 && !selected;
  return (
    <div
      className={cn("group flex h-7 min-w-0 items-center rounded-md pr-2 text-[12px] hover:bg-accent/50", selected && "bg-accent text-accent-foreground")}
      style={{ paddingLeft: 4 + depth * 14 }}
      data-state={selected ? "active" : undefined}
      data-empty={count === 0 ? "true" : undefined}
      data-testid={testId}
    >
      {onToggle !== undefined
        ? (
          <button
            type="button"
            className="flex size-5 shrink-0 items-center justify-center rounded-sm text-muted-foreground hover:text-foreground"
            onClick={onToggle}
            aria-expanded={open}
            aria-label={open ? `Fold ${label}` : `Unfold ${label}`}
          >
            {open ? <ChevronDown className="size-3.5" /> : <ChevronRight className="size-3.5" />}
          </button>
        )
        : <span className="size-5 shrink-0" aria-hidden />}
      <button type="button" className="flex h-full min-w-0 flex-1 items-center gap-1.5 text-left" onClick={onSelect} title={title ?? label} aria-current={selected ? "true" : undefined}>
        {icon}
        <span className={cn("min-w-0 truncate", mono && "font-mono text-[11px]", empty && "text-muted-foreground")}>{label}</span>
        <Count value={count} quiet={!selected} />
      </button>
    </div>
  );
}

/**
 * The kinds the list shows: those the search finds records of (and with nothing typed, every kind the Schema service
 * lists), and the type or kind picked, with none where the search finds nothing there, so the list always says where the
 * reader is.
 */
function withPlace(kinds: ExplorerKindCount[], scope: ExplorerScope | null): ExplorerKindCount[] {
  if (scope === null || scope.level === "all" || scope.level === "group" || kinds.some((held) => kindInScope(held.kind, scope))) {
    return kinds;
  }

  return [...kinds, { kind: scopeKind(scope)!, count: 0 }];
}

/**
 * The kinds of the records a search finds, as a list to pick from: every type, then each group (master-data,
 * reference-data, work-product-component, ...) with its types, then the partition's own types that name no group, each
 * namespace (`acme:wells`) a group of them; a type kept in several kinds (versions, authorities) with those kinds under
 * it, each with how many records the search finds there. With nothing typed the list holds every kind the partition's
 * Schema service lists, those holding no record the search can read among them, quiet, so a type registered and not yet
 * delivered is there to pick. Picking one narrows the records to it; the counts follow the text and the values typed, so
 * the list also says where else a search finds something. A filter finds a type among hundreds, by its name or any part of
 * its kind (`acme`).
 *
 * The groups start folded, so the list opens as a short list of groups, but for the one holding the place picked. While a
 * search is in view, the groups it finds something in open (but for one of more than 24 types), so the list says where the
 * search found records; a filter opens every group it finds a type in. A group the reader opens or folds stays so until
 * the search changes.
 */
export function ExplorerTypeRail({ types, loading, error, scope, searchKey, onScope, onRefresh, refreshing = false }: {
  types: ExplorerTypes | undefined;
  loading: boolean;
  error: string | null;
  /** The place picked; null while the reader has picked none. */
  scope: ExplorerScope | null;
  /** The search the types are counted for, as text; null while every record is counted. */
  searchKey: string | null;
  onScope: (scope: ExplorerScope) => void;
  /** Lists the partition's schemas again and counts the types anew. */
  onRefresh?: () => void;
  refreshing?: boolean;
}) {
  const [filter, setFilter] = useState("");
  const groups = useMemo(() => typeTree(withPlace(types?.kinds ?? [], types === undefined ? null : scope)), [types, scope]);
  // The groups and types the reader opened or folded, for the search they did it under: another search starts afresh.
  const [decided, setDecided] = useState<{ search: string | null; open: ReadonlyMap<string, boolean> }>(() => ({ search: searchKey, open: UNDECIDED }));
  const decisions = decided.search === searchKey ? decided.open : UNDECIDED;
  const term = filter.trim().toLowerCase();
  const shown = useMemo((): GroupNode[] => (term === ""
    ? groups
    : groups
      .map((group) => ({
        ...group,
        types: group.types.filter((type) => type.entityType.toLowerCase().includes(term) || type.kinds.some((kind) => kind.kind.toLowerCase().includes(term))),
      }))
      .filter((group) => group.types.length > 0)), [groups, term]);

  const picked = (candidate: ExplorerScope) => scope !== null && sameScope(scope, candidate);
  // A group starts open when it holds the place picked, or while a search is in view and the group is not a long one; the
  // reader's own choice wins, and a filter opens them all.
  const startsOpen = (group: GroupNode) => (scope?.level === "group" && scope.group === group.group)
    || (scope?.level === "type" && group.types.some((type) => picked(type.scope)))
    || (scope?.level === "kind" && group.types.some((type) => type.kinds.some((kind) => kind.kind === scope.kind)))
    || (searchKey !== null && group.types.length <= OPEN_UP_TO);
  const isOpen = (key: string, starts: boolean) => term !== "" || (decisions.get(key) ?? starts);
  const toggle = (key: string, starts: boolean) => setDecided({
    search: searchKey,
    open: new Map(decisions).set(key, !(decisions.get(key) ?? starts)),
  });
  const schemas = types?.schemas ?? null;

  return (
    <div className="relative flex h-full min-h-0 flex-col" data-testid="explorer-types">
      {loading && <ReadingBar label="Counting the types in OSDU" />}
      <div className="flex items-center gap-1 border-b p-2">
        <SearchInput value={filter} onChange={setFilter} placeholder="Filter types" label="Filter the types" className="w-full sm:w-full" testId="explorer-types-filter" />
        {onRefresh !== undefined && (
          <IconAction
            label="List the partition's types again: the schemas registered since, and the records counted anew"
            icon={refreshing ? <Loader2 className="animate-spin" /> : <RefreshCw />}
            className="shrink-0 text-muted-foreground"
            onClick={onRefresh}
            disabled={refreshing || types === undefined}
            data-testid="explorer-types-refresh"
          />
        )}
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto p-1" data-testid="explorer-types-list">
        {error !== null && <p className="px-2 py-1 text-[12px] text-destructive" data-testid="explorer-types-error">{error}</p>}
        {types === undefined && loading && Array.from({ length: 12 }, (_, index) => <Skeleton key={index} className="mx-1 my-1.5 h-4 rounded" style={{ width: `${55 + ((index * 17) % 40)}%` }} />)}
        {types !== undefined && (
          <>
            <div
              className={cn("flex h-7 items-center gap-1.5 rounded-md px-2 text-[12px] hover:bg-accent/50", scope?.level === "all" && "bg-accent text-accent-foreground")}
              data-state={scope?.level === "all" ? "active" : undefined}
              data-testid="explorer-type-all"
            >
              <button type="button" className="flex h-full min-w-0 flex-1 items-center gap-1.5 text-left font-medium" onClick={() => onScope({ level: "all" })}>
                <Layers className="size-3.5 shrink-0 text-muted-foreground" />
                <span className="truncate">All types</span>
                <Count value={types.total} quiet={scope?.level !== "all"} />
              </button>
            </div>
            {shown.map((group) => {
              const groupScope: ExplorerScope = { level: "group", group: group.group };
              const groupStarts = startsOpen(group);
              const open = isOpen(group.group, groupStarts);
              return (
                <div key={group.group}>
                  <Row
                    depth={0}
                    icon={group.namespace ? <Boxes className="size-3.5 shrink-0 text-muted-foreground" aria-hidden /> : <GroupGlyph group={group.group} className="size-3.5" />}
                    label={group.group}
                    title={group.namespace ? `${group.group}: the types of authority ${group.group.split(":")[0]} and source ${group.group.split(":")[1]} that name no group` : group.group}
                    count={group.count}
                    selected={picked(groupScope)}
                    open={open}
                    onToggle={() => toggle(group.group, groupStarts)}
                    onSelect={() => onScope(groupScope)}
                    mono={group.namespace}
                    testId="explorer-type-group"
                  />
                  {open && group.types.map((type) => {
                    const versions = type.kinds.length > 1;
                    const typeKey = scopeKind(type.scope) ?? type.entityType;
                    const typeStarts = type.kinds.some((kind) => scope?.level === "kind" && kind.kind === scope.kind);
                    const typeOpen = versions && isOpen(typeKey, typeStarts);
                    return (
                      <div key={type.entityType}>
                        <Row
                          depth={1}
                          label={type.type}
                          title={versions ? type.entityType : kindTitle(type.kinds[0])}
                          count={type.count}
                          selected={picked(type.scope)}
                          open={versions ? typeOpen : undefined}
                          onToggle={versions ? () => toggle(typeKey, typeStarts) : undefined}
                          onSelect={() => onScope(type.scope)}
                          testId="explorer-type"
                        />
                        {typeOpen && type.kinds.map((kind) => (
                          <Row
                            key={kind.kind}
                            depth={2}
                            label={`${kind.parts.version} ${kind.parts.authority}:${kind.parts.source}`}
                            title={kindTitle(kind)}
                            count={kind.count}
                            selected={scope?.level === "kind" && scope.kind === kind.kind}
                            onSelect={() => onScope({ level: "kind", kind: kind.kind })}
                            mono
                            testId="explorer-type-kind"
                          />
                        ))}
                      </div>
                    );
                  })}
                </div>
              );
            })}
            {shown.length === 0 && (
              <p className="px-2 py-3 text-[12px] text-muted-foreground" data-testid="explorer-types-none">
                {term === "" ? "Nothing here holds what the search asks." : "No type matches the filter."}
              </p>
            )}
          </>
        )}
      </div>
      {schemas?.state === "reading" && (
        <div className="flex items-center gap-1.5 border-t px-3 py-1.5 text-[11px] text-muted-foreground" data-testid="explorer-types-listing">
          <Loader2 className="size-3 animate-spin" aria-hidden />
          Listing the partition's schemas, to add the types with no records
        </div>
      )}
      {schemas?.state === "failed" && (
        <div
          className="border-t px-3 py-1.5 text-[11px] text-muted-foreground"
          title={schemas.problem ?? undefined}
          data-testid="explorer-types-unlisted"
        >
          The Schema service could not be listed, so only the types holding records are here.
        </div>
      )}
      {types !== undefined && types.listed < types.total && (
        <div
          className="border-t px-3 py-1.5 text-[11px] text-muted-foreground"
          title="The search service names at most 1,000 kinds in one answer, and the Schema service lists none of these: records of kinds no schema is registered for."
          data-testid="explorer-types-partial"
        >
          {`${(types.total - types.listed).toLocaleString("en-US")} records in kinds not listed`}
        </div>
      )}
    </div>
  );
}
