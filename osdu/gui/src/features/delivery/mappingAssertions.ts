// The mapping builder's rules for an entry's assertions ($assert, osdu/docs/reference/flow/mapping-assertions.md) that need
// no server: which entries take them, which value they may judge, what a failure may do, the operators and what each
// compares with, and what an entry's settings leave its assertions unable to say. The server's check stays the authority:
// these rules only keep the editor from offering what it would refuse.

import type { MappingDraftAssertion, MappingDraftAssertionFilter, MappingDraftEntry, MappingDraftInput } from "../../api/delivery";
import type { AssertionStage } from "../../api/validation";

/** The most assertions one entry states. */
export const MAX_ENTRY_ASSERTIONS = 20;

/** The most conditions one assertion is judged under. */
export const MAX_ASSERTION_CONDITIONS = 10;

/** The longest name an assertion takes. */
export const MAX_ASSERTION_NAME = 200;

/** The operators of an assertion of a mapping, in the order the assertion flows list them; `resolves` is not one. */
export const ASSERTION_OPERATORS: readonly string[] = [
  "equals", "notEquals", "in", "notIn", "atLeast", "atMost", "greaterThan", "lessThan", "between", "matches", "notMatches",
  "startsWith", "endsWith", "contains", "notContains", "exists", "empty", "type", "length",
];

/** What a value meets each operator by, in a few words, as the operator picker says it. */
export const OPERATOR_MEANINGS: Readonly<Record<string, string>> = {
  equals: "equals the value",
  notEquals: "does not equal it",
  in: "is one of the values listed",
  notIn: "is none of them",
  atLeast: "is that or more",
  atMost: "is that or less",
  greaterThan: "is more than that",
  lessThan: "is less than that",
  between: "lies between two values, both included",
  matches: "its text matches a regular expression",
  notMatches: "its text does not match it",
  startsWith: "its text starts with it",
  endsWith: "its text ends with it",
  contains: "a list holding it, or text containing it",
  notContains: "holds no such value",
  exists: "has a value at all, or has none",
  empty: "is empty (no value, empty text, list or object), or not",
  type: "is of a JSON type",
  length: "its text length or item count compares",
};

/** What each operator compares with, written as the document writes it after the operator. */
const OPERAND_EXAMPLES: Readonly<Record<string, string>> = {
  equals: "'GR'",
  notEquals: "'NONE'",
  in: "[GR, SP]",
  notIn: "[-999, -999.25]",
  atLeast: "0",
  atMost: "250",
  greaterThan: "0",
  lessThan: "10000",
  between: "[0, 250]",
  matches: "'^[A-Z0-9_]{1,16}$'",
  notMatches: "'\\s'",
  startsWith: "'dev:'",
  endsWith: "':'",
  contains: "'GR'",
  notContains: "'NONE'",
  exists: "true",
  empty: "false",
  type: "string",
  length: "{ atLeast: 1, atMost: 16 }",
};

/** The operators whose operand is true or false. */
export const FLAG_OPERATORS: ReadonlySet<string> = new Set(["exists", "empty"]);

/** The JSON types a `type` condition names. */
export const JSON_TYPES: readonly string[] = ["string", "number", "integer", "boolean", "object", "array", "null"];

/** The operators that compare text and so may ignore case. */
const IGNORE_CASE_OPERATORS: ReadonlySet<string> = new Set([
  "equals", "notEquals", "in", "notIn", "matches", "notMatches", "startsWith", "endsWith", "contains", "notContains",
]);

/** The operators that compare numbers and so may take a tolerance. */
const TOLERANCE_OPERATORS: ReadonlySet<string> = new Set([
  "equals", "notEquals", "in", "notIn", "atLeast", "atMost", "greaterThan", "lessThan", "between",
]);

/** Whether `ignoreCase` means something for an operator. */
export function ignoresCase(operator: string): boolean {
  return IGNORE_CASE_OPERATORS.has(operator.trim());
}

/** Whether `tolerance` means something for an operator. */
export function takesTolerance(operator: string): boolean {
  return TOLERANCE_OPERATORS.has(operator.trim());
}

/** An example of what an operator compares with, as the operand input's placeholder shows it. */
export function operandExample(operator: string): string {
  return OPERAND_EXAMPLES[operator.trim()] ?? "'GR'";
}

/**
 * The operand an operator starts from when it is picked in place of `previous`: true for exists and empty and string for
 * type, unless what was written already suits them; what was written for any other operator, which the new one may
 * compare with as well; and nothing after a flag or a type, which no other operator compares with.
 */
export function operandFor(operator: string, previous: string, written: string): string {
  const text = written.trim();
  if (FLAG_OPERATORS.has(operator)) {
    return text === "true" || text === "false" ? text : "true";
  }

  if (operator === "type") {
    return JSON_TYPES.includes(text) ? text : "string";
  }

  return FLAG_OPERATORS.has(previous) || previous === "type" ? "" : written;
}

/**
 * Why an operand cannot be what an operator compares with, as far as its shape shows, or null when it may be. Whether the
 * values suit the property is judged when the composed mapping is read and checked.
 */
export function operandProblem(operator: string, operand: string): string | null {
  const op = operator.trim();
  const text = operand.trim();
  if (!ASSERTION_OPERATORS.includes(op)) {
    return `'${op}' is not a condition; pick one of ${ASSERTION_OPERATORS.join(", ")}.`;
  }

  if (text === "") {
    return `Give ${op} what it compares with, such as ${operandExample(op)}.`;
  }

  const bracketed = text.startsWith("[") && text.endsWith("]");
  switch (op) {
    case "between":
      return bracketed && text.slice(1, -1).includes(",") ? null : "between lists the lower bound and the upper in brackets, such as [0, 250].";
    case "in":
    case "notIn":
      return bracketed && text.slice(1, -1).trim() !== "" ? null : `${op} lists the values in brackets, such as ${operandExample(op)}.`;
    case "exists":
    case "empty":
      return text === "true" || text === "false" ? null : `${op} is true or false.`;
    case "type":
      return JSON_TYPES.includes(text) ? null : `type names a JSON type: one of ${JSON_TYPES.join(", ")}.`;
    case "length":
      return /^\d+$/.test(text) || (text.startsWith("{") && text.endsWith("}"))
        ? null
        : "length compares with a whole number, or a comparison in braces such as { atLeast: 1, atMost: 16 }.";
    default:
      return bracketed || (text.startsWith("{") && text.endsWith("}"))
        ? `${op} compares with one value: text in quotes, a number, or true or false.`
        : null;
  }
}

/** A tolerance as a form holds it, read: null for none, or why the text is not one. */
export function toleranceOf(text: string): { value: number | null; error: string | null } {
  const trimmed = text.trim();
  if (trimmed === "") {
    return { value: null, error: null };
  }

  const value = Number(trimmed);
  return Number.isFinite(value) && value >= 0
    ? { value, error: null }
    : { value: null, error: `A tolerance is a number, zero or more; '${trimmed}' is not.` };
}

/** The assertions an entry states, none when the draft says nothing of them. */
export function assertionsOf(entry: Pick<MappingDraftEntry, "assertions"> | null | undefined): MappingDraftAssertion[] {
  return entry?.assertions ?? [];
}

/** A new assertion, with every setting at its default: judged on the record, holding it when it fails, every value. */
export function newAssertion(): MappingDraftAssertion {
  return {
    operator: "equals",
    operand: "",
    stage: "record",
    onFail: "hold",
    anyValue: false,
    ignoreCase: false,
    tolerance: null,
    where: [],
    name: null,
    description: null,
  };
}

/** What a condition of an assertion reads on a stage: a field of the record, or a column of the row. */
export function filterReads(stage: AssertionStage): MappingDraftAssertionFilter["reads"] {
  return stage === "incoming" ? "column" : "field";
}

/**
 * Which part of an entry a form edits: the entry, an alternative of a coalesce, an item of a list, or a property of an
 * item of a list of objects.
 */
export type AssertionPart = "entry" | "alternative" | "item" | "property";

/**
 * Whether a node takes assertions: one with a value of its own to judge, read from the row, the cache, a lookup, a search
 * or the first of several alternatives, or a repeat's array as a whole. A fixed value gives every record the same value, a
 * list and an object item are written as what they hold, and an alternative or an item of a list gives one value of the
 * node it is part of, whose assertions judge it.
 */
export function takesAssertions(input: MappingDraftInput, part: AssertionPart = "entry"): boolean {
  if (part === "alternative" || part === "item") {
    return false;
  }

  return input !== "Static" && input !== "List" && input !== "Group";
}

/** Why a node's assertions are not kept, said of an input that takes none. */
export function noAssertionsReason(input: MappingDraftInput, part: AssertionPart = "entry"): string {
  if (part === "alternative") {
    return "an alternative's value is judged by the assertions of the whole entry";
  }

  if (part === "item") {
    return "an item gives one value of the list, which has no value of its own to judge";
  }

  return input === "Static"
    ? "a fixed value gives every record the same value"
    : "a list or an object is written as what it holds, and has no value of its own to judge";
}

/** Whether a node's value is the row's, which the incoming stage judges: a dataset column or an expression. */
export function readsRow(input: MappingDraftInput): boolean {
  return input === "Dataset" || input === "Expression";
}

/** Where the value of a node that does not read the row comes from, said after "this value comes from". */
function valueOrigin(input: MappingDraftInput): string {
  switch (input) {
    case "Cache":
      return "the cache";
    case "Lookup":
      return "a lookup's record";
    case "Search":
      return "a search of the platform";
    case "Parent":
      return "the record's parent";
    case "Child":
      return "the link to the record's child";
    case "Coalesce":
      return "the first alternative that gives one";
    case "Repeat":
      return "the child rows, as an array";
    default:
      return "the mapping";
  }
}

/** What an entry's settings decide for its assertions. */
export interface AssertionNode {
  /** The target the entry fills, which messages name. */
  target: string;
  input: MappingDraftInput;
  /** The entry holds the record when it has no value ($required). */
  required: boolean;
  /** The template requires the variable the entry fills. */
  templateRequired: boolean;
  /** The entry holds several values (a list, an item of a repeated array, a find all), so values all or any applies. */
  severalValues: boolean;
}

/** Why the incoming stage is not offered for a node, or null when it is. */
export function incomingBlocked(node: AssertionNode): string | null {
  return readsRow(node.input)
    ? null
    : `This value comes from ${valueOrigin(node.input)}, not from the row, so an assertion judges the value the record carries.`;
}

/** Why a failure may not leave a node's value out, or null when it may. */
export function omitBlocked(node: AssertionNode): string | null {
  if (node.templateRequired) {
    return `The template requires ${node.target}, so a record never goes without it; hold or report the record instead.`;
  }

  return node.required
    ? "The entry holds the record when there is no value (Required), so its value cannot be left out; turn that off first, or hold or report the record."
    : null;
}

/**
 * What the entry's settings leave its assertions unable to say, one line each, as the check would refuse them: too many,
 * the incoming stage on a value that does not come from the row, and a failure leaving out a value the record cannot go
 * without. What an assertion itself lacks is said where it is edited.
 */
export function assertionConflicts(assertions: readonly MappingDraftAssertion[], node: AssertionNode): string[] {
  const conflicts: string[] = [];
  if (assertions.length > MAX_ENTRY_ASSERTIONS) {
    conflicts.push(`An entry states at most ${MAX_ENTRY_ASSERTIONS} assertions, and this one states ${assertions.length}; remove some.`);
  }

  const incoming = incomingBlocked(node);
  const omit = omitBlocked(node);
  assertions.forEach((assertion, index) => {
    if (assertion.stage === "incoming" && incoming !== null) {
      conflicts.push(`Assertion ${index + 1} judges the value the row gives. ${incoming}`);
    }

    if (assertion.onFail === "omit" && omit !== null) {
      conflicts.push(`Assertion ${index + 1} leaves the value out when it fails. ${omit}`);
    }
  });
  return conflicts;
}

/**
 * A variable's path as a record path a condition of the record stage reads: `data.Curves.CurveID` for
 * `osdu.data.Curves[].CurveID`, arrays crossed implicitly so a field inside the same array reads the same item.
 */
export function recordFieldOf(path: string): string {
  return path.replace(/^osdu\./, "").replaceAll("[]", "");
}
