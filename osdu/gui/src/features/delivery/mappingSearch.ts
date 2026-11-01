/** What a search reads of a mapping: what its row shows. */
interface SearchedMapping {
  reference: string;
  kind: string;
  relativePath: string;
  templateVersion: string;
  summary: Readonly<Record<string, unknown>>;
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
  ].join(" ");
}

/** The mappings a search names: every word typed is found in what the row shows, in any order and ignoring case. */
export function matchingMappings<T extends SearchedMapping>(mappings: readonly T[], search: string): T[] {
  const words = search.toLowerCase().split(/\s+/).filter((word) => word !== "");
  return mappings.filter((mapping) => {
    const text = mappingSearchText(mapping).toLowerCase();
    return words.every((word) => text.includes(word));
  });
}
