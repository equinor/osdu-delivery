// Which inputs the mapping builder offers an entry (osdu/docs/reference/flow/mapping.md): what its variable takes, narrowed
// by where the entry sits (an alternative of a coalesce, an item of a list, a property of an item, an item a child dataset's
// rows fill) and by what the mapping and its template declare. The entry editor draws the choice; this decides it, and the
// unit tier tests it without rendering anything.

/** An input of a mapping entry, as the API names it (MappingDraftInput). */
export type EntryInput =
  | "Dataset" | "Repeat" | "Cache" | "Static" | "Search" | "Expression" | "Coalesce" | "Lookup" | "List" | "Group" | "Parent" | "Child";

/** A template variable as the choice reads it (DeliveryTemplateVariable): its path and its shape. */
export interface EntryVariable {
  path: string;
  shape: "Value" | "ValueList" | "Group" | "GroupList" | "Whole";
}

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

/** The inputs one item of a list of values may read: one value each, or with a find all many. */
const ITEM_INPUTS: readonly EntryInput[] = ["Static", "Dataset", "Expression", "Cache", "Lookup", "Search", "Child"];

/** The inputs one item of a list of objects may be: an object whose properties are each filled on their own, or a fixed object. */
const OBJECT_ITEM_INPUTS: readonly EntryInput[] = ["Group", "Static"];

/** The inputs a property of the items a child dataset's rows fill may read: the child's rows, never the record's relatives. */
const OUTSIDE_INPUTS: readonly EntryInput[] = ["Dataset", "Expression", "Repeat", "Cache", "Lookup", "Search", "Static"];

/**
 * The inputs a variable takes, as the preflight allows them: a value or a list of values from a dataset column, the cache,
 * a lookup, a search of the platform or a static value, and a list of values from items that are each one of those; a
 * list of objects from a repeater, a static list, or a list whose items are objects of properties each filled on its
 * own; an object only from a static value. A section of the record's parent fills any shape, for a template linked to a
 * parent, and the link to one of the record's children a value, for a template other templates are linked to (the editor
 * offers each only then, as {@link inputsOffered} says). The legal lists are static, and the access lists static or a list
 * of values that keeps a fixed value every record carries.
 */
export function inputsFor(variable: EntryVariable): EntryInput[] {
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

/** Where an entry sits, which narrows what its variable takes. */
export interface EntryPlace {
  /** The part of another entry it is: an alternative of a coalesce, an item of a list, a property of an item; null for an entry of its own. */
  nested: "alternative" | "item" | "property" | null;
  /** Whether it fills a property of the items a child dataset's rows fill. */
  outside: boolean;
  /** Whether a repeater fills the list its variable belongs to, so its rows are the repeater's. */
  inRepeater: boolean;
}

/** What the mapping and its template declare that an input reads. */
export interface EntryDeclared {
  /** Searches the mapping declares to look in. */
  searches: number;
  /** Lookups the mapping declares to read. */
  lookups: number;
  /** Whether the template is linked to a parent record. */
  parent: boolean;
  /** Whether other templates are linked to this one, so a record has children to link to. */
  children: boolean;
}

/**
 * The inputs the editor offers an entry: what its variable takes ({@link inputsFor}), narrowed by where it sits and by what
 * the mapping declares, with the input it has now kept even where it is no longer offered, so opening an entry never
 * drops what it is. A search is offered once the mapping declares one to look in and a lookup once it declares one to
 * read; the parent record once the template is linked to a parent and a child record once a template is linked to this
 * one, neither inside a repeater's items, and a child link never as an alternative, since nothing stands in for it.
 * A part of another entry reads values of its own, so it is neither a repeat, a coalesce nor a list, except a property,
 * which is filled as its variable is anywhere but repeats no rows and writes a list of objects whole.
 */
export function inputsOffered(variable: EntryVariable, place: EntryPlace, declared: EntryDeclared, current: EntryInput | null): EntryInput[] {
  const isItem = place.nested === "item";
  const isProperty = place.nested === "property";
  const offered: readonly EntryInput[] = place.outside
    ? OUTSIDE_INPUTS
    : isItem && variable.shape === "GroupList"
      ? OBJECT_ITEM_INPUTS
      : isItem
        ? ITEM_INPUTS
        : isProperty
          ? inputsFor(variable).filter((input) => input !== "Repeat" && !(input === "List" && variable.shape === "GroupList"))
          : inputsFor(variable);
  const allowed = offered.filter((input) => (input !== "Search" || declared.searches > 0)
    && (input !== "Lookup" || declared.lookups > 0)
    && (input !== "Parent" || (declared.parent && !place.inRepeater && !isItem))
    && (input !== "Child" || (declared.children && !place.inRepeater && place.nested !== "alternative"))
    && (place.nested === null || isProperty || (input !== "Repeat" && input !== "Coalesce" && input !== "List")));
  if (current !== null && !allowed.includes(current)) {
    allowed.push(current);
  }

  return allowed;
}
