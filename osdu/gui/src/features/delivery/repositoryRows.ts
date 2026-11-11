/** The repository pick that lists every repository. */
export const ALL_REPOSITORIES = "all";

/** A repository a grid's rows come from: its id and the name it is shown by. */
export interface GridRepository {
  id: string;
  name: string;
}

/** `count` with its noun, the noun made plural for any count but one. */
export function countOf(count: number, noun: string, plural = `${noun}s`): string {
  return `${count.toLocaleString("en-US")} ${count === 1 ? noun : plural}`;
}

/** The latest of the ISO instants given, or null for none. */
export function latestOf(values: readonly string[]): string | null {
  return values.reduce<string | null>((best, value) => (best === null || value > best ? value : best), null);
}

/**
 * The rows in the order a grid lists them: by the name of their repository, ignoring case, and within one in the order
 * they came, which is the repository's own path order. A page lists, searches and opens the first of what it shows in
 * this order, so Enter opens the row on top. A repository with no name is placed by its id.
 */
export function inRepositoryOrder<T>(rows: readonly T[], repoId: (row: T) => string, names: ReadonlyMap<string, string>): T[] {
  return rows
    .map((row, index) => ({ row, index, name: names.get(repoId(row)) ?? repoId(row) }))
    .sort((a, b) => a.name.localeCompare(b.name, "en-US", { sensitivity: "base" }) || a.index - b.index)
    .map((entry) => entry.row);
}

/** The repositories the rows come from, by name, ignoring case; one with no name is shown by its id. */
export function repositoriesOf<T>(rows: readonly T[], repoId: (row: T) => string, names: ReadonlyMap<string, string>): GridRepository[] {
  return [...new Set(rows.map(repoId))]
    .map((id) => ({ id, name: names.get(id) ?? id }))
    .sort((a, b) => a.name.localeCompare(b.name, "en-US", { sensitivity: "base" }));
}

/**
 * The repository a stored pick names, or every one when it names none of them: a repository removed since, or the only
 * repository there is, which a page shows no picker for, so a pick of it could never be taken back.
 */
export function pickedRepository(value: string, repositories: readonly GridRepository[]): string {
  return repositories.length > 1 && repositories.some((repository) => repository.id === value) ? value : ALL_REPOSITORIES;
}
