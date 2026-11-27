import type { SchemaChangeColumn } from "../../api/types.ts";

const typeOf = (type: string | null) => type ?? "(type not read)";

const nullabilityOf = (nullable: boolean | null) => (nullable === null ? "" : nullable ? " NULL" : " NOT NULL");

/** Two rendered types are the same type in any case and with any spacing, as the server compares them. */
const sameType = (a: string | null, b: string | null) =>
  a === null || b === null || a.replace(/\s+/g, "").toLowerCase() === b.replace(/\s+/g, "").toLowerCase();

/**
 * How one column a sync change moved reads in an object's history, in a few words: an added column's type, what a
 * dropped one was, and for a changed one what changed about it. A changed column whose type and nullability stayed
 * was put elsewhere, so only then is its place named: a type change beside an added column also shifts its position,
 * which is not a move.
 */
export function describeColumnChange(column: SchemaChangeColumn): string {
  switch (column.changeType) {
    case "Added":
      return `${typeOf(column.toType)}${nullabilityOf(column.toNullable)}`;
    case "Deleted":
      return `was ${typeOf(column.fromType)}${nullabilityOf(column.fromNullable)}`;
    case "Changed": {
      const parts: string[] = [];
      if (!sameType(column.fromType, column.toType)) {
        parts.push(`${typeOf(column.fromType)} to ${typeOf(column.toType)}`);
      }
      if (column.fromNullable !== null && column.toNullable !== null && column.fromNullable !== column.toNullable) {
        parts.push(column.toNullable ? "now takes NULL" : "now NOT NULL");
      }
      if (parts.length === 0 && column.fromOrdinal !== null && column.toOrdinal !== null) {
        parts.push(`moved from position ${column.fromOrdinal} to ${column.toOrdinal}`);
      }
      return parts.join(", ");
    }
  }
}

/** Whether a change touched a column whose name holds the term, for the page's free-text filter. */
export function movesColumnMatching(columns: readonly SchemaChangeColumn[], term: string): boolean {
  return columns.some((column) => column.name.toLowerCase().includes(term));
}
