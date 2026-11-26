// What each drawing of the overview's canvas works with, and the handlers its parts take. A drawing is handed the canvas's
// scene: its layers, the page's handlers, the layout as the canvas holds it when the drawing reads it, how pointing waits,
// and how a row is brought into view. Pointing at a part lights it, a click keeps it lit, a double click opens its details,
// a right click opens its menu, and a tooltip says what it is.

import type { BaseType, Selection } from "d3-selection";
import { LEAVE_MS } from "./canvasLook";
import type { OutlineLayout } from "./outlineLayout";
import type { OverviewHandlers, OverviewMenuTarget, OverviewPart } from "./OverviewCanvas";

/** The canvas's layers, each a group of the viewport. */
export type CanvasLayers = Record<"drops" | "wires" | "tracks" | "cards" | "record" | "rows" | "edges" | "dots", Selection<SVGGElement, unknown, null, undefined>>;

/** What a drawing reads from the canvas it draws on. */
export interface CanvasScene {
  readonly layers: CanvasLayers;
  readonly handlers: OverviewHandlers;
  /** The layout the canvas holds now: the one drawn last, or null before the first. */
  layout(): OutlineLayout | null;
  /** Does what pointing means after `wait` ms, in place of whatever was waiting. */
  settle(act: () => void, wait: number): void;
  /** Brings a row of the record clear of any panel, without moving a view that already shows it. */
  reveal(path: string): void;
}

/** The kind of source a table of the layout reads (record, child, cache, search, parameters); empty for no table of it. */
export function kindOf(scene: CanvasScene, source: string): string {
  return scene.layout()?.cards.find((card) => card.card.id === source)?.card.source.kind ?? "";
}

/** The handlers every part takes: pointing lights it, a click keeps it lit, and a double click opens its details. */
export function wire<E extends SVGElement, D>(scene: CanvasScene, parts: Selection<E, D, BaseType, unknown>, part: (datum: D) => OverviewPart): void {
  parts
    .on("mouseenter", (_event, datum) => scene.settle(() => scene.handlers.point(part(datum)), 0))
    .on("mouseleave", () => scene.settle(() => scene.handlers.point(null), LEAVE_MS))
    .on("focus", (_event, datum) => scene.handlers.point(part(datum)))
    .on("blur", () => scene.handlers.point(null))
    .on("click", (event: Event, datum) => {
      event.stopPropagation();
      scene.handlers.pick(part(datum));
    })
    .on("dblclick", (event: Event, datum) => {
      event.stopPropagation();
      scene.handlers.open(part(datum));
    });
}

/**
 * A right click on a part opens its menu at the pointer. With `keys`, so does the context menu key or Shift+F10 on it, under
 * it, Space does what a click does and Enter what a double click does, since a group taking the focus is no button the browser
 * presses.
 */
export function menued<E extends SVGElement, D>(scene: CanvasScene, parts: Selection<E, D, BaseType, unknown>, part: (datum: D) => OverviewMenuTarget, keys: boolean): void {
  parts.on("contextmenu", (event: Event, datum) => {
    const { clientX: x, clientY: y } = event as MouseEvent;
    event.preventDefault();
    event.stopPropagation();
    scene.handlers.tip(null);
    scene.handlers.menu(part(datum), { x, y });
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
      scene.handlers.menu(part(datum), { x: box.left + 12, y: box.bottom });
    } else if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      event.stopPropagation();
      target.dispatchEvent(new MouseEvent(event.key === "Enter" ? "dblclick" : "click", { bubbles: true }));
    }
  });
}

/** A tooltip on a part of the canvas, while the pointer is on it. */
export function tipped<E extends SVGElement, D>(scene: CanvasScene, parts: Selection<E, D, BaseType, unknown>, tip: (datum: D) => { title: string; body: string } | null): void {
  parts
    .on("mouseenter.tip", (event: Event, datum) => {
      const said = tip(datum);
      if (said !== null) {
        const box = (event.currentTarget as Element).getBoundingClientRect();
        scene.handlers.tip({ x: box.left + box.width / 2, y: box.top, ...said });
      }
    })
    .on("mouseleave.tip", () => scene.handlers.tip(null));
}
