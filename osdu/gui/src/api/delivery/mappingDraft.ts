// The mapping builder's draft of a mapping document, as the delivery API takes and answers it: its parameters, row
// rules, searches, lookups and entries, and what each entry finds, modifies and asserts.

import type { AssertionAction, AssertionStage } from "../validation";

/**
 * Where a draft entry's value comes from: a dataset column, a child dataset's rows, a cached record, a fixed value, the
 * id of a record found by searching the platform, a value an expression computes from the row, the first of several
 * alternatives that gives a value ($coalesce), a field of the record one of the mapping's lookups finds ($lookup), a list
 * (of values whose items are fixed values and value nodes, or of objects whose items are groups and fixed objects), a
 * group: an item of a list of objects, the object its properties give, a section of the record's parent ($parent), for a
 * template linked to a parent, or the link to one of the record's children ($child), for a template other templates are
 * linked to.
 */
export type MappingDraftInput = "Dataset" | "Repeat" | "Cache" | "Static" | "Search" | "Expression" | "Coalesce" | "Lookup" | "List" | "Group" | "Parent" | "Child";

export type MappingDraftModifierKind = "trim" | "upper" | "lower" | "split" | "replace" | "equals" | "date" | "number" | "id" | "ref";

/** A parameter the mapping declares, which the flow supplies under render.parameters. */
export interface MappingDraftParameter {
  name: string;
  required: boolean;
  default: string | null;
  description: string | null;
}

/**
 * One findBy line: the cached field, and the dataset column (without `dataset.`) or the fixed text it must equal. A line of
 * a cache entry or a lookup may compare more fields in `and`, which a record must meet together with this one.
 */
export interface MappingDraftFind {
  field: string;
  column: string | null;
  literal: string | null;
  /** The comparisons after the first on the line, each of one field; null or absent for a line of one. */
  and?: MappingDraftFind[] | null;
}

/** One pair of a replace: the incoming value, and what it becomes; null is no value, so the entry's required flag decides. */
export interface MappingDraftReplacement {
  from: string;
  to: string | null;
}

/** What a replace does with a value its pairs do not list: keep it (the default), give no value, or give a fixed text. */
export type MappingDraftOtherwiseKind = "keep" | "empty" | "text";

/**
 * One modifier with its settings: split takes a separator and a part, replace its pairs (or the cached table it reads them
 * from) and what an unlisted value becomes, equals its text, date an optional format in text, number its decimal and
 * group separators, and id the template the id is built from, in text.
 */
export interface MappingDraftModifier {
  kind: MappingDraftModifierKind;
  separator: string | null;
  part: number | null;
  replacements: MappingDraftReplacement[] | null;
  text: string | null;
  /** number: the separator before the decimals, "." or ",". */
  decimalSeparator: string | null;
  /** number: the separator between groups of three digits, or null when the value is written without one. */
  groupSeparator: string | null;
  /** replace: what a value the pairs do not list becomes; null on every other modifier. */
  otherwiseKind?: MappingDraftOtherwiseKind | null;
  /** replace: the text an unlisted value becomes when otherwiseKind is text. */
  otherwiseText?: string | null;
  /** replace: the cached type the table is read from (replace: $cache.<table>), instead of pairs; null for pairs. */
  table?: string | null;
  /** replace from the cache: the field a value is matched on; null for the table's key. */
  match?: string | null;
  /** replace from the cache: the field that replaces a value; null for the one field a lookup table holds beside its key. */
  field?: string | null;
}

/** One entry as the builder edits it. */
export interface MappingDraftEntry {
  target: string;
  input: MappingDraftInput;
  /** Dataset: `column`, or `child.column` for an entry inside a repeater. */
  column: string | null;
  /** Repeat: the child dataset whose rows become the items. */
  child: string | null;
  /** Cache: the cached type, such as UnitOfMeasure. Search: the search, by the name the mapping declares it under. */
  cacheType: string | null;
  /** Cache and Lookup: the field of the record to read, usually id. Search: always id, the one thing a search returns. */
  cacheField: string | null;
  /** Lookup: the lookup read, by the name the mapping declares it under. */
  lookup: string | null;
  /** Parent: the path of the section of the record's parent it takes, from the record's root, such as data.DDMSDatasets. */
  parentPath?: string | null;
  /** Child: the kind of the child it links, a custom template linked to this one, such as welldb:wks:work-product-component--WellLogDetail:1.0.0. */
  childKind?: string | null;
  findBy: MappingDraftFind[];
  /** Cache: every matching row read ($findAll) in place of findBy lines; null for an entry that finds one record. */
  findAll: MappingDraftFindAll | null;
  modifiers: MappingDraftModifier[];
  /** Expression: the expression the value is computed with, as the record tree writes it in the entry's scope. */
  expression: string | null;
  /** The condition (`$when`) that decides whether the property is written for a row, such as `status = "FINAL"`; null always writes it. */
  when: string | null;
  /** Repeat: the condition (`$where`) a child row must hold to become an item; null keeps every row. */
  where: string | null;
  required: boolean;
  /**
   * `$restrictWhenMissing`: a value the entry should give and does not restricts the record (it goes out under the mapping's
   * `restrictedAccess`, the property left out) rather than holding it. Beside a required entry only. Absent from an older
   * control plane, which reads as false.
   */
  restrictWhenMissing?: boolean;
  ignoreSeparators: boolean;
  /**
   * An entry that builds an id with id or ref ($unverified): the id is written even when the cache holds records of its
   * entity type and not this one, and recorded as an unverified reference.
   */
  unverified: boolean;
  /**
   * Coalesce: the alternatives in the order they are tried, each an entry of its own input; their target, condition,
   * required flag and description are this entry's.
   */
  alternatives: MappingDraftEntry[];
  /**
   * List: the items in the order they are written, each an entry of its own input. A fixed item of a list of values holds
   * one value, with its own condition, required flag and description; an item of a list of objects is a group or a fixed
   * object. Their target is this entry's, which itself takes no condition.
   */
  items: MappingDraftEntry[];
  /**
   * Group, an item of a list of objects: its properties in the order they are written, each an entry of its own input
   * whose target is the variable inside the list's items it fills (`osdu.data.TechnicalAssurances[].TechnicalAssuranceTypeID`).
   * The group itself takes no condition, required flag or description; its properties do.
   */
  properties: MappingDraftEntry[];
  /** Static: the value as JSON text, such as "[\"a\"]", "\"MD\"", "5" or "true". */
  static: string | null;
  description: string | null;
  /**
   * The entry's assertions ($assert, osdu/docs/reference/flow/mapping-assertions.md), in the order they are written. A
   * coalesce entry's are the entry's, never an alternative's; a fixed value, a list and an item of a list of values take none.
   */
  assertions?: MappingDraftAssertion[];
  /** True when the builder proposed the entry from the cache, until someone edits it. */
  prefilled: boolean;
}

/**
 * One assertion of an entry as the builder edits it: the condition's operator and its operand as the document writes it
 * after the operator (`[0, 250]`, `'GR'`, `{ atLeast: 1 }`), the value it judges, what a record that fails it does, how it
 * reads several values, the conditions it is judged under, its name and its description.
 */
export interface MappingDraftAssertion {
  operator: string;
  /** One line of YAML, as it follows the operator. */
  operand: string;
  stage: AssertionStage;
  onFail: AssertionAction;
  /** For a property holding several values: true when one of them has to meet it, false when every one does. */
  anyValue: boolean;
  ignoreCase: boolean;
  tolerance: number | null;
  where: MappingDraftAssertionFilter[];
  name: string | null;
  description: string | null;
}

/**
 * One condition an assertion is judged under: what it reads (`field`, a path into the record, for the record stage;
 * `column`, a column as the draft names one, for the incoming stage), its operator and its operand.
 */
/** One rule on the rows a mapping renders from (`dataset.assert`), as the draft holds it. */
export interface MappingDraftRowRule {
  kind: "value" | "count" | "unique";
  per: "record" | "run";
  /** The child dataset whose rows it reads; null for the record's own row. */
  rows: string | null;
  column: string | null;
  operator: string;
  operand: string;
  /** For a count rule: the comparison as `count:` writes it (`{ atLeast: 2 }`, or a number). */
  count: string | null;
  unique: string[];
  anyRow: boolean;
  ignoreCase: boolean;
  tolerance: number | null;
  onFail: string;
  where: MappingDraftAssertionFilter[];
  name: string | null;
  description: string | null;
}

export interface MappingDraftAssertionFilter {
  reads: "field" | "column";
  path: string;
  operator: string;
  operand: string;
  ignoreCase: boolean;
  tolerance: number | null;
}

/**
 * A cache entry's $findAll: the cached field the rows are found by, compared with exactly one of a dataset column, a fixed
 * text, or a field of a lookup's record written with the lookup's name (`wellbore.GeoContexts.FieldID`); and the fields a
 * row must hold nothing under; and the lines comparing two fields of the same row (`GroupName contains CountryName`).
 */
export interface MappingDraftFindAll {
  field: string;
  column: string | null;
  literal: string | null;
  lookup: string | null;
  empty: string[];
  contains: MappingDraftFindAllContains[];
}

/**
 * A $findAll line a row meets when its text under `field` holds its text under `part`, both read by their letters and
 * digits alone, ignoring case.
 */
export interface MappingDraftFindAllContains {
  field: string;
  part: string;
}

/**
 * A record the mapping finds once for a row and reads wherever the record needs it (lookups.<name>): the cached type it is
 * found in, the findBy lines comparing columns of the dataset's own row, and the modifiers and separator fold a cache
 * entry takes.
 */
export interface MappingDraftLookup {
  name: string;
  cacheType: string;
  findBy: MappingDraftFind[];
  modifiers: MappingDraftModifier[];
  ignoreSeparators: boolean;
  description: string | null;
}

/**
 * One record set the mapping searches the platform for as a record needs one, rather than capturing it into the cache:
 * the name entries read it by, the kind it looks in, and the template whose schema says how that kind is indexed.
 */
export interface MappingDraftSearch {
  name: string;
  kind: string;
  schemaKind: string;
  schemaVersion: string;
  description: string | null;
}

/** What a mapping makes its records' OSDU ids from: the delivery key (a GUID), or the key's own values (`WELLDB::GAPI`). */
export type MappingIdFrom = "deliveryKey" | "key";

/** A mapping as the builder edits it: the header, the parameters and the entries. */
export interface MappingDraft {
  name: string;
  version: string;
  templateKind: string;
  templateVersion: string;
  description: string | null;
  system: string;
  /** The dataset columns of the key, without `dataset.`. */
  key: string[];
  /** The label as written, with {column} tokens naming columns of the dataset's own row. */
  label: string | null;
  /** The dataset columns an operator finds a record by, without `dataset.`. */
  identity: string[];
  /** What the OSDU id is made from, as `dataset.idFrom` writes it: `key` for the key's own values; `deliveryKey` or absent for the delivery key. */
  idFrom?: MappingIdFrom | null;
  /** The rules on the rows the mapping renders from (`dataset.assert`), in the order the document writes them. */
  rules?: MappingDraftRowRule[];
  parameters: MappingDraftParameter[];
  /** The record sets the mapping's search entries look in. */
  searches: MappingDraftSearch[];
  /** The records the mapping finds once for a row, which lookup entries and find all lines read. */
  lookups: MappingDraftLookup[];
  /**
   * The access restriction a record goes out under while a value an entry marks `$restrictWhenMissing` is missing
   * (`restrictedAccess`, osdu/docs/reference/flow/mapping-restricted-access.md): its registered name, or text reading a
   * parameter such as `{$param.restriction}`; null or absent for none.
   */
  restrictedAccess?: string | null;
  entries: MappingDraftEntry[];
}
