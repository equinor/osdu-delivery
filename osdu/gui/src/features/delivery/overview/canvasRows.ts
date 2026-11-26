// The rows of the overview's canvas: a row per property of the record, with its track from the table it reads, its guides,
// chevron, name, flags, value, pills and fixed value, and the keys a row answers when it has the focus.

import { select, type BaseType, type Selection } from "d3-selection";
import "d3-transition";
import { drawGlyph, labelOf, LIT_GROUP, TEXT, toneOf, WIRE, type Glyph } from "./canvasLook";
import { kindOf, menued, tipped, wire, type CanvasScene } from "./canvasScene";
import { fitText, fitValue, MEASURE, pillText, textWidth, type OutlineLayout, type PlacedPill, type PlacedRow } from "./outlineLayout";
import type { OverviewDress } from "./OverviewCanvas";

export function drawRows(scene: CanvasScene, layout: OutlineLayout, dress: OverviewDress, moves: number): void {
  const { record } = layout;
  const tracks = scene.layers.tracks.selectAll<SVGLineElement, PlacedRow>("line").data(layout.rows.filter((row) => row.lineFrom !== null), (row) => row.row.path);
  tracks.exit().transition().duration(moves / 2).style("opacity", 0).remove();
  const enteredTracks = tracks.enter().append("line")
    .attr("x1", (row) => row.lineFrom ?? 0)
    .attr("y1", (row) => row.y)
    .attr("y2", (row) => row.y)
    .style("opacity", 0);
  enteredTracks.transition().duration(moves).style("opacity", null);
  enteredTracks.merge(tracks)
    .attr("class", (row) => `${WIRE} ${toneOf(kindOf(scene, row.from ?? "")).stroke}`)
    .attr("stroke-opacity", 0.55)
    .attr("stroke-width", 1.25)
    .attr("x2", record.x)
    .attr("stroke-dasharray", (row) => (row.row.literal !== null ? "2 3" : null));
  tracks.transition().duration(moves).attr("x1", (row) => row.lineFrom ?? 0).attr("y1", (row) => row.y).attr("y2", (row) => row.y);

  const joined = scene.layers.rows.selectAll<SVGGElement, PlacedRow>("g.row").data(layout.rows, (row) => row.row.path);
  joined.exit().transition().duration(moves / 2).style("opacity", 0).remove();
  const entered = joined.enter().append("g")
    .attr("class", `row ${LIT_GROUP} group/row outline-none`)
    .attr("transform", (row) => `translate(0,${row.y})`)
    .attr("tabindex", 0)
    .attr("role", "button")
    .style("opacity", 0);
  entered.transition().duration(moves).style("opacity", null);
  entered.append("rect").attr("class", "line-hit cursor-pointer").attr("fill", "transparent").attr("y", -MEASURE.row / 2).attr("height", MEASURE.row);
  entered.append("rect").attr("class", "shade fill-accent opacity-0 transition-opacity duration-[120ms] group-hover/row:opacity-100 group-focus-visible/row:opacity-100 group-data-[picked=true]/row:opacity-100 group-data-[found=true]/row:opacity-100").attr("y", -MEASURE.row / 2 + 1).attr("height", MEASURE.row - 2).attr("rx", 3);
  entered.append("g").attr("class", "guides");
  entered.append("circle").attr("class", "port").attr("r", 3);
  entered.append("g").attr("class", "chevron cursor-pointer");
  entered.append("text").attr("class", "name").attr("y", 4);
  entered.append("g").attr("class", "flags");
  entered.append("text").attr("class", `end ${TEXT.faint}`).attr("y", 3.5).attr("text-anchor", "end");
  entered.append("g").attr("class", "pills");
  const literal = entered.append("g").attr("class", "literal");
  literal.append("rect").attr("class", "fill-card stroke-border").attr("y", -MEASURE.pillHeight / 2).attr("height", MEASURE.pillHeight).attr("rx", 3).attr("stroke-dasharray", "2 2");
  literal.append("text").attr("class", TEXT.quiet).attr("y", 3.5);

  const all = entered.merge(joined);
  all.attr("data-testid", (row) => `mapping-overview-row-${row.row.path}`)
    .attr("aria-expanded", (row) => (row.row.folding ? String(row.row.open) : null))
    .attr("aria-label", (row) => labelOf(row));
  joined.transition().duration(moves).attr("transform", (row) => `translate(0,${row.y})`);
  all.select("rect.line-hit").attr("x", (row) => row.lineFrom ?? record.x).attr("width", (row) => record.x + record.width - (row.lineFrom ?? record.x));
  all.select("rect.shade").attr("x", record.x + 3).attr("width", record.width - 6);
  all.select<SVGCircleElement>("circle.port")
    .attr("cx", record.x)
    .attr("stroke-width", 1.5)
    .attr("class", (row) => (row.row.node.required ? "port fill-muted-foreground stroke-card" : "port fill-card stroke-muted-foreground"));

  all.each((row, index, nodes) => {
    drawRowParts(scene, select<SVGGElement, PlacedRow>(nodes[index]), row, record, dress, moves);
  });

  wire(scene, all, (row) => ({ type: "row", path: row.row.path }));
  all.on("focus.reveal", (_event, row) => scene.reveal(row.row.path));
  all.on("click", (event: MouseEvent, row) => {
    event.stopPropagation();
    // The second click of a double click leaves a section it opened open: the double click opens its details.
    if (event.detail > 1) {
      return;
    }

    if (row.row.folding && row.row.node.shape !== "choice") {
      scene.handlers.toggle(row.row.path);
    } else {
      scene.handlers.pick({ type: "row", path: row.row.path });
    }
  });
  all.on("keydown", (event: KeyboardEvent, row) => onRowKey(scene, event, row));
  menued(scene, all, (row) => ({ type: "row", path: row.row.path }), false);
}

function onRowKey(scene: CanvasScene, event: KeyboardEvent, row: PlacedRow): void {
  const stop = () => {
    event.preventDefault();
    event.stopPropagation();
  };

  const move = (step: 1 | -1) => {
    const rows = scene.layers.rows.selectAll<SVGGElement, PlacedRow>("g.row");
    const ordered = rows.nodes().sort((a, b) => select<SVGGElement, PlacedRow>(a).datum().y - select<SVGGElement, PlacedRow>(b).datum().y);
    const at = ordered.indexOf(event.currentTarget as SVGGElement);
    ordered[at + step]?.focus();
  };

  switch (event.key) {
    case "ContextMenu":
    case "F10": {
      if (event.key === "F10" && !event.shiftKey) {
        break;
      }

      stop();
      const box = (event.currentTarget as Element).getBoundingClientRect();
      scene.handlers.menu({ type: "row", path: row.row.path }, { x: box.left + 24, y: box.bottom });
      break;
    }
    case " ":
      stop();
      if (row.row.folding && row.row.node.shape !== "choice") {
        scene.handlers.toggle(row.row.path);
      } else {
        scene.handlers.pick({ type: "row", path: row.row.path });
      }

      break;
    case "Enter":
      stop();
      scene.handlers.open({ type: "row", path: row.row.path });
      break;
    case "ArrowRight":
      stop();
      if (row.row.folding && !row.row.open) {
        scene.handlers.toggle(row.row.path);
      }

      break;
    case "ArrowLeft":
      stop();
      if (row.row.folding && row.row.open) {
        scene.handlers.toggle(row.row.path);
      }

      break;
    case "ArrowDown":
      stop();
      move(1);
      break;
    case "ArrowUp":
      stop();
      move(-1);
      break;
  }
}

function drawRowParts(scene: CanvasScene, group: Selection<SVGGElement, PlacedRow, null, undefined>, placed: PlacedRow, record: OutlineLayout["record"], dress: OverviewDress, moves: number): void {
  const { row } = placed;
  const nameX = record.x + 14 + row.depth * MEASURE.indent + (row.folding ? 16 : 16);

  // A nested row's guides: one line per level above it, as the workbench's trees draw them (DESIGN.md 7.2a).
  const guides = group.select<SVGGElement>("g.guides").selectAll<SVGLineElement, number>("line").data(Array.from({ length: row.depth }, (_, depth) => depth));
  guides.exit().remove();
  guides.enter().append("line").attr("class", "stroke-border").merge(guides)
    .attr("x1", (depth) => record.x + 21 + depth * MEASURE.indent)
    .attr("x2", (depth) => record.x + 21 + depth * MEASURE.indent)
    .attr("y1", -MEASURE.row / 2)
    .attr("y2", MEASURE.row / 2);

  const chevron = group.select<SVGGElement>("g.chevron");
  chevron.attr("display", row.folding ? null : "none");
  if (row.folding && chevron.selectAll("g").empty()) {
    chevron.append("rect").attr("x", -6).attr("y", -10).attr("width", 26).attr("height", 20).attr("fill", "transparent");
    const turn = chevron.append("g").attr("class", "turn");
    drawGlyph(turn as unknown as Selection<SVGGElement, unknown, BaseType, unknown>, "chevron", 14, "text-muted-foreground");
    chevron.on("click", (event: MouseEvent) => {
      event.stopPropagation();
      scene.handlers.toggle(row.path);
    });
    // A chevron turns on each click; a double click on it opens no details.
    chevron.on("dblclick", (event: MouseEvent) => event.stopPropagation());
  }

  chevron.attr("transform", `translate(${nameX - 16},0)`);
  chevron.select("g.turn")
    .transition().duration(moves)
    .attr("transform", row.open ? "translate(14,-7) rotate(90)" : "translate(0,-7)");

  const end = dress.values.get(row.path) ?? row.node.type ?? "";
  const shown = dress.values.has(row.path);
  const endText = row.open ? "" : shown ? fitValue(end, record.width * 0.46, 11) : fitText(end, 84, 11);
  group.select<SVGTextElement>("text.end")
    .attr("x", record.x + record.width - 12)
    .attr("class", `end ${shown ? TEXT.value : TEXT.faint}`)
    .text(endText);

  const flags: Glyph[] = [];
  if (row.node.assertions.length > 0) {
    flags.push("rule");
  }

  const mark = dress.marks.get(row.path);
  if (mark !== undefined) {
    flags.push("alert");
  }

  const flagsWidth = flags.length * 18;
  const room = record.x + record.width - 12 - textWidth(endText, 11) - 8 - flagsWidth - nameX;
  const suffix = row.node.shape === "repeat" ? "[]" : "";
  const counted = row.folded > 1 ? ` ${row.folded}` : "";
  const name = fitText(`${row.name}${suffix}`, room - textWidth(counted, 11), 12);
  const text = group.select<SVGTextElement>("text.name")
    .attr("x", nameX)
    .attr("class", `name ${row.folding ? TEXT.nameStrong : TEXT.name}`);
  text.selectAll("tspan").remove();
  text.text(null);
  text.append("tspan").text(name);
  if (counted !== "") {
    text.append("tspan").attr("class", "fill-muted-foreground font-normal text-[11px]").text(counted);
  }

  const flagsAt = group.select<SVGGElement>("g.flags");
  flagsAt.selectAll("*").remove();
  flags.forEach((flag, index) => {
    const holder = flagsAt.append("g")
      .attr("transform", `translate(${nameX + textWidth(name + counted, 12) + 6 + index * 18},-7)`) as unknown as Selection<SVGGElement, unknown, BaseType, unknown>;
    const ink = flag === "alert" ? (mark?.tone === "error" ? "text-destructive" : "text-warning") : "text-muted-foreground";
    drawGlyph(holder, flag, 14, ink);
    tipped(scene, holder, () => (flag === "alert" ? { title: "Needs a look", body: mark?.text ?? "" } : { title: "Rules on this value", body: row.node.assertions.map((rule) => `${rule.label}: ${rule.expected}`).join("\n") }));
  });

  drawPills(scene, group, placed, moves);

  const literal = group.select<SVGGElement>("g.literal");
  literal.attr("display", placed.literal === null ? "none" : null);
  if (placed.literal !== null) {
    literal.select("rect").attr("x", placed.literal.x).attr("width", placed.literal.width);
    literal.select("text").attr("x", placed.literal.x + 6).text(placed.literal.text);
  }
}

function drawPills(scene: CanvasScene, group: Selection<SVGGElement, PlacedRow, null, undefined>, placed: PlacedRow, moves: number): void {
  const parent = group.select<SVGGElement>("g.pills");
  const joined = parent.selectAll<SVGGElement, PlacedPill>("g.pill").data(placed.pills, (pill) => pill.id);
  joined.exit().remove();
  const entered = joined.enter().append("g").attr("class", "pill").style("opacity", 0).attr("transform", (pill) => `translate(${pill.x},${-MEASURE.pillHeight / 2})`);
  entered.transition().duration(moves).style("opacity", null);
  entered.append("rect").attr("height", MEASURE.pillHeight).attr("rx", 3);
  entered.append("text").attr("class", TEXT.quiet).attr("x", 6).attr("y", 12.5);
  const all = entered.merge(joined);
  joined.attr("transform", (pill) => `translate(${pill.x},${-MEASURE.pillHeight / 2})`);
  all.select("rect")
    .attr("width", (pill) => pill.width)
    .attr("class", (pill) => (pill.pill.gate ? "fill-card stroke-muted-foreground" : "fill-muted stroke-border"))
    .attr("stroke-dasharray", (pill) => (pill.pill.gate ? "3 2" : null));
  all.select("text").text((pill) => pillText(pill.pill));
  tipped(scene, all, (pill) => (pill.pill.detail === "" ? null : { title: pill.pill.text === "" ? pillText(pill.pill) : pill.pill.text, body: pill.pill.detail }));
}
