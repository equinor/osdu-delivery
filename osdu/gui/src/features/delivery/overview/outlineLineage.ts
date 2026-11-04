// Where the parts of a mapping's overview stand in SQLFlow's lineage graph (osdu/docs/reference/concepts/mapping-overview.md,
// Opening a part in lineage): the node each table on the canvas is, and for a property the nodes of what its value comes from.
// A column is no node of its own, so it opens its table's. No drawing and no jump here: OverviewLineage turns these into the
// lineage picker's targets.

import type { DeliverySourceTable, MappingOutline, MappingOutlineNode } from "../../../api/mappingOutline.ts";
import { nodeAt, type OutlineCard } from "./outlineModel.ts";

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
 * The node a source on the canvas is, or null for one lineage holds no node of: a table the flow picked does not read, the
 * parameters with no flow picked, a search, parent or child whose kind is not known, the record an enrichment adds to.
 */
export function nodeOfCard(card: OutlineCard, outline: MappingOutline, scope: LineageScope): LineageNode | null {
  const { source } = card;
  switch (source.kind) {
    case "record":
    case "child": {
      const dataset = source.kind === "record" ? null : source.id.slice(source.id.indexOf(".") + 1);
      const table = scope.tables?.find((candidate) => candidate.dataset === dataset);
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
