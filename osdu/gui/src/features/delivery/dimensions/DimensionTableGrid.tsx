import { useMemo, useState } from "react";
import { keepPreviousData, useInfiniteQuery } from "@tanstack/react-query";
import { ArrowDownWideNarrow, ArrowUpNarrowWide, Ban, Table2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { CopyButton } from "@/components/CopyButton";
import { DataTable, type Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { SearchInput } from "@/components/SearchInput";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { cn } from "@/lib/utils";
import { deliveryApi, type DeliveryDimensionAttributeSpec, type DeliveryDimensionTableRow, type DimensionAttributeCondition } from "../../../api/delivery";
import { ProblemView } from "../TemplateSheet";
import { DimensionAttributeFilter } from "./DimensionAttributeFilter";
import { attributeColumnId } from "./dimensionColumns";
import { DimensionGrid, GridFooter, GridViewMenu } from "./DimensionGrid";
import { useHiddenColumns, type GridColumnChoice } from "./dimensionGridState";
import { DimensionValueText } from "./DimensionValueText";
import { keyTail } from "./dimensionFormat";

const PAGE = 100;

const SEARCH_DELAY_MS = 300;

/** The widest an attribute's cell draws before it is cut short, with the whole of it on hover. */
const ATTRIBUTE_WIDTH = 120;

/** How much of a key's end stays in view: enough to tell ids apart, the whole key being on hover. */
const KEY_TAIL = 14;

/** The columns a reader starts without: numbers a table of facts joins on, which a person seldom reads. */
const HIDDEN_AT_FIRST = ["id"];

/** How the rows are ordered: a column of the table, and which end comes first. */
interface TableOrder {
  column: string;
  descending: boolean;
}

/** The table's columns holding when a build first wrote a row and last changed it, as SQLFlow's own tables name them. */
const INSERTED = "InsertedDate_DW";
const UPDATED = "UpdatedDate_DW";

/** The order a column starts in when it is picked: the most records and the latest times first, anything else from its lowest. */
function firstOrder(column: string): TableOrder {
  return { column, descending: column === "records" || column === INSERTED || column === UPDATED };
}

/**
 * The column the rows are ordered by, and which end comes first: picking a column orders by it, picking it again turns
 * the order round.
 */
function OrderMenu({ order, columns, onChange }: {
  order: TableOrder;
  columns: { id: string; label: string }[];
  onChange: (order: TableOrder) => void;
}) {
  const current = columns.find((column) => column.id === order.column)?.label ?? order.column;
  const Arrow = order.descending ? ArrowDownWideNarrow : ArrowUpNarrowWide;
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="outline" size="sm" className="h-8 gap-1.5 px-2.5 text-[13px]" data-testid="dimension-table-order">
          <Arrow />
          <span className="text-muted-foreground">Sorted by</span>
          <span className="max-w-32 truncate">{current}</span>
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="max-h-80 min-w-48 overflow-y-auto">
        {columns.map((column) => {
          const picked = column.id === order.column;
          return (
            <DropdownMenuItem
              key={column.id}
              onSelect={() => onChange(picked ? { column: column.id, descending: !order.descending } : firstOrder(column.id))}
              className={cn("justify-between gap-3", picked && "bg-accent/60")}
              data-testid={`dimension-table-order-${column.id}`}
            >
              <span className="min-w-0 truncate">{column.label}</span>
              {picked && <span className="shrink-0 text-[11px] text-muted-foreground">{order.descending ? "highest first" : "lowest first"}</span>}
            </DropdownMenuItem>
          );
        })}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/**
 * The dimension as one table, as its own table in the database holds it: a row per key and value it collects, with the
 * key's value, a column per attribute, when a build first wrote the row and last changed it (the table's
 * `InsertedDate_DW` and `UpdatedDate_DW`), the records of the row and the search finding the key's records. Its columns are
 * headed as the table names them: the key's and the value's after what the dimension reads, as the attributes' are. Read
 * a page at a time, more as the grid is scrolled; found by text anywhere in a row, narrowed by attribute values, ordered
 * by any column. Columns are left out under View.
 */
export function DimensionTableGrid({ dimensionId, table, keyColumn, valueColumn, attributes, unlabelled }: {
  dimensionId: number;
  /** The table's name in the database, as the dimension names it; null until a run has written it. */
  table: string | null;
  /** What the dimension calls its key and its value: the two columns' headings until a page says what the table names them. */
  keyColumn: string;
  valueColumn: string;
  /** The attributes the dimension declares, which the attribute filter offers. */
  attributes: DeliveryDimensionAttributeSpec[];
  /** The value the dimension gives what it could not read, drawn faint; null when it gives none. */
  unlabelled: string | null;
}) {
  const [typed, setTyped] = useState("");
  const search = useDebouncedValue(typed.trim(), SEARCH_DELAY_MS);
  const [order, setOrder] = useState<TableOrder>({ column: "value", descending: false });
  const [conditions, setConditions] = useState<DimensionAttributeCondition[]>([]);
  const [hidden, toggleColumn] = useHiddenColumns(`osdu.dimensions.table.columns.${dimensionId}`, HIDDEN_AT_FIRST);
  const pages = useInfiniteQuery({
    queryKey: ["delivery", "dimensions", "table", dimensionId, search, order, conditions],
    queryFn: ({ pageParam }) => deliveryApi.dimensionTable(dimensionId, {
      search, order: order.column, dir: order.descending ? "desc" : "asc", offset: pageParam, limit: PAGE, attributes: conditions,
    }),
    initialPageParam: 0,
    getNextPageParam: (last, all) => (last.more ? all.reduce((count, page) => count + page.rows.length, 0) : undefined),
    placeholderData: keepPreviousData,
  });
  const first = pages.data?.pages[0];
  const names = useMemo(() => first?.attributes ?? [], [first]);
  // The table's own names: a table its next run has yet to rename still holds the two columns under their old ones.
  const keyName = first?.keyColumn ?? keyColumn;
  const valueName = first?.valueColumn ?? valueColumn;
  const rows = useMemo(() => pages.data?.pages.flatMap((page) => page.rows), [pages.data]);
  const more = () => {
    if (pages.hasNextPage && !pages.isFetchingNextPage) {
      void pages.fetchNextPage();
    }
  };

  const choices: GridColumnChoice[] = [
    { id: "id", label: "Id" },
    { id: "records", label: "Records" },
    { id: "key", label: keyName },
    ...names.map((name) => ({ id: attributeColumnId(name), label: name })),
    { id: "inserted", label: "Inserted" },
    { id: "updated", label: "Updated" },
    { id: "search", label: "Search" },
  ];
  // The two columns are asked for by the words value and key, which name them whatever the table calls them.
  const sortable = [
    { id: "value", label: valueName },
    { id: "records", label: "Records" },
    { id: "key", label: keyName },
    ...names.map((name) => ({ id: name, label: name })),
    { id: UPDATED, label: "Updated" },
    { id: INSERTED, label: "Inserted" },
    { id: "id", label: "Id" },
  ];
  const all: Column<DeliveryDimensionTableRow>[] = [
    {
      id: "id",
      header: "Id",
      align: "right",
      render: (row) => <span className="font-mono text-[12px] tabular-nums text-muted-foreground" data-testid="dimension-table-id">{row.id}</span>,
    },
    {
      id: "value",
      header: valueName,
      fill: true,
      floor: 150,
      render: (row) => (
        <DimensionValueText
          value={row.value}
          search={search}
          strong
          maxWidth="100%"
          className={row.value === unlabelled ? "text-muted-foreground/70" : undefined}
          testId="dimension-table-value"
        />
      ),
    },
    {
      id: "records",
      header: "Records",
      align: "right",
      render: (row) => <span className="font-mono text-[12px] tabular-nums">{row.records.toLocaleString("en-US")}</span>,
    },
    {
      id: "key",
      header: keyName,
      render: (row) => (
        <DimensionValueText
          value={row.key}
          search={search}
          maxWidth={140}
          tail={Math.min(keyTail(row.key), KEY_TAIL)}
          className="text-muted-foreground"
          testId="dimension-table-key"
        />
      ),
    },
    ...names.map((name, index): Column<DeliveryDimensionTableRow> => ({
      id: attributeColumnId(name),
      header: name,
      render: (row) => {
        const value = row.attributes[index] ?? null;
        return value === null
          ? <span className="text-muted-foreground/50">-</span>
          : <DimensionValueText value={value} search={search} maxWidth={ATTRIBUTE_WIDTH} className={value === unlabelled ? "text-muted-foreground/70" : undefined} />;
      },
    })),
    {
      id: "inserted",
      header: "Inserted",
      render: (row) => <span className="whitespace-nowrap text-[12px] text-muted-foreground" data-testid="dimension-table-inserted"><RelativeTime value={row.insertedUtc} absolute={false} /></span>,
    },
    {
      id: "updated",
      header: "Updated",
      render: (row) => <span className="whitespace-nowrap text-[12px] text-muted-foreground" data-testid="dimension-table-updated"><RelativeTime value={row.updatedUtc} absolute={false} /></span>,
    },
    {
      id: "search",
      header: "Search",
      align: "right",
      render: (row) => (row.filter === null
        ? (
          <RichTooltip body="No search query can carry this key, so no search finds the records holding it.">
            <Ban className="inline size-4 text-warning" aria-label="Cannot be carried in a query" />
          </RichTooltip>
        )
        : (
          <span className="inline-flex" onClick={(event) => event.stopPropagation()}>
            <CopyButton iconOnly label="Copy the search that finds the key's records" text={row.filter} testId="dimension-table-copy-filter" />
          </span>
        )),
    },
  ];
  const columns = all.filter((column) => !hidden.has(column.id));
  const narrowed = search !== "" || conditions.length > 0;

  return (
    <div className="flex flex-col gap-2" data-testid="dimension-table">
      <FilterBar>
        <SearchInput
          value={typed}
          onChange={setTyped}
          placeholder="Find text in any column"
          label="Find rows"
          className="sm:w-72"
          testId="dimension-table-search"
        />
        <DimensionAttributeFilter dimensionId={dimensionId} attributes={attributes} conditions={conditions} onChange={setConditions} testId="dimension-table-attributes" />
        <OrderMenu order={order} columns={sortable} onChange={setOrder} />
        {/* Takes what the row leaves, so the table's name is cut short before the row breaks in two. */}
        <div className="flex min-w-0 flex-1 basis-40 items-center justify-end gap-2">
          {(first?.table ?? table) !== null && (
            <RichTooltip body="The table in the database these rows are read from, which a run of the pipeline writes: any SQL client reads it, and a table of facts joins on its id. The Definition tab lists its columns.">
              <span className="flex min-w-0 items-center gap-1 text-[12px] text-muted-foreground" data-testid="dimension-table-name">
                <Table2 className="size-3.5 shrink-0" aria-hidden />
                <span className="min-w-0 truncate font-mono">{first?.table ?? table}</span>
                <CopyButton iconOnly label="Copy the table's name" text={first?.table ?? table ?? ""} testId="dimension-copy-table-name" />
              </span>
            </RichTooltip>
          )}
          <GridViewMenu columns={choices} hidden={hidden} atFirst={HIDDEN_AT_FIRST} onToggle={toggleColumn} testId="dimension-table-view" />
        </div>
      </FilterBar>

      {pages.isError
        ? <ProblemView error={pages.error} testId="dimension-table-error" />
        : (
          <DimensionGrid onNearEnd={more}>
            <DataTable
              columns={columns}
              rows={rows}
              rowKey={(row) => row.id}
              footer={(
                <GridFooter
                  shown={rows?.length ?? 0}
                  total={first?.total ?? null}
                  noun="row"
                  hasMore={pages.hasNextPage}
                  loading={pages.isFetchingNextPage}
                  onMore={more}
                  testId="dimension-table-footer"
                />
              )}
              emptyMessage={narrowed ? "No row holds what was asked for." : "The table holds no row yet. Run the pipeline to write it."}
              data-testid="dimension-table-rows"
            />
          </DimensionGrid>
        )}
    </div>
  );
}
