// What a mapping's overview shows, worked out from its outline (osdu/docs/reference/concepts/mapping-overview.md): which rows
// of the record are open, what each row carries, in what order a table's fields sit so the lines between them run parallel,
// and what lights up when something is pointed at. No drawing here: outlineLayout places these, and OverviewCanvas draws them.

import type {
  DeliverySourceTable, MappingOutline, MappingOutlineFlow, MappingOutlineHow, MappingOutlineLookup, MappingOutlineNode, MappingOutlineRead, MappingOutlineSource, MappingOutlineStep,
} from "../../../api/mappingOutline.ts";

/** What the cache knows of a type, which is what names a table of pairs. */
export interface CachedTypeFields {
  name: string;
  key: string | null;
  fields: string[];
}

/** A mark on a line: something done to the value on its way. */
export interface Pill {
  label: string;
  /** What it does, in plain words. */
  detail: string;
  /** How the mapping writes it. */
  text: string;
  /** How many steps of what is folded it stands for. */
  count: number;
  /** A condition rather than a change: it decides whether the value is written, and draws dashed. */
  gate: boolean;
}

/** One row of the record as it stands: a property, or what is folded under one. */
export interface OutlineRow {
  path: string;
  name: string;
  node: MappingOutlineNode;
  depth: number;
  /** It holds others, which open and close. */
  folding: boolean;
  open: boolean;
  /** The properties folded under it, when it is closed. */
  folded: number;
  /** What enters its line, each field once, of what is folded under it too. */
  reads: MappingOutlineRead[];
  /** The tables its line passes through, each once. */
  lookups: MappingOutlineLookup[];
  /** The marks before the first table it passes through. */
  before: Pill[];
  /** The marks after it. */
  after: Pill[];
  /** What a literal writes, shown in place of a line. */
  literal: string | null;
  /** The child dataset whose rows it repeats. */
  repeats: string | null;
}

/** The kinds of source that hang above the lines rather than feed them from the left. */
const ABOVE = new Set(["parameters", "cache", "search"]);

/** Whether a source hangs above the lines: what a value is looked up in, rather than the row it comes from. */
export function isAbove(source: MappingOutlineSource): boolean {
  return ABOVE.has(source.kind);
}

/** The steps a table does the whole of: they draw as a dot where the line crosses the table, not as a mark of their own. */
function isPureLookup(kind: string, lookups: readonly MappingOutlineLookup[]): boolean {
  return kind === "find" || (kind === "replace" && lookups.length > 0);
}

function pillOf(step: MappingOutlineFlow["steps"][number]): Pill {
  return { label: step.label, detail: step.detail, text: step.text, count: 1, gate: false };
}

/** The marks of a value's line, those before the first table it passes through apart from those after. */
function pillsOf(flow: MappingOutlineFlow): { before: Pill[]; after: Pill[] } {
  const first = flow.steps.findIndex((step) => step.lookups.length > 0);
  const marked = (from: number, to: number) => flow.steps
    .slice(from, to)
    .filter((step) => !isPureLookup(step.kind, step.lookups))
    .map(pillOf);
  return first < 0 ? { before: [], after: marked(0, flow.steps.length) } : { before: marked(0, first), after: marked(first, flow.steps.length) };
}

/** The properties a table reaches, or one field of it, in the order the document writes them. */
export function readersOf(outline: MappingOutline, source: string, field: string | null): MappingOutlineNode[] {
  return propertiesOf(outline.record).filter((node) => {
    const flows = [...leavesOf(node).map((leaf) => leaf.flow)];
    return flows.some((flow) => flow?.reads.some((read) => read.source === source && (field === null || read.field === field)) === true
      || flow?.steps.some((step) => step.lookups.some((lookup) => lookup.source === source
        && (field === null || lookup.field === field || lookup.matches.some((match) => match.field === field)))) === true);
  });
}

/** The value nodes under a node, it included. */
export function leavesOf(node: MappingOutlineNode): MappingOutlineNode[] {
  return node.children.length === 0 ? [node] : node.children.flatMap(leavesOf);
}

/** The properties a person counts: a `$coalesce` or a list is one, whatever it holds. */
export function propertiesOf(nodes: readonly MappingOutlineNode[]): MappingOutlineNode[] {
  return nodes.flatMap((node) => {
    if (node.shape === "choice" || node.shape === "list" || node.children.length === 0) {
      return [node];
    }

    return propertiesOf(node.children);
  });
}

function uniqueReads(reads: MappingOutlineRead[]): MappingOutlineRead[] {
  const seen = new Map<string, MappingOutlineRead>();
  for (const read of reads) {
    const key = `${read.source}\u0000${read.field}`;
    const known = seen.get(key);
    // A field that is a value somewhere and a condition elsewhere draws as a value.
    if (known === undefined || (known.role === "condition" && read.role !== "condition")) {
      seen.set(key, read);
    }
  }

  return [...seen.values()];
}

function uniqueLookups(lookups: MappingOutlineLookup[]): MappingOutlineLookup[] {
  const seen = new Map<string, MappingOutlineLookup>();
  for (const lookup of lookups) {
    const known = seen.get(lookup.source);
    if (known === undefined) {
      seen.set(lookup.source, { ...lookup, matches: [...lookup.matches] });
    } else {
      for (const match of lookup.matches) {
        if (!known.matches.some((m) => m.field === match.field)) {
          known.matches.push(match);
        }
      }
    }
  }

  return [...seen.values()];
}

/** What is folded under a node, as marks counted by kind, the most used first. */
function foldedPills(nodes: readonly MappingOutlineNode[]): Pill[] {
  const counted = new Map<string, Pill>();
  for (const leaf of nodes) {
    for (const step of leaf.flow?.steps ?? []) {
      if (isPureLookup(step.kind, step.lookups) || step.kind === "key") {
        continue;
      }

      const known = counted.get(step.kind);
      if (known === undefined) {
        counted.set(step.kind, { label: step.kind, detail: "", text: "", count: 1, gate: false });
      } else {
        known.count++;
      }
    }
  }

  return [...counted.values()].sort((a, b) => b.count - a.count);
}

/** The first node's text of an `$item`'s row and a repeater's row: what opens by default, read off how it reads. */
function rowOf(node: MappingOutlineNode, depth: number, open: ReadonlySet<string>): OutlineRow {
  const folding = node.children.length > 0;
  const isOpen = folding && open.has(node.path);
  const leaves = leavesOf(node);
  const under = isOpen ? [] : leaves;
  const flows = folding ? under.flatMap((leaf) => (leaf.flow === null || leaf.flow === undefined ? [] : [leaf.flow])) : node.flow === null || node.flow === undefined ? [] : [node.flow];
  const reads = uniqueReads(flows.flatMap((flow) => flow.reads));
  const lookups = uniqueLookups(flows.flatMap((flow) => flow.steps.flatMap((step) => step.lookups)));
  const pills = !folding && node.flow !== null && node.flow !== undefined ? pillsOf(node.flow) : { before: [], after: [] };
  const conditions: Pill[] = [];
  if (node.when !== null && node.when !== undefined) {
    conditions.push({ label: "when", detail: `Written only for a row where ${node.when}.`, text: `$when: ${node.when}`, count: 1, gate: true });
  }

  if (node.where !== null && node.where !== undefined) {
    conditions.push({ label: "where", detail: `Only the rows where ${node.where} become items.`, text: `$where: ${node.where}`, count: 1, gate: true });
  }

  let after = [...conditions, ...pills.after];
  if (folding && !isOpen) {
    after = node.shape === "choice"
      ? [{ label: `first of ${node.children.length}`, detail: "The first alternative that gives a value writes the property.", text: "$coalesce", count: 1, gate: false }, ...conditions]
      : [...conditions, ...foldedPills(leaves)];
  }

  return {
    path: node.path,
    name: node.name,
    node,
    depth,
    folding,
    open: isOpen,
    folded: folding && !isOpen ? (node.shape === "choice" ? 1 : propertiesOf(node.children).length) : 0,
    reads,
    lookups,
    before: pills.before,
    after,
    literal: !folding && node.flow?.origin === "literal" ? node.flow.literal ?? null : null,
    repeats: node.dataset ?? null,
  };
}

/** The rows of the record with `open` nodes opened, in the order the document writes them. */
export function rowsOf(outline: MappingOutline, open: ReadonlySet<string>): OutlineRow[] {
  const rows: OutlineRow[] = [];
  const walk = (nodes: readonly MappingOutlineNode[], depth: number) => {
    for (const node of nodes) {
      const row = rowOf(node, depth, open);
      rows.push(row);
      if (row.open) {
        walk(node.children, depth + 1);
      }
    }
  };

  walk(outline.record, 0);
  return rows;
}

/**
 * What opens first: the record's `data`, one level down, so the page starts on what the mapping fills without what it nests.
 * The envelope and whatever `data` nests wait for a click, each as one line saying what it holds. A record with no `data` opens
 * its first object.
 */
export function defaultOpen(outline: MappingOutline): Set<string> {
  const data = outline.record.find((node) => node.path === "data") ?? outline.record.find((node) => node.children.length > 0);
  return new Set(data === undefined ? [] : [data.path]);
}

/** Every node that opens: what a reader asks for to see the whole mapping at once. */
export function everyFolding(outline: MappingOutline): Set<string> {
  const paths = new Set<string>();
  const walk = (nodes: readonly MappingOutlineNode[]) => {
    for (const node of nodes) {
      if (node.children.length > 0) {
        paths.add(node.path);
        walk(node.children);
      }
    }
  };

  walk(outline.record);
  return paths;
}

/** One field of a table on the canvas. */
export interface CardField {
  /** The name it lights by: its source and its name. */
  id: string;
  name: string;
  key: boolean;
  /** Only the label or the identity read it. */
  displayOnly: boolean;
  match: boolean;
  read: boolean;
  /** The catalog's type for it, when the catalog describes the table. */
  type: string | null;
}

/** A table on the canvas. */
export interface OutlineCard {
  id: string;
  source: MappingOutlineSource;
  /** What its heading names: the table's name when a flow tells it, else what the document calls it. */
  title: string;
  /** Its place in the database, muted beside the title. */
  subtitle: string | null;
  /** What one of its rows is to the record. */
  role: string;
  fields: CardField[];
  /** Its fields are folded with the repeater that reads them, and it stands for them as one line. */
  folded: boolean;
  /** The columns the catalog holds that the mapping does not read. */
  unread: { name: string; dataType: string | null }[];
  /** The repeater it feeds, for a child table. */
  repeater: string | null;
  /** How many properties it reaches. */
  readers: number;
}

export function fieldId(source: string, field: string): string {
  return `${source}\u0000${field}`;
}

/** Each field's first row among the record's rows with everything open, in document order. */
function firstUses(outline: MappingOutline): Map<string, number> {
  const uses = new Map<string, number>();
  let place = 0;
  const walk = (nodes: readonly MappingOutlineNode[]) => {
    for (const node of nodes) {
      for (const read of node.flow?.reads ?? []) {
        const id = fieldId(read.source, read.field);
        if (!uses.has(id)) {
          uses.set(id, place);
        }
      }

      place++;
      walk(node.children);
    }
  };

  walk(outline.record);
  return uses;
}

/** A table's name as a person reads it, the database apart, however a flow quotes it: `[OsduData].[silver].[WellLog]`, `OsduData.silver.WellLog`. */
export function tableName(table: string): { title: string; subtitle: string | null } {
  const parts = [...table.matchAll(/\[([^\]]+)\]|([^.[\]]+)/g)].map((match) => match[1] ?? match[2]);
  return parts.length === 3 ? { title: `${parts[1]}.${parts[2]}`, subtitle: parts[0] } : { title: parts.join("."), subtitle: null };
}

function roleOf(source: MappingOutlineSource, repeater: OutlineRepeater | undefined, outline: MappingOutline): string {
  switch (source.kind) {
    case "record":
      return "One row, one record";
    case "child":
      return repeater === undefined ? "Rows of the record" : `One row, one item of ${repeater.name}`;
    case "parameters":
      return "From the flow";
    case "cache":
      return "The partition's cache";
    case "search":
      return "Searched in OSDU";
    case "parent":
      return "The record's parent";
    case "childRecord":
      return "A record linked to this one";
    case "base":
      return `The record ${outline.reference} adds to`;
    default:
      return "Records an enrichment registers";
  }
}

interface OutlineRepeater {
  path: string;
  name: string;
  dataset: string;
}

function repeatersOf(nodes: readonly MappingOutlineNode[]): OutlineRepeater[] {
  return nodes.flatMap((node) => [
    ...(node.shape === "repeat" && node.dataset !== null && node.dataset !== undefined ? [{ path: node.path, name: node.name, dataset: node.dataset }] : []),
    ...repeatersOf(node.children),
  ]);
}

/**
 * The tables of the canvas. A table's fields sit in the order the record first reads them, key first, so the lines from one
 * table to the record run side by side rather than crossing, and that order holds whatever is open, because closing a part of
 * the record only joins neighbours. A child table folds with the repeater that reads it: while the repeater is closed the
 * table is one line, as the repeater is.
 */
export function cardsOf(
  outline: MappingOutline, open: ReadonlySet<string>, tables: readonly DeliverySourceTable[] | null, cached: readonly CachedTypeFields[] | null = null,
): OutlineCard[] {
  const uses = firstUses(outline);
  const repeaters = repeatersOf(outline.record);
  return outline.sources.map((source) => {
    const child = source.kind === "child" ? source.id.slice(source.id.indexOf(".") + 1) : null;
    const table = tables?.find((candidate) => (child === null ? source.kind === "record" && candidate.dataset === null : candidate.dataset === child));
    const repeater = child === null ? undefined : repeaters.find((candidate) => candidate.dataset === child);
    const types = new Map((table?.columns ?? []).map((column) => [column.name.toLowerCase(), column.dataType]));
    const named = table === undefined ? null : tableName(table.table);
    // What is read comes in the order it is first read; what only names a record to a person comes after, as it is listed.
    const orderOf = (name: string, key: boolean) => (key ? -1 : uses.get(fieldId(source.id, name)) ?? Number.MAX_SAFE_INTEGER);
    const fields = [...source.fields]
      .sort((a, b) => orderOf(a.name, a.key) - orderOf(b.name, b.key))
      .map((field): CardField => ({
        id: fieldId(source.id, field.name),
        name: field.name,
        key: field.key,
        displayOnly: field.display && field.reads === 0 && !field.key,
        match: field.match,
        read: field.reads > 0,
        type: types.get(field.name.toLowerCase()) ?? null,
      }));
    if (source.kind === "cache" && fields.length === 0) {
      fields.push(...pairFields(source.id, cached?.find((type) => type.name === source.name) ?? null));
    }

    const known = new Set(fields.map((field) => field.name.toLowerCase()));
    return {
      id: source.id,
      source,
      title: named?.title ?? (source.kind === "record" ? "The record table" : source.name),
      subtitle: named?.subtitle ?? null,
      role: roleOf(source, repeater, outline),
      fields,
      folded: repeater !== undefined && !open.has(repeater.path),
      unread: (table?.columns ?? []).filter((column) => !known.has(column.name.toLowerCase())).map((column) => ({ name: column.name, dataType: column.dataType })),
      repeater: repeater?.path ?? null,
      readers: readersOf(outline, source.id, null).length,
    };
  });
}

/**
 * The fields of a table of pairs, which a mapping reads by value alone (`replace: $cache.UnitAlias`), so the document names none:
 * its key and what stands beside it as the cache knows them, else the words for them.
 */
function pairFields(source: string, type: CachedTypeFields | null): CardField[] {
  const field = (name: string, match: boolean): CardField => ({ id: fieldId(source, name), name, key: false, displayOnly: false, match, read: !match, type: null });
  const key = type?.key ?? "key";
  const value = type?.fields.find((name) => name !== key) ?? "value";
  return [field(key, true), field(value, false)];
}

/** What a lookup's line compares its field with: a column the flow reads, or the text the document writes there. */
export interface WayOperand {
  /** The column's name, or the text as written: `'metre'`, `$lookup.wellbore.GeoContexts.FieldID`, `the value`. */
  text: string;
  read: MappingOutlineRead | null;
}

/** One line of a lookup: the field of the table it looks in, and what that field has to hold. */
export interface WayLine {
  field: string;
  operand: WayOperand;
}

/**
 * One stage of a value's way to its property, in the order a render takes them: what enters, what changes it, for a lookup
 * the table it is compared in and what is fetched from the record found, and the property it is written to.
 */
export type WayStage =
  | { kind: "input"; reads: MappingOutlineRead[]; literal: string | null }
  | { kind: "changed"; steps: MappingOutlineStep[] }
  /** `changed` when a step before it changed the value, so a column's value in the row is not what is compared. */
  | { kind: "compared"; step: MappingOutlineStep; lookup: MappingOutlineLookup; lines: WayLine[]; changed: boolean }
  | { kind: "fetched"; lookup: MappingOutlineLookup }
  | { kind: "written" };

/** A line's operand: the column of the flow's reads it names (`dataset.wellbore_uwi`, `dataset.curves.mnemonic`), else its text. */
export function operandOf(reads: readonly MappingOutlineRead[], value: string): WayOperand {
  const read = reads.find((candidate) => `${candidate.source}.${candidate.field}` === value) ?? null;
  return { text: read?.field ?? value, read };
}

/** The lookup a step compares in: a find's, or a table a replace matches the value against. */
function comparedIn(step: MappingOutlineStep): MappingOutlineLookup | undefined {
  return step.kind === "find" ? step.lookups[0] : step.lookups.find((lookup) => lookup.matches.length > 0);
}

/** A value's way as stages: an input, then each change, each lookup as what it compares and what it fetches, then the property. */
export function wayOf(flow: MappingOutlineFlow): WayStage[] {
  const reads = flow.reads.filter((read) => read.role !== "condition");
  const literal = flow.origin === "literal" ? flow.literal ?? null : null;
  const stages: WayStage[] = reads.length > 0 || literal !== null ? [{ kind: "input", reads, literal }] : [];
  let changed = false;
  for (const step of flow.steps) {
    const lookup = comparedIn(step);
    if (lookup === undefined) {
      const last = stages[stages.length - 1];
      if (last?.kind === "changed") {
        last.steps.push(step);
      } else {
        stages.push({ kind: "changed", steps: [step] });
      }
    } else {
      stages.push({ kind: "compared", step, lookup, lines: lookup.matches.map((match) => ({ field: match.field, operand: operandOf(reads, match.value) })), changed });
      stages.push({ kind: "fetched", lookup });
    }

    changed = true;
  }

  stages.push({ kind: "written" });
  return stages;
}

/** How a mapping's properties come to their values, counted: the numbers the page leads with. */
export interface OutlineCounts {
  properties: number;
  copied: number;
  changed: number;
  lookedUp: number;
  fixed: number;
  linked: number;
}

export function countsOf(outline: MappingOutline): OutlineCounts {
  const counts: OutlineCounts = { properties: 0, copied: 0, changed: 0, lookedUp: 0, fixed: 0, linked: 0 };
  for (const node of propertiesOf(outline.record)) {
    if (node.how === "key" || node.how === "") {
      continue;
    }

    counts.properties++;
    counts[node.how]++;
  }

  return counts;
}

/** What is wrong with a property, by its path. */
export interface OutlineMark {
  path: string;
  tone: "error" | "warning";
  text: string;
}

/** Each mark at the row that shows it: its own, or the row folding it, so what is wrong is seen with everything closed. */
export function marksFor(rows: readonly OutlineRow[], marks: readonly OutlineMark[]): Map<string, { tone: "error" | "warning"; text: string }> {
  const shown = new Map<string, { tone: "error" | "warning"; text: string }>();
  for (const mark of marks) {
    const row = rowShowing(rows, mark.path);
    if (row !== null) {
      const known = shown.get(row.path);
      shown.set(row.path, known === undefined
        ? { tone: mark.tone, text: mark.text }
        : { tone: known.tone === "error" ? "error" : mark.tone, text: `${known.text}\n${mark.text}` });
    }
  }

  return shown;
}

/** What is lit: the rows, fields, tables and lines that make one thing, or null when nothing is pointed at. */
export interface OutlineLight {
  rows: Set<string>;
  fields: Set<string>;
  cards: Set<string>;
  /** The crossing of a row's line with a table, as `dotId` names it. */
  dots: Set<string>;
}

export function dotId(row: string, source: string): string {
  return `${row}\u0000${source}`;
}

function newLight(): OutlineLight {
  return { rows: new Set(), fields: new Set(), cards: new Set(), dots: new Set() };
}

function lightRow(light: OutlineLight, row: OutlineRow, ancestors: ReadonlyMap<string, string>): void {
  light.rows.add(row.path);
  for (let parent = ancestors.get(row.path); parent !== undefined; parent = ancestors.get(parent)) {
    light.rows.add(parent);
  }

  for (const read of row.reads) {
    light.fields.add(fieldId(read.source, read.field));
    light.cards.add(read.source);
  }

  for (const lookup of row.lookups) {
    light.cards.add(lookup.source);
    light.dots.add(dotId(row.path, lookup.source));
    if (lookup.field !== null && lookup.field !== undefined) {
      light.fields.add(fieldId(lookup.source, lookup.field));
    }

    for (const match of lookup.matches) {
      light.fields.add(fieldId(lookup.source, match.field));
    }
  }

  if (row.repeats !== null) {
    light.cards.add(`dataset.${row.repeats}`);
  }
}

/** Each row's parent among the rows shown. */
export function ancestorsOf(rows: readonly OutlineRow[]): Map<string, string> {
  const parents = new Map<string, string>();
  const trail: string[] = [];
  for (const row of rows) {
    trail.length = row.depth;
    if (row.depth > 0) {
      parents.set(row.path, trail[row.depth - 1]);
    }

    trail[row.depth] = row.path;
  }

  return parents;
}

/** What enters a row: the columns a value on its line starts from. */
function lightReads(light: OutlineLight, row: OutlineRow): void {
  for (const read of row.reads) {
    light.fields.add(fieldId(read.source, read.field));
    light.cards.add(read.source);
  }
}

/** What makes the rows `paths` names: each with what enters it and the rows it opens under. */
export function lightOfRows(rows: readonly OutlineRow[], paths: ReadonlySet<string>): OutlineLight {
  const ancestors = ancestorsOf(rows);
  const light = newLight();
  for (const row of rows) {
    if (paths.has(row.path)) {
      lightRow(light, row, ancestors);
    }
  }

  return light;
}

/**
 * What a field reaches: every row it enters or is looked up for. A column goes on through the tables its value is looked up in,
 * and a field of one of those tables comes from the columns looked up in it, so either lights the whole way.
 */
export function lightOfField(rows: readonly OutlineRow[], source: string, field: string): OutlineLight {
  const ancestors = ancestorsOf(rows);
  const light = newLight();
  const id = fieldId(source, field);
  light.fields.add(id);
  light.cards.add(source);
  for (const row of rows) {
    const reads = row.reads.some((read) => read.source === source && read.field === field);
    const looked = row.lookups.filter((lookup) => lookup.source === source && (lookup.field === field || lookup.matches.some((match) => match.field === field)));
    if (!reads && looked.length === 0) {
      continue;
    }

    light.rows.add(row.path);
    for (let parent = ancestors.get(row.path); parent !== undefined; parent = ancestors.get(parent)) {
      light.rows.add(parent);
    }

    if (looked.length > 0) {
      lightReads(light, row);
    }

    // A value entering a line goes through every table on it; a field read of one table goes on from there.
    for (const lookup of reads ? row.lookups : looked) {
      light.dots.add(dotId(row.path, lookup.source));
      light.cards.add(lookup.source);
    }
  }

  return light;
}

/** What a table reaches: every row reading anything of it, the whole way, from the columns a value starts at to the property. */
export function lightOfCard(rows: readonly OutlineRow[], source: string): OutlineLight {
  const ancestors = ancestorsOf(rows);
  const light = newLight();
  light.cards.add(source);
  for (const row of rows) {
    const reads = row.reads.filter((read) => read.source === source);
    const looked = row.lookups.filter((lookup) => lookup.source === source);
    if (reads.length === 0 && looked.length === 0 && row.repeats !== source.slice(source.indexOf(".") + 1)) {
      continue;
    }

    light.rows.add(row.path);
    for (let parent = ancestors.get(row.path); parent !== undefined; parent = ancestors.get(parent)) {
      light.rows.add(parent);
    }

    for (const read of reads) {
      light.fields.add(fieldId(read.source, read.field));
    }

    if (looked.length > 0) {
      lightReads(light, row);
    }

    for (const lookup of looked) {
      if (lookup.field !== null && lookup.field !== undefined) {
        light.fields.add(fieldId(lookup.source, lookup.field));
      }

      for (const match of lookup.matches) {
        light.fields.add(fieldId(lookup.source, match.field));
      }
    }

    for (const lookup of reads.length > 0 ? row.lookups : looked) {
      light.dots.add(dotId(row.path, lookup.source));
      light.cards.add(lookup.source);
    }
  }

  return light;
}

/** The paths of the rows of one way of coming to a value, opening what holds them so they can be seen. */
export function pathsOfHow(outline: MappingOutline, how: MappingOutlineHow): { paths: Set<string>; opens: Set<string> } {
  return pathsWhere(outline, (node) => node.how === how);
}

/** The paths of the properties a search finds, in the property's name or anything it reads, with what must open to show them. */
export function pathsOfSearch(outline: MappingOutline, term: string): { paths: Set<string>; opens: Set<string> } {
  const text = term.trim().toLowerCase();
  return pathsWhere(outline, (node) => {
    const flows = [node.flow, ...leavesOf(node).map((leaf) => leaf.flow)];
    return node.path.toLowerCase().includes(text) || flows.some((flow) => flow?.summary.toLowerCase().includes(text) === true);
  });
}

function pathsWhere(outline: MappingOutline, wanted: (node: MappingOutlineNode) => boolean): { paths: Set<string>; opens: Set<string> } {
  const paths = new Set<string>();
  const opens = new Set<string>();
  const walk = (nodes: readonly MappingOutlineNode[], trail: readonly string[]) => {
    for (const node of nodes) {
      if (node.shape === "value" || node.shape === "choice" || node.shape === "list") {
        if (wanted(node)) {
          paths.add(node.path);
          for (const step of trail) {
            opens.add(step);
          }
        }
      } else {
        walk(node.children, [...trail, node.path]);
      }
    }
  };

  walk(outline.record, []);
  return { paths, opens };
}

/** Whether a row holds a path: it is the row, or the path is inside it, a property's, an item's or an alternative's. */
function holds(row: string, path: string): boolean {
  return path === row || path.startsWith(`${row}.`) || path.startsWith(`${row}[]`) || path.startsWith(`${row}|`);
}

/** The row showing a path: its own, or the row folding it. */
export function rowShowing(rows: readonly OutlineRow[], path: string): OutlineRow | null {
  return rows.reduce<OutlineRow | null>((best, row) => (holds(row.path, path) && (best === null || row.path.length > best.path.length) ? row : best), null);
}

/** What must open for a node to be shown: everything above it. */
export function opensTo(outline: MappingOutline, path: string): Set<string> {
  const opens = new Set<string>();
  const walk = (nodes: readonly MappingOutlineNode[], trail: readonly string[]): boolean => nodes.some((node) => {
    if (node.path === path) {
      trail.forEach((step) => opens.add(step));
      return true;
    }

    return walk(node.children, [...trail, node.path]);
  });

  walk(outline.record, []);
  return opens;
}

/** The node at `path`, or null. */
export function nodeAt(outline: MappingOutline, path: string): MappingOutlineNode | null {
  const walk = (nodes: readonly MappingOutlineNode[]): MappingOutlineNode | null => {
    for (const node of nodes) {
      if (node.path === path) {
        return node;
      }

      const found = walk(node.children);
      if (found !== null) {
        return found;
      }
    }

    return null;
  };

  return walk(outline.record);
}

/** The property a line of the document writes: the narrowest node whose lines hold it. */
export function nodeAtLine(outline: MappingOutline, line: number): MappingOutlineNode | null {
  let best: MappingOutlineNode | null = null;
  const walk = (nodes: readonly MappingOutlineNode[]) => {
    for (const node of nodes) {
      const { line: first, endLine: last } = node;
      if (first !== null && first !== undefined && last !== null && last !== undefined && first <= line && line <= last) {
        if (best === null || last - first <= (best.endLine ?? 0) - (best.line ?? 0)) {
          best = node;
        }
      }

      walk(node.children);
    }
  };

  walk(outline.record);
  return best;
}

/** The way a value is read in a rendered record: `data.Curves[].CurveUnit` of the first item, a `$coalesce`'s value at its own path. */
export function valueAt(record: Record<string, unknown> | null | undefined, path: string): unknown {
  if (record === null || record === undefined) {
    return undefined;
  }

  let value: unknown = record;
  for (const step of path.split("|")[0].split(".")) {
    if (value === null || typeof value !== "object") {
      return undefined;
    }

    const array = step.endsWith("[]");
    const held = (value as Record<string, unknown>)[array ? step.slice(0, -2) : step];
    value = array ? (Array.isArray(held) ? held[0] : undefined) : held;
  }

  return value;
}
