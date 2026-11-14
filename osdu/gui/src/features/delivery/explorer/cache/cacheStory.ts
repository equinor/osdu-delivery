import { kindTypeName, lastProperty, type StepState } from "../dimension/dimensionStory.ts";

// How the explorer's cache builder tells a drafted cached type as a story
// (osdu/docs/reference/concepts/explorer-cache-builder.md): the draft an address holds, where each step stands and which
// asks for a pick next, which step each of the builder's findings is about, the names a type and its values take when the
// draft gives none, how a sampled value reads in a cell, and the lookup a mapping reads the type by. Every check of what
// a type may hold is the control plane's, which reads the draft back through the loader a cache flow is read by.

/** The explorer's address part holding the draft while a cached type is built. */
export const CACHE_PARAM = "cache";

/** The most records an attribute reads through, one path each, the cached record first: DimensionSpec.MaxLabelSteps. */
export const MAX_CACHE_STEPS = 3;

/** The most attributes a cached type reads (osdu/docs/reference/flow/cache-attributes.md). */
export const MAX_CACHE_ATTRIBUTES = 64;

/** A drafted cached type, as the story reads it: the builder's draft, structurally. */
export interface CacheStoryDraft {
  name: string;
  kind: string;
  query: string | null;
  fields: { path: string; as: string | null }[];
  attributes: { name: string; steps: string[] }[];
  fetchRecords: boolean;
  onChange: "approve" | null;
}

/** A draft that has picked nothing but the kind it caches (none yet, for an empty kind). */
export function emptyCacheDraft(kind: string): CacheStoryDraft {
  return { name: "", kind, query: null, fields: [], attributes: [], fetchRecords: false, onChange: null };
}

const text = (value: unknown): string | null => (typeof value === "string" ? value : null);
const isObject = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === "object" && !Array.isArray(value);

/**
 * The draft an address holds, read defensively: an address is typed, pasted and kept, so whatever it holds that is not a
 * draft's part is passed over rather than trusted. An address holding nothing a draft can be read from starts an empty one.
 */
export function cacheDraftFromParam(param: string): CacheStoryDraft {
  const fresh = emptyCacheDraft("");
  let parsed: unknown;
  try {
    parsed = JSON.parse(param);
  } catch {
    return fresh;
  }

  if (!isObject(parsed)) {
    return fresh;
  }

  const fields = Array.isArray(parsed.fields)
    ? parsed.fields.filter(isObject).map((f) => ({ path: text(f.path) ?? "", as: text(f.as) })).filter((f) => f.path !== "")
    : [];
  const attributes = Array.isArray(parsed.attributes)
    ? parsed.attributes
      .filter(isObject)
      .map((a) => ({ name: text(a.name) ?? "", steps: Array.isArray(a.steps) ? a.steps.filter((s): s is string => typeof s === "string") : [] }))
      .filter((a) => a.steps.length > 0)
    : [];
  return {
    name: text(parsed.name) ?? "",
    kind: text(parsed.kind) ?? "",
    query: text(parsed.query),
    fields,
    attributes,
    fetchRecords: parsed.fetchRecords === true,
    onChange: parsed.onChange === "approve" ? "approve" : null,
  };
}

/** The draft as an address holds it: the parts it has, and nothing for the parts it leaves empty. */
export function cacheDraftParam(draft: CacheStoryDraft): string {
  const kept: Record<string, unknown> = {};
  for (const [name, value] of Object.entries(draft)) {
    if (value === null || value === "" || value === false || (Array.isArray(value) && value.length === 0)) {
      continue;
    }

    kept[name] = name === "fields" ? (value as CacheStoryDraft["fields"]).map((f) => (f.as === null ? { path: f.path } : f)) : value;
  }

  return JSON.stringify(kept);
}

/** The name a mapping reads the type by when the draft gives none: the kind's type (`Wellbore` of `master-data--Wellbore`). */
export function defaultTypeName(kind: string): string {
  return kind === "" ? "" : kindTypeName(kind);
}

/** The name a type goes by: the draft's own, or the kind's. */
export function typeNameOf(draft: Pick<CacheStoryDraft, "name" | "kind">): string {
  return draft.name.trim() === "" ? defaultTypeName(draft.kind) : draft.name.trim();
}

/** The name the cache keeps a path under when the draft gives none: the path without `data.` (ReferenceField.Normalize). */
export function pathName(path: string): string {
  const trimmed = path.trim().replace(/^\$\.?/, "");
  return /^data\./i.test(trimmed) ? trimmed.slice(5) : trimmed;
}

/** The name the cache keeps a field under: the one the draft gives, or the path's own. */
export function fieldName(field: { path: string; as: string | null }): string {
  return field.as !== null && field.as.trim() !== "" ? field.as.trim() : pathName(field.path);
}

/**
 * The name a picked path is kept under, when the path's own would be awkward for a mapping to read: a path inside an
 * object or a list (`data.NameAliases.AliasName`) is kept under the property it ends with (`AliasName`), numbered where
 * a value of the type has that name already; a property of the record's data keeps the name the path gives (null).
 */
export function suggestedAs(path: string, taken: readonly string[]): string | null {
  const own = pathName(path);
  const free = (name: string) => !taken.some((t) => t.toLowerCase() === name.toLowerCase()) && name.toLowerCase() !== "id";
  if (!own.includes(".") && free(own)) {
    return null;
  }

  const base = lastProperty(path).replace(/[^A-Za-z0-9_]/g, "_") || "Value";
  if (free(base)) {
    return base;
  }

  for (let n = 2; ; n++) {
    if (free(`${base}${n}`)) {
      return `${base}${n}`;
    }
  }
}

/** The steps of the cache builder's story, in the order they are read. */
export type CacheStep = "from" | "keeps" | "with" | "read" | "makes";

export const CACHE_STEPS: readonly CacheStep[] = ["from", "keeps", "with", "read", "makes"];

/**
 * Where each step stands: the records first, then what is kept of each, which asks for a pick while the type keeps
 * nothing; the attributes and the settings are open once there is a kind, chosen once they hold something; and the type
 * it makes waits for something to keep.
 */
export function cacheStepStates(draft: CacheStoryDraft): Record<CacheStep, StepState> {
  if (draft.kind === "") {
    return { from: "next", keeps: "waiting", with: "waiting", read: "waiting", makes: "waiting" };
  }

  const keepsSomething = draft.fields.length > 0 || draft.attributes.length > 0;
  return {
    from: "done",
    keeps: draft.fields.length > 0 ? "done" : keepsSomething ? "open" : "next",
    with: draft.attributes.length > 0 ? "done" : "open",
    read: draft.fetchRecords || draft.onChange !== null ? "done" : "open",
    makes: keepsSomething ? "done" : "waiting",
  };
}

/** The step asking for a pick, the first that does; null when none asks. */
export function nextCacheStep(states: Record<CacheStep, StepState>): CacheStep | null {
  return CACHE_STEPS.find((step) => states[step] === "next") ?? null;
}

/**
 * The step a finding is about, by the part of the type it names: the name heads the panel, the kind and the query are
 * where the records come from, the fields what is kept, an attribute what is read through, and the two settings how the
 * records are read; a finding about the whole type is said where the type is made.
 */
export function cacheStepOfTarget(target: string | null): CacheStep | "name" {
  if (target === null) {
    return "makes";
  }

  if (target === "name") {
    return "name";
  }

  if (target === "kind" || target === "query" || target === "entityType") {
    return "from";
  }

  if (target === "fields") {
    return "keeps";
  }

  if (target.startsWith("attributes") || target === "lookupKinds") {
    return "with";
  }

  return target === "fetchRecords" || target === "onChange" ? "read" : "makes";
}

/** The attribute a finding names (`attributes.WellName`); null for any other finding. */
export function cacheAttributeOfTarget(target: string | null): string | null {
  return target !== null && target.startsWith("attributes.") ? target.slice("attributes.".length) : null;
}

/** The values of a list a cell joins by. */
const JOINED = "; ";

/**
 * A cached value as a cell reads it: a text as it is, a number or a boolean written out, the values of a list joined, an
 * object as its JSON; null where the record gave none.
 */
export function cellText(value: unknown): string | null {
  if (value === undefined || value === null) {
    return null;
  }

  if (typeof value === "string") {
    return value;
  }

  if (typeof value === "number" || typeof value === "boolean") {
    return String(value);
  }

  if (Array.isArray(value)) {
    const parts = value.map(cellText).filter((part): part is string => part !== null);
    return parts.length === 0 ? null : parts.join(JOINED);
  }

  return JSON.stringify(value);
}

/** How many records a preview shows of how many the type would hold. */
export function sampleWords(shown: number, total: number): string {
  if (total === 0) {
    return "No record matches, so the type would be empty";
  }

  const records = total === 1 ? "record" : "records";
  return shown >= total
    ? `${total === 1 ? "The one record" : `All ${total.toLocaleString("en-US")} ${records}`} the type would hold`
    : `The first ${shown.toLocaleString("en-US")} of the ${total.toLocaleString("en-US")} ${records} the type would hold`;
}

/**
 * The lookup a mapping reads the type by, as a mapping writes one (osdu/docs/reference/flow/mapping-lookups.md): the
 * type by its name, a record of it found by the first value it keeps. Null while the type keeps nothing to find one by.
 */
export function lookupExample(name: string, firstValue: string | null): string | null {
  if (name === "" || firstValue === null) {
    return null;
  }

  const lookup = name.charAt(0).toLowerCase() + name.slice(1);
  return ["lookups:", `  ${lookup}:`, `    $cache: ${name}`, "    $findBy:", `      - ${firstValue} = <a source column>`].join("\n");
}
