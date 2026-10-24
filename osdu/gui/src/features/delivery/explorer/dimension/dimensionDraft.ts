import type { DimensionDraft, DimensionDraftAttribute, DimensionDraftCleanStep, DimensionKeyCandidate } from "../../../../api/explorer";
import type { InspectorTrailRecord } from "../../OsduRecordInspector";
import { isRecordReference, withoutVersion } from "../../osduDocument";
import { locationOf, namesOf, type Location } from "../../recordPath";

// What the explorer's dimension builder holds while a person builds a dimension from the records OSDU holds
// (osdu/docs/reference/concepts/explorer.md, Building a dimension): the draft, kept in the explorer's address; what a
// pick in a record on the explorer's trail makes of it (the trail's first record is one of the dimension's kind, the
// link it is left by is the key, and every link after it a step of a value's path); the filters suggested where a step
// passes through one item of a list; and which values of a record the draft reads, to mark them. Every check of what a
// dimension may hold is the control plane's, which reads the draft back through the loader a flow is read by.

/** The most records a label or an attribute reads through, one path each: DimensionSpec.MaxLabelSteps. */
export const MAX_STEPS = 3;

/** The most attributes a dimension reads: DimensionSpec.MaxAttributes. */
export const MAX_ATTRIBUTES = 20;

/** The keys the example steps through: the commonest, as a dimension's own pages offer theirs. */
export const EXAMPLES = 25;

/** The longest attribute name: DimensionAttributeSpec.MaxNameLength. */
export const MAX_NAME_LENGTH = 64;

/** The clean steps written as their name alone, as the documentation names them. */
export const PLAIN_STEPS = ["trim", "collapseSpaces", "upper", "lower", "nfc", "nfkc", "foldSeparators"] as const;

/** The explorer's address part holding the draft while a dimension is built. */
export const DRAFT_PARAM = "dim";

/** Names an attribute cannot take: the columns every dimension's table has, and the words a key's own facts go by. */
const RESERVED = new Set(["value", "keys", "key", "key_id", "records", "filter", "label", "id", "partition"]);

/** A draft that has picked nothing but the kind it reads (none yet, for an empty kind). */
export function emptyDraft(kind: string): DimensionDraft {
  return {
    name: "",
    description: null,
    kind,
    query: null,
    path: null,
    label: [],
    unlabelled: null,
    attributes: [],
    clean: [],
    keyColumn: null,
    valueColumn: null,
    countRecords: false,
    maxValues: null,
  };
}

const text = (value: unknown): string | null => (typeof value === "string" ? value : null);
const texts = (value: unknown): string[] => (Array.isArray(value) ? value.filter((v): v is string => typeof v === "string") : []);
const isObject = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === "object" && !Array.isArray(value);

/**
 * The draft an address holds, read defensively: an address is typed, pasted and kept, so whatever it holds that is not a
 * draft's part is passed over rather than trusted. An address holding nothing a draft can be read from starts an empty one.
 */
export function draftFromParam(param: string): DimensionDraft {
  const fresh = emptyDraft("");
  let parsed: unknown;
  try {
    parsed = JSON.parse(param);
  } catch {
    return fresh;
  }

  if (!isObject(parsed)) {
    return fresh;
  }

  const held = parsed;
  const attributes = Array.isArray(held.attributes)
    ? held.attributes
      .filter(isObject)
      .map((a): DimensionDraftAttribute => ({
        name: text(a.name) ?? "",
        steps: Array.isArray(a.steps) ? texts(a.steps) : null,
        collect: text(a.collect),
      }))
      .filter((a) => (a.steps !== null && a.steps.length > 0) || a.collect !== null)
    : [];
  const clean = Array.isArray(held.clean)
    ? held.clean
      .filter((c): c is Record<string, unknown> => isObject(c) && typeof c.step === "string")
      .map((c): DimensionDraftCleanStep => ({ step: c.step as string, pattern: text(c.pattern), with: text(c.with) }))
    : [];
  const maxValues = typeof held.maxValues === "number" && Number.isSafeInteger(held.maxValues) ? held.maxValues : null;
  return {
    name: text(held.name) ?? "",
    description: text(held.description),
    kind: text(held.kind) ?? "",
    query: text(held.query),
    path: text(held.path),
    label: texts(held.label),
    unlabelled: text(held.unlabelled),
    attributes,
    clean,
    keyColumn: text(held.keyColumn),
    valueColumn: text(held.valueColumn),
    countRecords: held.countRecords === true,
    maxValues,
  };
}

/** The draft as an address holds it: the parts it has, and nothing for the parts it leaves empty. */
export function draftParam(draft: DimensionDraft): string {
  const kept: Record<string, unknown> = {};
  for (const [name, value] of Object.entries(draft)) {
    if (value === null || value === "" || value === false || (Array.isArray(value) && value.length === 0)) {
      continue;
    }

    kept[name] = value;
  }

  return JSON.stringify(kept);
}

/** How a filter compares a property of an object with its text: equal, containing it, or ending with it. */
export type FilterCompare = "=" | "*=" | "$=";

/** A filter on one segment of a path: of the objects the segment holds, those whose property compares with the text. */
export interface SegmentFilter {
  property: string;
  compare: FilterCompare;
  value: string;
}

/**
 * The path a location is read by: its property names joined by dots, an array met on the way stepped into, and a filter on
 * the segment it names (by its place among the names), written `[Property*=text]` after it.
 */
export function pathOf(location: Location, filters: ReadonlyMap<number, SegmentFilter> = new Map()): string {
  return namesOf(location)
    .map((name, index) => {
      const filter = filters.get(index);
      return filter === undefined ? name : `${name}[${filter.property}${filter.compare}${filter.value.trim()}]`;
    })
    .join(".");
}

/** A path without its filters: the property names it reads through. */
export function plainPath(path: string): string {
  return path.replace(/\[[^\]]*\]/g, "");
}

/** Why a filter cannot be written: a property that is no name, or a text that is empty or holds the `]` that would end it. */
export function filterProblem(filter: SegmentFilter): string | null {
  if (!/^[A-Za-z0-9_]+$/.test(filter.property)) {
    return "The property compared is a name of letters, digits and underscores.";
  }

  const value = filter.value.trim();
  if (value === "") {
    return "Give the text the property is compared with.";
  }

  return value.includes("]") ? "The text cannot hold ], which ends a filter." : null;
}

/** A segment of a written step: the property, and the filter on the objects it holds. */
interface StepSegment {
  name: string;
  filter: SegmentFilter | null;
}

/** The segments of a step as a draft writes it (`data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName`); null for text that is not one. */
export function stepSegments(step: string): StepSegment[] | null {
  const segments: StepSegment[] = [];
  for (const part of step.match(/[^.[]+(?:\[[^\]]*\])?/g) ?? []) {
    const match = /^([A-Za-z0-9_]+)(?:\[([A-Za-z0-9_]+)(\*=|\$=|=)([^\]]*)\])?$/.exec(part);
    if (match === null) {
      return null;
    }

    segments.push({
      name: match[1],
      filter: match[2] === undefined ? null : { property: match[2], compare: match[3] as FilterCompare, value: match[4] },
    });
  }

  return segments.length === 0 ? null : segments;
}

/** The property a path ends with, its filters set aside. */
export function lastProperty(path: string): string {
  const names = plainPath(path).split(".");
  return names[names.length - 1] ?? path;
}

/** Whether two names are the same name, as the loader compares them: ignoring case. */
const sameName = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

/**
 * A name for an attribute read at `path`: the property it ends with, made a name an attribute may take (a letter, then
 * letters, digits and underscores, at most 64), never a reserved word and never one of `taken` (compared ignoring case),
 * numbered when it would be.
 */
export function attributeNameFor(path: string, taken: readonly string[]): string {
  let base = lastProperty(path).replace(/[^A-Za-z0-9_]/g, "_");
  if (!/^[A-Za-z]/.test(base)) {
    base = `A${base}`;
  }

  base = base.slice(0, MAX_NAME_LENGTH);
  const free = (name: string) => !RESERVED.has(name.toLowerCase()) && !taken.some((t) => sameName(t, name));
  if (free(base)) {
    return base;
  }

  for (let n = 2; ; n++) {
    const numbered = `${base.slice(0, MAX_NAME_LENGTH - String(n).length)}${n}`;
    if (free(numbered)) {
      return numbered;
    }
  }
}

/**
 * A name for the dimension: the type of record its key names (a wellbore's id, or a key the template says names wellbores,
 * makes a `Wellbore` dimension), or the property its key is read at, with whatever a dimension's name may not hold left out.
 */
export function dimensionNameFor(path: string, example: string | null, names: readonly string[] = []): string {
  const named = example !== null && isRecordReference(example) ? entityTypeOf(example) : names.find((name) => name.includes("--")) ?? null;
  const base = named === null ? lastProperty(path) : typeName(named);
  const cleaned = base.replace(/[^A-Za-z0-9._-]/g, "").replace(/^[^A-Za-z0-9]+/, "");
  return cleaned.slice(0, 100);
}

/** The entity type a record id names: its second segment (`master-data--Wellbore`). */
export function entityTypeOf(id: string): string {
  return withoutVersion(id).split(":")[1] ?? id;
}

/** The type of an entity type, its group aside: `Wellbore` of `master-data--Wellbore`. */
export function typeName(entityType: string): string {
  const at = entityType.indexOf("--");
  return at < 0 ? entityType : entityType.slice(at + 2);
}

/**
 * Whether `kind` is one the dimension's `pattern` reads: four segments each, each the same ignoring case or matched by its
 * wildcards, a star standing for any text, as KindPatterns matches them.
 */
export function kindMatches(pattern: string, kind: string): boolean {
  const want = pattern.split(":");
  const have = kind.split(":");
  if (want.length !== 4 || have.length !== 4) {
    return false;
  }

  return want.every((segment, index) => {
    const expression = segment.split("*").map((part) => part.replace(/[.+?^${}()|[\]\\]/g, "\\$&")).join(".*");
    return new RegExp(`^${expression}$`, "i").test(have[index]);
  });
}

/** One chain of a draft: its value (the label) or an attribute, read through `steps` from the record a key names. */
export interface DraftChain {
  role: "label" | "attribute";
  name: string | null;
  steps: string[];
}

/** The chains a draft reads through the records its keys name: the label first, then each attribute that is read, not collected. */
export function chainsOf(draft: DimensionDraft): DraftChain[] {
  const chains: DraftChain[] = [];
  if (draft.label.length > 0) {
    chains.push({ role: "label", name: null, steps: draft.label });
  }

  for (const attribute of draft.attributes) {
    if (attribute.collect === null && attribute.steps !== null && attribute.steps.length > 0) {
      chains.push({ role: "attribute", name: attribute.name, steps: attribute.steps });
    }
  }

  return chains;
}

/** A property a filter can compare, with the values the objects hold there. */
export interface FilterCandidate {
  property: string;
  values: string[];
}

/** The text a value is compared as: a text, a number or a boolean as written; nothing for anything else. */
function comparable(value: unknown): string | null {
  if (typeof value === "string") {
    return value;
  }

  return typeof value === "number" || typeof value === "boolean" ? String(value) : null;
}

/** The texts an object holds at `property`: its value, or each value of a list of them. */
function heldAt(object: Record<string, unknown>, property: string): string[] {
  const value = object[property];
  const values = Array.isArray(value) ? value : [value];
  return values.map(comparable).filter((v): v is string => v !== null && v.trim() !== "");
}

/**
 * The filter a pick of `chosen` among `objects` is read by, when several objects answer: the properties the chosen object
 * holds a value of (its own `leaf` aside), those that tell the objects apart first, and a type, a kind or a role before
 * anything else, since that is what a person picks one object of a list by; with the filter keeping the chosen object
 * suggested. A record id is compared by containing its type and code (`GeoPoliticalEntityType:Country:`), so the filter
 * reads alike in every partition and in every group the type is kept in; any other text equal, exactly.
 */
export function suggestFilter(objects: readonly Record<string, unknown>[], chosen: number, leaf: string | null): {
  candidates: FilterCandidate[];
  suggested: SegmentFilter | null;
} {
  const picked = objects[chosen];
  if (picked === undefined) {
    return { candidates: [], suggested: null };
  }

  const candidates: (FilterCandidate & { telling: boolean; typed: boolean })[] = [];
  for (const property of Object.keys(picked)) {
    if (property === leaf || !/^[A-Za-z0-9_]+$/.test(property) || heldAt(picked, property).length === 0) {
      continue;
    }

    const values = [...new Set(objects.flatMap((object) => heldAt(object, property)))];
    const mine = new Set(heldAt(picked, property));
    const telling = objects.some((object, index) => index !== chosen && !heldAt(object, property).some((v) => mine.has(v)));
    candidates.push({ property, values, telling, typed: /type|kind|role|category|class/i.test(property) });
  }

  candidates.sort((a, b) => Number(b.typed) - Number(a.typed) || Number(b.telling) - Number(a.telling) || a.property.localeCompare(b.property));
  const best = candidates.find((candidate) => candidate.telling) ?? null;
  return {
    candidates: candidates.map(({ property, values }) => ({ property, values })),
    suggested: best === null ? null : filterFor(best.property, heldAt(picked, best.property)[0]),
  };
}

/** The filter keeping the objects holding `value` at `property`, written as `suggestFilter` says; null when no filter can be written of it. */
export function filterFor(property: string, value: string): SegmentFilter | null {
  const held: unknown = value;
  if (isRecordReference(held)) {
    // The type without its group and the code, as the documentation writes it: GeoPoliticalEntityType:Country:.
    const [, entityType = "", ...code] = withoutVersion(value.trim()).split(":");
    const filter: SegmentFilter = { property, compare: "*=", value: `${typeName(entityType)}:${code.join(":")}:` };
    return filterProblem(filter) === null ? filter : null;
  }

  const filter: SegmentFilter = { property, compare: "=", value: value.trim() };
  return filterProblem(filter) === null ? filter : null;
}

/** Whether `object` is one a filter keeps, as a build compares: equal exactly, or containing or ending with the text ignoring case. */
export function keeps(object: Record<string, unknown>, filter: SegmentFilter): boolean {
  const wanted = filter.value.trim();
  return heldAt(object, filter.property).some((value) => {
    switch (filter.compare) {
      case "*=":
        return value.toLowerCase().includes(wanted.toLowerCase());
      case "$=":
        return value.toLowerCase().endsWith(wanted.toLowerCase());
      default:
        return value === wanted;
    }
  });
}

/** The value at `location` in `record`, or undefined where it holds nothing. */
export function valueAt(record: unknown, location: Location): unknown {
  let at: unknown = record;
  for (const part of location) {
    if (at === null || typeof at !== "object") {
      return undefined;
    }

    at = (at as Record<string | number, unknown>)[part];
  }

  return at;
}

/** The values each of `records` holds at the property names `names`, its lists stepped into: what a build reads there, record by record. */
export function valuesAcross(records: readonly unknown[], names: readonly string[]): string[][] {
  const read = (node: unknown, at: number): string[] => {
    if (Array.isArray(node)) {
      return node.flatMap((item) => read(item, at));
    }

    if (at === names.length) {
      const value = comparable(node);
      return value === null ? [] : [value];
    }

    return isObject(node) ? read(node[names[at]], at + 1) : [];
  };

  return records.map((record) => read(record, 0));
}

/** The first value a record holds at a key's path (`data.WellboreID`), its lists stepped into; null where it holds none. */
export function firstValueAt(record: Record<string, unknown> | null, path: string): string | null {
  if (record === null) {
    return null;
  }

  return valuesAcross([record], path.split("."))[0].find((value) => value.trim() !== "") ?? null;
}

/**
 * Whether a step as a draft writes it reads the value at `location` of `record`: the same properties, and every filter on
 * the way keeping the object the location passes through (the item of a list, or the record's data).
 */
export function stepReads(step: string, location: Location, record: Record<string, unknown> | null): boolean {
  const segments = stepSegments(step);
  const names = namesOf(location);
  if (segments === null || segments.length !== names.length || segments.some((segment, index) => segment.name !== names[index])) {
    return false;
  }

  let name = -1;
  for (let at = 0; at < location.length; at++) {
    if (typeof location[at] !== "string") {
      continue;
    }

    name++;
    const filter = segments[name].filter;
    if (filter === null) {
      continue;
    }

    // The objects the filter compares: the item the location steps into next, or what the segment holds.
    const into = typeof location[at + 1] === "number" ? location.slice(0, at + 2) : location.slice(0, at + 1);
    const held = valueAt(record, into);
    const kept = Array.isArray(held) ? held.filter(isObject).some((object) => keeps(object, filter)) : isObject(held) && keeps(held, filter);
    if (!kept) {
      return false;
    }
  }

  return true;
}

/**
 * A pick that passes through one item of a list whose items answer differently, waiting on how it is read: the list, its
 * items, the one picked, what each holds there, and where a filter goes.
 */
export interface AmbiguousPick {
  /** What is being picked, as a person reads it. */
  what: string;
  /** The list's path in the record it is in (`data.GeoContexts`). */
  list: string;
  objects: Record<string, unknown>[];
  /** What each object is called, for the list. */
  names: string[];
  /** What each object holds where the pick reads, in order; the first non-empty is what a build keeps without a filter. */
  values: string[][];
  chosen: number;
  /** The property the pick reads in each object, which a filter does not compare. */
  leaf: string | null;
  /** True when every value is followed rather than the first kept: a step naming the next records. */
  follows: boolean;
  /** The step of the path the filter goes on, from 0. */
  step: number;
  /** The segment of that step the filter goes on, by its place among the step's names. */
  segment: number;
}

/** Whether the objects answer a pick differently: another object holds a value too where a step follows them all, or the values are not one. */
function differs(values: string[][], chosen: number, follows: boolean): boolean {
  if (follows) {
    return values.some((held, index) => index !== chosen && held.length > 0);
  }

  return new Set(values.flat().filter((v) => v.trim() !== "")).size > 1;
}

/** What an item of a list is called: its place, and the type or kind it says it is, where it says one. */
function itemName(object: Record<string, unknown>, index: number): string {
  const typed = Object.entries(object).find(([property, value]) => /type|kind/i.test(property) && typeof value === "string" && value !== "");
  if (typed === undefined) {
    return `item ${index + 1}`;
  }

  const value = typed[1] as string;
  const shown = isRecordReference(value) ? withoutVersion(value).split(":").filter((part) => part !== "").pop() ?? value : value;
  return `item ${index + 1}: ${shown}`;
}

/**
 * The name an attribute read through `steps` starts from: what its filter keeps (`Country` of a filter on a country's type),
 * the type of the records its last step reads in when it reads through another record (`Field`), else the property it ends
 * with (`FacilityID`).
 */
export function chainNameBase(steps: readonly string[], filter: SegmentFilter | null, readIn: string | null): string {
  if (filter !== null && filter.value.includes(":")) {
    const code = filter.value.split(":").filter((part) => part.trim() !== "").pop();
    if (code !== undefined && /^[A-Za-z][A-Za-z0-9_ -]*$/.test(code.trim())) {
      return code.trim().replace(/[ -]/g, "_");
    }
  }

  if (steps.length > 1 && readIn !== null) {
    return typeName(entityTypeOf(readIn));
  }

  return lastProperty(steps[steps.length - 1] ?? "");
}

/** What a person can make of a value of a record on the explorer's trail. */
export type BuildAction = "key" | "collect" | "value" | "attribute";

/** A pick in the explorer: what to make of a value, the records from the one the explorer opened to the one it is in, and its path there. */
export interface BuildPick {
  action: BuildAction;
  trail: InspectorTrailRecord[];
  /** The value's path in the last record of the trail, as the inspector names it (`data.GeoContexts[1].GeoPoliticalEntityID`). */
  path: string;
}

/** A step of a value's or an attribute's path: where it is read, in which record. */
export interface PlannedStep {
  location: Location;
  /** The id of the record the step is read in. */
  readIn: string;
}

/** What a pick makes of the draft before any question is answered. */
export interface PickPlan {
  action: BuildAction;
  /** The kind the dimension reads once the pick is made. */
  kind: string;
  /** The key once the pick is made. */
  key: string;
  /** What the dimension is named after when it has no name yet: a value the key holds in the record picked from. */
  keyExample: string | null;
  /** The key's or the collected path, for a key or a collect. */
  path: string;
  /** The steps of a value's or an attribute's path, the first read in the record the key names. */
  steps: PlannedStep[];
  /** The lists of objects the steps pass through one item of, where the objects answer differently: a question each. */
  questions: AmbiguousPick[];
}

/** Why a pick cannot be made, and the key that would let it, where another key would. */
export interface PickRefusal {
  problem: string;
  rekey: string | null;
}

/** The kind a record says it is; empty where it says none. */
function kindOf(record: Record<string, unknown> | null): string {
  return record === null ? "" : text(record.kind) ?? "";
}

/**
 * What a pick in the explorer makes of the draft, or why it cannot be made. The trail's first record is one of the
 * dimension's kind (it sets the kind of a draft that has none): its values are made the key or collected. A value of a
 * record further along is the dimension's value or an attribute: the link the first record was left by is the key (it
 * becomes the key of a draft without one, and must be it otherwise), and every link after it a step, each read in the
 * record before. Where a step passes through one item of a list whose items answer differently, a question asks which.
 */
export function planOf(draft: DimensionDraft, pick: BuildPick): PickPlan | PickRefusal {
  const refused = (problem: string, rekey: string | null = null): PickRefusal => ({ problem, rekey });
  const root = pick.trail[0];
  if (root === undefined || root.record === null) {
    return refused("The record is still being read.");
  }

  const rootKind = kindOf(root.record);
  if (rootKind === "") {
    return refused("This record says no kind, so no dimension can read it.");
  }

  if (draft.kind !== "" && !kindMatches(draft.kind, rootKind)) {
    return refused(`The dimension reads ${draft.kind}, and this record is ${rootKind}. Open one of its records, or start over to read this kind.`);
  }

  const kind = draft.kind === "" ? rootKind : draft.kind;
  const location = locationOf(pick.path);
  const names = namesOf(location);
  if (names.length === 0) {
    return refused("This is no property of the record.");
  }

  const level = pick.trail.length - 1;
  const plain = names.join(".");
  if (pick.action === "key" || pick.action === "collect") {
    if (level > 0) {
      return refused("A key is read in the dimension's own records: go back along the trail to the first record, and pick it there.");
    }

    if (pick.action === "key" && draft.path === plain) {
      return refused(`${plain} is the key.`);
    }

    const collected = draft.attributes.find((a) => a.collect === plain);
    if (pick.action === "collect" && collected !== undefined) {
      return refused(`${plain} is collected as ${collected.name}.`);
    }

    return {
      action: pick.action, kind, key: pick.action === "key" ? plain : draft.path ?? "", keyExample: firstValueAt(root.record, plain),
      path: plain, steps: [], questions: [],
    };
  }

  if (level === 0) {
    return refused("A value is read in the record the key names: open that record from the key's link, and pick it there.");
  }

  if (level > MAX_STEPS) {
    return refused(`This record is ${level} links from the dimension's record, and a value or an attribute reads through at most ${MAX_STEPS} records.`);
  }

  for (let at = 1; at <= level; at++) {
    if (pick.trail[at].from === null) {
      return refused("This record was opened from the records that mention the one before it, not from a value of it, so no path of a dimension reaches it.");
    }

    if (pick.trail[at - 1].record === null) {
      return refused("A record on the way is still being read.");
    }
  }

  const keyPath = namesOf(locationOf(pick.trail[1].from!)).join(".");
  if (draft.path !== null && draft.path !== "" && draft.path !== keyPath) {
    return refused(`This record is reached through ${keyPath}, and the key is ${draft.path}: a value is read in the record the key names.`, keyPath);
  }

  // Each step is read in the record before the one it opens: the second link in the record the key names, and so on, the
  // value itself in the record it is picked in.
  const steps: (PlannedStep & { record: Record<string, unknown> })[] = [];
  for (let at = 2; at <= level; at++) {
    steps.push({ location: locationOf(pick.trail[at].from!), readIn: pick.trail[at - 1].id, record: pick.trail[at - 1].record! });
  }

  const last = pick.trail[level];
  if (last.record === null) {
    return refused("The record is still being read.");
  }

  steps.push({ location, readIn: last.id, record: last.record });
  const what = pick.action === "value" ? "the value of each key" : "the attribute";
  const questions: AmbiguousPick[] = [];
  steps.forEach((step, index) => {
    const follows = index < steps.length - 1;
    let segment = -1;
    step.location.forEach((part, at) => {
      if (typeof part === "string") {
        segment++;
        return;
      }

      const list = valueAt(step.record, step.location.slice(0, at));
      if (!Array.isArray(list) || list.length < 2 || !list.every(isObject)) {
        return;
      }

      const within = namesOf(step.location.slice(at + 1));
      const values = valuesAcross(list, within);
      if (differs(values, part, follows)) {
        questions.push({
          what: follows ? "the records named next" : what, list: namesOf(step.location.slice(0, at)).join("."), objects: list,
          names: list.map(itemName), values, chosen: part, leaf: within[0] ?? null, follows, step: index, segment,
        });
      }
    });
  });

  return {
    action: pick.action, kind, key: keyPath, keyExample: firstValueAt(root.record, keyPath), path: plain,
    steps: steps.map(({ location: at, readIn }) => ({ location: at, readIn })), questions,
  };
}

/**
 * The draft once a plan is made, each of its questions answered by a filter or by none (`answers`, in the order of the
 * questions): a key replaces the key, naming the dimension when it is unnamed (after the type its value names, or the
 * template says it names: `names`); a collected path is added under a name no column has; a value replaces the value read;
 * an attribute is added under a name no column has. `columns` are the names of the key's and the value's columns.
 */
export function applyPlan(
  draft: DimensionDraft, plan: PickPlan, answers: readonly (SegmentFilter | null)[], columns: readonly string[], names: readonly string[] = [],
): DimensionDraft {
  const named = (path: string) => (draft.name.trim() === "" ? dimensionNameFor(path, plan.keyExample, names) : draft.name);
  const base: DimensionDraft = { ...draft, kind: plan.kind };
  switch (plan.action) {
    case "key":
      return { ...base, path: plan.path, name: named(plan.path) };

    case "collect": {
      // A dimension collects one attribute at most: a path collected replaces the one collected before.
      const others = base.attributes.filter((a) => a.collect === null);
      const name = attributeNameFor(plan.path, [...others.map((a) => a.name), ...columns]);
      return { ...base, attributes: [...others, { name, steps: null, collect: plan.path }] };
    }

    case "value":
    case "attribute": {
      const keyed: DimensionDraft = base.path === plan.key ? base : { ...base, path: plan.key, name: named(plan.key) };
      const filters = plan.steps.map(() => new Map<number, SegmentFilter>());
      let lastFilter: SegmentFilter | null = null;
      for (let index = 0; index < plan.questions.length; index++) {
        const question = plan.questions[index];
        const filter = answers[index] ?? null;
        if (filter !== null) {
          filters[question.step].set(question.segment, filter);
          lastFilter = filter;
        }
      }

      const steps = plan.steps.map((step, index) => pathOf(step.location, filters[index]));
      if (plan.action === "value") {
        return JSON.stringify(steps) === JSON.stringify(keyed.label) ? keyed : { ...keyed, label: steps };
      }

      if (keyed.attributes.some((a) => a.collect === null && JSON.stringify(a.steps) === JSON.stringify(steps))) {
        return keyed;
      }

      const readIn = plan.steps[plan.steps.length - 1]?.readIn ?? null;
      const name = attributeNameFor(chainNameBase(steps, lastFilter, readIn), [...keyed.attributes.map((a) => a.name), ...columns]);
      return { ...keyed, attributes: [...keyed.attributes, { name, steps, collect: null }] };
    }
  }
}

/** A use the draft makes of a value: its key, its value, an attribute, a collected attribute, or a step a value or an attribute is read through. */
export interface ValueMark {
  role: "key" | "value" | "attribute" | "collect" | "step";
  /** The attribute's name; for a step, the value's or the attribute's it leads to. */
  name: string;
}

/**
 * What the draft reads at a value of a record on the explorer's trail: in the trail's first record, its key and what it
 * collects; in a record reached through the key, every value and attribute whose steps lead there, their filters checked
 * against the records on the way, and the steps on the way to them.
 */
export function marksOf(draft: DimensionDraft, trail: readonly InspectorTrailRecord[], path: string): ValueMark[] {
  const root = trail[0];
  if (root === undefined || root.record === null || (draft.kind !== "" && !kindMatches(draft.kind, kindOf(root.record)))) {
    return [];
  }

  const location = locationOf(path);
  const level = trail.length - 1;
  if (level === 0) {
    const plain = namesOf(location).join(".");
    return [
      ...(draft.path === plain ? [{ role: "key" as const, name: "key" }] : []),
      ...draft.attributes.filter((a) => a.collect === plain).map((a) => ({ role: "collect" as const, name: a.name })),
    ];
  }

  if (draft.path === null || trail[1].from === null || namesOf(locationOf(trail[1].from)).join(".") !== draft.path) {
    return [];
  }

  const marks: ValueMark[] = [];
  for (const chain of chainsOf(draft)) {
    if (chain.steps.length < level) {
      continue;
    }

    let reaches = true;
    for (let step = 0; step < level - 1 && reaches; step++) {
      const opened = trail[step + 2];
      reaches = opened.from !== null && stepReads(chain.steps[step], locationOf(opened.from), trail[step + 1].record);
    }

    if (!reaches || !stepReads(chain.steps[level - 1], location, trail[level].record)) {
      continue;
    }

    const name = chain.role === "label" ? "value" : chain.name ?? "";
    marks.push(chain.steps.length === level ? { role: chain.role === "label" ? "value" : "attribute", name } : { role: "step", name });
  }

  return marks;
}

/**
 * The key a build starts from: the likeliest of the template's suggestions that the record in view holds a value at, or
 * the likeliest of all where it holds none of them (or none is in view); null where the template suggests nothing.
 */
export function chooseKey(candidates: readonly DimensionKeyCandidate[], record: Record<string, unknown> | null): DimensionKeyCandidate | null {
  return candidates.find((candidate) => firstValueAt(record, candidate.path) !== null) ?? candidates[0] ?? null;
}
