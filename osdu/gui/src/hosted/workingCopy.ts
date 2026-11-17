// A mapping document being written in the editor, placed in the estate (hosted/WorkingCopyMappingPage.tsx): what the
// text says it is, the synced mapping it is a copy of when the estate has one, and the repository its template is
// settled from. Kept free of imports, so the unit suite reads it.

/** What the page reads of a synced mapping to place a working copy against it. */
export interface PlacedMapping {
  id: string;
  repoId: string;
  reference: string;
  relativePath: string;
}

/** What a mapping document says it is, from its root keys: enough to name it and find it before it parses whole. */
export interface DeclaredMapping {
  name: string | null;
  version: string | null;
  /** `name@version`, the reference a flow pins it by; null while either is missing. */
  reference: string | null;
  /** The kind of the template it fills. */
  kind: string | null;
}

function scalar(value: string): string | null {
  const trimmed = value.trim();
  const quoted = /^(["'])(.*?)\1/.exec(trimmed);
  const read = quoted !== null ? quoted[2] : trimmed.replace(/\s+#.*$/, "").trim();
  return read === "" ? null : read;
}

function rootValue(text: string, key: string): string | null {
  const match = new RegExp(`^${key}[ \\t]*:[ \\t]*([^\\r\\n]*)`, "m").exec(text);
  return match === null ? null : scalar(match[1]);
}

/**
 * Reads what a mapping document declares at its root. By line, not by parsing the document: a mapping half written is
 * exactly the one a person wants to see laid out, and it still names itself.
 */
export function declaredMapping(text: string): DeclaredMapping {
  const name = rootValue(text, "name");
  const version = rootValue(text, "version");
  const template = /^template[ \t]*:[ \t]*(?:#[^\r\n]*)?\r?\n((?:[ \t]+[^\r\n]*(?:\r?\n|$)|[ \t]*\r?\n)+)/m.exec(text);
  const kind = template === null ? null : /^[ \t]+kind[ \t]*:[ \t]*([^\r\n]*)/m.exec(template[1]);
  return {
    name,
    version,
    reference: name !== null && version !== null ? `${name}@${version}` : null,
    kind: kind === null ? null : scalar(kind[1]),
  };
}

/** A key that changes when the text does: what the page's reads of a document are cached by, in place of the sync's hash. */
export function textKey(text: string): string {
  // FNV-1a over the text's code units: a key for a cache, never a claim about content.
  let hash = 0x811c9dc5;
  for (let index = 0; index < text.length; index++) {
    hash ^= text.charCodeAt(index);
    hash = Math.imul(hash, 0x01000193);
  }

  return `working-copy-${text.length.toString(16)}-${(hash >>> 0).toString(16)}`;
}

function segments(path: string): string[] {
  return path.replace(/\\/g, "/").split("/").filter((segment) => segment !== "" && segment !== ".");
}

/**
 * Whether two paths name one file, seen from two roots: the estate reads a repository from its own root, and the editor
 * may have the repository, a folder above it or a folder inside it open, so one path is the tail of the other.
 */
export function sameFile(a: string, b: string): boolean {
  const first = segments(a);
  const second = segments(b);
  const [longer, shorter] = first.length >= second.length ? [first, second] : [second, first];
  return shorter.length > 0 && shorter.every((segment, index) => segment === longer[longer.length - shorter.length + index]);
}

function folderOf(path: string): string {
  return segments(path).slice(0, -1).join("/");
}

/** Where a working copy stands in the estate. */
export interface WorkingCopyPlace<T extends PlacedMapping> {
  /** The synced mapping this file is the working copy of, when the estate has it. */
  synced: T | null;
  /**
   * The synced repository the file belongs to, whose template folders settle its template: the synced mapping's, or,
   * for a mapping the estate has not seen yet, the repository whose mappings sit in the same folder.
   */
  repoId: string | null;
}

/**
 * Places a working copy among the estate's mappings. It is the working copy of the mapping at its path that goes by its
 * reference; a file whose name or version was changed since the sync is still that mapping's working copy, so the path
 * alone decides when no reference matches. A new file belongs to the repository its neighbours were synced from.
 */
export function placeWorkingCopy<T extends PlacedMapping>(mappings: readonly T[], path: string, reference: string | null): WorkingCopyPlace<T> {
  const here = mappings.filter((mapping) => sameFile(mapping.relativePath, path));
  const synced = here.find((mapping) => mapping.reference === reference) ?? here[0] ?? null;
  if (synced !== null) {
    return { synced, repoId: synced.repoId };
  }

  const folder = folderOf(path);
  const neighbour = mappings.find((mapping) => {
    const theirs = folderOf(mapping.relativePath);
    return theirs !== "" && folder !== "" && (sameFile(theirs, folder));
  });
  return { synced: null, repoId: neighbour?.repoId ?? null };
}

/** What the page knows of the document it shows, beside where it stands. */
export interface WorkingCopyDocument {
  path: string;
  /** What the text declares itself, `name@version`; null while either is missing. */
  reference: string | null;
  /** The key of the text shown (textKey). */
  contentHash: string;
  /** The template folder file its template settles to; empty when none is found. */
  templatePath: string;
}

/** A mapping the estate has not seen, as the workspace reads it: the document alone, which no flow renders with. */
export interface UnseenMapping {
  id: null;
  repoId: string | null;
  reference: string;
  relativePath: string;
  contentHash: string;
  templatePath: string;
}

/**
 * The mapping the page's workspace shows for a working copy: the synced mapping it is a copy of, keyed by the text
 * shown so what is drawn from the text follows a save, or, for one the estate has not seen, the document itself, with
 * no id, since no flow renders with it until the repository is synced.
 */
export function workspaceMappingOf<T extends PlacedMapping>(place: WorkingCopyPlace<T>, document: WorkingCopyDocument): T | UnseenMapping {
  if (place.synced !== null) {
    return { ...place.synced, contentHash: document.contentHash };
  }

  return {
    id: null,
    repoId: place.repoId,
    reference: document.reference ?? document.path,
    relativePath: document.path,
    contentHash: document.contentHash,
    templatePath: document.templatePath,
  };
}
