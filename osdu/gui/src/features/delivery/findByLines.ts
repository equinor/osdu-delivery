// The findBy lines of a mapping's cache and search nodes (osdu/docs/reference/flow/mapping-lookups.md), as the mapping
// builder edits them and as the cache pages name what a line of several comparisons looked up. A line compares one field,
// or several joined by "and", which a record must meet together; the lines themselves are tried in order.

/** One findBy line as the draft stores it: its first comparison, and the comparisons after it on the same line. */
export interface DraftFind {
  field: string;
  column: string | null;
  literal: string | null;
  /** The comparisons a record must meet together with this one; absent, null or empty for a line of one. */
  and?: DraftFind[] | null;
}

/** One comparison as a form edits it: the field compared, and a dataset column or a fixed text. */
export interface FindPart {
  field: string;
  mode: "column" | "text";
  value: string;
}

/** One findBy line as a form edits it: its first comparison, and the comparisons a record must meet together with it. */
export interface FindLine extends FindPart {
  and: FindPart[];
}

/** A new line comparing `field` with a column still to be named. */
export function newFindLine(field: string): FindLine {
  return { field, mode: "column", value: "", and: [] };
}

/** A column as the draft stores it: trimmed, and without the dataset. prefix a person may type out of habit. */
export function bareColumn(text: string): string {
  return text.trim().replace(/^dataset\./, "");
}

/** Every comparison of a draft's line, the first one first. */
export function comparisonsOf(find: DraftFind): DraftFind[] {
  return [find, ...(find.and ?? [])];
}

function partOf(find: DraftFind): FindPart {
  return find.literal !== null && find.literal !== ""
    ? { field: find.field, mode: "text", value: find.literal }
    : { field: find.field, mode: "column", value: find.column ?? "" };
}

function findOf(part: FindPart): DraftFind {
  return part.mode === "text"
    ? { field: part.field.trim(), column: null, literal: part.value }
    : { field: part.field.trim(), column: bareColumn(part.value), literal: null };
}

/** The draft's findBy lines as a form edits them. */
export function findLinesOf(finds: readonly DraftFind[]): FindLine[] {
  return finds.map((find): FindLine => ({ ...partOf(find), and: (find.and ?? []).map(partOf) }));
}

/** A form's findBy lines as the draft stores them: a line of one comparison carries no `and`. */
export function findsOf(lines: readonly FindLine[]): DraftFind[] {
  return lines.map((line): DraftFind => ({ ...findOf(line), and: line.and.length === 0 ? null : line.and.map(findOf) }));
}

/**
 * The lines as one phrase, the way the renderer words it: consecutive lines of one comparison that compare the same value
 * run together (`Code/Name = dataset.unit`), a line of several reads as its comparisons joined by "and", in brackets when
 * other lines stand beside it, and the lines after the first read as alternatives.
 */
export function findByPhrase(finds: readonly DraftFind[], operandOf: (find: DraftFind) => string): string {
  const groups: ({ fields: string[]; operand: string } | { together: string })[] = [];
  for (const find of finds) {
    const comparisons = comparisonsOf(find);
    if (comparisons.length > 1) {
      groups.push({ together: comparisons.map((comparison) => `${comparison.field} = ${operandOf(comparison)}`).join(" and ") });
      continue;
    }

    const operand = operandOf(find);
    const last = groups.at(-1);
    if (last !== undefined && "fields" in last && last.operand === operand) {
      last.fields.push(find.field);
    } else {
      groups.push({ fields: [find.field], operand });
    }
  }

  return groups
    .map((group) => ("fields" in group ? `${group.fields.join("/")} = ${group.operand}` : groups.length === 1 ? group.together : `(${group.together})`))
    .join(" or ");
}

/**
 * What a line of several comparisons looked up and found no row meeting together, as the cache's gaps and changes keep it
 * (a path of fields joined by " and ", the values as a JSON array): `Well = A-12 and Mnemonic = GR`. Null for anything
 * else, which is shown as it is.
 */
export function compositeLookedUp(path: string, value: string | null): string | null {
  if (value === null || !path.includes(" and ")) {
    return null;
  }

  const fields = path.split(" and ");
  let parsed: unknown;
  try {
    parsed = JSON.parse(value);
  } catch {
    // A value the ledger cut short at its column's length: shown as it is kept.
    return null;
  }

  if (!Array.isArray(parsed) || parsed.length !== fields.length || !parsed.every((item): item is string => typeof item === "string")) {
    return null;
  }

  const values: readonly string[] = parsed;
  return fields.map((field, index) => `${field} = ${values[index]}`).join(" and ");
}
