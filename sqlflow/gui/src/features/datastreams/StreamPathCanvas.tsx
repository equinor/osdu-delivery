import { useEffect, useMemo, useRef, type ReactNode } from "react";
import { CircleCheck, CircleHelp } from "lucide-react";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import type { DataStream, StreamPath, StreamPathFire, StreamPathNode, StreamPathRun } from "../../api/types";
import { formatDurationSeconds } from "../../lib/time";
import { flowKindIdentity, flowKindToneClasses } from "../../modules/flowKinds";
import { kindContribution } from "../../modules/registry";
import { bandedLayout } from "../lineage/graphBands";
import { kindLook } from "../lineage/graphKinds";
import { NODE_HEIGHT, NODE_WIDTH, type Position } from "../lineage/graphLayout";
import { statusIcons, statusInk } from "./StreamStatusBadge";
import { finding, statusLabels } from "./streamPresentation";
import {
  edgeOutcome, filesFigures, landedFigures, nodeLabel, omissionText, readOutcome, runFigures, runOutcome, runsByFlow,
  type RunOutcome,
} from "./streamPathModel";

/** The room a band's name takes beside its cards, and the margin a band leaves around them. */
const LANE_LABEL_ROOM = 92;
const LANE_MARGIN = 16;
const CANVAS_MARGIN = 12;

/**
 * How each hand-over is inked in the fire shown. The data's own line is the one series in slot 1 (DESIGN.md 3.4); a
 * hand-over that carried nothing is a dashed line in recessive ink; one whose run failed wears the reserved destructive
 * colour; one whose flow had no run in the fire is barely there. So where the data stopped is the place the blue line
 * ends, and a failure is the one red line on the page.
 */
const EDGE_INK: Record<RunOutcome | "none", { stroke: string; marker: string; width: number; dash?: string }> = {
  moved: { stroke: "stroke-chart-1", marker: "fill-chart-1", width: 2 },
  empty: { stroke: "stroke-muted-foreground/70", marker: "fill-muted-foreground/70", width: 1.5, dash: "5 4" },
  failed: { stroke: "stroke-destructive", marker: "fill-destructive", width: 2 },
  running: { stroke: "stroke-chart-1/60", marker: "fill-chart-1/60", width: 2, dash: "7 4" },
  queued: { stroke: "stroke-chart-1/40", marker: "fill-chart-1/40", width: 1.5, dash: "2 4" },
  cancelled: { stroke: "stroke-muted-foreground/40", marker: "fill-muted-foreground/40", width: 1.5, dash: "2 4" },
  absent: { stroke: "stroke-border", marker: "fill-border", width: 1.5, dash: "2 4" },
  none: { stroke: "stroke-muted-foreground/50", marker: "fill-muted-foreground/50", width: 1.5 },
};

/** The mark beside a run's figures on a card: a filled dot for data moved, a ring for a run that moved none, the
 * reserved colour for a failure. The words beside it always say the same, so nothing rests on the mark alone. */
function OutcomeMark({ outcome }: { outcome: RunOutcome }) {
  return (
    <span
      aria-hidden
      className={cn(
        "inline-block size-2 shrink-0 rounded-full",
        outcome === "moved" && "bg-chart-1",
        outcome === "empty" && "ring-1 ring-muted-foreground/70 ring-inset",
        outcome === "failed" && "bg-destructive",
        outcome === "running" && "animate-pulse bg-chart-1",
        outcome === "queued" && "ring-1 ring-chart-1 ring-inset",
        outcome === "cancelled" && "bg-muted-foreground/40",
        outcome === "absent" && "bg-border",
      )}
    />
  );
}

/** The first line of a run's error: a card has room for one, and the panel shows it whole. */
function firstLine(text: string): string {
  const line = text.split(/\r?\n/, 1)[0] ?? text;
  return line.length > 120 ? `${line.slice(0, 117)}...` : line;
}

/** What a flow's run did in the fire, in the card's second line. */
function FlowFigures({ entry, windowDays, noFires }: { entry: StreamPathRun | undefined; windowDays: number; noFires: boolean }) {
  if (noFires) {
    return <span className="truncate text-muted-foreground">No fire in the last {windowDays} days</span>;
  }

  const outcome = runOutcome(entry?.run);
  if (entry === undefined) {
    return (
      <>
        <OutcomeMark outcome={outcome} />
        <span className="truncate text-muted-foreground">Not in this fire</span>
      </>
    );
  }

  const run = entry.run;
  const counts = kindContribution(run.flowKind)?.runCountColumns;
  let figures: ReactNode;
  switch (outcome) {
    case "failed":
      figures = <span className="truncate text-destructive">{run.error === null ? "Failed" : `Failed: ${firstLine(run.error)}`}</span>;
      break;
    case "running":
      figures = <span className="truncate">Running</span>;
      break;
    case "queued":
      figures = <span className="truncate text-muted-foreground">Queued</span>;
      break;
    case "cancelled":
      figures = <span className="truncate text-muted-foreground">Cancelled</span>;
      break;
    default:
      // A kind that counts in its result shows its own counts, as a list of its runs does; SQLFlow's kinds say what
      // they took in and what they did to their table.
      figures = counts !== undefined && counts.length > 0
        ? (
          <span className="flex min-w-0 items-baseline gap-2 overflow-hidden whitespace-nowrap">
            {counts.map((column) => (
              <span key={column.id} className="inline-flex items-baseline gap-1">
                <span className="text-[10px] text-muted-foreground">{column.header}</span>
                {column.render(run)}
              </span>
            ))}
          </span>
        )
        : <span className={cn("truncate", outcome === "empty" && "text-muted-foreground")}>{runFigures(run)}</span>;
  }

  return (
    <>
      <OutcomeMark outcome={outcome} />
      {figures}
      {run.durationSeconds !== null && (
        <span className="ml-auto shrink-0 pl-1 font-mono text-[10px] tabular-nums text-muted-foreground">
          {formatDurationSeconds(run.durationSeconds)}
        </span>
      )}
    </>
  );
}

/** The verdict over the window, as one glyph at the card's corner: loud when something is wrong, quiet when healthy. */
function VerdictMark({ stream, windowDays }: { stream: DataStream | undefined; windowDays: number }) {
  const Icon = stream === undefined ? CircleHelp : stream.status === "healthy" ? CircleCheck : statusIcons[stream.status];
  const quiet = stream === undefined || stream.status === "healthy" || stream.status === "insufficient-history";
  const words = stream === undefined
    ? `No runs in the last ${windowDays} days, so there is nothing to judge.`
    : `${statusLabels[stream.status]} over ${windowDays} days: ${finding(stream)}`;
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span
          className={cn("ml-auto inline-flex shrink-0", quiet ? "text-muted-foreground/60" : statusInk[stream.status])}
          aria-label={words}
          data-testid="stream-path-verdict"
          data-status={stream?.status ?? "none"}
        >
          <Icon className="size-3.5" aria-hidden />
        </span>
      </TooltipTrigger>
      <TooltipContent className="max-w-[320px]">{words}</TooltipContent>
    </Tooltip>
  );
}

/** What a node stands for without drawing it, as a small count on its corner, named in full on hover. */
function OmittedMark({ node }: { node: StreamPathNode }) {
  if (node.omitted.length === 0) {
    return null;
  }

  const total = node.omitted.reduce((sum, omission) => sum + omission.count, 0);
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span
          className="absolute -top-2 right-2 rounded-sm border border-border bg-card px-1 font-mono text-[10px] leading-4 tabular-nums text-muted-foreground"
          data-testid="stream-path-omitted"
        >
          +{total}
        </span>
      </TooltipTrigger>
      <TooltipContent className="max-w-[360px]">
        <div className="flex flex-col gap-1">
          {node.omitted.map((omission) => (
            <div key={omission.what}>
              <div className="font-medium">{omissionText(omission.what, omission.count)}</div>
              <div className="break-all font-mono text-[11px] opacity-80">
                {omission.names.slice(0, 6).join(", ")}
                {omission.count > 6 && `, and ${omission.count - 6} more`}
              </div>
            </div>
          ))}
          <div className="opacity-70">Open the node for the whole list.</div>
        </div>
      </TooltipContent>
    </Tooltip>
  );
}

/** The run that took in what an object holds (its reader on the path), for where data enters. */
function readerRun(node: StreamPathNode, path: StreamPath, runs: ReadonlyMap<string, StreamPathRun>, byId: ReadonlyMap<string, StreamPathNode>): StreamPathRun | undefined {
  for (const edge of path.edges) {
    const reader = edge.from === node.id ? byId.get(edge.to) : undefined;
    if (reader !== undefined && reader.pipelineId !== null && runs.has(reader.pipelineId)) {
      return runs.get(reader.pipelineId);
    }
  }
  return undefined;
}

/** The run that wrote an object (its producer on the path) in the fire. */
function writerOf(node: StreamPathNode, path: StreamPath, byId: ReadonlyMap<string, StreamPathNode>): StreamPathNode | undefined {
  for (const edge of path.edges) {
    const writer = edge.to === node.id ? byId.get(edge.from) : undefined;
    if (writer?.type === "flow") {
      return writer;
    }
  }
  return undefined;
}

/** What landed in an object in the fire, in its card's second line. */
function ObjectFigures({ node, path, runs, byId, noFires }: {
  node: StreamPathNode;
  path: StreamPath;
  runs: ReadonlyMap<string, StreamPathRun>;
  byId: ReadonlyMap<string, StreamPathNode>;
  noFires: boolean;
}) {
  if (noFires) {
    return <span className="truncate text-muted-foreground">{node.qualifiedName ?? node.kind}</span>;
  }

  if (node.external) {
    const reader = readerRun(node, path, runs, byId);
    const outcome = readOutcome(reader);
    return (
      <>
        <OutcomeMark outcome={outcome} />
        <span className={cn("truncate", outcome !== "moved" && "text-muted-foreground")}>
          {reader === undefined
            ? "Not read in this fire"
            : reader.files !== null
              ? filesFigures(reader.files)
              : reader.run.rowsLoaded !== null
                ? `${runFigures(reader.run).split(" · ")[0]} read`
                : "Read, nothing counted"}
        </span>
      </>
    );
  }

  const writer = writerOf(node, path, byId);
  const entry = writer === undefined || writer.pipelineId === null ? undefined : runs.get(writer.pipelineId);
  if (entry === undefined) {
    return (
      <>
        <OutcomeMark outcome="absent" />
        <span className="truncate text-muted-foreground">{writer === undefined ? node.kind : "Not written in this fire"}</span>
      </>
    );
  }

  const outcome = runOutcome(entry.run);
  const landed = landedFigures(entry.run);
  return (
    <>
      <OutcomeMark outcome={outcome} />
      <span className={cn("truncate", outcome !== "moved" && "text-muted-foreground")}>
        {outcome === "failed" ? "Its writer failed" : landed ?? (outcome === "moved" ? "Written" : "Nothing written")}
      </span>
    </>
  );
}

function edgePath(from: Position, to: Position): string {
  const sx = from.x + NODE_WIDTH / 2;
  const sy = from.y + NODE_HEIGHT;
  const tx = to.x + NODE_WIDTH / 2;
  const ty = to.y;
  if (ty > sy) {
    const bend = (ty - sy) / 2;
    return `M ${sx} ${sy} C ${sx} ${sy + bend}, ${tx} ${ty - bend}, ${tx} ${ty - 3}`;
  }

  // A hand-over that runs back up the page (a flow reading back what a later one writes) leaves by the side and comes
  // round, so it never crosses the cards between.
  const side = Math.max(from.x, to.x) + NODE_WIDTH;
  const fy = from.y + NODE_HEIGHT / 2;
  const toY = to.y + NODE_HEIGHT / 2;
  return `M ${from.x + NODE_WIDTH} ${fy} C ${side + 56} ${fy}, ${side + 56} ${toY}, ${to.x + NODE_WIDTH + 3} ${toY}`;
}

interface StreamPathCanvasProps {
  path: StreamPath;
  /** The fire whose runs the cards and lines show; null when the window holds none. */
  fire: StreamPathFire | null;
  windowDays: number;
  selectedId: string | null;
  onSelect: (id: string | null) => void;
  /** The width the side panel covers on the right, kept clear so no card hides under it. */
  inset: number;
}

/**
 * A stream's path drawn top to bottom in the order the data moves: where it enters, every flow it goes through in the
 * band of the wave that runs it, what each flow writes under it, and where it ends. It is laid out by the lineage
 * graph's own banded layout, so a chain reads here exactly as it does there, and every line runs down the page.
 *
 * Each card says what happened in one fire: a flow's card what its run took in and did, an object's card what landed
 * in it, and where the data enters, the files that arrived. The lines between are inked by whether data moved through
 * them in that fire, so the question "how far did this delivery get" is answered by where the blue line stops.
 */
export function StreamPathCanvas({ path, fire, windowDays, selectedId, onSelect, inset }: StreamPathCanvasProps) {
  const scroller = useRef<HTMLDivElement>(null);
  const byId = useMemo(() => new Map(path.nodes.map((node) => [node.id, node])), [path.nodes]);
  const streams = useMemo(() => new Map(path.streams.map((stream) => [stream.pipelineId, stream])), [path.streams]);
  const runs = useMemo(() => runsByFlow(fire), [fire]);
  const noFires = path.fires.length === 0;

  const layout = useMemo(() => {
    const banded = bandedLayout(
      path.nodes.map((node) => ({ id: node.id, flow: node.type === "flow", waves: node.wave === null ? [] : [node.wave] })),
      path.edges.map((edge) => ({ source: edge.from, target: edge.to })),
      "down",
    );
    const placed = [...banded.positions.values()];
    const left = Math.min(...placed.map((p) => p.x), banded.crossStart) - LANE_LABEL_ROOM - LANE_MARGIN - CANVAS_MARGIN;
    const top = Math.min(...placed.map((p) => p.y), ...banded.lanes.map((lane) => lane.start)) - LANE_MARGIN - CANVAS_MARGIN;
    const right = Math.max(...placed.map((p) => p.x + NODE_WIDTH), banded.crossEnd) + LANE_MARGIN + CANVAS_MARGIN + 64;
    const bottom = Math.max(...placed.map((p) => p.y + NODE_HEIGHT), ...banded.lanes.map((lane) => lane.end))
      + LANE_MARGIN + CANVAS_MARGIN;
    const positions = new Map<string, Position>();
    for (const [id, position] of banded.positions) {
      positions.set(id, { x: position.x - left, y: position.y - top });
    }
    return {
      positions,
      lanes: banded.lanes.map((lane) => ({ ...lane, start: lane.start - top, end: lane.end - top })),
      reach: { start: banded.crossStart - left, end: banded.crossEnd - left },
      width: right - left,
      height: bottom - top,
    };
  }, [path.nodes, path.edges]);

  // The picked card is brought into view once the panel has taken its room, so it never sits under the panel.
  useEffect(() => {
    if (selectedId === null) {
      return;
    }
    const frame = requestAnimationFrame(() => requestAnimationFrame(() => {
      scroller.current
        ?.querySelector(`[data-node-id="${CSS.escape(selectedId)}"]`)
        ?.scrollIntoView({ block: "nearest", inline: "nearest", behavior: "smooth" });
    }));
    return () => cancelAnimationFrame(frame);
  }, [selectedId, inset]);

  return (
    <div
      ref={scroller}
      className="h-full overflow-auto"
      style={{ paddingRight: inset }}
      onClick={(event) => {
        if (!(event.target as HTMLElement).closest("[data-node-id]")) {
          onSelect(null);
        }
      }}
      data-testid="stream-path-canvas"
    >
      <div className="relative mx-auto" style={{ width: layout.width, height: layout.height }}>
        {layout.lanes.map((lane, index) => (
          <div
            key={lane.rank}
            aria-hidden
            className={cn(
              "pointer-events-none absolute rounded-md border border-dashed border-border/60",
              index % 2 === 0 ? "bg-muted/25" : "bg-transparent",
            )}
            style={{
              left: layout.reach.start - LANE_LABEL_ROOM - LANE_MARGIN,
              top: lane.start - LANE_MARGIN,
              width: layout.reach.end - layout.reach.start + LANE_LABEL_ROOM + LANE_MARGIN * 2,
              height: lane.end - lane.start + LANE_MARGIN * 2,
            }}
          >
            {lane.wave !== null && (
              <span className="absolute top-1/2 left-3 -translate-y-1/2 text-[15px] font-semibold tracking-wide whitespace-nowrap text-muted-foreground/80">
                Wave {lane.wave}
              </span>
            )}
          </div>
        ))}

        <svg className="pointer-events-none absolute inset-0" width={layout.width} height={layout.height} aria-hidden>
          <defs>
            {Object.entries(EDGE_INK).map(([name, ink]) => (
              <marker
                key={name}
                id={`stream-path-arrow-${name}`}
                viewBox="0 0 8 8"
                refX="6"
                refY="4"
                markerWidth="7"
                markerHeight="7"
                orient="auto-start-reverse"
              >
                <path d="M0,0 L8,4 L0,8 z" className={ink.marker} />
              </marker>
            ))}
          </defs>
          {path.edges.map((edge) => {
            const from = layout.positions.get(edge.from);
            const to = layout.positions.get(edge.to);
            if (from === undefined || to === undefined) {
              return null;
            }
            const outcome = noFires ? "none" : edgeOutcome(edge, byId, runs);
            const ink = EDGE_INK[outcome];
            return (
              <path
                key={`${edge.from}->${edge.to}`}
                d={edgePath(from, to)}
                className={cn("fill-none", ink.stroke)}
                strokeWidth={ink.width}
                strokeDasharray={ink.dash}
                markerEnd={`url(#stream-path-arrow-${outcome})`}
                data-testid="stream-path-edge"
                data-outcome={outcome}
              />
            );
          })}
        </svg>

        {path.nodes.map((node) => {
          const position = layout.positions.get(node.id);
          if (position === undefined) {
            return null;
          }
          const picked = node.id === selectedId;
          const flow = node.type === "flow";
          const identity = flow ? flowKindIdentity(node.kind) : null;
          const tone = identity === null ? null : flowKindToneClasses(identity.tone);
          const look = kindLook(node.type, node.kind.toLowerCase());
          const Icon = look.icon;
          return (
            <button
              key={node.id}
              type="button"
              data-node-id={node.id}
              data-testid={flow ? "stream-path-flow" : "stream-path-object"}
              data-role={node.role}
              onClick={() => onSelect(picked ? null : node.id)}
              className={cn(
                "absolute flex cursor-pointer flex-col justify-center gap-0.5 rounded-md border bg-card px-2.5 text-left shadow-xs transition-shadow hover:shadow-md focus-visible:outline-none",
                flow && "border-t-[3px]",
                tone?.edge,
                node.role === "selected" ? "border-foreground/45" : "border-border",
                !flow && "rounded-lg border-dashed",
                node.external && "bg-muted/40",
                picked && "ring-2 ring-primary ring-offset-1 ring-offset-background",
              )}
              style={{ left: position.x, top: position.y, width: NODE_WIDTH, height: NODE_HEIGHT }}
              aria-pressed={picked}
              aria-label={`${flow ? identity!.label : look.label} ${nodeLabel(node)}`}
            >
              <span className="flex min-w-0 items-center gap-1.5">
                <Icon aria-hidden className={cn("size-3.5 shrink-0", flow ? tone!.icon : "text-muted-foreground")} />
                {/* Clipped from the start: names on one path share their head (a source's prefix) and differ at the end
                    (-pre, -ing, -delivery-v150), so the end is the part kept in view. */}
                <span
                  className={cn(
                    "min-w-0 truncate text-left font-mono text-[12px] leading-4 [direction:rtl]",
                    node.role === "selected" ? "font-semibold" : "font-medium",
                  )}
                  title={node.qualifiedName ?? node.name}
                >
                  <bdi>{nodeLabel(node)}</bdi>
                </span>
                {flow && node.pipelineId !== null && <VerdictMark stream={streams.get(node.pipelineId)} windowDays={windowDays} />}
              </span>
              <span className="flex min-w-0 items-center gap-1.5 text-[11px] leading-4">
                {flow
                  ? <FlowFigures entry={node.pipelineId === null ? undefined : runs.get(node.pipelineId)} windowDays={windowDays} noFires={noFires} />
                  : <ObjectFigures node={node} path={path} runs={runs} byId={byId} noFires={noFires} />}
              </span>
              <OmittedMark node={node} />
            </button>
          );
        })}
      </div>
    </div>
  );
}
