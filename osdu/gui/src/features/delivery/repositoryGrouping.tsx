import type { TableGrouping } from "@/components/DataTable";
import { RepositoryHeading } from "./RepositoryGrid";
import { countOf, latestOf } from "./repositoryRows";

/** How a grid reads the repository of a row, when the repository's sync last saw it, and whether the row is a problem. */
interface RepositoryRows<T> {
  repoId: (row: T) => string;
  seenUtc: (row: T) => string;
  failing: (row: T) => boolean;
}

/** What a repository's heading calls its rows, and how it says that some of them are a problem. */
interface RowNouns {
  one: string;
  many: string;
  failing: (count: number) => string;
}

/**
 * Rows of several repositories, each repository a node of the grid with its name, how many rows it holds, when its sync
 * last saw them, and how many of them are a problem. Rows of one repository need no node: undefined, and the grid lists
 * them flat. The rows must already be in repository order (`inRepositoryOrder`), since a node takes the rows next to
 * each other that share its repository. `rows` is every row the grid lists: a paged grid shows a page of them, and a
 * repository's node on it still counts all of the repository's rows, on every page.
 */
export function repositoryGrouping<T>(
  rows: readonly T[] | undefined,
  read: RepositoryRows<T>,
  names: ReadonlyMap<string, string>,
  nouns: RowNouns,
): TableGrouping<T> | undefined {
  if (rows === undefined || new Set(rows.map(read.repoId)).size < 2) {
    return undefined;
  }

  const byRepository = new Map<string, T[]>();
  for (const row of rows) {
    const id = read.repoId(row);
    const held = byRepository.get(id);
    if (held === undefined) {
      byRepository.set(id, [row]);
    } else {
      held.push(row);
    }
  }

  return {
    levels: [{
      key: read.repoId,
      renderHeader: (group) => {
        const id = read.repoId(group[0]);
        const all = byRepository.get(id) ?? group;
        const failing = all.filter(read.failing).length;
        return (
          <RepositoryHeading
            name={names.get(id) ?? id}
            count={countOf(all.length, nouns.one, nouns.many)}
            seenUtc={latestOf(all.map(read.seenUtc))}
            problem={failing > 0 ? nouns.failing(failing) : null}
          />
        );
      },
    }],
  };
}
