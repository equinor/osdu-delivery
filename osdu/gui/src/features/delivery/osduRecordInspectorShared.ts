// What the parts of the OSDU record inspector (OsduRecordInspector) share: how many rows of a branch show at once,
// how a record opens another and names the one it was opened from, and how a value reads as text.

import { idParts, isMintedUnique } from "./osduRecordModel";

/** How many rows of one branch show before the rest wait behind a button, so a curve list of thousands stays usable. */
export const PAGE = 100;

/** The record a linked one was opened from, as the way back names it: which record, where in it the link stood, and its place. */
export interface EarlierRecord {
  id: string;
  from: string | null;
  level: number;
}

/**
 * How a record opens another: the id, and the path of the value in this record that names it; null for a record opened
 * from somewhere else (one that mentions this record).
 */
export type OpenLink = (id: string, from: string | null) => void;

export function text(value: unknown): string | null {
  return typeof value === "string" ? value : typeof value === "number" ? String(value) : null;
}

export function texts(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : [];
}

/**
 * Whether a crumb names a record by its type alone. The first record is the page's own, which the page already names
 * above the inspector, and an id minted by a machine says nothing a reader knows; either would only take the path's
 * room. A linked record whose id reads as a name (a wellbore's, a reference value's) keeps it, as nothing else names it.
 */
export function namedByType(id: string, level: number): boolean {
  return level === 0 || isMintedUnique(idParts(id).unique);
}
