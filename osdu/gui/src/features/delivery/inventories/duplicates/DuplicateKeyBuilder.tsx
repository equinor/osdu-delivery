import { useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from "react";
import { flushSync } from "react-dom";
import { useInfiniteQuery, useQuery, type InfiniteData } from "@tanstack/react-query";
import {
  ChevronDown, ChevronLeft, ChevronRight, ChevronsDownUp, ChevronsUpDown, Eraser, FileSearch, KeyRound, ListFilter, Loader2, LocateFixed, MousePointerClick,
  Plus, SearchX, TriangleAlert, X,
} from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { CopyButton } from "@/components/CopyButton";
import { EmptyState } from "@/components/EmptyState";
import { IconAction } from "@/components/IconAction";
import { RichTooltip } from "@/components/RichTooltip";
import { SearchInput } from "@/components/SearchInput";
import { cn } from "@/lib/utils";
import { explorerApi } from "../../../../api/explorer";
import { MAX_DUPLICATE_KEY, inventoryApi, type DuplicateKeyProperty, type Inventory, type InventoryRecordPage } from "../../../../api/inventories";
import { Highlight } from "../../OsduRecordInspector";
import { describeBranch, documentNode, idParts, recordNameOf, type RecordNode } from "../../osduRecordModel";
import { ProblemView, TaskProgress } from "../../TemplateSheet";
import { ExplorerAttributeList } from "../../explorer/ExplorerAttributeList";
import { useExplorerFields } from "../../explorer/explorerFields";
import { fieldLabel } from "../../explorer/explorerModel";
import { useWindowFit } from "../../useWindowFit";
import { KeyTitle } from "./DuplicateKeyParts";
import {
  FOLDED_AT_FIRST, ITEMS_PAGE, documentLines, pickedPath, revealOf, sectionsOf, valuesAt, type DocumentLine,
} from "./duplicateDocument";
import { withProperty } from "./duplicateModel";

/** The findings whose ids OSDU does not serve, so they are no record to pick a key in. */
const UNSERVED = new Set(["missing", "gone"]);

/** How many ids of the inventory are read at a time to step through. */
const IDS_PAGE = 100;

/** How many pages of ids a step forward reads at most looking for one OSDU serves, before it says there is none. */
const PAGES_PER_STEP = 5;

/** How much of a text value a line of the record shows; the rest is counted after it. */
const TEXT_SHOWN = 240;

/** How many of a property's values its row in the key shows, and how much of each. */
const VALUES_SHOWN = 4;
const VALUE_SHOWN = 80;

/** What stays under the builder: the card's edge, the page's padding and the workbench's status bar. */
const BELOW_BUILDER = 40;

/** The least height the builder keeps, so a short window still shows a screenful of the record. */
const MIN_BUILDER = 320;

/** How far each level of the record is indented. */
const INDENT = 16;

/** The ids of the pages read so far that OSDU serves, in the inventory's order. */
function servedOf(data: InfiniteData<InventoryRecordPage> | undefined): string[] {
  return (data?.pages ?? []).flatMap((page) => page.items).filter((record) => !UNSERVED.has(record.finding)).map((record) => record.targetId);
}

/** A value of the record as JSON writes it, a long text cut with what is left counted, the search marked. */
function ValueText({ value, term }: { value: unknown; term: string }) {
  if (typeof value === "string") {
    const cut = value.length > TEXT_SHOWN;
    return (
      <span className="text-foreground">
        &quot;<Highlight text={cut ? value.slice(0, TEXT_SHOWN) : value} term={term} />{cut ? "…" : ""}&quot;
        {cut && <span className="ml-1 font-sans text-[11px] text-muted-foreground">{`${value.length.toLocaleString("en-US")} characters`}</span>}
      </span>
    );
  }

  if (typeof value === "number") {
    return <span className="tabular-nums text-info"><Highlight text={String(value)} term={term} /></span>;
  }

  if (typeof value === "boolean") {
    return <span className="text-warning"><Highlight text={String(value)} term={term} /></span>;
  }

  return <span className="text-muted-foreground">null</span>;
}

/**
 * Scrolls a line to the middle of the record's own scrolling frame, and nothing else: the page around the builder stays
 * where it is.
 */
function centre(frame: HTMLElement | null, line: HTMLElement): void {
  if (frame === null) {
    return;
  }

  const offset = line.getBoundingClientRect().top - frame.getBoundingClientRect().top + frame.scrollTop;
  frame.scrollTop = Math.max(0, offset - (frame.clientHeight - line.offsetHeight) / 2);
}

/** The bracket a section opens and closes with. */
function brackets(node: RecordNode): [string, string] {
  return node.kind === "array" ? ["[", "]"] : ["{", "}"];
}

/** Why the explorer's list of the kind's properties not holding a property of the key matters, as its mark says it. */
function unlistedText(path: string, kind: string): string {
  return `${path} is not among the properties the explorer read for ${kind} (those its schema declares and those of the records it sampled). If OSDU's search does not return it, the check reads no value at it, and says so when it ends.`;
}

/** The values a property holds in the record, as its row in the key shows them. */
function valueText(value: unknown): string {
  const text = typeof value === "string" ? value : value === null ? "null" : typeof value === "object" ? JSON.stringify(value) : String(value);
  return text.length > VALUE_SHOWN ? `${text.slice(0, VALUE_SHOWN)}…` : text;
}

/**
 * One line of the record. A line that holds a value (or a list of values) is a button: a click adds its property to the key,
 * and a click on a line the key holds takes it out. The first line of a property of the key carries the way to leave it out;
 * every other line of it (a list's values, the same property in the list's other items) is marked. A line that can be no part
 * of a key says why on hover.
 */
function Line({ line, term, property, first, disabled, onToggle, onFold, onMore }: {
  line: DocumentLine;
  term: string;
  /** The property of the key the line holds; undefined for a line the key does not hold. */
  property: DuplicateKeyProperty | undefined;
  /** Whether this is the first line of that property, which carries the way to leave it out. */
  first: boolean;
  disabled: boolean;
  onToggle: (line: DocumentLine) => void;
  onFold: (path: string) => void;
  onMore: (line: DocumentLine) => void;
}) {
  const { node, kind, depth } = line;
  const path = pickedPath(line.pick);
  const problem = line.pick !== null && "problem" in line.pick ? line.pick.problem : null;
  const picked = property !== undefined;
  const [open, close] = node.kind === "leaf" ? ["", ""] : brackets(node);
  const comma = line.comma ? <span className="text-muted-foreground">,</span> : null;
  const lead = 20 + depth * INDENT + 16;

  if (kind === "more") {
    return (
      <div className="flex min-h-6 items-center" style={{ paddingLeft: lead }} data-testid="duplicate-key-line-more">
        <button type="button" className="cursor-pointer font-sans text-[11.5px] text-primary hover:underline" onClick={() => onMore(line)}>
          {(line.hidden ?? 0) <= ITEMS_PAGE
            ? `Show ${(line.hidden ?? 0).toLocaleString("en-US")} more ${line.hidden === 1 ? "item" : "items"}`
            : `Show ${ITEMS_PAGE.toLocaleString("en-US")} more of ${(line.hidden ?? 0).toLocaleString("en-US")} items`}
        </button>
      </div>
    );
  }

  if (kind === "close") {
    return (
      <div className="flex min-h-6 items-center" style={{ paddingLeft: lead }}>
        <span className="text-muted-foreground">{close}</span>{comma}
      </div>
    );
  }

  const pickable = path !== null && !disabled;
  const folded = kind === "folded";
  // The comma stays with what it follows, so a long value that wraps ends with it rather than leaving it on the first line.
  const content = (
    <>
      {line.name !== null && <span className="text-muted-foreground">&quot;<Highlight text={line.name} term={term} />&quot;: </span>}
      {kind === "value" && <ValueText value={node.value} term={term} />}
      {kind === "empty" && <span className="text-muted-foreground">{`${open}${close}`}</span>}
      {(kind === "open" || folded) && <span className="text-muted-foreground">{open}</span>}
      {!folded && comma}
    </>
  );
  const caret = kind === "open" || folded;
  return (
    <div
      className={cn(
        "group/line flex min-w-0 items-start rounded-sm",
        picked && (first ? "bg-primary/12" : "bg-primary/6"),
        pickable && !picked && "hover:bg-accent/70",
      )}
      data-testid="duplicate-key-line"
      data-path={node.path}
      data-key-path={path ?? undefined}
      data-picked={picked ? "true" : undefined}
    >
      {/* The gutter marks the key's lines, and on hover a line a click would add to it. */}
      <span className="flex h-6 w-5 shrink-0 items-center justify-center" aria-hidden>
        {picked
          ? <KeyRound className={cn("size-3", first ? "text-primary" : "text-primary/50")} />
          : pickable && <KeyRound className="size-3 text-muted-foreground opacity-0 group-hover/line:opacity-70" />}
      </span>
      <span className="shrink-0" style={{ width: depth * INDENT }} aria-hidden />
      <span className="flex h-6 w-4 shrink-0 items-center justify-center">
        {caret && (
          <button
            type="button"
            className="text-muted-foreground hover:text-foreground"
            onClick={() => onFold(node.path)}
            aria-label={kind === "open" ? `Fold ${node.path}` : `Unfold ${node.path}`}
            aria-expanded={kind === "open"}
            data-testid="duplicate-key-line-fold"
          >
            {kind === "open" ? <ChevronDown className="size-3.5" /> : <ChevronRight className="size-3.5" />}
          </button>
        )}
      </span>
      <span className="flex min-w-0 flex-1 items-start gap-2 pr-2">
        {pickable
          ? (
            <button
              type="button"
              className="min-w-0 cursor-pointer whitespace-pre-wrap py-0.5 text-left outline-none [overflow-wrap:anywhere] focus-visible:rounded-sm focus-visible:ring-2 focus-visible:ring-ring/50"
              onClick={() => onToggle(line)}
              aria-pressed={picked}
              aria-label={picked ? `Take ${path} out of the key` : `Add ${path} to the key`}
              data-testid="duplicate-key-line-pick"
            >
              {content}
            </button>
          )
          : <span className="min-w-0 whitespace-pre-wrap py-0.5 [overflow-wrap:anywhere]" title={problem ?? undefined}>{content}</span>}
        {folded && (
          <span className="-ml-2 flex h-6 shrink-0 items-center">
            <button type="button" className="mx-1 rounded bg-muted px-1 font-sans text-[11px] text-muted-foreground hover:text-foreground" onClick={() => onFold(node.path)} data-testid="duplicate-key-line-unfold">
              {describeBranch(node)}
            </button>
            <span className="text-muted-foreground">{close}</span>{comma}
          </span>
        )}
        {picked && first && (
          <span className="flex h-6 shrink-0 items-center font-sans">
            <button
              type="button"
              onClick={() => onToggle(line)}
              disabled={disabled}
              className="inline-flex h-5 items-center gap-0.5 rounded border border-primary/40 bg-card pl-1 pr-0.5 text-[11px] text-muted-foreground shadow-xs outline-none hover:bg-accent hover:text-foreground focus-visible:bg-accent disabled:pointer-events-none"
              aria-label={`Leave ${fieldLabel(property.path)} out of the key`}
              data-testid="duplicate-key-line-remove"
            >
              key
              <X className="size-3" aria-hidden />
            </button>
          </span>
        )}
        {pickable && !picked && (
          <span className="flex h-6 shrink-0 items-center font-sans opacity-0 transition-opacity group-hover/line:opacity-100" aria-hidden>
            <span className="inline-flex items-center gap-0.5 rounded border border-dashed border-primary/50 px-1 text-[11px] leading-4 text-primary">
              <Plus className="size-3" />
              key
            </span>
          </span>
        )}
      </span>
    </div>
  );
}

/** The way to show any record by its OSDU id, which need not be one of the inventory's. */
function OpenId({ onOpen, label = "Open an id" }: { onOpen: (id: string) => void; label?: string }) {
  const [open, setOpen] = useState(false);
  const [text, setText] = useState("");
  const submit = (event: FormEvent) => {
    event.preventDefault();
    const id = text.trim();
    if (id !== "") {
      onOpen(id);
      setOpen(false);
      setText("");
    }
  };

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button variant="ghost" size="sm" className="h-7 gap-1 px-2 text-[12.5px] text-muted-foreground hover:text-foreground" title="Show any record of the kind by its OSDU id" data-testid="duplicate-key-document-open">
          <FileSearch className="size-3.5" />
          {label}
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-[28rem] p-2">
        <form onSubmit={submit} className="flex items-center gap-2">
          <Input
            autoFocus
            value={text}
            onChange={(event) => setText(event.target.value)}
            placeholder="The record's OSDU id"
            aria-label="The OSDU id of the record to pick the key in"
            className="h-8 font-mono text-[12px]"
            data-testid="duplicate-key-document-open-id"
          />
          <Button type="submit" size="sm" className="h-8" disabled={text.trim() === ""}>Show</Button>
        </form>
      </PopoverContent>
    </Popover>
  );
}

/**
 * One property of the key being built: its name, which shows its first line in the record; its path; the values the record
 * shown holds there, which are what a copy of it would repeat; and the way to leave it out. A property the record holds no
 * value at says so, one the explorer does not list for the kind carries a warning, and one the check in view did not download
 * is marked, since a key holding it downloads again.
 */
function KeyRow({ property, values, held, unlisted, fresh, disabled, onReveal, onRemove }: {
  property: DuplicateKeyProperty;
  /** What the record shown holds at it; null while no record is shown. */
  values: unknown[] | null;
  /** Whether the record has a line of it to show. */
  held: boolean;
  unlisted: string | null;
  /** The check in view that downloaded other properties and not this one; null when there is none, or it downloaded this one. */
  fresh: number | null;
  disabled: boolean;
  onReveal: () => void;
  onRemove: () => void;
}) {
  return (
    <li className="group/key border-b border-border px-3 py-2 last:border-b-0" data-testid="duplicate-key-row" data-path={property.path}>
      <div className="flex min-w-0 items-center gap-1.5">
        <KeyRound className="size-3.5 shrink-0 text-primary" aria-hidden />
        <button
          type="button"
          className="min-w-0 flex-1 truncate text-left text-[13px] font-medium outline-none hover:underline focus-visible:underline disabled:no-underline"
          onClick={onReveal}
          disabled={!held}
          title={held ? `Show ${property.path} in the record` : `The record shown holds no value at ${property.path}`}
          data-testid="duplicate-key-row-reveal"
        >
          {fieldLabel(property.path)}
        </button>
        {unlisted !== null && (
          <RichTooltip title="The search may not return it" body={unlisted}>
            <span className="inline-flex shrink-0 text-warning" data-testid="duplicate-key-row-unlisted"><TriangleAlert className="size-3.5" aria-label="The search may not return it" /></span>
          </RichTooltip>
        )}
        {fresh !== null && (
          <RichTooltip title="To download" body={`Check ${fresh} did not download ${property.path}, so a key holding it downloads the key's properties of every id again, through the search.`}>
            <span className="shrink-0 rounded border border-dashed border-border px-1 text-[10.5px] leading-4 text-muted-foreground" data-testid="duplicate-key-row-fresh">to download</span>
          </RichTooltip>
        )}
        <button
          type="button"
          onClick={onRemove}
          disabled={disabled}
          className="flex size-6 shrink-0 items-center justify-center rounded-md text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:bg-accent disabled:pointer-events-none"
          aria-label={`Leave ${fieldLabel(property.path)} out of the key`}
          data-testid="duplicate-key-remove"
        >
          <X className="size-3.5" aria-hidden />
        </button>
      </div>
      <div className="mt-0.5 flex min-w-0 items-center gap-1 pl-5 font-mono text-[11px] text-muted-foreground">
        <span className="min-w-0 truncate" title={property.path}>{property.path}</span>
        {values !== null && values.length > 1 && (
          <span className="shrink-0 font-sans" data-testid="duplicate-key-row-many">{`\u00b7 ${values.length.toLocaleString("en-US")} values, in any order`}</span>
        )}
        {held && (
          <button type="button" className="shrink-0 rounded p-0.5 opacity-0 hover:text-foreground focus-visible:opacity-100 group-hover/key:opacity-100" onClick={onReveal} aria-label={`Show ${property.path} in the record`}>
            <LocateFixed className="size-3" />
          </button>
        )}
      </div>
      {values !== null && (
        <div className="mt-1 flex min-w-0 flex-wrap items-center gap-1 pl-5" data-testid="duplicate-key-row-values" data-count={values.length}>
          {values.length === 0
            ? <span className="text-[11.5px] italic text-muted-foreground">no value in this record</span>
            : (
              <>
                {values.slice(0, VALUES_SHOWN).map((value, index) => (
                  <span
                    // The record's values in its order; one may repeat, so its place names it.
                    key={index}
                    className={cn(
                      "max-w-full truncate rounded border border-border bg-muted/50 px-1.5 font-mono text-[11.5px] leading-5",
                      typeof value === "number" ? "text-info" : typeof value === "boolean" ? "text-warning" : value === null ? "text-muted-foreground" : "text-foreground",
                    )}
                    title={typeof value === "string" ? value : JSON.stringify(value)}
                  >
                    {valueText(value)}
                  </span>
                ))}
                {values.length > VALUES_SHOWN && <span className="text-[11.5px] text-muted-foreground">{`+${(values.length - VALUES_SHOWN).toLocaleString("en-US")} more`}</span>}
              </>
            )}
        </div>
      )}
    </li>
  );
}

/**
 * Where a duplicate key is built (osdu/docs/reference/flow/inventory-duplicates.md, In the GUI): one of the inventory's
 * records as OSDU holds it, read through the partition's connection and shown as its JSON, beside the key. A click on a value
 * adds its property to the key, a click on it again takes it out, and each property is marked where it stands in the record
 * with the way to leave it out. The key lists its properties with the values the record holds there, so the key reads as what
 * the copies of this record would repeat; a property's name shows its first line in the record, and a property the check in
 * view did not download (`downloaded`) is marked, since a key holding it downloads again.
 *
 * The record's line steps through the inventory's ids or opens any id (`sampleId`, which the page's address keeps), searches
 * the record's names and values, narrows it to what the key compares, and folds it. The sections the copies of a record rarely
 * tell apart by (acl, legal, meta, ancestry) start folded and a list shows its first items, so a record of hundreds of
 * properties opens on what tells its copies apart. A property the record holds no value at is added by its name from the
 * kind's list. The key's own actions (finding the duplicates) end it (`actions`).
 */
export function DuplicateKeyBuilder({ inventory, served, draft, downloaded, onChange, disabled, sampleId, onSampleId, actions }: {
  inventory: Inventory;
  /** How many of the inventory's ids OSDU serves (every finding but missing and gone), for the place of the one shown. */
  served: number;
  draft: readonly DuplicateKeyProperty[];
  /** The check in view that downloaded properties, with them; null when there is none. */
  downloaded: { checkId: number; paths: ReadonlySet<string> } | null;
  /**
   * Changes the key: the change is given the key as last changed, which the page may not have drawn yet, so clicks closer
   * together than the address follows build on one another.
   */
  onChange: (change: (current: readonly DuplicateKeyProperty[]) => readonly DuplicateKeyProperty[]) => void;
  disabled: boolean;
  /** The record shown, as the address names it; null shows the first id of the inventory OSDU serves. */
  sampleId: string | null;
  onSampleId: (id: string | null) => void;
  actions: ReactNode;
}) {
  const { partition, inventoryId } = inventory;
  const ids = useInfiniteQuery({
    queryKey: ["delivery", "inventories", "duplicates", "samples", partition, inventoryId],
    queryFn: ({ pageParam }) => inventoryApi.records(partition, inventoryId, pageParam === undefined ? { limit: IDS_PAGE } : { after: pageParam, limit: IDS_PAGE }),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: (page) => page.next,
    staleTime: 5 * 60_000,
  });
  const servedIds = useMemo(() => servedOf(ids.data), [ids.data]);
  const shownId = sampleId ?? servedIds[0] ?? null;
  const at = shownId === null ? -1 : servedIds.indexOf(shownId);
  const [stepping, setStepping] = useState(false);

  const read = useQuery({
    // The explorer's own key for a record at its latest, so a record read there or in the inventory's panel is read once.
    queryKey: ["explorer", "read", partition, shownId, null],
    queryFn: () => explorerApi.read(partition, shownId!),
    enabled: shownId !== null,
    staleTime: Number.POSITIVE_INFINITY,
    gcTime: 60_000,
    retry: false,
    refetchOnWindowFocus: false,
  });
  const record = read.data?.found === true ? read.data.record ?? null : null;
  const root = useMemo(() => (record === null ? null : documentNode(record)), [record]);

  const fields = useExplorerFields(partition, inventory.kind, true);
  const listed = useMemo(() => {
    const answer = fields.data?.answer;
    return answer === undefined ? null : new Set(answer.fields.map((field) => field.path));
  }, [fields.data]);

  const [folded, setFolded] = useState<ReadonlySet<string>>(() => new Set(FOLDED_AT_FIRST));
  const [shown, setShown] = useState<ReadonlyMap<string, number>>(() => new Map());
  const [search, setSearch] = useState("");
  const [onlyKey, setOnlyKey] = useState(false);
  const [adding, setAdding] = useState(false);
  const term = search.trim().toLowerCase();
  const keyed = useMemo(() => new Map(draft.map((property) => [property.path, property])), [draft]);
  const only = onlyKey && draft.length > 0;
  const drawn = useMemo(
    () => (root === null ? null : documentLines(root, { folded, shown, term, only: only ? new Set(keyed.keys()) : null })),
    [root, folded, shown, term, only, keyed]);

  const frame = useRef<HTMLDivElement>(null);
  const scroller = useRef<HTMLDivElement>(null);
  useWindowFit(frame, BELOW_BUILDER, MIN_BUILDER, undefined, "height");

  // Each record opens on the key's first line, so stepping through records shows what each holds at the key; a record
  // holding none of it, or a key still empty, opens at its top.
  const landed = useRef<string | null>(null);
  useEffect(() => {
    if (root === null || shownId === null || landed.current === shownId) {
      return;
    }

    landed.current = shownId;
    const first = scroller.current?.querySelector<HTMLElement>('[data-testid="duplicate-key-line"][data-picked="true"]');
    if (first !== null && first !== undefined) {
      centre(scroller.current, first);
    } else {
      scroller.current?.scrollTo({ top: 0 });
    }
  }, [root, shownId]);

  // Adds a property after the others, or says why the key cannot take it; whether it was added.
  const add = (path: string): boolean => {
    let added = false;
    onChange((current) => {
      const next = withProperty(current, path);
      if ("problem" in next) {
        toast.error(`${fieldLabel(path)} cannot join the key: ${next.problem}`);
        return current;
      }

      added = true;
      return next.key;
    });
    return added;
  };

  const remove = (path: string) => onChange((current) => current.filter((property) => property.path !== path));

  // A line's property joins the key, or leaves it when the key compares it already.
  const toggle = (line: DocumentLine) => {
    const path = pickedPath(line.pick);
    if (path === null) {
      return;
    }

    onChange((current) => {
      if (current.some((property) => property.path === path)) {
        return current.filter((property) => property.path !== path);
      }

      const next = withProperty(current, path);
      if ("problem" in next) {
        toast.error(`${fieldLabel(path)} cannot join the key: ${next.problem}`);
        return current;
      }

      return next.key;
    });
  };

  const fold = (path: string) => setFolded((current) => {
    const next = new Set(current);
    if (next.has(path)) {
      next.delete(path);
    } else {
      next.add(path);
    }

    return next;
  });

  const more = (line: DocumentLine) => setShown((current) => new Map(current).set(line.node.path, (line.showing ?? 0) + ITEMS_PAGE));

  // Shows a property's first line: whatever hides it (a search, the key alone, a fold, a list's first items) gives way, the
  // line is scrolled to the middle of the record, and it flashes once so the eye finds it.
  const reveal = (path: string) => {
    if (root === null) {
      return;
    }

    const place = revealOf(root, path);
    if (place === null) {
      return;
    }

    flushSync(() => {
      setSearch("");
      setOnlyKey(false);
      setFolded((current) => new Set([...current].filter((section) => !place.sections.includes(section))));
      setShown((current) => {
        const next = new Map(current);
        for (const [list, count] of place.items) {
          next.set(list, Math.max(next.get(list) ?? 0, count));
        }

        return next;
      });
    });
    const element = scroller.current?.querySelector<HTMLElement>(`[data-testid="duplicate-key-line"][data-path="${CSS.escape(place.line)}"]`);
    if (element !== null && element !== undefined) {
      centre(scroller.current, element);
      element.animate(
        [{ boxShadow: "inset 0 0 0 2px var(--primary)" }, { boxShadow: "inset 0 0 0 2px transparent" }],
        { duration: 1600, easing: "ease-out" },
      );
    }
  };

  // A step forward past the ids read so far reads the next pages, until one holds an id OSDU serves or the ids run out.
  const forward = async () => {
    if (at + 1 < servedIds.length) {
      onSampleId(servedIds[at + 1]);
      return;
    }

    setStepping(true);
    try {
      let data = ids.data;
      for (let page = 0; page < PAGES_PER_STEP && data?.pages.at(-1)?.next !== undefined; page += 1) {
        data = (await ids.fetchNextPage()).data;
        const next = servedOf(data)[at + 1];
        if (next !== undefined) {
          onSampleId(next);
          return;
        }
      }

      toast.info("The inventory serves no other id after this one.");
    } catch (error) {
      toast.error(`The inventory's next ids could not be read: ${error instanceof Error ? error.message : String(error)}`);
    } finally {
      setStepping(false);
    }
  };

  const canForward = at + 1 < servedIds.length || ids.hasNextPage;
  const recordName = record === null ? null : recordNameOf(record);

  const recordBar = (
    <div className="flex min-w-0 flex-wrap items-center gap-x-1.5 gap-y-1 border-b border-border px-2 py-1.5" data-testid="duplicate-key-document-bar">
      <span className="flex min-w-[19rem] flex-1 items-center gap-1.5 text-[12.5px]">
        <span className="shrink-0 pl-1 text-[12px] font-medium text-muted-foreground">Record</span>
        {shownId !== null
          ? (
            // The record by its name, or by the unique part of its id where it holds none; the whole id on hover, to copy.
            <span className="flex min-w-0 items-center gap-0.5" data-testid="duplicate-key-document-record" data-id={shownId}>
              <RichTooltip title={recordName ?? "The record's id"} body={shownId} mono>
                <span className={cn("min-w-0 truncate", recordName === null ? "font-mono text-[12px]" : "font-medium")}>{recordName ?? idParts(shownId).unique}</span>
              </RichTooltip>
              <CopyButton iconOnly label="Copy the record's id" text={shownId} testId="duplicate-key-document-copy" />
            </span>
          )
          : <span className="text-muted-foreground">none yet</span>}
        {at >= 0 && (
          <span className="shrink-0 font-mono text-[11.5px] tabular-nums text-muted-foreground" data-testid="duplicate-key-document-place">
            {`${(at + 1).toLocaleString("en-US")} of ${served.toLocaleString("en-US")}`}
          </span>
        )}
        <span className="flex shrink-0 items-center">
          <IconAction label="The id before" icon={<ChevronLeft />} disabled={at <= 0} onClick={() => onSampleId(servedIds[at - 1])} className="size-7" data-testid="duplicate-key-document-back" />
          <IconAction
            label="The id after"
            icon={stepping ? <Loader2 className="animate-spin" /> : <ChevronRight />}
            disabled={!canForward || stepping}
            onClick={() => void forward()}
            className="size-7"
            data-testid="duplicate-key-document-forward"
          />
        </span>
        <OpenId onOpen={onSampleId} />
      </span>
      <span className="ml-auto flex shrink-0 items-center gap-1">
        <SearchInput value={search} onChange={setSearch} placeholder="Find a property or value" label="Find a property or value in the record" className="h-8 sm:w-56" testId="duplicate-key-document-search" />
        {term !== "" && drawn !== null && (
          <span className="shrink-0 px-1 text-[11.5px] tabular-nums text-muted-foreground" data-testid="duplicate-key-document-matches">
            {drawn.matches === 0 ? "no match" : `${drawn.matches.toLocaleString("en-US")} ${drawn.matches === 1 ? "match" : "matches"}`}
          </span>
        )}
        <Button
          variant={only ? "secondary" : "ghost"}
          size="sm"
          className="h-7 gap-1 px-2 text-[12.5px]"
          disabled={draft.length === 0}
          aria-pressed={only}
          onClick={() => setOnlyKey((was) => !was)}
          title={draft.length === 0 ? "The key compares no property yet" : only ? "Show every line of the record" : "Show only the lines the key compares"}
          data-testid="duplicate-key-document-only"
        >
          <ListFilter className="size-3.5" />
          Only the key
        </Button>
        <IconAction label="Fold every section" icon={<ChevronsDownUp />} disabled={root === null} onClick={() => root !== null && setFolded(sectionsOf(root))} className="size-7" data-testid="duplicate-key-document-fold-all" />
        <IconAction label="Unfold every section" icon={<ChevronsUpDown />} disabled={root === null} onClick={() => setFolded(new Set())} className="size-7" data-testid="duplicate-key-document-unfold-all" />
      </span>
    </div>
  );

  let recordView: ReactNode;
  if (shownId === null) {
    recordView = ids.isPending
      ? <div className="p-3"><TaskProgress label="Reading the inventory's ids" testId="duplicate-key-document-ids" /></div>
      : ids.isError
        ? <div className="p-3"><ProblemView error={ids.error} testId="duplicate-key-document-ids-error" /></div>
        : (
          <EmptyState
            icon={<SearchX />}
            title="No id of the inventory is a record OSDU serves"
            description="Every id the inventory holds is missing or gone. Open a record of the kind by its id to pick the key in, or build the inventory again."
            action={<OpenId onOpen={onSampleId} label="Open a record by its id" />}
            data-testid="duplicate-key-document-no-ids"
          />
        );
  } else if (read.isError) {
    recordView = <div className="p-3"><ProblemView error={read.error} testId="duplicate-key-document-error" /></div>;
  } else if (read.data === undefined) {
    recordView = <div className="p-3"><TaskProgress label={`Reading ${shownId} from OSDU`} testId="duplicate-key-document-reading" /></div>;
  } else if (root === null) {
    recordView = (
      <EmptyState
        icon={<SearchX />}
        title="OSDU holds no record under this id"
        description={`${shownId} is not served in ${partition}: it was deleted since the inventory's build, or it is not an id of the partition.`}
        action={canForward ? <Button size="sm" variant="outline" onClick={() => void forward()} disabled={stepping}>The id after</Button> : undefined}
        data-testid="duplicate-key-document-not-found"
      />
    );
  } else if (drawn === null || drawn.lines.length === 0) {
    recordView = (
      <EmptyState
        icon={<SearchX />}
        title={term !== "" ? `No name or value of this record holds "${search.trim()}"` : "This record holds none of the key's properties"}
        description={term !== "" ? "The search reads every name and value of the record, folded or not." : "Show every line, or pick the key in another record."}
        data-testid="duplicate-key-document-nothing"
      />
    );
  } else {
    const controlled = new Set<string>();
    recordView = (
      <div className="py-1 pr-1 font-mono text-[12px] leading-5" data-testid="duplicate-key-document-lines">
        {drawn.lines.map((line) => {
          const path = pickedPath(line.pick);
          const property = path === null || line.kind === "close" || line.kind === "more" ? undefined : keyed.get(path);
          const first = property !== undefined && !controlled.has(property.path);
          if (first) {
            controlled.add(property.path);
          }

          return (
            <Line
              key={line.id}
              line={line}
              term={term}
              property={property}
              first={first}
              disabled={disabled}
              onToggle={toggle}
              onFold={fold}
              onMore={more}
            />
          );
        })}
      </div>
    );
  }

  const keyPane = (
    <div className="flex w-80 shrink-0 flex-col border-l border-border bg-muted/20 xl:w-[22rem]" data-testid="duplicate-key-pane">
      <div className="flex h-11 shrink-0 items-center gap-1 border-b border-border pl-2 pr-1.5">
        <KeyTitle className="text-[12.5px] text-foreground" />
        {draft.length > 0 && (
          <span
            className="rounded-full bg-primary/10 px-1.5 font-mono text-[11px] tabular-nums leading-4 text-foreground"
            title={`${draft.length} of the ${MAX_DUPLICATE_KEY} properties a key compares at most`}
            data-testid="duplicate-key-count"
          >
            {draft.length}
          </span>
        )}
        <Button
          variant="ghost"
          size="sm"
          className="ml-auto h-7 gap-1 px-2 text-[12.5px] text-muted-foreground hover:text-foreground"
          disabled={disabled || draft.length === 0}
          onClick={() => onChange(() => [])}
          title={draft.length === 0 ? "The key holds no property" : "Take every property out of the key, and start it again"}
          data-testid="duplicate-key-clear"
        >
          <Eraser className="size-3.5" />
          Clear
        </Button>
        <Popover open={adding} onOpenChange={setAdding}>
          <PopoverTrigger asChild>
            <Button
              variant="ghost"
              size="sm"
              className="h-7 gap-1 px-2 text-[12.5px] text-muted-foreground hover:text-foreground"
              disabled={disabled || draft.length >= MAX_DUPLICATE_KEY}
              title="Add a property the record shown holds no value at, from the kind's properties"
              data-testid="duplicate-key-by-name"
            >
              <Plus className="size-3.5" />
              By name
            </Button>
          </PopoverTrigger>
          <PopoverContent align="end" className="w-[26rem] p-0" data-testid="duplicate-key-add-panel">
            {adding && (
              <ExplorerAttributeList
                read={fields}
                kind={inventory.kind}
                onPick={(field) => {
                  if (add(field.path)) {
                    setAdding(false);
                  }
                }}
                placeholder="Find a property of the kind's records"
                testId="duplicate-key-properties"
              />
            )}
          </PopoverContent>
        </Popover>
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto">
        {draft.length === 0
          ? (
            <div className="flex flex-col items-center gap-2 px-6 py-10 text-center" data-testid="duplicate-key-empty">
              <MousePointerClick className="size-8 stroke-[1.25] text-muted-foreground/60" aria-hidden />
              <div className="text-[13px] font-medium text-muted-foreground">Click a value in the record</div>
              <p className="text-[12px] leading-relaxed text-muted-foreground/90">
                Its property joins the key. Two records are copies of one another when they hold equal values at every property
                of the key, so pick what every copy of a record repeats: its name, the record it belongs to, its source&apos;s own id.
              </p>
            </div>
          )
          : (
            <ul data-testid="duplicate-key-list">
              {draft.map((property) => {
                const values = record === null ? null : valuesAt(record, property.path);
                const held = root !== null && revealOf(root, property.path) !== null;
                const unlisted = listed !== null && property.path.startsWith("data.") && !listed.has(property.path) ? unlistedText(property.path, inventory.kind) : null;
                return (
                  <KeyRow
                    key={property.path}
                    property={property}
                    values={values}
                    held={held}
                    unlisted={unlisted}
                    fresh={downloaded === null || downloaded.paths.has(property.path) ? null : downloaded.checkId}
                    disabled={disabled}
                    onReveal={() => reveal(property.path)}
                    onRemove={() => remove(property.path)}
                  />
                );
              })}
            </ul>
          )}
      </div>
      <div className="flex shrink-0 flex-col gap-1.5 border-t border-border p-2" data-testid="duplicate-key-actions">{actions}</div>
    </div>
  );

  return (
    <div ref={frame} className="flex min-h-0 min-w-0" data-testid="duplicate-key-builder" data-sample={shownId ?? undefined}>
      <div className="flex min-w-0 flex-1 flex-col" data-testid="duplicate-key-document">
        {recordBar}
        <div ref={scroller} className="min-h-0 flex-1 overflow-auto">{recordView}</div>
      </div>
      {keyPane}
    </div>
  );
}
