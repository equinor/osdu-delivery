import type { RecordNode } from "../../osduRecordModel";
import { locationOf, namesOf } from "../../recordPath.ts";

// A record of the inventory as the JSON a duplicate key is picked from (osdu/docs/reference/flow/inventory-duplicates.md,
// The key): which of its lines can join the key and by which property, and which lines show as the document is folded,
// searched, or narrowed to what the key compares.

/** The roots a key's property starts at, as the control plane reads a record; `id` and `version` are every record's own. */
export const KEY_ROOTS: readonly string[] = [
  "data", "meta", "tags", "ancestry", "acl", "legal", "kind", "createUser", "createTime", "modifyUser", "modifyTime",
];

/** The sections of a record folded when it is first shown: what the copies of a record rarely tell apart by. */
export const FOLDED_AT_FIRST: readonly string[] = ["acl", "legal", "tags", "meta", "ancestry"];

/** How many items of a list show before the rest wait behind a button: enough to see what each item holds. */
export const ITEMS_AT_FIRST = 3;

/** How many more items of a list each click on its button shows. */
export const ITEMS_PAGE = 100;

/** A property name a key can carry: the address writes a key's paths as names joined by dots. */
const KEY_NAME = /^[A-Za-z_][A-Za-z0-9_]*$/;

/** What a line of the document is to the key: the property it would add, or why none of it can be, or nothing (a section). */
export type KeyPick = { path: string } | { problem: string } | null;

/** The property a path of the record names, its list places left out: `data.Curves[3].Mnemonic` is `data.Curves.Mnemonic`. */
export function keyPathOf(path: string): string {
  return namesOf(locationOf(path)).join(".");
}

/** Whether a branch is a list of values (text, numbers, true or false), which a key compares as the values it holds. */
function isValueList(node: RecordNode): boolean {
  return node.kind === "array" && node.children.length > 0 && node.children.every((child) => child.kind === "leaf");
}

/** The property a path of the record adds to a key, or why it cannot be one. */
function pickOfPath(path: string): KeyPick {
  const names = namesOf(locationOf(path));
  const root = names[0] ?? "";
  if (root === "id" || root === "version") {
    return { problem: `Every record holds its own ${root}, so no two share it.` };
  }

  if (!KEY_ROOTS.includes(root)) {
    return { problem: `A key's property starts at one of the record's roots: ${KEY_ROOTS.join(", ")}.` };
  }

  if (!names.every((name) => KEY_NAME.test(name))) {
    return { problem: "A key names a property by letters, digits and underscores, and this one's name holds other characters." };
  }

  return { path: names.join(".") };
}

/**
 * What a line adds to the key: a value adds its property, and a value of a list of values (and the list itself) adds the
 * list's property, which compares every value the list holds, in any order. A value inside the items of a list adds its
 * property across every item. A section (an object, a list of objects, an empty list) holds no value of its own to compare.
 */
export function pickOf(node: RecordNode, parent: RecordNode | null): KeyPick {
  if (parent === null) {
    return null;
  }

  if (node.kind === "leaf") {
    return pickOfPath(parent.kind === "array" ? parent.path : node.path);
  }

  return isValueList(node) ? pickOfPath(node.path) : null;
}

/** The property a pick adds, or null for a line that adds none. */
export function pickedPath(pick: KeyPick): string | null {
  return pick !== null && "path" in pick ? pick.path : null;
}

/** One line of the document as it is drawn. */
export interface DocumentLine {
  /** Unique among the lines, for the list's keys. */
  id: string;
  /**
   * `value`: a value; `open`: a section's opening bracket, its content on the lines after; `folded`: a section on one line,
   * its content behind its caret; `empty`: a section that holds nothing; `close`: a section's closing bracket; `more`: the
   * items of a list waiting behind a button.
   */
  kind: "value" | "open" | "folded" | "empty" | "close" | "more";
  depth: number;
  node: RecordNode;
  /** The property name the line starts with; null for an item of a list, a closing bracket and the record itself. */
  name: string | null;
  /** What the line adds to the key; a closing bracket and a list's button add nothing. */
  pick: KeyPick;
  /** Whether the line ends with a comma, as JSON writes one between the members of a section. */
  comma: boolean;
  /** For `more`: how many items are waiting. */
  hidden?: number;
  /** For `more`: how many items the list shows now. */
  showing?: number;
}

/** How the document is shown: what the reader folded and unfolded, the search, and whether only the key's properties show. */
export interface DocumentView {
  /** The sections folded. */
  folded: ReadonlySet<string>;
  /** How many items each list shows, where a reader asked for more than the first ones. */
  shown: ReadonlyMap<string, number>;
  /** The search, lower-cased and trimmed; empty for none. */
  term: string;
  /** The key's properties, when only the lines that compare them show; null for every line. */
  only: ReadonlySet<string> | null;
}

/** The document's lines, and how many of its names and values hold the search. */
export interface DocumentLines {
  lines: DocumentLine[];
  matches: number;
}

/**
 * The lines a record is drawn as. Unfiltered, a section the reader folded is one line and a list shows its first items, the
 * rest behind a button. A search (a property's name or a value) shows every line that holds it, with the sections around it
 * open whatever was folded, and a section whose name holds it whole. The key's properties alone show the lines that compare
 * them the same way, a list its first items holding them and the rest behind its button.
 */
export function documentLines(root: RecordNode, view: DocumentView): DocumentLines {
  const { term, only } = view;
  const filtering = term !== "" || only !== null;
  const kept = new Map<string, boolean>();
  let matches = 0;

  // Which lines a filter keeps: a line that holds what is asked, and every section holding one.
  const keep = (node: RecordNode, parent: RecordNode | null, inside: boolean): boolean => {
    const named = parent !== null && parent.kind === "object" && term !== "" && node.keyText.includes(term);
    const valued = term !== "" && node.kind === "leaf" && node.valueText.includes(term);
    if (named || valued) {
      matches += 1;
    }

    const held = inside || term === "" || named || valued;
    const picked = only === null || (() => {
      const path = pickedPath(pickOf(node, parent));
      return path !== null && only.has(path);
    })();
    let any = false;
    for (const child of node.children) {
      // Every child is visited, so each one's answer is kept for the lines that follow.
      any = keep(child, node, inside || named) || any;
    }

    // A section shows for its own sake when its name holds the search, when it holds nothing (so the search finds it empty),
    // or when it is a list of values the key compares; otherwise it shows for the lines inside it that do.
    const own = node.kind === "leaf" || node.children.length === 0 || named || only !== null;
    const shown = (held && picked && own) || any;
    kept.set(node.path, shown);
    return shown;
  };

  if (filtering) {
    keep(root, null, false);
  }

  const lines: DocumentLine[] = [];
  const emit = (node: RecordNode, parent: RecordNode | null, depth: number, comma: boolean) => {
    const name = parent !== null && parent.kind === "object" ? node.key : null;
    const pick = pickOf(node, parent);
    if (node.kind === "leaf") {
      lines.push({ id: node.path, kind: "value", depth, node, name, pick, comma });
      return;
    }

    if (node.children.length === 0) {
      lines.push({ id: node.path, kind: "empty", depth, node, name, pick, comma });
      return;
    }

    if (!filtering && parent !== null && view.folded.has(node.path)) {
      lines.push({ id: node.path, kind: "folded", depth, node, name, pick, comma });
      return;
    }

    lines.push({ id: `${node.path}:open`, kind: "open", depth, node, name, pick, comma: false });
    // A search shows every item that holds it; otherwise a list shows its first items (of those holding the key's
    // properties, when only they show), the rest behind a button.
    const candidates = filtering ? node.children.filter((child) => kept.get(child.path) === true) : node.children;
    const limit = node.kind !== "array" || term !== "" ? candidates.length : Math.min(candidates.length, view.shown.get(node.path) ?? ITEMS_AT_FIRST);
    const children = candidates.slice(0, limit);
    const waiting = candidates.length - limit;
    children.forEach((child, index) => emit(child, node, depth + 1, index < children.length - 1 || waiting > 0));
    if (waiting > 0) {
      lines.push({ id: `${node.path}:more`, kind: "more", depth: depth + 1, node, name: null, pick: null, comma: false, hidden: waiting, showing: limit });
    }

    lines.push({ id: `${node.path}:close`, kind: "close", depth, node, name: null, pick: null, comma });
  };

  if (!filtering || kept.get(root.path) === true) {
    emit(root, null, 0, false);
  }

  return { lines, matches };
}

/**
 * The values a record holds at a key's property, as the check reads them: every value of every item of each list on the way,
 * and of a list of values at its end, in the record's order. None when the record holds nothing there.
 */
export function valuesAt(record: unknown, path: string): unknown[] {
  let current: unknown[] = [record];
  for (const name of path.split(".")) {
    const next: unknown[] = [];
    for (const value of current) {
      for (const item of Array.isArray(value) ? value : [value]) {
        if (item !== null && typeof item === "object" && !Array.isArray(item) && Object.prototype.hasOwnProperty.call(item, name)) {
          next.push((item as Record<string, unknown>)[name]);
        }
      }
    }

    current = next;
  }

  return current.flatMap((value) => (Array.isArray(value) ? value : [value]));
}

/**
 * What the document opens to show the first line of a key's property: the line's path, the sections it sits in, and for each
 * list on the way how many items it must show; null when the record holds no line of the property.
 */
export function revealOf(root: RecordNode, path: string): { line: string; sections: string[]; items: Map<string, number> } | null {
  const find = (node: RecordNode, parent: RecordNode | null, chain: RecordNode[]): RecordNode[] | null => {
    if (pickedPath(pickOf(node, parent)) === path) {
      return [...chain, node];
    }

    for (const child of node.children) {
      const found = find(child, node, [...chain, node]);
      if (found !== null) {
        return found;
      }
    }

    return null;
  };

  const chain = find(root, null, []);
  if (chain === null) {
    return null;
  }

  const items = new Map<string, number>();
  for (let index = 0; index < chain.length - 1; index += 1) {
    const section = chain[index];
    if (section.kind === "array") {
      items.set(section.path, section.children.indexOf(chain[index + 1]) + 1);
    }
  }

  return { line: chain[chain.length - 1].path, sections: chain.slice(1, -1).map((node) => node.path), items };
}

/** Every section of a record below the record itself: what Fold all folds. */
export function sectionsOf(root: RecordNode): Set<string> {
  const sections = new Set<string>();
  const walk = (node: RecordNode) => {
    for (const child of node.children) {
      if (child.kind !== "leaf") {
        sections.add(child.path);
        walk(child);
      }
    }
  };
  walk(root);
  return sections;
}
