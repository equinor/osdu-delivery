/** What a search reads of a mapping: what its row shows. */
interface SearchedMapping {
  reference: string;
  kind: string;
  relativePath: string;
  templateVersion: string;
  summary: Readonly<Record<string, unknown>>;
  /** Why the mapping does not load, which its row shows in place of its description; absent or null when it loads. */
  message?: string | null;
}

/** The page of a mapping. */
export function mappingRoute(id: string): string {
  return `/delivery/documents/${encodeURIComponent(id)}`;
}

/** One value of a mapping's summary as text: a list's values joined; empty when the sync left it out. */
export function summaryText(summary: Readonly<Record<string, unknown>>, key: string): string {
  const value = summary[key];
  if (value === undefined || value === null) {
    return "";
  }

  return Array.isArray(value) ? value.join(", ") : String(value);
}

/** Everything a mapping's row shows, as the text a search reads, so what a reader sees on a row is what finds it. */
export function mappingSearchText(mapping: SearchedMapping): string {
  return [
    mapping.reference,
    mapping.kind,
    mapping.relativePath,
    mapping.templateVersion,
    summaryText(mapping.summary, "system"),
    summaryText(mapping.summary, "key"),
    summaryText(mapping.summary, "description"),
    mapping.message ?? "",
  ].join(" ");
}

/**
 * The mappings a search names: every word typed is found in what the row shows, in any order and ignoring case.
 * `context` is what the grid shows of a mapping beyond its own row, such as the repository its group is headed by.
 */
export function matchingMappings<T extends SearchedMapping>(mappings: readonly T[], search: string, context?: (mapping: T) => string): T[] {
  const words = search.toLowerCase().split(/\s+/).filter((word) => word !== "");
  if (words.length === 0) {
    return [...mappings];
  }

  return mappings.filter((mapping) => {
    const text = `${mappingSearchText(mapping)} ${context?.(mapping) ?? ""}`.toLowerCase();
    return words.every((word) => text.includes(word));
  });
}
