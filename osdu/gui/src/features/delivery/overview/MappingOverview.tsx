import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { useQuery } from "@tanstack/react-query";
import { ChevronLeft, ChevronRight, ChevronsDownUp, ChevronsUpDown, FileCode, Loader2, Maximize, Minus, OctagonAlert, Plus, Rows3, TriangleAlert } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from "@/components/ui/resizable";
import { Skeleton } from "@/components/ui/skeleton";
import { ExplainMark } from "@/components/ExplainTip";
import { SearchInput } from "@/components/SearchInput";
import { usePersistentLayout } from "@/layout/workbench/usePersistentLayout";
import { cn } from "@/lib/utils";
import { deliveryApi, type DeliveryMappingDetail, type MappingDraftIssue, type MappingOutline, type MappingOutlineHow } from "../../../api/delivery";
import { useActivePartition } from "../activePartition";
import { LedgerStanding } from "../LedgerStanding";
import { ProblemView } from "../TemplateSheet";
import { ValueCheckScope } from "../ValueCheckBar";
import { flowScope, type ValueCheckSession } from "../useValueCheck";
import { formatCount } from "../valueCheck";
import { OverviewCanvas, type OverviewHandlers, type OverviewPart, type OverviewTip } from "./OverviewCanvas";
import { INSPECTOR_WIDTH, OverviewInspector } from "./OverviewInspector";
import { OverviewYaml } from "./OverviewYaml";
import { layoutOf } from "./outlineLayout.ts";
import {
  cardsOf, countsOf, defaultOpen, everyFolding, lightOfCard, lightOfField, lightOfRows, marksFor, nodeAt, nodeAtLine, opensTo, pathsOfHow, pathsOfSearch, rowShowing, rowsOf,
  type OutlineLight,
} from "./outlineModel.ts";
import { useRecordValues, type RecordValues } from "./useRecordValues";

const ABOUT = "Each line is one property of the record, read left to right: the table it starts from, what is done to it, and the property it fills. "
  + "A dot is where a line is looked up in a table on the shelf above. A dashed mark is a condition, and a hollow end an optional property. "
  + "Click a section to open it, and a property to see how it is filled. Hold Ctrl and scroll to zoom. Nothing is rendered here, unless you ask to show a record.";

/** What a canvas showing no record is given, one map for every render so nothing is drawn again for a new empty one. */
const NO_VALUES: ReadonlyMap<string, string> = new Map();

const HOWS: { how: MappingOutlineHow; label: string; count: keyof ReturnType<typeof countsOf>; meaning: string }[] = [
  { how: "copied", label: "copied", count: "copied", meaning: "A column, written as it is." },
  { how: "changed", label: "changed", count: "changed", meaning: "A column or an expression, changed on its way." },
  { how: "lookedUp", label: "looked up", count: "lookedUp", meaning: "Found in the partition's cache, or by a search of OSDU." },
  { how: "fixed", label: "fixed", count: "fixed", meaning: "The same on every record: a literal, or a parameter of the flow." },
  { how: "linked", label: "linked", count: "linked", meaning: "Read from another record: the parent, a child, or an enrichment." },
];

/** The name of a kind's entity: `WellLog` of `osdu:wks:work-product-component--WellLog:1.4.0`. */
function entityOf(kind: string): { entity: string; version: string } {
  const parts = kind.split(":");
  const type = parts[2] ?? kind;
  return { entity: type.slice(type.lastIndexOf("-") + 1), version: parts[3] ?? "" };
}

/** The one line saying what the mapping does: the rows it reads, the record it makes, and what repeats. */
function Lead({ outline, table, repeats }: { outline: MappingOutline; table: string | null; repeats: { name: string; table: string | null }[] }) {
  const { entity } = entityOf(outline.kind);
  if (outline.enrichment) {
    return <>Adds to each <span className="font-medium">{entity}</span> record another mapping renders.</>;
  }

  return (
    <>
      Each row of {table === null ? "the record table" : <span className="font-mono font-medium">{table}</span>} becomes one <span className="font-medium">{entity}</span> record
      {repeats.map((repeat) => (
        <span key={repeat.name}>, with one item of <span className="font-medium">{repeat.name}</span> per row of {repeat.table === null ? "its child table" : <span className="font-mono font-medium">{repeat.table}</span>}</span>
      ))}.
    </>
  );
}

/** A tooltip at a place on the canvas, kept inside the frame's width by its own transform. */
function CanvasTip({ tip }: { tip: OverviewTip }) {
  return (
    <div
      role="tooltip"
      className="pointer-events-none absolute z-20 flex max-w-80 -translate-x-1/2 -translate-y-full flex-col gap-0.5 rounded-md border border-border bg-popover px-2 py-1.5 text-popover-foreground shadow-md"
      style={{ left: Math.max(170, tip.x), top: Math.max(56, tip.y - 8) }}
      data-testid="mapping-overview-tip"
    >
      <span className="font-mono text-[11.5px] font-medium [overflow-wrap:anywhere]">{tip.title}</span>
      {tip.body !== "" && <span className="whitespace-pre-line text-[12px] text-muted-foreground [overflow-wrap:anywhere]">{tip.body}</span>}
    </div>
  );
}

/** The record being followed through the mapping, in place of the sentence: which row, where it stands, and whose rows these are. */
function RecordLine({ record, session }: { record: RecordValues; session: ValueCheckSession }) {
  const { lines, line } = record;
  const name = line?.label ?? line?.sourceKey;
  return (
    <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1" data-testid="mapping-overview-record">
      {lines !== null && lines.length > 0 && (
        <span className="flex min-w-0 items-center gap-0.5">
          <Button variant="ghost" size="icon-xs" aria-label="The record before" disabled={record.at === 0} onClick={() => record.step(-1)}><ChevronLeft /></Button>
          <span className="max-w-80 truncate font-mono text-[12px]" title={name}>{name}</span>
          <Button variant="ghost" size="icon-xs" aria-label="The record after" disabled={record.at >= lines.length - 1} onClick={() => record.step(1)}><ChevronRight /></Button>
        </span>
      )}
      {lines !== null && lines.length === 0 && <span className="text-muted-foreground" data-testid="mapping-overview-no-row">No row is staged in this scope.</span>}
      {record.ledger !== undefined && <LedgerStanding ledger={record.ledger} />}
      {record.loading && <Loader2 className="size-3.5 animate-spin text-muted-foreground" aria-label="Reading the record" />}
      <ValueCheckScope session={session} />
    </div>
  );
}

/** The canvas and what surrounds it, for a mapping whose outline has been read. */
function OverviewBody({ detail, outline, unfilled, issues, session, tools, onProperties }: {
  detail: DeliveryMappingDetail;
  outline: MappingOutline;
  unfilled: number | null;
  issues: MappingDraftIssue[];
  session: ValueCheckSession;
  tools: HTMLElement | null;
  onProperties: () => void;
}) {
  const { mapping, yaml } = detail;
  const { flow } = session;
  const [open, setOpen] = useState(() => defaultOpen(outline));
  const [picked, setPicked] = useState<OverviewPart | null>(null);
  const [pointed, setPointed] = useState<OverviewPart | null>(null);
  const [linePointed, setLinePointed] = useState<string | null>(null);
  const [how, setHow] = useState<MappingOutlineHow | null>(null);
  const [search, setSearch] = useState("");
  const [showYaml, setShowYaml] = useState(false);
  const [tip, setTip] = useState<OverviewTip | null>(null);
  const layout = usePersistentLayout("sqlflow.delivery.mapping.overview.layout");

  const [partition] = useActivePartition();
  const tables = useQuery({
    queryKey: ["delivery", "flow-source-tables", flow?.pipelineId ?? null, flow?.interface ?? null, flow?.partition ?? null],
    queryFn: () => deliveryApi.sourceTables(flow!.pipelineId, flowScope(flow!)),
    enabled: flow !== null,
    retry: false,
  });
  const caches = useQuery({ queryKey: ["delivery", "mapping-builder", "caches"], queryFn: deliveryApi.builderCaches, retry: false });
  const cached = useMemo(() => caches.data?.find((candidate) => candidate.scope === partition)?.types ?? null, [caches.data, partition]);

  const record = useRecordValues(outline, session);
  const rows = useMemo(() => rowsOf(outline, open), [outline, open]);
  const measured = useMemo(() => rowsOf(outline, everyFolding(outline)), [outline]);
  const cards = useMemo(() => cardsOf(outline, open, tables.data ?? null, cached), [outline, open, tables.data, cached]);
  const placed = useMemo(() => layoutOf(rows, cards, measured), [rows, cards, measured]);
  const counts = useMemo(() => countsOf(outline), [outline]);

  const dryRun = session.dryRun !== null && !session.dryRunStale ? session.dryRun.run : null;
  const marks = useMemo(() => marksFor(rows, [
    ...issues.filter((issue) => issue.target !== null).map((issue) => ({ path: issue.target!.replace(/^osdu\./, ""), tone: issue.severity, text: issue.message })),
    ...(dryRun?.rules ?? []).filter((rule) => rule.at !== "").map((rule) => ({
      path: rule.at.replace(/^osdu\./, "").replace(/\[\d+\]/g, "[]"), tone: "warning" as const, text: `${formatCount(rule.records)} ${rule.records === 1 ? "record breaks" : "records break"} ${rule.rule}. ${rule.exampleMessage}`,
    })),
  ]), [issues, dryRun, rows]);

  const frame = useRef<HTMLDivElement>(null);
  const svg = useRef<SVGSVGElement>(null);
  const canvas = useRef<OverviewCanvas | null>(null);

  // What was just opened, whose rows are brought into view once they are drawn.
  const opened = useRef<string | null>(null);

  const handlers = useRef<OverviewHandlers>({
    point: setPointed,
    pick: setPicked,
    toggle: (path) => setOpen((current) => {
      const next = new Set(current);
      if (!next.delete(path)) {
        next.add(path);
        opened.current = path;
      }

      return next;
    }),
    // The canvas says where on the page; the tooltip is placed in the frame.
    tip: (said) => {
      const box = frame.current?.getBoundingClientRect();
      setTip(said === null || box === undefined ? null : { ...said, x: said.x - box.left, y: said.y - box.top });
    },
    clear: () => setPicked(null),
    leftOut: onProperties,
  });

  useEffect(() => {
    if (svg.current === null) {
      return undefined;
    }

    const drawn = new OverviewCanvas(svg.current, {
      point: (part) => handlers.current.point(part),
      pick: (part) => handlers.current.pick(part),
      toggle: (path) => handlers.current.toggle(path),
      tip: (said) => handlers.current.tip(said),
      clear: () => handlers.current.clear(),
      leftOut: () => handlers.current.leftOut(),
    });
    canvas.current = drawn;
    return () => {
      drawn.destroy();
      canvas.current = null;
    };
  }, []);

  const { entity, version } = entityOf(outline.kind);
  useEffect(() => {
    canvas.current?.render(placed, {
      title: `${entity} ${version}`,
      subtitle: outline.kind.split(":").slice(0, 2).join(":"),
      values: record.on ? record.values : NO_VALUES,
      marks,
      unfilled,
    });

    const path = opened.current;
    if (path !== null) {
      opened.current = null;
      const under = placed.rows.filter((row) => row.row.path === path || row.row.path.startsWith(`${path}.`) || row.row.path.startsWith(`${path}[]`) || row.row.path.startsWith(`${path}|`));
      if (under.length > 0) {
        canvas.current?.revealRows(path, under[under.length - 1].row.path);
      }
    }
  }, [placed, entity, version, outline.kind, record.on, record.values, marks, unfilled]);

  // What is pointed at comes first; failing that what a line of the YAML writes, what was picked, then a filter or a search.
  const filtered = useMemo(() => {
    if (search.trim() !== "") {
      return pathsOfSearch(outline, search).paths;
    }

    return how === null ? null : pathsOfHow(outline, how).paths;
  }, [outline, search, how]);

  const light = useMemo<OutlineLight | null>(() => {
    const part = pointed ?? (linePointed === null ? picked : { type: "row" as const, path: linePointed });
    if (part !== null) {
      // A line of the document can write what is folded: the row folding it stands for it.
      return part.type === "row"
        ? lightOfRows(rows, new Set([rowShowing(rows, part.path)?.path ?? part.path]))
        : part.type === "field" ? lightOfField(rows, part.source, part.field) : lightOfCard(rows, part.source);
    }

    return filtered === null ? null : lightOfRows(rows, filtered);
  }, [rows, pointed, linePointed, picked, filtered]);

  useEffect(() => {
    canvas.current?.setLight(light, picked);
  }, [light, picked, placed]);

  // What a search or a filter finds is brought into view once, when it is asked for: a mapping can be taller than the window.
  const shownFilter = useRef<ReadonlySet<string> | null>(null);
  useEffect(() => {
    if (filtered !== null && filtered !== shownFilter.current) {
      const first = placed.rows.find((row) => filtered.has(row.row.path));
      if (first !== undefined) {
        canvas.current?.reveal(first.row.path);
      }
    }

    shownFilter.current = filtered;
  }, [filtered, placed]);

  useEffect(() => {
    canvas.current?.setInset(picked === null ? 0 : Math.min(INSPECTOR_WIDTH, (frame.current?.clientWidth ?? 0) * 0.85), 0);
    if (picked?.type === "row") {
      canvas.current?.reveal(picked.path);
    }
  }, [picked]);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setPicked(null);
      }
    };

    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const opensFor = useCallback((paths: ReadonlySet<string>) => {
    if (paths.size > 0) {
      setOpen((current) => new Set([...current, ...paths]));
    }
  }, []);

  // A pick from the document or the panel can name what is folded, so what holds it opens with it.
  const pickShown = useCallback((part: OverviewPart) => {
    if (part.type === "row") {
      opensFor(opensTo(outline, part.path));
    }

    setPicked(part);
  }, [outline, opensFor]);

  const onSearch = (next: string) => {
    setSearch(next);
    setHow(null);
    if (next.trim() !== "") {
      opensFor(pathsOfSearch(outline, next).opens);
    }
  };

  const onHow = (next: MappingOutlineHow) => {
    const turnedOn = how !== next;
    setHow(turnedOn ? next : null);
    setSearch("");
    if (turnedOn) {
      opensFor(pathsOfHow(outline, next).opens);
    }
  };

  const found = search.trim() === "" ? null : filtered?.size ?? 0;
  const allOpen = rows.every((row) => !row.folding || row.open);
  const litLines = useMemo(() => {
    const lit = new Set<number>();
    if (light === null) {
      return lit;
    }

    for (const row of rows) {
      const { line, endLine } = row.node;
      if (light.rows.has(row.path) && !row.open && line !== null && line !== undefined && endLine !== null && endLine !== undefined) {
        for (let at = line; at <= endLine; at++) {
          lit.add(at);
        }
      }
    }

    return lit;
  }, [light, rows]);

  const pickedNode = picked?.type === "row" ? nodeAt(outline, picked.path) : null;
  const main = cards.find((card) => card.source.kind === "record");
  const repeats = cards.filter((card) => card.source.kind === "child" && card.repeater !== null)
    .map((card) => ({ name: nodeAt(outline, card.repeater!)?.name ?? card.title, table: card.subtitle === null ? null : card.title }));
  const problems = issues.filter((issue) => issue.target === null);

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-2" data-testid="mapping-overview">
      {problems.map((issue, index) => (
        <p key={`${index}-${issue.message}`} className={cn("flex items-start gap-1.5 text-[13px]", issue.severity === "error" ? "text-destructive" : "text-warning")} data-testid="mapping-overview-issue">
          {issue.severity === "error" ? <OctagonAlert className="mt-0.5 size-3.5 shrink-0" /> : <TriangleAlert className="mt-0.5 size-3.5 shrink-0" />}
          {issue.message}
        </p>
      ))}

      {tools !== null && createPortal(
        <>
          <SearchInput value={search} onChange={onSearch} placeholder="Property or column" label="Find a property or a column" className="w-52" testId="mapping-overview-search" />
          <div className="ml-auto flex items-center gap-1">
            <Button
              size="sm"
              variant={record.on ? "secondary" : "outline"}
              aria-pressed={record.on}
              disabled={flow === null}
              title={flow === null ? record.blocked ?? undefined : "Follow one staged record through the mapping. Nothing is sent."}
              onClick={record.toggle}
              data-testid="mapping-overview-show-record"
            >
              <Rows3 />
              Show a record
            </Button>
            <Button size="sm" variant={showYaml ? "secondary" : "outline"} aria-pressed={showYaml} onClick={() => setShowYaml((current) => !current)} data-testid="mapping-overview-toggle-yaml">
              <FileCode />
              YAML
            </Button>
          </div>
        </>,
        tools,
      )}

      <div className="flex shrink-0 flex-wrap items-center gap-x-6 gap-y-1 text-[13px]">
        {record.on
          ? <RecordLine record={record} session={session} />
          : (
            <p className="flex min-w-0 items-center gap-1" data-testid="mapping-overview-lead">
              <span className="min-w-0 truncate"><Lead outline={outline} table={main?.subtitle === null || main === undefined ? null : main.title} repeats={repeats} /></span>
              <ExplainMark title="How to read this" text={ABOUT} testId="mapping-overview-about" />
            </p>
          )}
        <div className="ml-auto flex shrink-0 flex-wrap items-center gap-x-3 text-[12px] text-muted-foreground" role="group" aria-label="Light the properties filled one way" data-testid="mapping-overview-hows">
          <span data-testid="mapping-overview-properties">
            {found === null
              ? <><span className="font-mono tabular-nums">{formatCount(counts.properties)}</span> properties</>
              : <><span className="font-mono tabular-nums">{formatCount(found)}</span> of <span className="font-mono tabular-nums">{formatCount(counts.properties)}</span> found</>}
          </span>
          {HOWS.filter((item) => counts[item.count] > 0 || how === item.how).map((item) => (
            <button
              key={item.how}
              type="button"
              aria-pressed={how === item.how}
              title={item.meaning}
              onClick={() => onHow(item.how)}
              className={cn(
                "rounded-sm outline-none transition-colors hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring/50",
                how === item.how && "text-foreground underline decoration-primary decoration-2 underline-offset-4",
              )}
              data-testid={`mapping-overview-how-${item.how}`}
            >
              <span className="font-mono tabular-nums">{formatCount(counts[item.count])}</span>
              {` ${item.label}`}
            </button>
          ))}
        </div>
      </div>
      {record.problem !== null && <ProblemView error={record.problem} testId="mapping-overview-record-error" />}

      <Card className="min-h-0 flex-1 gap-0 overflow-hidden rounded-lg p-0">
        {/* The canvas keeps its place in the tree whether or not the YAML is open: D3 owns what is inside its svg. */}
        <ResizablePanelGroup orientation="horizontal" className="min-h-0 flex-1" defaultLayout={showYaml ? layout.defaultLayout : undefined} onLayoutChanged={showYaml ? layout.onLayoutChanged : undefined}>
          <ResizablePanel id="overview-canvas" minSize="40" className="flex min-h-0 min-w-0 flex-col">
            <div ref={frame} className="relative h-full min-h-0 min-w-0 overflow-hidden" data-testid="mapping-overview-canvas">
              <svg ref={svg} className="size-full cursor-grab bg-background active:cursor-grabbing" role="group" aria-label={`How ${mapping.reference} fills a record`} />
              <OverviewInspector
                part={picked}
                outline={outline}
                tables={tables.data ?? null}
                cached={cached}
                yaml={yaml}
                values={record.on ? record.values : NO_VALUES}
                onPick={pickShown}
                onClose={() => setPicked(null)}
              />
              {tip !== null && <CanvasTip tip={tip} />}
              <div className="absolute right-4 bottom-3 flex items-center gap-0.5 rounded-md border border-border bg-card p-0.5 shadow-xs" role="group" aria-label="The view">
                <Button
                  variant="ghost"
                  size="icon-xs"
                  aria-label={allOpen ? "Fold" : "Open all"}
                  title={allOpen ? "Fold" : "Open all"}
                  onClick={() => setOpen(allOpen ? defaultOpen(outline) : everyFolding(outline))}
                  data-testid="mapping-overview-open-all"
                >
                  {allOpen ? <ChevronsDownUp /> : <ChevronsUpDown />}
                </Button>
                <span className="mx-0.5 h-4 w-px bg-border" aria-hidden />
                <Button variant="ghost" size="icon-xs" aria-label="Zoom out" title="Zoom out" onClick={() => canvas.current?.zoomBy(0.8)} data-testid="mapping-overview-zoom-out"><Minus /></Button>
                <Button variant="ghost" size="icon-xs" aria-label="Zoom in" title="Zoom in" onClick={() => canvas.current?.zoomBy(1.25)} data-testid="mapping-overview-zoom-in"><Plus /></Button>
                <Button variant="ghost" size="icon-xs" aria-label="Fit to the window" title="Fit to the window" onClick={() => canvas.current?.fit(true)} data-testid="mapping-overview-fit"><Maximize /></Button>
              </div>
            </div>
          </ResizablePanel>
          {showYaml && (
            <>
              <ResizableHandle withHandle />
              <ResizablePanel id="overview-yaml" defaultSize={420} minSize={280} maxSize="60" className="flex min-h-0 min-w-0 flex-col">
                <OverviewYaml
                  file={mapping.relativePath}
                  yaml={yaml}
                  lit={litLines}
                  focus={pickedNode?.line ?? null}
                  onLine={(line) => setLinePointed(line === null ? null : nodeAtLine(outline, line)?.path ?? null)}
                  onPick={(line) => {
                    const node = nodeAtLine(outline, line);
                    if (node !== null) {
                      pickShown({ type: "row", path: node.path });
                    }
                  }}
                />
              </ResizablePanel>
            </>
          )}
        </ResizablePanelGroup>
      </Card>
    </div>
  );
}

/**
 * What a mapping does, on one canvas: the tables it reads on the left, the record it fills on the right, one line per property
 * between them with what is done to its value on the way, and what it looks values up in on a shelf above. It opens on the
 * sections of the record, each section a click from what it holds, and is read from the document alone until a reader asks to
 * follow a record through it (osdu/docs/reference/concepts/mapping-overview.md).
 */
export function MappingOverview({ detail, session, tools, onProperties }: {
  detail: DeliveryMappingDetail;
  session: ValueCheckSession;
  /** Where in the page's toolbar the overview's own controls go. */
  tools: HTMLElement | null;
  onProperties: () => void;
}) {
  const { mapping, yaml } = detail;
  const outline = useQuery({
    queryKey: ["delivery", "mapping-outline", mapping.repoId, mapping.relativePath, mapping.contentHash],
    queryFn: () => deliveryApi.mappingOutline(yaml, mapping.relativePath, mapping.repoId),
  });

  if (outline.isPending) {
    return <Skeleton className="min-h-80 w-full flex-1" data-testid="mapping-overview-loading" />;
  }

  if (outline.isError) {
    return <ProblemView error={outline.error} testId="mapping-overview-error" />;
  }

  const { outline: read, unfilled, issues } = outline.data;
  if (read === null) {
    return (
      <div className="flex flex-col gap-1" data-testid="mapping-overview-unread">
        {issues.map((issue, index) => (
          <p key={`${index}-${issue.message}`} className="flex items-start gap-1.5 text-[13px] text-destructive">
            <OctagonAlert className="mt-0.5 size-3.5 shrink-0" />
            {issue.message}
          </p>
        ))}
      </div>
    );
  }

  return <OverviewBody key={mapping.contentHash} detail={detail} outline={read} unfilled={unfilled} issues={issues} session={session} tools={tools} onProperties={onProperties} />;
}
