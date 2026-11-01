import { useCallback, useEffect, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { Loader2 } from "lucide-react";
import { SearchInput } from "@/components/SearchInput";
import { cn } from "@/lib/utils";
import { DRY_RUN_OUTCOMES, NOT_RUN, lineDetail, type StagedLine } from "./dryRun";
import { formatCount } from "./valueCheck";

/** The height of a line: the list draws only the lines in view, so every one is the same. */
const LINE_HEIGHT = 46;

/** Lines drawn past the ones in view, so a quick scroll meets no blank. */
const OVERSCAN = 8;

/** How near the end of what is loaded a scroll comes before the next page is read. */
const LOAD_AHEAD = 30;

/** One record's line: its outcome's glyph, its name and its row, and under them why it came to that. */
function Line({ line, top, picked, onPick }: { line: StagedLine; top: number; picked: boolean; onPick: () => void }) {
  const visual = line.keyJson === null && line.result === null
    ? DRY_RUN_OUTCOMES.held
    : line.result === null ? NOT_RUN : DRY_RUN_OUTCOMES[line.result.outcome];
  const Icon = visual.icon;
  const detail = lineDetail(line);
  const name = line.label ?? line.sourceKey;
  return (
    <button
      type="button"
      role="option"
      aria-selected={picked}
      tabIndex={-1}
      onClick={onPick}
      title={[line.label, line.sourceKey, line.file === null ? null : `${line.file}${line.row === null ? "" : `, row ${formatCount(line.row)}`}`].filter((part) => part !== null).join("\n")}
      style={{ top, height: LINE_HEIGHT }}
      className={cn(
        "absolute inset-x-0 flex w-full min-w-0 items-start gap-2 border-b border-border/60 px-2.5 py-1.5 text-left outline-none transition-colors hover:bg-accent/50",
        picked && "bg-accent shadow-[inset_2px_0_0_var(--primary)] hover:bg-accent",
      )}
      data-testid="staged-line"
      data-outcome={line.result?.outcome ?? "none"}
    >
      <Icon className={cn("mt-0.5 size-3.5 shrink-0", visual.tone)} aria-label={visual.label} />
      <span className="flex min-w-0 flex-1 flex-col gap-0.5">
        <span className="flex min-w-0 items-baseline justify-between gap-2">
          <span className="min-w-0 truncate font-mono text-[12px]">{name}</span>
          {line.row !== null && <span className="shrink-0 font-mono text-[10.5px] text-muted-foreground tabular-nums">{formatCount(line.row)}</span>}
        </span>
        <span className="min-w-0 truncate text-[11.5px] text-muted-foreground">{detail}</span>
      </span>
    </button>
  );
}

/**
 * The records of a list as a scrolling column, of any length: one fixed-height line for each, only those in view drawn, so a
 * list of thousands scrolls as a list of ten does, and the next page read as the scroll comes near the end of what is loaded.
 * Up and Down step through the records, which is how a reader goes through them. It takes the height of its panel: the search
 * and whatever narrows the list on top, the lines between, and at the foot how much is listed and of which table.
 */
export function StagedRecordList({ lines, picked, onPick, search, onSearch, total, loading, hasMore, onMore, empty, head, table }: {
  lines: readonly StagedLine[];
  picked: string | null;
  onPick: (line: StagedLine) => void;
  search: string;
  onSearch: (text: string) => void;
  /** How many rows the list is of, which may be more than are loaded; null while it is not known. */
  total: number | null;
  loading: boolean;
  hasMore: boolean;
  onMore: () => void;
  /** What to say when no line is listed. */
  empty: string;
  /** What sits under the search: what narrows the list, what to know about it. */
  head?: ReactNode;
  /** The table the rows are of, and its child tables; null until a page has said. */
  table: { name: string; children: readonly string[] } | null;
}) {
  const scroller = useRef<HTMLDivElement>(null);
  const [top, setTop] = useState(0);
  const [height, setHeight] = useState(0);

  useEffect(() => {
    const element = scroller.current;
    if (element === null) {
      return undefined;
    }

    const observer = new ResizeObserver(() => setHeight(element.clientHeight));
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  const first = Math.max(0, Math.floor(top / LINE_HEIGHT) - OVERSCAN);
  const last = Math.min(lines.length, Math.ceil((top + height) / LINE_HEIGHT) + OVERSCAN);
  useEffect(() => {
    if (hasMore && !loading && height > 0 && last >= lines.length - LOAD_AHEAD) {
      onMore();
    }
  }, [hasMore, loading, height, last, lines.length, onMore]);

  const reveal = useCallback((index: number) => {
    const element = scroller.current;
    if (element === null) {
      return;
    }

    const lineTop = index * LINE_HEIGHT;
    if (lineTop < element.scrollTop) {
      element.scrollTop = lineTop;
    } else if (lineTop + LINE_HEIGHT > element.scrollTop + element.clientHeight) {
      element.scrollTop = lineTop + LINE_HEIGHT - element.clientHeight;
    }
  }, []);

  const step = (event: KeyboardEvent<HTMLDivElement>) => {
    const moves: Record<string, number> = { ArrowDown: 1, ArrowUp: -1, PageDown: 10, PageUp: -10 };
    const at = lines.findIndex((line) => line.sourceKey === picked);
    const next = event.key === "Home" ? 0 : event.key === "End" ? lines.length - 1 : moves[event.key] === undefined ? null : at + moves[event.key];
    if (next === null || lines.length === 0) {
      return;
    }

    event.preventDefault();
    const index = Math.min(lines.length - 1, Math.max(0, next));
    onPick(lines[index]);
    reveal(index);
  };

  const children = table === null || table.children.length === 0 ? "" : `, with ${table.children.join(", ")}`;
  return (
    <div className="flex min-h-0 min-w-0 flex-1 flex-col" data-testid="staged-list">
      <div className="flex shrink-0 flex-col gap-1.5 border-b p-2">
        <SearchInput value={search} onChange={onSearch} placeholder="Search key, label or file" label="Search the staged records" className="sm:w-full" testId="staged-search" />
        {head}
      </div>
      <div
        ref={scroller}
        role="listbox"
        aria-label="Staged records"
        tabIndex={0}
        onKeyDown={step}
        onScroll={(event) => setTop(event.currentTarget.scrollTop)}
        className="relative min-h-0 flex-1 overflow-y-auto outline-none focus-visible:ring-2 focus-visible:ring-ring/50 focus-visible:ring-inset"
        data-testid="staged-lines"
      >
        {lines.length === 0
          ? <p className="px-3 py-6 text-center text-[12px] text-muted-foreground" data-testid="staged-empty">{loading ? "Reading the rows." : empty}</p>
          : (
            <div style={{ height: lines.length * LINE_HEIGHT }} className="relative">
              {lines.slice(first, last).map((line, offset) => (
                <Line
                  key={line.sourceKey}
                  line={line}
                  top={(first + offset) * LINE_HEIGHT}
                  picked={line.sourceKey === picked}
                  onPick={() => onPick(line)}
                />
              ))}
            </div>
          )}
      </div>
      {(table !== null || lines.length > 0) && (
        <p
          className="flex shrink-0 items-center gap-1.5 border-t px-2.5 py-1 text-[11.5px] text-muted-foreground"
          title={table === null ? undefined : `${table.name}${children}`}
          data-testid="staged-count"
        >
          {loading && <Loader2 className="size-3.5 shrink-0 animate-spin" aria-hidden />}
          <span className="shrink-0 font-mono tabular-nums">{total === null ? formatCount(lines.length) : `${formatCount(lines.length)} of ${formatCount(total)}`}</span>
          {table !== null && <span className="min-w-0 truncate font-mono" data-testid="staged-table">{`${table.name}${children}`}</span>}
        </p>
      )}
    </div>
  );
}
