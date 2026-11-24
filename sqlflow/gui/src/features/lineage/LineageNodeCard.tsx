import { createContext, memo, useContext } from "react";
import { Handle, Position, type Node, type NodeProps } from "@xyflow/react";
import { ChevronsDown, ChevronsRight, CircleX, Layers, Loader2, TriangleAlert } from "lucide-react";
import { cn } from "@/lib/utils";
import type { StreamStatus } from "../../api/types";
import { statusIcons, statusInk } from "../datastreams/StreamStatusBadge";
import { statusLabels } from "../datastreams/streamPresentation";
import type { LensBar, NodeFigure } from "./dataLens";
import { counted, kindLook } from "./graphKinds";
import { CARD_SIZE, DATA_CARD_SIZE, type Orientation } from "./graphLayout";
import type { ViewNode } from "./graphModel";
import { LensBars } from "./LensBars";

/** Where a node stands against the selection: the selected node, on its trace, or beside it. */
export type CardRole = "selected" | "upstream" | "downstream" | "unrelated" | null;

/** What a card of the data view shows under its node's name: what the node did, and its chart over the window. */
export interface CardLens {
  figure: NodeFigure;
  /** One entry per day of the window, oldest first. */
  bars: readonly LensBar[];
  /** The day looked at, as an index into `bars`. */
  day: number;
}

export interface CardData extends Record<string, unknown> {
  node: ViewNode;
  /** Which way data runs: where the card's edges attach, and on which side it counts what was left out. */
  orientation: Orientation;
  role: CardRole;
  /** Dimmed while a control is being dragged towards hiding it, so its effect is seen before it is applied. */
  leaving: boolean;
  /** The worst delivery verdict among the flows the node stands for; null when they are healthy or unjudged. */
  status: StreamStatus | null;
  /** The data view's content; null when the graph shows how nodes connect and nothing else. */
  lens: CardLens | null;
}

export type CardNode = Node<CardData, "card">;

/** What a card's own controls do. Held in context so a node's data stays plain values. */
export interface CardActions {
  /** Bring back the producers (`up`) or consumers (`down`) a trace left out beside this node. */
  onReveal: (id: string, side: "up" | "down") => void;
  /** Ask the server to walk past the frontier objects reached through this node. */
  onExpandFrontier: (keys: string[]) => void;
  /** The data view: look at a day, picked on a card's chart. */
  onPickDay: (day: number) => void;
  /** The data view: a day of one node in words, for its chart's tooltip. */
  describeDay: (id: string, day: number) => string;
}

export const CardActionsContext = createContext<CardActions>({
  onReveal: () => {}, onExpandFrontier: () => {}, onPickDay: () => {}, describeDay: () => "",
});

function waveText(waves: readonly number[]): string {
  const real = waves.filter((wave) => wave >= 0);
  if (real.length === 0) {
    return waves.length > 0 ? "unwaved" : "";
  }
  const first = real[0];
  const last = real[real.length - 1];
  return first === last ? `wave ${first}` : `waves ${first}-${last}`;
}

/** The line under a node's name: what it is and where it sits. */
export function cardCaption(node: ViewNode): string {
  const parts: string[] = [];
  if (node.type === "group") {
    parts.push(counted(node.flowCount, "flow"));
    if (node.objectCount > 0) {
      parts.push(counted(node.objectCount, "object"));
    }
    parts.push(waveText(node.waves));
  } else if (node.type === "stack") {
    parts.push(node.name);
  } else if (node.type === "flow" && node.described === true) {
    // A lineage description is a step another system runs: it has no wave, and saying so here keeps it from reading
    // as a flow the estate forgot to order.
    parts.push(kindLook("flow", node.kind).label, "never run here");
  } else if (node.type === "flow") {
    parts.push(kindLook("flow", node.kind).label, waveText(node.waves));
    if (node.incompleteReasons.length > 0) {
      parts.push("lineage not derived");
    }
  } else {
    // An object only descriptions name is said to be so: no catalog row stands behind it.
    parts.push(node.kind, node.place, node.described === true ? "described" : "");
  }
  return parts.filter((part) => part !== "").join(" · ");
}

/** The characters of a flow's or an object's name a card has room for, in its monospaced face. */
const NAME_ROOM = 27;
/** How much of a long name's end is always shown. */
const NAME_TAIL = 12;

/**
 * A flow's or an object's name, cut in its middle when the card has no room for it. The flows of a folder share
 * how their names begin and differ in how they end (`...-01-pre`, `...-03-delivery`), and a kind's version is its
 * last segment, so cutting the end would leave a column of cards reading alike. The whole name stays in the page
 * (the start is clipped by its box, not by rewriting it), so it is still found by a search of the page.
 */
function CardName({ name, room }: { name: string; room: number }) {
  if (name.length <= room) {
    return <>{name}</>;
  }
  const cut = name.length - NAME_TAIL;
  return (
    <span className="flex min-w-0">
      <span className="min-w-0 truncate">{name.slice(0, cut)}</span>
      <span className="shrink-0 whitespace-pre">{name.slice(cut)}</span>
    </span>
  );
}

/** A node's name as its card writes it: a stack says how many of what it holds, everything else its own name. */
export function cardTitle(node: ViewNode): string {
  if (node.type !== "stack") {
    return node.name;
  }
  const look = kindLook(node.memberType, node.kind);
  return node.memberType === "flow"
    ? `${node.members.length} ${look.label.toLowerCase()} flows`
    : `${node.members.length} × ${look.label}`;
}

/**
 * A node's name on a card of the data view, which has no caption to say where it is: a table or a view leads with
 * its schema, because a chain holds the same name twice (the landing table and the archive table it is keyed
 * into) and the schema is what tells them apart. Everything else is named as it is everywhere.
 */
export function lensTitle(node: ViewNode): string {
  const title = cardTitle(node);
  if (node.type !== "object" || (node.kind !== "table" && node.kind !== "view")) {
    return title;
  }
  const schema = node.place.split(".").pop() ?? "";
  return schema === "" ? title : `${schema}.${title}`;
}

const ROLE_RING: Readonly<Record<Exclude<CardRole, null>, string>> = {
  selected: "border-primary bg-primary text-primary-foreground shadow-[0_0_0_3px_color-mix(in_srgb,var(--primary)_35%,transparent)]",
  upstream: "border-chart-7 shadow-[0_0_0_1px_var(--chart-7)]",
  downstream: "border-chart-2 shadow-[0_0_0_1px_var(--chart-2)]",
  unrelated: "opacity-25",
};

/**
 * The same roles on a card of the data view. Its ink says what moved, so the selection is told by the card's edge
 * alone: a filled card would recolour the chart inside it, and the upstream and downstream hues would read as two
 * more outcomes beside the ones the lines already show.
 */
const LENS_ROLE_RING: Readonly<Record<Exclude<CardRole, null>, string>> = {
  selected: "border-primary shadow-[0_0_0_3px_color-mix(in_srgb,var(--primary)_35%,transparent)]",
  upstream: "",
  downstream: "",
  unrelated: "opacity-25",
};

/** The room inside a data card's edges, which its chart fills. */
const LENS_PADDING = 12;
const LENS_CHART_HEIGHT = 24;

/**
 * What a node did in the day looked at, in the card's second row: a number that leads when data moved, and plain
 * words when it did not. Healthy and idle read quietly, a failure reads in the status colour with its glyph, and a
 * second fact (what was read, how much could not be moved) sits at the row's end.
 */
function LensFigure({ figure }: { figure: NodeFigure }) {
  const quiet = figure.outcome === "empty" || figure.outcome === "absent";
  return (
    <div className="flex h-7 min-w-0 items-baseline gap-1.5" data-testid="lens-figure" data-outcome={figure.outcome}>
      {figure.outcome === "failed" && <CircleX aria-hidden className="size-3.5 shrink-0 self-center text-destructive" />}
      {figure.outcome === "running" && <Loader2 aria-hidden className="size-3.5 shrink-0 animate-spin self-center text-primary" />}
      {figure.value !== "" && (
        <span className="shrink-0 text-[20px] leading-7 font-semibold tabular-nums">{figure.value}</span>
      )}
      <span
        className={cn(
          "min-w-0 shrink-0 truncate",
          figure.value !== "" ? "text-[12px] text-muted-foreground" : "text-[14px] leading-7 font-medium",
          figure.outcome === "failed" && "text-destructive",
          figure.outcome === "absent" && "text-muted-foreground/70",
          figure.outcome === "empty" && "text-muted-foreground",
        )}
      >
        {figure.label}
      </span>
      {(figure.note !== "" || figure.someFailed) && (
        <span
          className={cn(
            "ml-auto inline-flex min-w-0 items-center gap-1 truncate text-[12px]",
            figure.someFailed ? "text-destructive" : quiet ? "text-muted-foreground/80" : "text-muted-foreground",
          )}
        >
          {figure.someFailed && <CircleX aria-hidden className="size-3 shrink-0" />}
          <span className="truncate">{figure.someFailed && figure.note === "" ? "a run failed" : figure.note}</span>
        </span>
      )}
    </div>
  );
}

/**
 * One node of the graph. A flow is a square-cornered card led by its kind's glyph; data is a pill with a dashed
 * edge, so the two read apart without colour; a folder and a stack are cards with a second edge behind them, since
 * each stands for several nodes. What a reduction left out beside a node is said on the node, as a count above or
 * below it, and clicking the count brings exactly that back.
 *
 * In the data view the card is taller: under the name it says what the node did in the day looked at, and charts the
 * same measure over the window, so a chain is read top to bottom as amounts and its history is on every node of it.
 */
function LineageNodeCardInner({ data }: NodeProps<CardNode>) {
  const { node, orientation, role, leaving, status, lens } = data;
  const down = orientation === "down";
  // What was left out is counted where it would have been drawn: above and below the card, or before and after it.
  const before = down ? "-top-1 right-3 -translate-y-full" : "top-1/2 -left-2 -translate-x-full -translate-y-1/2";
  const after = down ? "-bottom-1 right-3 translate-y-full" : "top-1/2 -right-2 translate-x-full -translate-y-1/2";
  const More = down ? ChevronsDown : ChevronsRight;
  const actions = useContext(CardActionsContext);
  const look = kindLook(node.type === "group" ? "group" : node.memberType, node.kind);
  const Icon = node.type === "stack" ? Layers : look.icon;
  const title = lens === null ? cardTitle(node) : lensTitle(node);
  const caption = cardCaption(node);
  const selected = role === "selected";
  // Only the card that shows how nodes connect fills when selected; a data card keeps its paper so its chart reads.
  const filled = selected && lens === null;
  const incomplete = node.incompleteReasons.length > 0;
  const StatusIcon = status !== null ? statusIcons[status] : null;
  const several = node.type === "group" || node.type === "stack";
  const isData = node.memberType === "object" && node.type !== "stack";
  const size = lens === null ? CARD_SIZE : DATA_CARD_SIZE;

  const name = node.type === "group" ? (
    // A folder's path is told apart by its end, so a long one loses its start, not its end.
    <div className="truncate text-left text-[12px] leading-[18px] font-semibold [direction:rtl]">
      <bdi>{title}</bdi>
    </div>
  ) : (
    <div
      className={cn(
        "truncate text-[13px] leading-[18px] font-semibold",
        node.type !== "stack" && (lens === null ? "font-mono text-[12px]" : "font-mono text-[12.5px]"),
      )}
    >
      {node.type === "stack" ? title : <CardName name={title} room={lens === null ? NAME_ROOM : NAME_ROOM + 3} />}
    </div>
  );
  const marks = (
    <>
      {incomplete && <TriangleAlert aria-label="Lineage not derived" className="size-3.5 shrink-0 text-warning" />}
      {StatusIcon !== null && status !== null && (
        <span
          className={cn("inline-flex shrink-0", filled ? "text-primary-foreground" : statusInk[status])}
          title={`Data stream: ${statusLabels[status]}`}
          aria-label={`Data stream: ${statusLabels[status]}`}
        >
          <StatusIcon aria-hidden className="size-3.5" />
        </span>
      )}
    </>
  );

  return (
    <div
      data-testid="graph-node"
      data-node-type={node.type}
      data-node-id={node.id}
      title={incomplete ? node.incompleteReasons[0] : `${title}\n${caption}`}
      style={{ width: size.width, height: size.height }}
      className={cn(
        "relative border bg-card text-left text-card-foreground transition-opacity duration-150",
        lens === null ? "flex items-center gap-2 px-2.5" : "flex flex-col justify-between pt-1.5 pb-2",
        lens === null
          ? isData ? "rounded-full border-dashed px-3.5" : "rounded-md"
          : isData ? "rounded-xl border-dashed" : "rounded-md",
        several && "shadow-[3px_3px_0_0_var(--border)]",
        incomplete && "border-2 border-dashed border-warning",
        lens?.figure.outcome === "failed" && "border-destructive",
        node.anchor && role === null && "border-primary shadow-[0_0_0_2px_color-mix(in_srgb,var(--primary)_40%,transparent)]",
        role !== null && (lens === null ? ROLE_RING : LENS_ROLE_RING)[role],
        leaving && "opacity-20",
      )}
    >
      <Handle type="target" position={down ? Position.Top : Position.Left} isConnectable={false} className="!size-1 !min-h-0 !min-w-0 !border-0 !opacity-0" />
      {lens === null ? (
        <>
          <Icon aria-hidden className={cn("size-4 shrink-0", filled ? "text-primary-foreground" : look.iconClass)} />
          <div className="min-w-0 flex-1 overflow-hidden">
            {name}
            <div className={cn("truncate text-[11px] leading-4", filled ? "text-primary-foreground/80" : "text-muted-foreground")}>
              {caption}
            </div>
          </div>
          {marks}
        </>
      ) : (
        <>
          <div style={{ paddingInline: LENS_PADDING }} className="flex flex-col">
            <div className="flex h-[18px] items-center gap-2">
              <Icon aria-hidden className={cn("size-4 shrink-0", look.iconClass)} />
              <div className="min-w-0 flex-1 overflow-hidden">{name}</div>
              {marks}
            </div>
            <LensFigure figure={lens.figure} />
          </div>
          <div style={{ paddingInline: LENS_PADDING }}>
            <LensBars
              bars={lens.bars}
              day={lens.day}
              width={size.width - LENS_PADDING * 2 - 2}
              height={LENS_CHART_HEIGHT}
              describe={(day) => actions.describeDay(node.id, day)}
              onPick={actions.onPickDay}
            />
          </div>
        </>
      )}

      {node.hiddenUp > 0 && (
        <button
          type="button"
          data-testid="graph-node-more-up"
          className={cn("nodrag nopan absolute h-5 min-w-5 cursor-pointer rounded-full border border-border bg-background px-1.5 text-[10px] font-medium text-muted-foreground hover:border-primary hover:text-foreground", before)}
          title={`${counted(node.hiddenUp, "producer")} not drawn. Click to draw ${node.hiddenUp === 1 ? "it" : "them"}.`}
          aria-label={`Draw ${counted(node.hiddenUp, "hidden producer")}`}
          onClick={(event) => {
            event.stopPropagation();
            actions.onReveal(node.id, "up");
          }}
        >
          +{node.hiddenUp}
        </button>
      )}
      {node.hiddenDown > 0 && (
        <button
          type="button"
          data-testid="graph-node-more-down"
          className={cn("nodrag nopan absolute h-5 min-w-5 cursor-pointer rounded-full border border-border bg-background px-1.5 text-[10px] font-medium text-muted-foreground hover:border-primary hover:text-foreground", after)}
          title={`${counted(node.hiddenDown, "consumer")} not drawn. Click to draw ${node.hiddenDown === 1 ? "it" : "them"}.`}
          aria-label={`Draw ${counted(node.hiddenDown, "hidden consumer")}`}
          onClick={(event) => {
            event.stopPropagation();
            actions.onReveal(node.id, "down");
          }}
        >
          +{node.hiddenDown}
        </button>
      )}
      {node.frontierKeys.length > 0 && node.hiddenDown === 0 && (
        <button
          type="button"
          data-testid="graph-node-expand"
          className={cn("nodrag nopan absolute inline-flex h-5 cursor-pointer items-center gap-0.5 rounded-full border border-chart-7 bg-background px-1.5 text-[10px] font-medium text-foreground hover:bg-accent", after)}
          title="More is downstream of this node than the graph loaded. Click to load it."
          aria-label="Load what is further downstream"
          onClick={(event) => {
            event.stopPropagation();
            actions.onExpandFrontier(node.frontierKeys);
          }}
        >
          more
          <More aria-hidden className="size-3" />
        </button>
      )}
      <Handle type="source" position={down ? Position.Bottom : Position.Right} isConnectable={false} className="!size-1 !min-h-0 !min-w-0 !border-0 !opacity-0" />
    </div>
  );
}

export const LineageNodeCard = memo(LineageNodeCardInner);
