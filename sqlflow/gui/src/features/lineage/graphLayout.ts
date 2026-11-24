/**
 * A layered (Sugiyama) layout, which is what makes data flow readable. By default it runs top to bottom: sources at
 * the top, consumers below, one row per level. A card is four times as wide as it is tall, so a level of thirty
 * nodes on one line would run seven screens wide; such a level is laid in several staggered lines instead. The same
 * levels can be drawn left to right, one column per level, where a long level simply runs down the page.
 *
 * Levels come from a longest-path layering over the edges, with cycles broken first (a depth-first walk drops the
 * edges that close a cycle from the level calculation, so a flow that reads a view derived from its own output
 * still gets a real level instead of collapsing onto the top row). Within each row, several barycenter
 * passes order nodes by the average position of their neighbours to cut edge crossings. Independent of any layout
 * library, so the level alignment is exact.
 */

export const NODE_WIDTH = 240;
export const NODE_HEIGHT = 54;
/** Vertical distance between rows: a card plus the room an edge and its label need. */
export const LAYER_SPACING = NODE_HEIGHT + 96;
/** Horizontal distance between the cards of a row. */
export const NODE_SPACING = NODE_WIDTH + 28;
/** The most cards one line of a row holds; a level with more is laid in several lines. */
export const MAX_ROW_NODES = 8;
/** Vertical distance between the lines of one row. */
export const LINE_SPACING = NODE_HEIGHT + 26;

/** Left-to-right: the distance between columns, a card plus the room an edge needs to bend. */
export const COLUMN_SPACING = NODE_WIDTH + 110;
/** Left-to-right: the distance between the cards of a column. */
export const COLUMN_NODE_SPACING = NODE_HEIGHT + 20;

/**
 * How large the cards a layout places are, and (for a banded layout) how much room it leaves between the lines of
 * one band and between two bands. The rooms across a line stay as they are, whatever the card.
 */
export interface CardSize {
  width: number;
  height: number;
  /** Between two lines of one band; the banded layout's own room when left out. */
  lineGap?: number;
  /** Between two bands; the banded layout's own room when left out. */
  bandGap?: number;
}

/** The card the graph draws when it shows how nodes connect: a name and a caption. */
export const CARD_SIZE: CardSize = { width: NODE_WIDTH, height: NODE_HEIGHT };
/**
 * The taller, wider card of the data view, which adds a figure and a day-by-day chart under the name. Its lines and
 * bands stand closer than the graph's: a chain is read down the page, and every row that fits without scrolling is
 * a step of it seen beside the others.
 */
export const DATA_CARD_SIZE: CardSize = { width: 280, height: 92, lineGap: 44, bandGap: 72 };

/** Which way data runs on the canvas. */
export type Orientation = "down" | "right";

export interface LayoutEdge {
  source: string;
  target: string;
}

export interface Position {
  x: number;
  y: number;
}

/**
 * The edges that close a cycle, by their source: a depth-first walk from the roots, in the order given, marks each
 * edge that leads back onto the walk's own stack. Left out, the rest of the graph is acyclic. The roots' order
 * decides which edge of a cycle is the one cut, so the caller puts the nodes a cycle should start from first.
 */
export function backEdges(roots: readonly string[], outgoing: ReadonlyMap<string, readonly string[]>): Map<string, Set<string>> {
  const back = new Map<string, Set<string>>();
  const state = new Map<string, number>(); // 1 = on the walk's stack, 2 = finished
  for (const root of roots) {
    if (state.has(root)) {
      continue;
    }
    const stack: Array<{ id: string; next: number }> = [{ id: root, next: 0 }];
    state.set(root, 1);
    while (stack.length > 0) {
      const frame = stack[stack.length - 1];
      const children = outgoing.get(frame.id) ?? [];
      if (frame.next < children.length) {
        const child = children[frame.next];
        frame.next += 1;
        const childState = state.get(child);
        if (childState === 1) {
          (back.get(frame.id) ?? back.set(frame.id, new Set()).get(frame.id)!).add(child);
        } else if (childState === undefined) {
          state.set(child, 1);
          stack.push({ id: child, next: 0 });
        }
      } else {
        state.set(frame.id, 2);
        stack.pop();
      }
    }
  }
  return back;
}

export function layeredLayout(
  ids: readonly string[],
  edges: readonly LayoutEdge[],
  orientation: Orientation = "down",
  size: CardSize = CARD_SIZE,
): Map<string, Position> {
  const position = new Map<string, Position>();
  // The same rooms between cards whatever their size: a taller or wider card pushes its neighbours out by as much.
  const nodeSpacing = NODE_SPACING + size.width - NODE_WIDTH;
  const lineSpacing = LINE_SPACING + size.height - NODE_HEIGHT;
  const layerSpacing = LAYER_SPACING + size.height - NODE_HEIGHT;
  const columnSpacing = COLUMN_SPACING + size.width - NODE_WIDTH;
  const columnNodeSpacing = COLUMN_NODE_SPACING + size.height - NODE_HEIGHT;
  if (ids.length === 0) {
    return position;
  }

  const known = new Set(ids);
  const outgoing = new Map<string, string[]>();
  const incoming = new Map<string, string[]>();
  const levelInDegree = new Map<string, number>();
  for (const id of known) {
    outgoing.set(id, []);
    incoming.set(id, []);
    levelInDegree.set(id, 0);
  }
  const seenEdges = new Set<string>();
  for (const edge of edges) {
    const key = `${edge.source}\u0000${edge.target}`;
    if (known.has(edge.source) && known.has(edge.target) && edge.source !== edge.target && !seenEdges.has(key)) {
      seenEdges.add(key);
      outgoing.get(edge.source)!.push(edge.target);
      incoming.get(edge.target)!.push(edge.source);
      levelInDegree.set(edge.target, levelInDegree.get(edge.target)! + 1);
    }
  }

  // ---- Level assignment: longest path, with cycles broken first. ----------------------------------------------
  // A cyclic subgraph would leave Kahn's queue undrained and pin every node of the cycle to the top row. A
  // depth-first walk therefore marks each edge that closes back onto its own stack; those edges are left out of
  // the level calculation (they still draw, running upwards), so the rest is acyclic and every node gets a real
  // level. Nodes with the fewest inbound edges are visited first so cycles break in the natural flow direction.
  const roots = [...known].sort((a, b) => levelInDegree.get(a)! - levelInDegree.get(b)!);
  const backTargets = backEdges(roots, outgoing);
  for (const targets of backTargets.values()) {
    for (const target of targets) {
      levelInDegree.set(target, levelInDegree.get(target)! - 1);
    }
  }

  // Longest-path layering over the acyclic remainder: a node sits one row below its deepest producer.
  const level = new Map<string, number>();
  const queue: string[] = [];
  for (const id of known) {
    if (levelInDegree.get(id) === 0) {
      level.set(id, 0);
      queue.push(id);
    }
  }
  for (let head = 0; head < queue.length; head++) {
    const id = queue[head];
    const current = level.get(id)!;
    for (const child of outgoing.get(id)!) {
      if (backTargets.get(id)?.has(child)) {
        continue;
      }
      level.set(child, Math.max(level.get(child) ?? 0, current + 1));
      const remaining = levelInDegree.get(child)! - 1;
      levelInDegree.set(child, remaining);
      if (remaining === 0) {
        queue.push(child);
      }
    }
  }

  // As-late-as-possible relaxation: slide every node to one row above its EARLIEST consumer. Longest-path
  // places each node as early as it can, which strands a slack chain (a hand-kept input, its loader, its table) at
  // the top even when its only consumers sit rows below. Walking the topological order in reverse and only
  // ever moving a node down keeps every producer above its consumers; a sink or a lone node never moves.
  for (let i = queue.length - 1; i >= 0; i--) {
    const id = queue[i];
    const back = backTargets.get(id);
    let earliest = Number.POSITIVE_INFINITY;
    for (const child of outgoing.get(id)!) {
      if (!back?.has(child)) {
        earliest = Math.min(earliest, level.get(child)!);
      }
    }
    if (Number.isFinite(earliest) && earliest - 1 > level.get(id)!) {
      level.set(id, earliest - 1);
    }
  }

  const rows = new Map<number, string[]>();
  for (const id of known) {
    const row = level.get(id) ?? 0;
    (rows.get(row) ?? rows.set(row, []).get(row)!).push(id);
  }
  const levels = [...rows.keys()].sort((a, b) => a - b);

  // ---- Crossing reduction: order each row by the barycenter of its neighbours, both directions. --------------
  const order = new Map<string, number>();
  for (const row of levels) {
    const group = rows.get(row)!;
    group.sort((a, b) =>
      outgoing.get(b)!.length + incoming.get(b)!.length - (outgoing.get(a)!.length + incoming.get(a)!.length)
      || a.localeCompare(b));
    group.forEach((id, i) => order.set(id, i));
  }

  const barycenter = (id: string, neighbours: Map<string, string[]>, neighbourLevel: number): number => {
    const relevant = neighbours.get(id)!.filter((n) => level.get(n) === neighbourLevel);
    if (relevant.length === 0) {
      return order.get(id) ?? 0;
    }
    return relevant.reduce((sum, n) => sum + (order.get(n) ?? 0), 0) / relevant.length;
  };

  for (let pass = 0; pass < 6; pass++) {
    for (let i = 1; i < levels.length; i++) {
      const group = rows.get(levels[i])!;
      const weight = new Map(group.map((id) => [id, barycenter(id, incoming, levels[i - 1])]));
      group.sort((a, b) => weight.get(a)! - weight.get(b)!);
      group.forEach((id, j) => order.set(id, j));
    }
    for (let i = levels.length - 2; i >= 0; i--) {
      const group = rows.get(levels[i])!;
      const weight = new Map(group.map((id) => [id, barycenter(id, outgoing, levels[i + 1])]));
      group.sort((a, b) => weight.get(a)! - weight.get(b)!);
      group.forEach((id, j) => order.set(id, j));
    }
  }

  // ---- Positions: y by row, x by order within the row, each row centered on the same axis. -----------------------
  // A level holding more nodes than a row has room for is laid in several lines, its nodes dealt to them in turn and
  // each line shifted by a fraction of a card, like bricks: neighbours in the order stay near each other, and an edge
  // leaving an upper line runs down through the gaps of the lines under it instead of behind their cards.
  if (orientation === "right") {
    levels.forEach((row, index) => {
      const group = rows.get(row)!;
      const height = (group.length - 1) * columnNodeSpacing;
      group.forEach((id, i) => {
        position.set(id, { x: index * columnSpacing, y: i * columnNodeSpacing - height / 2 });
      });
    });
    return position;
  }

  let top = 0;
  for (const row of levels) {
    const group = rows.get(row)!;
    const lines = Math.ceil(group.length / MAX_ROW_NODES);
    const slots = Math.ceil(group.length / lines);
    const width = (slots - 1 + (lines - 1) / lines) * nodeSpacing;
    group.forEach((id, i) => {
      const line = i % lines;
      position.set(id, {
        x: (Math.floor(i / lines) + line / lines) * nodeSpacing - width / 2,
        y: top + line * lineSpacing,
      });
    });
    top += (lines - 1) * lineSpacing + layerSpacing;
  }

  return position;
}
