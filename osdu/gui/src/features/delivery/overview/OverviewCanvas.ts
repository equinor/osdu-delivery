// The overview's canvas, drawn by D3: what outlineLayout placed, joined to SVG so a row opening moves things rather than
// redrawing them, with pan and zoom on the viewport. It owns its DOM and holds no React state: the page hands it a layout and
// what is lit, and hears back what the reader points at (osdu/docs/reference/concepts/mapping-overview.md).

import { select, type BaseType, type Selection } from "d3-selection";
import "d3-transition";
import { zoom, zoomIdentity, zoomTransform, type D3ZoomEvent, type ZoomBehavior, type ZoomTransform } from "d3-zoom";
import { operandOf, type CardField, type OutlineFound, type OutlineLight } from "./outlineModel";
import {
  boundsOf, fitText, fitValue, framingOf, MEASURE, pillText, textWidth, ZOOM, type OutlineLayout, type PlacedCard, type PlacedDot, type PlacedDrop, type PlacedField,
  type PlacedPill, type PlacedRow, type PlacedWire,
} from "./outlineLayout";

/** Something on the canvas a reader can point at. */
export type OverviewPart =
  | { type: "row"; path: string }
  | { type: "field"; source: string; field: string }
  | { type: "card"; source: string };

/** What a right click opens a menu for: a part, the record's heading, or the canvas itself. */
export type OverviewMenuTarget = OverviewPart | { type: "record" } | { type: "canvas" };

/** What a tooltip says, and where it points, in the page's coordinates. */
export interface OverviewTip {
  x: number;
  y: number;
  title: string;
  body: string;
}

export interface OverviewHandlers {
  point: (part: OverviewPart | null) => void;
  pick: (part: OverviewPart) => void;
  /** Opens or closes a node holding others. */
  toggle: (path: string) => void;
  tip: (tip: OverviewTip | null) => void;
  /** The reader clicked the canvas itself. */
  clear: () => void;
  /** The reader asked to see what the template holds that the mapping leaves out. */
  leftOut: () => void;
  /** The reader asked for the menu of something on the canvas, or of the canvas, at a place on the page. */
  menu: (target: OverviewMenuTarget, at: { x: number; y: number }) => void;
}

/** What is drawn besides the layout. */
export interface OverviewDress {
  /** The record's heading. */
  title: string;
  /** Its second line. */
  subtitle: string;
  /** A field's or a row's value in the record shown, by field id or path; empty when no record is shown. */
  values: ReadonlyMap<string, string>;
  /** The rows that need a look, by path. */
  marks: ReadonlyMap<string, { tone: "error" | "warning"; text: string }>;
  /** How many template properties nothing writes, or null when the template could not be read. */
  unfilled: number | null;
}

interface Tone {
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

const toneOf = (kind: string): Tone => TONES[kind] ?? NEUTRAL;

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

const SOURCE_GLYPH: Record<string, keyof typeof GLYPHS> = {
  record: "table",
  child: "table",
  cache: "cache",
  search: "search",
  parameters: "parameters",
};

type Glyph = keyof typeof GLYPHS;

const reducedMotion = () => window.matchMedia("(prefers-reduced-motion: reduce)").matches;

/** Moves (DESIGN.md 9): quick, no bounce, and interrupted by the next one. */
const MOVE_MS = 180;

/** The share of a glyph's 24 px box a size in px is. */
const scaleOf = (size: number) => size / 24;

function drawGlyph(parent: Selection<SVGGElement, unknown, BaseType, unknown>, glyph: Glyph, size: number, ink: string): void {
  const group = parent.append("g").attr("transform", `scale(${scaleOf(size)})`).attr("class", ink).attr("aria-hidden", "true");
  for (const d of GLYPHS[glyph]) {
    group.append("path").attr("d", d).attr("fill", "none").attr("stroke", "currentColor").attr("stroke-width", 2).attr("stroke-linecap", "round").attr("stroke-linejoin", "round");
  }
}

const TEXT = {
  value: "font-mono text-[11px] fill-foreground",
  faint: "font-mono text-[11px] fill-muted-foreground/70",
  name: "font-mono text-[12px] fill-foreground",
  nameStrong: "font-mono text-[12px] font-medium fill-foreground",
  title: "font-mono text-[12.5px] font-semibold fill-foreground",
  quiet: "font-mono text-[11px] fill-muted-foreground",
  note: "font-sans text-[11px] fill-muted-foreground",
};

/** How a part of the canvas takes the light, as classes: lit, or faded while something else is lit. */
const WIRE = "fill-none transition-[stroke,opacity,stroke-width] duration-[120ms] data-[lit=on]:stroke-primary data-[lit=on]:[stroke-opacity:1] data-[lit=on]:stroke-[1.75] data-[lit=off]:opacity-25";
const LIT_GROUP = "transition-opacity duration-[120ms] data-[lit=off]:opacity-45";

/** How long the pointer rests on a line before it counts: the shelf's lines cross every row, and passing over one lights nothing. */
const SETTLE_MS = 70;

/** How long a part stays lit after the pointer leaves it, so moving from one part to the next does not blink. */
const LEAVE_MS = 40;

/** The width a line takes the pointer in, wider than it is drawn. */
const EDGE_HIT = 9;

/** A line a reader can point at, and what pointing at it means. */
interface PlacedEdge {
  id: string;
  d: string;
  part: OverviewPart;
  tip: { title: string; body: string } | null;
}

export class OverviewCanvas {
  private readonly svg: Selection<SVGSVGElement, unknown, null, undefined>;

  private readonly viewport: Selection<SVGGElement, unknown, null, undefined>;

  private readonly layers: Record<"drops" | "wires" | "tracks" | "cards" | "record" | "rows" | "edges" | "dots", Selection<SVGGElement, unknown, null, undefined>>;

  private readonly zoomer: ZoomBehavior<SVGSVGElement, unknown>;

  /** Where the view is in a canvas taller than the window, as a scrollbar says it. */
  private readonly rail: Selection<SVGRectElement, unknown, null, undefined>;

  private readonly resize: ResizeObserver;

  private layout: OutlineLayout | null = null;

  /** Whether the view follows the window, which it does until the reader moves it. */
  private fitted = true;

  private scale = 1;

  /** Where the view is, or is on its way to. */
  private target: ZoomTransform = zoomIdentity;

  /** The room a panel over the canvas takes, which `fit` and `reveal` keep clear. */
  private inset = { left: 0, right: 0 };

  /**
   * What a search found, while the view frames it, and the view to go back to when the search ends, null for the fitted one.
   * The reader moving the view lets it go.
   */
  private framed: { found: OutlineFound; back: ZoomTransform | null } | null = null;

  /** What pointing is waiting to do. */
  private settling: ReturnType<typeof setTimeout> | undefined;

  constructor(private readonly element: SVGSVGElement, private readonly handlers: OverviewHandlers) {
    this.svg = select(element);
    this.svg.selectAll("*").remove();
    const defs = this.svg.append("defs");
    const grid = defs.append("pattern").attr("id", "mapping-overview-grid").attr("width", 24).attr("height", 24).attr("patternUnits", "userSpaceOnUse");
    grid.append("circle").attr("cx", 1).attr("cy", 1).attr("r", 0.75).attr("class", "fill-border");
    this.svg.append("rect").attr("width", "100%").attr("height", "100%").attr("fill", "url(#mapping-overview-grid)").attr("class", "opacity-60");
    this.viewport = this.svg.append("g");
    this.rail = this.svg.append("rect").attr("class", "fill-muted-foreground/40").attr("width", 4).attr("rx", 2).attr("pointer-events", "none");
    this.layers = {
      drops: this.viewport.append("g"),
      wires: this.viewport.append("g"),
      record: this.viewport.append("g"),
      tracks: this.viewport.append("g"),
      cards: this.viewport.append("g"),
      rows: this.viewport.append("g"),
      edges: this.viewport.append("g"),
      dots: this.viewport.append("g"),
    };

    this.zoomer = zoom<SVGSVGElement, unknown>()
      .scaleExtent([ZOOM.min, ZOOM.max])
      // A wheel moves the page, as it does everywhere else in the workbench; held with Ctrl, or pinched, it zooms.
      .filter((event: Event) => (event.type === "wheel" ? (event as WheelEvent).ctrlKey : (event as MouseEvent).button === 0))
      .on("zoom", (event: D3ZoomEvent<SVGSVGElement, unknown>) => {
        this.viewport.attr("transform", event.transform.toString());
        this.scale = event.transform.k;
        this.drawRail(event.transform.y, event.transform.k);
        if (event.sourceEvent !== null && event.sourceEvent !== undefined) {
          this.target = event.transform;
          this.fitted = false;
          this.framed = null;
          this.handlers.tip(null);
        }
      });
    this.svg.call(this.zoomer).on("dblclick.zoom", null);
    this.svg.on("wheel.pan", (event: WheelEvent) => {
      if (!event.ctrlKey) {
        event.preventDefault();
        this.fitted = false;
        this.framed = null;
        this.handlers.tip(null);
        this.zoomer.translateBy(this.svg, -event.deltaX / this.scale, -event.deltaY / this.scale);
        this.target = zoomTransform(element);
      }
    });
    this.svg.on("click.clear", (event: MouseEvent) => {
      const target = event.target as Element;
      if (target === element || (target.tagName === "rect" && target.parentNode === element)) {
        this.handlers.clear();
      }
    });
    // Whatever takes a right click of its own stops it; what reaches the canvas is the canvas's.
    this.svg.on("contextmenu.canvas", (event: MouseEvent) => {
      event.preventDefault();
      this.handlers.tip(null);
      this.handlers.menu({ type: "canvas" }, { x: event.clientX, y: event.clientY });
    });

    this.resize = new ResizeObserver(() => {
      if (this.fitted) {
        this.fit(false);
      } else if (this.framed !== null) {
        this.frame(false);
      }
    });
    this.resize.observe(element);
  }

  destroy(): void {
    clearTimeout(this.settling);
    this.resize.disconnect();
    this.svg.on(".zoom", null).on("wheel.pan", null).on("click.clear", null).on("contextmenu.canvas", null);
    this.svg.selectAll("*").remove();
  }

  /** Joins a layout to the canvas: what stays moves, what is new fades in, what went fades out. */
  render(layout: OutlineLayout, dress: OverviewDress): void {
    const first = this.layout === null;
    this.layout = layout;
    this.extent();
    const moves = first || reducedMotion() ? 0 : MOVE_MS;
    this.drawDrops(layout.drops, moves);
    this.drawWires(layout.wires, moves);
    this.drawRecord(layout, dress, moves);
    this.drawCards(layout.cards, dress, moves);
    this.drawRows(layout, dress, moves);
    this.drawEdges(layout);
    this.drawDots(layout.dots, moves);
    if (first || this.fitted) {
      this.fit(!first);
    }
  }

  private drawRail(y: number, scale: number): void {
    const layout = this.layout;
    if (layout === null) {
      return;
    }

    const { width, height } = this.view();
    const whole = layout.height * scale + 16;
    if (whole <= height + 8) {
      this.rail.attr("display", "none");
      return;
    }

    const length = Math.max(24, (height / whole) * (height - 8));
    const moved = Math.min(1, Math.max(0, (16 - y) / (whole - height)));
    this.rail.attr("display", null).attr("x", width - this.inset.right - 7).attr("y", 4 + moved * (height - 8 - length)).attr("height", length);
  }

  private extent(): void {
    const layout = this.layout;
    if (layout === null) {
      return;
    }

    // A panel over the canvas takes room the drawing may be moved under, at any scale, or a drag after a move that keeps the
    // panel's side clear would jump back.
    const margin = 240;
    const left = margin + this.inset.left / ZOOM.min;
    const right = margin + this.inset.right / ZOOM.min;
    this.zoomer.translateExtent([[-left, -margin], [layout.width + right, layout.height + margin]]);
  }

  private view(): { width: number; height: number } {
    const box = this.element.getBoundingClientRect();
    return { width: box.width, height: box.height };
  }

  /** Whatever a panel over the canvas takes, kept clear from now on. */
  setInset(left: number, right: number): void {
    this.inset = { left, right };
    this.extent();
    if (this.fitted) {
      this.fit(true);
    } else if (this.framed !== null) {
      this.frame(true);
    } else {
      this.drawRail(this.target.y, this.target.k);
    }
  }

  /** Shows the whole width, and as much of the height as reading the page at this size leaves, from the top. */
  fit(animate: boolean): void {
    const layout = this.layout;
    if (layout === null) {
      return;
    }

    this.fitted = true;
    this.framed = null;
    const { width, height } = this.view();
    if (width === 0 || height === 0) {
      return;
    }

    const pad = 16;
    const room = width - this.inset.left - this.inset.right - pad * 2;
    const wide = room / layout.width;
    const tall = (height - pad * 2) / layout.height;
    const scale = Math.max(0.5, Math.min(1, wide, Math.max(tall, 0.76)));
    const x = this.inset.left + pad + Math.max(0, (room - layout.width * scale) / 2);
    this.moveTo(zoomIdentity.translate(x, pad).scale(scale), animate);
  }

  private moveTo(transform: ZoomTransform, animate: boolean): void {
    this.target = transform;
    if (animate && !reducedMotion()) {
      this.svg.transition().duration(MOVE_MS).call(this.zoomer.transform, transform);
    } else {
      this.svg.call(this.zoomer.transform, transform);
    }
  }

  /** Zooms about the middle of the view. */
  zoomBy(factor: number): void {
    const { width, height } = this.view();
    const k = Math.max(ZOOM.min, Math.min(ZOOM.max, this.target.k * factor));
    const x = width / 2 - ((width / 2 - this.target.x) / this.target.k) * k;
    const y = height / 2 - ((height / 2 - this.target.y) / this.target.k) * k;
    this.fitted = false;
    this.framed = null;
    this.moveTo(zoomIdentity.translate(x, y).scale(k), true);
  }

  /**
   * Zooms to what a search found, close enough to read (`framingOf`), and keeps it framed as the window or a panel changes until
   * the reader moves the view. A search that finds nothing drawn leaves the view where it is.
   */
  focus(found: OutlineFound): void {
    const back = this.framed === null ? (this.fitted ? null : this.target) : this.framed.back;
    this.framed = { found, back };
    this.frame(true);
  }

  /** Goes back to the view the search started from, unless the reader has moved it since. */
  unfocus(): void {
    const framed = this.framed;
    this.framed = null;
    if (framed === null) {
      return;
    }

    if (framed.back === null) {
      this.fit(true);
    } else {
      this.moveTo(framed.back, true);
    }
  }

  private frame(animate: boolean): void {
    const layout = this.layout;
    const found = this.framed?.found;
    if (layout === null || found === undefined) {
      return;
    }

    const { width, height } = this.view();
    const rows = boundsOf(layout, found.rows, new Set());
    const whole = boundsOf(layout, found.rows, found.reads);
    if (width === 0 || height === 0 || rows === null || whole === null) {
      return;
    }

    // Held inside what a drag may reach, so the reader's first drag after it does not jump.
    const { x, y, k } = framingOf(rows, whole, { width, height }, this.inset);
    const held = this.zoomer.constrain()(zoomIdentity.translate(x, y).scale(k), [[0, 0], [width, height]], this.zoomer.translateExtent());
    this.fitted = false;
    this.moveTo(held, animate);
  }

  /** Brings a row of the record clear of any panel, without moving a view that already shows it. */
  reveal(path: string): void {
    const layout = this.layout;
    const row = layout?.rows.find((candidate) => candidate.row.path === path);
    if (layout === undefined || layout === null || row === undefined) {
      return;
    }

    const { width, height } = this.view();
    const { x, y: top, k } = this.target;
    const left = x + layout.record.x * k;
    const y = top + row.y * k;
    const right = left + layout.record.width * k;
    const shiftY = y < 40 ? 40 - y : y > height - 40 ? height - 40 - y : 0;
    const shiftX = right > width - this.inset.right ? width - this.inset.right - right - 16 : 0;
    if (shiftX !== 0 || shiftY !== 0) {
      this.fitted = false;
      this.moveTo(this.target.translate(shiftX / k, shiftY / k), true);
    }
  }

  /** Moves the view just far enough to show the rows from one path to another, the first of them whatever else gives way. */
  revealRows(first: string, last: string): void {
    const rows = this.layout?.rows;
    const from = rows?.find((candidate) => candidate.row.path === first);
    const to = rows?.find((candidate) => candidate.row.path === last);
    if (from === undefined || to === undefined) {
      return;
    }

    const { height } = this.view();
    const { y, k } = this.target;
    const top = y + from.y * k - MEASURE.row * k;
    const bottom = y + to.y * k + MEASURE.row * k;
    const shift = bottom > height ? Math.max(height - bottom, 8 - top) : top < 8 ? 8 - top : 0;
    if (shift !== 0) {
      this.fitted = false;
      this.moveTo(this.target.translate(0, shift / k), true);
    }
  }

  /**
   * Lights what `light` names and fades the rest, or leaves everything as it stands for null. What a search `found` is shaded
   * whatever is lit: the properties, and the columns it found by name.
   */
  setLight(light: OutlineLight | null, picked: OverviewPart | null, found: OutlineFound | null): void {
    const state = (on: boolean) => (light === null ? null : on ? "on" : "off");
    const pickedRow = picked?.type === "row" ? picked.path : null;
    const pickedField = picked?.type === "field" ? `${picked.source}\u0000${picked.field}` : null;
    this.layers.wires.selectAll<SVGPathElement, PlacedWire>("path")
      .attr("data-lit", (wire) => state(light !== null && light.rows.has(wire.row) && (wire.field === "" ? light.cards.has(wire.source) : light.fields.has(wire.field))));
    this.layers.tracks.selectAll<SVGLineElement, PlacedRow>("line")
      .attr("data-lit", (row) => state(light?.rows.has(row.row.path) === true));
    this.layers.rows.selectAll<SVGGElement, PlacedRow>("g.row")
      .attr("data-lit", (row) => state(light?.rows.has(row.row.path) === true))
      .attr("data-picked", (row) => (row.row.path === pickedRow ? "true" : null))
      .attr("data-found", (row) => (found?.rows.has(row.row.path) === true ? "true" : null));
    this.layers.dots.selectAll<SVGGElement, PlacedDot>("g.dot")
      .attr("data-lit", (dot) => state(light?.dots.has(dot.id) === true));
    // A table's line is lit down to the last row lit on it, so it says how far the value goes rather than where the column ends.
    const deepest = new Map<string, number>();
    for (const dot of this.layout?.dots ?? []) {
      if (light?.dots.has(dot.id) === true) {
        deepest.set(dot.source, Math.max(deepest.get(dot.source) ?? 0, dot.y));
      }
    }

    const drops = this.layers.drops.selectAll<SVGGElement, PlacedDrop>("g.drop")
      .attr("data-lit", (drop) => state(light?.cards.has(drop.source) === true));
    drops.select("path.curve-lit").attr("display", (drop) => (deepest.has(drop.source) ? null : "none"));
    drops.select("line.column-lit")
      .attr("display", (drop) => (deepest.has(drop.source) ? null : "none"))
      .attr("y2", (drop) => deepest.get(drop.source) ?? drop.from);
    this.layers.cards.selectAll<SVGGElement, PlacedCard>("g.card")
      .attr("data-lit", (card) => state(light?.cards.has(card.card.id) === true));
    this.layers.cards.selectAll<SVGGElement, PlacedField>("g.field")
      .attr("data-lit", (field) => state(light?.fields.has(field.field.id) === true))
      .attr("data-picked", (field) => (field.field.id === pickedField ? "true" : null))
      .attr("data-found", (field) => (found?.fields.has(field.field.id) === true ? "true" : null));
  }

  /** The handlers every part takes: pointing lights it, a click or Enter picks it. */
  private wire<E extends SVGElement, D>(parts: Selection<E, D, BaseType, unknown>, part: (datum: D) => OverviewPart): void {
    parts
      .on("mouseenter", (_event, datum) => this.settle(() => this.handlers.point(part(datum)), 0))
      .on("mouseleave", () => this.settle(() => this.handlers.point(null), LEAVE_MS))
      .on("focus", (_event, datum) => this.handlers.point(part(datum)))
      .on("blur", () => this.handlers.point(null))
      .on("click", (event: Event, datum) => {
        event.stopPropagation();
        this.handlers.pick(part(datum));
      });
  }

  /**
   * A right click on a part opens its menu at the pointer. With `keys`, so does the context menu key or Shift+F10 on it, under
   * it, and Enter or Space does what a click does, since a group taking the focus is no button the browser presses.
   */
  private menued<E extends SVGElement, D>(parts: Selection<E, D, BaseType, unknown>, part: (datum: D) => OverviewMenuTarget, keys: boolean): void {
    parts.on("contextmenu", (event: Event, datum) => {
      const { clientX: x, clientY: y } = event as MouseEvent;
      event.preventDefault();
      event.stopPropagation();
      this.handlers.tip(null);
      this.handlers.menu(part(datum), { x, y });
    });
    if (!keys) {
      return;
    }

    parts.on("keydown.menu", (said: Event, datum) => {
      const event = said as KeyboardEvent;
      const target = event.currentTarget as Element;
      if (event.key === "ContextMenu" || (event.key === "F10" && event.shiftKey)) {
        event.preventDefault();
        event.stopPropagation();
        const box = target.getBoundingClientRect();
        this.handlers.menu(part(datum), { x: box.left + 12, y: box.bottom });
      } else if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        event.stopPropagation();
        target.dispatchEvent(new MouseEvent("click", { bubbles: true }));
      }
    });
  }

  /** Drops what pointing at the canvas was waiting to do, for a reader now pointing at something beside it. */
  stopPointing(): void {
    clearTimeout(this.settling);
  }

  /** Does what pointing means after `wait` ms, in place of whatever was waiting. */
  private settle(act: () => void, wait: number): void {
    clearTimeout(this.settling);
    if (wait === 0) {
      act();
    } else {
      this.settling = setTimeout(act, wait);
    }
  }

  /** A tooltip on a part of the canvas, while the pointer is on it. */
  private tipped<E extends SVGElement, D>(parts: Selection<E, D, BaseType, unknown>, tip: (datum: D) => { title: string; body: string } | null): void {
    parts
      .on("mouseenter.tip", (event: Event, datum) => {
        const said = tip(datum);
        if (said !== null) {
          const box = (event.currentTarget as Element).getBoundingClientRect();
          this.handlers.tip({ x: box.left + box.width / 2, y: box.top, ...said });
        }
      })
      .on("mouseleave.tip", () => this.handlers.tip(null));
  }

  private drawDrops(drops: readonly PlacedDrop[], moves: number): void {
    const joined = this.layers.drops.selectAll<SVGGElement, PlacedDrop>("g.drop").data(drops, (drop) => drop.source);
    const entered = joined.enter().append("g").attr("class", `drop ${LIT_GROUP}`);
    entered.append("path").attr("class", "curve fill-none");
    entered.append("line").attr("class", "column");
    entered.append("path").attr("class", "curve-lit fill-none stroke-primary").attr("stroke-width", 1.75).attr("display", "none");
    entered.append("line").attr("class", "column-lit stroke-primary").attr("stroke-width", 1.75).attr("display", "none");
    joined.exit().remove();
    const all = entered.merge(joined);
    const kind = (drop: PlacedDrop) => this.kindOf(drop.source);
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
   * lights a line is what lights the part it comes from, and a click picks that part, so a line works as its table or its field.
   */
  private drawEdges(layout: OutlineLayout): void {
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

    const joined = this.layers.edges.selectAll<SVGPathElement, PlacedEdge>("path").data(edges, (edge) => edge.id);
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
        this.settle(() => {
          this.handlers.point(edge.part);
          this.handlers.tip(edge.tip === null ? null : { x, y, ...edge.tip });
        }, SETTLE_MS);
      })
      .on("mouseleave", () => {
        this.handlers.tip(null);
        this.settle(() => this.handlers.point(null), LEAVE_MS);
      })
      .on("click", (event: MouseEvent, edge) => {
        event.stopPropagation();
        this.handlers.pick(edge.part);
      });
    this.menued(all, (edge) => edge.part, false);
  }

  private kindOf(source: string): string {
    return this.layout?.cards.find((card) => card.card.id === source)?.card.source.kind ?? "";
  }

  private drawWires(wires: readonly PlacedWire[], moves: number): void {
    const joined = this.layers.wires.selectAll<SVGPathElement, PlacedWire>("path").data(wires, (wire) => wire.id);
    joined.exit().transition().duration(moves / 2).style("opacity", 0).remove();
    const entered = joined.enter().append("path").attr("d", (wire) => wire.d).style("opacity", 0);
    entered.transition().duration(moves).style("opacity", null);
    const all = entered.merge(joined);
    all
      .attr("class", (wire) => `${WIRE} ${toneOf(this.kindOf(wire.source)).stroke}`)
      .attr("stroke-opacity", (wire) => (wire.heavy ? 0.7 : 0.55))
      .attr("stroke-width", (wire) => (wire.heavy ? 2.5 : 1.25))
      .attr("stroke-dasharray", (wire) => (wire.dashed ? "4 3" : null));
    joined.transition().duration(moves).attr("d", (wire) => wire.d);
  }

  private drawRecord(layout: OutlineLayout, dress: OverviewDress, moves: number): void {
    const { record } = layout;
    const layer = this.layers.record;
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
        this.handlers.leftOut();
      });
    card.attr("aria-label", `${dress.title}: the record. The menu key opens what can be done with it.`);
    this.menued(card, () => ({ type: "record" }), true);
  }

  private drawCards(cards: readonly PlacedCard[], dress: OverviewDress, moves: number): void {
    const joined = this.layers.cards.selectAll<SVGGElement, PlacedCard>("g.card").data(cards, (card) => card.card.id);
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
    all.select("text.sub").text((card) => fitText(this.subtitleOf(card), card.width - 24, 8.8));
    all.select("text.foot")
      .attr("y", (card) => card.height - 9)
      .attr("display", (card) => (card.card.unread.length > 0 && !card.card.folded ? null : "none"))
      .text((card) => `${card.card.unread.length} ${card.card.unread.length === 1 ? "column" : "columns"} not read`)
      .on("click", (event: MouseEvent, card) => {
        event.stopPropagation();
        this.handlers.pick({ type: "card", source: card.card.id });
      });

    this.wire(all.select<SVGGElement>("g.head"), (card) => ({ type: "card", source: card.card.id }));
    all.select<SVGGElement>("g.head").on("click", (event: MouseEvent, card) => {
      event.stopPropagation();
      if (card.card.repeater !== null) {
        this.handlers.toggle(card.card.repeater);
      } else {
        this.handlers.pick({ type: "card", source: card.card.id });
      }
    });
    // Anywhere on a table opens its menu, a field its own; the heading answers the keys.
    this.menued(all, (card) => ({ type: "card", source: card.card.id }), false);
    this.menued(all.select<SVGGElement>("g.head"), (card) => ({ type: "card", source: card.card.id }), true);

    all.each((card, index, nodes) => {
      this.drawFields(select<SVGGElement, PlacedCard>(nodes[index]).select<SVGGElement>("g.fields"), card, dress, moves);
    });
  }

  /** What a table's second line says: what a row of it is, and for one folded how much it holds. */
  private subtitleOf(card: PlacedCard): string {
    const { card: table } = card;
    const place = table.subtitle === null ? "" : ` · ${table.subtitle}`;
    return table.folded ? `${table.role} · ${table.fields.length} columns` : `${table.role}${place}`;
  }

  private drawFields(parent: Selection<SVGGElement, PlacedCard, BaseType, unknown>, card: PlacedCard, dress: OverviewDress, moves: number): void {
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
    all.attr("aria-label", (field) => `${field.field.name}${field.field.key ? ", key" : ""}`);
    all.select("rect.shade").attr("width", card.width - 2);
    entered.each(function drawMark(field) {
      if (field.field.key) {
        drawGlyph(select<SVGGElement, unknown>(this).select<SVGGElement>("g.mark"), "key", 13, "text-muted-foreground");
      }
    });
    all.select("text.name")
      .attr("x", (field) => (field.field.key ? 31 : 12))
      .attr("class", (field) => `name font-mono text-[12px] ${field.field.displayOnly ? "fill-muted-foreground" : "fill-foreground"}`)
      .text((field) => fitText(shownName(card, field.field), this.nameRoom(card, field.field, dress), 12));
    all.select("text.end")
      .attr("x", card.width - 10)
      .attr("class", (field) => `end ${dress.values.has(field.field.id) ? TEXT.value : TEXT.faint}`)
      .text((field) => this.endOf(card, field.field, dress));
    this.wire(all, (field) => ({ type: "field", source: card.card.id, field: field.field.name }));
    this.menued(all, (field) => ({ type: "field", source: card.card.id, field: field.field.name }), true);
    this.tipped(all, (field) => {
      const value = dress.values.get(field.field.id);
      const shown = fitText(shownName(card, field.field), this.nameRoom(card, field.field, dress), 12);
      return value === undefined && shown === field.field.name ? null : { title: field.field.name, body: value ?? "" };
    });
  }

  /** What stands at the end of a field: its value in the record shown, else its type, else what a lookup does with it. */
  private endOf(card: PlacedCard, field: CardField, dress: OverviewDress): string {
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

  private nameRoom(card: PlacedCard, field: CardField, dress: OverviewDress): number {
    const end = textWidth(this.endOf(card, field, dress), 11);
    return card.width - (field.key ? 31 : 12) - end - 18;
  }

  private drawRows(layout: OutlineLayout, dress: OverviewDress, moves: number): void {
    const { record } = layout;
    const tracks = this.layers.tracks.selectAll<SVGLineElement, PlacedRow>("line").data(layout.rows.filter((row) => row.lineFrom !== null), (row) => row.row.path);
    tracks.exit().transition().duration(moves / 2).style("opacity", 0).remove();
    const enteredTracks = tracks.enter().append("line")
      .attr("x1", (row) => row.lineFrom ?? 0)
      .attr("y1", (row) => row.y)
      .attr("y2", (row) => row.y)
      .style("opacity", 0);
    enteredTracks.transition().duration(moves).style("opacity", null);
    enteredTracks.merge(tracks)
      .attr("class", (row) => `${WIRE} ${toneOf(this.kindOf(row.from ?? "")).stroke}`)
      .attr("stroke-opacity", 0.55)
      .attr("stroke-width", 1.25)
      .attr("x2", record.x)
      .attr("stroke-dasharray", (row) => (row.row.literal !== null ? "2 3" : null));
    tracks.transition().duration(moves).attr("x1", (row) => row.lineFrom ?? 0).attr("y1", (row) => row.y).attr("y2", (row) => row.y);

    const joined = this.layers.rows.selectAll<SVGGElement, PlacedRow>("g.row").data(layout.rows, (row) => row.row.path);
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
      this.drawRowParts(select<SVGGElement, PlacedRow>(nodes[index]), row, record, dress, moves);
    });

    this.wire(all, (row) => ({ type: "row", path: row.row.path }));
    all.on("focus.reveal", (_event, row) => this.reveal(row.row.path));
    all.on("click", (event: MouseEvent, row) => {
      event.stopPropagation();
      if (row.row.folding && row.row.node.shape !== "choice") {
        this.handlers.toggle(row.row.path);
      } else {
        this.handlers.pick({ type: "row", path: row.row.path });
      }
    });
    all.on("dblclick", (event: MouseEvent, row) => {
      event.stopPropagation();
      if (row.row.node.shape === "choice") {
        this.handlers.toggle(row.row.path);
      }
    });
    all.on("keydown", (event: KeyboardEvent, row) => this.onRowKey(event, row));
    this.menued(all, (row) => ({ type: "row", path: row.row.path }), false);
  }

  private onRowKey(event: KeyboardEvent, row: PlacedRow): void {
    const stop = () => {
      event.preventDefault();
      event.stopPropagation();
    };

    const move = (step: 1 | -1) => {
      const rows = this.layers.rows.selectAll<SVGGElement, PlacedRow>("g.row");
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
        this.handlers.menu({ type: "row", path: row.row.path }, { x: box.left + 24, y: box.bottom });
        break;
      }
      case "Enter":
      case " ":
        stop();
        if (row.row.folding && row.row.node.shape !== "choice") {
          this.handlers.toggle(row.row.path);
        } else {
          this.handlers.pick({ type: "row", path: row.row.path });
        }

        break;
      case "ArrowRight":
        stop();
        if (row.row.folding && !row.row.open) {
          this.handlers.toggle(row.row.path);
        }

        break;
      case "ArrowLeft":
        stop();
        if (row.row.folding && row.row.open) {
          this.handlers.toggle(row.row.path);
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

  private drawRowParts(group: Selection<SVGGElement, PlacedRow, null, undefined>, placed: PlacedRow, record: OutlineLayout["record"], dress: OverviewDress, moves: number): void {
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
        this.handlers.toggle(row.path);
      });
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
      this.tipped(holder, () => (flag === "alert" ? { title: "Needs a look", body: mark?.text ?? "" } : { title: "Rules on this value", body: row.node.assertions.map((rule) => `${rule.label}: ${rule.expected}`).join("\n") }));
    });

    this.drawPills(group, placed, moves);

    const literal = group.select<SVGGElement>("g.literal");
    literal.attr("display", placed.literal === null ? "none" : null);
    if (placed.literal !== null) {
      literal.select("rect").attr("x", placed.literal.x).attr("width", placed.literal.width);
      literal.select("text").attr("x", placed.literal.x + 6).text(placed.literal.text);
    }
  }

  private drawPills(group: Selection<SVGGElement, PlacedRow, null, undefined>, placed: PlacedRow, moves: number): void {
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
    this.tipped(all, (pill) => (pill.pill.detail === "" ? null : { title: pill.pill.text === "" ? pillText(pill.pill) : pill.pill.text, body: pill.pill.detail }));
  }

  private drawDots(dots: readonly PlacedDot[], moves: number): void {
    const joined = this.layers.dots.selectAll<SVGGElement, PlacedDot>("g.dot").data(dots, (dot) => dot.id);
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
    all.select("circle.spot").attr("class", (dot) => `spot stroke-background ${toneOf(this.kindOf(dot.source)).fill}`);
    this.wire(all, (dot) => ({ type: "row", path: dot.row }));
    this.menued(all, (dot) => ({ type: "row", path: dot.row }), false);
    this.tipped(all, (dot) => this.dotTip(dot));
  }

  /** What a dot says: which table is read, by what, for what. */
  private dotTip(dot: PlacedDot): { title: string; body: string } | null {
    const card = this.layout?.cards.find((candidate) => candidate.card.id === dot.source);
    const row = this.layout?.rows.find((candidate) => candidate.row.path === dot.row);
    if (card === undefined || row === undefined) {
      return null;
    }

    if (card.card.source.kind === "parameters") {
      return { title: "From the flow", body: row.row.reads.filter((read) => read.source === dot.source).map((read) => read.field).join(", ") };
    }

    const lookup = row.row.lookups.find((candidate) => candidate.source === dot.source);
    const by = lookup === undefined || lookup.matches.length === 0
      ? "by the value"
      : `where ${lookup.matches.map((match) => `${match.field} = ${operandOf(row.row.reads, match.value).text}`).join(", else ")}`;
    const read = lookup?.field ?? "what it holds for it";
    return { title: card.card.title, body: `Finds the row ${by}, and reads ${read}.` };
  }
}

/** A field as its table shows it: a searched record's path by its last step, which is what tells two of them apart. */
function shownName(card: PlacedCard, field: CardField): string {
  return card.card.source.kind === "search" ? field.name.slice(field.name.lastIndexOf(".") + 1) : field.name;
}

/** A catalog type as it reads at a field's end: its name, without what only the database needs. */
function shortType(type: string | null): string {
  return type === null ? "" : type.replace(/\s*\(.*\)$/, "").toLowerCase();
}

function labelOf(placed: PlacedRow): string {
  const { row } = placed;
  const how = row.folding ? `${row.folded} ${row.folded === 1 ? "property" : "properties"}` : row.node.flow?.summary ?? "";
  return `${row.path}${how === "" ? "" : `: ${how}`}`;
}
