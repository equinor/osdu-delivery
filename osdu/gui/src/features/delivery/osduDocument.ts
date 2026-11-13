// Reading OSDU records side by side: the fields OSDU keeps on a record it stores, a document with its keys in order so two
// documents compare by content rather than by the order they were written in, the differences between two documents
// leaf by leaf, and the OSDU records a document refers to.

/** The top-level fields OSDU keeps on a record it stores, which no delivery sends. */
export const OSDU_OWNED_FIELDS = ["version", "createUser", "createTime", "modifyUser", "modifyTime"] as const;

/** A JSON value whose objects list their keys in order, at every depth. */
export function sortedJson(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map(sortedJson);
  }

  if (value !== null && typeof value === "object") {
    return Object.fromEntries(Object.keys(value).sort().map((key) => [key, sortedJson((value as Record<string, unknown>)[key])]));
  }

  return value;
}

/** The top-level fields of a record in the order OSDU writes them, which is the order a reader looks for them in. */
const ENVELOPE = ["id", "kind", "version", "acl", "legal", "ancestry", "meta", "tags", "data"] as const;

/**
 * A record with its envelope first (id, kind, version, acl, legal and so on, then data) and any other top-level field
 * after it, the order OSDU writes a record in. A rendered document is canonical, its keys in order, which puts id and
 * kind at the bottom; a reader looks for them at the top. Only the top level moves; what is under it is as it was.
 */
export function envelopeFirst(record: Record<string, unknown>): Record<string, unknown> {
  const envelope = new Set<string>(ENVELOPE);
  return Object.fromEntries([
    ...ENVELOPE.filter((key) => key in record).map((key) => [key, record[key]] as const),
    ...Object.keys(record).filter((key) => !envelope.has(key)).sort().map((key) => [key, record[key]] as const),
  ]);
}

/** Indented JSON with every object's keys in order, the envelope first: the form two documents are compared in. */
export function canonicalText(value: unknown): string {
  const sorted = sortedJson(value);
  const ordered = sorted !== null && typeof sorted === "object" && !Array.isArray(sorted)
    ? envelopeFirst(sorted as Record<string, unknown>)
    : sorted;
  return JSON.stringify(ordered, null, 2);
}

/** The record without the fields OSDU keeps on it: what a delivery sends, as OSDU holds it. */
export function withoutOsduFields(record: Record<string, unknown>): Record<string, unknown> {
  const owned = new Set<string>(OSDU_OWNED_FIELDS);
  return Object.fromEntries(Object.entries(record).filter(([key]) => !owned.has(key)));
}

/** The fields OSDU keeps on the record, those it holds. */
export function osduFields(record: Record<string, unknown>): { name: string; value: unknown }[] {
  return OSDU_OWNED_FIELDS.filter((name) => name in record).map((name) => ({ name, value: record[name] }));
}

/** How one leaf of two documents differs. */
export type DifferenceKind = "changed" | "onlyInOsdu" | "onlyInPreview" | "placeholder";

export interface JsonDifference {
  /** Where, as a path: data.Name, data.Curves[3].Mnemonic. */
  path: string;
  kind: DifferenceKind;
  osdu?: unknown;
  preview?: unknown;
}

/** A value the preview shows for what the platform gives when the record is sent: "<...>" or "<...>:". */
export function isPlaceholder(value: unknown): boolean {
  return typeof value === "string" && value.startsWith("<") && (value.endsWith(">") || value.endsWith(">:"));
}

/** Nothing worth a difference: a value absent on one side and empty on the other, as storage writes an empty ancestry or tags. */
function isEmpty(value: unknown): boolean {
  return value === undefined
    || value === null
    || (Array.isArray(value) && value.length === 0)
    || (typeof value === "object" && value !== null && !Array.isArray(value) && Object.keys(value).length === 0);
}

function isContainer(value: unknown): value is Record<string, unknown> | unknown[] {
  return value !== null && typeof value === "object";
}

function childPath(path: string, key: string | number): string {
  return typeof key === "number" ? `${path}[${key}]` : path === "" ? key : `${path}.${key}`;
}

/**
 * The differences between what OSDU holds and what a delivery would send now, leaf by leaf, at most `max` of them. Arrays
 * compare by position, since a record's lists are ordered. A value only one side holds counts only when the other side
 * does not hold it as empty, and a placeholder the preview shows for a value the platform gives is named as one rather
 * than as a change.
 */
export function differences(osdu: unknown, preview: unknown, max = 500): { items: JsonDifference[]; truncated: boolean } {
  const items: JsonDifference[] = [];
  let truncated = false;
  const add = (difference: JsonDifference) => {
    if (items.length < max) {
      items.push(difference);
    } else {
      truncated = true;
    }
  };

  const walk = (left: unknown, right: unknown, path: string) => {
    if (truncated) {
      return;
    }

    if (left === undefined || right === undefined) {
      if (isEmpty(left) && isEmpty(right)) {
        return;
      }

      if (left === undefined) {
        add({ path, kind: isPlaceholder(right) ? "placeholder" : "onlyInPreview", preview: right });
      } else {
        add({ path, kind: "onlyInOsdu", osdu: left });
      }

      return;
    }

    if (isContainer(left) && isContainer(right) && Array.isArray(left) === Array.isArray(right)) {
      if (Array.isArray(left) && Array.isArray(right)) {
        for (let index = 0; index < Math.max(left.length, right.length); index++) {
          walk(left[index], right[index], childPath(path, index));
        }

        return;
      }

      const l = left as Record<string, unknown>;
      const r = right as Record<string, unknown>;
      for (const key of [...new Set([...Object.keys(l), ...Object.keys(r)])].sort()) {
        walk(l[key], r[key], childPath(path, key));
      }

      return;
    }

    if (JSON.stringify(left) !== JSON.stringify(right)) {
      add({ path, kind: isPlaceholder(right) ? "placeholder" : "changed", osdu: left, preview: right });
    }
  };

  walk(osdu, preview, "");
  return { items, truncated };
}

/**
 * Whether a text reads as a reference to an OSDU record: a partition, an entity type (group--Entity) and a unique part,
 * with or without the version. The same test the control plane applies before it reads one.
 */
export function isRecordReference(value: unknown): value is string {
  if (typeof value !== "string" || value.trim() === "" || /\s/.test(value)) {
    return false;
  }

  const parts = value.split(":");
  const at = parts.length >= 3 ? parts[1].indexOf("--") : -1;
  return parts.length >= 3 && parts[0].length > 0 && at > 0 && at < parts[1].length - 2 && parts[2].length > 0;
}

/** The storage service's record id (os-core-common ValidationDoc.RECORD_ID_REGEX): no group asked of the middle part. */
const STORAGE_ID = /^[A-Za-z0-9_\-.]+:[A-Za-z0-9_\-.]+:[A-Za-z0-9_\-.:%]+$/;

/**
 * Whether a text names a record to open in `partition`: a reference as {@link isRecordReference} reads one, or any id the
 * storage service takes that starts with the partition, so a record of a partition's own type naming no group
 * (dev:site-note:3b9c...) opens as well. Elsewhere three colon-separated parts may be any text.
 */
export function isRecordIdIn(value: unknown, partition: string | null): value is string {
  if (isRecordReference(value)) {
    return true;
  }

  return typeof value === "string" && partition !== null && value.toLowerCase().startsWith(`${partition.toLowerCase()}:`) && STORAGE_ID.test(value);
}

/** A reference without its version: p:t:k: and p:t:k:123 become p:t:k, as the control plane reads it. */
export function withoutVersion(reference: string): string {
  const last = reference.lastIndexOf(":");
  if (last < 0) {
    return reference;
  }

  const tail = reference.slice(last + 1);
  const colons = reference.split(":").length - 1;
  return colons >= 3 && /^\d*$/.test(tail) ? reference.slice(0, last) : reference;
}

/** An OSDU record a document refers to, and where. */
export interface RecordReferenceAt {
  id: string;
  paths: string[];
}

/**
 * The OSDU records a document refers to: every text value that reads as a record reference, by the record it names (its
 * version set aside), with the paths it is found at. The record's own id and kind are not references, and a reference to
 * the record itself is left out.
 */
export function recordReferences(document: Record<string, unknown>, ownId?: string | null, max = 200): RecordReferenceAt[] {
  const found = new Map<string, string[]>();
  const own = ownId ? withoutVersion(ownId) : null;
  const walk = (value: unknown, path: string) => {
    if (found.size >= max) {
      return;
    }

    if (Array.isArray(value)) {
      value.forEach((item, index) => walk(item, childPath(path, index)));
    } else if (value !== null && typeof value === "object") {
      for (const [key, item] of Object.entries(value)) {
        if (path === "" && (key === "id" || key === "kind")) {
          continue;
        }

        walk(item, childPath(path, key));
      }
    } else if (isRecordReference(value)) {
      const id = withoutVersion(value);
      if (id !== own) {
        found.set(id, [...(found.get(id) ?? []), path]);
      }
    }
  };

  walk(document, "");
  return [...found.entries()].map(([id, paths]) => ({ id, paths }));
}

/** A value as one short line for a table cell: text as it is, anything else as compact JSON, cut at `max`. */
export function shortValue(value: unknown, max = 160): string {
  const text = value === undefined ? "" : typeof value === "string" ? value : JSON.stringify(value);
  return text.length > max ? `${text.slice(0, max)}...` : text;
}

/** A byte count as people read it. */
export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) {
    return "-";
  }

  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }

  return unit === 0 ? `${bytes} B` : `${value.toFixed(value >= 100 ? 0 : 1)} ${units[unit]}`;
}

/** Offers a JSON document as a file to save, as the browser saves a download. */
export function downloadJson(fileName: string, value: unknown): void {
  const blob = new Blob([JSON.stringify(value, null, 2)], { type: "application/json;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = fileName;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}

/** A file name made of the parts given, safe on every file system. */
export function fileNameOf(...parts: (string | null | undefined)[]): string {
  const name = parts.filter((part): part is string => typeof part === "string" && part.trim() !== "").join("-");
  return `${name.replace(/[^A-Za-z0-9._-]+/g, "_").replace(/_+/g, "_").slice(0, 120) || "record"}.json`;
}
