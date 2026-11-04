import type { DeliveryCache, DeliveryCacheField, DeliveryCacheOrigin, DeliveryCacheSchedule, DeliveryCacheTypeSource } from "../../api/delivery";

/** The entity type prefix of a lookup table: a type filled from an ingestion table or a dictionary, holding no OSDU records. */
export const LOOKUP_ENTITY_PREFIX = "lookup--";

/** Whether a cached type is a lookup table, whose rows are kept under their keys rather than OSDU ids. */
export function isLookupEntityType(entityType: string): boolean {
  return entityType.startsWith(LOOKUP_ENTITY_PREFIX);
}

/** What a cached record is identified by: a lookup row by its key, an OSDU record by its id. */
export function recordIdLabel(entityType: string): string {
  return isLookupEntityType(entityType) ? "Key" : "OSDU id";
}

/** A cached value on one line: a scalar as itself, a set as its values, an object as its JSON. */
export function cachedText(value: unknown): string {
  if (value === null || value === undefined) {
    return "-";
  }

  if (Array.isArray(value)) {
    return value.map((v) => cachedText(v)).join(", ");
  }

  return typeof value === "object" ? JSON.stringify(value) : String(value);
}

/** A cached value for a table cell: null where the record holds nothing under the name, so the cell shows its placeholder. */
export function cachedCell(value: unknown): string | null {
  return value === null || value === undefined ? null : cachedText(value);
}

/** Every captured value of a cached record on one line, name by name. */
export function cachedFieldsText(fields: Record<string, unknown> | null): string {
  return Object.entries(fields ?? {}).map(([name, value]) => `${name}: ${cachedText(value)}`).join("  ·  ") || "-";
}

/**
 * How a mapping reads a cached value: `$cache: <Type>.<name>` on the property it fills, where the name is what the cache
 * flow caches a path under, or id for the record's OSDU id. The partition is never named: a delivery flow reads the cache of
 * the partition it delivers to.
 */
export function cacheReference(typeName: string, name: string): string {
  return `$cache: ${typeName}.${name}`;
}

/** The name a mapping reads a cached record's OSDU id under. */
export const CACHE_ID_FIELD = "id";

/**
 * The node a property of the record is written as to hold a cached record's OSDU id, found by one of its captured values
 * matching a column of the row: what a mapping author starts from for a type. The column is a placeholder the author renames.
 */
export function cacheEntryExample(typeName: string, lookupField: string | null): string {
  const lines = [cacheReference(typeName, CACHE_ID_FIELD)];
  if (lookupField !== null) {
    lines.push(`$findBy: ${lookupField} = <column>`);
  }

  return lines.join("\n");
}

/**
 * The node that reads a value of a lookup table's row, found by its key matching a column of the row. A lookup row has no
 * OSDU id, so the node reads one of its values; the column is a placeholder the author renames.
 */
export function lookupEntryExample(typeName: string, keyField: string, valueField: string | null): string {
  return [
    cacheReference(typeName, valueField ?? keyField),
    `$findBy: ${keyField} = <column>`,
  ].join("\n");
}

/**
 * A replace modifier that turns a dataset value into what a lookup table gives for it, on its way to the property: matched
 * on the table's key and replaced by the one value a row holds beside it; a table holding several names the one it replaces by.
 */
export function lookupReplaceExample(typeName: string, keyField: string, valueFields: string[]): string {
  const beside = valueFields.filter((name) => name !== keyField);
  const lines = ["$modifiers:", `  - replace: $cache.${typeName}`];
  if (beside.length > 1) {
    lines.push(`    field: ${beside[0]}`);
  }

  return lines.join("\n");
}

/**
 * An OSDU record id in two parts: the partition and entity type that every id of a type repeats, and the part that
 * tells the records apart. A column of ids reads by its tails once the repeated prefix steps back.
 */
export function splitRecordId(id: string): { prefix: string; tail: string } {
  const at = id.lastIndexOf(":");
  if (at < 0 || at === id.length - 1) {
    return { prefix: "", tail: id };
  }

  return { prefix: id.slice(0, at + 1), tail: id.slice(at + 1) };
}

/**
 * What tells a cached record apart within its type, as a reader would say it: a lookup row's key, or the part of an OSDU
 * id past its partition and entity type with its escapes undone (UnitOfMeasure:%25 is "%"). A value a record holds that
 * reads the same only repeats the record's identity.
 */
export function recordIdentity(recordId: string, entityType: string): string {
  if (isLookupEntityType(entityType)) {
    return recordId;
  }

  const { tail } = splitRecordId(recordId);
  try {
    return decodeURIComponent(tail);
  } catch {
    // A lone % or a broken escape is not an escape: the tail is read as it is written.
    return tail;
  }
}

/** One run of a cached value as a cell draws it: a percent escape, a stretch the search matched, both, or plain text. */
export interface ValueSegment {
  text: string;
  /** Part of a percent escape (%20, %5B), kept as captured but stepped back so the words between read. */
  escape: boolean;
  /** Part of a stretch the search term matched, regardless of case. */
  match: boolean;
}

const PERCENT_ESCAPE = /%[0-9A-Fa-f]{2}/g;

/**
 * A cached value cut into the runs a cell draws. The value is never changed: an id minted from a code keeps its escapes
 * (Resistivity%20-%20Deep), and a value copied from the cell must be the value a mapping reads. The escapes are marked so
 * the cell can step them back, and the stretches the search matched are marked so a row says why the search found it.
 */
export function valueSegments(text: string, search: string): ValueSegment[] {
  if (text === "") {
    return [];
  }

  const escape = new Uint8Array(text.length);
  for (const found of text.matchAll(PERCENT_ESCAPE)) {
    escape.fill(1, found.index, found.index + found[0].length);
  }

  // A search is matched regardless of case, as the cache search is. Lowercasing can change a string's length (a dotted
  // capital I becomes two code units), and then no index of the lowered text points into the value: nothing is marked.
  const match = new Uint8Array(text.length);
  const term = search.trim().toLowerCase();
  const lower = text.toLowerCase();
  if (term !== "" && lower.length === text.length) {
    for (let at = lower.indexOf(term); at >= 0; at = lower.indexOf(term, at + term.length)) {
      match.fill(1, at, at + term.length);
    }
  }

  const segments: ValueSegment[] = [];
  let start = 0;
  for (let at = 1; at <= text.length; at++) {
    if (at === text.length || escape[at] !== escape[start] || match[at] !== match[start]) {
      segments.push({ text: text.slice(start, at), escape: escape[start] === 1, match: match[start] === 1 });
      start = at;
    }
  }

  return segments;
}

/**
 * A platform feature flag's name in three parts: the affixes the services repeat on most names (featureFlag. before,
 * .enabled or -enabled after) and the part that says what the flag is (keywordLower, index-augmenter).
 */
export function splitFlagName(name: string): { head: string; core: string; tail: string } {
  const parts = /^(featureFlag\.)?(.+?)([.-]enabled)?$/.exec(name);
  return parts === null
    ? { head: "", core: name, tail: "" }
    : { head: parts[1] ?? "", core: parts[2], tail: parts[3] ?? "" };
}

/** The family an entity type belongs to, as a group heading: reference-data--UnitOfMeasure is "Reference data". */
export function entityFamily(entityType: string): string {
  if (isLookupEntityType(entityType)) {
    return "Lookup tables";
  }

  const at = entityType.indexOf("--");
  if (at <= 0) {
    return "Other";
  }

  const words = entityType.slice(0, at).replace(/-/g, " ");
  return words.charAt(0).toUpperCase() + words.slice(1);
}

/** Families in the order the type picker lists them: what mappings look up first, then the master data they point at. */
const familyRank: Record<string, number> = { "Reference data": 0, "Master data": 1, "Lookup tables": 2 };

export function compareFamilies(a: string, b: string): number {
  const rankA = familyRank[a] ?? 3;
  const rankB = familyRank[b] ?? 3;
  return rankA !== rankB ? rankA - rankB : a.localeCompare(b);
}

/** One type of the partition's cache, as the Definition tab and the type picker show it. */
export interface CachedTypeSummary {
  name: string;
  entityType: string;
  family: string;
  /** Every cache flow's declaration of the type: where it takes the records from. */
  sources: DeliveryCacheTypeSource[];
  /** Where the records come from: searched on OSDU, read from an ingestion table, or held from a dictionary. */
  origin: DeliveryCacheOrigin;
  /** For a lookup table, the name each row's key is kept under; null for OSDU records. */
  key: string | null;
  /** The records the current version holds of the type. */
  items: number;
  /** Every path the cache keeps for the type, by the name it is cached under, with the flows that declare it. */
  fields: DeliveryCacheField[];
  /** approve or auto: what a changed value does to the records built from it. */
  onChange: string;
  /** For an OSDU type: its records are read whole from storage, not from the search index's projection. */
  fetchRecords: boolean;
}

/** The types a partition's cache holds, sorted the way the type picker lists them: by family, then by name. */
export function summarizeTypes(cache: DeliveryCache | null): CachedTypeSummary[] {
  return (cache?.types ?? [])
    .map((type) => ({
      name: type.name,
      entityType: type.entityType,
      family: entityFamily(type.entityType),
      sources: type.sources,
      origin: type.origin,
      key: type.key,
      items: type.items,
      fields: type.fields,
      onChange: type.onChange,
      fetchRecords: type.fetchRecords,
    }))
    .sort((a, b) => compareFamilies(a.family, b.family) || a.name.localeCompare(b.name));
}

/** A schedule's cadence in words: its cron, its interval, or that it fires behind other schedules. */
export function scheduleCadence(schedule: DeliveryCacheSchedule): string {
  if (schedule.cron !== null && schedule.cron.trim() !== "") {
    return `${schedule.name}: cron ${schedule.cron}`;
  }

  if (schedule.intervalSeconds !== null) {
    return `${schedule.name}: every ${schedule.intervalSeconds}s`;
  }

  return schedule.chained ? `${schedule.name}: after the schedules it follows` : schedule.name;
}
