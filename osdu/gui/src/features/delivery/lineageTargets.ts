import type { DatasetJumpTarget, LineageJumpTarget } from "@/components/LineageJumpButton";

/** The system a mapping's lineage node belongs to (`OsduLineage.MappingSystem`). */
const MAPPING_SYSTEM = "osdu-mapping";

/** The system an OSDU type's lineage node belongs to (`OsduLineage.TypeSystem`). */
const TYPE_SYSTEM = "osdu-type";

/** The system a partition's cache types belong to (`OsduLineage.CacheSystem`). */
const CACHE_SYSTEM = "osdu-cache";

/** The group of a mapping whose mappings directory is the checkout's own root (`OsduLineage.RootGroup`). */
const ROOT_GROUP = ".";

/**
 * The namespace of a node declared by a flow that leaves its partitions to the registry (`PartitionNames.Every`): it
 * stands for every registered partition, so it is the node of whichever partition the workbench works in too.
 */
const EVERY_PARTITION = "*";

/** The namespaces a node of `partition` is declared under; none when the workbench works in no partition. */
function partitionNamespaces(partition: string | null): string[] | undefined {
  return partition === null ? undefined : [partition, EVERY_PARTITION];
}

/**
 * Every directory above a file of the checkout, nearest first, ending with the checkout's root. A mapping's node is
 * grouped under the mappings directory its flows name, and the document may sit in a folder below that directory.
 */
function directoriesAbove(relativePath: string): string[] {
  const segments = relativePath.replace(/\\/g, "/").split("/").filter((segment) => segment !== "").slice(0, -1);
  return [...segments.map((_, index) => segments.slice(0, segments.length - index).join("/")), ROOT_GROUP];
}

/**
 * A mapping document in the lineage graph: the node the delivery flows rendering with it read, as the partition the
 * workbench works in renders it. The same document rendered for another partition is another node, offered when
 * this partition has none.
 */
export function mappingLineageTarget(
  mapping: { reference: string; relativePath: string }, partition: string | null,
): DatasetJumpTarget {
  return {
    kind: "dataset",
    system: MAPPING_SYSTEM,
    name: mapping.reference,
    namespaces: partitionNamespaces(partition),
    groups: directoriesAbove(mapping.relativePath),
    label: mapping.reference,
    sublabel: mapping.relativePath,
  };
}

/**
 * A mapping known by its reference alone, as a mapping's `enrich:` names the enrichment mapping it renders: every node of
 * the reference, the partition's when it has one, whichever directory the document is filed in.
 */
export function mappingReferenceLineageTarget(reference: string, partition: string | null, sublabel?: string): DatasetJumpTarget {
  return {
    kind: "dataset",
    system: MAPPING_SYSTEM,
    name: reference,
    namespaces: partitionNamespaces(partition),
    label: reference,
    sublabel,
  };
}

/**
 * A type of a partition's cache in the lineage graph: the node its cache flows write and the mappings resolving against it
 * read. With no partition, every node of the name is offered.
 */
export function cacheTypeLineageTarget(
  type: { name: string; entityType?: string | null }, partition: string | null,
): DatasetJumpTarget {
  return {
    kind: "dataset",
    system: CACHE_SYSTEM,
    name: type.name,
    namespaces: partitionNamespaces(partition),
    label: type.name,
    sublabel: type.entityType === null || type.entityType === undefined || type.entityType === "" ? undefined : type.entityType,
  };
}

/**
 * An OSDU type in the lineage graph: the node of a kind in the partition, which the flows delivering it write and the flows
 * reading it read. A kind with wildcards, as a search may name, is a node of its own, read by the mappings searching it.
 */
export function osduTypeLineageTarget(kind: string, partition: string | null, label: string = kind): DatasetJumpTarget {
  return {
    kind: "dataset",
    system: TYPE_SYSTEM,
    name: kind,
    namespaces: partitionNamespaces(partition),
    label,
    sublabel: label === kind ? undefined : kind,
  };
}

/**
 * A table a delivery flow reads, in the lineage graph: the node its read lands on (the source tables' `objectKey`), which the
 * ingestion flow writing the table writes.
 */
export function tableLineageTarget(objectKey: string, title: string, subtitle: string | null): LineageJumpTarget {
  return { kind: "object", objectKey, objectKind: "Table", label: title, sublabel: subtitle ?? undefined };
}

/** A delivery flow in the lineage graph: its node in the repository's graph, with what it reads and what it delivers. */
export function flowLineageTarget(
  flow: { pipelineId: string; flow: string; interface: string | null }, repo: { id: string; name: string | null },
): LineageJumpTarget {
  return {
    kind: "node",
    repoId: repo.id,
    repoName: repo.name ?? repo.id,
    focusId: flow.pipelineId,
    label: flow.flow,
    sublabel: flow.interface === null ? undefined : `Interface ${flow.interface}`,
  };
}
