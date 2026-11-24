import { Columns3, CornerDownRight, KeyRound, Layers, Tag, type LucideIcon } from "lucide-react";
import type {
  BlueprintColumn, BlueprintRead, BlueprintRecords, BlueprintRole, DeliveryDimensionKey, DimensionBlueprint,
  DimensionYamlSpan,
} from "../../../api/delivery";

/**
 * The parts of a blueprint a page points at: the dimension's own records (`card:source`), records read by id
 * (`card:<id>`), the table (`card:table`), a read (`read:<id>`) and a column (`col:<name>`).
 */
export type BlueprintElement = string;

export const SOURCE_CARD = "card:source";
export const TABLE_CARD = "card:table";

export const readElement = (readId: string): BlueprintElement => `read:${readId}`;
export const columnElement = (name: string): BlueprintElement => `col:${name}`;
export const recordsElement = (recordsId: string): BlueprintElement => `card:${recordsId}`;

/** The read of the dimension's key, which every chain starts from. */
export const KEY_READ = "key";

/** The columns every dimension's table has, which no read is drawn to: the row's and key's numbers, the partition, the records, the filter and the row's two times. */
export const FIXED_COLUMNS = new Set(["id", "partition", "keyId", "records", "filter", "inserted", "updated"]);

/**
 * One way a column is made, from the key to the last read: the key alone (its own column, its number and filter), the
 * label (the value's column), an attribute read through the records a key names, or one collected from the dimension's
 * own records.
 */
export interface BlueprintChain {
  id: string;
  role: BlueprintRole;
  attribute: string | null;
  /** The reads in order, the key first for every chain but a collected one. */
  reads: string[];
  /** The columns written from the chain's last read. */
  columns: string[];
}

/** A line drawn between two parts: a read naming the records read next (`follow`), or a read a column is written from (`value`). */
export interface BlueprintEdge {
  id: string;
  kind: "follow" | "value";
  from: BlueprintElement;
  to: BlueprintElement;
  chains: string[];
}

/** One lane of the diagram, left to right: the dimension's own records, each depth of records read by id, then the table. */
export interface BlueprintLane {
  id: string;
  depth: number;
  records: BlueprintRecords[];
}

/** Everything a page draws and links, laid out once per blueprint. */
export interface BlueprintLayout {
  blueprint: DimensionBlueprint;
  reads: Map<string, BlueprintRead>;
  /** The records each read is read from: `source`, or a records id. */
  readCard: Map<string, BlueprintElement>;
  records: Map<string, BlueprintRecords>;
  columns: Map<string, BlueprintColumn>;
  chains: BlueprintChain[];
  edges: BlueprintEdge[];
  lanes: BlueprintLane[];
  /** The deepest step a chain reaches, 0 when a dimension reads nothing through its keys. */
  depth: number;
  keyColumn: string;
  valueColumn: string;
}

/** Lays a blueprint out: its reads and columns by id, the chains that make each column, the lines between them and the lanes. */
export function layoutOf(blueprint: DimensionBlueprint): BlueprintLayout {
  const reads = new Map<string, BlueprintRead>();
  const readCard = new Map<string, BlueprintElement>();
  for (const read of blueprint.source.reads) {
    reads.set(read.id, read);
    readCard.set(read.id, SOURCE_CARD);
  }

  const records = new Map<string, BlueprintRecords>();
  for (const node of blueprint.records) {
    records.set(node.id, node);
    for (const read of node.reads) {
      reads.set(read.id, read);
      readCard.set(read.id, recordsElement(node.id));
    }
  }

  const columns = new Map(blueprint.columns.map((column) => [column.name, column]));
  const keyColumn = blueprint.columns.find((column) => column.role === "key")?.name ?? "key";
  const valueColumn = blueprint.columns.find((column) => column.role === "value")?.name ?? "value";

  // Each chain's reads in the order of its steps; a read several chains share (one wellbore read for its name and its
  // country's id) belongs to each of them.
  const chainReads = new Map<string, { role: BlueprintRole; attribute: string | null; steps: Map<number, string> }>();
  for (const read of reads.values()) {
    for (const use of read.uses) {
      const id = chainId(use.role, use.attribute);
      const chain = chainReads.get(id) ?? { role: use.role, attribute: use.attribute, steps: new Map<number, string>() };
      chain.steps.set(use.step, read.id);
      chainReads.set(id, chain);
    }
  }

  const chains: BlueprintChain[] = [];
  for (const [id, chain] of chainReads) {
    const ordered = [...chain.steps.entries()].sort((a, b) => a[0] - b[0]).map(([, readId]) => readId);
    const all = chain.role === "key" || chain.role === "collect" ? ordered : [KEY_READ, ...ordered];
    const last = all[all.length - 1];
    chains.push({
      id,
      role: chain.role,
      attribute: chain.attribute,
      reads: all,
      columns: blueprint.columns.filter((column) => column.from.includes(last)).map((column) => column.name),
    });
  }

  const order: Record<BlueprintRole, number> = { key: 0, label: 1, attribute: 2, collect: 3 };
  chains.sort((a, b) => order[a.role] - order[b.role]);

  const edges: BlueprintEdge[] = [];
  for (const node of blueprint.records) {
    const through = chains.filter((chain) => chain.reads.includes(node.from) && node.reads.some((read) => chain.reads.includes(read.id)));
    edges.push({ id: `follow:${node.id}`, kind: "follow", from: readElement(node.from), to: recordsElement(node.id), chains: through.map((c) => c.id) });
  }

  for (const column of blueprint.columns) {
    if (FIXED_COLUMNS.has(column.role)) {
      continue;
    }

    for (const readId of column.from) {
      const through = chains.filter((chain) => chain.columns.includes(column.name) && chain.reads[chain.reads.length - 1] === readId);
      edges.push({ id: `value:${readId}:${column.name}`, kind: "value", from: readElement(readId), to: columnElement(column.name), chains: through.map((c) => c.id) });
    }
  }

  const depth = blueprint.records.reduce((deepest, node) => Math.max(deepest, node.depth), 0);
  const lanes: BlueprintLane[] = [{ id: "source", depth: 0, records: [] }];
  for (let at = 1; at <= depth; at++) {
    lanes.push({ id: `depth-${at}`, depth: at, records: blueprint.records.filter((node) => node.depth === at) });
  }

  lanes.push({ id: "table", depth: depth + 1, records: [] });
  return { blueprint, reads, readCard, records, columns, chains, edges, lanes, depth, keyColumn, valueColumn };
}

export function chainId(role: BlueprintRole, attribute: string | null): string {
  return attribute === null ? role : `${role}:${attribute}`;
}

/** The chains a part of the diagram belongs to, so pointing at it lights every read and column that makes the same thing. */
export function chainsOfElement(layout: BlueprintLayout, element: BlueprintElement): BlueprintChain[] {
  if (element.startsWith("read:")) {
    const readId = element.slice("read:".length);
    const read = layout.reads.get(readId);
    if (read === undefined) {
      return [];
    }

    const own = new Set(read.uses.map((use) => chainId(use.role, use.attribute)));
    return layout.chains.filter((chain) => own.has(chain.id));
  }

  if (element.startsWith("col:")) {
    const name = element.slice("col:".length);
    return layout.chains.filter((chain) => chain.columns.includes(name));
  }

  return [];
}

/**
 * What lights up when a person points at `elements`: the parts themselves, every read and column of the chains they
 * belong to, the records those reads are read from, and the lines between them. Pointing at records lights them, the
 * read naming them and the reads made of them.
 */
export function highlightOf(layout: BlueprintLayout, elements: BlueprintElement[]): { parts: Set<BlueprintElement>; chains: Set<string>; edges: Set<string> } {
  const parts = new Set<BlueprintElement>();
  const chains = new Set<string>();
  const edges = new Set<string>();
  for (const element of elements) {
    parts.add(element);
    if (element.startsWith("card:") && element !== SOURCE_CARD && element !== TABLE_CARD) {
      const node = layout.records.get(element.slice("card:".length));
      if (node !== undefined) {
        parts.add(readElement(node.from));
        node.reads.forEach((read) => parts.add(readElement(read.id)));
        edges.add(`follow:${node.id}`);
      }

      continue;
    }

    for (const chain of chainsOfElement(layout, element)) {
      chains.add(chain.id);
    }
  }

  for (const chain of layout.chains.filter((c) => chains.has(c.id))) {
    for (const readId of chain.reads) {
      parts.add(readElement(readId));
      const card = layout.readCard.get(readId);
      if (card !== undefined && card !== SOURCE_CARD) {
        parts.add(card);
      }
    }

    chain.columns.forEach((name) => parts.add(columnElement(name)));
  }

  for (const edge of layout.edges) {
    if (edge.chains.some((chain) => chains.has(chain))) {
      edges.add(edge.id);
    }
  }

  return { parts, chains, edges };
}

/** The reads of one chain step, by role and attribute. */
function readsOf(layout: BlueprintLayout, role: BlueprintRole, attribute: string | null, step?: number): BlueprintElement[] {
  const found: BlueprintElement[] = [];
  for (const read of layout.reads.values()) {
    if (read.uses.some((use) => use.role === role && use.attribute === attribute && (step === undefined || use.step === step))) {
      found.push(readElement(read.id));
    }
  }

  return found;
}

/**
 * The parts a line of the YAML declares, by the target the server names it with (`path`, `label.0`,
 * `attributes.Country.1`, `attributes.Source.collect`, `clean.2`, `columns.value`, ...).
 */
export function elementsOfTarget(layout: BlueprintLayout, target: string): BlueprintElement[] {
  const parts = target.split(".");
  const head = parts[0];
  switch (head) {
    case "kind":
    case "query":
    case "partitions":
    case "maxValues":
    case "countRecords":
      return [SOURCE_CARD];
    case "path":
      return [readElement(KEY_READ)];
    case "name":
      return [TABLE_CARD];
    case "label":
      return parts.length > 1 ? readsOf(layout, "label", null, Number(parts[1])) : [columnElement(layout.valueColumn)];
    case "unlabelled":
    case "clean":
      return [columnElement(layout.valueColumn)];
    case "columns":
      if (parts[1] === "key") {
        return [columnElement(layout.keyColumn)];
      }

      return parts[1] === "value" ? [columnElement(layout.valueColumn)] : [columnElement(layout.keyColumn), columnElement(layout.valueColumn)];
    case "attributes": {
      if (parts.length === 1) {
        return layout.blueprint.columns.filter((column) => column.role === "attribute").map((column) => columnElement(column.name));
      }

      // An attribute's name holds no dot (a letter, then letters, digits and underscores), so the parts after it are its own.
      const name = parts[1];
      if (parts.length === 2) {
        return [columnElement(name)];
      }

      return parts[2] === "collect" ? readsOf(layout, "collect", name) : readsOf(layout, "attribute", name, Number(parts[2]));
    }

    default:
      return [];
  }
}

/** Whether a target holds others (`attributes` holds `attributes.Country`), so only its key is marked when it lights. */
export function isContainer(target: string, spans: DimensionYamlSpan[]): boolean {
  return spans.some((span) => span.target.startsWith(`${target}.`));
}

/** The name a target's key is written with (`Country` for `attributes.Country`, `collect` for `attributes.Source.collect`). */
export function keyNameOf(target: string): string {
  const parts = target.split(".");
  return parts[parts.length - 1];
}

/**
 * The entity type's own name (`Wellbore` of `master-data--Wellbore`) and its group (`master-data`). A pattern's type with a
 * wildcard keeps its pattern (`*--Wellbore`), and a type that is all wildcard is every kind.
 */
export function entityName(entityType: string): { group: string; name: string } {
  if (entityType === "*") {
    return { group: "", name: "Every kind" };
  }

  if (entityType.includes("*")) {
    return { group: "", name: entityType };
  }

  const at = entityType.lastIndexOf("--");
  return at < 0 ? { group: "", name: entityType } : { group: entityType.slice(0, at), name: entityType.slice(at + 2) };
}

/** The entity type of a kind (`master-data--Wellbore` of `osdu:wks:master-data--Wellbore:1.3.0`), or the kind when it has no four parts. */
export function entityTypeOfKind(kind: string): string {
  const parts = kind.split(":");
  return parts.length === 4 ? parts[2] : kind;
}

/** The version of a kind (`1.3.0`), or none. */
export function versionOfKind(kind: string): string | null {
  const parts = kind.split(":");
  return parts.length === 4 ? parts[3] : null;
}

/** What one key read at a part of the diagram: the text, and where it came from when the part names a record. */
export interface ExampleValue {
  text: string;
  /** More values the key holds there, beyond the first (a collected attribute). */
  more: number;
  /** A value that is the id of a record read next. */
  reference: boolean;
  /** Every value the key holds there, the first among them. */
  all: string[];
}

/**
 * What one key's ledger row holds at each part of the diagram: the key at its read and column, its label at the label's
 * last read and the record it came from one step before, each attribute's value and its record likewise, the values it
 * collects, and its value and columns in the table. A step further back than the record a value came from is not kept,
 * so it shows nothing.
 */
export function examplesOf(layout: BlueprintLayout, key: DeliveryDimensionKey): Map<BlueprintElement, ExampleValue> {
  const shown = new Map<BlueprintElement, ExampleValue>();
  const one = (text: string, reference = false): ExampleValue => ({ text, more: 0, reference, all: [text] });
  shown.set(readElement(KEY_READ), one(key.key, true));
  shown.set(columnElement(layout.keyColumn), one(key.key, true));
  shown.set(columnElement("key_id"), one(String(key.keyId)));
  shown.set(columnElement("records"), one(key.count.toLocaleString("en-US")));
  if (key.filter !== null) {
    shown.set(columnElement("filter"), one(key.filter));
  }

  if (key.value !== null) {
    shown.set(columnElement(layout.valueColumn), one(key.value));
  }

  for (const chain of layout.chains) {
    const steps = chain.reads.filter((readId) => readId !== KEY_READ);
    const last = steps[steps.length - 1];
    if (last === undefined) {
      continue;
    }

    if (chain.role === "label") {
      if (key.label !== null) {
        shown.set(readElement(last), one(key.label));
      }

      if (steps.length > 1 && key.labelFrom !== null) {
        shown.set(readElement(steps[steps.length - 2]), one(key.labelFrom, true));
      }

      continue;
    }

    if (chain.attribute === null) {
      continue;
    }

    const held = key.attributes.filter((attribute) => attribute.name === chain.attribute);
    if (held.length === 0) {
      continue;
    }

    const value: ExampleValue = { text: held[0].value, more: held.length - 1, reference: false, all: held.map((attribute) => attribute.value) };
    shown.set(readElement(last), value);
    shown.set(columnElement(chain.attribute), value);
    if (chain.role === "attribute" && steps.length > 1 && held[0].from !== null) {
      shown.set(readElement(steps[steps.length - 2]), one(held[0].from, true));
    }
  }

  return shown;
}

/** The key a page shows first: of the keys with the most records, the one holding the most attributes, a labelled one first. */
export function exampleIndex(keys: DeliveryDimensionKey[]): number {
  let best = -1;
  let score = -1;
  keys.forEach((key, index) => {
    const held = new Set(key.attributes.map((attribute) => attribute.name)).size + (key.label === null ? 0 : 1);
    if (held > score) {
      best = index;
      score = held;
    }
  });

  return best;
}


/** The glyph each kind of read leads with, and what the legend calls it. */
export const ROLE_GLYPHS: Record<"key" | "label" | "attribute" | "step" | "collect", { icon: LucideIcon; label: string }> = {
  key: { icon: KeyRound, label: "key" },
  label: { icon: Tag, label: "value" },
  attribute: { icon: Columns3, label: "attribute" },
  step: { icon: CornerDownRight, label: "names the next record" },
  collect: { icon: Layers, label: "collected" },
};

/** A path's segments, a dot inside a filter (`[Type*=a.b]`) kept with its segment. */
export function splitPath(path: string): string[] {
  const segments: string[] = [];
  let current = "";
  let depth = 0;
  for (const c of path) {
    if (c === "[") {
      depth++;
    } else if (c === "]") {
      depth = Math.max(0, depth - 1);
    }

    if (c === "." && depth === 0) {
      segments.push(current);
      current = "";
      continue;
    }

    current += c;
  }

  segments.push(current);
  return segments;
}
