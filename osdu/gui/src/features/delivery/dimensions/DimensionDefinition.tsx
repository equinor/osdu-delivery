import { useCallback, useMemo, useState, type KeyboardEvent } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ChevronLeft, ChevronRight, CircleX, Eye, EyeOff, RotateCw, TriangleAlert } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import { deliveryApi, type DeliveryDimensionCoverage } from "../../../api/delivery";
import { problemText } from "../problemText";
import { BlueprintDiagram, type BlueprintLight } from "./BlueprintDiagram";
import { BlueprintInspector } from "./BlueprintInspector";
import { BlueprintYaml } from "./BlueprintYaml";
import { ROLE_GLYPHS, exampleIndex, examplesOf, highlightOf, layoutOf, type BlueprintElement } from "./blueprintModel";
import type { DimensionEntry } from "./dimensionFormat";

/** How many of the keys with the most records a page can step through as examples. */
const EXAMPLE_KEYS = 25;

/** The table a dimension's first build makes: its name with whatever is not a letter, a digit or an underscore an underscore. */
function tableOf(name: string): string {
  return `osdu.dim_${name.replace(/[^A-Za-z0-9_]/g, "_")}`;
}

/**
 * How the flow's YAML builds the dimension, read against the templates of every record it reads: a diagram from the
 * records searched, through each record found by id, to the columns of the table; the dimension's YAML beside it, every
 * line linked to what it declares; and a panel explaining what is picked. Pointing at a line of the YAML, a read or a
 * column lights everything that makes the same thing. Once built, an example key fills each part with what it read.
 */
export function DimensionDefinition({ entry }: { entry: DimensionEntry }) {
  const { dimension, flow } = entry;
  const blueprint = useQuery({
    queryKey: ["delivery", "dimensions", "blueprint", flow.pipelineId, dimension.name, flow.partition],
    queryFn: () => deliveryApi.dimensionBlueprint(flow.pipelineId, dimension.name, flow.partition),
  });

  const built = dimension.dimensionId !== null && dimension.keys > 0;
  const [showExamples, setShowExamples] = useState(true);
  const keys = useQuery({
    queryKey: ["delivery", "dimensions", "keys", dimension.dimensionId, "examples"],
    queryFn: () => deliveryApi.dimensionKeys(dimension.dimensionId!, { order: "count", limit: EXAMPLE_KEYS }),
    enabled: built && showExamples,
  });

  const [pointed, setPointed] = useState<BlueprintElement[] | null>(null);
  const [picked, setPicked] = useState<BlueprintElement | null>(null);
  const [exampleAt, setExampleAt] = useState<number | null>(null);

  const layout = useMemo(() => (blueprint.data === undefined ? null : layoutOf(blueprint.data.blueprint)), [blueprint.data]);
  const coverage = useMemo(
    () => new Map<string, DeliveryDimensionCoverage>((blueprint.data?.coverage ?? []).map((c) => [c.name, c])),
    [blueprint.data],
  );

  const found = keys.data?.items ?? [];
  const at = found.length === 0 ? -1 : Math.min(found.length - 1, exampleAt ?? exampleIndex(found));
  const example = showExamples && at >= 0 ? found[at] : null;
  const examples = useMemo(() => (layout === null || example === null ? null : examplesOf(layout, example)), [layout, example]);

  const light = useMemo((): BlueprintLight | null => {
    if (layout === null) {
      return null;
    }

    const shown = pointed ?? (picked === null ? null : [picked]);
    return shown === null || shown.length === 0 ? null : highlightOf(layout, shown);
  }, [layout, pointed, picked]);

  const pointOne = useCallback((element: BlueprintElement | null) => setPointed(element === null ? null : [element]), []);
  const pick = useCallback((element: BlueprintElement | null) => setPicked((current) => (element === null || current === element ? null : element)), []);

  if (blueprint.isPending) {
    return (
      <div className="flex flex-col gap-3" data-testid="dimension-definition">
        <Skeleton className="h-72 w-full rounded-lg" />
        <div className="grid gap-3 xl:grid-cols-2">
          <Skeleton className="h-64 rounded-lg" />
          <Skeleton className="h-64 rounded-lg" />
        </div>
      </div>
    );
  }

  if (blueprint.isError || layout === null) {
    return (
      <Alert variant="destructive" data-testid="dimension-definition-error">
        <CircleX />
        <AlertTitle>The definition could not be read</AlertTitle>
        <AlertDescription className="flex flex-col gap-1.5">
          <span className="break-words">{problemText(blueprint.error)}</span>
          <Button size="xs" variant="outline" className="self-start" onClick={() => void blueprint.refetch()}>
            <RotateCw />
            Try again
          </Button>
        </AlertDescription>
      </Alert>
    );
  }

  const response = blueprint.data;
  const table = dimension.table ?? tableOf(dimension.name);
  const undescribed = response.blueprint.records.filter((node) => node.template === null && node.entityTypes.length > 0).flatMap((node) => node.entityTypes);
  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === "Escape") {
      setPicked(null);
    }
  };

  return (
    <div className="flex flex-col gap-3" data-testid="dimension-definition" onKeyDown={onKeyDown}>
      <section className="min-w-0 overflow-hidden rounded-lg border border-border bg-card" data-testid="blueprint">
        <header className="flex min-w-0 flex-wrap items-center gap-x-4 gap-y-1.5 border-b border-border bg-muted/40 px-3 py-2">
          <h3 className="text-[13px] font-semibold">How a row is built</h3>
          <Legend />
          {built && (
            <ExampleControl
              shown={showExamples}
              onShown={setShowExamples}
              loading={keys.isPending && showExamples}
              at={at}
              count={found.length}
              label={example === null ? null : (example.value ?? example.key)}
              keyText={example?.key ?? null}
              onStep={(step) => setExampleAt((found.length + at + step) % found.length)}
            />
          )}
        </header>
        <div onClick={() => setPicked(null)}>
          <BlueprintDiagram
            layout={layout}
            dimension={dimension}
            table={table}
            coverage={coverage}
            light={light}
            picked={picked}
            examples={examples}
            onPoint={pointOne}
            onPick={pick}
          />
        </div>
        {undescribed.length > 0 && (
          <p className="flex items-start gap-1.5 border-t border-border/60 px-4 py-2 text-[12px] text-muted-foreground" data-testid="blueprint-undescribed">
            <TriangleAlert className="mt-0.5 size-3.5 shrink-0 text-warning" aria-hidden />
            <span>
              No template folder on the flow's path holds a template of <span className="font-mono text-foreground">{[...new Set(undescribed)].join(", ")}</span>,
              so the paths read from those records are read as written, unchecked. Put one there: Browse OSDU on the{" "}
              <Link to="/delivery/templates" className="text-primary hover:underline">Templates</Link> page downloads it.
            </span>
          </p>
        )}
      </section>

      <div className="grid min-w-0 items-start gap-3 xl:grid-cols-[minmax(0,1.1fr)_minmax(0,0.9fr)]">
        <BlueprintYaml yaml={response.yaml} missing={response.yamlMissing} layout={layout} light={light} onPoint={setPointed} onPick={pick} />
        <BlueprintInspector
          layout={layout}
          dimension={dimension}
          table={table}
          partition={flow.partition}
          coverage={coverage}
          picked={picked}
          examples={examples}
          onPick={pick}
        />
      </div>
    </div>
  );
}

/** What the glyphs and the lines of the diagram mean, in one line. */
function Legend() {
  const glyphs = [ROLE_GLYPHS.key, ROLE_GLYPHS.label, ROLE_GLYPHS.attribute, ROLE_GLYPHS.collect, ROLE_GLYPHS.step];
  return (
    <ul className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 text-[11.5px] text-muted-foreground" data-testid="blueprint-legend">
      {glyphs.map(({ icon: Icon, label }) => (
        <li key={label} className="flex items-center gap-1">
          <Icon className="size-3.5" aria-hidden />
          {label}
        </li>
      ))}
      <li className="flex items-center gap-1">
        <svg width="22" height="8" aria-hidden><path d="M1 4 H21" className="stroke-muted-foreground" strokeWidth="1.25" strokeDasharray="4 3" /></svg>
        records read next
      </li>
      <li className="flex items-center gap-1">
        <svg width="22" height="8" aria-hidden><path d="M1 4 H21" className="stroke-muted-foreground" strokeWidth="1.25" /></svg>
        written to
      </li>
    </ul>
  );
}

/** One key of the dimension filling the diagram with what it read: shown or not, and the keys with the most records stepped through. */
function ExampleControl({ shown, onShown, loading, at, count, label, keyText, onStep }: {
  shown: boolean;
  onShown: (shown: boolean) => void;
  loading: boolean;
  at: number;
  count: number;
  label: string | null;
  keyText: string | null;
  onStep: (step: number) => void;
}) {
  return (
    <div className="ml-auto flex shrink-0 items-center gap-1 text-[12px]" data-testid="blueprint-example-control">
      <RichTooltip body={shown ? "Hide what one key read at each part." : "Fill each part with what one key read, from the keys with the most records."}>
        <Button variant="ghost" size="icon-xs" aria-label={shown ? "Hide the example" : "Show an example"} onClick={() => onShown(!shown)} data-testid="blueprint-example-toggle">
          {shown ? <Eye /> : <EyeOff />}
        </Button>
      </RichTooltip>
      <span className="text-muted-foreground">Example</span>
      {shown && count > 0 && (
        <>
          <Button variant="ghost" size="icon-xs" aria-label="The previous key" onClick={() => onStep(-1)} disabled={count < 2}>
            <ChevronLeft />
          </Button>
          <RichTooltip title="Key" body={keyText ?? ""} mono>
            <span className="max-w-48 truncate font-mono text-foreground" data-testid="blueprint-example-key">{label}</span>
          </RichTooltip>
          <Button variant="ghost" size="icon-xs" aria-label="The next key" onClick={() => onStep(1)} disabled={count < 2}>
            <ChevronRight />
          </Button>
          <span className="font-mono tabular-nums text-muted-foreground">{at + 1}/{count}</span>
        </>
      )}
      {shown && count === 0 && (
        <span className={cn("text-muted-foreground", loading && "animate-pulse")}>{loading ? "reading keys" : "no key to show"}</span>
      )}
    </div>
  );
}
