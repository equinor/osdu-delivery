import { CircleCheck, CircleDashed, CircleSlash, CircleX, PencilLine, Unlink, type LucideIcon } from "lucide-react";
import { parseUtc } from "@/lib/time";
import type {
  DeliveryDimensionView, DeliveryDimensionViewCheck, DeliveryDimensionViewColumnCheck, DeliveryDimensionViewJoinCheck, DeliveryDimensionViewJoinVerdict,
  DimensionViewJoinVerdictKind,
} from "../../../api/delivery";
import { duration } from "../assertions/assertionFormat";
import type { StandingVisual } from "./dimensionFormat";

/**
 * Where a view stands (osdu/docs/reference/flow/dimension-views.md): the database holds it as its flow declares it,
 * its flow declares it differently now, a build dropped it to write it again, no build has written it yet, or no active
 * flow declares it.
 */
export type ViewStanding = "written" | "changed" | "dropped" | "notWritten" | "undeclared";

export const VIEW_STANDING_VISUALS: Record<ViewStanding, StandingVisual> = {
  written: { icon: CircleCheck, label: "Written", tone: "text-success", hint: "The database holds the view as its flow declares it." },
  changed: {
    icon: PencilLine, label: "Changed", tone: "text-warning",
    hint: "Its flow declares it differently from how a build last wrote it: the next build of the flow writes it again.",
  },
  dropped: {
    icon: CircleSlash, label: "Dropped", tone: "text-warning",
    hint: "A build dropped it to write it again, so the database does not hold it now: the next build of the flow writes it.",
  },
  notWritten: {
    icon: CircleDashed, label: "Not written yet", tone: "text-muted-foreground",
    hint: "No build has written it yet: the next build of its flow writes it, after the flow's dimensions.",
  },
  undeclared: {
    icon: Unlink, label: "No longer declared", tone: "text-muted-foreground",
    hint: "No active flow declares it: its flow is gone, or no longer declares it and has not been built since (its next build drops it). An admin can remove it.",
  },
};

/** What the flow's templates say of a join, as a glyph in its tone and a word. */
export const VERDICT_VISUALS: Record<DimensionViewJoinVerdictKind, { icon: LucideIcon; label: string; tone: string }> = {
  agrees: { icon: CircleCheck, label: "agrees", tone: "text-success" },
  differs: { icon: CircleX, label: "differs", tone: "text-destructive" },
  unchecked: { icon: CircleDashed, label: "unchecked", tone: "text-muted-foreground" },
};

/** What the templates say of a join, in the words a tooltip gives: the verdict's sentence, what the column names and what the dimension reads. */
export function verdictText(verdict: DeliveryDimensionViewJoinVerdict): string {
  const lines = [verdict.note];
  if (verdict.names.length > 0) {
    lines.push(`${verdict.on} names: ${verdict.names.join(", ")}`);
  }

  if (verdict.reads != null) {
    lines.push(`${verdict.to} reads: ${verdict.reads}`);
  }

  return lines.join("\n");
}

/** What the templates say of each join, by its alias. */
export function joinVerdicts(verdicts: readonly DeliveryDimensionViewJoinVerdict[] | undefined): Map<string, DeliveryDimensionViewJoinVerdict> {
  return new Map((verdicts ?? []).map((verdict) => [verdict.alias.toLowerCase(), verdict]));
}

/** Where a view stands; one no flow declares comes first, since nothing will write it again. */
export function viewStandingOf(view: DeliveryDimensionView): ViewStanding {
  if (!view.declared) {
    return "undeclared";
  }

  if (!view.recorded) {
    return "notWritten";
  }

  if (!view.written) {
    return "dropped";
  }

  return view.changed ? "changed" : "written";
}

/** What a view stands for in the database: its name in the module's schema. */
export function viewObject(view: { viewName: string }): string {
  return `osdu.${view.viewName}`;
}

/** A dimension table as a person types it. */
export function tableObject(table: string): string {
  return table.includes(".") ? table : `osdu.${table}`;
}

/** An instant as a tooltip says it: to the minute, in UTC. */
export function utcText(value: string): string {
  return `${parseUtc(value).toISOString().slice(0, 16).replace("T", " ")} UTC`;
}

/** Where and when a check ran, and how long it took, as a tooltip says it. */
export function checkWhen(check: DeliveryDimensionViewCheck): string {
  return `Checked ${utcText(check.checkedUtc)} in partition ${check.partition}, in ${duration(check.durationMs)}.`;
}

/** How the page's link names a view, and the tab it opens on. */
export const VIEW_PARAM = "v";
export const VIEW_TAB_PARAM = "vt";

/** The link to a view's page, on the Dimensions page. */
export function viewHref(name: string): string {
  return `/delivery/dimensions?${VIEW_PARAM}=${encodeURIComponent(name)}`;
}

/** The questions a view's page answers, a tab each. */
export type ViewTab = "columns" | "joins" | "checks" | "sql" | "definition";

/** A view's tabs, in order; the first is the one a view opens on. */
export const VIEW_TABS: readonly ViewTab[] = ["columns", "joins", "checks", "sql", "definition"];

/** The tab a link names, the first when it names none or one that is not a tab. */
export function viewTabOf(value: string | null): ViewTab {
  return VIEW_TABS.find((tab) => tab === value) ?? VIEW_TABS[0];
}

/** The two columns every view begins with: the partition and the number of the `from` row, its key. */
export const VIEW_KEY_COLUMNS: readonly string[] = ["partition", "id"];

/** The values a check's conversions could not read, all columns together. */
export function unconvertedOf(check: DeliveryDimensionViewCheck | null | undefined): number {
  return (check?.columns ?? []).reduce((sum, column) => sum + (column.unconverted ?? 0), 0);
}

/** The rows whose join value named no row, all joins together. */
export function unmatchedOf(check: DeliveryDimensionViewCheck | null | undefined): number {
  return (check?.joins ?? []).reduce((sum, join) => sum + join.unmatched, 0);
}

/** A join whose rows held no value to join on at all: the check found neither a match nor a miss in rows it read. */
export function joinFoundNothing(join: DeliveryDimensionViewJoinCheck, rows: number): boolean {
  return rows > 0 && join.matched === 0 && join.unmatched === 0;
}

/** What a check found of a column, by the column's name. */
export function columnFindings(check: DeliveryDimensionViewCheck | null | undefined): Map<string, DeliveryDimensionViewColumnCheck> {
  return new Map((check?.columns ?? []).map((column) => [column.name.toLowerCase(), column]));
}

/** What a check found of a join, by its alias. */
export function joinFindings(check: DeliveryDimensionViewCheck | null | undefined): Map<string, DeliveryDimensionViewJoinCheck> {
  return new Map((check?.joins ?? []).map((join) => [join.alias.toLowerCase(), join]));
}

/** A value a conversion could not read, quoted as the run's notes quote it, with the row it was read in. */
export function exampleText(example: { id: number; value: string }): string {
  return `'${example.value}' (row ${example.id.toLocaleString("en-US")})`;
}

/**
 * What a part of a view's YAML declares, for linking a line to what it makes: `from`, a join by its place (`join.0`), or a
 * column by its name (`columns.TopDepth`); null for the view's name, description and the keys that hold the lists.
 */
export function declaredOf(target: string): string | null {
  const parts = target.split(".");
  if (parts[0] === "from" && parts.length === 1) {
    return "from";
  }

  if ((parts[0] === "join" || parts[0] === "columns") && parts.length >= 2 && parts[1] !== "") {
    return `${parts[0]}.${parts[1]}`;
  }

  return null;
}

/** One view as a dimension run's result reports it: what the build did with it and what its check found, or what a plan would write. */
export interface RunView {
  view: string;
  viewName: string;
  /** `written`, `unchanged` or `failed` for a build; null for a plan, which writes nothing. */
  status: "written" | "unchanged" | "failed" | null;
  error: string | null;
  check: "passed" | "failed" | null;
  rows: number | null;
  notes: string[];
  /** What would stop a build writing it, as a plan reports it. */
  problems: string[];
  /** The statement a plan says a build writes it with. */
  sql: string | null;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function texts(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((text): text is string => typeof text === "string") : [];
}

function text(value: unknown): string | null {
  return typeof value === "string" && value !== "" ? value : null;
}

/**
 * The views a dimension run's result reports, and the views it dropped because its flow no longer declares them; null for
 * a run whose result says nothing of views (one that finished before views were reported, or stopped before them).
 */
export function runViewsOf(result: Record<string, unknown> | null): { views: RunView[]; dropped: string[] } | null {
  if (result === null || (!Array.isArray(result.views) && !Array.isArray(result.viewsDropped))) {
    return null;
  }

  const views = (Array.isArray(result.views) ? result.views : []).filter(isRecord).map((entry): RunView => {
    const status = entry.status === "written" || entry.status === "unchanged" || entry.status === "failed" ? entry.status : null;
    const check = entry.check === "passed" || entry.check === "failed" ? entry.check : null;
    return {
      view: text(entry.view) ?? "",
      viewName: text(entry.viewName) ?? "",
      status,
      error: text(entry.error),
      check,
      rows: typeof entry.rows === "number" && Number.isFinite(entry.rows) ? entry.rows : null,
      notes: texts(entry.notes),
      problems: texts(entry.problems),
      sql: text(entry.sql),
    };
  }).filter((view) => view.view !== "");
  return { views, dropped: texts(result.viewsDropped) };
}

/** Whether a view stopped its run: it could not be written, or its check could not read it. */
export function runViewFailed(view: RunView): boolean {
  return view.status === "failed" || view.check === "failed";
}
