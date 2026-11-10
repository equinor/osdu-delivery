// Where the document names a column or a table of the overview (osdu/docs/reference/concepts/mapping-overview.md, The YAML
// beside the canvas): the blocks of what reads it, lit whole as a property's are, and the very places it is named, marked.
// No drawing here: OverviewYaml draws what this works out.

import type { MappingOutline, MappingOutlineFlow, MappingOutlineNode } from "../../../api/mappingOutline.ts";
import { tokensOf, type Token } from "../dimensions/yamlTokens.ts";

/** A stretch of a line, by the offsets of its first character and of the one after its last. */
export interface LineRun {
  start: number;
  end: number;
}

/** What the document shows of a part: the lines lit, where on them the part is named, and the line to bring into view. */
export interface PartLines {
  /** The lines lit, from 1. */
  lit: Set<number>;
  /** Where on a line the part is named, by line from 1, in order. */
  marks: Map<number, LineRun[]>;
  /** The first line naming it, else the first line lit; null when the document writes it nowhere. */
  first: number | null;
}

/** A column or a table of the canvas: a field of a source, or the whole source when `field` is null. */
export interface LinesPart {
  source: string;
  field: string | null;
}

/** A character a name runs on into: one of these beside a match makes it part of a longer name. */
const WORD = /[A-Za-z0-9_]/;

/**
 * Where `name` stands on `line` as a whole name, outside a comment: not run on into a longer name on either side
 * (`log_id` is not found in `wellbore_log_id`), compared as written or, with `caseless`, ignoring case.
 */
export function namedOn(line: string, name: string, caseless: boolean, tokens: readonly Token[] = tokensOf(line)): LineRun[] {
  if (name === "") {
    return [];
  }

  const haystack = caseless ? line.toLowerCase() : line;
  const needle = caseless ? name.toLowerCase() : name;
  // A letter whose lower case is longer than it would move every offset after it.
  if (caseless && (haystack.length !== line.length || needle.length !== name.length)) {
    return [];
  }

  const runs: LineRun[] = [];
  for (let at = haystack.indexOf(needle); at >= 0; at = haystack.indexOf(needle, at + 1)) {
    const end = at + needle.length;
    const before = at === 0 ? "" : line[at - 1];
    const after = end >= line.length ? "" : line[end];
    if (WORD.test(before) || WORD.test(after) || tokens[at] === "comment") {
      continue;
    }

    runs.push({ start: at, end });
  }

  return runs;
}

/** Whether a node's own value reads the part: as a value, a condition, what a lookup compares, or what it fetches. */
function reads(flow: MappingOutlineFlow | null | undefined, part: LinesPart): boolean {
  if (flow === null || flow === undefined) {
    return false;
  }

  return flow.reads.some((read) => read.source === part.source && (part.field === null || read.field === part.field))
    || flow.steps.some((step) => step.lookups.some((lookup) => lookup.source === part.source
      && (part.field === null || lookup.field === part.field || lookup.matches.some((match) => match.field === part.field))));
}

/** The child dataset a source id names (`curves` of `dataset.curves`), or null for another source. */
function childOf(source: string): string | null {
  return source.startsWith("dataset.") ? source.slice("dataset.".length) : null;
}

/**
 * The blocks of the nodes reading the part, each the innermost that does: an alternative of a `$coalesce` rather than the
 * whole of it. A node the document writes no lines for stands in its nearest ancestor that has them. A child dataset's
 * blocks include the whole `$forEach` that repeats over its rows, whatever inside it reads them too.
 */
function readerBlocks(nodes: readonly MappingOutlineNode[], part: LinesPart, child: string | null, held: LineRun | null, into: LineRun[]): boolean {
  let found = false;
  for (const node of nodes) {
    const lines = node.line !== null && node.line !== undefined && node.endLine !== null && node.endLine !== undefined ? { start: node.line, end: node.endLine } : held;
    const below = readerBlocks(node.children, part, child, lines, into);
    const own = reads(node.flow, part);
    const repeats = part.field === null && child !== null && node.shape === "repeat" && node.dataset === child;
    if (lines !== null && ((own && !below) || repeats)) {
      into.push(lines);
    }

    found = found || below || own || repeats;
  }

  return found;
}

/** The words the part is named by in the blocks reading it: the field, or for a table the name a reader writes it by. */
function namesOf(outline: MappingOutline, part: LinesPart): string[] {
  if (part.field !== null) {
    return [part.field];
  }

  const source = outline.sources.find((candidate) => candidate.id === part.source);
  // A table of the record and the parameters are named by none of their own words; a cached type, a search and a child
  // dataset by the name `$cache:`, `$search:` and `$forEach:` write.
  return source !== undefined && (source.kind === "cache" || source.kind === "search" || source.kind === "child") ? [source.name] : [];
}

/**
 * Where the document names `part`, read from the mapping's `lines` (osdu/docs/reference/concepts/mapping-overview.md): every
 * block of a property, an alternative or a rule on the rows that reads it, lit whole, and the block that declares its table
 * (the `dataset` block, the `parameters` block, a search's block), where only the lines naming the field are lit, or the whole
 * block for the table itself. Within those the part is marked where it is named, as written, or ignoring case where a block
 * names it in no other way; a comment's mention is no place it is read.
 */
export function linesOfPart(outline: MappingOutline, lines: readonly string[], part: LinesPart): PartLines {
  const lit = new Set<number>();
  const marks = new Map<number, LineRun[]>();
  const names = namesOf(outline, part);
  const tokens = new Map<number, Token[]>();
  const tokensAt = (line: number) => {
    let held = tokens.get(line);
    if (held === undefined) {
      held = tokensOf(lines[line - 1] ?? "");
      tokens.set(line, held);
    }

    return held;
  };

  // The stretches naming the part within `block`, as written, else ignoring case.
  const namedIn = (block: LineRun): Map<number, LineRun[]> => {
    for (const caseless of [false, true]) {
      const found = new Map<number, LineRun[]>();
      for (let line = block.start; line <= block.end && line <= lines.length; line++) {
        const runs = names.flatMap((name) => namedOn(lines[line - 1], name, caseless, tokensAt(line)));
        if (runs.length > 0) {
          found.set(line, runs.sort((a, b) => a.start - b.start));
        }
      }

      if (found.size > 0) {
        return found;
      }
    }

    return new Map();
  };

  const mark = (found: Map<number, LineRun[]>) => {
    for (const [line, runs] of found) {
      const held = marks.get(line) ?? [];
      for (const run of runs) {
        if (!held.some((other) => other.start === run.start)) {
          held.push(run);
        }
      }

      marks.set(line, held.sort((a, b) => a.start - b.start));
    }
  };

  // What reads it, lit whole: the properties and alternatives, and the rules stated on the rows.
  const blocks: LineRun[] = [];
  readerBlocks(outline.record, part, childOf(part.source), null, blocks);
  for (const rule of outline.dataset.rules ?? []) {
    const judges = part.field === null
      ? rule.source === part.source || rule.reads.some((read) => read.source === part.source)
      : rule.reads.some((read) => read.source === part.source && read.field === part.field);
    if (judges && rule.line !== null && rule.line !== undefined && rule.endLine !== null && rule.endLine !== undefined) {
      blocks.push({ start: rule.line, end: rule.endLine });
    }
  }

  for (const block of blocks) {
    for (let line = block.start; line <= block.end && line <= lines.length; line++) {
      lit.add(line);
    }

    mark(namedIn(block));
  }

  // Where its table is declared: the lines naming a field there (a key, a label, a parameter), or the whole block for the table.
  const source = outline.sources.find((candidate) => candidate.id === part.source);
  if (source?.line !== null && source?.line !== undefined && source.endLine !== null && source.endLine !== undefined) {
    const declared = { start: source.line, end: Math.min(source.endLine, lines.length) };
    if (part.field === null) {
      for (let line = declared.start; line <= declared.end; line++) {
        lit.add(line);
      }
    } else {
      const found = namedIn(declared);
      for (const line of found.keys()) {
        lit.add(line);
      }

      mark(found);
    }
  }

  const firstOf = (held: Iterable<number>) => {
    let first: number | null = null;
    for (const line of held) {
      first = first === null || line < first ? line : first;
    }

    return first;
  };

  return { lit, marks, first: firstOf(marks.keys()) ?? firstOf(lit) };
}

/** A run of one token as a line is drawn, cut where a mark starts or ends, and whether it is marked. */
export interface DrawnRun<T> {
  text: string;
  token: T;
  marked: boolean;
}

/** The runs of a line, each cut where a mark starts or ends, so a mark is drawn across the tokens it covers. */
export function markRuns<T>(runs: readonly { text: string; token: T }[], marks: readonly LineRun[]): DrawnRun<T>[] {
  if (marks.length === 0) {
    return runs.map((run) => ({ ...run, marked: false }));
  }

  const inMark = (at: number) => marks.some((run) => run.start <= at && at < run.end);
  const drawn: DrawnRun<T>[] = [];
  let offset = 0;
  for (const run of runs) {
    for (let at = 0; at < run.text.length; at++) {
      const marked = inMark(offset + at);
      const last = drawn[drawn.length - 1];
      if (last !== undefined && at > 0 && last.marked === marked) {
        last.text += run.text[at];
      } else {
        drawn.push({ text: run.text[at], token: run.token, marked });
      }
    }

    offset += run.text.length;
  }

  return drawn;
}
