// The cards of the overview's canvas: the record's own card with its heading, and a card per table the mapping reads, with
// its heading, its fields and what each field shows at its end.

import { select, type BaseType, type Selection } from "d3-selection";
import "d3-transition";
import { drawGlyph, LIT_GROUP, shortType, shownName, SOURCE_GLYPH, TEXT, toneOf } from "./canvasLook";
import { menued, tipped, wire, type CanvasScene } from "./canvasScene";
import { lookupOrigin, type CardField } from "./outlineModel";
import { fitText, fitValue, MEASURE, textWidth, type OutlineLayout, type PlacedCard, type PlacedField } from "./outlineLayout";
import type { OverviewDress } from "./OverviewCanvas";

export function drawRecord(scene: CanvasScene, layout: OutlineLayout, dress: OverviewDress, moves: number): void {
  const { record } = layout;
  const layer = scene.layers.record;
  let card = layer.select<SVGGElement>("g.record-card");
  if (card.empty()) {
    card = layer.append("g").attr("class", "record-card outline-none [&:focus-visible>rect.frame]:stroke-ring [&:focus-visible>rect.frame]:[stroke-width:2]").attr("tabindex", 0).attr("role", "button");
    card.append("rect").attr("class", "frame fill-card stroke-border").attr("rx", 8);
    card.append("line").attr("class", "edge stroke-primary").attr("stroke-width", 2).attr("x1", 8).attr("y1", 0.75);
    card.append("line").attr("class", "rule stroke-border").attr("x1", 0).attr("y1", MEASURE.head).attr("y2", MEASURE.head);
    card.append("text").attr("class", `title ${TEXT.title}`).attr("x", 14).attr("y", 18);
    card.append("text").attr("class", `sub ${TEXT.note}`).attr("x", 14).attr("y", 34);
    card.append("text").attr("class", `left-out ${TEXT.note}`).attr("text-anchor", "end").attr("y", 34).attr("data-testid", "mapping-overview-left-out");
  }

  card.attr("transform", `translate(${record.x},${record.y})`);
  card.select("rect.frame").attr("width", record.width).transition().duration(moves).attr("height", record.height);
  card.select("line.edge").attr("x2", record.width - 8);
  card.select("line.rule").attr("x2", record.width);
  card.select("text.title").text(fitText(dress.title, record.width - 28, 12.5));
  card.select("text.sub").text(fitText(dress.subtitle, record.width - 28, 10));
  card.select("text.left-out")
    .attr("x", record.width - 14)
    .attr("class", `left-out cursor-pointer ${TEXT.note} hover:fill-foreground`)
    .text(dress.unfilled === null || dress.unfilled === 0 ? "" : `${dress.unfilled.toLocaleString("en-US")} template properties unfilled`)
    .on("click", (event: MouseEvent) => {
      event.stopPropagation();
      scene.handlers.leftOut();
    });
  card.attr("aria-label", `${dress.title}: the record. The menu key opens what can be done with it.`);
  menued(scene, card, () => ({ type: "record" }), true);
}

export function drawCards(scene: CanvasScene, cards: readonly PlacedCard[], dress: OverviewDress, moves: number): void {
  const joined = scene.layers.cards.selectAll<SVGGElement, PlacedCard>("g.card").data(cards, (card) => card.card.id);
  joined.exit().remove();
  const entered = joined.enter().append("g").attr("class", `card ${LIT_GROUP}`).attr("transform", (card) => `translate(${card.x},${card.y})`);
  entered.append("rect").attr("class", "frame fill-card stroke-border").attr("rx", 8);
  entered.append("line").attr("class", "edge").attr("stroke-width", 2).attr("x1", 8).attr("y1", 0.75);
  const head = entered.append("g").attr("class", "head cursor-pointer outline-none").attr("tabindex", 0).attr("role", "button");
  head.append("rect").attr("class", "hit").attr("fill", "transparent").attr("height", MEASURE.head);
  head.append("g").attr("class", "glyph").attr("transform", "translate(11,7)");
  head.append("text").attr("class", `title ${TEXT.title}`).attr("x", 31).attr("y", 18);
  head.append("text").attr("class", `sub ${TEXT.note}`).attr("x", 12).attr("y", 34);
  entered.append("line").attr("class", "rule stroke-border").attr("x1", 0).attr("y1", MEASURE.head).attr("y2", MEASURE.head);
  entered.append("g").attr("class", "fields");
  entered.append("text").attr("class", `foot cursor-pointer ${TEXT.note} hover:fill-foreground`).attr("x", 12);

  const all = entered.merge(joined);
  all.attr("data-testid", (card) => `mapping-overview-card-${card.card.id}`);
  all.transition().duration(moves).attr("transform", (card) => `translate(${card.x},${card.y})`);
  all.select("rect.frame").attr("width", (card) => card.width).transition().duration(moves).attr("height", (card) => card.height);
  all.select<SVGLineElement>("line.edge")
    .attr("class", (card) => `edge ${toneOf(card.card.source.kind).stroke}`)
    .attr("x2", (card) => card.width - 8);
  all.select("line.rule")
    .attr("x2", (card) => card.width)
    .attr("display", (card) => (card.card.folded ? "none" : null));
  all.select("rect.hit").attr("width", (card) => card.width);
  all.select<SVGGElement>("g.head").attr("aria-label", (card) => `${card.card.title}: ${card.card.role}`);
  entered.each(function drawGlyphOf(card) {
    drawGlyph(select<SVGGElement, unknown>(this).select<SVGGElement>("g.glyph"), SOURCE_GLYPH[card.card.source.kind] ?? "link", 15, toneOf(card.card.source.kind).ink);
  });
  all.select("text.title").text((card) => fitText(card.card.title, card.width - 44, 12.5));
  all.select("text.sub").text((card) => fitText(subtitleOf(card), card.width - 24, 8.8));
  all.select("text.foot")
    .attr("y", (card) => card.height - 9)
    .attr("display", (card) => (card.card.unread.length > 0 && !card.card.folded ? null : "none"))
    .text((card) => `${card.card.unread.length} ${card.card.unread.length === 1 ? "column" : "columns"} not read`)
    .on("click", (event: MouseEvent, card) => {
      // The words ask for what the table's details list, so one click opens them.
      event.stopPropagation();
      scene.handlers.open({ type: "card", source: card.card.id });
    });

  wire(scene, all.select<SVGGElement>("g.head"), (card) => ({ type: "card", source: card.card.id }));
  all.select<SVGGElement>("g.head").on("click", (event: MouseEvent, card) => {
    event.stopPropagation();
    // The second click of a double click leaves a table it opened open: the double click opens its details.
    if (event.detail > 1) {
      return;
    }

    if (card.card.repeater !== null) {
      scene.handlers.toggle(card.card.repeater);
    } else {
      scene.handlers.pick({ type: "card", source: card.card.id });
    }
  });
  // Anywhere on a table opens its menu, a field its own; the heading answers the keys.
  menued(scene, all, (card) => ({ type: "card", source: card.card.id }), false);
  menued(scene, all.select<SVGGElement>("g.head"), (card) => ({ type: "card", source: card.card.id }), true);

  all.each((card, index, nodes) => {
    drawFields(scene, select<SVGGElement, PlacedCard>(nodes[index]).select<SVGGElement>("g.fields"), card, dress, moves);
  });
}

/** What a table's second line says: what a row of it is, and for one folded how much it holds. */
function subtitleOf(card: PlacedCard): string {
  const { card: table } = card;
  const place = table.subtitle === null ? "" : ` · ${table.subtitle}`;
  return table.folded ? `${table.role} · ${table.fields.length} columns` : `${table.role}${place}`;
}

function drawFields(scene: CanvasScene, parent: Selection<SVGGElement, PlacedCard, BaseType, unknown>, card: PlacedCard, dress: OverviewDress, moves: number): void {
  const pitch = card.shelf ? MEASURE.shelfField : MEASURE.field;
  const fields = parent.selectAll<SVGGElement, PlacedField>("g.field").data(card.fields, (field) => field.field.id);
  fields.exit().transition().duration(moves / 2).style("opacity", 0).remove();
  const entered = fields.enter().append("g")
    .attr("class", `field ${LIT_GROUP} group/field cursor-pointer outline-none`)
    .attr("tabindex", 0)
    .attr("role", "button")
    .style("opacity", 0);
  entered.transition().duration(moves).style("opacity", null);
  entered.append("rect").attr("class", "shade fill-accent opacity-0 transition-opacity duration-[120ms] group-data-[found=true]/field:opacity-100").attr("x", 1).attr("y", -pitch / 2).attr("height", pitch);
  entered.append("g").attr("class", "mark").attr("transform", `translate(12,${-6.5})`);
  entered.append("text").attr("class", `name ${TEXT.name}`).attr("y", 4);
  entered.append("text").attr("class", `end ${TEXT.faint}`).attr("y", 3.5).attr("text-anchor", "end");

  const all = entered.merge(fields);
  all.attr("transform", (field) => `translate(0,${card.shelf ? field.y : field.y - card.y})`);
  all.attr("data-testid", (field) => `mapping-overview-field-${field.field.id.replace("\u0000", "-")}`);
  all.attr("aria-label", (field) => `${field.field.name}${field.field.key ? ", key" : ""}${field.field.lookup === null ? "" : `, from lookup ${field.field.lookup.name}`}`);
  all.select("rect.shade").attr("width", card.width - 2);
  entered.each(function drawMark(field) {
    if (field.field.key) {
      drawGlyph(select<SVGGElement, unknown>(this).select<SVGGElement>("g.mark"), "key", 13, "text-muted-foreground");
    } else if (field.field.lookup !== null) {
      // A column a lookup table gives the row, joined into the read rather than held by the table itself.
      drawGlyph(select<SVGGElement, unknown>(this).select<SVGGElement>("g.mark"), "link", 13, "text-muted-foreground");
    }
  });
  all.select("text.name")
    .attr("x", (field) => (field.field.key || field.field.lookup !== null ? 31 : 12))
    .attr("class", (field) => `name font-mono text-[12px] ${field.field.displayOnly ? "fill-muted-foreground" : "fill-foreground"}`)
    .text((field) => fitText(shownName(card, field.field), nameRoom(card, field.field, dress), 12));
  all.select("text.end")
    .attr("x", card.width - 10)
    .attr("class", (field) => `end ${dress.values.has(field.field.id) ? TEXT.value : TEXT.faint}`)
    .text((field) => endOf(card, field.field, dress));
  wire(scene, all, (field) => ({ type: "field", source: card.card.id, field: field.field.name }));
  menued(scene, all, (field) => ({ type: "field", source: card.card.id, field: field.field.name }), true);
  tipped(scene, all, (field) => {
    const value = dress.values.get(field.field.id);
    const shown = fitText(shownName(card, field.field), nameRoom(card, field.field, dress), 12);
    if (field.field.lookup !== null) {
      return { title: field.field.name, body: value === undefined ? lookupOrigin(field.field.lookup) : `${value}\n${lookupOrigin(field.field.lookup)}` };
    }

    return value === undefined && shown === field.field.name ? null : { title: field.field.name, body: value ?? "" };
  });
}

/** What stands at the end of a field: its value in the record shown, else its type, else what a lookup does with it. */
function endOf(card: PlacedCard, field: CardField, dress: OverviewDress): string {
  const value = dress.values.get(field.id);
  if (value !== undefined) {
    return fitValue(value, card.width * 0.42, 11);
  }

  // A table is read unless it says otherwise, so only what a lookup compares is marked.
  if (card.shelf) {
    return field.match && !field.read ? "found by" : "";
  }

  return fitText(shortType(field.type), 84, 11);
}

function nameRoom(card: PlacedCard, field: CardField, dress: OverviewDress): number {
  const end = textWidth(endOf(card, field, dress), 11);
  return card.width - (field.key || field.lookup !== null ? 31 : 12) - end - 18;
}
