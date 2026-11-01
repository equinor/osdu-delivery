import { LineageJumpButton } from "@/components/LineageJumpButton";
import type { DeliveryMapping } from "../../api/delivery";
import { useActivePartition } from "./activePartition";
import { mappingLineageTarget } from "./lineageTargets";

/** The jump to a mapping's node in the lineage graph: the flows rendering with it, and the cache types and kinds it reads. */
export function MappingLineage({ mapping }: { mapping: DeliveryMapping }) {
  const [partition] = useActivePartition();
  return <LineageJumpButton target={mappingLineageTarget(mapping, partition)} iconOnly />;
}
