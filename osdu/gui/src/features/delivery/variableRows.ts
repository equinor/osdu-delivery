import type {
  DeliveryTemplateDetail, DeliveryTemplateVariable, MappingDraft, MappingDraftEntry,
} from "../../api/delivery";
import { isWithin, parsePath } from "./recordPath";

/** One row of the variables list: a variable a mapping can fill, a key under an object with free keys, or an entry the template does not let a mapping fill. */
export interface VariableRow {
  key: string;
  variable: DeliveryTemplateVariable;
  entry: MappingDraftEntry | null;
  kind: "variable" | "key" | "outside";
  /** Why no mapping may fill the row's target, for an outside row. */
  outside: string | null;
}

/** The variable a key under an object with free keys is, the way the template resolves it. */
export function keyVariable(holder: DeliveryTemplateVariable, target: string): DeliveryTemplateVariable {
  const type = holder.keyValueType ?? "string";
  return {
    path: target,
    shape: type === "object" || type === "array" || type === "any" ? "Whole" : "Value",
    type,
    itemType: null,
    format: null,
    required: false,
    role: "Mapping",
    relationships: [],
    pattern: null,
    unitContext: null,
    title: null,
    description: holder.description,
    keyValueType: null,
    nested: false,
    cacheTypes: [],
    open: false,
  };
}

/** The variable a path inside an open object is: it takes a value, an object or a list as written. */
function insideVariable(holder: DeliveryTemplateVariable, target: string): DeliveryTemplateVariable {
  return { ...keyVariable(holder, target), shape: "Whole", type: "any" };
}

/**
 * The template's variables with one for every entry that fills a free key of an object that takes them
 * (`osdu.tags.DeliveredBy`), each right after the object holding it, so a tree still reads parents before children.
 * Without them an entry under `tags` would be filled by the mapping and shown nowhere.
 */
export function withKeyVariables(
  variables: DeliveryTemplateVariable[],
  entries: readonly MappingDraftEntry[],
): DeliveryTemplateVariable[] {
  const named = new Set(variables.map((variable) => variable.path));
  const listed: DeliveryTemplateVariable[] = [];
  for (const variable of variables) {
    listed.push(variable);
    for (const entry of entries) {
      const held = named.has(entry.target) ? null : heldVariable(variable, entry.target);
      if (held !== null) {
        named.add(entry.target);
        listed.push(held);
      }
    }
  }

  return listed;
}

/** The variable `target` is under `holder`: a free key of an object that takes them, or any path inside an open object. Null for neither. */
function heldVariable(holder: DeliveryTemplateVariable, target: string): DeliveryTemplateVariable | null {
  if (target === holder.path || !isWithin(target, holder.path) || target.startsWith("[]", holder.path.length)) {
    return null;
  }

  if (holder.open) {
    return insideVariable(holder, target);
  }

  // A free key is one name below the object, whatever it holds: osdu.tags.Source, osdu.tags['lra.dataID'].
  const inner = parsePath(target).steps;
  const outer = parsePath(holder.path).steps;
  const oneName = inner !== null && outer !== null && inner.length === outer.length + 1 && inner[inner.length - 1].kind === "property";
  return holder.keyValueType !== null && oneName ? keyVariable(holder, target) : null;
}

function unknownVariable(target: string): DeliveryTemplateVariable {
  return {
    path: target,
    shape: "Value",
    type: "any",
    itemType: null,
    format: null,
    required: false,
    role: "Mapping",
    relationships: [],
    pattern: null,
    unitContext: null,
    title: null,
    description: null,
    keyValueType: null,
    nested: false,
    cacheTypes: [],
    open: false,
  };
}

/**
 * The rows of the variables list, in template order: every variable a mapping can fill with its entry, each key entry
 * under the object with free keys it belongs to, and at the end every entry whose target the template does not let a
 * mapping fill, so nothing in the draft is out of sight.
 */
export function variableRows(detail: DeliveryTemplateDetail, draft: MappingDraft): VariableRow[] {
  const byTarget = new Map(draft.entries.map((entry) => [entry.target, entry]));
  const byPath = new Map(detail.variables.map((variable) => [variable.path, variable]));
  const shown = new Set<string>();
  const rows: VariableRow[] = [];

  for (const variable of detail.variables) {
    if (variable.role !== "Mapping" || variable.nested) {
      continue;
    }

    const entry = byTarget.get(variable.path) ?? null;
    shown.add(variable.path);
    rows.push({ key: variable.path, variable, entry, kind: "variable", outside: null });
    for (const candidate of draft.entries) {
      // A property the template names is its own row, even under an object that also takes free keys.
      const held = shown.has(candidate.target) || byPath.has(candidate.target) ? null : heldVariable(variable, candidate.target);
      if (held !== null) {
        shown.add(candidate.target);
        rows.push({ key: candidate.target, variable: held, entry: candidate, kind: "key", outside: null });
      }
    }
  }

  for (const entry of draft.entries) {
    if (shown.has(entry.target)) {
      continue;
    }

    shown.add(entry.target);
    const known = byPath.get(entry.target);
    let outside: string;
    if (known === undefined) {
      outside = `Template ${detail.kind} version ${detail.version} has no variable ${entry.target}.`;
    } else if (known.role === "Engine") {
      outside = `${entry.target} is written by OSDU Delivery, not by a mapping.`;
    } else if (known.role === "Osdu") {
      outside = `${entry.target} is set by OSDU when the record is stored, not by a mapping.`;
    } else {
      outside = `${entry.target} is a list inside a repeated item, which a mapping cannot fill.`;
    }

    rows.push({ key: entry.target, variable: known ?? unknownVariable(entry.target), entry, kind: "outside", outside });
  }

  return rows;
}
