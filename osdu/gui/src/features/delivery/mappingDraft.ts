// The mapping builder's draft logic that needs no server: which inputs a variable takes, where an entry goes in the draft,
// how a static value is edited, how an entry reads as text, and the column names a draft already knows. The server writes
// the YAML and checks it.

import type {
  DeliveryCachedType, DeliveryTemplateVariable, MappingDraft, MappingDraftAssertion, MappingDraftEntry, MappingDraftFind, MappingDraftFindAll,
  MappingDraftInput, MappingDraftModifier, MappingDraftModifierKind,
} from "../../api/delivery";
import { assertionText } from "./mappingAssertionText";

/** The access and legal variables every record carries, which a mapping gives as non-empty lists of strings. */
export const ENVELOPE_TARGETS: readonly string[] = [
  "osdu.acl.owners",
  "osdu.acl.viewers",
  "osdu.legal.legaltags",
  "osdu.legal.otherRelevantDataCountries",
];

/**
 * The access lists, which a list of values may add to: nodes reading groups beside the fixed values every record carries.
 * The legal lists stay fixed, since the legal service checks them before a run.
 */
export const ACCESS_TARGETS: readonly string[] = ["osdu.acl.owners", "osdu.acl.viewers"];

/** A lookup's name, as the mapping format takes it: letters, digits, underscores and hyphens. */
export const LOOKUP_NAME = /^[A-Za-z0-9_-]+$/;

/** How the builder's check names a lookup an issue is about (`lookups.wellbore`), so the page can open that lookup. */
export const LOOKUP_TARGET_PREFIX = "lookups.";

/** A dataset column or child dataset name, as the mapping format takes it. */
export const COLUMN_NAME = /^[A-Za-z0-9_-]+$/;

/** A column as an entry reads it: `column`, or `child.column` inside a repeater. */
export const DATASET_COLUMN = /^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)?$/;

/** A key under an object with free keys (a tag name): anything but dots, brackets and whitespace. */
export const KEY_NAME = /^[^.[\]\s]+$/;

export const MODIFIER_KINDS: readonly MappingDraftModifierKind[] = ["trim", "upper", "lower", "split", "replace", "equals", "date", "number", "id", "ref"];

/** The modifiers that build the id an entry writes, from a dataset value: always the last, and at most one of them. */
export const ID_MODIFIER_KINDS: readonly MappingDraftModifierKind[] = ["id", "ref"];

/** What a new id modifier starts from: a reference to a unit of measure whose code is the value. */
export const ID_TEMPLATE_EXAMPLE = "{$param.dataPartition}:reference-data--UnitOfMeasure:{$value}:";

/** The separators a number modifier reads before the decimals. */
export const DECIMAL_SEPARATORS: readonly { value: string; label: string }[] = [
  { value: ".", label: "point ." },
  { value: ",", label: "comma ," },
];

/** The group separator choice that stands for none; a select item cannot carry an empty value. */
export const NO_GROUP = "none";

/** The separators a number modifier reads between groups of three digits; a space also stands for a no-break or thin space. */
export const GROUP_SEPARATORS: readonly { value: string; label: string }[] = [
  { value: NO_GROUP, label: "none" },
  { value: ",", label: "comma ," },
  { value: ".", label: "point ." },
  { value: " ", label: "space" },
  { value: "'", label: "apostrophe '" },
];

/** A list with the item at `index` moved `delta` places, or as it was when that would move it off either end. */
export function moveItem<T>(items: readonly T[], index: number, delta: number): T[] {
  const to = index + delta;
  const next = [...items];
  if (to < 0 || to >= items.length) {
    return next;
  }

  [next[index], next[to]] = [next[to], next[index]];
  return next;
}

/** One findBy line as a form edits it: the field compared, and a dataset column or a fixed text. */
export interface FindLine {
  field: string;
  mode: "column" | "text";
  value: string;
}

/** A column as the draft stores it: trimmed, and without the dataset. prefix a person may type out of habit. */
export function bareColumn(text: string): string {
  return text.trim().replace(/^dataset\./, "");
}

/** The draft's findBy lines as a form edits them. */
export function findLinesOf(finds: readonly MappingDraftFind[]): FindLine[] {
  return finds.map((find): FindLine => (find.literal !== null && find.literal !== ""
    ? { field: find.field, mode: "text", value: find.literal }
    : { field: find.field, mode: "column", value: find.column ?? "" }));
}

/** A form's findBy lines as the draft stores them. */
export function findsOf(lines: readonly FindLine[]): MappingDraftFind[] {
  return lines.map((line) => (line.mode === "text"
    ? { field: line.field.trim(), column: null, literal: line.value }
    : { field: line.field.trim(), column: bareColumn(line.value), literal: null }));
}

/** An entry with no settings beyond its target and input. */
export function emptyEntry(target: string, input: MappingDraftInput): MappingDraftEntry {
  return {
    target,
    input,
    column: null,
    child: null,
    cacheType: null,
    cacheField: null,
    lookup: null,
    parentPath: null,
    childKind: null,
    findBy: [],
    findAll: null,
    modifiers: [],
    expression: null,
    when: null,
    where: null,
    required: true,
    ignoreSeparators: false,
    unverified: false,
    alternatives: [],
    items: [],
    properties: [],
    static: null,
    description: null,
    assertions: [],
    prefilled: false,
  };
}

/**
 * An entry and the value nodes it reads through: a coalesce entry's alternatives, a list's items, and the properties of
 * an item of a list of objects with what they read through in turn.
 */
export function valueNodesOf(entry: MappingDraftEntry): MappingDraftEntry[] {
  return [entry, ...entry.alternatives, ...entry.items.flatMap(valueNodesOf), ...entry.properties.flatMap(valueNodesOf)];
}

/** A list of objects: one with a group, or a fixed object without a condition or description, among its items. */
export function isObjectList(entry: Pick<MappingDraftEntry, "input" | "items">): boolean {
  return entry.input === "List" && entry.items.some((item) => item.input === "Group"
    || (item.input === "Static" && isPlainObject(parseJson(item.static)) && item.when === null && (item.description ?? "") === ""));
}

/** True for a parsed JSON object holding at least one property. */
function isPlainObject(parsed: ReturnType<typeof parseJson>): boolean {
  return parsed.ok && parsed.value !== null && typeof parsed.value === "object" && !Array.isArray(parsed.value)
    && Object.keys(parsed.value as object).length > 0;
}

/** Where a property of an item of the list at `list` lies inside the item: `Detail.Note` for `osdu.data.X[].Detail.Note`. */
export function withinItem(list: string, target: string): string {
  const prefix = `${list}[].`;
  return target.startsWith(prefix) ? target.slice(prefix.length) : target;
}

/** The lookup a find all line keys by, from its `wellbore.GeoContexts.FieldID`; null when it keys by none. */
function findAllLookup(findAll: MappingDraftFindAll | null): string | null {
  const path = (findAll?.lookup ?? "").trim();
  return path === "" ? null : path.split(".")[0];
}

/** The targets of the entries that read a lookup, with a lookup input or in a find all line, whichever node of theirs does. */
export function lookupReaders(entries: readonly MappingDraftEntry[], name: string): string[] {
  return entries
    .filter((entry) => valueNodesOf(entry).some((node) => (node.input === "Lookup" && (node.lookup ?? "").trim() === name)
      || (node.input === "Cache" && findAllLookup(node.findAll) === name)))
    .map((entry) => entry.target);
}

/** The entries with every read of lookup `from` reading `to`: a renamed lookup keeps its readers. */
export function renameLookup(entries: readonly MappingDraftEntry[], from: string, to: string): MappingDraftEntry[] {
  const rename = (node: MappingDraftEntry): MappingDraftEntry => {
    const lookup = node.input === "Lookup" && (node.lookup ?? "").trim() === from ? to : node.lookup;
    const findAll = node.findAll !== null && findAllLookup(node.findAll) === from
      ? { ...node.findAll, lookup: `${to}${(node.findAll.lookup ?? "").trim().slice(from.length)}` }
      : node.findAll;
    return {
      ...node, lookup, findAll, alternatives: node.alternatives.map(rename), items: node.items.map(rename), properties: node.properties.map(rename),
    };
  };
  return entries.map(rename);
}

/** A new modifier of a kind, with the settings that kind asks for left for the person to give. */
export function newModifier(kind: MappingDraftModifierKind): MappingDraftModifier {
  switch (kind) {
    case "split":
      return { kind, separator: "", part: 1, replacements: null, text: null, decimalSeparator: null, groupSeparator: null };
    case "replace":
      return {
        kind, separator: null, part: null, replacements: [{ from: "", to: "" }], text: null, decimalSeparator: null, groupSeparator: null,
        otherwiseKind: "keep", otherwiseText: null,
      };
    case "equals":
      return { kind, separator: null, part: null, replacements: null, text: "", decimalSeparator: null, groupSeparator: null };
    case "id":
      return { kind, separator: null, part: null, replacements: null, text: ID_TEMPLATE_EXAMPLE, decimalSeparator: null, groupSeparator: null };
    case "number":
      return { kind, separator: null, part: null, replacements: null, text: null, decimalSeparator: ".", groupSeparator: null };
    case "trim":
    case "upper":
    case "lower":
    case "date":
    case "ref":
      return { kind, separator: null, part: null, replacements: null, text: null, decimalSeparator: null, groupSeparator: null };
  }
}

/**
 * The inputs a variable takes, as the preflight allows them: a value or a list of values from a dataset column, the cache,
 * a lookup, a search of the platform or a static value, and a list of values from items that are each one of those; a
 * list of objects from a repeater, a static list, or a list whose items are objects of properties each filled on its
 * own; an object only from a static value. A section of the record's parent fills any shape, for a template linked to a
 * parent, and the link to one of the record's children a value, for a template other templates are linked to (the editor
 * offers each only then). The legal lists are static, and the access lists static or a list of values that keeps a fixed
 * value every record carries.
 */
export function inputsFor(variable: Pick<DeliveryTemplateVariable, "shape" | "path">): MappingDraftInput[] {
  if (ACCESS_TARGETS.includes(variable.path)) {
    return ["Static", "List"];
  }

  if (ENVELOPE_TARGETS.includes(variable.path)) {
    return ["Static"];
  }

  switch (variable.shape) {
    case "Value":
      return ["Dataset", "Expression", "Cache", "Lookup", "Search", "Parent", "Child", "Static", "Coalesce"];
    case "ValueList":
      return ["Dataset", "Expression", "Cache", "Lookup", "Search", "Parent", "Child", "Static", "Coalesce", "List"];
    case "GroupList":
      return ["Repeat", "List", "Parent", "Static"];
    case "Group":
    case "Whole":
      return ["Parent", "Static"];
  }
}

/** The list of objects a target inside a repeated item belongs to: osdu.data.Curves for osdu.data.Curves[].CurveID. */
export function repeaterOf(target: string): string | null {
  const at = target.indexOf("[]");
  return at < 0 ? null : target.slice(0, at);
}

/**
 * Where a target sorts in the template: its variable's position in schema order. A key under an object with free keys
 * sorts right after that object, and a target the template does not have sorts last.
 */
export function targetOrder(target: string, order: ReadonlyMap<string, number>): number {
  const own = order.get(target);
  if (own !== undefined) {
    return own;
  }

  const at = target.lastIndexOf(".");
  const holder = at < 0 ? undefined : order.get(target.slice(0, at));
  return holder !== undefined ? holder + 0.5 : Number.MAX_SAFE_INTEGER;
}

/**
 * The entries with one entry put in its place: replaced where it stands when its target already has an entry, and
 * otherwise inserted after the last entry that sorts before it (or with it) in template order.
 */
export function putEntry(
  entries: readonly MappingDraftEntry[], entry: MappingDraftEntry, order: ReadonlyMap<string, number>,
): MappingDraftEntry[] {
  const existing = entries.findIndex((candidate) => candidate.target === entry.target);
  if (existing >= 0) {
    return entries.map((candidate, index) => (index === existing ? entry : candidate));
  }

  const position = targetOrder(entry.target, order);
  let after = -1;
  entries.forEach((candidate, index) => {
    if (targetOrder(candidate.target, order) <= position) {
      after = index;
    }
  });

  return [...entries.slice(0, after + 1), entry, ...entries.slice(after + 1)];
}

/** The entry's input in one short line: the column, child rows, cached field or search it reads, or its fixed value. */
export function entrySummary(entry: MappingDraftEntry): string {
  switch (entry.input) {
    case "Dataset":
      return `dataset.${entry.column ?? ""}`;
    case "Repeat":
      return `rows of dataset.${entry.child ?? ""}`;
    case "Cache":
      return `cache.${entry.cacheType ?? ""}.${entry.cacheField ?? ""}`;
    case "Search":
      return `search.${entry.cacheType ?? ""}.${entry.cacheField ?? "id"}`;
    case "Expression":
      return entry.expression ?? "";
    case "Static":
      return `static ${entry.static ?? ""}`;
    case "Coalesce":
      return entry.alternatives.map(entrySummary).join(", else ");
    case "Lookup":
      return `lookup.${entry.lookup ?? ""}.${entry.cacheField ?? ""}`;
    case "List":
      return entry.items.map(entrySummary).join(", ");
    case "Group":
      return groupText(entry, entrySummary);
    case "Parent":
      return `parent.${entry.parentPath ?? ""}`;
    case "Child":
      return `child.${entry.childKind ?? ""}`;
  }
}

/** An item of a list of objects on one line, each property by its place in the item: `{ TypeID: dataset.type, Note: static "x" }`. */
function groupText(group: MappingDraftEntry, text: (property: MappingDraftEntry) => string): string {
  return group.properties.length === 0
    ? "{ }"
    : `{ ${group.properties.map((property) => `${withinItem(group.target, property.target)}: ${text(property)}`).join(", ")} }`;
}

/** Whether an entry finds a record by findBy lines, or rows by a find all: out of the cache, or by searching the platform. */
export function looksUp(entry: Pick<MappingDraftEntry, "input">): boolean {
  return entry.input === "Cache" || entry.input === "Search";
}

/** The value a find all compares its field with: a fixed text, a field of a lookup's record, or a dataset column. */
function findAllOperand(findAll: MappingDraftFindAll): string {
  const literal = findAll.literal ?? "";
  const lookup = (findAll.lookup ?? "").trim();
  return literal.trim() !== "" ? quoted(literal) : lookup !== "" ? `$lookup.${lookup}` : `dataset.${findAll.column ?? ""}`;
}

/** The rows a find all reads, as one phrase: `every row with FieldIDList = $lookup.wellbore.GeoContexts.FieldID and FieldList empty`. */
export function findAllText(findAll: MappingDraftFindAll): string {
  return [`every row with ${findAll.field} = ${findAllOperand(findAll)}`, ...findAll.empty.map((field) => `${field} empty`)].join(" and ");
}

/** How an entry's record is found, as the words after its source: `by <findBy lines>`, `from <the rows of a find all>`, or nothing. */
function lookupPhrase(entry: MappingDraftEntry): string {
  const lookup = lookupText(entry);
  return lookup === "" ? "" : entry.input === "Cache" && entry.findAll !== null ? `from ${lookup}` : `by ${lookup}`;
}

/**
 * An entry on one line, without the target it fills: where the value comes from, the lookup that finds it, what is done
 * to it, and when it applies. `dataset.facility_name | trim`, `search.Wellbore.id by data.FacilityName = dataset.wellbore_uwi`.
 * A list reads as its items, one after another, and an item of a list of objects as its properties in braces.
 */
export function entryText(entry: MappingDraftEntry): string {
  if (entry.input === "Coalesce") {
    // Each alternative as a line of its own would read, in the order they are tried; the condition is the node's.
    return [
      entry.alternatives.map(alternativeText).join(", else "),
      entry.when === null ? "" : `when ${entry.when}`,
    ].filter((part) => part !== "").join(" ");
  }

  if (entry.input === "List") {
    return entry.items.map(entryText).join("; ");
  }

  if (entry.input === "Group") {
    return groupText(entry, entryText);
  }

  return [
    entrySummary(entry),
    lookupPhrase(entry),
    entry.modifiers.length === 0 ? "" : `| ${entry.modifiers.map(modifierText).join(" | ")}`,
    entry.unverified ? "(unverified)" : "",
    entry.when === null ? "" : `when ${entry.when}`,
  ].filter((part) => part !== "").join(" ");
}

/** One alternative of a coalesce entry on one line: what it reads, looks up and does, and whether it may go out unverified. */
export function alternativeText(alternative: MappingDraftEntry): string {
  return entryText(alternative);
}

/**
 * What an entry further up writes into a target no entry of its own fills: the nearest entry whose target holds it and
 * that writes what it holds (a static value, a value written whole such as a cached field holding an object, a coalesce
 * of them, or a list of objects whose items write it), with the values a static value gives the target, once each.
 * `present` is false when that entry is a static value without the target in it, or a list none of whose items writes
 * it, which leaves the target out. Null when nothing above writes it; a repeat does not, since each property of its items
 * has an entry of its own.
 */
export interface InheritedFill {
  holder: MappingDraftEntry;
  values: string[];
  present: boolean;
}

export function inheritedFill(entries: readonly MappingDraftEntry[], target: string): InheritedFill | null {
  let holder: MappingDraftEntry | null = null;
  for (const entry of entries) {
    const inside = target.startsWith(`${entry.target}.`) || target.startsWith(`${entry.target}[].`);
    if (inside && entry.input !== "Repeat" && (holder === null || entry.target.length > holder.target.length)) {
      holder = entry;
    }
  }

  if (holder === null) {
    return null;
  }

  if (holder.input === "List") {
    return listFill(holder, target);
  }

  const literals = holder.input === "Static"
    ? [holder.static]
    : holder.input === "Coalesce" ? holder.alternatives.filter((alternative) => alternative.input === "Static").map((alternative) => alternative.static) : [];
  const found = literals.flatMap((text) => valuesAt(text, target.slice(holder.target.length)));
  return { holder, values: shownValues(found), present: holder.input !== "Static" || found.length > 0 };
}

/**
 * What the items of a list of objects write into a variable inside them: the values its fixed items and the fixed
 * properties of its groups give it, and whether any item writes it at all, fixed or read from the row.
 */
function listFill(list: MappingDraftEntry, target: string): InheritedFill {
  const rest = target.slice(list.target.length);
  const fixed = list.items.filter((item) => item.input === "Static").map((item) => parseJson(item.static))
    .flatMap((parsed) => (parsed.ok && parsed.value !== undefined ? [parsed.value] : []));
  const found = valuesAt(JSON.stringify(fixed), rest);
  let written = false;
  for (const property of list.items.filter((item) => item.input === "Group").flatMap((item) => item.properties)) {
    if (property.target === target) {
      written = true;
      if (property.input === "Static") {
        found.push(...valuesAt(property.static, ""));
      }
    } else if (target.startsWith(`${property.target}.`) || target.startsWith(`${property.target}[].`)) {
      // A property filled whole holds the variable when its value does.
      const inside = property.input === "Static" ? valuesAt(property.static, target.slice(property.target.length)) : [];
      written ||= property.input !== "Static" || inside.length > 0;
      found.push(...inside);
    }
  }

  return { holder: list, values: shownValues(found), present: written || found.length > 0 };
}

/** The values a static value gives a variable as a view shows them: each text, number or boolean once. */
function shownValues(found: unknown[]): string[] {
  return [...new Set(found.filter((value) => value !== undefined).map(scalarText).filter((text): text is string => text !== null))];
}

/** What a static value's JSON holds at a path below it (`.Name`, `[].TypeID`, `.Items[].Code`): every value along it. */
function valuesAt(json: string | null, rest: string): unknown[] {
  const parsed = parseJson(json);
  if (!parsed.ok || parsed.value === undefined) {
    return [];
  }

  let nodes: unknown[] = [parsed.value];
  for (const step of rest.split(/(?=\.)|(?=\[\])/).filter((part) => part !== "")) {
    nodes = step === "[]"
      ? nodes.flatMap((node) => (Array.isArray(node) ? node : []))
      : nodes.flatMap((node) => {
        const name = step.startsWith(".") ? step.slice(1) : step;
        return node !== null && typeof node === "object" && !Array.isArray(node) && name in node ? [(node as Record<string, unknown>)[name]] : [];
      });
  }

  return nodes;
}

/** A value a static value gives as the document writes it: text as it is, a number or a boolean as written; null for an object or a list. */
function scalarText(value: unknown): string | null {
  if (typeof value === "string") {
    return value;
  }

  return typeof value === "number" || typeof value === "boolean" ? String(value) : null;
}

/** An assertion a property states, with the property it is written on: the entry's own, or one of its list's object items'. */
export interface PropertyAssertion {
  target: string;
  assertion: MappingDraftAssertion;
}

/** The assertions an entry states ($assert), then those the properties of its list's object items state, in the order they are written. */
export function propertyAssertions(entry: MappingDraftEntry): PropertyAssertion[] {
  const own = (entry.assertions ?? []).map((assertion) => ({ target: entry.target, assertion }));
  return [...own, ...entry.items.flatMap((item) => item.properties.flatMap(propertyAssertions))];
}

/** One property the mapping fills: where the value comes from, what is done to it, and the target it is written to. */
export interface PropertyRow {
  target: string;
  /** Where the value comes from, which decides what the rest of the entry means. */
  input: MappingDraftInput;
  /** The value's origin: `dataset.log_source`, `cache.UnitOfMeasure.id`, `search.Wellbore.id`, `rows of dataset.curves`, `static "MD"`. */
  source: string;
  /** The origin without the word that names its kind, for a view that says the kind itself. */
  sourceValue: string;
  /** The record's lookup as one phrase, `Code/Name = dataset.elev_meas_ref`; empty when no record is looked up. */
  lookup: string;
  /** The lookup with the word that joins it to the source: `by Code = dataset.unit`, `from every row with ...`; empty for none. */
  lookupPhrase: string;
  /** One line per findBy, naming the record set and the field compared, for the property's own view. */
  lookupDetail: string[];
  /** The lines are a find all's: every row they hold for is read, rather than the first line that finds one record. */
  findsAll: boolean;
  /** Coalesce: each alternative on one line, in the order they are tried; empty for any other entry. */
  alternatives: string[];
  /** List: each item on one line, in the order they are written; empty for any other entry. */
  items: string[];
  /** List: its items are objects, each a group of properties or a fixed object, rather than values. */
  objects: boolean;
  /** The id the entry builds goes out even when the cache holds no such record ($unverified). */
  unverified: boolean;
  /** A cache lookup tries once more with punctuation and spacing folded away ($ignoreSeparators). */
  ignoreSeparators: boolean;
  /** The modifiers in order, one line each; empty for a value taken as it stands. */
  modifiers: string[];
  /** The modifier kinds in order, which is what a row has room for: `split, replace`. */
  modifierKinds: string;
  /** The entry's condition (`$when`), or null when it always applies. */
  condition: string | null;
  description: string | null;
  required: boolean;
  /** The rules its value is held to (osdu/docs/reference/flow/mapping-assertions.md), with the property each is written on. */
  assertions: PropertyAssertion[];
  /** The whole line as text, with every modifier spelled out, which a hover panel shows and a filter matches. */
  detail: string;
  /** Everything the row holds, lowercased, which a filter matches against. */
  search: string;
}

/** One entry read as a row: every part of it worded once, so every view of a mapping says the same thing. */
export function propertyRow(entry: MappingDraftEntry): PropertyRow {
  // A list's source is its items, each with how it finds what it reads.
  const source = entry.input === "List" ? entryText(entry) : entrySummary(entry);
  const lookup = lookupText(entry);
  const modifiers = entry.modifiers.map(modifierText);
  const condition = entry.when;
  // The modifiers change the value a lookup compares, not the cached field, so they read after that value.
  const detail = entry.input === "List"
    ? `${entryText(entry)} -> ${entry.target}`
    : [
      source,
      lookupPhrase(entry),
      modifiers.length === 0 ? "" : `| ${modifiers.join(" | ")}`,
      `-> ${entry.target}`,
      condition === null ? "" : `when ${condition}`,
    ].filter((part) => part !== "").join(" ");
  const assertions = propertyAssertions(entry);
  return {
    target: entry.target,
    input: entry.input,
    source,
    sourceValue: entry.input === "Repeat"
      ? `dataset.${entry.child ?? ""}${entry.where === null ? "" : ` where ${entry.where}`}`
      : entry.input === "Static" ? entry.static ?? "" : source,
    lookup,
    lookupPhrase: lookupPhrase(entry),
    lookupDetail: lookupLines(entry),
    findsAll: entry.input === "Cache" && entry.findAll !== null,
    alternatives: entry.input === "Coalesce" ? entry.alternatives.map(alternativeText) : [],
    items: entry.input === "List" ? entry.items.map(entryText) : [],
    objects: isObjectList(entry),
    unverified: entry.unverified,
    ignoreSeparators: entry.input === "Cache" && entry.ignoreSeparators,
    modifiers,
    modifierKinds: entry.modifiers.map((modifier) => (isCachedReplace(modifier) ? "replace from cache" : modifier.kind)).join(", "),
    condition,
    description: entry.description,
    required: entry.required,
    assertions,
    detail,
    search: [detail, entry.description ?? "", ...assertions.map(({ assertion }) => `${assertion.name ?? ""} ${assertionText(assertion)}`)].join("\n").toLowerCase(),
  };
}

/** Text a modifier or a findBy line quotes, so a separator that is a space or a comma is visible. */
function quoted(text: string): string {
  return text.includes("'") ? `"${text}"` : `'${text}'`;
}

/** True for a replace that reads its table from the cache rather than listing its pairs. */
export function isCachedReplace(modifier: MappingDraftModifier): boolean {
  return modifier.kind === "replace" && (modifier.table ?? "").trim() !== "";
}

/**
 * The fields a replace reading a cached table matches on and replaces by, as a render settles them: those it names, or,
 * for a lookup table, its key and the one field it holds beside its key (`value` for a dictionary of pairs). Null where the
 * table cannot settle one: a type of OSDU records has no key and many fields, and a lookup table with several fields
 * beside its key does not say which replaces. `settled` says the field came from the table, not the replace.
 */
export function cachedReplaceFields(
  modifier: MappingDraftModifier,
  type: DeliveryCachedType | undefined,
): { match: string | null; field: string | null; matchSettled: boolean; fieldSettled: boolean } {
  const named = (text: string | null | undefined) => (text ?? "").trim() === "" ? null : (text ?? "").trim();
  const match = named(modifier.match);
  const field = named(modifier.field);
  const key = type?.key ?? null;
  const beside = type === undefined || key === null ? [] : type.fields.filter((name) => name.toLowerCase() !== key.toLowerCase());
  const fieldDefault = key === null ? null : beside.length === 1 ? beside[0] : beside.length === 0 ? "value" : null;
  return {
    match: match ?? key,
    field: field ?? fieldDefault,
    matchSettled: match === null && key !== null,
    fieldSettled: field === null && fieldDefault !== null,
  };
}

/** One modifier in one short line, with the settings its kind carries: `split on ',', part 1`, `replace M to m`. */
export function modifierText(modifier: MappingDraftModifier): string {
  switch (modifier.kind) {
    case "split":
      return `split on ${quoted(modifier.separator ?? "")}, part ${modifier.part ?? 0}`;
    case "replace": {
      if (isCachedReplace(modifier)) {
        const match = (modifier.match ?? "").trim();
        const field = (modifier.field ?? "").trim();
        const fields = match === "" && field === "" ? "" : ` (${match === "" ? "its key" : match} to ${field === "" ? "its value" : field})`;
        return `replace from $cache.${(modifier.table ?? "").trim()}${fields}${otherwiseText(modifier)}`;
      }

      const pairs = (modifier.replacements ?? []).map((pair) => `${pair.from} to ${pair.to === null ? "no value" : pair.to}`).join(", ");
      return `replace ${pairs}${otherwiseText(modifier)}`;
    }
    case "equals":
      return `equals ${modifier.text ?? ""}`;
    case "date":
      return modifier.text === null || modifier.text === "" ? "date" : `date ${modifier.text}`;
    case "number": {
      const group = modifier.groupSeparator === null ? "" : `, group ${quoted(modifier.groupSeparator)}`;
      return `number, decimal ${quoted(modifier.decimalSeparator ?? ".")}${group}`;
    }
    case "id":
      return `id ${modifier.text ?? ""}`;
    case "ref":
      return modifier.text === null || modifier.text.trim() === "" ? "ref" : `ref ${modifier.text.trim()}`;
    case "trim":
    case "upper":
    case "lower":
      return modifier.kind;
  }
}

/** What a replace says about the values its pairs do not list, when it says anything: `, otherwise no value`. */
function otherwiseText(modifier: MappingDraftModifier): string {
  switch (modifier.otherwiseKind) {
    case "empty":
      return ", otherwise no value";
    case "text":
      return `, otherwise ${modifier.otherwiseText ?? ""}`;
    default:
      return "";
  }
}

/** The value one findBy compares: a dataset column, or a fixed text. */
function operandOf(find: MappingDraftFind): string {
  return find.literal !== null && find.literal.trim() !== "" ? quoted(find.literal) : `dataset.${find.column ?? ""}`;
}

/**
 * The lookup an entry finds its record by, one line per findBy, each naming the record set, the field compared and the
 * value it must equal: `cache.UnitOfMeasure.Code = dataset.elev_meas_ref`,
 * `search.Wellbore.data.FacilityName = dataset.wellbore_uwi`. Empty for an entry that looks nothing up.
 */
export function lookupLines(entry: MappingDraftEntry): string[] {
  if (!looksUp(entry)) {
    return [];
  }

  if (entry.input === "Cache" && entry.findAll !== null) {
    const type = entry.cacheType ?? "";
    return [
      `cache.${type}.${entry.findAll.field} = ${findAllOperand(entry.findAll)}`,
      ...entry.findAll.empty.map((field) => `cache.${type}.${field} is empty`),
    ];
  }

  const prefix = entry.input === "Search" ? "search" : "cache";
  return entry.findBy.map((find) => `${prefix}.${entry.cacheType ?? ""}.${find.field} = ${operandOf(find)}`);
}

/**
 * The lookup as one phrase, the way the renderer words it: the fields that compare the same value run together, and
 * the phrases after the first read as alternatives. `Code/Name/id = dataset.elev_meas_ref`, and empty for an entry
 * that looks nothing up. The cached type or the search is left out: the entry's source already names it.
 */
export function lookupText(entry: MappingDraftEntry): string {
  if (!looksUp(entry)) {
    return "";
  }

  if (entry.input === "Cache" && entry.findAll !== null) {
    return findAllText(entry.findAll);
  }

  const groups: { fields: string[]; operand: string }[] = [];
  for (const find of entry.findBy) {
    const operand = operandOf(find);
    const last = groups.at(-1);
    if (last !== undefined && last.operand === operand) {
      last.fields.push(find.field);
    } else {
      groups.push({ fields: [find.field], operand });
    }
  }

  return groups.map((group) => `${group.fields.join("/")} = ${group.operand}`).join(" or ");
}

/**
 * The dataset columns and child datasets a draft already names: its key, label, entries, lookups, findBy lines, and the
 * conditions of the assertions that judge the row's value.
 */
export function knownColumns(draft: MappingDraft): { columns: string[]; children: string[] } {
  const columns = new Set<string>(draft.key);
  const children = new Set<string>();
  // A coalesce entry reads through its alternatives and a list through its items, which name columns as any entry does.
  for (const entry of draft.entries.flatMap(valueNodesOf)) {
    if (entry.column !== null && entry.column !== "") {
      columns.add(entry.column);
    }

    if (entry.child !== null && entry.child !== "") {
      children.add(entry.child);
    }

    for (const find of entry.findBy) {
      if (find.column !== null && find.column !== "") {
        columns.add(find.column);
      }
    }

    if (entry.findAll !== null && (entry.findAll.column ?? "") !== "") {
      columns.add(entry.findAll.column ?? "");
    }

    // An assertion judging the row's value reads columns in its conditions, named as an entry names them.
    for (const filter of (entry.assertions ?? []).filter((assertion) => assertion.stage === "incoming").flatMap((assertion) => assertion.where)) {
      if (filter.reads === "column" && filter.path.trim() !== "") {
        columns.add(filter.path.trim());
      }
    }
  }

  for (const find of draft.lookups.flatMap((lookup) => lookup.findBy)) {
    if (find.column !== null && find.column !== "") {
      columns.add(find.column);
    }
  }

  for (const match of (draft.label ?? "").matchAll(/\{dataset\.([A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)?)\}/g)) {
    columns.add(match[1]);
  }

  return { columns: [...columns].sort(), children: [...children].sort() };
}

/** How the entry editor offers a static value: a list of strings, one text, number or boolean, or JSON. */
export type StaticMode = "list" | "text" | "number" | "boolean" | "json";

/** JSON text parsed, or why it does not parse. Empty text is no value. */
export function parseJson(text: string | null): { ok: true; value: unknown } | { ok: false; error: string } {
  if (text === null || text.trim() === "") {
    return { ok: true, value: undefined };
  }

  try {
    return { ok: true, value: JSON.parse(text) as unknown };
  } catch (error) {
    return { ok: false, error: error instanceof Error ? error.message : String(error) };
  }
}

/**
 * The editor for a variable's static value: a list editor for a list of values, a typed field for one value, and JSON
 * for objects, lists of objects, and any value already written in a form those editors cannot show.
 */
export function staticModeFor(variable: Pick<DeliveryTemplateVariable, "shape" | "type">, json: string | null): StaticMode {
  const parsed = parseJson(json);
  if (!parsed.ok) {
    return "json";
  }

  const value = parsed.value;
  if (variable.shape === "ValueList") {
    return value === undefined || (Array.isArray(value) && value.every((item) => item === null || typeof item !== "object"))
      ? "list"
      : "json";
  }

  if (variable.shape !== "Value") {
    return "json";
  }

  switch (variable.type) {
    case "boolean":
      return value === undefined || typeof value === "boolean" ? "boolean" : "json";
    case "number":
    case "integer":
      return value === undefined || typeof value === "number" ? "number" : "json";
    default:
      return value === undefined || typeof value === "string" ? "text" : "json";
  }
}
