import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Info, Loader2, Search, SearchCode } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { CopyButton } from "@/components/CopyButton";
import { RichTooltip } from "@/components/RichTooltip";
import { elementQueries, type ExplorerElementAnswer, type ExplorerElementLeaf, type ExplorerElementQuery } from "../../../api/explorer";
import { failureText } from "../answers";
import type { InspectorField } from "../OsduRecordInspector";
import type { RecordNode } from "../osduRecordModel";

/** The most values of a section sent: the server compares at most as many in one query. */
const MAX_LEAVES = 48;

/** How the other ways of finding records by an element are named. */
const OTHERS: Record<ExplorerElementQuery["purpose"], string> = {
  exact: "Exactly",
  words: "Its words",
  exists: "Holds any",
};

/** The value of a leaf as the request carries it: a text, a number, a boolean, or null. */
function scalar(value: unknown): string | number | boolean | null {
  return typeof value === "string" || typeof value === "number" || typeof value === "boolean" ? value : null;
}

/** The values a section holds at any depth, in the record's order, each by its own path; nulls left out, at most `MAX_LEAVES`. */
function leavesOf(node: RecordNode): ExplorerElementLeaf[] {
  const found: ExplorerElementLeaf[] = [];
  const walk = (at: RecordNode) => {
    for (const child of at.children) {
      if (found.length >= MAX_LEAVES) {
        return;
      }

      if (child.kind === "leaf") {
        const value = scalar(child.value);
        if (value !== null) {
          found.push({ path: child.path, value });
        }
      } else {
        walk(child);
      }
    }
  };
  walk(node);
  return found;
}

/**
 * Beside every value and section of a record in the explorer (osdu/docs/reference/concepts/explorer.md, A record): the
 * Lucene query that finds the records holding exactly it (a value, a list, an object, a nested list's item), written by
 * the control plane from how the template of the record's kind a synced repository holds has the platform index each value, with its words
 * and whether a record holds one beside it. The query is what shows; how it was written is a tooltip away.
 */
export function ElementQueryButton({ field, onSearch }: { field: InspectorField; onSearch: (kind: string, query: string) => void }) {
  const [open, setOpen] = useState(false);
  const record = field.trail[field.trail.length - 1]?.record ?? null;
  const kind = record !== null && typeof record.kind === "string" ? record.kind : null;
  const section = field.node.kind !== "leaf";
  const request = kind === null
    ? null
    : section
      ? { kind, path: field.node.path, section, values: leavesOf(field.node) }
      : { kind, path: field.node.path, section, value: scalar(field.node.value) };
  const answer = useQuery({
    queryKey: ["explorer", "element-queries", request],
    queryFn: () => elementQueries(request!),
    enabled: open && request !== null,
    staleTime: 10 * 60_000,
    retry: false,
    refetchOnWindowFocus: false,
  });
  if (kind === null) {
    return null;
  }

  const search = (query: string) => {
    setOpen(false);
    onSearch(kind, query);
  };

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          className="inline-flex size-5 items-center justify-center rounded text-muted-foreground hover:bg-accent hover:text-foreground"
          aria-label={`The query that finds ${field.node.path}`}
          title="The query that finds this"
          onClick={(event) => event.stopPropagation()}
          data-testid="explorer-element-query"
          data-path={field.node.path}
        >
          <SearchCode className="size-3.5" />
        </button>
      </PopoverTrigger>
      {/* A click inside stays inside: the button can sit in a row that opens on a click. */}
      <PopoverContent align="end" className="w-[min(620px,92vw)] p-3" onClick={(event) => event.stopPropagation()} data-testid="explorer-element-queries">
        {answer.isPending && (
          <span className="inline-flex items-center gap-1.5 text-[12px] text-muted-foreground">
            <Loader2 className="size-3.5 animate-spin" aria-hidden />
            Writing the query
          </span>
        )}
        {answer.isError && <p className="text-[12px] text-destructive" data-testid="explorer-element-failed">{failureText(answer.error)}</p>}
        {answer.data !== undefined && <Answer path={field.node.path} answer={answer.data} onSearch={search} />}
      </PopoverContent>
    </Popover>
  );
}

function Answer({ path, answer, onSearch }: { path: string; answer: ExplorerElementAnswer; onSearch: (query: string) => void }) {
  const [main, ...others] = answer.queries;
  const about = [
    answer.reading,
    answer.template === null ? "No template a synced repository holds describes this kind." : `Read by the template ${answer.template.kind}.`,
    ...answer.notes,
  ].filter((line): line is string => line !== null && line !== "");

  return (
    <div className="flex flex-col gap-2.5">
      <div className="flex min-w-0 items-center gap-2 text-[12px] text-muted-foreground">
        <span className="min-w-0 truncate font-mono" title={path}>{path}</span>
        {answer.guess !== null && (
          <RichTooltip title="A guess" body={answer.guess}>
            <span className="shrink-0 rounded border border-warning/50 px-1 text-[11px] leading-4 text-foreground" data-testid="explorer-element-guess">guessed</span>
          </RichTooltip>
        )}
        <RichTooltip title="How this is written" body={about.join("\n\n")}>
          <Info className="ml-auto size-3.5 shrink-0" aria-label="How this is written" data-testid="explorer-element-about" />
        </RichTooltip>
      </div>

      {main === undefined
        ? <p className="text-[12.5px]" data-testid="explorer-element-problem">{answer.problem ?? "No query can be written for this."}</p>
        : (
          <div className="flex flex-col gap-1.5" data-testid="explorer-element-query-item" data-purpose={main.purpose}>
            <RichTooltip body={main.says}>
              <code className="block max-h-48 overflow-auto rounded-md bg-muted px-2.5 py-2 font-mono text-[13px] leading-5 break-all text-foreground" data-testid="explorer-element-lucene">
                {main.query}
              </code>
            </RichTooltip>
            <div className="flex items-center justify-end gap-1">
              <CopyButton label="Copy" text={main.query} testId="explorer-element-copy" />
              <Button size="sm" className="h-7 px-2.5 text-[12px]" onClick={() => onSearch(main.query)} data-testid="explorer-element-search">
                <Search />
                Search
              </Button>
            </div>
          </div>
        )}

      {others.length > 0 && (
        <div className="flex flex-col gap-1 border-t pt-2">
          {others.map((query) => (
            <div key={query.purpose} className="flex min-w-0 items-center gap-2" data-testid="explorer-element-query-item" data-purpose={query.purpose}>
              <RichTooltip body={query.says}>
                <span className="w-16 shrink-0 text-[11.5px] text-muted-foreground">{OTHERS[query.purpose]}</span>
              </RichTooltip>
              <code className="min-w-0 flex-1 truncate font-mono text-[12px]" title={query.query} data-testid="explorer-element-lucene">{query.query}</code>
              <CopyButton iconOnly label="Copy the query" text={query.query} testId="explorer-element-copy" />
              <Button variant="ghost" size="icon-xs" aria-label="Search with it" onClick={() => onSearch(query.query)} data-testid="explorer-element-search">
                <Search />
              </Button>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
