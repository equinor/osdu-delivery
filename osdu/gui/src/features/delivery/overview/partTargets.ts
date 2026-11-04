// The lineage picker's targets for what a part of a mapping's overview opens (osdu/docs/reference/concepts/mapping-overview.md,
// Opening a part in lineage): each node outlineLineage names, as the partition the workbench works in declares it.

import type { LineageJumpTarget } from "@/components/LineageJumpButton";
import type { DeliveryCachedType, DeliveryMappingFlow } from "../../../api/delivery";
import type { MappingOutlineSourceKind } from "../../../api/mappingOutline";
import {
  cacheTypeLineageTarget, flowLineageTarget, mappingReferenceLineageTarget, osduTypeLineageTarget, tableLineageTarget,
} from "../lineageTargets";
import type { SourceLineage } from "./outlineLineage.ts";

/** What places a node of the overview on the graph: the partition the workbench works in, the flow picked and its repository. */
export interface LineagePlace {
  partition: string | null;
  flow: DeliveryMappingFlow | null;
  repo: { id: string; name: string | null };
  /** The partition's cache types, whose entity types name them in the picker. */
  cached: readonly DeliveryCachedType[] | null;
}

/** What a target is on the canvas, which its glyph shows: a source, the mapping itself, or the OSDU type of the record it fills. */
export type PartMark = MappingOutlineSourceKind | "mapping" | "type";

/** One thing a part opens in lineage: what it is on the canvas, and the picker's target for its node. */
export interface PartTarget {
  id: string;
  title: string;
  mark: PartMark;
  target: LineageJumpTarget;
}

/** The picker's target for a source's node, as the partition the workbench works in declares it. */
function targetOf({ card, node }: SourceLineage, place: LineagePlace): LineageJumpTarget | null {
  switch (node.node) {
    case "table":
      return tableLineageTarget(node.objectKey, card.title, card.subtitle);
    case "flow":
      return place.flow === null ? null : flowLineageTarget(place.flow, place.repo);
    case "cache":
      return cacheTypeLineageTarget({ name: node.type, entityType: place.cached?.find((type) => type.name === node.type)?.entityType }, place.partition);
    case "type":
      return osduTypeLineageTarget(node.kind, place.partition, card.title);
    case "mapping":
      return mappingReferenceLineageTarget(node.reference, place.partition, card.role);
  }
}

/** The targets of what a part opens in lineage, each with what it is on the canvas. */
export function partTargets(lineage: readonly SourceLineage[], place: LineagePlace): PartTarget[] {
  return lineage.flatMap((source) => {
    const target = targetOf(source, place);
    return target === null ? [] : [{ id: source.card.id, title: source.card.title, mark: source.card.source.kind, target }];
  });
}
