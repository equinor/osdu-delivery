import { useEffect, useMemo, useRef } from "react";
import { cn } from "@/lib/utils";
import { TOKEN_CLASS, tokensOf, type Token } from "../dimensions/yamlTokens";
import { YamlHeading } from "../dimensions/BlueprintYaml";

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
 * The mapping as written, comments and all, each line tied to the canvas: pointing at a line lights the property it writes,
 * clicking picks that property, and whatever is lit on the canvas lights the lines that write it. The lines of the property
 * picked are brought into view.
 */
export function OverviewYaml({ file, yaml, lit, focus, onLine, onPick }: {
  file: string;
  yaml: string;
  /** The lines lit, from 1. */
  lit: ReadonlySet<number>;
  /** The line to bring into view, when something is picked. */
  focus: number | null;
  onLine: (line: number | null) => void;
  onPick: (line: number) => void;
}) {
  const lines = useMemo(() => yaml.replace(/\r\n/g, "\n").split("\n").map(runsOf), [yaml]);
  const box = useRef<HTMLDivElement>(null);

  useEffect(() => {
    // Inside the pane alone: anything larger would move the page around it.
    const pane = box.current;
    const line = pane?.querySelector<HTMLElement>(`[data-line="${focus}"]`);
    if (pane !== null && pane !== undefined && line !== null && line !== undefined) {
      pane.scrollTo({ top: line.offsetTop - pane.offsetTop - pane.clientHeight / 3, behavior: "smooth" });
    }
  }, [focus]);

  return (
    <section className="flex h-full min-h-0 min-w-0 flex-col bg-card" data-testid="mapping-overview-yaml">
      <YamlHeading file={file} first={null} last={null} copy={yaml} copyLabel="Copy the mapping's YAML" copyTestId="mapping-overview-yaml-copy" />
      <div ref={box} className="min-h-0 flex-1 overflow-auto py-1.5" onMouseLeave={() => onLine(null)}>
        <pre className="min-w-max font-mono text-[12px] leading-5">
          {lines.map((runs, index) => {
            const number = index + 1;
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
                <code>{runs.map((run, at) => <span key={at} className={TOKEN_CLASS[run.token]}>{run.text}</span>)}</code>
              </div>
            );
          })}
        </pre>
      </div>
    </section>
  );
}
