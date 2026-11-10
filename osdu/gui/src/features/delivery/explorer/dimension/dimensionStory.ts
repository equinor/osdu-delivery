import { isRecordReference, withoutVersion } from "../../osduDocument.ts";

// How the explorer's dimension builder tells a draft (osdu/docs/reference/concepts/explorer-dimension-builder.md): as a
// story read top to bottom, a step per choice, "from these records, one row per this key, shown as this, with these
// columns, cleaned so, makes this table". This module says which step a draft has reached, which step asks for a pick
// next, which step each of the builder's findings is about, the words a clean step is said in, how the types and
// properties a story names are written, and where in a record a key's value is held. It reads no API type and no SQLFlow
// component, so the unit tier checks it.

/** The steps a dimension is told in, in order: the records read, the key, the value, the attributes, the cleaning, the table. */
export type StoryStep = "from" | "rows" | "shown" | "with" | "cleaned" | "makes";

export const STORY_STEPS: readonly StoryStep[] = ["from", "rows", "shown", "with", "cleaned", "makes"];

/**
 * Where a step stands: `done` holds a choice, `next` asks for one (one step at most), `open` may take one and does well
 * without, and `waiting` cannot be chosen until a step before it is.
 */
export type StepState = "done" | "next" | "open" | "waiting";

/** The clean steps written as their name alone, as the documentation names them. */
export const PLAIN_STEPS = ["trim", "collapseSpaces", "upper", "lower", "nfc", "nfkc", "foldSeparators"] as const;

/** What each clean step does, as the story says it once it is picked. */
const STEP_WORDS: Record<string, string> = {
  trim: "trimmed",
  collapseSpaces: "spaces collapsed",
  upper: "upper case",
  lower: "lower case",
  nfc: "composed (NFC)",
  nfkc: "compatibility form (NFKC)",
  foldSeparators: "separators folded",
};

/** What each clean step does, in a few words, as the documentation says it. */
export const STEP_HELP: Record<string, string> = {
  trim: "White space at both ends removed.",
  collapseSpaces: "Every run of white space made one space.",
  upper: "Upper case.",
  lower: "Lower case.",
  nfc: "Unicode's composed form.",
  nfkc: "Unicode's compatibility form.",
  foldSeparators: "Letters and digits kept, every run of anything else one hyphen, lower case.",
};

/** What a story reads of a draft: the parts of the API's draft it is told by. */
export interface StoryDraft {
  kind: string;
  path: string | null;
  label: readonly string[];
  attributes: readonly { name: string; steps: readonly string[] | null; collect: string | null }[];
  clean: readonly { step: string; pattern: string | null; with: string | null }[];
}

/** Whether a draft has its key: a path whose distinct values are its rows. */
export function hasKey(draft: StoryDraft): boolean {
  return draft.path !== null && draft.path.trim() !== "";
}

/**
 * Where each step of a draft stands. The kind comes first and the key after it; every other step waits for the key. Once
 * there is one, the value is asked for next when the key names records (a key naming records is most often shown by a
 * name its record holds), and is open otherwise, as the attributes and the cleaning are; the table is done with the key.
 */
export function stepStates(draft: StoryDraft, keyNamesRecords: boolean): Record<StoryStep, StepState> {
  if (draft.kind.trim() === "") {
    return { from: "next", rows: "waiting", shown: "waiting", with: "waiting", cleaned: "waiting", makes: "waiting" };
  }

  if (!hasKey(draft)) {
    return { from: "done", rows: "next", shown: "waiting", with: "waiting", cleaned: "waiting", makes: "waiting" };
  }

  return {
    from: "done",
    rows: "done",
    shown: draft.label.length > 0 ? "done" : keyNamesRecords ? "next" : "open",
    with: draft.attributes.length > 0 ? "done" : "open",
    cleaned: draft.clean.length > 0 ? "done" : "open",
    makes: "done",
  };
}

/** The step that asks for a pick next; null once the draft holds what a dimension needs. */
export function nextStep(states: Record<StoryStep, StepState>): StoryStep | null {
  return STORY_STEPS.find((step) => states[step] === "next") ?? null;
}

/**
 * The step a finding of the builder is about, by the part of the dimension it names (as the YAML's spans name them):
 * `name` for the dimension's name and description, which head the story; null for a finding about the whole, or a part
 * no step shows.
 */
export function stepOfTarget(target: string | null): StoryStep | "name" | null {
  if (target === null) {
    return null;
  }

  const part = target.split(/[.[]/)[0];
  switch (part) {
    case "name":
    case "description":
      return "name";
    case "kind":
    case "query":
      return "from";
    case "path":
    case "maxValues":
    case "countRecords":
      return "rows";
    case "label":
    case "unlabelled":
      return "shown";
    case "attributes":
      return "with";
    case "clean":
      return "cleaned";
    case "columns":
      return "makes";
    default:
      return null;
  }
}

/** The attribute a finding is about (`Country` of `attributes.Country`); null for a finding about anything else. */
export function attributeOfTarget(target: string | null): string | null {
  return target !== null && target.startsWith("attributes.") ? target.slice("attributes.".length) : null;
}

/** A clean step as the story says it: its effect, a replacement by what it replaces. */
export function cleanWords(step: { step: string; pattern: string | null; with: string | null }): string {
  if (step.step === "replace") {
    const pattern = step.pattern ?? "";
    return pattern === "" ? "a replacement" : `/${pattern}/ replaced`;
  }

  return STEP_WORDS[step.step] ?? step.step;
}

/** A path without its filters: the property names it reads through. */
export function plainPath(path: string): string {
  return path.replace(/\[[^\]]*\]/g, "");
}

/** The property a path ends with, its filters set aside. */
export function lastProperty(path: string): string {
  const names = plainPath(path).split(".");
  return names[names.length - 1] ?? path;
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

/** The type of a kind's records, as the story names them: `WellLog` of `osdu:wks:work-product-component--WellLog:1.4.0`. */
export function kindTypeName(kind: string): string {
  const segments = kind.split(":");
  return segments.length === 4 ? typeName(segments[2]) : kind;
}

/**
 * The entity type a key's values name: the one the kind's template says the key's property names (`names`, the template's
 * suggestion for the path), else the one the example key is an id of; null for a key that names no record.
 */
export function keyEntityType(names: readonly string[], exampleKey: string | null): string | null {
  const named = names.find((name) => name.includes("--"));
  if (named !== undefined) {
    return named;
  }

  return exampleKey !== null && isRecordReference(exampleKey) ? entityTypeOf(exampleKey) : null;
}

/** The records a chain of steps reads through, as the story says it: the property each step ends with, joined by arrows. */
export function chainWords(steps: readonly string[]): string {
  return steps.map(lastProperty).join(" › ");
}

/** "1 of 3": an example's place among those stepped through; "none" when there are none. */
export function exampleCount(at: number, count: number): string {
  return count === 0 ? "none" : `${Math.min(at, count - 1) + 1} of ${count}`;
}

const isObject = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === "object" && !Array.isArray(value);

/** The text a value is compared as: a text, a number or a boolean as written; null for anything else. */
function comparable(value: unknown): string | null {
  if (typeof value === "string") {
    return value;
  }

  return typeof value === "number" || typeof value === "boolean" ? String(value) : null;
}

/**
 * Where a record holds the first value at a key's path (`data.GeoContexts.GeoPoliticalEntityID`), its lists stepped into:
 * the path the record views name it by (`data.GeoContexts[0].GeoPoliticalEntityID`), so the record it names can be opened
 * from it. With `wanted`, the first value that is it; without, the first that is not blank. Null where the record holds none.
 */
export function firstLocationAt(record: unknown, path: string, wanted: string | null): string | null {
  const names = path.split(".").filter((name) => name !== "");
  if (names.length === 0) {
    return null;
  }

  const walk = (node: unknown, at: number, written: string): string | null => {
    if (Array.isArray(node)) {
      for (let index = 0; index < node.length; index++) {
        const found = walk(node[index], at, `${written}[${index}]`);
        if (found !== null) {
          return found;
        }
      }

      return null;
    }

    if (at === names.length) {
      const value = comparable(node);
      const held = value !== null && value.trim() !== "";
      return held && (wanted === null || value === wanted) ? written : null;
    }

    if (!isObject(node)) {
      return null;
    }

    const name = names[at];
    return walk(node[name], at + 1, written === "" ? name : `${written}.${name}`);
  };

  return walk(record, 0, "");
}
