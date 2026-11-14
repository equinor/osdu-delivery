import type { CacheTypeDraft } from "../../../../api/explorer";
import type { InspectorTrailRecord } from "../../OsduRecordInspector";
import { locationOf, namesOf } from "../../recordPath";
import {
  attributeNameFor, chainNameBase, kindMatches, pathOf, questionsOf, stepReads, type AmbiguousPick, type PlannedStep, type SegmentFilter,
} from "../dimension/dimensionDraft";
import { MAX_CACHE_ATTRIBUTES, MAX_CACHE_STEPS, fieldName, suggestedAs } from "./cacheStory";

// What a pick in a record on the explorer's trail makes of a drafted cached type
// (osdu/docs/reference/concepts/explorer-cache-builder.md). The trail's first record is one of the type's kind, a record
// the cache would hold: a value of it is kept (a field). A value of a record further along is an attribute: every link
// on the way is a step of its path, the first read in the cached record itself, as a cache flow writes an attribute
// (osdu/docs/reference/flow/cache-attributes.md). Where a step passes through one item of a list whose items answer
// differently, a filter is asked for, as the dimension builder asks.

/** What a person can make of a value of a record on the explorer's trail while a cached type is built. */
export type CacheAction = "field" | "attribute";

/** A pick in the explorer: what to make of a value, the records from the one the explorer opened to the one it is in, and its path there. */
export interface CachePick {
  action: CacheAction;
  trail: InspectorTrailRecord[];
  /** The value's path in the last record of the trail, as the inspector names it (`data.GeoContexts[1].FieldID`). */
  path: string;
}

/** What a pick makes of the draft before any question is answered. */
export interface CachePlan {
  action: CacheAction;
  /** The kind the type caches once the pick is made. */
  kind: string;
  /** The path kept, for a field. */
  path: string;
  /** The steps of an attribute's path, the first read in the cached record. */
  steps: PlannedStep[];
  questions: AmbiguousPick[];
}

/** Why a pick cannot be made. */
export interface CacheRefusal {
  problem: string;
}

const kindOf = (record: Record<string, unknown> | null): string => (record !== null && typeof record.kind === "string" ? record.kind : "");

/** The names the type's values go by already: its fields' and its attributes'. */
export function namesTaken(draft: CacheTypeDraft): string[] {
  return [...draft.fields.map(fieldName), ...draft.attributes.map((a) => a.name)];
}

/**
 * What a pick in the explorer makes of the draft, or why it cannot be made. The trail's first record sets the kind of a
 * draft that has none, and must be of the draft's kind otherwise. In that record a value is kept as a field; in a record
 * further along it is an attribute, read through every link from the first record to it.
 */
export function planCachePick(draft: CacheTypeDraft, pick: CachePick): CachePlan | CacheRefusal {
  const refused = (problem: string): CacheRefusal => ({ problem });
  const root = pick.trail[0];
  if (root === undefined || root.record === null) {
    return refused("The record is still being read.");
  }

  const rootKind = kindOf(root.record);
  if (rootKind === "") {
    return refused("This record says no kind, so no cache flow can search for it.");
  }

  if (draft.kind !== "" && !kindMatches(draft.kind, rootKind)) {
    return refused(`The type caches ${draft.kind}, and this record is ${rootKind}. Open one of its records, or start over to cache this kind.`);
  }

  const kind = draft.kind === "" ? rootKind : draft.kind;
  const location = locationOf(pick.path);
  const names = namesOf(location);
  if (names.length === 0) {
    return refused("This is no property of the record.");
  }

  const level = pick.trail.length - 1;
  const plain = names.join(".");
  if (pick.action === "field") {
    if (level > 0) {
      return refused("A value kept as it is belongs to the cached record itself: here, add it as an attribute, read through the link that led here.");
    }

    if (plain === "id") {
      return refused("A record's id is always kept.");
    }

    const kept = draft.fields.find((field) => field.path === plain);
    return kept === undefined
      ? { action: "field", kind, path: plain, steps: [], questions: [] }
      : refused(`${plain} is kept as ${fieldName(kept)}.`);
  }

  if (level === 0) {
    return refused("An attribute is read through a link: open the record a value here names, and pick the value there. A value of this record is kept as it is.");
  }

  if (level >= MAX_CACHE_STEPS) {
    return refused(`This record is ${level} links from the cached record, and an attribute reads through at most ${MAX_CACHE_STEPS - 1}.`);
  }

  if (draft.attributes.length >= MAX_CACHE_ATTRIBUTES) {
    return refused(`A cached type reads at most ${MAX_CACHE_ATTRIBUTES} attributes.`);
  }

  for (let at = 1; at <= level; at++) {
    if (pick.trail[at].from === null) {
      return refused("This record was opened from the records that mention the one before it, not from a value of it, so no path of an attribute reaches it.");
    }

    if (pick.trail[at - 1].record === null) {
      return refused("A record on the way is still being read.");
    }
  }

  const last = pick.trail[level];
  if (last.record === null) {
    return refused("The record is still being read.");
  }

  // Each step is read in the record before the one it opens: the first link in the cached record, and so on, the value
  // itself in the record it is picked in.
  const steps: (PlannedStep & { record: Record<string, unknown> })[] = [];
  for (let at = 1; at <= level; at++) {
    steps.push({ location: locationOf(pick.trail[at].from!), readIn: pick.trail[at - 1].id, record: pick.trail[at - 1].record! });
  }

  steps.push({ location, readIn: last.id, record: last.record });
  return {
    action: "attribute", kind, path: plain,
    steps: steps.map(({ location: at, readIn }) => ({ location: at, readIn })),
    questions: questionsOf(steps, "the attribute"),
  };
}

/**
 * The draft once a plan is made, each of its questions answered by a filter or by none (`answers`, in the order of the
 * questions): a field is added, under the property it ends with where its path's own name would be awkward; an attribute
 * is added under a name no value of the type has, unless one reads the same steps already.
 */
export function applyCachePlan(draft: CacheTypeDraft, plan: CachePlan, answers: readonly (SegmentFilter | null)[]): CacheTypeDraft {
  const base: CacheTypeDraft = { ...draft, kind: plan.kind };
  if (plan.action === "field") {
    return { ...base, fields: [...base.fields, { path: plan.path, as: suggestedAs(plan.path, namesTaken(base)) }] };
  }

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
  if (base.attributes.some((a) => JSON.stringify(a.steps) === JSON.stringify(steps))) {
    return base;
  }

  const readIn = plan.steps[plan.steps.length - 1]?.readIn ?? null;
  const name = attributeNameFor(chainNameBase(steps, lastFilter, readIn), namesTaken(base));
  return { ...base, attributes: [...base.attributes, { name, steps }] };
}

/** A use the draft makes of a value: kept as a field, read as an attribute, or a step an attribute is read through. */
export interface CacheMark {
  role: "field" | "attribute" | "step";
  /** The name it is kept under; for a step, the attribute's it leads to. */
  name: string;
}

/**
 * What the draft reads at a value of a record on the explorer's trail: in the trail's first record, the fields kept and
 * the first step of each attribute; in a record reached through its links, every attribute whose steps lead there, their
 * filters checked against the records on the way, and the steps on the way to them.
 */
export function cacheMarksOf(draft: CacheTypeDraft, trail: readonly InspectorTrailRecord[], path: string): CacheMark[] {
  const root = trail[0];
  if (root === undefined || root.record === null || (draft.kind !== "" && !kindMatches(draft.kind, kindOf(root.record)))) {
    return [];
  }

  const location = locationOf(path);
  const level = trail.length - 1;
  const marks: CacheMark[] = [];
  if (level === 0) {
    const plain = namesOf(location).join(".");
    marks.push(...draft.fields.filter((field) => field.path === plain).map((field) => ({ role: "field" as const, name: fieldName(field) })));
  }

  for (const attribute of draft.attributes) {
    if (attribute.steps.length <= level) {
      continue;
    }

    let reaches = true;
    for (let step = 0; step < level && reaches; step++) {
      const opened = trail[step + 1];
      reaches = opened.from !== null && stepReads(attribute.steps[step], locationOf(opened.from), trail[step].record);
    }

    if (reaches && stepReads(attribute.steps[level], location, trail[level].record)) {
      marks.push({ role: attribute.steps.length === level + 1 ? "attribute" : "step", name: attribute.name });
    }
  }

  return marks;
}
