// The overview's canvas, drawn by D3: what outlineLayout placed, joined to SVG so a row opening moves things rather than
// redrawing them, with pan and zoom on the viewport. It owns its DOM and holds no React state: the page hands it a layout and
// what is lit, and hears back what the reader points at (osdu/docs/reference/concepts/mapping-overview.md).

import { select, type Selection } from "d3-selection";
import "d3-transition";
import { zoom, zoomIdentity, zoomTransform, type D3ZoomEvent, type ZoomBehavior, type ZoomTransform } from "d3-zoom";
import { drawCards, drawRecord } from "./canvasCards";
import { drawDots, drawDrops, drawEdges, drawWires } from "./canvasLines";
import { MOVE_MS, reducedMotion } from "./canvasLook";
import { drawRows } from "./canvasRows";
import type { CanvasLayers, CanvasScene } from "./canvasScene";
import type { OutlineFound, OutlineLight } from "./outlineModel";
import {
  boundsOf, framingOf, MEASURE, ZOOM, type OutlineLayout, type PlacedCard, type PlacedDot, type PlacedDrop, type PlacedField, type PlacedRow,
  type PlacedWire,
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
  /** The reader clicked a part, or pressed Space on it: it stays lit, and nothing opens. */
  pick: (part: OverviewPart) => void;
  /** The reader double-clicked a part, or pressed Enter on it: its details open. */
  open: (part: OverviewPart) => void;
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

export class OverviewCanvas {
  private readonly svg: Selection<SVGSVGElement, unknown, null, undefined>;

  private readonly viewport: Selection<SVGGElement, unknown, null, undefined>;

  private readonly layers: CanvasLayers;

  /** What the drawing of each part reads from the canvas. */
  private readonly scene: CanvasScene;

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
    this.scene = {
      layers: this.layers,
      handlers,
      layout: () => this.layout,
      settle: (act, wait) => this.settle(act, wait),
      reveal: (path) => this.reveal(path),
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
    drawDrops(this.scene, layout.drops, moves);
    drawWires(this.scene, layout.wires, moves);
    drawRecord(this.scene, layout, dress, moves);
    drawCards(this.scene, layout.cards, dress, moves);
    drawRows(this.scene, layout, dress, moves);
    drawEdges(this.scene, layout);
    drawDots(this.scene, layout.dots, moves);
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
}
