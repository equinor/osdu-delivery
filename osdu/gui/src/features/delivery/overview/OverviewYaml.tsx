import { useEffect, useMemo, useRef, useState } from "react";
import { Copy } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { ContextMenu, ContextMenuContent, ContextMenuItem, ContextMenuLabel, ContextMenuSeparator, ContextMenuTrigger } from "@/components/ui/context-menu";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { ExplainIcon } from "@/features/chat/ExplainIcon";
import { cn } from "@/lib/utils";
import { TOKEN_CLASS, tokensOf, type Token } from "../dimensions/yamlTokens";
import { YamlHeading } from "../dimensions/BlueprintYaml";
import { markRuns, type LineRun } from "./partLines.ts";
import { explainedYaml, linesLabel, yamlLines, type ExplainedYaml, type LineRange } from "./yamlExplain.ts";

/** The lines drawn, each split into runs of one token. */
function runsOf(line: string): { text: string; token: Token }[] {
  const tokens = tokensOf(line);
  const runs: { text: string; token: Token }[] = [];
  for (let at = 0; at < line.length; at++) {
    const last = runs[runs.length - 1];
    if (last !== undefined && last.token === tokens[at]) {
      last.text += line[at];
    } else {
      runs.push({ text: line[at], token: tokens[at] });
    }
  }

  return runs;
}

/**
 * The lines of the pane the reader's selection holds text of, or null when it holds none. A line counts when the selection
 * starts before its text ends and ends after its text starts, so a drag that stops at the start of the next line leaves
 * that line out, and a selection running on past the pane takes only the lines in it.
 */
function selectedLines(pane: HTMLElement | null): LineRange | null {
  const selection = window.getSelection();
  if (pane === null || selection === null || selection.isCollapsed || selection.rangeCount === 0) {
    return null;
  }

  const selected = selection.getRangeAt(0);
  const text = document.createRange();
  let start: number | null = null;
  let end: number | null = null;
  for (const row of pane.querySelectorAll<HTMLElement>("[data-line]")) {
    const code = row.querySelector("code");
    if (code === null) {
      continue;
    }

    text.selectNodeContents(code);
    if (selected.compareBoundaryPoints(Range.END_TO_START, text) < 0 && selected.compareBoundaryPoints(Range.START_TO_END, text) > 0) {
      const line = Number(row.dataset.line);
      start ??= line;
      end = line;
    }
  }

  return start === null || end === null ? null : { start, end };
}

/** What a right click is on: the lines selected, the property the line clicked writes, that line alone, or the whole mapping. */
interface MenuTarget {
  /** Null for the whole mapping. */
  range: LineRange | null;
  /** The property the lines write, when the click picked them by the property. */
  named: string | null;
}

/**
 * The mapping as written, comments and all, each line tied to the canvas: pointing at a line lights the property it writes,
 * clicking picks that property, and whatever is lit on the canvas lights the lines that write it, a column or a table marked
 * where the lines name it. The first line of what is picked is brought into view. Explain asks the assistant about the lines
 * selected, or the whole mapping; a right click offers it over the lines selected, or else over the property the line
 * clicked writes, and copies the same lines.
 */
export function OverviewYaml({ file, yaml, lit, marks, focus, focusKey, onLine, onPick, onExplain, blockOf }: {
  file: string;
  yaml: string;
  /** The lines lit, from 1. */
  lit: ReadonlySet<number>;
  /** Where on a line the column or table lit is named, by line from 1. */
  marks: ReadonlyMap<number, readonly LineRun[]>;
  /** The line to bring into view, when something is picked. */
  focus: number | null;
  /** What was picked, so picking another thing on the same line brings it into view again. */
  focusKey: string;
  onLine: (line: number | null) => void;
  onPick: (line: number) => void;
  /** Asks the assistant about lines of the mapping, or all of it; null while the assistant is not configured. */
  onExplain: ((code: ExplainedYaml) => void) | null;
  /** The lines of the property a line writes, with the property's path; null for a line that writes none. */
  blockOf: (line: number) => { range: LineRange; named: string } | null;
}) {
  const lines = useMemo(() => yamlLines(yaml).map(runsOf), [yaml]);
  const box = useRef<HTMLDivElement>(null);
  const [menu, setMenu] = useState<MenuTarget>({ range: null, named: null });

  useEffect(() => {
    // Inside the pane alone: anything larger would move the page around it.
    const pane = box.current;
    const line = pane?.querySelector<HTMLElement>(`[data-line="${focus}"]`);
    if (pane !== null && pane !== undefined && line !== null && line !== undefined) {
      pane.scrollTo({ top: line.offsetTop - pane.offsetTop - pane.clientHeight / 3, behavior: "smooth" });
    }
  }, [focus, focusKey]);

  const aimMenu = (target: EventTarget) => {
    const selected = selectedLines(box.current);
    if (selected !== null) {
      setMenu({ range: selected, named: null });
      return;
    }

    const row = target instanceof Element ? target.closest<HTMLElement>("[data-line]") : null;
    if (row === null) {
      setMenu({ range: null, named: null });
      return;
    }

    const line = Number(row.dataset.line);
    setMenu(blockOf(line) ?? { range: { start: line, end: line }, named: null });
  };

  const copyMenu = () => {
    const { code, lines: range } = explainedYaml(yaml, menu.range);
    const what = range === null ? "the mapping's YAML" : linesLabel(range).toLowerCase();
    void navigator.clipboard
      .writeText(code)
      .then(() => toast.success(`Copied ${what}`))
      .catch(() => toast.error(`Could not copy ${what} to the clipboard`));
  };

  const explain = onExplain === null ? undefined : (
    <Tooltip>
      <TooltipTrigger asChild>
        <Button
          variant="ghost"
          size="xs"
          // Pressing the button would otherwise take away the selection it asks about.
          onMouseDown={(event) => event.preventDefault()}
          onClick={() => onExplain(explainedYaml(yaml, selectedLines(box.current)))}
          className="text-muted-foreground hover:text-foreground"
          data-testid="mapping-overview-yaml-explain"
        >
          <ExplainIcon />
          Explain
        </Button>
      </TooltipTrigger>
      <TooltipContent>The assistant explains the selected lines, or the whole mapping when nothing is selected, under the canvas</TooltipContent>
    </Tooltip>
  );

  return (
    <section className="flex h-full min-h-0 min-w-0 flex-col bg-card" data-testid="mapping-overview-yaml">
      <YamlHeading file={file} first={null} last={null} copy={yaml} copyLabel="Copy the mapping's YAML" copyTestId="mapping-overview-yaml-copy" actions={explain} />
      {/* Without the assistant there is nothing of the pane's own to offer, and the browser's menu stays. */}
      <ContextMenu>
        <ContextMenuTrigger asChild disabled={onExplain === null} onContextMenu={(event) => aimMenu(event.target)}>
          <div ref={box} className="min-h-0 flex-1 overflow-auto py-1.5" onMouseLeave={() => onLine(null)}>
            <pre className="min-w-max font-mono text-[12px] leading-5">
              {lines.map((runs, index) => {
                const number = index + 1;
                const named = marks.get(number);
                return (
                  <div
                    key={number}
                    data-line={number}
                    className={cn("flex cursor-pointer border-l-2 pr-4 hover:bg-accent/60", lit.has(number) ? "border-primary bg-primary/[0.06]" : "border-transparent")}
                    onMouseEnter={() => onLine(number)}
                    onClick={() => onPick(number)}
                    data-testid="mapping-overview-yaml-line"
                  >
                    <span className="w-10 shrink-0 select-none pr-3 text-right tabular-nums text-muted-foreground/60">{number}</span>
                    <code>
                      {named === undefined
                        ? runs.map((run, at) => <span key={at} className={TOKEN_CLASS[run.token]}>{run.text}</span>)
                        : markRuns(runs, named).map((run, at) => (run.marked
                          // A mark takes the colour of what it covers; the browser's own black would vanish on the dark theme.
                          ? <mark key={at} className={cn("rounded-[2px] bg-primary/20 ring-1 ring-primary/40", TOKEN_CLASS[run.token] === "" ? "text-foreground" : TOKEN_CLASS[run.token])} data-testid="mapping-overview-yaml-mark">{run.text}</mark>
                          : <span key={at} className={TOKEN_CLASS[run.token]}>{run.text}</span>))}
                    </code>
                  </div>
                );
              })}
            </pre>
          </div>
        </ContextMenuTrigger>
        <ContextMenuContent className="w-64" data-testid="mapping-overview-yaml-menu">
          <ContextMenuLabel className="flex min-w-0 items-baseline gap-1.5 text-[12px] font-normal text-muted-foreground">
            <span className="shrink-0">{menu.range === null ? "The whole mapping" : linesLabel(menu.range)}</span>
            {menu.named !== null && <span className="min-w-0 truncate font-mono" title={menu.named}>{menu.named}</span>}
          </ContextMenuLabel>
          <ContextMenuSeparator />
          <ContextMenuItem onSelect={() => onExplain?.(explainedYaml(yaml, menu.range))} data-testid="mapping-overview-yaml-menu-explain">
            <ExplainIcon className="size-3.5" />
            Explain this
          </ContextMenuItem>
          <ContextMenuItem onSelect={copyMenu} data-testid="mapping-overview-yaml-menu-copy">
            <Copy className="size-3.5" />
            Copy
          </ContextMenuItem>
        </ContextMenuContent>
      </ContextMenu>
    </section>
  );
}
