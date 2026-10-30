// What a data check needs of a record (osdu/docs/reference/concepts/gui.md, Mappings): where an attribute lands in its
// document, what its source row holds for the attribute, the records the check lists for it, and the workbook of its issues
// as a file to save.

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

/** The findings of an attribute, as far as this module reads them. */
interface FindingRecords {
  outcome: string;
  samples: readonly { sourceKey: string; label?: string | null; item?: number | null }[];
}

/** A record a check lists for an attribute, with the child rows it found wrong, counting from 1. */
export interface ListedRecord {
  sourceKey: string;
  label: string | null;
  items: number[];
}

/**
 * The records a check lists for an attribute, each once, in the order its findings are read, the largest first: the ones a
 * reader steps through to see what those rows render to and what they hold. A row the mapping means no value for has nothing
 * wrong with it.
 */
export function recordsOfFindings(findings: readonly FindingRecords[]): ListedRecord[] {
  const listed = new Map<string, ListedRecord>();
  for (const finding of findings) {
    if (finding.outcome !== "notApplicable") {
      for (const sample of finding.samples) {
        const record = listed.get(sample.sourceKey) ?? { sourceKey: sample.sourceKey, label: sample.label ?? null, items: [] };
        listed.set(sample.sourceKey, record);
        if (sample.item !== null && sample.item !== undefined && !record.items.includes(sample.item)) {
          record.items.push(sample.item);
        }
      }
    }
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
