import { useEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from "react";
import {
  Background,
  MarkerType,
  MiniMap,
  Panel,
  ReactFlow,
  useNodesState,
  useReactFlow,
  useStore,
  useStoreApi,
  type Viewport,
  type Edge as FlowEdge,
  type Node as FlowNode,
  type OnNodesChange,
} from "@xyflow/react";
import { Maximize, Minus, Plus } from "lucide-react";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import type { StreamStatus } from "../../api/types";
import { edgeWidth, type Outcome } from "./dataLens";
import { type CardSize, type Orientation, type Position } from "./graphLayout";
import type { Lane } from "./graphBands";
import type { View, ViewEdge } from "./graphModel";
import { LANE_LABEL_ROOM, LANE_LABEL_ROOM_DESCRIBED, LaneBand, type LaneNode } from "./LaneBand";
import {
  CardActionsContext, LineageNodeCard, type CardActions, type CardLens, type CardNode, type CardRole,
} from "./LineageNodeCard";

const nodeTypes = { card: LineageNodeCard, lane: LaneBand };

/** How far a band's backdrop reaches past its cards. */
const LANE_MARGIN = 16;

/** A drawing this small is seen whole, so a map of it would only cover part of it. */
const MINIMAP_ABOVE = 60;
/** An arrowhead's size on the data view's lines, in pixels at full zoom, whatever the line's weight. */
const ARROW_SIZE = 18;
/** The data view's cards are larger and placed for reading, so far fewer of them fill a window. */
const MAP_DATA_ABOVE = 6;
/** The map of the whole drawing, over the zoom buttons in the canvas's lower left corner. */
const MAP_WIDTH = 132;
const MAP_HEIGHT = 124;
/** The room a placed drawing keeps clear on its left for the map, where one is drawn. */
const MAP_ROOM = MAP_WIDTH + 28;

/** Up to this many edges, each one names the flows it stands for; past it the names would bury the drawing. */
const LABEL_EVERY_EDGE_UP_TO = 30;
/** The selected node's own edges say what they pass through, unless it has more of them than can be read. */
const LABEL_SELECTED_EDGES_UP_TO = 10;
/** The room kept clear beside the details panel when a drawing is fitted with a node selected. */
const DETAILS_MARGIN = 24;
/**
 * The room a placed drawing keeps clear on its left, where the zoom buttons stand in the canvas's lower corner: a
 * band's name is the first thing on that side, and the buttons would cover the name of whichever band ends there.
 */
const CONTROLS_ROOM = 56;

/** Whether a drawing gets a map: one seen whole needs none, and a map of it would only cover part of it. */
function hasMap(lensOn: boolean, nodes: number): boolean {
  return nodes > (lensOn ? MAP_DATA_ABOVE : MINIMAP_ABOVE);
}

/** The room the canvas's own control takes on the left: the map over the zoom buttons, or the buttons alone. */
function controlRoom(mapped: boolean): number {
  return mapped ? MAP_ROOM : CONTROLS_ROOM;
}

function fitOptions(detailsWidth: number, mapped: boolean) {
  return {
    maxZoom: 1,
    padding: {
      top: "24px",
      bottom: "24px",
      left: `${controlRoom(mapped)}px`,
      right: `${detailsWidth > 0 ? detailsWidth + DETAILS_MARGIN : 32}px`,
    },
  } as const;
}

/** The least zoom the data view places a drawing at: below it a card's figure and name are no longer read. */
const READABLE_ZOOM = 0.74;

/**
 * Where the data view places a drawing: whole and centred when that leaves it readable, otherwise at the least
 * readable zoom from its top, where data enters, with the node asked for (the traced or selected one) in view.
 * A chain of seven steps is taller than a window is high; shrunk to fit, none of its figures could be read, so
 * the page shows the start of it at a size that can and the reader moves down it. Across the reading direction
 * the node asked for stands in the middle of the room: a traced chain is read down its own column, and a wide
 * row of what depends on it far below would otherwise push that column to the canvas's edge.
 */
function readableViewport(
  positions: ReadonlyMap<string, Position>,
  size: CardSize,
  canvas: { width: number; height: number },
  /** `control` is the canvas's own control on the left; `left` is what the drawing itself reaches past its cards. */
  room: { control: number; left: number; right: number },
  anchorId: string | null,
  /** Whether the drawing reads top to bottom; otherwise it reads left to right. */
  down: boolean,
): Viewport | null {
  if (positions.size === 0 || canvas.width <= 0 || canvas.height <= 0) {
    return null;
  }

  let minX = Number.POSITIVE_INFINITY;
  let minY = Number.POSITIVE_INFINITY;
  let maxX = Number.NEGATIVE_INFINITY;
  let maxY = Number.NEGATIVE_INFINITY;
  for (const at of positions.values()) {
    minX = Math.min(minX, at.x);
    minY = Math.min(minY, at.y);
    maxX = Math.max(maxX, at.x + size.width);
    maxY = Math.max(maxY, at.y + size.height);
  }
  minX -= room.left;

  const pad = 24;
  const top = pad;
  const wide = canvas.width - room.control - room.right;
  const high = canvas.height - top - pad;
  const zoom = Math.max(READABLE_ZOOM, Math.min(1, wide / (maxX - minX), high / (maxY - minY)));
  const anchor = anchorId === null ? undefined : positions.get(anchorId);
  // Along one axis: centred when the drawing fits. Otherwise, in the reading direction, from its start, moved
  // only as far as brings the anchor a third of the way in; across it, with the anchor in the middle. Never
  // past either end of the drawing.
  const along = (
    start: number, end: number, space: number, offset: number, anchorAt: number | undefined, anchorSize: number, reading: boolean,
  ) => {
    const length = (end - start) * zoom;
    if (length <= space) {
      return offset + (space - length) / 2 - start * zoom;
    }
    const fromStart = offset - start * zoom;
    if (anchorAt === undefined) {
      return fromStart;
    }
    const toEnd = offset + space - end * zoom;
    if (!reading) {
      return Math.max(toEnd, Math.min(fromStart, offset + space / 2 - (anchorAt + anchorSize / 2) * zoom));
    }
    return (anchorAt + anchorSize - start) * zoom <= space
      ? fromStart
      : Math.max(toEnd, Math.min(fromStart, offset + space / 3 - anchorAt * zoom));
  };
  return {
    x: along(minX, maxX, wide, room.control, anchor?.x, size.width, !down),
    y: along(minY, maxY, high, top, anchor?.y, size.height, down),
    zoom,
  };
}

/**
 * What the data view puts on the canvas: each card's figure and chart, and how each line is inked and weighed by
 * what it carried in the day looked at.
 */
export interface CanvasLens {
  cards: ReadonlyMap<string, CardLens>;
  edges: ReadonlyMap<string, { outcome: Outcome; volume: number }>;
  /** The most any line of the drawing carried, which its widest line stands for. */
  largest: number;
}

/** A line's ink by what happened on it: carried data, ran and carried none, failed, under way, or no run at all. */
const LENS_INK: Readonly<Record<Outcome, { stroke: string; dash?: string; opacity: number }>> = {
  moved: { stroke: "var(--chart-1)", opacity: 0.95 },
  empty: { stroke: "var(--muted-foreground)", dash: "6 5", opacity: 0.8 },
  failed: { stroke: "var(--destructive)", opacity: 0.95 },
  running: { stroke: "var(--primary)", dash: "2 4", opacity: 0.9 },
  absent: { stroke: "var(--muted-foreground)", dash: "1 6", opacity: 0.4 },
};

export interface CanvasSelection {
  id: string;
  upstream: ReadonlySet<string>;
  downstream: ReadonlySet<string>;
}

export interface CenterRequest {
  id: string;
  nonce: number;
}

interface CanvasProps {
  view: View;
  positions: ReadonlyMap<string, Position>;
  orientation: Orientation;
  /** The bands the drawing is laid in, one per wave, with the reach of the drawing across them. Empty when it has none. */
  lanes: readonly Lane[];
  laneReach: { start: number; end: number };
  selection: CanvasSelection | null;
  /** The worst delivery verdict per drawn node, where there is one worth showing. */
  statuses: ReadonlyMap<string, StreamStatus>;
  /** While the wave range is being dragged: the waves that would stay. Nodes outside them fade. */
  previewWaves: ReadonlySet<number> | null;
  /** The names the hidden nodes an edge passes through go by. */
  nameOf: (baseId: string) => string;
  colorMode: "light" | "dark";
  /** The size of the cards the positions were laid out for. */
  size: CardSize;
  /** The data view's figures; null when the graph shows how nodes connect and nothing else. */
  lens: CanvasLens | null;
  /** The width the details panel covers on the right while a node is selected. */
  detailsWidth: number;
  /** The node a drawing placed for reading keeps in view: the one traced or selected. */
  anchorId: string | null;
  centerRequest: CenterRequest | null;
  actions: CardActions;
  onSelect: (id: string | null) => void;
  onActivate: (id: string) => void;
  onNodeContextMenu: (id: string, position: { x: number; y: number }) => void;
  children?: ReactNode;
}

/**
 * The map inside the canvas's control: the library places a map over a corner as a panel of its own, and here it is
 * one part of a control that is already placed, so it takes its place in that control's flow and wears no edge.
 */
const MAP_STYLE: CSSProperties = {
  position: "relative", margin: 0, width: MAP_WIDTH, height: MAP_HEIGHT, border: "none", borderRadius: 0,
};

const CONTROL_BUTTON = "inline-flex h-7 min-w-7 shrink-0 cursor-pointer items-center justify-center text-muted-foreground hover:bg-accent hover:text-foreground disabled:cursor-default disabled:opacity-40 disabled:hover:bg-transparent disabled:hover:text-muted-foreground";

/**
 * The one thing that stands on the canvas: the map of the whole drawing, where one is drawn, over the three zoom
 * buttons, as a single control in the lower left corner. A drawing small enough to see whole has the buttons alone,
 * stacked, so they take the least width beside the bands' names. Everything else about the drawing is said in the
 * page's own edges: the line over the canvas and the details beside it.
 */
function CanvasControl({ onFit, children }: { onFit: () => void; children: ReactNode }) {
  const flow = useReactFlow();
  const atLeast = useStore((state) => state.transform[2] <= state.minZoom);
  const atMost = useStore((state) => state.transform[2] >= state.maxZoom);
  const mapped = children !== null && children !== undefined && children !== false;
  const side = mapped ? "top" as const : "right" as const;
  return (
    <Panel position="bottom-left" className="!m-3">
      <div
        data-testid="graph-canvas-control"
        className="flex flex-col overflow-hidden rounded-md border border-border bg-card shadow-sm"
      >
        {children}
        <div className={cn("flex", mapped ? "border-t border-border" : "flex-col")} role="group" aria-label="Zoom">
          <Tooltip>
            <TooltipTrigger asChild>
              <button
                type="button" className={cn(CONTROL_BUTTON, mapped && "flex-1")} disabled={atMost} aria-label="Zoom in"
                onClick={() => void flow.zoomIn({ duration: 160 })} data-testid="graph-zoom-in"
              >
                <Plus aria-hidden className="size-3.5" />
              </button>
            </TooltipTrigger>
            <TooltipContent side={side}>Zoom in</TooltipContent>
          </Tooltip>
          <Tooltip>
            <TooltipTrigger asChild>
              <button
                type="button" className={cn(CONTROL_BUTTON, mapped ? "flex-1 border-x border-border" : "border-y border-border")}
                disabled={atLeast} aria-label="Zoom out"
                onClick={() => void flow.zoomOut({ duration: 160 })} data-testid="graph-zoom-out"
              >
                <Minus aria-hidden className="size-3.5" />
              </button>
            </TooltipTrigger>
            <TooltipContent side={side}>Zoom out</TooltipContent>
          </Tooltip>
          <Tooltip>
            <TooltipTrigger asChild>
              <button
                type="button" className={cn(CONTROL_BUTTON, mapped && "flex-1")} aria-label="Fit the whole drawing"
                onClick={onFit} data-testid="graph-zoom-fit"
              >
                <Maximize aria-hidden className="size-3.5" />
              </button>
            </TooltipTrigger>
            <TooltipContent side={side}>Fit the whole drawing in the canvas</TooltipContent>
          </Tooltip>
        </div>
      </div>
    </Panel>
  );
}

function edgeLabel(edge: ViewEdge, nameOf: (id: string) => string): string {
  if (edge.labels.length > 0) {
    return edge.labels.length <= 2 ? edge.labels.join(", ") : `${edge.labels.length} flows`;
  }
  if (edge.via.length === 1) {
    return `via ${nameOf(edge.via[0])}`;
  }
  return edge.via.length > 1 ? `via ${edge.via.length} nodes` : "";
}

/**
 * The canvas. Positions live in React Flow's state so a node can be dragged, and are seeded again whenever the
 * drawing changes; the selection's emphasis is applied on top without touching positions. A click selects (the
 * node's producers and consumers light up, the rest fades), a double click opens what the node stands for, and a
 * click on the empty canvas clears the selection. A new drawing is fitted to the canvas, and its nodes slide to
 * their places instead of jumping, so the reader keeps their bearings through a change of scope.
 */
export function GraphCanvas({
  view, positions, orientation, lanes, laneReach, selection, statuses, previewWaves, nameOf, colorMode, size, lens, detailsWidth,
  anchorId, centerRequest, actions,
  onSelect, onActivate, onNodeContextMenu, children,
}: CanvasProps) {
  const flow = useReactFlow();
  const store = useStoreApi();
  const seeded = useMemo<CardNode[]>(
    () => view.nodes.map((node) => ({
      id: node.id,
      type: "card" as const,
      position: positions.get(node.id) ?? { x: 0, y: 0 },
      data: { node, orientation, role: null, leaving: false, status: null, lens: null },
      width: size.width,
      height: size.height,
    })),
    [view, positions, orientation, size],
  );
  const [flowNodes, setFlowNodes, onNodesChange] = useNodesState<CardNode>(seeded);
  const [moving, setMoving] = useState(false);
  const firstLayout = useRef(true);
  // Read when a drawing is fitted, never a reason to fit: selecting a node must not move the canvas.
  const detailsOpen = useRef(selection !== null ? detailsWidth : 0);
  useEffect(() => {
    detailsOpen.current = selection !== null ? detailsWidth : 0;
  }, [selection, detailsWidth]);

  // How a drawing is placed on the canvas. The graph of how nodes connect is fitted whole, as it always was. The
  // data view is placed for reading: its cards carry figures, and a chain shrunk until it fits shows none of them.
  // Read when a drawing is placed, never a reason to place one, so selecting a node does not move the canvas.
  const mapped = hasMap(lens !== null, view.nodes.length);
  const placing = useRef({ readable: lens !== null, mapped, anchorId, positions, size, lanes, orientation });
  useEffect(() => {
    placing.current = { readable: lens !== null, mapped, anchorId, positions, size, lanes, orientation };
  }, [lens, mapped, anchorId, positions, size, lanes, orientation]);
  /** The whole drawing in the canvas, clear of the canvas's control on the left and of the details on the right. */
  const fitWhole = useRef((duration: number) => {
    void flow.fitView({ duration, ...fitOptions(detailsOpen.current, placing.current.mapped) });
  });
  const place = useRef((duration: number) => {
    const now = placing.current;
    if (!now.readable) {
      fitWhole.current(duration);
      return;
    }
    const { width, height } = store.getState();
    // The bands' names stand beside the cards (top to bottom), so the drawing reaches that much further left.
    const labelRoom = now.lanes.length > 0 && now.orientation === "down"
      ? (now.lanes.some((lane) => lane.described) ? LANE_LABEL_ROOM_DESCRIBED : LANE_LABEL_ROOM) + LANE_MARGIN
      : 0;
    const viewport = readableViewport(
      now.positions, now.size, { width, height },
      // The canvas's control stands in the lower left corner, so the drawing starts beside it and never under it.
      {
        control: controlRoom(now.mapped),
        left: labelRoom,
        right: detailsOpen.current > 0 ? detailsOpen.current + DETAILS_MARGIN : 24,
      },
      now.anchorId,
      now.orientation === "down",
    );
    if (viewport !== null) {
      void flow.setViewport(viewport, { duration });
    }
  });

  useEffect(() => {
    setFlowNodes(seeded);
    // The first drawing appears in place; later ones slide, and the canvas is placed once they have landed.
    if (firstLayout.current) {
      firstLayout.current = false;
      return;
    }
    setMoving(true);
    const settle = window.setTimeout(() => setMoving(false), 260);
    const fit = window.requestAnimationFrame(() => place.current(240));
    return () => {
      window.clearTimeout(settle);
      window.cancelAnimationFrame(fit);
    };
  }, [seeded, setFlowNodes]);

  // A request is answered once, by the first drawing that holds its node: a node picked from a list may only be
  // drawn by the render after the one that asked for it (its folder opens, its stack unstacks).
  const answered = useRef(0);
  useEffect(() => {
    if (centerRequest === null || answered.current === centerRequest.nonce) {
      return;
    }
    const target = positions.get(centerRequest.id);
    if (target === undefined) {
      return;
    }
    // After the fit a new drawing asks for, so the node asked for is what the canvas ends on.
    const handle = window.setTimeout(() => {
      answered.current = centerRequest.nonce;
      void flow.setCenter(target.x + size.width / 2, target.y + size.height / 2, {
        zoom: Math.max(flow.getZoom(), 0.9),
        duration: 400,
      });
    }, 300);
    return () => window.clearTimeout(handle);
  }, [centerRequest, positions, flow, size]);

  const styledNodes = useMemo<CardNode[]>(() => flowNodes.map((flowNode) => {
    const node = flowNode.data.node;
    let role: CardRole = null;
    if (selection !== null) {
      role = node.id === selection.id
        ? "selected"
        : selection.upstream.has(node.id)
          ? "upstream"
          : selection.downstream.has(node.id) ? "downstream" : "unrelated";
    }
    const leaving = previewWaves !== null && node.memberType === "flow" && node.waves.length > 0
      && !node.waves.some((wave) => previewWaves.has(wave));
    return {
      ...flowNode,
      zIndex: role === "selected" ? 10 : undefined,
      data: { node, orientation, role, leaving, status: statuses.get(node.id) ?? null, lens: lens?.cards.get(node.id) ?? null },
    };
  }), [flowNodes, orientation, selection, previewWaves, statuses, lens]);

  // The bands sit behind the cards and take no part in anything: no click, no selection, no drag.
  const laneNodes = useMemo<LaneNode[]>(() => lanes.map((lane, index) => {
    const down = orientation === "down";
    // Every band of a drawing leaves the same room for its name, so the bands line up.
    const labelRoom = lanes.some((other) => other.described) ? LANE_LABEL_ROOM_DESCRIBED : LANE_LABEL_ROOM;
    const along = lane.end - lane.start + LANE_MARGIN * 2;
    const across = laneReach.end - laneReach.start + labelRoom + LANE_MARGIN * 2;
    const acrossRight = laneReach.end - laneReach.start + 36 + LANE_MARGIN * 2;
    return {
      id: `lane:${lane.rank}`,
      type: "lane" as const,
      position: down
        ? { x: laneReach.start - labelRoom - LANE_MARGIN, y: lane.start - LANE_MARGIN }
        : { x: lane.start - LANE_MARGIN, y: laneReach.start - 36 - LANE_MARGIN },
      width: down ? across : along,
      height: down ? along : acrossRight,
      zIndex: -1,
      draggable: false,
      selectable: false,
      focusable: false,
      style: { pointerEvents: "none" as const },
      data: {
        // A wave's band is named by its wave. A band of steps other systems run says so: it is no wave of the plan.
        label: lane.wave !== null ? `Wave ${lane.wave}` : lane.described ? "Other systems" : "",
        shaded: index % 2 === 0,
        orientation,
      },
    };
  }), [lanes, laneReach, orientation]);
  const drawnNodes = useMemo<FlowNode[]>(() => [...laneNodes, ...styledNodes], [laneNodes, styledNodes]);

  const styledEdges = useMemo<FlowEdge[]>(() => {
    const labelAll = view.edges.length <= LABEL_EVERY_EDGE_UP_TO;
    const own = selection === null
      ? 0
      : view.edges.filter((edge) => edge.source === selection.id || edge.target === selection.id).length;
    const upstream = selection === null ? null : new Set([selection.id, ...selection.upstream]);
    const downstream = selection === null ? null : new Set([selection.id, ...selection.downstream]);
    // An arrow into or out of a described node is one a lineage description states, not one the estate computed:
    // it is drawn dotted, so the two read apart without colour.
    const described = new Set(view.nodes.filter((node) => node.described === true).map((node) => node.id));
    return view.edges.map((edge) => {
      const onUpstream = upstream !== null && upstream.has(edge.source) && upstream.has(edge.target);
      const onDownstream = downstream !== null && downstream.has(edge.source) && downstream.has(edge.target);
      const related = onUpstream || onDownstream;
      const stroke = onUpstream ? "var(--chart-7)" : onDownstream ? "var(--chart-2)" : "var(--muted-foreground)";
      const opacity = selection === null ? 0.55 : related ? 1 : 0.08;
      // An edge is named where the name can be read: on a small drawing the flows it stands for, and on the
      // selected node's own edges what they pass through. Everything else is in the details panel.
      const touchesSelected = selection !== null && (edge.source === selection.id || edge.target === selection.id);
      const dotted = described.has(edge.source) || described.has(edge.target) ? "2 5" : undefined;
      if (lens !== null) {
        // The data view inks a line by what it carried and weighs it by how much, so the eye follows the data down
        // the chain and sees where it thinned out or stopped. The selection only fades what is beside it.
        const carried = lens.edges.get(edge.id) ?? { outcome: "absent" as Outcome, volume: 0 };
        const ink = LENS_INK[carried.outcome];
        const faded = selection !== null && !related;
        // No number rides on a line: the cards at its ends say what was written and what was read, and a
        // third copy between them would only cover the line. An arrowhead is sized in units of its line's
        // width, so it is given fewer of them on a heavier line: it stays near one size, a little wider than
        // the line it ends, and a heavy line reads as much data rather than as a large arrow.
        const weight = carried.outcome === "moved" ? edgeWidth(carried.volume, lens.largest) : 1.5;
        const head = Math.max(ARROW_SIZE, weight * 5.5) / weight;
        return {
          id: edge.id,
          source: edge.source,
          target: edge.target,
          zIndex: carried.outcome === "moved" || carried.outcome === "failed" ? 5 : 0,
          markerEnd: { type: MarkerType.ArrowClosed, color: ink.stroke, width: head, height: head },
          style: {
            stroke: ink.stroke,
            strokeWidth: weight,
            opacity: faded ? 0.08 : ink.opacity,
            strokeDasharray: dotted ?? ink.dash,
          },
        };
      }
      const label = touchesSelected && own <= LABEL_SELECTED_EDGES_UP_TO
        ? edgeLabel(edge, nameOf)
        : labelAll && selection === null && edge.labels.length > 0 ? edgeLabel(edge, nameOf) : "";
      return {
        id: edge.id,
        source: edge.source,
        target: edge.target,
        label: label === "" ? undefined : label,
        zIndex: related ? 5 : 0,
        markerEnd: { type: MarkerType.ArrowClosed, color: stroke, width: 16, height: 16 },
        style: {
          stroke, strokeWidth: edge.weight > 1 ? 2 : 1.25, opacity,
          strokeDasharray: dotted,
        },
        labelStyle: { fontSize: 11, opacity },
      };
    });
  }, [view.edges, view.nodes, selection, nameOf, lens]);

  return (
    <CardActionsContext.Provider value={actions}>
      <ReactFlow
        className={moving ? "sf-lineage-moving" : undefined}
        nodes={drawnNodes}
        edges={styledEdges}
        nodeTypes={nodeTypes}
        onNodesChange={onNodesChange as OnNodesChange}
        onNodeClick={(_, node) => onSelect(node.id)}
        onNodeDoubleClick={(_, node) => onActivate(node.id)}
        onNodeContextMenu={(event, node) => {
          event.preventDefault();
          onNodeContextMenu(node.id, { x: event.clientX, y: event.clientY });
        }}
        onPaneClick={() => onSelect(null)}
        colorMode={colorMode}
        // The data view's first drawing is placed for reading once the canvas knows its size; the graph's is fitted.
        onInit={() => {
          if (placing.current.readable) {
            place.current(0);
          }
        }}
        fitView={lens === null}
        fitViewOptions={fitOptions(selection !== null ? detailsWidth : 0, mapped)}
        minZoom={0.05}
        nodesConnectable={false}
        nodesDraggable
        zoomOnDoubleClick={false}
      >
        <Background />
        <CanvasControl onFit={() => fitWhole.current(240)}>
          {/* A drawing too large to see whole gets a map of all of it. In the data view, which is placed for
              reading and so often runs past the window, each node is in the ink of what happened there, which is
              where the data stopped at a glance. */}
          {mapped && (
            <MiniMap
              pannable
              zoomable
              style={MAP_STYLE}
              ariaLabel={lens === null ? "The whole drawing" : "The whole drawing, each node in the ink of what happened there"}
              nodeBorderRadius={6}
              nodeColor={(node) => {
                if (node.type === "lane") {
                  return "transparent";
                }
                const outcome = lens?.cards.get(node.id)?.figure.outcome ?? "absent";
                return outcome === "absent" ? "var(--input)" : LENS_INK[outcome].stroke;
              }}
            />
          )}
        </CanvasControl>
        {children}
      </ReactFlow>
    </CardActionsContext.Provider>
  );
}
