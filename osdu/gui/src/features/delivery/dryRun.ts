import { CircleCheck, CircleDashed, CircleMinus, OctagonAlert, TriangleAlert, type LucideIcon } from "lucide-react";
import type {
  DeliveryDryRun,
  DeliveryDryRunOutcome,
  DeliveryDryRunRecord,
  DeliverySchemaCrossMatch,
  DeliveryStagedRow,
} from "../../api/delivery";
import { formatCount } from "./valueCheck";

/** How an outcome of a dry run reads wherever one is shown: its word, its glyph in its tone, and what it means for the record. */
export interface DryRunVisual {
  label: string;
  icon: LucideIcon;
  tone: string;
  meaning: string;
}

export const DRY_RUN_OUTCOMES: Record<DeliveryDryRunOutcome, DryRunVisual> = {
  ready: {
    label: "Ready",
    icon: CircleCheck,
    tone: "text-success",
    meaning: "A delivery would send this record, and nothing was found wrong with it.",
  },
  attention: {
    label: "Worth a look",
    icon: TriangleAlert,
    tone: "text-warning",
    meaning: "A delivery would send this record, but something about it should be looked at first: a schema rule it breaks, an assertion it fails, or a record it refers to that nothing holds.",
  },
  held: {
    label: "Held",
    icon: OctagonAlert,
    tone: "text-destructive",
    meaning: "A delivery would not send this record: a value it needs could not be made, or a check before sending would stop it.",
  },
  passedOver: {
    label: "Passed over",
    icon: CircleMinus,
    tone: "text-muted-foreground",
    meaning: "A delivery never renders this row: the ingestion table marks it deleted, or the source no longer holds its key.",
  },
};

/** A row nothing has rendered yet. */
export const NOT_RUN: DryRunVisual = {
  label: "Not rendered yet",
  icon: CircleDashed,
  tone: "text-muted-foreground/60",
  meaning: "This row has not been rendered. Pick it to see its document, or run a dry run.",
};

/** One row as the list shows it: what is staged, and what a dry run made of it where one has. */
export interface StagedLine {
  /** What the line is known by, and what a dry run matches it on. */
  sourceKey: string;
  /** The key's parts as JSON, which names the row to a preview; null for a row whose key is incomplete. */
  keyJson: string | null;
  label: string | null;
  file: string | null;
  row: number | null;
  deletedUtc: string | null;
  children: Record<string, number>;
  result: DeliveryDryRunRecord | null;
}

/** The line of a row a page of staged rows listed, with what the dry run made of it. */
export function lineOfRow(row: DeliveryStagedRow, results: ReadonlyMap<string, DeliveryDryRunRecord> | null): StagedLine {
  return {
    sourceKey: row.sourceKey,
    keyJson: row.complete ? row.keyJson : null,
    label: row.label ?? null,
    file: row.file ?? null,
    row: row.row ?? null,
    deletedUtc: row.deletedUtc ?? null,
    children: row.children ?? {},
    result: results?.get(row.sourceKey) ?? null,
  };
}

/** The line of a row only a dry run named: one past the rows paged in so far, picked by its outcome. */
export function lineOfResult(result: DeliveryDryRunRecord): StagedLine {
  return {
    sourceKey: result.sourceKey,
    keyJson: result.keyJson ?? null,
    label: result.label ?? null,
    file: result.file ?? null,
    row: result.row ?? null,
    deletedUtc: null,
    children: {},
    result,
  };
}

/** The line of a row known by its source key alone, which a preview reads as a key. */
export function lineOfKey(sourceKey: string): StagedLine {
  return { sourceKey, keyJson: sourceKey, label: null, file: null, row: null, deletedUtc: null, children: {}, result: null };
}

/** What a dry run said of each row, by the name the rows go by, for a list of any length to look up in constant time. */
export function resultsOf(run: DeliveryDryRun | null): ReadonlyMap<string, DeliveryDryRunRecord> | null {
  return run === null ? null : new Map(run.records.map((record) => [record.sourceKey, record]));
}

/** What a line says under its name: why it came to what it did, else what the row holds in its child tables, else where it came from. */
export function lineDetail(line: StagedLine): string {
  if (line.keyJson === null) {
    return "A part of its key is empty, so it has no record";
  }

  if (line.result?.reason) {
    return line.result.reason;
  }

  if (line.deletedUtc !== null) {
    return "Marked deleted in the record table";
  }

  const children = Object.entries(line.children);
  if (children.length > 0) {
    return children.map(([dataset, rows]) => `${dataset} ${formatCount(rows)}`).join(", ");
  }

  return line.file ?? "";
}

/** The first sentence of a dry run: what a reader came to ask, whether these rows can be delivered. */
export function verdictSentence(run: DeliveryDryRun): string {
  const { rows, held, attention, passedOver } = run.outcomes;
  const counted = `${formatCount(rows)} ${rows === 1 ? "row" : "rows"}`;
  if (rows === 0) {
    return "No row was read, so nothing was rendered.";
  }

  if (passedOver === rows) {
    return `Every one of the ${counted} is deleted or kept out, so a delivery would render none of them.`;
  }

  if (held > 0) {
    return `${formatCount(held)} of ${counted} would not be delivered${attention > 0 ? `, and ${formatCount(attention)} more ${attention === 1 ? "is" : "are"} worth a look` : ""}.`;
  }

  if (attention > 0) {
    return `All ${counted} would be delivered, and ${formatCount(attention)} ${attention === 1 ? "is" : "are"} worth a look first.`;
  }

  return `All ${counted} would be delivered as they are.`;
}

/** What the next run would do with the rows read, in a line; nothing for a count of none. */
export function nextRunLine(run: DeliveryDryRun): string {
  const { create, update, unchanged, hold } = run.nextRun;
  const parts = [
    create > 0 ? `${formatCount(create)} new` : null,
    update > 0 ? `${formatCount(update)} to update` : null,
    unchanged > 0 ? `${formatCount(unchanged)} unchanged` : null,
    hold > 0 ? `${formatCount(hold)} held` : null,
  ].filter((part): part is string => part !== null);
  return parts.length === 0 ? "nothing to do" : parts.join(", ");
}

/** What a check against the Schema service came to, as a tone, a glyph and a few words. */
export interface SchemaMatchVisual {
  word: string;
  icon: LucideIcon;
  tone: string;
}

export function schemaMatchVisual(match: DeliverySchemaCrossMatch): SchemaMatchVisual {
  switch (match.state) {
    case "matches":
      return { word: match.status === "OBSOLETE" ? "Matches the template, but the schema is obsolete" : "Matches the template", icon: match.status === "OBSOLETE" ? TriangleAlert : CircleCheck, tone: match.status === "OBSOLETE" ? "text-warning" : "text-success" };
    case "read":
      return { word: "Schema read", icon: CircleCheck, tone: "text-success" };
    case "differs":
      return {
        word: `Differs from the template in ${formatCount(match.differenceCount)} ${match.differenceCount === 1 ? "property" : "properties"}`,
        icon: TriangleAlert,
        tone: match.breakingCount > 0 ? "text-destructive" : "text-warning",
      };
    case "unregistered":
      return { word: "This kind is not registered", icon: TriangleAlert, tone: "text-warning" };
    case "unavailable":
      return { word: "Could not be read", icon: TriangleAlert, tone: "text-warning" };
    default:
      return { word: "Not an OSDU record", icon: CircleMinus, tone: "text-muted-foreground" };
  }
}

/** What a reader is told of a check against the Schema service, for a hover: what it is, and everything it said. */
export function schemaMatchDetail(match: DeliverySchemaCrossMatch): string {
  const facts = [
    match.why ?? null,
    match.status ? `${match.kind} is ${match.status}${match.scope ? `, ${match.scope}` : ""} in the partition's Schema service.` : null,
    match.schemasRead > 0 ? `Bundling it read ${formatCount(match.schemasRead)} ${match.schemasRead === 1 ? "schema" : "schemas"}.` : null,
    match.unresolved.length > 0 ? `References not followed, whose values count as not checked:\n${match.unresolved.join("\n")}` : null,
  ];
  return facts.filter((fact): fact is string => fact !== null).join("\n\n");
}
