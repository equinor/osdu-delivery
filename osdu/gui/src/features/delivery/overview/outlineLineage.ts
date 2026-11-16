// Where the parts of a mapping's overview stand in SQLFlow's lineage graph (osdu/docs/reference/concepts/mapping-overview.md,
// Opening a part in lineage): the node each table on the canvas is, and for a property the nodes of what its value comes from.
// A column is no node of its own, so it opens its table's; the assistant traces one by its name and its table's node, which
// is what a field of a table, and the columns some lines of the document read, are worked out as here. No drawing, no jump
// and no question here: OverviewLineage turns the nodes into the lineage picker's targets, and the page asks the assistant.

import type { LineageColumn } from "../../../../../../sqlflow/gui/src/features/chat/columnLineage.ts";
import type { DeliverySourceTable, MappingOutline, MappingOutlineNode, MappingOutlineRead } from "../../../api/mappingOutline.ts";
import { nodeAt, tableName, type OutlineCard } from "./outlineModel.ts";
import type { LineRange } from "./yamlExplain.ts";

/** A node of the lineage graph, as the module's lineage names it (osdu/docs/reference/concepts/lineage.md). */
export type LineageNode =
  /** A table the flow reads, by the key the flow's read of it lands on. */
  | { node: "table"; objectKey: string }
  /** The flow rendering with the mapping, which supplies its parameters. */
  | { node: "flow" }
  /** A type of the partition's cache, by its name. */
  | { node: "cache"; type: string }
  /** An OSDU type, by its kind: wildcards and all, as a search names it. */
  | { node: "type"; kind: string }
  /** A mapping known by its reference alone: an enrichment's. */
  | { node: "mapping"; reference: string };

/** A source on the canvas, and the node it is in lineage. */
export interface SourceLineage {
  card: OutlineCard;
  node: LineageNode;
}

/** What the page knows that places a source in lineage: the tables of the flow picked, and whether one is. */
export interface LineageScope {
  /** The flow's tables with their nodes; null while no flow is picked, or they are still being read. */
  tables: readonly DeliverySourceTable[] | null;
  flow: boolean;
}

/** A part of the canvas, as a pick or a menu names it. */
export type LineagePart = { type: "row"; path: string } | { type: "field"; source: string; field: string } | { type: "card"; source: string };

function filled(text: string | null | undefined): text is string {
  return text !== null && text !== undefined && text.trim() !== "";
}

/**
 * The table of the flow picked a source on the canvas is: the record table, or a child dataset's. Undefined for any other
 * source, and while no flow says which table it is.
 */
function tableOfCard(card: OutlineCard, scope: LineageScope): DeliverySourceTable | undefined {
  const { source } = card;
  if (source.kind !== "record" && source.kind !== "child") {
    return undefined;
  }

  const dataset = source.kind === "record" ? null : source.id.slice(source.id.indexOf(".") + 1);
  return scope.tables?.find((candidate) => candidate.dataset === dataset);
}

/**
 * The node a source on the canvas is, or null for one lineage holds no node of: a table the flow picked does not read, the
 * parameters with no flow picked, a search, parent or child whose kind is not known, the record an enrichment adds to.
 */
export function nodeOfCard(card: OutlineCard, outline: MappingOutline, scope: LineageScope): LineageNode | null {
  const { source } = card;
  switch (source.kind) {
    case "record":
    case "child": {
      const table = tableOfCard(card, scope);
      return table === undefined || !filled(table.objectKey) ? null : { node: "table", objectKey: table.objectKey };
    }
    case "parameters":
      return scope.flow ? { node: "flow" } : null;
    case "cache":
      return { node: "cache", type: source.name };
    case "search":
    case "parent":
    case "childRecord":
      return filled(source.detail) ? { node: "type", kind: source.detail.trim() } : null;
    case "link": {
      // The records an enrichment registers are its mapping's: `$link: <name>` names the enrichment, `enrich:` its mapping.
      const name = source.id.slice(source.id.indexOf(".") + 1);
      const reference = outline.enrichments.find((enrichment) => enrichment.name === name)?.mapping;
      return filled(reference) ? { node: "mapping", reference: reference.trim() } : null;
    }
    default:
      return null;
  }
}

/** The sources a property's value comes from, by id: what its lines read, what they are looked up in, and the child table it repeats. */
export function sourcesOfNode(node: MappingOutlineNode): Set<string> {
  const ids = new Set<string>();
  const walk = (at: MappingOutlineNode) => {
    for (const read of at.flow?.reads ?? []) {
      ids.add(read.source);
    }

    for (const step of at.flow?.steps ?? []) {
      for (const lookup of step.lookups) {
        ids.add(lookup.source);
      }
    }

    if (filled(at.dataset)) {
      ids.add(`dataset.${at.dataset}`);
    }

    at.children.forEach(walk);
  };

  walk(node);
  return ids;
}

/**
 * What a part opens in lineage, in the order the canvas lists the tables: a table's own node, a column's table's, and for a
 * property each node its value comes from, of everything folded under it too. Empty where lineage holds nothing to open, such
 * as a literal.
 */
export function lineageOfPart(part: LineagePart, outline: MappingOutline, cards: readonly OutlineCard[], scope: LineageScope): SourceLineage[] {
  let wanted: ReadonlySet<string>;
  if (part.type === "row") {
    const node = nodeAt(outline, part.path);
    wanted = node === null ? new Set() : sourcesOfNode(node);
  } else {
    wanted = new Set([part.source]);
  }

  return cards.flatMap((card) => {
    if (!wanted.has(card.id)) {
      return [];
    }

    const node = nodeOfCard(card, outline, scope);
    return node === null ? [] : [{ card, node }];
  });
}

/**
 * The column a field of a table on the canvas is, as the assistant is asked to trace it: its name, the table it belongs to
 * as the canvas names it, and that table's node. A column a lookup table gives the row
 * (osdu/docs/reference/flow/delivery-lookups.md) is the lookup table's, under the name it has there. Null where there is
 * no column to trace: a field of anything but a table of the flow picked (a parameter, a field of a cached type or of
 * another record), and a table lineage holds no node of.
 */
export function columnOfField(card: OutlineCard, field: string, scope: LineageScope): LineageColumn | null {
  const table = tableOfCard(card, scope);
  if (table === undefined || !filled(field)) {
    return null;
  }

  // A lookup names each column it gives a row `<name>_<column>`, so what follows its name and the mark is the column.
  const given = field.toLowerCase();
  const lookup = table.lookups.find((candidate) => candidate.columns.some((column) => column.name.toLowerCase() === given));
  if (lookup !== undefined) {
    return filled(lookup.objectKey)
      ? { column: field.slice(lookup.name.length + 1), object: tableName(lookup.table).title, objectKey: lookup.objectKey }
      : null;
  }

  return filled(table.objectKey) ? { column: field, object: tableName(table.table).title, objectKey: table.objectKey } : null;
}

/** The lines the document writes something on, or null where it writes it nowhere. */
function linesOf(written: { line?: number | null; endLine?: number | null }): LineRange | null {
  const { line, endLine } = written;
  return line === null || line === undefined || endLine === null || endLine === undefined ? null : { start: line, end: endLine };
}

/**
 * The table columns some lines of the document read, each once, in the order the lines read them: what the innermost
 * properties and alternatives whose lines reach into `range` read, and the rules on the rows stated there. A node the
 * document writes no lines for stands in its nearest ancestor that has them. A column is one however its case is written,
 * and what is no column of a table (a parameter, a field of the cache) is left out. Nothing for the whole mapping, a null
 * range, which reads too many to name.
 */
export function columnsOfLines(outline: MappingOutline, cards: readonly OutlineCard[], scope: LineageScope, range: LineRange | null): LineageColumn[] {
  if (range === null) {
    return [];
  }

  const first = Math.min(range.start, range.end);
  const last = Math.max(range.start, range.end);
  const reaches = (lines: LineRange) => lines.start <= last && first <= lines.end;
  const read: { line: number; reads: readonly MappingOutlineRead[] }[] = [];

  const walk = (nodes: readonly MappingOutlineNode[], held: LineRange | null): boolean => {
    let reached = false;
    for (const node of nodes) {
      const lines = linesOf(node) ?? held;
      if (lines !== null && !reaches(lines)) {
        continue;
      }

      const below = walk(node.children, lines);
      if (lines !== null && !below) {
        read.push({ line: lines.start, reads: node.flow?.reads ?? [] });
      }

      reached = reached || below || lines !== null;
    }

    return reached;
  };

  walk(outline.record, null);
  for (const rule of outline.dataset.rules ?? []) {
    const lines = linesOf(rule);
    if (lines !== null && reaches(lines)) {
      read.push({ line: lines.start, reads: rule.reads });
    }
  }

  const columns: LineageColumn[] = [];
  const seen = new Set<string>();
  // The sort keeps what starts on one line in the order it was found, which is the order the document writes it in.
  for (const { reads } of read.sort((a, b) => a.line - b.line)) {
    for (const { source, field } of reads) {
      const card = cards.find((candidate) => candidate.id === source);
      const column = card === undefined ? null : columnOfField(card, field, scope);
      if (column === null) {
        continue;
      }

      const id = `${column.objectKey}\u0000${column.column}`.toLowerCase();
      if (!seen.has(id)) {
        seen.add(id);
        columns.push(column);
      }
    }
  }

  return columns;
}
