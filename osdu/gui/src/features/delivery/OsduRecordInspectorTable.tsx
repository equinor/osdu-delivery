// An array of objects as the OSDU record inspector shows it: a table of its items with as many of their shared fields
// as the pane has room for.

import { useLayoutEffect, useMemo, useState, type ReactNode } from "react";
import { ChevronRight } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable, type Column } from "@/components/DataTable";
import { describeBranch, type RecordNode } from "./osduRecordModel";
import type { OpenLink } from "./osduRecordInspectorShared";
import { Leaf } from "./OsduRecordInspectorValues";

/** The most columns a table of items shows, however wide it is; the rest of an item's fields are a click into the item away. */
const TABLE_COLUMNS = 8;

/** The width a field column of a table of items asks for, so a narrower table shows fewer fields instead of scrolling sideways. */
const TABLE_COLUMN_WIDTH = 170;

/** The width a table of items gives its item number, and its count of the columns it leaves out. */
const TABLE_INDEX_WIDTH = 48;
const TABLE_MORE_WIDTH = 80;

/** What a field's name takes in a header: a generous width per character of the header's face, and the cell's padding. */
const TABLE_HEADER_CHARACTER = 7;
const TABLE_CELL_PADDING = 24;

/** The border around a table of items, which its columns do not get to use. */
const TABLE_BORDER = 2;

/** The keys the items of a table share, the most common first: the order its columns take as its width allows. */
function tableColumns(items: RecordNode[]): string[] {
  const counts = new Map<string, number>();
  for (const item of items) {
    for (const child of item.children) {
      counts.set(child.key, (counts.get(child.key) ?? 0) + 1);
    }
  }

  return [...counts.entries()].sort((a, b) => b[1] - a[1]).map(([key]) => key);
}

/** A field column of a table of items: the field it shows and the width it is given, padding included. */
interface ItemColumn {
  key: string;
  width: number;
}

/**
 * The field columns that fit a table of items this wide, taken in order, at most {@link TABLE_COLUMNS}. A column asks
 * for its share or for its name, whichever is wider, since a header does not clip the way a value does; what the
 * columns that fit leave over is split between them. The first column is always shown, in whatever room there is.
 */
function columnsThatFit(keys: string[], width: number): ItemColumn[] {
  let room = width - TABLE_BORDER - TABLE_INDEX_WIDTH - TABLE_MORE_WIDTH;
  const fitting: ItemColumn[] = [];
  for (const key of keys.slice(0, TABLE_COLUMNS)) {
    const ask = Math.max(TABLE_COLUMN_WIDTH, key.length * TABLE_HEADER_CHARACTER + TABLE_CELL_PADDING);
    if (fitting.length > 0 && ask > room) {
      break;
    }

    const given = fitting.length === 0 ? Math.min(ask, Math.max(room, TABLE_COLUMN_WIDTH / 2)) : ask;
    fitting.push({ key, width: given });
    room -= given;
  }

  const spare = fitting.length === 0 ? 0 : Math.max(0, Math.floor(room / fitting.length));
  return fitting.map((column) => ({ key: column.key, width: column.width + spare }));
}

/**
 * The width an element has, followed as its panel is resized; null until it is laid out. Measured before the browser
 * paints, so what depends on it never shows at a width it does not have.
 */
function useWidth(): [(node: HTMLDivElement | null) => void, number | null] {
  const [node, setNode] = useState<HTMLDivElement | null>(null);
  const [width, setWidth] = useState<number | null>(null);
  useLayoutEffect(() => {
    if (node === null) {
      return;
    }

    const measure = () => setWidth(node.clientWidth);
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(node);
    return () => observer.disconnect();
  }, [node]);

  return [setNode, width];
}

/**
 * An array of objects as a table: one row per item, each row a step into its item, and one column per shared field,
 * the first as many as the pane has room for. Each column is given its width and clips its values to it, so the table
 * never scrolls sideways; the columns it leaves out are counted in the last header, and a row opens its whole item.
 */
export function ItemTable({ node, term, hits, ownId, onOpenLink, opening, onSelect, shown, onShowMore, actions }: {
  node: RecordNode; term: string; hits: Set<string>; ownId: string | null; onOpenLink?: OpenLink; opening?: string | null;
  onSelect: (path: string) => void; shown: number; onShowMore: () => void;
  /** What a page shows beside an item, at the end of its row. */
  actions?: (node: RecordNode) => ReactNode;
}) {
  const shared = useMemo(() => tableColumns(node.children), [node]);
  const [measured, width] = useWidth();
  const fields = columnsThatFit(shared, width ?? 0);
  const hidden = shared.length - fields.length;
  const columns: Column<RecordNode>[] = [
    { id: "#", header: "#", width: TABLE_INDEX_WIDTH, align: "right", render: (item) => <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{item.key}</span> },
    ...fields.map(({ key, width: given }): Column<RecordNode> => ({
      id: key,
      header: key,
      width: given,
      render: (item) => {
        const field = item.children.find((child) => child.key === key);
        // The cell's content is held to the column's width, so a long value clips inside it instead of widening the table.
        return (
          <div className="overflow-hidden" style={{ width: given - TABLE_CELL_PADDING }}>
            {field === undefined
              ? <span className="text-muted-foreground">-</span>
              : field.kind === "leaf"
                ? <Leaf node={field} term={term} ownId={ownId} onOpenLink={onOpenLink} opening={opening} compact />
                : <span className="text-[11px] text-muted-foreground">{describeBranch(field)}</span>}
          </div>
        );
      },
    })),
    {
      id: "more",
      header: hidden > 0 ? `+${hidden} more` : "",
      width: TABLE_MORE_WIDTH,
      align: "right",
      render: (item) => (
        <span className="flex items-center justify-end gap-1">
          {actions?.(item)}
          <ChevronRight className="size-3.5 text-muted-foreground" aria-label="Open the item" />
        </span>
      ),
    },
  ];
  return (
    <div className="flex flex-col gap-2 p-2">
      <div ref={measured} className="min-w-0">
        <DataTable
          columns={columns}
          rows={node.children.slice(0, shown)}
          rowKey={(item) => item.path}
          onRowClick={(item) => onSelect(item.path)}
          rowSx={(item) => (hits.has(item.path) || item.children.some((child) => hits.has(child.path)) ? { backgroundColor: "color-mix(in oklab, var(--warning) 10%, transparent)" } : undefined)}
          emptyMessage="An empty list"
          data-testid="osdu-item-table"
        />
      </div>
      {shown < node.children.length && (
        <div>
          <Button variant="outline" size="sm" className="h-7" onClick={onShowMore} data-testid="osdu-show-more">{`Show the other ${node.children.length - shown}`}</Button>
        </div>
      )}
    </div>
  );
}
