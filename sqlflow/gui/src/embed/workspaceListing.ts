// How a host's side bar lists the working copy (protocol.ts, WorkspaceState): its documents by the sort they are, and
// the ones a typed term finds. A working copy can hold thousands of documents, so a group shows a first stretch of its
// rows and more on asking, and a term narrows every group at once. Kept free of imports beyond the protocol's types, so
// the unit suite reads it.

import type { WorkspaceDocument } from "./protocol.ts";

/** The documents of one sort, in path order. */
export interface DocumentGroup {
  label: string;
  documents: WorkspaceDocument[];
}

/** How many of a group's rows are drawn before the person asks for more. */
export const GROUP_STRETCH = 200;

function isWordCharacter(character: string): boolean {
  return /[\p{L}\p{N}]/u.test(character);
}

/** Whether `text` holds `word` at the start of one of its words: at its own start, or after anything that is not a letter or a digit. */
function holdsAtWordStart(text: string, word: string): boolean {
  let from = 0;
  for (;;) {
    const found = text.indexOf(word, from);
    if (found < 0) {
      return false;
    }

    if (found === 0 || !isWordCharacter(text[found - 1])) {
      return true;
    }

    from = found + 1;
  }
}

/** The words of a typed term, lower-cased; none for a blank one. */
export function termWords(term: string): string[] {
  return term.toLowerCase().split(/\s+/).filter((word) => word !== "");
}

/**
 * Whether a row is found by a term: every word of it starts a word of what the row shows, its name or its path. A row
 * shows both, so a person finds a document by either, and by a folder on its path as well as by its file.
 */
export function documentMatches(document: WorkspaceDocument, words: readonly string[]): boolean {
  if (words.length === 0) {
    return true;
  }

  const name = document.name.toLowerCase();
  const path = document.path.toLowerCase();
  return words.every((word) => holdsAtWordStart(name, word) || holdsAtWordStart(path, word));
}

/** The documents a term finds, by the sort they are: groups in name order, each in path order, an empty one left out. */
export function groupDocuments(documents: readonly WorkspaceDocument[], term: string): DocumentGroup[] {
  const words = termWords(term);
  const groups = new Map<string, WorkspaceDocument[]>();
  for (const document of documents) {
    if (!documentMatches(document, words)) {
      continue;
    }

    const members = groups.get(document.group);
    if (members === undefined) {
      groups.set(document.group, [document]);
    } else {
      members.push(document);
    }
  }

  return [...groups.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([label, members]) => ({
      label,
      documents: [...members].sort((a, b) => a.path.localeCompare(b.path)),
    }));
}

/** The folder a document sits in, as a row shows it beside the name: the path without the file, empty at the root. */
export function folderOf(path: string): string {
  const cut = path.lastIndexOf("/");
  return cut < 0 ? "" : path.slice(0, cut);
}

/** The group holding a document, so the side bar can unfold it when the document comes to the front of the editor. */
export function groupOf(groups: readonly DocumentGroup[], id: string | null): string | null {
  if (id === null) {
    return null;
  }

  return groups.find((group) => group.documents.some((document) => document.id === id))?.label ?? null;
}
