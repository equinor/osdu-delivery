// The kinds of a partition as the explorer arranges them: a kind's parts, the places a reader narrows the records to (every
// type, a group, a type, a kind) and the kind patterns they are asked as, and the kinds as a tree of groups and types. A
// partition's own kinds need not name a group; those of one authority and source are then a group of their own.


/** The four parts of a kind, `authority:source:group--Type:version`, with the group and type of its entity type apart. */
export interface KindParts {
  authority: string;
  source: string;
  entityType: string;
  group: string;
  type: string;
  version: string;
}

export function kindParts(kind: string): KindParts {
  const [authority = "", source = "", entityType = "", version = ""] = kind.split(":");
  const dashes = entityType.indexOf("--");
  return {
    authority,
    source,
    entityType,
    group: dashes < 0 ? "" : entityType.slice(0, dashes),
    type: dashes < 0 ? entityType : entityType.slice(dashes + 2),
    version,
  };
}

/**
 * What the reader narrowed the records to: every type, one group of types, one type in every version, or one kind. A
 * partition's own kinds need not name a group (`acme:wells:Completion:1.0.0`): those of one authority and source are a
 * group of their own, named `authority:source` (a namespace), and a type of one is that type within its namespace.
 */
export type ExplorerScope =
  | { level: "all" }
  | { level: "group"; group: string }
  | { level: "type"; entityType: string; namespace?: string }
  | { level: "kind"; kind: string };

/** Whether a group is a namespace, `authority:source`, gathering the kinds whose entity type names no group. */
export function isNamespace(group: string): boolean {
  return group.includes(":");
}

/** The group a kind sits in: its entity type's group, or the namespace of a kind whose type names none (`other` for a pattern). */
export function groupOf(parts: KindParts): string {
  if (parts.group !== "") {
    return parts.group;
  }

  return /^[\w.-]+$/.test(parts.authority) && /^[\w.-]+$/.test(parts.source) ? `${parts.authority}:${parts.source}` : "other";
}

/** Every version of a kind's type: within its namespace where the type names no group; every type for a kind naming none. */
export function typeScopeOf(kind: string): ExplorerScope {
  const parts = kindParts(kind);
  if (!/^[\w.-]+$/.test(parts.entityType)) {
    return { level: "all" };
  }

  const group = groupOf(parts);
  return isNamespace(group) ? { level: "type", entityType: parts.entityType, namespace: group } : { level: "type", entityType: parts.entityType };
}

/** The kind pattern the search is asked of for a scope; none for every type. */
export function scopeKind(scope: ExplorerScope): string | undefined {
  switch (scope.level) {
    case "all":
      return undefined;
    case "group":
      return isNamespace(scope.group) ? `${scope.group}:*:*` : `*:*:${scope.group}--*:*`;
    case "type":
      return scope.namespace === undefined ? `*:*:${scope.entityType}:*` : `${scope.namespace}:${scope.entityType}:*`;
    default:
      return scope.kind;
  }
}

/** The kind pattern of every type, which the address carries once a reader picked every type. */
export const ALL_KINDS = "*:*:*:*";

/** The scope a kind pattern in the address names; every type for none, or for one the explorer did not write. */
export function scopeOf(kind: string | null): ExplorerScope {
  if (kind === null || kind.trim() === "" || kind === ALL_KINDS) {
    return { level: "all" };
  }

  const group = /^\*:\*:([\w.-]+)--\*:\*$/.exec(kind);
  if (group !== null) {
    return { level: "group", group: group[1] };
  }

  const namespace = /^([\w.-]+):([\w.-]+):\*:\*$/.exec(kind);
  if (namespace !== null) {
    return { level: "group", group: `${namespace[1]}:${namespace[2]}` };
  }

  const type = /^\*:\*:([\w.-]+):\*$/.exec(kind);
  if (type !== null) {
    return { level: "type", entityType: type[1] };
  }

  // A type naming no group, within its namespace; one naming a group is the type in every namespace, so a pattern that
  // names its authority and source is a kind pattern the explorer did not write.
  const own = /^([\w.-]+):([\w.-]+):([\w.-]+):\*$/.exec(kind);
  if (own !== null && !own[3].includes("--")) {
    return { level: "type", entityType: own[3], namespace: `${own[1]}:${own[2]}` };
  }

  return /^[\w.*-]+:[\w.*-]+:[\w.*-]+:[\d.*]+$/.test(kind) ? { level: "kind", kind } : { level: "all" };
}

/** Whether two scopes are the same. */
export function sameScope(a: ExplorerScope, b: ExplorerScope): boolean {
  return scopeKind(a) === scopeKind(b);
}

/** A scope as a reader says it: the type, the group, or every type. */
export function scopeLabel(scope: ExplorerScope): string {
  switch (scope.level) {
    case "all":
      return "every type";
    case "group":
      return scope.group;
    case "type":
      return kindParts(`*:*:${scope.entityType}:*`).type;
    default: {
      const parts = kindParts(scope.kind);
      return `${parts.type} ${parts.version}`;
    }
  }
}

/** A kind as the list of types gives it: its count, and its schema's status and scope where the Schema service lists it. */
export interface KindCount {
  kind: string;
  count: number;
  status?: string | null;
  scope?: string | null;
}

/** One kind of a type, with its count, and its schema's status and scope where the Schema service lists it. */
export interface KindNode {
  kind: string;
  parts: KindParts;
  count: number;
  status: string | null;
  scope: string | null;
}

/** One type (an entity type in every kind of it, within its namespace where it names no group), with its kinds and their count. */
export interface TypeNode {
  entityType: string;
  group: string;
  type: string;
  count: number;
  kinds: KindNode[];
  /** The place picking the type narrows to. */
  scope: ExplorerScope;
}

/** One group of types (master-data, reference-data, a namespace of types naming no group), with its types and their count. */
export interface GroupNode {
  group: string;
  /** The records picking the group finds: of its types, and for a namespace, of every kind of its authority and source. */
  count: number;
  types: TypeNode[];
  /** A namespace, `authority:source`, rather than a group the types name. */
  namespace: boolean;
  /** A group of a partition's own types (see {@link isCustomGroup}), listed under Custom. */
  custom: boolean;
}

/** The groups the OSDU data definitions name the types of records by. */
export const OSDU_GROUPS: ReadonlySet<string> = new Set(["dataset", "master-data", "reference-data", "work-product", "work-product-component"]);

/**
 * Whether a group holds a partition's own types: a group the OSDU data definitions do not name (`data-governance`), or a
 * namespace of types naming none (`acme:wells`). A partition's own kind of an OSDU type (`acme:custom:master-data--Wellbore`)
 * stays under that type, beside the platform's kinds of it, as the type's pattern finds them all.
 */
export function isCustomGroup(group: string): boolean {
  return !OSDU_GROUPS.has(group);
}

/**
 * The kinds a partition holds as groups of types, each in name order, with the counts summed upward: OSDU's own groups
 * first, then the custom ones (see {@link isCustomGroup}), the groups the types name before the namespaces of those that
 * name none.
 */
export function typeTree(kinds: KindCount[]): GroupNode[] {
  const groups = new Map<string, Map<string, KindNode[]>>();
  // What a namespace's pattern finds: every kind of its authority and source, those naming a group too.
  const spaces = new Map<string, number>();
  for (const { kind, count, status, scope } of kinds) {
    const parts = kindParts(kind);
    const group = groupOf(parts);
    const types = groups.get(group) ?? new Map<string, KindNode[]>();
    groups.set(group, types);
    types.set(parts.entityType, [...(types.get(parts.entityType) ?? []), { kind, parts, count, status: status ?? null, scope: scope ?? null }]);
    const space = `${parts.authority}:${parts.source}`;
    spaces.set(space, (spaces.get(space) ?? 0) + count);
  }

  const byName = (a: string, b: string) => a.localeCompare(b, "en", { sensitivity: "base" });
  return [...groups.entries()]
    .sort(([a], [b]) => Number(isCustomGroup(a)) - Number(isCustomGroup(b)) || Number(isNamespace(a)) - Number(isNamespace(b)) || byName(a, b))
    .map(([group, types]) => {
      const namespace = isNamespace(group);
      const nodes = [...types.entries()]
        .map(([entityType, kindNodes]): TypeNode => ({
          entityType,
          group,
          type: kindNodes[0].parts.type,
          count: kindNodes.reduce((sum, node) => sum + node.count, 0),
          kinds: [...kindNodes].sort((a, b) => byName(a.kind, b.kind)),
          scope: namespace ? { level: "type", entityType, namespace: group } : { level: "type", entityType },
        }))
        .sort((a, b) => byName(a.type, b.type));
      return {
        group, count: namespace ? spaces.get(group) ?? 0 : nodes.reduce((sum, node) => sum + node.count, 0), types: nodes, namespace, custom: isCustomGroup(group),
      };
    });
}

/** Whether a kind (or a kind pattern the list holds for a place) falls under a place: what the place's pattern would find. */
export function kindInScope(kind: string, scope: ExplorerScope): boolean {
  const pattern = scopeKind(scope);
  if (pattern === undefined) {
    return true;
  }

  const asked = pattern.split(":");
  const held = kind.split(":");
  return asked.length === 4 && held.length === 4 && asked.every((part, index) => partMatches(part, held[index]));
}

/** Whether one part of a kind matches a part of a pattern, a star standing for any run of characters. */
function partMatches(pattern: string, part: string): boolean {
  if (!pattern.includes("*")) {
    return pattern === part;
  }

  const expression = pattern.split("*").map((piece) => piece.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join(".*");
  return new RegExp(`^${expression}$`).test(part);
}

/** What a kind's row says on hover: the kind, its schema's status and scope, and whether the search finds any of its records. */
export function kindTitle(node: Pick<KindNode, "kind" | "count" | "status" | "scope">): string {
  const schema = node.status === null
    ? "No schema of it is listed by the Schema service."
    : `${node.status.charAt(0)}${node.status.slice(1).toLowerCase()} in the Schema service${node.scope === "INTERNAL" ? ", the partition's own" : node.scope === "SHARED" ? ", shared by the platform" : ""}.`;
  return `${node.kind}\n${schema}${node.count === 0 ? "\nThe search finds no record of it: none is stored, or none this connection may read." : ""}`;
}
