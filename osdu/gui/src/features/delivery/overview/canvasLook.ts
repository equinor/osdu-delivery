// How the overview's canvas looks and moves: the tones of what is read from the row and what is looked up, the glyphs its
// tables, fields and rows carry, the text classes, how a part takes the light, the timings of a move and of pointing, and
// the words a field and a row are shown and named by (osdu/docs/reference/concepts/mapping-overview.md).

import type { BaseType, Selection } from "d3-selection";
import type { CardField } from "./outlineModel";
import type { PlacedCard, PlacedRow } from "./outlineLayout";

export interface Tone {
  fill: string;
  stroke: string;
  ink: string;
}

const NEUTRAL: Tone = { fill: "fill-muted-foreground", stroke: "stroke-muted-foreground", ink: "text-muted-foreground" };

// Two tones with one meaning each: blue comes from the row, orange is looked up, whether in the cache or by a search, which their
// glyphs and captions tell apart. The dataviz validator holds this pair: on the dark card chart-1 beside chart-7 falls under its
// floor for telling hues apart, and chart-1 beside chart-6 clears it twice over, under every colour blindness too.
const FROM_THE_ROW: Tone = { fill: "fill-chart-1", stroke: "stroke-chart-1", ink: "text-chart-1" };
const LOOKED_UP: Tone = { fill: "fill-chart-6", stroke: "stroke-chart-6", ink: "text-chart-6" };
const TONES: Record<string, Tone> = { record: FROM_THE_ROW, child: FROM_THE_ROW, cache: LOOKED_UP, search: LOOKED_UP };

export const toneOf = (kind: string): Tone => TONES[kind] ?? NEUTRAL;

// Lucide's outlines, as the rest of the workbench draws them. An SVG canvas takes paths rather than components.
const GLYPHS = {
  table: ["M3 9h18", "M9 3v18", "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z"],
  cache: ["M3 5a9 3 0 1 0 18 0a9 3 0 1 0-18 0", "M3 5V19A9 3 0 0 0 21 19V5", "M3 12A9 3 0 0 0 21 12"],
  search: ["m21 21-4.34-4.34", "M19 11a8 8 0 1 1-16 0a8 8 0 1 1 16 0"],
  parameters: ["M20 7h-9", "M14 17H5", "M17 17a3 3 0 1 0 6 0a3 3 0 1 0-6 0", "M1 7a3 3 0 1 0 6 0a3 3 0 1 0-6 0"],
  link: ["M9 17H7A5 5 0 0 1 7 7h2", "M15 7h2a5 5 0 1 1 0 10h-2", "M8 12h8"],
  key: ["M2.586 17.414A2 2 0 0 0 2 18.828V21a1 1 0 0 0 1 1h3a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h1a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h.172a2 2 0 0 0 1.414-.586l.814-.814a6.5 6.5 0 1 0-4-4z", "M16.5 7.5h.01"],
  chevron: ["m9 18 6-6-6-6"],
  repeat: ["m17 2 4 4-4 4", "M3 11v-1a4 4 0 0 1 4-4h14", "m7 22-4-4 4-4", "M21 13v1a4 4 0 0 1-4 4H3"],
  gate: ["M10 20a1 1 0 0 0 .553.895l2 1A1 1 0 0 0 14 21v-7a2 2 0 0 1 .517-1.341L21.74 4.67A1 1 0 0 0 21 3H3a1 1 0 0 0-.742 1.67l7.225 7.989A2 2 0 0 1 10 14z"],
  rule: ["M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z", "m9 12 2 2 4-4"],
  alert: ["m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3", "M12 9v4", "M12 17h.01"],
} as const;

export const SOURCE_GLYPH: Record<string, keyof typeof GLYPHS> = {
  record: "table",
  child: "table",
  cache: "cache",
  search: "search",
  parameters: "parameters",
};

export type Glyph = keyof typeof GLYPHS;

export const reducedMotion = () => window.matchMedia("(prefers-reduced-motion: reduce)").matches;

/** Moves (DESIGN.md 9): quick, no bounce, and interrupted by the next one. */
export const MOVE_MS = 180;

/** The share of a glyph's 24 px box a size in px is. */
const scaleOf = (size: number) => size / 24;

export function drawGlyph(parent: Selection<SVGGElement, unknown, BaseType, unknown>, glyph: Glyph, size: number, ink: string): void {
  const group = parent.append("g").attr("transform", `scale(${scaleOf(size)})`).attr("class", ink).attr("aria-hidden", "true");
  for (const d of GLYPHS[glyph]) {
    group.append("path").attr("d", d).attr("fill", "none").attr("stroke", "currentColor").attr("stroke-width", 2).attr("stroke-linecap", "round").attr("stroke-linejoin", "round");
  }
}

export const TEXT = {
  value: "font-mono text-[11px] fill-foreground",
  faint: "font-mono text-[11px] fill-muted-foreground/70",
  name: "font-mono text-[12px] fill-foreground",
  nameStrong: "font-mono text-[12px] font-medium fill-foreground",
  title: "font-mono text-[12.5px] font-semibold fill-foreground",
  quiet: "font-mono text-[11px] fill-muted-foreground",
  note: "font-sans text-[11px] fill-muted-foreground",
};

/** How a part of the canvas takes the light, as classes: lit, or faded while something else is lit. */
export const WIRE = "fill-none transition-[stroke,opacity,stroke-width] duration-[120ms] data-[lit=on]:stroke-primary data-[lit=on]:[stroke-opacity:1] data-[lit=on]:stroke-[1.75] data-[lit=off]:opacity-25";
export const LIT_GROUP = "transition-opacity duration-[120ms] data-[lit=off]:opacity-45";

/** How long the pointer rests on a line before it counts: the shelf's lines cross every row, and passing over one lights nothing. */
export const SETTLE_MS = 70;

/** How long a part stays lit after the pointer leaves it, so moving from one part to the next does not blink. */
export const LEAVE_MS = 40;

/** The width a line takes the pointer in, wider than it is drawn. */
export const EDGE_HIT = 9;

/** A field as its table shows it: a searched record's path by its last step, which is what tells two of them apart. */
export function shownName(card: PlacedCard, field: CardField): string {
  return card.card.source.kind === "search" ? field.name.slice(field.name.lastIndexOf(".") + 1) : field.name;
}

/** A catalog type as it reads at a field's end: its name, without what only the database needs. */
export function shortType(type: string | null): string {
  return type === null ? "" : type.replace(/\s*\(.*\)$/, "").toLowerCase();
}

/** A row as its label reads to a screen reader: its path, and what it holds or how its value is made. */
export function labelOf(placed: PlacedRow): string {
  const { row } = placed;
  const how = row.folding ? `${row.folded} ${row.folded === 1 ? "property" : "properties"}` : row.node.flow?.summary ?? "";
  return `${row.path}${how === "" ? "" : `: ${how}`}`;
}
