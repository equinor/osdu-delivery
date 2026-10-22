import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Layers, TableProperties, Telescope, Unplug } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from "@/components/ui/resizable";
import { useAuth } from "@/auth/AuthContext";
import { useSideBarFold } from "@/layout/workbench/SideBarRoom";
import { EmptyState } from "@/components/EmptyState";
import { Page } from "@/components/Page";
import { RichTooltip } from "@/components/RichTooltip";
import type { DeliveryOsduRead } from "../../../api/delivery";
import { explorerApi, type ExplorerFieldInfo, type ExplorerFilter, type ExplorerSearchRequest, type ExplorerSort, type ExplorerTypes } from "../../../api/explorer";
import { isPartitionId, useActivePartition } from "../activePartition";
import { useWindowFit } from "../useWindowFit";
import { failureText } from "../answers";
import { ElementQueryButton } from "./ExplorerElementQuery";
import { ExplorerProblem } from "./ExplorerProblem";
import { ExplorerRecord } from "./ExplorerRecord";
import { ExplorerResults } from "./ExplorerResults";
import { ExplorerRecent, ExplorerSearchBar, type SearchIn, type SearchScope } from "./ExplorerSearchBar";
import { ExplorerTypeRail } from "./ExplorerTypeRail";
import { ExplorerWelcome } from "./ExplorerWelcome";
import { BuildFieldActions } from "./dimension/BuildFieldActions";
import { DimensionBuildPanel } from "./dimension/DimensionBuildPanel";
import { useDimensionBuild } from "./dimension/useDimensionBuild";
import { searchedInType, searchInChoices, useExplorerFields } from "./explorerFields";
import {
  scopeChoiceOf, scopeCondition, scopeMemory, scopeReplaces, scopeTargetOf, type SearchScopeChoice,
} from "./explorerScope";
import { offeredTerms, searchInTerms, termCondition, termMemory, useSearchTerms, type OfferedTerm } from "./explorerTerms";
import {
  ALL_KINDS, columnsOf, filtersOf, filtersText, NAME_FIELDS, recordAt, rememberSearchedIn, rememberType, sameFilter, scopeKind, scopeLabel, scopeOf,
  searchInCondition, sortOf, useExplorerRead, type ExplorerScope, type RecentRecord,
} from "./explorerModel";

/** The kind a dimension built here reads from the explorer's place: a kind, or every version of a type; null for a group or every type. */
function buildKind(scope: ExplorerScope): string | null {
  return scope.level === "kind" || scope.level === "type" ? scopeKind(scope) ?? null : null;
}

/** The least height the explorer keeps, so a short window still shows a few rows. */
const MIN_HEIGHT = 420;

/** What stays under the explorer: the page's bottom padding and the workbench's status bar. */
const BELOW = 46;

/**
 * A frame as tall as the window leaves under where it starts, whatever it holds, so what is in it scrolls inside it and the
 * page does not. It fits itself when it mounts, and again when it is shown after being hidden.
 */
function WindowFrame({ hidden = false, children, testId }: { hidden?: boolean; children: ReactNode; testId: string }) {
  const frame = useRef<HTMLDivElement>(null);
  useWindowFit(frame, BELOW, MIN_HEIGHT, undefined, "height");
  return <div ref={frame} hidden={hidden} className="flex min-h-0 flex-col" data-testid={testId}>{children}</div>;
}

/**
 * The explorer (osdu/docs/reference/concepts/explorer.md): a browser of what an OSDU partition holds, read live from
 * OSDU. It opens on a welcome that reads nothing from OSDU: the search field, the records and types opened lately in
 * this browser, and the types of the partition one click away. Once asked, the types of the partition the title bar
 * names stand beside its records; a type, a group or a kind narrows them, and a property's values group and narrow them
 * further. One search field searches the place in view (the type picked, or every type) and keeps it: an id (which
 * opens the record), the start of one, a name, any text or a Lucene query, in every property or in the one property or
 * source column picked at the field's start. A record opens in the record inspector, under the place it sits in, with
 * its versions, its links and the records that mention it. Everything is in the address (the search, the place, the
 * values, the order and the record open), so a link, Back and a refresh land on the same view; the records already read
 * are kept, so going back is immediate. Nothing here reads what the delivery system keeps.
 */
export default function ExplorerPage() {
  const [params, setParams] = useSearchParams();
  const queryClient = useQueryClient();
  const [active, setActive] = useActivePartition();
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");

  // A link that names a partition makes it the title bar's, so the title bar says what is in view; the link has then done
  // its work, and the title bar decides from there on.
  const linked = params.get("partition");
  useEffect(() => {
    if (linked === null) {
      return;
    }

    if (isPartitionId(linked) && linked !== active) {
      setActive(linked);
    }

    setParams((current) => {
      const next = new URLSearchParams(current);
      next.delete("partition");
      return next;
    }, { replace: true });
  }, [linked, active, setActive, setParams]);

  const text = params.get("q") ?? "";
  const lucene = params.get("lq") === "1";
  const kindParam = params.get("kind");
  const scope = useMemo(() => scopeOf(kindParam), [kindParam]);
  const filterParam = params.get("f");
  const filters = useMemo(() => filtersOf(filterParam), [filterParam]);
  const sort = sortOf(params.get("sort"));
  const recordId = params.get("id");
  // What the reader asked for: records (a search, a place, values to narrow to), or the types alone to pick a place from.
  // Until then nothing is read from OSDU.
  const asksRecords = text !== "" || kindParam !== null || filters.length > 0;
  const browsing = asksRecords || params.get("view") === "types";
  // A group, a type or a kind picked: the one search field searches inside it, and says so; else it searches every type.
  const inPlace = asksRecords && scope.level !== "all";
  // The place the field's choice of where to search is kept for: the kind pattern in view, every type when none is.
  const placeKey = scopeKind(scope) ?? ALL_KINDS;
  // Asked to (`/`, or Edit on the query sent), the field takes the cursor, at the end of what it shows.
  const [focusRequest, setFocusRequest] = useState(0);

  // `/` anywhere on the page but in a field puts the cursor in the search field.
  useEffect(() => {
    const keyed = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null;
      const typing = target !== null && (target.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName));
      if (event.key !== "/" || event.ctrlKey || event.metaKey || event.altKey || typing || recordId !== null) {
        return;
      }

      event.preventDefault();
      setFocusRequest((asked) => asked + 1);
    };
    window.addEventListener("keydown", keyed);
    return () => window.removeEventListener("keydown", keyed);
  }, [recordId]);
  const versionParam = Number(params.get("v") ?? "");
  const recordVersion = Number.isSafeInteger(versionParam) && versionParam > 0 ? versionParam : null;

  const navigate = (changes: Record<string, string | null>, replace = false) => setParams((current) => {
    const next = new URLSearchParams(current);
    for (const [key, value] of Object.entries(changes)) {
      if (value === null || value === "") {
        next.delete(key);
      } else {
        next.set(key, value);
      }
    }

    return next;
  }, { replace });

  const connection = useQuery({
    queryKey: ["explorer", "connection", active],
    queryFn: () => explorerApi.connection(active),
    staleTime: 60_000,
  });
  const reachable = canOperate && connection.data?.available === true;

  // A dimension built from what the explorer shows: the records are browsed and drilled into as ever, and each value picked
  // from where it is (osdu/docs/reference/concepts/explorer.md, Building a dimension).
  const build = useDimensionBuild({ partition: active, reachable, scopeKind: buildKind(scope), recordId, recordVersion });
  const openKind = () => {
    const read = recordId === null ? undefined : queryClient.getQueryData<DeliveryOsduRead>(["explorer", "read", active, recordId, recordVersion]);
    return read?.found === true && typeof read.record?.kind === "string" ? read.record.kind : null;
  };
  const startKind = buildKind(scope) ?? build.recordKind ?? "";
  const building = build.draft !== null && connection.data?.available === true;
  // The workbench's side bar is folded while the builder is docked, so the records and the builder share the width.
  useSideBarFold(building);

  // The types are counted for the search and its values, never for the place: the list is what a place is picked from.
  const typesRequest: ExplorerSearchRequest = { text: text === "" ? undefined : text, lucene, filters };
  const types = useExplorerRead<ExplorerTypes>(
    ["types", active, typesRequest],
    reachable && browsing ? () => explorerApi.types(active, typesRequest) : null,
    // The kinds of a whole partition move slowly; those a search finds are read again sooner.
    typesRequest.text === undefined && filters.length === 0 ? 10 * 60_000 : undefined,
  );
  // Each property a condition asks is a column of the grid, so the grid shows why each record is listed.
  const request = { text: text === "" ? undefined : text, lucene, kind: scopeKind(scope), filters, sort, columns: columnsOf(filters, NAME_FIELDS) };

  // Text searched in one property (osdu/docs/reference/concepts/explorer.md, Conditions and properties): the properties
  // of the place are read once text is typed in the field or its choice of where to search is opened. A type's source
  // columns (osdu/docs/reference/concepts/search-terms.md) are read with them, and those searched in lately are offered
  // after them.
  const [fieldsWanted, setFieldsWanted] = useState(false);
  const placeFields = useExplorerFields(active, scopeKind(scope), reachable && fieldsWanted);
  const placeTerms = useSearchTerms(scopeKind(scope), reachable && fieldsWanted);
  const placeOffered = useMemo(() => offeredTerms(placeTerms.data?.terms), [placeTerms.data]);
  const searchIn = (kind: string | undefined, fields: ExplorerFieldInfo[] | undefined, reading: boolean, terms: OfferedTerm[] = []): SearchIn => ({
    choices: searchInChoices(fields, kind, searchedInType(kind)),
    terms: searchInTerms(terms, searchedInType(kind)),
    hasTerms: terms.length > 0,
    reading,
    onWanted: () => setFieldsWanted(true),
    onSearchIn: (field, typed) => {
      rememberSearchedIn(searchedInType(kind), field.path);
      const condition: ExplorerFilter = {
        path: field.path,
        index: field.index,
        ...(field.nested ? { nested: field.nested } : {}),
        ...(searchInCondition(field) !== "is" ? { condition: searchInCondition(field) } : {}),
        value: typed,
      };
      // The text is the condition's now, so the field's own search, which it replaced as it was typed, is cleared.
      navigate({ q: null, lq: null, kind: kind ?? ALL_KINDS, f: filtersText([...filters.filter((f) => !sameFilter(f, condition)), condition]), id: null, v: null });
    },
    onSearchInTerm: (term, typed) => {
      rememberSearchedIn(searchedInType(kind), termMemory(term.term));
      const condition = termCondition(term, typed);
      navigate({ q: null, lq: null, kind: kind ?? ALL_KINDS, f: filtersText([...filters.filter((f) => !sameFilter(f, condition)), condition]), id: null, v: null });
    },
  });

  // Where the place's field searches (osdu/docs/reference/concepts/explorer.md, Conditions and properties): every
  // property, or one source column or property picked at its start, kept while the place is in view. A value searched
  // in it replaces the condition it had.
  const [scopeChoice, setScopeChoice] = useState<{ place: string; choice: SearchScopeChoice } | null>(null);
  const scopeTarget = scopeChoice !== null && scopeChoice.place === placeKey ? scopeTargetOf(scopeChoice.choice, placeOffered) : null;
  const placeScope: SearchScope = {
    target: scopeTarget,
    terms: placeOffered,
    fields: placeFields,
    kind: scopeKind(scope),
    partition: active,
    base: { ...request, text: undefined, lucene: false, filters: scopeTarget === null ? filters : filters.filter((f) => !scopeReplaces(f, scopeTarget)) },
    onWanted: () => setFieldsWanted(true),
    onChoose: (target) => setScopeChoice(target === null ? null : { place: placeKey, choice: scopeChoiceOf(target) }),
    onSearch: (target, value, exact) => {
      const kind = scopeKind(scope);
      rememberSearchedIn(searchedInType(kind), scopeMemory(target));
      const condition = scopeCondition(target, value, exact);
      // The value is the condition's now, so the field's own search, which it replaced as it was typed, is cleared.
      navigate({ q: null, lq: null, kind: kind ?? ALL_KINDS, f: filtersText([...filters.filter((f) => !scopeReplaces(f, target)), condition]), id: null, v: null });
    },
  };

  // Every type is a place picked like any other, so the address keeps it rather than falling back to the welcome.
  const goScope = (next: ExplorerScope) => {
    rememberType(next);
    navigate({ kind: scopeKind(next) ?? ALL_KINDS, view: null, id: null, v: null });
  };
  // An id typed with its version opens the record at that version; the version picker reads the others from there.
  const openId = (typed: string) => {
    const { id, version } = recordAt(typed);
    navigate({ id, v: version === null ? null : String(version) });
  };
  const openRecent = (record: RecentRecord) => {
    if (record.partition !== null && record.partition !== active) {
      setActive(record.partition);
    }

    navigate({ id: record.id, v: null });
  };

  const header = (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
      <h1 className="text-lg font-semibold leading-7">Explorer</h1>
      {connection.data?.available && (
        <RichTooltip
          title="Read live from OSDU"
          body={`Partition ${connection.data.partition ?? ""}, read through its own connection${connection.data.endpoint === null ? "" : ` to ${connection.data.endpoint}`}: the platform and client its configuration names. Every view is what OSDU's search, storage and schema services answer now.`}
        >
          <span className="inline-flex items-center gap-1.5 rounded-full border px-2 py-0.5 text-[12px] text-muted-foreground" data-testid="explorer-live">
            <span className="size-1.5 rounded-full bg-success" aria-hidden />
            {connection.data.partition}
          </span>
        </RichTooltip>
      )}
      <ExplorerSearchBar
        text={text}
        lucene={lucene}
        placeholder={inPlace ? `Search ${scopeLabel(scope)} by id, name or any text` : "Search every type by id, name or any text"}
        label="Search OSDU"
        // The field searches the place in view and keeps it: a type picked, or every type. Cleared, the place lists every
        // record it holds; every type is one click away in the list of types and in the place's own line.
        onSearch={(typed, asLucene) => navigate(typed === ""
          ? { q: null, lq: null, kind: kindParam ?? ALL_KINDS, id: null, v: null }
          : { q: typed, lq: asLucene ? "1" : null, kind: kindParam, id: null, v: null })}
        onOpenId={openId}
        searchIn={searchIn(scopeKind(scope), placeFields.data?.answer.fields, placeFields.isFetching && placeFields.data === undefined, placeOffered)}
        scope={reachable ? placeScope : undefined}
        shortcut={recordId === null}
        focusRequest={focusRequest}
        className="min-w-[320px] max-w-[880px] flex-1"
        testId="explorer-search"
      />
      {(browsing || recordId !== null) && <ExplorerRecent onOpen={openRecent} />}
      {reachable && build.draft === null && (
        <RichTooltip
          title="Build a dimension"
          body="Pick a dimension's key, value and attributes from the records as you browse them, following their links as far as a value is read, and get the YAML a dimension flow lists. The builder opens beside the records."
        >
          <Button variant="outline" size="sm" className="ml-auto" onClick={() => build.start(buildKind(scope) ?? openKind() ?? "")} data-testid="explorer-build-dimension">
            <TableProperties />
            Build a dimension
          </Button>
        </RichTooltip>
      )}
    </div>
  );

  let content;
  if (!canOperate) {
    content = (
      <EmptyState
        icon={<Telescope />}
        title="The explorer reads OSDU with the partition's credentials"
        description="Each read goes through the partition's own connection to OSDU, which takes a signed-in session. Sign in to read OSDU."
        data-testid="explorer-no-scope"
      />
    );
  } else if (connection.isError) {
    content = <ExplorerProblem error={connection.error} />;
  } else if (connection.data !== undefined && !connection.data.available) {
    content = (
      <EmptyState
        icon={<Unplug />}
        title={connection.data.partition === null ? "Pick a partition" : `No connection to ${connection.data.partition}`}
        description={connection.data.reason ?? undefined}
        data-testid="explorer-no-connection"
      />
    );
  } else if (connection.data !== undefined) {
    content = (
      <>
        {!browsing && recordId === null && (
          <WindowFrame testId="explorer-welcome-frame">
            <ExplorerWelcome
              partition={connection.data.partition}
              onBrowseTypes={() => navigate({ view: "types" })}
              onOpenRecent={openRecent}
              onOpenType={(type) => goScope(type.scope)}
            />
          </WindowFrame>
        )}
        {/* The records stay as they were left while a record is open, so going back finds the list where it was. */}
        {browsing && (
        <WindowFrame hidden={recordId !== null} testId="explorer-browse-frame">
        <Card className="min-h-0 flex-1 gap-0 overflow-hidden rounded-lg p-0" data-testid="explorer-browse">
          <ResizablePanelGroup orientation="horizontal" className="min-h-0 flex-1">
            <ResizablePanel defaultSize={264} minSize={200} maxSize="40" className="flex min-h-0 flex-col border-r">
              <ExplorerTypeRail
                types={types.data?.answer}
                loading={types.isFetching}
                error={types.isError ? failureText(types.error) : types.data?.answer.refusal ?? null}
                scope={asksRecords ? scope : null}
                searchKey={typesRequest.text === undefined && filters.length === 0 ? null : JSON.stringify(typesRequest)}
                onScope={goScope}
              />
            </ResizablePanel>
            <ResizableHandle />
            <ResizablePanel className="flex min-h-0 flex-col">
              {asksRecords
                ? (
                  <ExplorerResults
                    partition={active}
                    request={request}
                    scope={scope}
                    onScope={goScope}
                    onOpen={(hit) => openId(hit.id)}
                    onFilters={(next: ExplorerFilter[]) => navigate({ f: filtersText(next) })}
                    onSort={(next: ExplorerSort) => navigate({ sort: next === "relevance" ? null : next }, true)}
                    onSearchEverywhere={() => navigate({ kind: ALL_KINDS })}
                    onEditQuery={(query) => {
                      setFocusRequest((asked) => asked + 1);
                      navigate({ q: query, lq: "1", f: null, id: null, v: null });
                    }}
                  />
                )
                : (
                  <EmptyState
                    icon={<Layers />}
                    title="Pick a type"
                    description="Pick a group, a type or a kind on the left, or search above. All types lists every record of the partition."
                    data-testid="explorer-pick-type"
                  />
                )}
            </ResizablePanel>
          </ResizablePanelGroup>
        </Card>
        </WindowFrame>
        )}
        {recordId !== null && (
          <WindowFrame testId="explorer-record-frame">
            <ExplorerRecord
              key={`${active ?? ""}|${recordId}|${recordVersion ?? ""}`}
              partition={active}
              id={recordId}
              version={recordVersion}
              onBack={() => navigate({ id: null, v: null })}
              onScope={goScope}
              onOpenId={openId}
              onBrowseQuery={(query) => navigate({ q: query, lq: "1", kind: null, f: null, id: null, v: null })}
              onSwitchPartition={(partition) => setActive(partition)}
              fieldActions={(field) => (
                <span className="inline-flex items-center gap-1">
                  <ElementQueryButton field={field} onSearch={(kind, query) => navigate({ q: query, lq: "1", kind, f: null, id: null, v: null })} />
                  {build.draft !== null && <BuildFieldActions field={field} build={build} />}
                </span>
              )}
            />
          </WindowFrame>
        )}
      </>
    );
  }

  // While a dimension is built, the builder docks beside whatever the explorer shows, in a frame of the same height.
  if (building) {
    content = (
      <WindowFrame testId="explorer-build-frame">
        <ResizablePanelGroup orientation="horizontal" className="min-h-0 flex-1">
          {/* The records keep the larger share: the builder is used by opening records and following their links. */}
          <ResizablePanel minSize="45" className="flex min-h-0 flex-col gap-3 overflow-auto pr-1">
            {content}
          </ResizablePanel>
          <ResizableHandle />
          <ResizablePanel defaultSize="45" minSize={380} maxSize="55" className="flex min-h-0 flex-col pl-1">
            <Card className="flex min-h-0 flex-1 flex-col gap-0 overflow-hidden rounded-lg p-0">
              <DimensionBuildPanel build={build} startKind={startKind} />
            </Card>
          </ResizablePanel>
        </ResizablePanelGroup>
      </WindowFrame>
    );
  }

  return (
    <Page data-testid="page-delivery-explorer">
      {header}
      {content}
    </Page>
  );
}
