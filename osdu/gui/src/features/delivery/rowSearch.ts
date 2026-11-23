/**
 * The rows a search names: every word typed is found in what the row shows, in any order and ignoring case, so a reader
 * finds a row by any value they read off it, or by part of one. An empty search names every row, in the order given.
 */
export function rowsMatching<T>(rows: readonly T[], search: string, textOf: (row: T) => string): T[] {
  const words = search.toLowerCase().split(/\s+/).filter((word) => word !== "");
  if (words.length === 0) {
    return [...rows];
  }

  return rows.filter((row) => {
    const text = textOf(row).toLowerCase();
    return words.every((word) => text.includes(word));
  });
}
