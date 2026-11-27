import {
  backEdges,
  CARD_SIZE,
  COLUMN_NODE_SPACING,
  LINE_SPACING,
  MAX_ROW_NODES,
  NODE_HEIGHT,
  NODE_SPACING,
  NODE_WIDTH,
  type CardSize,
  type LayoutEdge,
  type Orientation,
  type Position,
} from "./graphLayout.ts";

/**
 * The layout of a drawing that holds flows: one band per execution wave, in running order, so the drawing is read
 * the way the data moves, source to target.
 *
 * A wave is the plan's own answer to "what runs after what", and it is what a reader follows a chain by. A layout
 * that only counts hops puts two flows of one wave on different rows as soon as one of them reads through a view,
 * and slides a flow with no early consumer rows away from its wave. Here a flow sits in the band of its wave, and
 * data sits where it comes into being:
 *
 *   - an object a flow writes sits under that flow, in the same band, and a view over it one line further down;
 *   - an object nothing in the drawing writes (a landed file, a table kept by hand) sits above the first flow that
 *     reads it, in that flow's band, and a view over such objects between them and the flow;
 *   - a derived dataset (a mapping, a model, a rule set a flow renders through) is an input of the flows reading it,
 *     not data that comes into being: it sits above the first of them, in that flow's band, even when what it is
 *     derived from was written bands earlier. Its reading flows still run after everything it reads.
 *
 * So every arrow runs down the page: within a band from inputs to flows to what they write, and from a band to a
 * later one. A flow drawn after a flow of another repository, a folder of flows or a stack has no single wave; it
 * takes the first band after everything it reads from, and never one before its own wave.
 *
 * The described lineage (steps other systems run, written down in lineage descriptions) has no wave and may move
 * nothing that has one. The bands of the estate's own flows are computed as if no description were drawn, and each
 * described step then takes a band of its own between two of them, at a half step: as late as what it feeds allows
 * when it leads into the estate (the chain that fills a landing folder sits above wave 1), and right after what it
 * reads when it only consumes. A wave's band therefore holds the same flows, under the same name, with the described
 * lineage drawn or not.
 */

export interface BandNode {
  id: string;
  /** True for a flow, and for a folder or a stack of flows. False for data. */
  flow: boolean;
  /** The waves of the flows the node stands for; empty for data, and for a flow lineage gave no wave. */
  waves: readonly number[];
  /** True for a node of the described lineage: a lineage description, or an object only descriptions name. */
  described?: boolean;
  /** True for a derived dataset: an input of the flows reading it, drawn above the first of them. */
  derived?: boolean;
}

/** One band of the drawing, along the axis data runs on (top to bottom: y). */
export interface Lane {
  /** The band's place in the running order, from 1. A band of the described lineage sits at a half step between
   * two of them (0.5 is above wave 1, and so are the steps before that). */
  rank: number;
  /** The wave every flow of the band runs in; null when its flows are of several waves, or of none. */
  wave: number | null;
  /** True for a band that holds only the described lineage: steps other systems run, and what only they name. */
  described: boolean;
  start: number;
  end: number;
}

export interface BandedLayout {
  positions: Map<string, Position>;
  lanes: Lane[];
  /** The drawing's reach across the bands (top to bottom: x), for a band's backdrop. */
  crossStart: number;
  crossEnd: number;
}

/** Between two lines of one band: room for the arrows from inputs to flows, and from flows to what they write. */
const BAND_LINE_GAP = 64;
/** Between two bands: wider, so a band reads as one thing. */
const BAND_GAP = 112;

type Section = "before" | "flows" | "after";

interface Line {
  band: number;
  section: Section;
  depth: number;
  ids: string[];
}

export function bandedLayout(
  nodes: readonly BandNode[],
  edges: readonly LayoutEdge[],
  orientation: Orientation = "down",
  size: CardSize = CARD_SIZE,
): BandedLayout {
  const positions = new Map<string, Position>();
  if (nodes.length === 0) {
    return { positions, lanes: [], crossStart: 0, crossEnd: 0 };
  }

  const byId = new Map(nodes.map((node) => [node.id, node]));
  const outgoing = new Map<string, string[]>();
  const incoming = new Map<string, string[]>();
  for (const id of byId.keys()) {
    outgoing.set(id, []);
    incoming.set(id, []);
  }
  const seen = new Set<string>();
  for (const edge of edges) {
    const key = `${edge.source}\u0000${edge.target}`;
    if (byId.has(edge.source) && byId.has(edge.target) && edge.source !== edge.target && !seen.has(key)) {
      seen.add(key);
      outgoing.get(edge.source)!.push(edge.target);
      incoming.get(edge.target)!.push(edge.source);
    }
  }

  // The wave a flow may not be drawn before: its own, or the first of those it stands for.
  const firstWave = (node: BandNode): number => {
    const real = node.waves.filter((wave) => wave >= 1);
    return real.length === 0 ? 1 : Math.min(...real);
  };

  // ---- Cycles. A flow may read what a later flow writes (a cache refreshed from what is delivered), which closes a
  // cycle the plan itself leaves unordered. The walk starts from the earliest waves, so the edge it cuts is the one
  // running back against the waves, and that edge is the one drawn upwards.
  const roots = [...byId.values()]
    .sort((a, b) =>
      (a.flow ? firstWave(a) : Number.MAX_SAFE_INTEGER) - (b.flow ? firstWave(b) : Number.MAX_SAFE_INTEGER)
      || incoming.get(a.id)!.length - incoming.get(b.id)!.length
      || a.id.localeCompare(b.id))
    .map((node) => node.id);
  // A flow that reads back what it wrote itself (it fills a table and reads it on its next run; it writes a lookup
  // that a document it renders with reads) closes a cycle through nothing but its own data. What it writes is drawn
  // under it and what it reads above it, so the arrow cut is the one leaving what the flow wrote for the flow itself
  // or for something that feeds it. That arrow is still drawn, running upwards.
  const own = new Map<string, Set<string>>();
  const cut = (source: string, target: string) => {
    (own.get(source) ?? own.set(source, new Set()).get(source)!).add(target);
  };
  for (const node of nodes) {
    if (!node.flow) {
      continue;
    }
    const feeds = new Set<string>();
    const queue = [node.id];
    for (let head = 0; head < queue.length; head++) {
      for (const producer of incoming.get(queue[head])!) {
        if (!feeds.has(producer) && !byId.get(producer)!.flow) {
          feeds.add(producer);
          queue.push(producer);
        }
      }
    }
    for (const written of outgoing.get(node.id)!) {
      if (byId.get(written)!.flow) {
        continue;
      }
      for (const reader of outgoing.get(written)!) {
        if (reader === node.id || feeds.has(reader)) {
          cut(written, reader);
        }
      }
    }
  }
  const kept = new Map<string, string[]>();
  for (const [id, targets] of outgoing) {
    kept.set(id, targets.filter((target) => !own.get(id)?.has(target)));
  }

  // What is left of a cycle runs through two flows or more.
  const back = backEdges(roots, kept);
  const forward = (id: string) => kept.get(id)!.filter((next) => !back.get(id)?.has(next));
  const backward = new Map<string, string[]>();
  for (const id of byId.keys()) {
    backward.set(id, []);
  }
  for (const id of byId.keys()) {
    for (const next of forward(id)) {
      backward.get(next)!.push(id);
    }
  }

  // Producers before consumers.
  const pending = new Map<string, number>();
  const order: string[] = [];
  for (const id of roots) {
    pending.set(id, backward.get(id)!.length);
    if (backward.get(id)!.length === 0) {
      order.push(id);
    }
  }
  for (let head = 0; head < order.length; head++) {
    for (const next of forward(order[head])) {
      const left = pending.get(next)! - 1;
      pending.set(next, left);
      if (left === 0) {
        order.push(next);
      }
    }
  }

  // ---- Bands. A flow's band is its wave, or the band after the last flow it reads from when that is later. `made`
  // is, for any node, the band of the latest flow above it: for data, the band it comes into being in (0: no flow of
  // the drawing writes it).
  const band = new Map<string, number>();
  const made = new Map<string, number>();
  const depth = new Map<string, number>();
  const section = new Map<string, Section>();
  const isDescribed = (id: string) => byId.get(id)!.described === true;
  for (const id of order) {
    const node = byId.get(id)!;
    if (node.described === true) {
      // Placed further down, around the bands the estate's own flows take here.
      continue;
    }
    const above = Math.max(
      0, ...backward.get(id)!.filter((producer) => !isDescribed(producer)).map((producer) => made.get(producer) ?? 0));
    if (node.flow) {
      const rank = Math.max(firstWave(node), above + 1);
      band.set(id, rank);
      made.set(id, rank);
      section.set(id, "flows");
      depth.set(id, 0);
    } else {
      made.set(id, above);
      // A derived dataset read by flows alone is placed with them below, above the first; one read by data, or by
      // nothing drawn, stays where it comes into being, so no arrow into it runs up.
      const readers = forward(id).filter((reader) => !isDescribed(reader));
      const input = node.derived === true && readers.length > 0 && readers.every((reader) => byId.get(reader)!.flow);
      if (above > 0 && !input) {
        // Written in this band: one line under the flow, or under the data of this band it is derived from.
        const under = backward.get(id)!
          .filter((producer) => section.get(producer) === "after" && band.get(producer) === above)
          .map((producer) => depth.get(producer)!);
        band.set(id, above);
        section.set(id, "after");
        depth.set(id, 1 + Math.max(0, ...under));
      }
    }
  }
  // Data nothing in the drawing writes, and the derived datasets flows read, consumers first: above the first thing
  // that reads it.
  for (let i = order.length - 1; i >= 0; i--) {
    const id = order[i];
    if (section.has(id) || isDescribed(id)) {
      continue;
    }
    const readers = forward(id).filter((reader) => band.has(reader));
    const first = readers.length === 0 ? 1 : Math.min(...readers.map((reader) => band.get(reader)!));
    const over = readers
      .filter((reader) => section.get(reader) === "before" && band.get(reader) === first)
      .map((reader) => depth.get(reader)!);
    band.set(id, first);
    section.set(id, "before");
    depth.set(id, 1 + Math.max(0, ...over));
  }

  // ---- The described lineage, around the bands above. A described step sits at a half step, so it never shares a
  // band with a flow that runs: half a step from a wave's band, a whole step from another described step.
  const describedFlows = order.filter((id) => isDescribed(id) && byId.get(id)!.flow);
  if (describedFlows.length > 0) {
    // The flows a node leads to, or comes from, through data alone.
    const flowsBeyond = (id: string, step: (node: string) => readonly string[]): string[] => {
      const found = new Set<string>();
      const passed = new Set<string>();
      const queue = [...step(id)];
      for (let head = 0; head < queue.length; head++) {
        const next = queue[head];
        if (byId.get(next)!.flow) {
          found.add(next);
        } else if (!passed.has(next)) {
          passed.add(next);
          queue.push(...step(next));
        }
      }
      return [...found];
    };
    const beside = (anchor: number, direction: 1 | -1) =>
      Number.isInteger(anchor) ? anchor + direction * 0.5 : anchor + direction;
    const place = (id: string, rank: number) => {
      band.set(id, rank);
      section.set(id, "flows");
      depth.set(id, 0);
    };

    // Whether a step leads into the estate: to a flow that runs, directly or through other described steps.
    const fed = new Map(describedFlows.map((id) => [id, flowsBeyond(id, forward)]));
    const leadsIn = new Set<string>();
    for (let i = describedFlows.length - 1; i >= 0; i--) {
      const id = describedFlows[i];
      if (fed.get(id)!.some((flow) => !isDescribed(flow) || leadsIn.has(flow))) {
        leadsIn.add(id);
      }
    }
    // Leading in: as late as what it feeds allows, consumers first, so the chain ends right above its first reader.
    for (let i = describedFlows.length - 1; i >= 0; i--) {
      const id = describedFlows[i];
      if (leadsIn.has(id)) {
        const readers = fed.get(id)!.filter((flow) => band.has(flow)).map((flow) => band.get(flow)!);
        place(id, beside(Math.min(...readers), -1));
      }
    }
    // Only consuming: right after the latest thing it reads, producers first.
    for (const id of describedFlows) {
      if (!leadsIn.has(id)) {
        const feeders = flowsBeyond(id, (node) => backward.get(node)!)
          .filter((flow) => band.has(flow))
          .map((flow) => band.get(flow)!);
        place(id, beside(Math.max(0, ...feeders), 1));
      }
    }
  }
  // What only descriptions name: under the step that writes it, as data is under the flow that writes it.
  for (const id of order) {
    if (!isDescribed(id) || section.has(id)) {
      continue;
    }
    const writers = backward.get(id)!.filter((producer) => band.has(producer));
    if (writers.length > 0) {
      const at = Math.max(...writers.map((producer) => band.get(producer)!));
      const under = writers
        .filter((producer) => section.get(producer) === "after" && band.get(producer) === at)
        .map((producer) => depth.get(producer)!);
      band.set(id, at);
      section.set(id, "after");
      depth.set(id, 1 + Math.max(0, ...under));
    }
  }
  // Where a described chain starts (nothing drawn writes it): above its first reader. One nothing reads either sits
  // above wave 1, with the rest of what lies before the estate.
  for (let i = order.length - 1; i >= 0; i--) {
    const id = order[i];
    if (!isDescribed(id) || section.has(id)) {
      continue;
    }
    const readers = forward(id).filter((reader) => band.has(reader));
    const first = readers.length === 0 ? 0.5 : Math.min(...readers.map((reader) => band.get(reader)!));
    const over = readers
      .filter((reader) => section.get(reader) === "before" && band.get(reader) === first)
      .map((reader) => depth.get(reader)!);
    band.set(id, first);
    section.set(id, "before");
    depth.set(id, 1 + Math.max(0, ...over));
  }

  // ---- Lines, in reading order: a band's inputs (the furthest from its flows first), its flows, what they write.
  const lineKey = (id: string) => {
    const part = section.get(id)!;
    const offset = part === "before" ? -depth.get(id)! : part === "after" ? depth.get(id)! : 0;
    return { band: band.get(id)!, offset, part };
  };
  const lines = new Map<string, Line & { offset: number }>();
  for (const id of roots) {
    const key = lineKey(id);
    const name = `${key.band}/${key.offset}`;
    const line = lines.get(name)
      ?? lines.set(name, { band: key.band, section: key.part, depth: Math.abs(key.offset), offset: key.offset, ids: [] }).get(name)!;
    line.ids.push(id);
  }
  const ordered = [...lines.values()].sort((a, b) => a.band - b.band || a.offset - b.offset);

  // ---- Order within a line: by where a node's neighbours sit, both ways, to cut crossings. A node's place is the
  // share of its line it sits at, so lines of different lengths compare.
  const place = new Map<string, number>();
  const settle = (line: Line) => line.ids.forEach((id, i) => place.set(id, (i + 0.5) / line.ids.length));
  for (const line of ordered) {
    line.ids.sort((a, b) => a.localeCompare(b));
    settle(line);
  }
  const lineOf = new Map<string, number>();
  ordered.forEach((line, index) => line.ids.forEach((id) => lineOf.set(id, index)));
  const pull = (id: string, neighbours: Map<string, string[]>, earlier: boolean): number => {
    const own = lineOf.get(id)!;
    const relevant = neighbours.get(id)!.filter((other) => (earlier ? lineOf.get(other)! < own : lineOf.get(other)! > own));
    return relevant.length === 0
      ? place.get(id)!
      : relevant.reduce((sum, other) => sum + place.get(other)!, 0) / relevant.length;
  };
  for (let pass = 0; pass < 6; pass++) {
    for (const line of ordered) {
      const weight = new Map(line.ids.map((id) => [id, pull(id, incoming, true)]));
      line.ids.sort((a, b) => weight.get(a)! - weight.get(b)! || a.localeCompare(b));
      settle(line);
    }
    for (let i = ordered.length - 1; i >= 0; i--) {
      const line = ordered[i];
      const weight = new Map(line.ids.map((id) => [id, pull(id, outgoing, false)]));
      line.ids.sort((a, b) => weight.get(a)! - weight.get(b)! || a.localeCompare(b));
      settle(line);
    }
  }

  // ---- Positions. Along the axis data runs on, line after line and band after band; across it, each line centered.
  const lanes: Lane[] = [];
  let cursor = 0;
  let crossStart = Number.POSITIVE_INFINITY;
  let crossEnd = Number.NEGATIVE_INFINITY;
  const down = orientation === "down";
  const thickness = down ? size.height : size.width;
  const breadth = down ? size.width : size.height;
  // The same rooms between cards whatever their size: a taller or wider card pushes its neighbours out by as much.
  const nodeSpacing = NODE_SPACING + size.width - NODE_WIDTH;
  const lineSpacing = LINE_SPACING + size.height - NODE_HEIGHT;
  const columnNodeSpacing = COLUMN_NODE_SPACING + size.height - NODE_HEIGHT;
  const put = (id: string, along: number, across: number) => {
    positions.set(id, down ? { x: across, y: along } : { x: along, y: across });
    crossStart = Math.min(crossStart, across);
    crossEnd = Math.max(crossEnd, across + breadth);
  };

  ordered.forEach((line, index) => {
    const previous = ordered[index - 1];
    if (previous !== undefined) {
      cursor += thickness + (previous.band === line.band ? size.lineGap ?? BAND_LINE_GAP : size.bandGap ?? BAND_GAP);
    }
    let lane = lanes[lanes.length - 1];
    if (lane === undefined || lane.rank !== line.band) {
      lane = { rank: line.band, wave: null, described: false, start: cursor, end: cursor + thickness };
      lanes.push(lane);
    }

    if (down) {
      // A line holding more cards than a row has room for is laid in several rows, its nodes dealt to them in turn
      // and each row shifted by a fraction of a card, like bricks: neighbours in the order stay near each other, and
      // an arrow leaving an upper row runs down through the gaps of the rows under it instead of behind their cards.
      const rows = Math.ceil(line.ids.length / MAX_ROW_NODES);
      const slots = Math.ceil(line.ids.length / rows);
      const width = (slots - 1 + (rows - 1) / rows) * nodeSpacing;
      line.ids.forEach((id, i) => {
        const row = i % rows;
        put(id, cursor + row * lineSpacing, (Math.floor(i / rows) + row / rows) * nodeSpacing - width / 2 - size.width / 2);
      });
      cursor += (rows - 1) * lineSpacing;
    } else {
      const height = (line.ids.length - 1) * columnNodeSpacing;
      line.ids.forEach((id, i) => put(id, cursor, i * columnNodeSpacing - height / 2 - size.height / 2));
    }
    lane.end = cursor + thickness;
  });

  // A band is named by its wave only when that is true of every flow in it. A band of the described lineage is one
  // that holds nothing else.
  for (const lane of lanes) {
    const waves = new Set<number>();
    let unwaved = false;
    lane.described = nodes.some((node) => band.get(node.id) === lane.rank)
      && nodes.every((node) => band.get(node.id) !== lane.rank || node.described === true);
    for (const node of nodes) {
      if (node.flow && band.get(node.id) === lane.rank) {
        if (node.waves.length === 0) {
          unwaved = true;
        }
        for (const wave of node.waves) {
          waves.add(wave);
        }
      }
    }
    lane.wave = !unwaved && waves.size === 1 && [...waves][0] === lane.rank ? lane.rank : null;
  }

  return { positions, lanes, crossStart, crossEnd };
}
