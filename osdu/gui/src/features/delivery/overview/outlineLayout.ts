// Where everything on a mapping's overview goes (osdu/docs/reference/concepts/mapping-overview.md). One value is one level line,
// from the table on the left to the property on the right. What a value is looked up in hangs on a shelf above, each table with a
// column of its own, and a dot says where a line crosses a table it reads. No drawing and no DOM here, so the geometry can be
// reasoned about and tested apart from D3.

import { dotId, fieldId, isAbove, type CardField, type OutlineCard, type OutlineRow, type Pill } from "./outlineModel.ts";

/** The canvas's measures, in px at 100%. */
export const MEASURE = {
  /** The pitch of the record's rows, and so of the lines. */
  row: 24,
  /** The pitch of a table's fields. */
  field: 22,
  /** A shelf table's field: a shelf is read at a glance, and is kept short. */
  shelfField: 18,
  /** A table's heading: its name and what a row of it is. */
  head: 42,
  /** What a table keeps under its last field. */
  foot: 24,
  cardGap: 14,
  sourceWidth: 240,
  /** Where the lines leaving a table curve to their level. */
  gutter: 104,
  /** Between two tables' columns of dots. */
  columnPitch: 36,
  shelfWidth: 172,
  shelfGap: 12,
  /** The room the lines take dropping from the shelf to their columns. */
  shelfDrop: 40,
  recordMin: 296,
  recordMax: 392,
  indent: 14,
  pillHeight: 18,
  pillGap: 5,
  /** The room kept for marks beside the record, whatever is open. */
  marksMin: 72,
  marksMax: 216,
} as const;

/** The advance of JetBrains Mono, the face everything measured here is set in, as a share of its size. */
const MONO_ADVANCE = 0.6;

/** The width of `text` in the mono face at `size` px. */
export function textWidth(text: string, size: number): number {
  return text.length * size * MONO_ADVANCE;
}

/** `text` cut to what fits `width` at `size` px, ending in an ellipsis when anything is cut. */
export function fitText(text: string, width: number, size: number): string {
  const room = Math.floor(width / (size * MONO_ADVANCE));
  if (text.length <= room) {
    return text;
  }

  return room <= 1 ? "…" : `${text.slice(0, room - 1)}…`;
}

/**
 * A value cut to what fits, a reference keeping its end, since that is the part that tells two apart:
 * `…UnitOfMeasure:M:` of `dev:reference-data--UnitOfMeasure:M:`.
 */
export function fitValue(text: string, width: number, size: number): string {
  const room = Math.floor(width / (size * MONO_ADVANCE));
  if (text.length <= room) {
    return text;
  }

  return text.includes("--") ? `…${text.slice(text.length - room + 1)}` : fitText(text, width, size);
}

/** The face and size of a mark's word. */
export const PILL_FONT = 11;

/** A mark's word with how many it stands for. */
export function pillText(pill: Pill): string {
  return pill.count > 1 ? `${pill.label} ×${pill.count}` : pill.label;
}

function pillWidth(pill: Pill): number {
  return Math.ceil(textWidth(pillText(pill), PILL_FONT)) + 12;
}

function pillsWidth(pills: readonly Pill[]): number {
  return pills.reduce((sum, pill) => sum + pillWidth(pill) + MEASURE.pillGap, 0);
}

export interface PlacedField {
  field: CardField;
  /** Its middle, in the canvas's coordinates. */
  y: number;
}

export interface PlacedCard {
  card: OutlineCard;
  x: number;
  y: number;
  width: number;
  height: number;
  /** On the shelf, rather than feeding lines from the left. */
  shelf: boolean;
  fields: PlacedField[];
  /** Where a shelf table's dots run. */
  column: number | null;
}

export interface PlacedPill {
  id: string;
  pill: Pill;
  /** Its left edge. */
  x: number;
  width: number;
}

export interface PlacedRow {
  row: OutlineRow;
  /** Its middle: the level its line runs at. */
  y: number;
  /** Where its line starts, or null for a row no value reaches by a line. */
  lineFrom: number | null;
  /** The source its line starts from, whose tone it takes. */
  from: string | null;
  pills: PlacedPill[];
  /** A literal's chip: what it writes, cut to fit. */
  literal: { text: string; x: number; width: number } | null;
}

export interface PlacedDot {
  id: string;
  row: string;
  source: string;
  x: number;
  y: number;
}

/** The line a table drops to its column on, and on down to the last row reading it. */
export interface PlacedDrop {
  source: string;
  /** A path from the table to the top of its column. */
  curve: string;
  x: number;
  from: number;
  to: number;
}

/** A line from a field of a table on the left to the row it enters. */
export interface PlacedWire {
  id: string;
  field: string;
  source: string;
  row: string;
  d: string;
  /** A condition tests the field rather than taking its value. */
  dashed: boolean;
  /** It stands for a whole table's rows reaching a repeater. */
  heavy: boolean;
}

export interface OutlineLayout {
  width: number;
  height: number;
  cards: PlacedCard[];
  rows: PlacedRow[];
  dots: PlacedDot[];
  drops: PlacedDrop[];
  wires: PlacedWire[];
  record: { x: number; y: number; width: number; height: number };
  /** Where the lanes run, for their headings. */
  lanes: { tables: number; marks: number; record: number; top: number };
}

/** A smooth line leaving level from one point and arriving level at another. */
export function levelCurve(x1: number, y1: number, x2: number, y2: number): string {
  const reach = (x2 - x1) * 0.5;
  return `M${x1},${y1}C${x1 + reach},${y1} ${x2 - reach},${y2} ${x2},${y2}`;
}

/** A smooth line leaving straight down from one point and arriving straight down at another. */
export function droppingCurve(x1: number, y1: number, x2: number, y2: number): string {
  const reach = (y2 - y1) * 0.5;
  return `M${x1},${y1}C${x1},${y1 + reach} ${x2},${y2 - reach} ${x2},${y2}`;
}

/** The height of a shelf table: a heading and its fields, or a line for a table of pairs, which names no field. */
function shelfHeight(card: OutlineCard): number {
  return MEASURE.head + Math.max(card.fields.length, 1) * MEASURE.shelfField + 8;
}

function tableHeight(card: OutlineCard): number {
  return card.folded ? MEASURE.head : MEASURE.head + card.fields.length * MEASURE.field + (card.unread.length > 0 ? MEASURE.foot : 6);
}

/** The first row reading a source, which orders the shelf so the dots step down the page rather than scatter. */
function firstRow(rows: readonly OutlineRow[], source: string): number {
  const at = rows.findIndex((row) => row.lookups.some((lookup) => lookup.source === source) || row.reads.some((read) => read.source === source));
  return at < 0 ? rows.length : at;
}

/** Whether a row's line is reached from a table on the left. */
function fedFromLeft(row: OutlineRow, cards: ReadonlyMap<string, OutlineCard>): boolean {
  return row.reads.some((read) => {
    const card = cards.get(read.source);
    return card !== undefined && !isAbove(card.source);
  });
}

/** Whether a row reads a value of a source on the shelf that is not a lookup: a parameter, which draws as a dot all the same. */
function shelfReads(row: OutlineRow, cards: ReadonlyMap<string, OutlineCard>): string[] {
  return [...new Set(row.reads.filter((read) => {
    const card = cards.get(read.source);
    return card !== undefined && isAbove(card.source) && read.role === "value";
  }).map((read) => read.source))];
}

/** The widest a row of the record gets, its indent, chevron, name and what stands at its end. */
function recordWidthOf(measured: readonly OutlineRow[]): number {
  const widest = measured.reduce<number>((most, row) => {
    const name = textWidth(row.name, 12);
    const end = textWidth(row.node.type ?? "", 11);
    return Math.max(most, 28 + row.depth * MEASURE.indent + 18 + name + 12 + end + 14);
  }, MEASURE.recordMin);
  return Math.min(MEASURE.recordMax, Math.ceil(widest));
}

/**
 * Lays out `rows` and `cards`. `measured` is every row with everything open, which fixes the lanes' widths, so opening a row
 * moves things down and never sideways.
 */
export function layoutOf(rows: readonly OutlineRow[], cards: readonly OutlineCard[], measured: readonly OutlineRow[]): OutlineLayout {
  const byId = new Map(cards.map((card) => [card.id, card]));
  const shelfCards = cards
    .filter((card) => isAbove(card.source))
    .sort((a, b) => (a.source.kind === "parameters" ? -1 : b.source.kind === "parameters" ? 1 : firstRow(measured, a.id) - firstRow(measured, b.id)));
  const tableCards = cards.filter((card) => !isAbove(card.source));

  const before = Math.min(MEASURE.marksMax, measured.reduce((most, row) => Math.max(most, pillsWidth(row.before)), 0));
  const after = Math.max(MEASURE.marksMin, Math.min(MEASURE.marksMax, measured.reduce((most, row) => Math.max(most, pillsWidth(row.after), row.literal === null ? 0 : 96), 0) + 8));
  const recordWidth = recordWidthOf(measured);

  const linesFrom = tableCards.length === 0 ? MEASURE.gutter : MEASURE.sourceWidth + MEASURE.gutter;
  const columnsFrom = linesFrom + before + (shelfCards.length > 0 ? MEASURE.columnPitch * 0.5 : 0);
  const columnsWidth = shelfCards.length * MEASURE.columnPitch;
  const marksFrom = columnsFrom + columnsWidth;
  const recordX = marksFrom + after + 14;
  const width = recordX + recordWidth;

  // The shelf sits over the lanes, its tables in the order of their columns, so the lines dropping to the columns do not cross.
  const shelfWidth = shelfCards.length * MEASURE.shelfWidth + Math.max(0, shelfCards.length - 1) * MEASURE.shelfGap;
  const shelfHeightMost = shelfCards.reduce((most, card) => Math.max(most, shelfHeight(card)), 0);
  const top = shelfCards.length === 0 ? 0 : shelfHeightMost + MEASURE.shelfDrop;
  const canvasWidth = Math.max(width, shelfWidth);
  const columnsMiddle = columnsFrom + columnsWidth / 2;
  const shelfX = Math.max(0, Math.min(canvasWidth - shelfWidth, columnsMiddle - shelfWidth / 2));

  const placedRows = new Map<string, PlacedRow>();
  const ordered: PlacedRow[] = rows.map((row, index) => {
    const y = top + MEASURE.head + index * MEASURE.row + MEASURE.row / 2;
    const placed: PlacedRow = { row, y, lineFrom: null, from: null, pills: [], literal: null };
    placedRows.set(row.path, placed);
    return placed;
  });

  const placedCards: PlacedCard[] = [];
  const columns = new Map<string, number>();
  shelfCards.forEach((card, index) => {
    const column = columnsFrom + index * MEASURE.columnPitch + MEASURE.columnPitch / 2;
    columns.set(card.id, column);
    const x = shelfX + index * (MEASURE.shelfWidth + MEASURE.shelfGap);
    placedCards.push({
      card,
      x,
      y: 0,
      width: MEASURE.shelfWidth,
      height: shelfHeightMost,
      shelf: true,
      column,
      fields: card.fields.map((field, at) => ({ field, y: MEASURE.head + at * MEASURE.shelfField + MEASURE.shelfField / 2 })),
    });
  });

  // A table starts level with what it feeds where it can: the record's first row, a child table its repeater.
  let next = top;
  for (const card of tableCards) {
    const repeater = card.repeater === null ? undefined : placedRows.get(card.repeater);
    const y = Math.max(next, repeater === undefined ? top : repeater.y - MEASURE.head / 2);
    const height = tableHeight(card);
    placedCards.push({
      card,
      x: 0,
      y,
      width: MEASURE.sourceWidth,
      height,
      shelf: false,
      column: null,
      fields: card.folded ? [] : card.fields.map((field, at) => ({ field, y: y + MEASURE.head + at * MEASURE.field + MEASURE.field / 2 })),
    });
    next = y + height + MEASURE.cardGap;
  }

  const fieldY = new Map<string, number>();
  const headY = new Map<string, number>();
  for (const placed of placedCards) {
    headY.set(placed.card.id, placed.y + MEASURE.head / 2);
    for (const field of placed.fields) {
      fieldY.set(field.field.id, field.y);
    }
  }

  const wires: PlacedWire[] = [];
  const dots: PlacedDot[] = [];
  const reach = new Map<string, number>();
  for (const placed of ordered) {
    const { row } = placed;
    const fed = fedFromLeft(row, byId);
    const held = [...row.lookups.map((lookup) => lookup.source), ...shelfReads(row, byId)];
    const crossed = [...new Set(held)].filter((source) => columns.has(source));

    for (const source of crossed) {
      const x = columns.get(source)!;
      dots.push({ id: dotId(row.path, source), row: row.path, source, x, y: placed.y });
      reach.set(source, Math.max(reach.get(source) ?? 0, placed.y));
    }

    const firstColumn = crossed.reduce((least, source) => Math.min(least, columns.get(source)!), Number.POSITIVE_INFINITY);
    if (fed) {
      placed.lineFrom = linesFrom;
      placed.from = row.reads.find((read) => {
        const card = byId.get(read.source);
        return card !== undefined && !isAbove(card.source);
      })?.source ?? null;
      for (const read of row.reads) {
        const card = byId.get(read.source);
        if (card === undefined || isAbove(card.source)) {
          continue;
        }

        const id = fieldId(read.source, read.field);
        const y = fieldY.get(id) ?? headY.get(read.source);
        if (y === undefined) {
          continue;
        }

        // A folded table reaches its repeater as one line, whatever it holds.
        const key = card.folded ? `${read.source}\u0000*` : id;
        const wire = `${key}\u0000${row.path}`;
        if (!wires.some((known) => known.id === wire)) {
          wires.push({
            id: wire,
            field: id,
            source: read.source,
            row: row.path,
            d: levelCurve(MEASURE.sourceWidth, y, linesFrom, placed.y),
            dashed: read.role === "condition",
            heavy: card.folded,
          });
        }
      }
    } else if (Number.isFinite(firstColumn)) {
      placed.lineFrom = firstColumn;
      placed.from = crossed.find((source) => columns.get(source) === firstColumn) ?? null;
    }

    if (row.repeats !== null) {
      const source = `dataset.${row.repeats}`;
      const y = headY.get(source);
      const wire = `${source}\u0000repeat\u0000${row.path}`;
      if (y !== undefined && !wires.some((known) => known.id === `${source}\u0000*\u0000${row.path}`)) {
        placed.lineFrom ??= linesFrom;
        placed.from ??= source;
        wires.push({ id: wire, field: "", source, row: row.path, d: levelCurve(MEASURE.sourceWidth, y, linesFrom, placed.y), dashed: false, heavy: true });
      }
    }

    placed.pills = placePills(row, linesFrom, recordX, after);
    if (row.literal !== null) {
      const text = fitText(row.literal, after - 16, PILL_FONT);
      const chip = Math.ceil(textWidth(text, PILL_FONT)) + 12;
      placed.literal = { text, x: recordX - 12 - chip, width: chip };
      placed.lineFrom ??= recordX - 12;
    }
  }

  const drops: PlacedDrop[] = [];
  for (const placed of placedCards) {
    if (placed.column !== null) {
      const fromX = placed.x + placed.width / 2;
      const fromY = placed.height;
      drops.push({
        source: placed.card.id,
        curve: droppingCurve(fromX, fromY, placed.column, top),
        x: placed.column,
        from: top,
        to: reach.get(placed.card.id) ?? top,
      });
    }
  }

  const recordHeight = MEASURE.head + rows.length * MEASURE.row + 6;
  const tablesBottom = placedCards.filter((card) => !card.shelf).reduce((most, card) => Math.max(most, card.y + card.height), 0);
  return {
    width: canvasWidth,
    height: Math.max(top + recordHeight, tablesBottom),
    cards: placedCards,
    rows: ordered,
    dots,
    drops,
    wires,
    record: { x: recordX, y: top, width: recordWidth, height: recordHeight },
    lanes: { tables: 0, marks: linesFrom, record: recordX, top },
  };
}

/** A row's marks: those before the tables at the start of its line, those after them against the record, as many as fit. */
function placePills(row: OutlineRow, linesFrom: number, recordX: number, after: number): PlacedPill[] {
  const placed: PlacedPill[] = [];
  let x = linesFrom + 8;
  row.before.forEach((pill, index) => {
    const width = pillWidth(pill);
    placed.push({ id: `${row.path}\u0000b${index}`, pill, x, width });
    x += width + MEASURE.pillGap;
  });

  // What is folded can hold more kinds of mark than the lane has room for: the rest are counted in a last mark. What fits, fits.
  const fitted: Pill[] = [];
  if (pillsWidth(row.after) <= after) {
    fitted.push(...row.after);
  } else {
    let room = after - 34;
    for (const pill of row.after) {
      const width = pillWidth(pill) + MEASURE.pillGap;
      if (width > room) {
        break;
      }

      fitted.push(pill);
      room -= width;
    }

    const rest = row.after.slice(fitted.length);
    fitted.push({ label: `+${rest.length}`, detail: rest.map(pillText).join(", "), text: "Also in here", count: 1, gate: false });
  }

  let end = recordX - 12;
  for (let index = fitted.length - 1; index >= 0; index--) {
    const width = pillWidth(fitted[index]);
    end -= width;
    placed.push({ id: `${row.path}\u0000a${index}`, pill: fitted[index], x: end, width });
    end -= MEASURE.pillGap;
  }

  return placed;
}
