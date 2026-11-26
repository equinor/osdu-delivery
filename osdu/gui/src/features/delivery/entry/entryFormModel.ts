// What the mapping entry editor's form reasons with apart from React: the inputs it offers and how it names them, the static
// value as each of its editors holds it, how a nested form names the part of an entry it edits, and the names its pickers
// offer from the cache and the mapping's lookups.

import type { DeliveryCachedType, DeliveryTemplateVariable, MappingDraftEntry, MappingDraftInput, MappingDraftLookup } from "../../../api/delivery";
import type { ChoiceOption } from "../MappingEditorParts";
import { parseJson, type StaticMode } from "../mappingDraft";

export type Choice = "None" | MappingDraftInput;

export const CHOICE_LABELS: Record<Choice, string> = {
  None: "Not filled",
  Dataset: "Dataset column",
  Repeat: "Repeat child rows",
  Cache: "Cache",
  Lookup: "Lookup",
  Static: "Static value",
  Search: "Platform search",
  Expression: "Expression",
  Coalesce: "First value of",
  List: "List of values",
  Group: "Object",
  Parent: "Parent record",
  Child: "Child record",
};

/** How the input choice names an input for a variable: a list filling a list of objects is a list of objects. */
export function choiceLabel(input: Choice, variable: Pick<DeliveryTemplateVariable, "shape">): string {
  return input === "List" && variable.shape === "GroupList" ? "List of objects" : CHOICE_LABELS[input];
}

/** What a find all compares its field with: a dataset column, a fixed text, or a field of a lookup's record. */
export type FindAllMode = "column" | "text" | "lookup";

/** The static value in every editor at once, so switching between them keeps what was typed. */
export interface StaticState {
  list: string[];
  text: string;
  number: string;
  boolean: "true" | "false";
  json: string;
}

function defaultJson(variable: DeliveryTemplateVariable): string {
  if (variable.shape === "GroupList" || variable.shape === "ValueList" || variable.type === "array") {
    return "[]";
  }

  return variable.shape === "Group" || variable.shape === "Whole" ? "{}" : "\"\"";
}

export function seedStatic(variable: DeliveryTemplateVariable, json: string | null): StaticState {
  const parsed = parseJson(json);
  const value = parsed.ok ? parsed.value : undefined;
  return {
    list: Array.isArray(value) ? value.map((item: unknown) => (item === null || item === undefined ? "" : String(item))) : [],
    text: typeof value === "string" ? value : "",
    number: typeof value === "number" ? String(value) : "",
    boolean: value === true ? "true" : "false",
    json: json !== null && json.trim() !== "" ? json : defaultJson(variable),
  };
}

/** The static value as JSON text from the editor in use, or why it cannot be; an empty number is no value yet. */
export function staticJsonOf(mode: StaticMode, state: StaticState, variable: DeliveryTemplateVariable): { json: string | null; error: string | null } {
  switch (mode) {
    case "list":
      return { json: JSON.stringify(state.list.map((item) => item.trim()).filter((item) => item !== "")), error: null };
    case "text":
      return { json: JSON.stringify(state.text), error: null };
    case "number": {
      const text = state.number.trim();
      if (text === "") {
        return { json: null, error: null };
      }

      const number = Number(text);
      if (!Number.isFinite(number)) {
        return { json: null, error: `'${text}' is not a number.` };
      }

      if (variable.type === "integer" && !Number.isInteger(number)) {
        return { json: null, error: `'${text}' is not a whole number, and the template takes an integer.` };
      }

      return { json: JSON.stringify(number), error: null };
    }
    case "boolean":
      return { json: state.boolean, error: null };
    case "json": {
      const parsed = parseJson(state.json);
      if (!parsed.ok) {
        return { json: null, error: `The static value is not valid JSON: ${parsed.error}` };
      }

      return { json: parsed.value === undefined ? null : state.json.trim(), error: null };
    }
  }
}

/**
 * How a nested form edits one part of an entry: an alternative of a coalesce, an item of a list, or a property of an item
 * of a list of objects.
 */
export interface NestedRole {
  kind: "alternative" | "item" | "property";
  /** How the form's title names the part: "alternative 2", "item 3". */
  label: string;
}

/**
 * The part the nested editor edits: which list, its place there (the end for a new one), for a property the variable
 * inside the list's items it fills, and a number per opening.
 */
export interface EditedPart {
  list: "alternatives" | "items" | "properties";
  index: number;
  entry: MappingDraftEntry | null;
  variable: DeliveryTemplateVariable | null;
  session: number;
}

/** The fields the cache captures of the cached type named, none for a type it does not hold. */
export function typeFieldsOf(repoTypes: readonly DeliveryCachedType[], cacheType: string): string[] {
  return repoTypes.find((type) => type.name === cacheType.trim())?.fields ?? [];
}

/** The fields of the cached type named offered to compare or read, its id among them. */
export function fieldOptionsOf(repoTypes: readonly DeliveryCachedType[], cacheType: string): ChoiceOption[] {
  const typeFields = typeFieldsOf(repoTypes, cacheType);
  return [...new Set([...typeFields, "id"])].map((field) => ({ value: field, group: "Cached fields" }));
}

/** The lookups the mapping declares, offered by name. */
export function lookupOptionsOf(lookups: readonly MappingDraftLookup[]): ChoiceOption[] {
  return lookups.map((lookup) => ({ value: lookup.name, hint: lookup.cacheType, group: "The mapping's lookups" }));
}

/** The fields of the record a lookup finds: those its cached type captures, and its id. */
export function lookupFieldsOf(lookups: readonly MappingDraftLookup[], repoTypes: readonly DeliveryCachedType[], name: string): ChoiceOption[] {
  const type = lookups.find((lookup) => lookup.name === name.trim())?.cacheType ?? "";
  const fields = repoTypes.find((candidate) => candidate.name === type)?.fields ?? [];
  return [...new Set(["id", ...fields])].map((field) => ({ value: field, group: `Fields of ${type === "" ? "its record" : type}` }));
}
