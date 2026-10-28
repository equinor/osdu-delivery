// How a record's enrichments read on its Enrichments tab (osdu/docs/reference/flow/enrich.md, What the ledger keeps): what
// each adds, where its rows came from, what tells a record it added from its others, and the writes made of it, each as text.

/** What an enrichment adds and how, as the record's enrichments answer it (DeliveryRecordEnrichment). */
interface EnrichmentAdds {
  adds: readonly string[];
  recordKind: string | null;
  writtenThrough: string | null;
}

/** Where an enrichment's rows came from, as the record's enrichments answer it (DeliveryEnrichmentRows). */
interface EnrichmentRows {
  count: number;
  files: readonly string[];
  filesCounted: number;
  row: string | null;
}

/** One write made of a record an enrichment added, as the record's attempt holds it (DeliveryEnrichmentWrite). */
interface EnrichmentWrite {
  atUtc: string;
  outcome: string;
  version: number | null;
  priorVersion: number | null;
}

/** A kind without its authority and source, `dataset--File.Generic:1.0.0`: the entity type and version a reader knows it by. */
export function shortKind(kind: string): string {
  const parts = kind.split(":");
  return parts.length === 4 ? `${parts[2]}:${parts[3]}` : kind;
}

/** What an enrichment adds, as a short phrase: paths, records, bulk data, a workflow run. */
export function addsText(enrichment: Pick<EnrichmentAdds, "adds">): string {
  const words = enrichment.adds.map((add) => {
    switch (add) {
      case "paths":
        return "paths";
      case "records":
        return "records";
      case "bulk":
        return "bulk data";
      case "workflow":
        return "workflow run";
      default:
        return add;
    }
  });
  return words.length === 0 ? "nothing" : words.join(", ");
}

/**
 * What an enrichment's additions are, as a quiet second line: the kind of the records it adds and how they are written, or
 * where the paths and the bulk data go; null when nothing more is worth saying.
 */
export function addsDetail(enrichment: EnrichmentAdds): string | null {
  if (enrichment.recordKind !== null) {
    const through = enrichment.writtenThrough === "dataset" ? "with files, via the Dataset service" : "via storage";
    return `${shortKind(enrichment.recordKind)} ${through}`;
  }

  if (enrichment.adds.includes("paths")) {
    return "into the record itself";
  }

  return enrichment.adds.includes("bulk") ? "streamed beside the record" : null;
}

/** Where an enrichment's rows came from, in one line: how many, and the one row or the files they were landed from; null for none kept. */
export function rowsText(rows: EnrichmentRows | null): string | null {
  if (rows === null) {
    return null;
  }

  const count = `${rows.count.toLocaleString("en-US")} ${rows.count === 1 ? "row" : "rows"}`;
  if (rows.row !== null) {
    return `${count}: ${rows.row}`;
  }

  if (rows.files.length === 0) {
    return count;
  }

  const more = rows.filesCounted - rows.files.length;
  return `${count} from ${rows.files.join(", ")}${more > 0 ? ` and ${more.toLocaleString("en-US")} more` : ""}`;
}

/**
 * Every write deliveries made of a record an enrichment added, newest first, one per line: when, whether it created the record
 * or replaced a version, the version it landed as, and how the record's try ended when it was not delivered.
 */
export function writesText(writes: readonly EnrichmentWrite[]): string {
  return writes
    .map((write) => {
      const when = `${write.atUtc.replace("T", " ").slice(0, 16)} UTC`;
      const landed = write.version !== null ? `version ${write.version}` : "no version answered";
      const what = write.priorVersion !== null ? `${landed}, replacing ${write.priorVersion}` : `created, ${landed}`;
      const tried = write.outcome === "delivered" ? "" : `; the record's try ended ${write.outcome}`;
      return `${when}: ${what}${tried}`;
    })
    .join("\n");
}

/**
 * What tells a record an enrichment added from the others it added, from its id (`...-enrich.<name>.<key hash>`): the hash
 * of its row's key cut to eight characters, or null for an enrichment adding one record for the record.
 */
export function addedKeyText(id: string, enrichment: string): string | null {
  const marker = `-enrich.${enrichment}.`;
  const at = id.lastIndexOf(marker);
  return at < 0 ? null : id.slice(at + marker.length, at + marker.length + 8);
}
