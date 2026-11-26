// The lines of the overview's canvas: each table's drop from its card to the rows it reaches, the wires from a field to the
// rows reading it, the dots where a row reads a table, and the wide invisible edges over every line that a reader points at.

import "d3-transition";
import { EDGE_HIT, LEAVE_MS, LIT_GROUP, SETTLE_MS, toneOf, WIRE } from "./canvasLook";
import { kindOf, menued, tipped, wire, type CanvasScene } from "./canvasScene";
import { operandOf } from "./outlineModel";
import type { OutlineLayout, PlacedDot, PlacedDrop, PlacedWire } from "./outlineLayout";
import type { OverviewPart } from "./OverviewCanvas";

/** A line a reader can point at, and what pointing at it means. */
interface PlacedEdge {
  id: string;
  d: string;
  part: OverviewPart;
  tip: { title: string; body: string } | null;
}

export function drawDrops(scene: CanvasScene, drops: readonly PlacedDrop[], moves: number): void {
  const joined = scene.layers.drops.selectAll<SVGGElement, PlacedDrop>("g.drop").data(drops, (drop) => drop.source);
  const entered = joined.enter().append("g").attr("class", `drop ${LIT_GROUP}`);
  entered.append("path").attr("class", "curve fill-none");
  entered.append("line").attr("class", "column");
  entered.append("path").attr("class", "curve-lit fill-none stroke-primary").attr("stroke-width", 1.75).attr("display", "none");
  entered.append("line").attr("class", "column-lit stroke-primary").attr("stroke-width", 1.75).attr("display", "none");
  joined.exit().remove();
  const all = entered.merge(joined);
  const kind = (drop: PlacedDrop) => kindOf(scene, drop.source);
  all.select<SVGPathElement>("path.curve-lit").attr("d", (drop) => drop.curve);
  all.select<SVGLineElement>("line.column-lit")
    .attr("x1", (drop) => drop.x)
    .attr("x2", (drop) => drop.x)
    .attr("y1", (drop) => drop.from);
  all.select<SVGPathElement>("path.curve")
    .attr("class", (drop) => `curve fill-none ${toneOf(kind(drop)).stroke}`)
    .attr("stroke-opacity", 0.4)
    .attr("stroke-width", 1)
    .attr("d", (drop) => drop.curve);
  all.select<SVGLineElement>("line.column")
    .attr("class", (drop) => `column ${toneOf(kind(drop)).stroke}`)
    .attr("stroke-opacity", 0.3)
    .attr("stroke-width", 1)
    .attr("x1", (drop) => drop.x)
    .attr("x2", (drop) => drop.x)
    .attr("y1", (drop) => drop.from)
    .transition().duration(moves)
    .attr("y2", (drop) => drop.to);
}

/**
 * Every line a reader can point at, drawn again over everything but the dots, wider than it shows and with nothing to see: what
 * lights a line is what lights the part it comes from, and a click picks that part (a double click opens it), so a line works as
 * its table or its field.
 */
export function drawEdges(scene: CanvasScene, layout: OutlineLayout): void {
  const edges: PlacedEdge[] = [
    ...layout.drops.flatMap((drop) => {
      const card = layout.cards.find((candidate) => candidate.card.id === drop.source);
      const tip = card === undefined
        ? null
        : { title: card.card.title, body: `${card.card.role}. Reaches ${card.card.readers} ${card.card.readers === 1 ? "property" : "properties"}.` };
      const part: OverviewPart = { type: "card", source: drop.source };
      return [
        { id: `${drop.source}-curve`, d: drop.curve, part, tip },
        { id: `${drop.source}-column`, d: `M${drop.x},${drop.from}V${drop.to}`, part, tip },
      ];
    }),
    ...layout.wires.map((wire): PlacedEdge => ({
      id: wire.id.replaceAll("\u0000", "-"),
      d: wire.d,
      part: wire.heavy || wire.field === ""
        ? { type: "card", source: wire.source }
        : { type: "field", source: wire.source, field: wire.field.slice(wire.field.indexOf("\u0000") + 1) },
      tip: null,
    })),
  ];

  const joined = scene.layers.edges.selectAll<SVGPathElement, PlacedEdge>("path").data(edges, (edge) => edge.id);
  joined.exit().remove();
  const all = joined.enter().append("path")
    .attr("fill", "none")
    .attr("stroke", "transparent")
    .attr("stroke-width", EDGE_HIT)
    .attr("pointer-events", "stroke")
    .attr("class", "cursor-pointer")
    .attr("data-testid", (edge) => `mapping-overview-edge-${edge.id}`)
    .merge(joined)
    .attr("d", (edge) => edge.d);
  all
    .on("mouseenter", (event: MouseEvent, edge) => {
      const { clientX: x, clientY: y } = event;
      scene.settle(() => {
        scene.handlers.point(edge.part);
        scene.handlers.tip(edge.tip === null ? null : { x, y, ...edge.tip });
      }, SETTLE_MS);
    })
    .on("mouseleave", () => {
      scene.handlers.tip(null);
      scene.settle(() => scene.handlers.point(null), LEAVE_MS);
    })
    .on("click", (event: MouseEvent, edge) => {
      event.stopPropagation();
      scene.handlers.pick(edge.part);
    })
    .on("dblclick", (event: MouseEvent, edge) => {
      event.stopPropagation();
      scene.handlers.open(edge.part);
    });
  menued(scene, all, (edge) => edge.part, false);
}

export function drawWires(scene: CanvasScene, wires: readonly PlacedWire[], moves: number): void {
  const joined = scene.layers.wires.selectAll<SVGPathElement, PlacedWire>("path").data(wires, (wire) => wire.id);
  joined.exit().transition().duration(moves / 2).style("opacity", 0).remove();
  const entered = joined.enter().append("path").attr("d", (wire) => wire.d).style("opacity", 0);
  entered.transition().duration(moves).style("opacity", null);
  const all = entered.merge(joined);
  all
    .attr("class", (wire) => `${WIRE} ${toneOf(kindOf(scene, wire.source)).stroke}`)
    .attr("stroke-opacity", (wire) => (wire.heavy ? 0.7 : 0.55))
    .attr("stroke-width", (wire) => (wire.heavy ? 2.5 : 1.25))
    .attr("stroke-dasharray", (wire) => (wire.dashed ? "4 3" : null));
  joined.transition().duration(moves).attr("d", (wire) => wire.d);
}

export function drawDots(scene: CanvasScene, dots: readonly PlacedDot[], moves: number): void {
  const joined = scene.layers.dots.selectAll<SVGGElement, PlacedDot>("g.dot").data(dots, (dot) => dot.id);
  joined.exit().transition().duration(moves / 2).style("opacity", 0).remove();
  const entered = joined.enter().append("g")
    .attr("class", `dot ${LIT_GROUP} cursor-pointer`)
    .attr("transform", (dot) => `translate(${dot.x},${dot.y})`)
    .style("opacity", 0);
  entered.transition().duration(moves).style("opacity", null);
  entered.append("circle").attr("r", 12).attr("fill", "transparent");
  entered.append("circle").attr("class", "spot").attr("r", 4.5).attr("stroke-width", 2);
  joined.transition().duration(moves).attr("transform", (dot) => `translate(${dot.x},${dot.y})`);
  const all = entered.merge(joined);
  all.attr("data-testid", (dot) => `mapping-overview-dot-${dot.id.replace("\u0000", "-")}`);
  all.select("circle.spot").attr("class", (dot) => `spot stroke-background ${toneOf(kindOf(scene, dot.source)).fill}`);
  wire(scene, all, (dot) => ({ type: "row", path: dot.row }));
  menued(scene, all, (dot) => ({ type: "row", path: dot.row }), false);
  tipped(scene, all, (dot) => dotTip(scene, dot));
}

/** What a dot says: which table is read, by what, for what. */
function dotTip(scene: CanvasScene, dot: PlacedDot): { title: string; body: string } | null {
  const card = scene.layout()?.cards.find((candidate) => candidate.card.id === dot.source);
  const row = scene.layout()?.rows.find((candidate) => candidate.row.path === dot.row);
  if (card === undefined || row === undefined) {
    return null;
  }

  if (card.card.source.kind === "parameters") {
    return { title: "From the flow", body: row.row.reads.filter((read) => read.source === dot.source).map((read) => read.field).join(", ") };
  }

  const lookup = row.row.lookups.find((candidate) => candidate.source === dot.source);
  const by = lookup === undefined || lookup.matches.length === 0
    ? "by the value"
    : `where ${lookup.matches.map((match, index) => `${index === 0 ? "" : match.and === true ? " and " : ", else "}${match.field} = ${operandOf(row.row.reads, match.value).text}`).join("")}`;
  const read = lookup?.field ?? "what it holds for it";
  return { title: card.card.title, body: `Finds the row ${by}, and reads ${read}.` };
}
