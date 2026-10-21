import { useCallback, useId, useLayoutEffect, useRef, type KeyboardEvent, type ReactNode } from "react";
import { Columns3, CornerDownRight, KeyRound, Layers, Table2, Tag, TriangleAlert } from "lucide-react";
import { RichTooltip } from "@/components/RichTooltip";
import { useClipped } from "@/components/useClipped";
import { cn } from "@/lib/utils";
import type { BlueprintRead, BlueprintRecords, DeliveryDimension, DeliveryDimensionCoverage } from "../../../api/delivery";
import {
  FIXED_COLUMNS, SOURCE_CARD, TABLE_CARD, columnElement, entityName, entityTypeOfKind, readElement, recordsElement, splitPath,
  versionOfKind, type BlueprintElement, type BlueprintLayout, type ExampleValue,
} from "./blueprintModel";

/** What lights up, and what was picked: the parts and lines a pointer or a pick lights, or null when nothing is pointed at. */
export interface BlueprintLight {
  parts: Set<BlueprintElement>;
  edges: Set<string>;
}

interface DiagramProps {
  layout: BlueprintLayout;
  dimension: DeliveryDimension;
  /** The table's name: as the ledger names it once built, else as the first build will make it. */
  table: string;
  coverage: Map<string, DeliveryDimensionCoverage>;
  light: BlueprintLight | null;
  picked: BlueprintElement | null;
  /** What one key read at each part, when the page shows an example; null shows the paths alone. */
  examples: Map<BlueprintElement, ExampleValue> | null;
  onPoint: (element: BlueprintElement | null) => void;
  onPick: (element: BlueprintElement) => void;
}

/** The glyph of a read: what its first use that ends a chain makes, or a step that names the next record. */
function ReadGlyph({ read, className }: { read: BlueprintRead; className: string }) {
  const use = read.uses.find((u) => u.last);
  switch (use?.role) {
    case "key":
      return <KeyRound className={className} aria-hidden />;
    case "label":
      return <Tag className={className} aria-hidden />;
    case "attribute":
      return <Columns3 className={className} aria-hidden />;
    case "collect":
      return <Layers className={className} aria-hidden />;
    default:
      return <CornerDownRight className={className} aria-hidden />;
  }
}

/**
 * A path as a person scans it: the segments before the last muted, the last (the property read) in full weight, and a
 * segment's filter (`[GeoPoliticalEntityTypeID*=...]`) muted beside its name. The text is the path verbatim, so it selects
 * and copies whole.
 */
export function PathText({ path, className }: { path: string; className?: string }) {
  const segments = splitPath(path);
  return (
    <span className={cn("font-mono text-[12px] leading-[1.35rem] [overflow-wrap:anywhere]", className)}>
      {segments.map((segment, index) => {
        const last = index === segments.length - 1;
        const open = segment.indexOf("[");
        const name = open < 0 ? segment : segment.slice(0, open);
        const filter = open < 0 ? "" : segment.slice(open);
        return (
          <span key={`${index}:${segment}`}>
            <span className={last ? "font-semibold text-foreground" : "text-muted-foreground"}>{name}</span>
            {filter !== "" && <span className="text-muted-foreground/80">{breakable(filter)}</span>}
            {!last && <><span className="text-muted-foreground/60">.</span><wbr /></>}
          </span>
        );
      })}
    </span>
  );
}

/** A filter's text with a place to break after each `=` and `:`, so a long one wraps where it reads well. */
function breakable(text: string): ReactNode[] {
  const parts: ReactNode[] = [];
  let run = "";
  for (const c of text) {
    run += c;
    if (c === "=" || c === ":") {
      parts.push(run, <wbr key={parts.length} />);
      run = "";
    }
  }

  parts.push(run);
  return parts;
}

/** The look of a part as the light falls: lit, picked, or faded while something else is lit. */
function lit(light: BlueprintLight | null, picked: BlueprintElement | null, element: BlueprintElement) {
  return {
    on: light !== null && light.parts.has(element),
    off: light !== null && !light.parts.has(element),
    picked: picked === element,
  };
}

/** The pointer and keyboard handlers every part carries: pointing lights it, a click or Enter picks it. */
function useParts(onPoint: (element: BlueprintElement | null) => void, onPick: (element: BlueprintElement) => void) {
  return useCallback((element: BlueprintElement) => ({
    role: "button" as const,
    tabIndex: 0,
    "data-part": element,
    onMouseEnter: () => onPoint(element),
    onMouseLeave: () => onPoint(null),
    onFocus: () => onPoint(element),
    onBlur: () => onPoint(null),
    onClick: (event: { stopPropagation: () => void }) => {
      event.stopPropagation();
      onPick(element);
    },
    onKeyDown: (event: KeyboardEvent) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        onPick(element);
      }
    },
  }), [onPoint, onPick]);
}

/**
 * How a dimension is built, drawn left to right: the dimension's own records, searched for every distinct key; then each
 * step of records found by id, as a build's searches group them (one entity type at one depth, read once for every path
 * any chain needs there); then the table, a column per thing kept. A dashed line runs from a read whose ids name records to
 * those records, a solid one from a read to the column written from it. Pointing at any part lights everything that makes
 * the same column, and the YAML lines that declare it.
 */
export function BlueprintDiagram({ layout, dimension, table, coverage, light, picked, examples, onPoint, onPick }: DiagramProps) {
  const partProps = useParts(onPoint, onPick);
  const surface = useRef<HTMLDivElement | null>(null);
  const anchors = useRef(new Map<BlueprintElement, HTMLElement>());
  const paths = useRef(new Map<string, SVGPathElement>());
  // A marker is named in a url(#...) reference, which takes no colon or guillemet of the ids React makes.
  const markerId = `blueprint-${useId().replace(/[^A-Za-z0-9_-]/g, "")}`;

  const anchor = useCallback((element: BlueprintElement) => (node: HTMLElement | null) => {
    if (node === null) {
      anchors.current.delete(element);
    } else {
      anchors.current.set(element, node);
    }
  }, []);

  const pathRef = useCallback((key: string) => (node: SVGPathElement | null) => {
    if (node === null) {
      paths.current.delete(key);
    } else {
      paths.current.set(key, node);
    }
  }, []);

  // The lines follow the parts wherever layout puts them: measured when the surface or any part changes size (the window,
  // a font arriving, examples shown), and drawn onto the paths directly, so a resize never re-renders the page.
  useLayoutEffect(() => {
    const root = surface.current;
    if (root === null) {
      return undefined;
    }

    let frame = 0;
    const draw = () => {
      frame = 0;
      const box = root.getBoundingClientRect();
      const cards = [...root.querySelectorAll<HTMLElement>("[data-blueprint-card]")].map((card) => relative(card.getBoundingClientRect(), box));
      const taken = new Set<number>();
      for (const edge of layout.edges) {
        const from = anchors.current.get(edge.from);
        const to = anchors.current.get(edge.to);
        const d = from === undefined || to === undefined
          ? ""
          : route(relative(from.getBoundingClientRect(), box), relative(to.getBoundingClientRect(), box), cards, taken);
        for (const layer of ["under", "over"]) {
          paths.current.get(`${layer}:${edge.id}`)?.setAttribute("d", d);
        }
      }
    };

    const schedule = () => {
      if (frame === 0) {
        frame = requestAnimationFrame(draw);
      }
    };

    // An observer reports every element it is given once, as soon as it observes it, which draws the lines the first time.
    const observer = new ResizeObserver(schedule);
    observer.observe(root);
    for (const node of anchors.current.values()) {
      observer.observe(node);
    }

    return () => {
      observer.disconnect();
      if (frame !== 0) {
        cancelAnimationFrame(frame);
      }
    };
  }, [layout, examples]);

  const keys = dimension.keys;
  const collected = layout.blueprint.columns.find((column) => column.role === "attribute" && layout.chains.some((c) => c.role === "collect" && c.attribute === column.attribute));
  const lanes = layout.lanes;

  return (
    <div className="overflow-x-auto" data-testid="blueprint-diagram">
      <div
        ref={surface}
        className="relative grid gap-x-9 gap-y-2.5 p-4"
        style={{ gridTemplateColumns: `repeat(${lanes.length}, minmax(12rem, 22rem))`, minWidth: `${lanes.length * 12.5 + 4}rem`, justifyContent: "space-between" }}
      >
        <svg className="pointer-events-none absolute inset-0 z-0 size-full overflow-visible" aria-hidden>
          <defs>
            <marker id={`${markerId}-dot`} viewBox="0 0 8 8" refX="4" refY="4" markerWidth="5" markerHeight="5">
              <circle cx="4" cy="4" r="3.2" className="fill-muted-foreground/60" />
            </marker>
            <marker id={`${markerId}-lit`} viewBox="0 0 8 8" refX="4" refY="4" markerWidth="5.5" markerHeight="5.5">
              <circle cx="4" cy="4" r="3.2" className="fill-primary" />
            </marker>
          </defs>
          {layout.edges.map((edge) => (
            <path
              key={edge.id}
              ref={pathRef(`under:${edge.id}`)}
              fill="none"
              strokeWidth={1.25}
              strokeDasharray={edge.kind === "follow" ? "5 4" : undefined}
              markerEnd={`url(#${markerId}-dot)`}
              className={cn("stroke-muted-foreground/45 transition-opacity", light !== null && "opacity-25")}
              data-edge={edge.id}
            />
          ))}
        </svg>

        {lanes.map((lane, index) => (
          <LaneHeading key={lane.id} step={index + 1}>
            {index === 0
              ? <><span className="font-medium text-foreground">Searched</span><span>for every distinct key</span></>
              : index === lanes.length - 1
                ? <><span className="font-medium text-foreground">Written</span><span>{collected === undefined ? "a row per key" : `a row per key and ${collected.name}`}</span></>
                : <><span className="font-medium text-foreground">Found by id</span><span>{lane.depth === 1 ? "the record each key names" : "the records those name"}</span></>}
          </LaneHeading>
        ))}

        {lanes.map((lane, index) => (
          <div key={lane.id} className="relative z-10 flex min-w-0 flex-col gap-5" style={{ gridColumn: index + 1, gridRow: 2 }}>
            {index === 0 && (
              <SourceCard layout={layout} light={light} picked={picked} examples={examples} partProps={partProps} anchor={anchor} />
            )}
            {lane.records.map((node) => (
              <RecordsCard key={node.id} layout={layout} node={node} light={light} picked={picked} examples={examples} partProps={partProps} anchor={anchor} />
            ))}
            {index === lanes.length - 1 && (
              <TableCard
                layout={layout} dimension={dimension} table={table} keys={keys} coverage={coverage} light={light} picked={picked} examples={examples}
                partProps={partProps} anchor={anchor}
              />
            )}
          </div>
        ))}

        <svg className="pointer-events-none absolute inset-0 z-20 size-full overflow-visible" aria-hidden>
          {layout.edges.map((edge) => (
            <path
              key={edge.id}
              ref={pathRef(`over:${edge.id}`)}
              fill="none"
              strokeWidth={1.75}
              strokeDasharray={edge.kind === "follow" ? "5 4" : undefined}
              markerEnd={`url(#${markerId}-lit)`}
              className={cn("stroke-primary transition-opacity", light?.edges.has(edge.id) === true ? "opacity-100" : "opacity-0")}
            />
          ))}
        </svg>
      </div>
    </div>
  );
}

/** A box in the surface's coordinates. */
interface Box {
  left: number;
  right: number;
  top: number;
  bottom: number;
  height: number;
}

function relative(rect: DOMRect, box: DOMRect): Box {
  return { left: rect.left - box.left, right: rect.right - box.left, top: rect.top - box.top, bottom: rect.bottom - box.top, height: rect.height };
}

/** How far a line keeps from a card it passes. */
const CLEARANCE = 4;

/**
 * A line from the right of one part to the left of another, leaving and arriving level, in the surface's coordinates. A
 * line that passes the cards of a lane between them runs through the free space between those cards (above them, between
 * two, or below them), the place nearest its ends, so it never seems to enter a card it does not reach; lines that would
 * share a channel are set a few pixels apart.
 */
function route(from: Box, to: Box, cards: Box[], taken: Set<number>): string {
  const x1 = from.right;
  const y1 = from.top + Math.min(from.height / 2, 14);
  const x2 = to.left - 3;
  const y2 = to.top + Math.min(to.height / 2, 14);
  const between = cards.filter((card) => card.left > x1 + 1 && card.right < x2 - 1);
  if (between.length === 0) {
    return bezier(x1, y1, x2, y2);
  }

  const free = (y: number) => between.every((card) => y < card.top - CLEARANCE || y > card.bottom + CLEARANCE);
  const candidates = [y1, y2, ...between.flatMap((card) => [card.top - CLEARANCE - 2, card.bottom + CLEARANCE + 2])];
  let channel: number | null = null;
  let cost = Number.POSITIVE_INFINITY;
  for (const candidate of candidates) {
    for (const shift of [0, 3, -3, 6, -6]) {
      const y = candidate + shift;
      if (y < 0 || !free(y) || taken.has(Math.round(y))) {
        continue;
      }

      const here = Math.abs(y - y1) + Math.abs(y - y2);
      if (here < cost) {
        channel = y;
        cost = here;
      }

      break;
    }
  }

  if (channel === null) {
    return bezier(x1, y1, x2, y2);
  }

  taken.add(Math.round(channel));
  const enter = Math.min(...between.map((card) => card.left)) - 12;
  const leave = Math.max(...between.map((card) => card.right)) + 12;
  const reachIn = Math.max(10, (enter - x1) * 0.5);
  const reachOut = Math.max(10, (x2 - leave) * 0.5);
  return `M ${n(x1)} ${n(y1)} C ${n(x1 + reachIn)} ${n(y1)}, ${n(enter - reachIn)} ${n(channel)}, ${n(enter)} ${n(channel)} `
    + `L ${n(leave)} ${n(channel)} C ${n(leave + reachOut)} ${n(channel)}, ${n(x2 - reachOut)} ${n(y2)}, ${n(x2)} ${n(y2)}`;
}

function bezier(x1: number, y1: number, x2: number, y2: number): string {
  const reach = Math.max(24, Math.abs(x2 - x1) * 0.45);
  return `M ${n(x1)} ${n(y1)} C ${n(x1 + reach)} ${n(y1)}, ${n(x2 - reach)} ${n(y2)}, ${n(x2)} ${n(y2)}`;
}

function n(value: number): string {
  return value.toFixed(1);
}

function LaneHeading({ step, children }: { step: number; children: ReactNode }) {
  return (
    <div className="flex min-w-0 items-center gap-2 text-[12px] text-muted-foreground" style={{ gridRow: 1 }} data-testid="blueprint-lane">
      <span className="flex size-5 shrink-0 items-center justify-center rounded-full border border-border bg-muted font-mono text-[11px] font-medium text-foreground">
        {step}
      </span>
      <span className="flex min-w-0 flex-col leading-tight">{children}</span>
    </div>
  );
}

type PartProps = ReturnType<ReturnType<typeof useParts>>;

interface CardProps {
  layout: BlueprintLayout;
  light: BlueprintLight | null;
  picked: BlueprintElement | null;
  examples: Map<BlueprintElement, ExampleValue> | null;
  partProps: (element: BlueprintElement) => PartProps;
  anchor: (element: BlueprintElement) => (node: HTMLElement | null) => void;
}

/** A card of the diagram: a heading that is a part of its own, then its rows. Records with no template are drawn dashed. */
function Card({ element, heading, sub, dashed = false, children, light, picked, partProps, anchor, testId }: {
  element: BlueprintElement;
  heading: ReactNode;
  sub?: ReactNode;
  dashed?: boolean;
  children: ReactNode;
  light: BlueprintLight | null;
  picked: BlueprintElement | null;
  partProps: (element: BlueprintElement) => PartProps;
  anchor: (element: BlueprintElement) => (node: HTMLElement | null) => void;
  testId: string;
}) {
  const state = lit(light, picked, element);
  return (
    <section
      data-blueprint-card
      className={cn(
        "min-w-0 overflow-hidden rounded-lg border bg-card shadow-xs transition-[opacity,border-color]",
        dashed ? "border-dashed border-muted-foreground/50" : "border-border",
        state.on && "border-primary/60",
        state.picked && "ring-2 ring-primary/60",
        state.off && "opacity-55",
      )}
      data-testid={testId}
    >
      <header
        ref={anchor(element)}
        {...partProps(element)}
        className={cn(
          "flex min-w-0 cursor-pointer flex-col gap-0.5 border-b border-border bg-muted/40 px-3 py-2 outline-none hover:bg-accent/60 focus-visible:bg-accent/60",
          state.on && "bg-primary/8",
        )}
      >
        {heading}
        {sub !== undefined && <div className="min-w-0 text-[11.5px] text-muted-foreground">{sub}</div>}
      </header>
      <ul className="divide-y divide-border/60">{children}</ul>
    </section>
  );
}

/** A card's heading: the entity's name, its group muted, and the template that describes it or why none does. */
function Heading({ name, group, version, templateKind, missing }: { name: string; group: string; version: string | null; templateKind: string | null; missing: string | null }) {
  return (
    <div className="flex min-w-0 items-center gap-1.5">
      <span className="min-w-0 shrink-0 truncate text-[13px] font-semibold" style={{ maxWidth: "70%" }}>{name}</span>
      {group !== "" && <span className="min-w-0 truncate text-[11px] text-muted-foreground">{group}</span>}
      <span className="ml-auto flex shrink-0 items-center gap-1">
        {version !== null && templateKind !== null && (
          <RichTooltip title="Template" body={`Described by the template ${templateKind}.`} mono>
            <span className="rounded-sm border border-border bg-background px-1 font-mono text-[11px] text-muted-foreground">{version}</span>
          </RichTooltip>
        )}
        {missing !== null && (
          <RichTooltip title="Not described" body={missing}>
            <TriangleAlert className="size-3.5 text-warning" aria-label="No template describes these records" />
          </RichTooltip>
        )}
      </span>
    </div>
  );
}

/** The dimension's own records: the kind they are of, how many the last build read, the query that narrows them, and the reads. */
function SourceCard({ layout, ...props }: CardProps) {
  const source = layout.blueprint.source;
  const { group, name } = entityName(entityTypeOfKind(source.kind));
  const counted = source.kinds.filter((kind) => kind.records !== null);
  const records = counted.length === 0 ? null : counted.reduce((sum, kind) => sum + (kind.records ?? 0), 0);
  const version = source.template === null ? versionOfKind(source.kind) : versionOfKind(source.template.kind);
  return (
    <Card
      element={SOURCE_CARD}
      heading={<Heading name={name} group={group} version={version} templateKind={source.template?.kind ?? null} missing={source.missing} />}
      sub={(
        <span className="flex min-w-0 flex-col">
          <span>
            {records === null
              ? "Every record of the kind"
              : <><span className="font-mono tabular-nums text-foreground">{records.toLocaleString("en-US")}</span> records{source.kinds.length > 1 ? ` in ${source.kinds.length} kinds` : ""}</>}
          </span>
          {source.query !== null && (
            <RichTooltip title="Query" body={source.query} mono>
              <span className="truncate font-mono">where {source.query}</span>
            </RichTooltip>
          )}
        </span>
      )}
      dashed={source.template === null}
      testId="blueprint-source"
      {...props}
    >
      {source.reads.map((read) => <ReadRow key={read.id} layout={layout} read={read} {...props} />)}
    </Card>
  );
}

/** Records found by id: the entity type the ids name, the template describing it, and each path read from them. */
function RecordsCard({ layout, node, ...props }: CardProps & { node: BlueprintRecords }) {
  const named = node.entityTypes.map((type) => entityName(type));
  const heading = named.length === 0
    ? <Heading name="Records the ids name" group="" version={null} templateKind={null} missing={node.missing} />
    : (
      <Heading
        name={named.map((n) => n.name).join(" or ")}
        group={named[0].group}
        version={node.template === null ? null : versionOfKind(node.template.kind)}
        templateKind={node.template?.kind ?? null}
        missing={node.missing}
      />
    );
  return (
    <Card element={recordsElement(node.id)} heading={heading} dashed={node.template === null} testId="blueprint-records" {...props}>
      {node.reads.map((read) => <ReadRow key={read.id} layout={layout} read={read} {...props} />)}
    </Card>
  );
}

/** What a read makes: the column a chain ending at it writes, or the records it names for a chain that goes on. */
function madeBy(layout: BlueprintLayout, read: BlueprintRead): string {
  const ends = read.uses.filter((use) => use.last);
  if (ends.length > 0) {
    return ends.map((use) => {
      if (use.role === "key") {
        return layout.keyColumn;
      }

      return use.role === "label" ? layout.valueColumn : use.attribute ?? "";
    }).join(", ");
  }

  const next = read.leadsTo === null ? undefined : layout.records.get(read.leadsTo);
  const names = next?.entityTypes.map((type) => entityName(type).name) ?? [];
  return names.length === 0 ? "the next record" : names.join(" or ");
}

/** One path read: its glyph, the path, what it makes, a mark when something keeps it from being read as meant, and an example's value. */
function ReadRow({ layout, read, light, picked, examples, partProps, anchor }: CardProps & { read: BlueprintRead }) {
  const element = readElement(read.id);
  const state = lit(light, picked, element);
  const made = madeBy(layout, read);
  const goesOn = read.uses.every((use) => !use.last);
  const example = examples?.get(element);
  return (
    <li
      ref={anchor(element)}
      {...partProps(element)}
      className={cn(
        "flex min-w-0 cursor-pointer flex-col gap-0.5 px-3 py-1.5 outline-none transition-colors hover:bg-accent/60 focus-visible:bg-accent/60",
        state.on && "bg-primary/8",
        state.picked && "bg-primary/12",
      )}
      data-testid="blueprint-read"
    >
      <div className="flex min-w-0 items-start gap-1.5">
        <ReadGlyph read={read} className={cn("mt-1 size-3.5 shrink-0", state.on ? "text-primary" : "text-muted-foreground")} />
        <PathText path={read.path} className="min-w-0 flex-1" />
        {read.problem !== null && (
          <RichTooltip title="Read otherwise than meant" body={read.problem}>
            <TriangleAlert className="mt-1 size-3.5 shrink-0 text-warning" aria-label="Read otherwise than meant" data-testid="blueprint-read-problem" />
          </RichTooltip>
        )}
      </div>
      <div className="flex min-w-0 items-baseline gap-2 pl-5">
        <span className="shrink-0 truncate rounded-sm border border-border bg-muted/60 px-1 font-mono text-[11px] text-muted-foreground" style={{ maxWidth: "60%" }} data-testid="blueprint-read-makes">
          {goesOn ? `→ ${made}` : made}
        </span>
        {example !== undefined && <ExampleText value={example} />}
      </div>
    </li>
  );
}

/** One key's value at a part: the text in full on hover, an id with its end in view, and how many more a key holds there. */
function ExampleText({ value }: { value: ExampleValue }) {
  const [textRef, clipped] = useClipped(value.text);
  const line = (
    <span className="flex min-w-0 items-baseline gap-1 text-[11.5px]" data-testid="blueprint-example">
      <span className="text-muted-foreground/70">=</span>
      <span ref={textRef} className={cn("min-w-0 truncate font-mono", value.reference ? "text-muted-foreground [direction:rtl]" : "text-foreground")}>
        {value.reference ? <bdi>{value.text}</bdi> : value.text}
      </span>
      {value.more > 0 && <span className="shrink-0 text-muted-foreground">+{value.more}</span>}
    </span>
  );

  // The whole value on hover only where some of it is hidden: cut short, or more values than the first.
  return clipped || value.more > 0
    ? <RichTooltip title={value.reference ? "Record id" : value.more > 0 ? "Values" : "Value"} body={value.all.join("\n")} mono>{line}</RichTooltip>
    : line;
}

/** The table a build writes: its name, the columns a read writes (key, value, attributes) with how much of each was read, then the fixed ones. */
function TableCard({ layout, dimension, table, keys, coverage, ...props }: CardProps & {
  dimension: DeliveryDimension;
  table: string;
  keys: number;
  coverage: Map<string, DeliveryDimensionCoverage>;
}) {
  const columns = layout.blueprint.columns;
  const made = columns.filter((column) => !FIXED_COLUMNS.has(column.role));
  const fixed = columns.filter((column) => FIXED_COLUMNS.has(column.role));
  const collected = new Set(layout.chains.filter((c) => c.role === "collect").map((c) => c.attribute));
  return (
    <Card
      element={TABLE_CARD}
      heading={(
        <div className="flex min-w-0 items-center gap-1.5">
          <Table2 className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
          <span className="truncate font-mono text-[12.5px] font-semibold">{table}</span>
        </div>
      )}
      sub={dimension.table === null ? "Made by the first build." : `${columns.length} columns`}
      testId="blueprint-table"
      {...props}
    >
      {made.map((column) => {
        const element = columnElement(column.name);
        const state = lit(props.light, props.picked, element);
        const read = column.role === "attribute" ? coverage.get(column.name) : undefined;
        const example = props.examples?.get(element);
        const role = column.role === "attribute" ? (collected.has(column.attribute) ? "collected" : "attribute") : column.role;
        return (
          <li
            key={column.name}
            ref={props.anchor(element)}
            {...props.partProps(element)}
            className={cn(
              "flex min-w-0 cursor-pointer flex-col gap-0.5 px-3 py-1.5 outline-none transition-colors hover:bg-accent/60 focus-visible:bg-accent/60",
              state.on && "bg-primary/8",
              state.picked && "bg-primary/12",
            )}
            data-testid="blueprint-column"
          >
            <div className="flex min-w-0 items-center gap-1.5">
              <span className="min-w-0 truncate font-mono text-[12px] font-semibold">{column.name}</span>
              <span className="shrink-0 text-[11px] text-muted-foreground">{role}</span>
              {column.role === "attribute" && keys > 0 && (
                <Coverage read={read} keys={keys} />
              )}
            </div>
            {column.role === "value" && (dimension.clean.length > 0 || dimension.unlabelled !== null) && (
              <div className="min-w-0 truncate text-[11px] text-muted-foreground">
                {dimension.clean.length > 0 && <span className="font-mono">{dimension.clean.join(" → ")}</span>}
                {dimension.clean.length > 0 && dimension.unlabelled !== null && <span> · </span>}
                {dimension.unlabelled !== null && <span>else <span className="text-foreground">{dimension.unlabelled}</span></span>}
              </div>
            )}
            {example !== undefined && <ExampleText value={example} />}
          </li>
        );
      })}
      <li className="flex min-w-0 flex-wrap items-center gap-1 px-3 py-1.5">
        {fixed.map((column) => {
          const element = columnElement(column.name);
          const state = lit(props.light, props.picked, element);
          return (
            <span
              key={column.name}
              {...props.partProps(element)}
              className={cn(
                "cursor-pointer rounded-sm border border-border px-1 font-mono text-[11px] text-muted-foreground outline-none hover:bg-accent/60 focus-visible:bg-accent/60",
                state.on && "border-primary/50 text-foreground",
                state.picked && "bg-primary/12",
              )}
              data-testid="blueprint-fixed-column"
            >
              {column.name}
            </span>
          );
        })}
      </li>
    </Card>
  );
}

/** How many of the keys hold a value read for an attribute: the share, with the counts on hover; none reads quiet. */
function Coverage({ read, keys }: { read: DeliveryDimensionCoverage | undefined; keys: number }) {
  const held = read?.keys ?? 0;
  const share = Math.min(1, held / keys);
  const shown = held === 0 ? "none" : share >= 0.995 ? "all" : `${Math.max(1, Math.round(share * 100))}%`;
  const body = held === 0
    ? `No key holds a value read for it: the keys hold the dimension's value for what is not read, or none.`
    : `${held.toLocaleString("en-US")} of ${keys.toLocaleString("en-US")} keys hold a value read for it, ${read!.values.toLocaleString("en-US")} ${read!.values === 1 ? "value" : "values"} in all.`;
  return (
    <RichTooltip title="Read for" body={body}>
      <span className={cn("ml-auto shrink-0 font-mono text-[11px] tabular-nums", held === 0 ? "text-muted-foreground/70" : "text-muted-foreground")} data-testid="blueprint-coverage">
        {shown}
      </span>
    </RichTooltip>
  );
}
