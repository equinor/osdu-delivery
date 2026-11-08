import { Fragment, useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { Database, KeyRound, Loader2, Search, Table2, X } from "lucide-react";
import { SearchInput } from "@/components/SearchInput";
import { Button } from "@/components/ui/button";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { cn } from "@/lib/utils";
import { deliveryApi, type DeliverySourceTable } from "../../../api/delivery";
import { explorerApi } from "../../../api/explorer";
import { cachedCell } from "../cacheFormat";
import { ProblemView } from "../TemplateSheet";
import { ValueCheckScope } from "../ValueCheckBar";
import { flowScope, type ValueCheckSession } from "../useValueCheck";
import { formatCount } from "../valueCheck";
import type { OverviewPart } from "./OverviewCanvas";
import { PartLineageButton } from "./OverviewLineage";
import type { PartTarget } from "./partTargets";
import type { OutlineCard } from "./outlineModel.ts";
import { matchRuns, PREVIEW_ROWS, previewColumns, recordColumnsOf, rowsOfRecord, rowsSourceOf, type RowsSource } from "./tableRows.ts";

/** The most properties a search's records carry as columns, as the explorer reads them. */
const SEARCH_COLUMNS = 8;

/** How long the grid waits for the typing to stop before it asks for the rows a search finds. */
const SEARCH_DELAY_MS = 300;

/** What a preview read: the names of its columns, its rows in their order, how many there are, and what a column's values lost. */
interface Read {
  names: string[];
  rows: (string | null)[][];
  /** How many rows there are where they were read; null when the read does not say. */
  total: number | null;
  capped: boolean;
  /** The columns whose values were cut, with the most characters a value keeps. */
  cut: ReadonlySet<string>;
  maxCharacters: number | null;
  /** The search the rows were read with, as it was sent; empty for none. */
  search: string;
  /** OSDU's own words when it refused the search; null when it did not. */
  refusal: string | null;
}

/** What the search box offers to find, where the rows are read. */
const FINDS: Record<RowsSource["from"], string> = {
  table: "Find text in any column",
  cache: "Find an id or a value",
  search: "Find words in OSDU",
};

/**
 * The rows of a table, read from where its source says: a flow's table in the flow's scope, the partition's cache, or OSDU. A
 * search is sent to where the rows are, which finds the rows holding it among all of them, not among those shown; the rows
 * found before stay on screen while the next search is read.
 */
function useRows(source: RowsSource, card: OutlineCard, session: ValueCheckSession, partition: string | null, search: string) {
  const { flow, ready, sentValues } = session;
  const asked = search === "" ? undefined : search;
  const dataset = source.from === "table" ? source.dataset : null;
  const table = useQuery({
    queryKey: ["delivery", "overview-table-rows", flow?.pipelineId ?? null, flow?.interface ?? null, flow?.partition ?? null, dataset, sentValues, search],
    queryFn: async () => ({
      search,
      answer: await deliveryApi.tableRows(flow!.pipelineId, { values: sentValues, dataset: dataset ?? undefined, limit: PREVIEW_ROWS, search: asked }, flowScope(flow!)),
    }),
    enabled: source.from === "table" && flow !== null && ready,
    staleTime: 30000,
    retry: false,
    placeholderData: keepPreviousData,
  });
  const type = source.from === "cache" ? source.type : null;
  const cache = useQuery({
    queryKey: ["delivery", "overview-cache-rows", partition, type, search],
    queryFn: async () => ({ search, answer: await deliveryApi.cachedItems({ scope: partition!, type: type!, page: 1, pageSize: PREVIEW_ROWS, search: asked }) }),
    enabled: type !== null && partition !== null,
    staleTime: 30000,
    retry: false,
    placeholderData: keepPreviousData,
  });
  const kind = source.from === "search" ? source.kind : null;
  const properties = useMemo(() => card.fields.map((field) => field.name).filter((name) => name !== "id").slice(0, SEARCH_COLUMNS), [card.fields]);
  const searched = useQuery({
    queryKey: ["delivery", "overview-search-rows", flow?.partition ?? partition, kind, properties, search],
    queryFn: async () => ({ search, answer: await explorerApi.search(flow?.partition ?? partition, { kind: kind!, limit: PREVIEW_ROWS, columns: properties, text: asked }) }),
    enabled: kind !== null,
    staleTime: 30000,
    retry: false,
    placeholderData: keepPreviousData,
  });

  const read = useMemo<Read | null>(() => {
    if (source.from === "table" && table.data !== undefined) {
      const { columns, rows, counted, countCapped, maxCharacters } = table.data.answer;
      return {
        names: columns.map((column) => column.name),
        rows,
        total: counted,
        capped: countCapped,
        cut: new Set(columns.filter((column) => column.cut).map((column) => column.name)),
        maxCharacters,
        search: table.data.search,
        refusal: null,
      };
    }

    if (source.from === "cache" && cache.data !== undefined) {
      // A record holds the names its type keeps, the ones it has nothing under left out.
      const { items, total, totalCapped } = cache.data.answer;
      const names = [...new Set(items.flatMap((item) => Object.keys(item.fields)))];
      return {
        names: ["id", ...names],
        rows: items.map((item) => [item.recordId, ...names.map((name) => cachedCell(item.fields[name]))]),
        total,
        capped: totalCapped === true,
        cut: new Set(),
        maxCharacters: null,
        search: cache.data.search,
        refusal: null,
      };
    }

    if (source.from === "search" && searched.data !== undefined) {
      const page = searched.data.answer.answer;
      return {
        names: ["id", ...properties],
        rows: page.hits.map((hit) => [hit.id, ...properties.map((name) => hit.values?.[name]?.join(", ") ?? null)]),
        total: page.total,
        capped: false,
        cut: new Set(),
        maxCharacters: null,
        search: searched.data.search,
        refusal: page.refusal ?? null,
      };
    }

    return null;
  }, [source.from, table.data, cache.data, searched.data, properties]);

  const query = source.from === "table" ? table : source.from === "cache" ? cache : searched;
  const blocked = source.from === "table"
    ? flow === null ? "No flow renders with this mapping yet, so there are no rows to read." : ready ? null : session.blocked
    : source.from === "cache" && partition === null ? "Choose a partition in the title bar: the cache is the partition's." : null;
  return { read, loading: query.isFetching, stale: query.isPlaceholderData, error: query.error, blocked };
}

/** A search as a line names it. */
function Asked({ search }: { search: string }) {
  return <>“<span className="font-mono">{search}</span>”</>;
}

/** The line over the rows: what they are, of how many, where they were read, and the search that found them. */
function Said({ source, card, read, partition }: { source: RowsSource; card: OutlineCard; read: Read | null; partition: string | null }) {
  if (read === null) {
    return null;
  }

  const shown = read.rows.length;
  const total = read.total === null ? null : `${formatCount(read.total)}${read.capped ? "+" : ""}`;
  const all = read.total !== null && !read.capped && read.total <= shown;
  const holding = read.search === "" ? null : <> holding <Asked search={read.search} /></>;
  switch (source.from) {
    case "table":
      return <>{all ? `All ${formatCount(shown)} ${shown === 1 ? "row" : "rows"}` : `The first ${formatCount(shown)} of ${total} rows`} a delivery reads in this scope{holding}</>;
    case "cache":
      return <>{all ? `All ${formatCount(shown)}` : `${formatCount(shown)} of ${total}`} records of <span className="font-mono">{card.source.name}</span> in the {partition === null ? "" : <span className="font-mono">{partition}</span>} cache{holding}</>;
    case "search":
      return <>{all ? `All ${formatCount(shown)}` : `${formatCount(shown)} of ${total}`} records of <span className="font-mono">{source.kind}</span> in OSDU{read.search === "" ? null : <> found by <Asked search={read.search} /></>}</>;
  }
}

/** What a read that found no row says: that nothing is there, or how the search looked and that it found nothing. */
function Unfound({ source, card, read }: { source: RowsSource; card: OutlineCard; read: Read }) {
  if (read.search === "") {
    return <>{source.from === "table" ? "No row is staged in this scope." : "No record is there."}</>;
  }

  switch (source.from) {
    case "table":
      return <>No row a delivery reads in this scope holds <Asked search={read.search} />. The search looks inside every value of every column as the grid shows it, in any case; a binary column is not searched.</>;
    case "cache":
      return <>No cached record of <span className="font-mono">{card.source.name}</span> holds <Asked search={read.search} /> in its id or its values. The search looks inside them, in any case.</>;
    case "search":
      return <>OSDU finds no record of <span className="font-mono">{source.kind}</span> by <Asked search={read.search} />. Its search finds whole words, and an id by its start.</>;
  }
}

/** A value of the grid, the stretches the search matched marked. */
function Marked({ text, search }: { text: string; search: string }) {
  return (
    <>
      {matchRuns(text, search).map((run, index) => (run.match
        ? <mark key={index} className="rounded-[2px] bg-warning/25 text-foreground">{run.text}</mark>
        : <Fragment key={index}>{run.text}</Fragment>))}
    </>
  );
}

/**
 * A look at a table's rows under the canvas: its first rows, every column, the fields the mapping reads first in the order the
 * table on the canvas lists them, then the rest muted. A search finds rows among all the table holds, where it is held, and marks
 * what it matched. Pointing at a column's heading lights its lines on the canvas, a click keeps them lit, a double click opens the
 * field's details, and the rows of the record shown, when one is, are marked. Nothing is sent or stored.
 */
export function OverviewRows({ card, focus, session, partition, tables, shownKey, lineage, onPoint, onPick, onOpen, onClose }: {
  card: OutlineCard;
  /** The field the preview was asked for from, whose column is brought into view and marked. */
  focus: string | null;
  session: ValueCheckSession;
  partition: string | null;
  tables: readonly DeliverySourceTable[] | null;
  /** The key of the record the canvas shows, part by part; null when none is shown. */
  shownKey: string[] | null;
  /** The table's node in lineage. */
  lineage: readonly PartTarget[];
  onPoint: (part: OverviewPart | null) => void;
  /** Keeps a column's lines lit. */
  onPick: (part: OverviewPart) => void;
  /** Opens a column's details. */
  onOpen: (part: OverviewPart) => void;
  onClose: () => void;
}) {
  const source = rowsSourceOf(card)!;
  const [typed, setTyped] = useState("");
  const search = useDebouncedValue(typed.trim(), SEARCH_DELAY_MS);
  const { read, loading, stale, error, blocked } = useRows(source, card, session, partition, search);
  const columns = useMemo(() => (read === null ? [] : previewColumns(card, read.names)), [card, read]);
  const marked = useMemo(() => {
    if (read === null || source.from !== "table") {
      return new Set<number>();
    }

    const record = tables?.find((candidate) => candidate.dataset === null);
    const table = tables?.find((candidate) => candidate.dataset === source.dataset);
    const named = record === undefined || table === undefined ? null : recordColumnsOf(table, record.key);
    return rowsOfRecord(read.names, read.rows, named, shownKey);
  }, [read, source, tables, shownKey]);

  // The column of the field the rows were asked for from, brought into view once the rows are there.
  const focused = focus === null ? -1 : columns.findIndex((column) => column.field?.name === focus);
  const scroller = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (focused >= 0) {
      scroller.current?.querySelector(`[data-column="${focused}"]`)?.scrollIntoView({ block: "nearest", inline: "center" });
    }
  }, [focused]);

  // Escape in the search clears it first; the page's Escape, which closes the grid, waits for an empty search.
  const onSearchKey = (event: KeyboardEvent<HTMLSpanElement>) => {
    if (event.key === "Escape" && typed !== "") {
      event.preventDefault();
      setTyped("");
    }
  };

  const tone = source.from === "table" ? "border-t-chart-1" : "border-t-chart-6";
  const Glyph = source.from === "table" ? Table2 : source.from === "cache" ? Database : Search;
  return (
    <section className="flex h-full min-h-0 flex-col bg-card" aria-label={`Rows of ${card.title}`} data-testid="mapping-overview-rows">
      <header className="flex min-w-0 shrink-0 flex-wrap items-center gap-x-3 gap-y-1 border-b border-border px-3 py-1.5 text-[12.5px]">
        <span className="flex min-w-0 items-center gap-1.5">
          <Glyph className={cn("size-3.5 shrink-0", source.from === "table" ? "text-chart-1" : "text-chart-6")} aria-hidden />
          <span className="truncate font-mono font-medium" title={card.subtitle === null ? card.title : `${card.subtitle}.${card.title}`}>{card.title}</span>
        </span>
        <span className="min-w-0 text-muted-foreground" data-testid="mapping-overview-rows-said"><Said source={source} card={card} read={read} partition={partition} /></span>
        {marked.size > 0 && <span className="flex items-center gap-1 text-muted-foreground" data-testid="mapping-overview-rows-marked"><span className="h-3 w-0.5 rounded-full bg-primary" aria-hidden />the record shown</span>}
        {loading && <Loader2 className="size-3.5 animate-spin text-muted-foreground" aria-label="Reading the rows" />}
        <span className="ml-auto flex items-center gap-2">
          <span className="contents" onKeyDown={onSearchKey}>
            <SearchInput
              value={typed}
              onChange={setTyped}
              placeholder={FINDS[source.from]}
              label={`Search the rows of ${card.title}`}
              className="w-56 sm:w-56"
              testId="mapping-overview-rows-search"
            />
          </span>
          {source.from === "table" && <ValueCheckScope session={session} />}
          <PartLineageButton targets={lineage} named={card.title} />
          <Button variant="ghost" size="icon-xs" aria-label="Close the rows" title="Close" onClick={onClose} data-testid="mapping-overview-rows-close"><X /></Button>
        </span>
      </header>
      {blocked !== null && blocked !== undefined && <p className="px-3 py-2 text-[12.5px] text-muted-foreground" data-testid="mapping-overview-rows-blocked">{blocked}</p>}
      {error !== null && error !== undefined && <div className="p-3"><ProblemView error={error} testId="mapping-overview-rows-error" /></div>}
      {read?.refusal !== null && read?.refusal !== undefined && (
        <p className="px-3 py-2 text-[12.5px] text-destructive" data-testid="mapping-overview-rows-refused">OSDU refused the search: {read.refusal}</p>
      )}
      {read !== null && read.rows.length === 0 && read.refusal === null && (
        <p className="px-3 py-2 text-[12.5px] text-muted-foreground" data-testid="mapping-overview-rows-empty">
          <Unfound source={source} card={card} read={read} />
        </p>
      )}
      {read !== null && read.rows.length > 0 && (
        <div ref={scroller} className={cn("min-h-0 flex-1 overflow-auto transition-opacity", stale && "opacity-60")} aria-busy={stale} data-testid="mapping-overview-rows-grid">
          <table className="border-separate border-spacing-0 font-mono text-[12px]">
            <thead>
              <tr>
                <th className="sticky top-0 left-0 z-20 border-b border-border bg-card px-2 py-1 text-right text-[11px] font-normal text-muted-foreground" aria-label="Row" />
                {columns.map((column, at) => {
                  const part: OverviewPart | null = column.field === null ? null : { type: "field", source: card.id, field: column.field.name };
                  const cut = read.cut.has(column.name);
                  return (
                    <th
                      key={column.index}
                      scope="col"
                      data-column={at}
                      title={[
                        column.field === null ? "Not read by the mapping." : "Read by the mapping: point to light its lines, click to keep them lit, double-click for its details.",
                        cut && read.maxCharacters !== null ? `Values cut at ${formatCount(read.maxCharacters)} characters.` : "",
                      ].filter(Boolean).join(" ")}
                      className={cn(
                        "sticky top-0 z-10 max-w-72 border-b border-border bg-card px-2 py-1 text-left font-medium whitespace-nowrap",
                        part === null ? "font-normal text-muted-foreground" : cn("cursor-pointer border-t-2 text-foreground select-none hover:bg-accent", tone),
                        at === focused && "bg-accent",
                      )}
                      onMouseEnter={part === null ? undefined : () => onPoint(part)}
                      onMouseLeave={part === null ? undefined : () => onPoint(null)}
                      onClick={part === null ? undefined : () => onPick(part)}
                      onDoubleClick={part === null ? undefined : () => onOpen(part)}
                      data-testid={`mapping-overview-rows-column-${column.name}`}
                    >
                      <span className="flex items-center gap-1">
                        {column.field?.key === true && <KeyRound className="size-3 shrink-0 text-muted-foreground" aria-label="Key" />}
                        <span className="truncate">{column.name}</span>
                      </span>
                    </th>
                  );
                })}
              </tr>
            </thead>
            <tbody>
              {read.rows.map((row, index) => (
                <tr key={index} className={cn("group/row", marked.has(index) && "bg-primary/5")} data-marked={marked.has(index) ? "true" : undefined} data-testid="mapping-overview-rows-row">
                  <td className={cn(
                    "sticky left-0 border-b border-border/60 bg-card px-2 py-0.5 text-right text-[11px] text-muted-foreground tabular-nums group-hover/row:bg-accent",
                    marked.has(index) && "shadow-[inset_2px_0_0_var(--primary)]",
                  )}
                  >
                    {index + 1}
                  </td>
                  {columns.map((column, at) => {
                    const value = row[column.index];
                    return (
                      <td
                        key={column.index}
                        title={value ?? "null"}
                        className={cn(
                          "max-w-72 truncate border-b border-border/60 px-2 py-0.5 whitespace-nowrap group-hover/row:bg-accent/60",
                          column.field === null && "text-muted-foreground",
                          at === focused && "bg-accent/50",
                        )}
                      >
                        {value === null ? <span className="text-muted-foreground/60 italic">null</span> : <Marked text={value} search={read.search} />}
                      </td>
                    );
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
