import { useMemo, useState } from "react";
import { Loader2 } from "lucide-react";
import type { DimensionCompose } from "../../../../api/explorer";
import type { BlueprintLight } from "../../dimensions/BlueprintDiagram";
import { BlueprintYaml } from "../../dimensions/BlueprintYaml";
import { elementsOfTarget, highlightOf, layoutOf, type BlueprintElement } from "../../dimensions/blueprintModel";
import { IssueLine } from "./StoryFrame";

/**
 * The dimension as YAML, the builder's output (osdu/docs/reference/concepts/explorer-dimension-builder.md): the item a
 * dimension flow lists under `dimensions`, as the control plane wrote it and read it back through the loader a flow is
 * read by, never a copy made here. What is wrong with it heads it, each finding lighting the lines it is about, and a
 * line pointed at lights the parts of the dimension it declares.
 */
export function BuilderYaml({ compose, failure }: { compose: DimensionCompose | undefined; failure: string | null }) {
  const [pointed, setPointed] = useState<string | null>(null);
  const layout = useMemo(() => (compose?.blueprint == null ? null : layoutOf(compose.blueprint)), [compose]);
  const light = useMemo((): BlueprintLight | null => {
    if (layout === null || pointed === null) {
      return null;
    }

    const elements = elementsOfTarget(layout, pointed);
    return elements.length === 0 ? null : highlightOf(layout, elements);
  }, [layout, pointed]);

  const issues = [...(compose?.issues.filter((issue) => issue.severity === "error") ?? []), ...(compose?.issues.filter((issue) => issue.severity === "warning") ?? [])];
  const pointElements = (elements: BlueprintElement[] | null) => setPointed(elements === null || layout === null ? null : targetOf(layout, elements));

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-2.5 overflow-auto px-4 py-3" data-testid="builder-expression">
      <p className="text-[12.5px] text-muted-foreground">
        Paste it under <span className="font-mono text-foreground">dimensions:</span> in a dimension flow; a build of the flow writes{" "}
        <span className="font-mono text-foreground">{compose?.table ?? "the dimension's table"}</span>.
      </p>

      {failure !== null && (
        <IssueLine issue={{ severity: "error", message: `The YAML could not be written: ${failure}`, target: null, code: null }} testId="builder-compose-failed" />
      )}
      {issues.length > 0 && (
        <ul className="flex flex-col" data-testid="builder-issues">
          {issues.map((issue, index) => (
            <li key={`${index}-${issue.message}`} onMouseEnter={() => setPointed(issue.target)} onMouseLeave={() => setPointed(null)}>
              <IssueLine issue={issue} />
            </li>
          ))}
        </ul>
      )}

      {compose === undefined
        ? (
          <span className="inline-flex items-center gap-1.5 text-[12px] text-muted-foreground">
            <Loader2 className="size-3.5 animate-spin" aria-hidden />
            Writing the YAML
          </span>
        )
        : layout !== null
          ? <BlueprintYaml yaml={compose.item} missing={null} layout={layout} light={light} onPoint={pointElements} onPick={() => undefined} />
          : <PlainYaml lines={compose.item.lines} />}
    </div>
  );
}

/** The part of the YAML a set of the blueprint's elements is declared at: the first span naming one of them. */
function targetOf(layout: ReturnType<typeof layoutOf>, elements: BlueprintElement[]): string | null {
  for (const read of layout.reads.values()) {
    if (elements.includes(`read:${read.id}`)) {
      const use = read.uses[0];
      if (use !== undefined) {
        return use.role === "key" ? "path" : use.role === "label" ? "label" : `attributes.${use.attribute ?? ""}`;
      }
    }
  }

  return null;
}

/** The YAML of an item the loader did not read, line by line, with nothing linked. */
function PlainYaml({ lines }: { lines: string[] }) {
  return (
    <section className="overflow-hidden rounded-lg border border-border bg-card" data-testid="builder-yaml-plain">
      <pre className="overflow-x-auto py-1.5 font-mono text-[12px] leading-5">
        {lines.map((line, index) => (
          <div key={index} className="flex pr-4">
            <span className="w-10 shrink-0 select-none pr-3 text-right tabular-nums text-muted-foreground/60">{index + 1}</span>
            <code>{line}</code>
          </div>
        ))}
      </pre>
    </section>
  );
}
