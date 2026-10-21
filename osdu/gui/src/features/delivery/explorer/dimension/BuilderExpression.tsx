import { useMemo, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { CircleCheck, CircleX, Info, Loader2, TriangleAlert, Workflow } from "lucide-react";
import { Button } from "@/components/ui/button";
import { CopyButton } from "@/components/CopyButton";
import { cn } from "@/lib/utils";
import type { DimensionCompose, DimensionDraftIssue } from "../../../../api/explorer";
import type { BlueprintLight } from "../../dimensions/BlueprintDiagram";
import { BlueprintYaml } from "../../dimensions/BlueprintYaml";
import { elementsOfTarget, highlightOf, layoutOf, type BlueprintElement } from "../../dimensions/blueprintModel";

/**
 * The dimension as YAML, the builder's output: the item a dimension flow lists under `dimensions`, ready to copy into one,
 * with what is wrong with it above it, each problem pointing at the lines it is about, and what the reads of OSDU had to say.
 * The YAML is what the control plane wrote and read back through the loader a flow is read by, never a copy made here.
 */
export function BuilderExpression({ compose, composing, failure, notes, pointed, onPoint, onShowRow }: {
  compose: DimensionCompose | undefined;
  composing: boolean;
  failure: string | null;
  notes: string[];
  pointed: string | null;
  onPoint: (target: string | null) => void;
  /** Opens how a build makes each column, drawn as a flow's own dimension is. */
  onShowRow: () => void;
}) {
  const layout = useMemo(() => (compose?.blueprint == null ? null : layoutOf(compose.blueprint)), [compose]);
  const light = useMemo((): BlueprintLight | null => {
    if (layout === null || pointed === null) {
      return null;
    }

    const elements = elementsOfTarget(layout, pointed);
    return elements.length === 0 ? null : highlightOf(layout, elements);
  }, [layout, pointed]);

  const errors = compose?.issues.filter((issue) => issue.severity === "error") ?? [];
  const warnings = compose?.issues.filter((issue) => issue.severity === "warning") ?? [];
  const pointElements = (elements: BlueprintElement[] | null) => onPoint(elements === null || layout === null ? null : targetOf(layout, elements));

  return (
    <div className="flex min-h-0 flex-col gap-2 overflow-auto p-3" data-testid="builder-expression">
      <div className="flex min-w-0 flex-wrap items-center gap-2">
        <h3 className="text-[13px] font-semibold">The expression</h3>
        <Status compose={compose} composing={composing} errors={errors.length} warnings={warnings.length} />
        <span className="ml-auto flex items-center gap-1">
          <Button variant="ghost" size="sm" onClick={onShowRow} disabled={compose?.blueprint == null} data-testid="builder-show-row">
            <Workflow />
            How a row is built
          </Button>
          {compose !== undefined && <CopyButton label="Copy the YAML" text={compose.yaml} testId="builder-copy" />}
        </span>
      </div>
      <p className="text-[12px] text-muted-foreground">
        Paste it under <span className="font-mono">dimensions:</span> in a dimension flow; a build of the flow makes{" "}
        <span className="font-mono text-foreground">{compose?.table ?? "the dimension's table"}</span>.
      </p>

      {failure !== null && (
        <Item tone="error" testId="builder-compose-failed">The YAML could not be written: {failure}</Item>
      )}
      {(errors.length > 0 || warnings.length > 0) && (
        <ul className="flex flex-col gap-1" data-testid="builder-issues">
          {[...errors, ...warnings].map((issue, index) => (
            <IssueItem key={`${index}-${issue.message}`} issue={issue} onPoint={onPoint} />
          ))}
        </ul>
      )}

      {compose !== undefined && (
        layout !== null
          ? <BlueprintYaml yaml={compose.item} missing={null} layout={layout} light={light} onPoint={pointElements} onPick={() => undefined} />
          : <PlainYaml lines={compose.item.lines} />
      )}

      {notes.length > 0 && (
        <ul className="flex flex-col gap-1 border-t border-border/60 pt-2" data-testid="builder-notes">
          {notes.map((note, index) => <Item key={`${index}-${note}`} tone="note">{note}</Item>)}
        </ul>
      )}
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

function Status({ compose, composing, errors, warnings }: { compose: DimensionCompose | undefined; composing: boolean; errors: number; warnings: number }) {
  if (compose === undefined) {
    return <span className="inline-flex items-center gap-1 text-[12px] text-muted-foreground"><Loader2 className="size-3.5 animate-spin" aria-hidden />writing</span>;
  }

  const label = errors > 0
    ? `${errors} ${errors === 1 ? "problem" : "problems"}`
    : warnings > 0 ? `ready, ${warnings} ${warnings === 1 ? "warning" : "warnings"}` : "ready to paste";
  return (
    <span className="inline-flex items-center gap-1 rounded-full border border-border px-2 py-0.5 text-[12px] text-muted-foreground" data-testid="builder-status" data-valid={compose.valid}>
      {composing ? <Loader2 className="size-3.5 animate-spin" aria-hidden />
        : errors > 0 ? <CircleX className="size-3.5 text-destructive" aria-hidden />
        : warnings > 0 ? <TriangleAlert className="size-3.5 text-warning" aria-hidden />
        : <CircleCheck className="size-3.5 text-success" aria-hidden />}
      {label}
    </span>
  );
}

function IssueItem({ issue, onPoint }: { issue: DimensionDraftIssue; onPoint: (target: string | null) => void }) {
  return (
    <li onMouseEnter={() => onPoint(issue.target)} onMouseLeave={() => onPoint(null)} data-testid="builder-issue" data-severity={issue.severity} data-target={issue.target ?? ""}>
      <Item tone={issue.severity === "error" ? "error" : "warning"}>
        <span className="break-words">{issue.message}</span>
        {issue.code === "template" && (
          <>
            {" "}
            <Link to="/delivery/templates" className="whitespace-nowrap text-primary hover:underline" data-testid="builder-issue-templates">Download it on the Templates page</Link>
          </>
        )}
      </Item>
    </li>
  );
}

function Item({ tone, children, testId }: { tone: "error" | "warning" | "note"; children: ReactNode; testId?: string }) {
  const Icon = tone === "error" ? CircleX : tone === "warning" ? TriangleAlert : Info;
  return (
    <div className="flex min-w-0 items-start gap-1.5 text-[12px] leading-5" data-testid={testId}>
      <Icon className={cn("mt-1 size-3.5 shrink-0", tone === "error" ? "text-destructive" : tone === "warning" ? "text-warning" : "text-muted-foreground")} aria-hidden />
      <span className={cn("min-w-0", tone === "note" && "text-muted-foreground")}>{children}</span>
    </div>
  );
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
