/** A column of a list of runs, by the id it is known by. */
export interface RunListColumn {
  id: string;
}

/**
 * A list of runs' columns with SQLFlow's row and file counts (the columns `counts` names) replaced by a flow kind's own
 * (`FlowKindContribution.runCountColumns`), at the place the first of them stood, so the kind's figures sit where a
 * reader looks for what a run did. Without the kind's columns the list is as given; an empty set of them leaves the
 * counts out. A list that holds none of the counts takes the kind's at its end.
 */
export function withKindCounts<T extends RunListColumn>(
  columns: readonly T[],
  counts: readonly string[],
  kindCounts: readonly T[] | undefined,
): T[] {
  if (kindCounts === undefined) {
    return [...columns];
  }

  const replaced = new Set(counts);
  const first = columns.findIndex((column) => replaced.has(column.id));
  const kept = columns.filter((column) => !replaced.has(column.id));
  if (first < 0) {
    return [...kept, ...kindCounts];
  }

  return [...kept.slice(0, first), ...kindCounts, ...kept.slice(first)];
}
