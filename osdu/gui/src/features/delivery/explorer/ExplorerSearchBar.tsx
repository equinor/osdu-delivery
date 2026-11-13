import { useEffect, useId, useRef, useState, type FormEvent, type KeyboardEvent, type ReactNode } from "react";
import { Braces, Columns3, CornerDownLeft, History, ListFilter, Loader2, Search, TextSearch, Trash2, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { RelativeTime } from "@/components/RelativeTime";
import { cn } from "@/lib/utils";
import type { ExplorerFieldInfo, ExplorerFields, ExplorerSearchRequest } from "../../../api/explorer";
import { isRecordIdIn, isRecordReference } from "../osduDocument";
import { idParts } from "../osduRecordModel";
import { RecordName } from "../RecordName";
import { ExplorerErrorText } from "./ExplorerProblem";
import { ExplorerSearchScope } from "./ExplorerSearchScope";
import { nestedLabel } from "./explorerFields";
import { useHeldValues, type HeldValues } from "./explorerHeld";
import { CONDITION_LABELS, fieldLabel, forgetRecentRecords, kindParts, recentRecords, searchInCondition, type RecentRecord } from "./explorerModel";
import { scopeHeld, scopeName, scopeTitle, scopeTypedCondition, type SearchScopeTarget } from "./explorerScope";
import { termSearchInCondition, termTitle, type OfferedTerm } from "./explorerTerms";

/** The values of the attribute searched in listed under the field at most, as text is typed. */
const SCOPED_VALUES = 8;

/** What the values held are read within while no attribute is picked: nothing, since none are read then. */
const NO_SEARCH: ExplorerSearchRequest = { filters: [] };
const NO_FIELD: ExplorerFieldInfo = { path: "id", index: "keyword" };

/**
 * Where a place's search field searches: every property (as it always does), or one attribute picked at its start, a source
 * column (osdu/docs/reference/concepts/search-terms.md) or a property. Picked, what is typed is searched in that
 * attribute alone, as a condition of the list that replaces the one the attribute had, with the values the attribute
 * holds listed under the field.
 */
export interface SearchScope {
  /** The attribute searched in; null for every property. */
  target: SearchScopeTarget | null;
  /** The source columns of the place's type, offered first. */
  terms: OfferedTerm[];
  /** The properties of the place's records. */
  fields: { data?: { answer: ExplorerFields }; isPending: boolean; isError: boolean; error: unknown };
  kind: string | undefined;
  partition: string | null;
  /** The search the values held are counted within: the one in view, without its text and the attribute's own condition. */
  base: ExplorerSearchRequest;
  /** Called as the choice opens, so the properties and the source columns are read only once they are wanted. */
  onWanted: () => void;
  onChoose: (target: SearchScopeTarget | null) => void;
  /** Searches `value` in the attribute: as typed (its words, its start or its whole value), or `exact`, a value picked. */
  onSearch: (target: SearchScopeTarget, value: string, exact: boolean) => void;
}

/** What a search field offers beside searching every property: the text searched in one property, picked under it. */
export interface SearchIn {
  /** The properties offered first, the likeliest first. */
  choices: ExplorerFieldInfo[];
  /**
   * The source columns (search terms, osdu/docs/reference/concepts/search-terms.md) offered after the properties: those
   * searched in lately for the type. None where the place is of many types.
   */
  terms?: OfferedTerm[];
  /** Whether the type has source columns to search in, which the editor lists beside its properties. */
  hasTerms?: boolean;
  /** Whether the properties are still being read. */
  reading: boolean;
  /** Called as the reader types, so the properties are read only once they may be wanted. */
  onWanted: () => void;
  /** Searches the text in one property, as a condition of the list. */
  onSearchIn: (field: ExplorerFieldInfo, text: string) => void;
  /** Searches the text in one source column, as a condition of the list, the text as the source holds it. */
  onSearchInTerm?: (term: OfferedTerm, text: string) => void;
}

/**
 * A search field of the explorer: the page's own, which searches every type, and the one over the records of a group, a
 * type or a kind picked, which searches inside it. What is typed is read when Enter is pressed: a whole record id opens
 * that record, and anything else searches the records, as an id, the start of one, or words found anywhere in a record.
 * The braces switch it to a Lucene query, sent as written. A hint at the end of the field says what Enter will do, so the
 * reader is never surprised. The cross and Escape clear the field and the search it shows, as Enter does on the field
 * emptied, so the records are whole again. The field shows the search in the address, so going back restores it, and a
 * search sent leaves the cursor where it was. Asked to (`focusRequest`), it takes the cursor to the end of what it shows,
 * so a query the page put there is edited at once.
 */
export function ExplorerSearchBar({ text, lucene, placeholder, label, onSearch, onOpenId, searchIn, scope, shortcut = false, focusRequest = 0, className, testId }: {
  text: string;
  lucene: boolean;
  placeholder: string;
  /** The field's accessible name, which tells the page's field from the one over the records. */
  label: string;
  onSearch: (text: string, lucene: boolean) => void;
  onOpenId: (id: string) => void;
  /** Searching the text in one property, offered under the field as it is typed; none where the field searches every property alone. */
  searchIn?: SearchIn;
  /** The attribute the field searches in, picked at its start; none where the field searches every property alone. */
  scope?: SearchScope;
  /** Whether `/` is the page's way into this field, shown at its end while it is empty and the cursor elsewhere. */
  shortcut?: boolean;
  /** Raised by the page to put the cursor in the field, at the end of what it shows; the value it starts with does not. */
  focusRequest?: number;
  className?: string;
  /** Base testid; the input, the hint, the clear button and the braces add `-input`, `-hint`, `-clear` and `-lucene`. */
  testId: string;
}) {
  const [draft, setDraft] = useState(text);
  const [asLucene, setAsLucene] = useState(lucene);
  // A new search in the address (one sent, Back, a query to edit) replaces what the field holds; the field stays the same
  // element, so the cursor stays in it.
  const [shown, setShown] = useState({ text, lucene });
  if (shown.text !== text || shown.lucene !== lucene) {
    setShown({ text, lucene });
    setDraft(text);
    setAsLucene(lucene);
  }

  const input = useRef<HTMLInputElement>(null);
  const focused = useRef(focusRequest);
  useEffect(() => {
    if (focusRequest === focused.current) {
      return;
    }

    focused.current = focusRequest;
    const field = input.current;
    if (field !== null) {
      field.focus();
      field.setSelectionRange(field.value.length, field.value.length);
    }
  }, [focusRequest]);
  const typed = draft.trim();
  // The attribute picked at the field's start: what is typed is a value of it, never an id to open.
  const scoped = scope?.target ?? null;
  // An id opens its record, one of a partition's own type naming no group among them (dev:site-note:3b9c...).
  const opens = scoped === null && !asLucene && isRecordIdIn(typed, scope?.partition ?? null);
  // Under the field as text is typed: every property (Enter), each property offered, and another property to pick; or, with
  // an attribute picked, the value typed searched in it (Enter) and the values it holds.
  const [hasFocus, setHasFocus] = useState(false);
  const [dismissed, setDismissed] = useState(false);
  const [active, setActive] = useState(0);
  const listId = useId();
  const typing = hasFocus && !dismissed && typed !== "" && !asLucene;
  const offering = searchIn !== undefined && scoped === null && typing && !opens;
  const held = scoped === null || scope === undefined ? null : scopeHeld(scoped, scope.base);
  const values = useHeldValues({
    partition: scope?.partition ?? null,
    base: held?.base ?? NO_SEARCH,
    field: held?.field ?? NO_FIELD,
    typed,
    limit: SCOPED_VALUES,
    enabled: held !== null && typing,
  });
  const scopedOffering = scoped !== null && typing;
  const termChoices = searchIn?.onSearchInTerm === undefined ? [] : searchIn.terms ?? [];
  // The last option opens the field's choice of where to search, where there is one, the text kept to be searched there.
  const [scopeOpen, setScopeOpen] = useState(false);
  const options = scopedOffering
    ? values.listed.length + 1
    : offering ? searchIn.choices.length + termChoices.length + 1 + (scope === undefined ? 0 : 1) : 0;
  const highlighted = Math.min(active, Math.max(options - 1, 0));
  // The search the field shows is still the list's until it is cleared: an emptied field says Enter clears it.
  const searched = text !== "";
  const hint = typed === "" ? (searched ? "clear" : null) : opens ? "open" : asLucene ? "query" : "search";

  // With an attribute picked: the value typed searched in it (the first option), or a value it holds, picked whole.
  const pickScoped = (option: number) => {
    if (scope === undefined || scoped === null) {
      return;
    }

    setDismissed(true);
    setDraft("");
    const pickedValue = option > 0 ? values.listed[option - 1]?.[0] : undefined;
    scope.onSearch(scoped, pickedValue ?? typed, pickedValue !== undefined);
  };

  // Clearing clears the search as well as the field; a field holding no search has only its draft to drop. The cursor
  // stays in the field, for the next search.
  const clear = () => {
    setDraft("");
    input.current?.focus();
    if (searched) {
      onSearch("", false);
    }
  };

  // The options after the first: each property offered, each source column offered, then another property or column,
  // picked in the field's choice of where to search, the text kept for it.
  const pickOption = (option: number) => {
    if (searchIn === undefined) {
      return;
    }

    setDismissed(true);
    const fields = searchIn.choices.length;
    if (option > fields + termChoices.length) {
      scope?.onWanted();
      setScopeOpen(true);
      return;
    }

    // The text is the condition's from here, not the field's to search with.
    setDraft("");
    if (option <= fields) {
      searchIn.onSearchIn(searchIn.choices[option - 1], typed);
    } else {
      searchIn.onSearchInTerm?.(termChoices[option - fields - 1], typed);
    }
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (scoped !== null && typed !== "" && !asLucene) {
      pickScoped(scopedOffering ? highlighted : 0);
      return;
    }

    if (offering && highlighted > 0) {
      pickOption(highlighted);
      return;
    }

    setDismissed(true);
    if (opens) {
      onOpenId(typed);
    } else if (typed !== "" || searched) {
      onSearch(typed, asLucene);
    }
  };

  const keyed = (event: KeyboardEvent<HTMLInputElement>) => {
    if ((offering || scopedOffering) && (event.key === "ArrowDown" || event.key === "ArrowUp")) {
      event.preventDefault();
      setActive((highlighted + (event.key === "ArrowDown" ? 1 : options - 1)) % options);
      return;
    }

    if (event.key === "Escape" && (offering || scopedOffering)) {
      event.preventDefault();
      setDismissed(true);
      return;
    }

    if (event.key === "Escape" && (draft !== "" || searched)) {
      event.preventDefault();
      clear();
    }
  };

  const listing = offering || scopedOffering;
  const fieldPlaceholder = asLucene
    ? "A Lucene query: createTime:[2024-01-01 TO *] AND kind:*Wellbore*"
    : scoped === null
      ? placeholder
      : scoped.kind === "term" ? `Type a ${scoped.term.term.name}, as the source holds it` : `Type a value of ${scopeName(scoped)}`;
  return (
    <form role="search" aria-label={label} onSubmit={submit} className={cn("relative flex min-w-0 items-center", className)} data-testid={testId}>
      {scope !== undefined && (
        <ExplorerSearchScope
          target={scoped}
          terms={scope.terms}
          fields={scope.fields}
          kind={scope.kind}
          open={scopeOpen}
          onOpenChange={setScopeOpen}
          onWanted={scope.onWanted}
          onChoose={(next) => {
            scope.onChoose(next);
            setAsLucene(false);
            setDismissed(false);
            setActive(0);
          }}
          // The cursor goes back to the field, for the value to search in what was picked.
          returnFocus={() => input.current?.focus()}
          testId={`${testId}-scope`}
        />
      )}
      <div className="relative flex min-w-0 flex-1 items-center">
        {scope === undefined && <Search className="pointer-events-none absolute left-3 size-4 text-muted-foreground" />}
        <Input
          ref={input}
          value={draft}
          onChange={(event) => {
            setDraft(event.target.value);
            setDismissed(false);
            setActive(0);
            searchIn?.onWanted();
            if (scoped !== null) {
              scope?.onWanted();
            }
          }}
          onKeyDown={keyed}
          onFocus={() => setHasFocus(true)}
          onBlur={() => setHasFocus(false)}
          role={searchIn === undefined && scope === undefined ? undefined : "combobox"}
          aria-expanded={searchIn === undefined && scope === undefined ? undefined : listing}
          aria-controls={listing ? listId : undefined}
          aria-activedescendant={listing ? `${listId}-${highlighted}` : undefined}
          placeholder={fieldPlaceholder}
          aria-label={scoped === null ? label : `${label}, in ${scopeName(scoped)}`}
          spellCheck={false}
          autoComplete="off"
          className={cn(
            "peer h-8 bg-secondary/70 pr-36 text-[13px] dark:bg-input/60",
            scope === undefined ? "pl-9" : "rounded-l-none pl-2.5",
            asLucene && "font-mono text-[12px]",
          )}
          data-testid={`${testId}-input`}
        />
        {shortcut && draft === "" && (
          <kbd
            className="pointer-events-none absolute right-9 flex h-5 min-w-5 items-center justify-center rounded border bg-background/60 px-1 font-mono text-[11px] text-muted-foreground peer-focus:hidden"
            title="Press / to search"
            data-testid={`${testId}-shortcut`}
          >
            /
          </kbd>
        )}
        <div className="absolute right-1.5 flex items-center gap-1">
          {hint !== null && (
            <span className="pointer-events-none flex items-center gap-1 rounded border px-1.5 py-0.5 text-[11px] text-muted-foreground" data-testid={`${testId}-hint`}>
              <CornerDownLeft className="size-3" />
              {hint}
            </span>
          )}
          {draft !== "" && (
            <button type="button" onClick={clear} aria-label="Clear the search" className="rounded-sm p-0.5 text-muted-foreground hover:text-foreground" data-testid={`${testId}-clear`}>
              <X className="size-4" />
            </button>
          )}
          <Button
            type="button"
            variant={asLucene ? "secondary" : "ghost"}
            size="icon"
            className={cn("size-6", asLucene && "text-primary")}
            aria-pressed={asLucene}
            aria-label="Write a Lucene query"
            title={asLucene ? "Searching with a Lucene query, sent as written. Click to search by id, name or text." : "Write a Lucene query: field:value, AND, OR, quotes and wildcards, sent as written."}
            onClick={() => {
              // A Lucene query is the whole search: it searches every property, so an attribute picked is dropped.
              if (!asLucene && scoped !== null) {
                scope?.onChoose(null);
              }

              setAsLucene((was) => !was);
            }}
            data-testid={`${testId}-lucene`}
          >
            <Braces />
          </Button>
        </div>
      </div>
      {scopedOffering && (
        <ScopedOptions
          id={listId}
          target={scoped}
          typed={typed}
          values={held === null ? null : values}
          heading={held?.heading}
          highlighted={highlighted}
          onPick={pickScoped}
          testId={`${testId}-in`}
        />
      )}
      {offering && (
        <SearchInOptions
          id={listId}
          typed={typed}
          searchIn={searchIn}
          terms={termChoices}
          picks={scope !== undefined}
          highlighted={highlighted}
          onEverywhere={() => { setDismissed(true); onSearch(typed, false); }}
          onPick={pickOption}
          testId={`${testId}-in`}
        />
      )}
    </form>
  );
}

/**
 * The options under a search field searching one attribute, as text is typed: the value typed searched in it (what Enter
 * does), as the attribute allows (its words, its start, or its whole value), then the values the attribute holds that hold
 * the text, the commonest first with how many records hold each, any of them picked whole with a click. For a source
 * column found through other records, the values are those of the records it is found by (a wellbore's names).
 */
function ScopedOptions({ id, target, typed, values, heading = "Values held here", highlighted, onPick, testId }: {
  id: string;
  target: SearchScopeTarget;
  typed: string;
  /** The values the attribute holds; null for one that holds none to list (a key, whose values are made). */
  values: HeldValues | null;
  heading?: string;
  highlighted: number;
  onPick: (option: number) => void;
  testId: string;
}) {
  const option = (index: number, children: ReactNode, suffix: string, title: string) => (
    <div
      key={`${suffix}:${index}`}
      id={`${id}-${index}`}
      role="option"
      aria-selected={highlighted === index}
      title={title}
      className={cn("flex h-8 cursor-pointer items-center gap-2 rounded-md px-2 text-[13px] hover:bg-accent/60", highlighted === index && "bg-accent text-accent-foreground")}
      // The field keeps the cursor: a press on an option does not blur it. Only the arrow keys light the option Enter takes,
      // so a pointer resting over the list never changes what Enter searches.
      onMouseDown={(event) => event.preventDefault()}
      onClick={() => onPick(index)}
      data-testid={`${testId}-${suffix}`}
    >
      {children}
    </div>
  );

  const name = scopeName(target);
  return (
    <div id={id} role="listbox" aria-label={`Search in ${name}`} className="absolute top-full right-0 left-0 z-50 mt-1 rounded-md border bg-popover p-1 text-popover-foreground shadow-md" data-testid={testId}>
      {option(0, (
        <>
          {target.kind === "term" ? <Columns3 className="size-3.5 shrink-0 text-muted-foreground" /> : <TextSearch className="size-3.5 shrink-0 text-muted-foreground" />}
          <span className="min-w-0 flex-1 truncate">
            <span className={target.kind === "field" ? "font-mono text-[12px]" : undefined}>{name}</span>
            <span className="text-muted-foreground"> {CONDITION_LABELS[scopeTypedCondition(target)]} </span>
            <span className="font-medium">{typed}</span>
          </span>
          <CornerDownLeft className="size-3.5 shrink-0 text-muted-foreground" />
        </>
      ), "scoped", scopeTitle(target))}
      {values !== null && (
        <>
          <div className="flex items-center gap-1.5 px-2 pt-1.5 pb-0.5 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
            <span className="min-w-0 truncate" title={heading}>{heading}</span>
            {values.reading && <Loader2 className="size-3 animate-spin" aria-label="Reading the values" />}
          </div>
          {values.error !== null && <ExplorerErrorText error={values.error} className="px-2 py-1" />}
          {values.refusal && <p className="px-2 py-1 text-[12px] text-destructive">{`The search service would not group by this attribute: ${values.refusal}`}</p>}
          {!values.reading && values.error === null && !values.refusal && values.listed.length === 0 && (
            <p className="px-2 py-1 text-[12px] text-muted-foreground" data-testid={`${testId}-held-none`}>{`No value held here matches "${values.settled}".`}</p>
          )}
          {values.listed.map(([value, count], at) => option(at + 1, (
            <>
              <span className="min-w-0 flex-1 truncate">{isRecordReference(value) ? <RecordName id={value} /> : value}</span>
              <span className="shrink-0 font-mono text-[11px] tabular-nums text-muted-foreground">{count.toLocaleString("en-US")}</span>
            </>
          ), "held", `${name} is ${value}`))}
        </>
      )}
    </div>
  );
}

/**
 * The options under a search field as text is typed: the text searched in every property (what Enter does), in one of the
 * properties offered (those searched in lately for the type, then the record's name), in one of the source columns searched
 * in lately, or in another property or column, picked in the field's choice of where to search. Text searched in one
 * property becomes a condition of the list, said beside it: its words for text, its start for a keyword, its whole value
 * otherwise; in a source column, as the column's route allows. The arrow keys move among the options and Enter takes the
 * one lit; the field keeps the cursor, since a press on an option does not take it.
 */
function SearchInOptions({ id, typed, searchIn, terms, picks, highlighted, onEverywhere, onPick, testId }: {
  id: string;
  typed: string;
  searchIn: SearchIn;
  terms: OfferedTerm[];
  /** Whether the field has a choice of where to search, which the last option opens. */
  picks: boolean;
  highlighted: number;
  onEverywhere: () => void;
  onPick: (option: number) => void;
  testId: string;
}) {
  const option = (index: number, onChoose: () => void, children: ReactNode, suffix: string, title?: string) => (
    <div
      key={`${suffix}:${index}`}
      id={`${id}-${index}`}
      role="option"
      aria-selected={highlighted === index}
      title={title}
      className={cn("flex h-8 cursor-pointer items-center gap-2 rounded-md px-2 text-[13px] hover:bg-accent/60", highlighted === index && "bg-accent text-accent-foreground")}
      // The field keeps the cursor: a press on an option does not blur it. Only the arrow keys light the option Enter takes,
      // so a pointer resting over the list never changes what Enter searches.
      onMouseDown={(event) => event.preventDefault()}
      onClick={onChoose}
      data-testid={`${testId}-${suffix}`}
    >
      {children}
    </div>
  );

  return (
    <div id={id} role="listbox" aria-label="Where to search" className="absolute top-full right-0 left-0 z-50 mt-1 rounded-md border bg-popover p-1 text-popover-foreground shadow-md" data-testid={testId}>
      {option(0, onEverywhere, (
        <>
          <Search className="size-3.5 shrink-0 text-muted-foreground" />
          <span className="min-w-0 flex-1 truncate">
            Search every property for <span className="font-medium">{typed}</span>
          </span>
          <CornerDownLeft className="size-3.5 shrink-0 text-muted-foreground" />
        </>
      ), "everywhere")}
      <div className="px-2 pt-1.5 pb-0.5 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Search in one property</div>
      {searchIn.choices.map((field, at) => option(at + 1, () => onPick(at + 1), (
        <>
          <TextSearch className="size-3.5 shrink-0 text-muted-foreground" />
          <span className="min-w-0 flex-1 truncate">
            <span className="font-mono text-[12px]">{fieldLabel(field.path)}</span>
            <span className="text-muted-foreground"> {CONDITION_LABELS[searchInCondition(field)]} </span>
            <span className="font-medium">{typed}</span>
          </span>
          {nestedLabel(field.nested) !== null && <span className="shrink-0 rounded-sm border px-1 text-[10px] text-muted-foreground">in {nestedLabel(field.nested)}</span>}
        </>
      ), "field", [field.path, field.title, field.description].filter(Boolean).join("\n")))}
      {terms.length > 0 && <div className="px-2 pt-1.5 pb-0.5 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Search in a source column</div>}
      {terms.map((term, at) => option(searchIn.choices.length + at + 1, () => onPick(searchIn.choices.length + at + 1), (
        <>
          <Columns3 className="size-3.5 shrink-0 text-muted-foreground" />
          <span className="min-w-0 flex-1 truncate">
            <span>{term.term.name}</span>
            <span className="text-muted-foreground"> {CONDITION_LABELS[termSearchInCondition(term.route)]} </span>
            <span className="font-medium">{typed}</span>
          </span>
        </>
      ), "term", termTitle(term)))}
      {picks && option(searchIn.choices.length + terms.length + 1, () => onPick(searchIn.choices.length + terms.length + 1), (
        <>
          <ListFilter className="size-3.5 shrink-0 text-muted-foreground" />
          <span className="min-w-0 flex-1 truncate text-muted-foreground">
            {searchIn.reading ? "Reading the properties..." : searchIn.hasTerms ? "Pick another property or source column..." : "Pick another property..."}
          </span>
        </>
      ), "choose")}
    </div>
  );
}

/**
 * The records opened last in this browser, newest first, one click from being opened again: the reader's own trail across
 * sessions, kept nowhere but here. A record of another partition opens in that partition.
 */
export function ExplorerRecent({ onOpen }: { onOpen: (record: RecentRecord) => void }) {
  const [open, setOpen] = useState(false);
  const [records, setRecords] = useState<RecentRecord[]>([]);
  const forget = () => {
    forgetRecentRecords();
    setRecords([]);
  };

  return (
    <Popover open={open} onOpenChange={(next) => { setOpen(next); if (next) { setRecords(recentRecords()); } }}>
      <PopoverTrigger asChild>
        <Button variant="outline" size="sm" className="gap-1.5 px-2.5 text-[13px]" title="The records opened last" data-testid="explorer-recent">
          <History />
          Recent
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-[420px] p-0" data-testid="explorer-recent-panel">
        <div className="flex items-center justify-between border-b px-3 py-2">
          <span className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Opened lately</span>
          {records.length > 0 && (
            <Button variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-[11px] text-muted-foreground" onClick={forget} data-testid="explorer-recent-forget">
              <Trash2 className="size-3" />
              Forget
            </Button>
          )}
        </div>
        {records.length === 0
          ? <p className="px-3 py-4 text-[12px] text-muted-foreground">Records opened in the explorer show here, in this browser only.</p>
          : (
            <div className="max-h-80 overflow-y-auto p-1">
              {records.map((record) => (
                <button
                  key={`${record.partition ?? ""}|${record.id}`}
                  type="button"
                  onClick={() => { setOpen(false); onOpen(record); }}
                  className="flex w-full min-w-0 items-center gap-2 rounded-md px-2 py-1.5 text-left hover:bg-accent/60"
                  title={record.id}
                  data-testid="explorer-recent-item"
                >
                  <span className="flex min-w-0 flex-1 flex-col">
                    <span className="truncate text-[13px]">{record.name ?? idParts(record.id).unique}</span>
                    <span className="truncate text-[11px] text-muted-foreground">
                      {[kindParts(record.kind ?? `*:*:${idParts(record.id).group}--${idParts(record.id).type}:*`).type, record.partition].filter(Boolean).join(" in ")}
                    </span>
                  </span>
                  <span className="shrink-0 text-[11px] text-muted-foreground"><RelativeTime value={record.openedUtc} absolute={false} /></span>
                </button>
              ))}
            </div>
          )}
      </PopoverContent>
    </Popover>
  );
}
