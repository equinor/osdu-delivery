// What a data check needs of a record (osdu/docs/reference/concepts/gui.md, Mappings): where an attribute landed in its
// document or is left out of it, what its source row holds for the attribute, the records a reason of the check lists, and
// the workbook of its issues as a file to save.

import type { RecordNode } from "./osduRecordModel.ts";

/** How a template path starts; a document's own paths begin at what follows it. */
const TEMPLATE_PREFIX = "osdu.";

/** How a mapping names a source column: `dataset.column`, or `dataset.child.column` for a column of a child dataset. */
const DATASET_PREFIX = "dataset.";

/** The media type of a workbook. */
const WORKBOOK_TYPE = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

/**
 * Where a template variable landed in a document: the path of each value written to it, one for every item of a repeated
 * array (`osdu.data.Aliases[].Name` is `data.Aliases[0].Name`, `data.Aliases[1].Name` and so on). None when the document
 * has no such value, which is what a value left out of it comes to.
 */
export function pathsOfVariable(document: RecordNode, variable: string): string[] {
  const wanted = variable.startsWith(TEMPLATE_PREFIX) ? variable.slice(TEMPLATE_PREFIX.length) : variable;
  const found: string[] = [];
  const walk = (node: RecordNode) => {
    if (node.path.replace(/\[\d+\]/g, "[]") === wanted) {
      found.push(node.path);
    }

    node.children.forEach(walk);
  };
  walk(document);
  return found;
}

/**
 * Where a template variable is missing from a document: for each object that should hold it and does not (each item of a
 * repeated array without it), its path and what it lacks. What a reader needs to see of a value left out, which the document
 * itself cannot show. Empty when every place that could hold it does.
 */
export function missingFromDocument(document: RecordNode, variable: string): ReadonlyMap<string, string> {
  const steps = (variable.startsWith(TEMPLATE_PREFIX) ? variable.slice(TEMPLATE_PREFIX.length) : variable).split(".");
  const missing = new Map<string, string>();
  const walk = (node: RecordNode, at: number) => {
    if (node.kind !== "object") {
      return;
    }

    const step = steps[at];
    const repeated = step.endsWith("[]");
    const key = repeated ? step.slice(0, -2) : step;
    const child = node.children.find((candidate) => candidate.key === key);
    if (child === undefined) {
      missing.set(node.path, steps.slice(at).join("."));
    } else if (at + 1 < steps.length) {
      (repeated ? child.children : [child]).forEach((next) => walk(next, at + 1));
    }
  };
  walk(document, 0);
  return missing;
}

/**
 * The branches of a document to fold so the places named stay in view: every one that does not hold one of them. The document
 * stays whole, its way down to those places open, so where they are in it can be seen at a glance.
 */
export function branchesToFold(document: RecordNode, places: readonly string[]): ReadonlySet<string> {
  const kept = new Set(places);
  const fold = new Set<string>();
  const walk = (node: RecordNode): boolean => {
    let holds = kept.has(node.path);
    for (const child of node.children) {
      holds = walk(child) || holds;
    }

    if (!holds && node.kind !== "leaf") {
      fold.add(node.path);
    }

    return holds;
  };
  walk(document);
  return fold;
}

/** An example record of a reason, as far as this module reads one. */
interface ReasonSample {
  sourceKey: string;
  label?: string | null;
  item?: number | null;
}

/** A record a reason lists, with each of its examples: an item of a repeated array is an example of its own. */
export interface ListedRecord<Sample extends ReasonSample> {
  sourceKey: string;
  samples: Sample[];
}

/** The records a reason lists, each once, in the order the scope is read: the ones a reader steps through. */
export function recordsOfFinding<Sample extends ReasonSample>(samples: readonly Sample[]): ListedRecord<Sample>[] {
  const listed = new Map<string, ListedRecord<Sample>>();
  for (const sample of samples) {
    const record = listed.get(sample.sourceKey) ?? { sourceKey: sample.sourceKey, samples: [] };
    listed.set(sample.sourceKey, record);
    record.samples.push(sample);
  }

  return [...listed.values()];
}

/** A source column as a mapping names it, read apart: the child dataset it is a column of, if any, and the column. */
export function sourceColumn(named: string): { child: string | null; column: string } {
  const name = named.startsWith(DATASET_PREFIX) ? named.slice(DATASET_PREFIX.length) : named;
  const dot = name.indexOf(".");
  return dot < 0 ? { child: null, column: name } : { child: name.slice(0, dot), column: name.slice(dot + 1) };
}

/** A row's columns split into those an attribute reads, in the order it reads them, and the rest. Column names are matched whatever their case, as the source's are. */
export function columnsOfRow(row: Readonly<Record<string, string | null>>, reads: readonly string[]): { read: string[]; others: string[] } {
  const columns = Object.keys(row);
  const read = reads.flatMap((wanted) => columns.filter((column) => column.toLowerCase() === wanted.toLowerCase()));
  return { read, others: columns.filter((column) => !read.includes(column)) };
}

/** The workbook a node answered with, Base64 in its result, as a file to save. */
export function workbookBlob(base64: string): Blob {
  const text = atob(base64);
  const bytes = new Uint8Array(text.length);
  for (let i = 0; i < text.length; i++) {
    bytes[i] = text.charCodeAt(i);
  }

  return new Blob([bytes], { type: WORKBOOK_TYPE });
}
