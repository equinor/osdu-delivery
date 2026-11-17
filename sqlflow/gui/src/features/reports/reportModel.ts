// The pure half of a report's page: how a query's answer becomes figures, bars, columns, a line, a share bar, a matrix
// or a table; how a value is written; which values a query is bound with; and how the page is laid out. Kept free of
// React and of the app's path aliases so the unit suite (gui/unit) can run it under Node directly.

import type {
  QueryResultColumn, Report, ReportColumn, ReportFormat, ReportParameter, ReportParameterType, ReportQuery, ReportView,
  ReportWidth,
} from "../../api/types.ts";
import { axisLabels, classify, toNumber, type ColumnKind } from "../chat/queryResultPlan.ts";

/** One row of an answer: a value per column, as text, null for SQL NULL. */
export type Row = readonly (string | null)[];

// ---- Names ---------------------------------------------------------------------------------------------------------

/**
 * A name as a person reads it: `by_source` and `per-day` and `LastLoaded` become "By source", "Per day" and "Last
 * loaded". A word written all in capitals is kept (an abbreviation). Used where a document gives a name and no title.
 */
export function humanize(name: string): string {
  const words = name
    .replace(/[_-]+/g, " ")
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .trim()
    .split(/\s+/)
    .filter((word) => word.length > 0);
  return words
    .map((word, index) => {
      if (word.length > 1 && word === word.toUpperCase() && /[A-Z]/.test(word)) return word;
      const lower = word.toLowerCase();
      return index === 0 ? lower.charAt(0).toUpperCase() + lower.slice(1) : lower;
    })
    .join(" ");
}

/** A query's heading: its title, or its name made readable when the document gave none (the API answers the name). */
export function queryTitle(query: Pick<ReportQuery, "name" | "title">): string {
  return query.title === query.name ? humanize(query.name) : query.title;
}

/** A parameter's caption: its label, or its name made readable when the document gave none. */
export function parameterLabel(parameter: Pick<ReportParameter, "name" | "label">): string {
  return parameter.label === parameter.name ? humanize(parameter.name) : parameter.label;
}

// ---- Columns and how a value is written ----------------------------------------------------------------------------

/** One column of an answer as a view shows it: where it is, what it is called, and how its values are written. */
export interface ShownColumn {
  index: number;
  /** The name the statement gave it. */
  name: string;
  /** What a person reads: the document's label, or the name. */
  label: string;
  kind: ColumnKind;
  format: ReportFormat;
  decimals: number | null;
  unit: string | null;
}

const WHOLE_TYPES = new Set(["int", "bigint", "smallint", "tinyint", "integer", "serial", "bigserial", "smallserial"]);

function bareType(dataType: string): string {
  return dataType.replace(/\([^)]*\)/g, "").replace(/\s+/g, " ").trim().toLowerCase();
}

/** The format a column takes when the document names none: what its type reads best as. */
export function defaultFormat(dataType: string): ReportFormat {
  const bare = bareType(dataType);
  if (bare === "bit" || bare === "boolean" || bare === "bool") return "flag";
  if (WHOLE_TYPES.has(bare)) return "integer";
  if (bare === "date") return "date";
  if (bare === "time") return "text";
  const kind = classify(dataType);
  return kind === "numeric" ? "number" : kind === "date" ? "datetime" : "text";
}

/** The columns of an answer with the document's presentation laid over them; a name is matched without regard to case. */
export function shownColumns(columns: readonly QueryResultColumn[], presentations: readonly ReportColumn[]): ShownColumn[] {
  return columns.map((column, index) => {
    const presentation = presentations.find((p) => p.name.toLowerCase() === column.name.toLowerCase());
    const name = column.name.trim().length > 0 ? column.name : `(column ${index + 1})`;
    return {
      index,
      name,
      label: presentation?.label ?? name,
      kind: classify(column.dataType),
      format: presentation?.format ?? defaultFormat(column.dataType),
      decimals: presentation?.decimals ?? null,
      unit: presentation?.unit ?? null,
    };
  });
}

const DATE_TIME = /^(\d{4}-\d{2}-\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2}))?)?/;
const BYTE_UNITS = ["B", "KB", "MB", "GB", "TB", "PB"];

function withUnit(text: string, unit: string | null): string {
  return unit === null || unit === "" ? text : `${text} ${unit}`;
}

function plainNumber(n: number, decimals: number | null, fallbackMax: number, locale?: string): string {
  return new Intl.NumberFormat(locale, decimals === null
    ? { maximumFractionDigits: fallbackMax }
    : { minimumFractionDigits: decimals, maximumFractionDigits: decimals }).format(n);
}

/** A number of seconds as a person says it: 0.85 is "850 ms", 12.3 is "12.3 s", 245 is "4 min 05 s", 3725 is "1 h 02 min". */
export function formatDuration(seconds: number, locale?: string): string {
  const sign = seconds < 0 ? "-" : "";
  const s = Math.abs(seconds);
  const two = (n: number) => String(n).padStart(2, "0");
  if (s < 1) return `${sign}${plainNumber(s * 1000, null, 0, locale)} ms`;
  if (s < 60) return `${sign}${plainNumber(s, null, 1, locale)} s`;
  if (s < 3600) return `${sign}${Math.floor(s / 60)} min ${two(Math.floor(s % 60))} s`;
  if (s < 86400) return `${sign}${Math.floor(s / 3600)} h ${two(Math.floor((s % 3600) / 60))} min`;
  return `${sign}${Math.floor(s / 86400)} d ${two(Math.floor((s % 86400) / 3600))} h`;
}

/** A count of bytes in the largest unit that fits, in steps of 1024: 1536 is "1.5 KB". */
export function formatBytes(bytes: number, decimals: number | null, locale?: string): string {
  let value = Math.abs(bytes);
  let unit = 0;
  while (value >= 1024 && unit < BYTE_UNITS.length - 1) {
    value /= 1024;
    unit++;
  }

  const text = unit === 0 ? plainNumber(value, null, 0, locale) : plainNumber(value, decimals, 1, locale);
  return `${bytes < 0 ? "-" : ""}${text} ${BYTE_UNITS[unit]}`;
}

/**
 * One value as its column writes it, or null for SQL NULL (each view says "no value" its own way). A value its format
 * cannot read (text in a number column) is shown as it is, never as a wrong number.
 */
export function formatValue(
  value: string | null, column: Pick<ShownColumn, "format" | "decimals" | "unit">, locale?: string,
): string | null {
  if (value === null) return null;
  const { format, decimals, unit } = column;
  switch (format) {
    case "integer":
    case "number":
    case "percent":
    case "bytes":
    case "duration": {
      const n = toNumber(value);
      if (n === null) return withUnit(value, unit);
      if (format === "integer") return withUnit(plainNumber(n, 0, 0, locale), unit);
      if (format === "number") return withUnit(plainNumber(n, decimals, 2, locale), unit);
      if (format === "bytes") return formatBytes(n, decimals, locale);
      if (format === "duration") return formatDuration(n, locale);
      return new Intl.NumberFormat(locale, decimals === null
        ? { style: "percent", maximumFractionDigits: 1 }
        : { style: "percent", minimumFractionDigits: decimals, maximumFractionDigits: decimals }).format(n);
    }

    case "date": {
      const match = DATE_TIME.exec(value);
      return match === null ? value : match[1];
    }

    case "datetime": {
      const match = DATE_TIME.exec(value);
      if (match === null) return value;
      return match[2] === undefined ? match[1] : `${match[1]} ${match[2]}:${match[3]}:${match[4] ?? "00"}`;
    }

    case "flag": {
      const text = value.trim().toLowerCase();
      if (text === "true" || text === "1") return "Yes";
      if (text === "false" || text === "0") return "No";
      return value;
    }

    default:
      return withUnit(value, unit);
  }
}

/** A number as an axis or a headline writes it when it is long: 12,900 is "12.9K", 4,200,000 is "4.2M". */
export function formatCompact(n: number, locale?: string): string {
  return new Intl.NumberFormat(locale, { notation: "compact", maximumFractionDigits: 1 }).format(n);
}

/** A number as a chart's mark or axis writes it: with its column's format, in full. */
export function formatNumber(n: number, column: Pick<ShownColumn, "format" | "decimals" | "unit">, locale?: string): string {
  const numeric: ReportFormat = column.format === "integer" || column.format === "number" || column.format === "percent"
    || column.format === "bytes" || column.format === "duration" ? column.format : "number";
  return formatValue(String(n), { format: numeric, decimals: column.decimals, unit: column.unit }, locale) ?? "";
}

/** An axis tick: compact once the axis reaches ten thousand, so ticks stay short; the column's own format otherwise. */
export function formatTick(n: number, column: Pick<ShownColumn, "format" | "decimals" | "unit">, axisMax: number, locale?: string): string {
  if (column.format === "percent" || column.format === "bytes" || column.format === "duration") {
    return formatNumber(n, { ...column, decimals: column.format === "percent" ? 0 : column.decimals }, locale);
  }

  return Math.abs(axisMax) >= 10_000 ? formatCompact(n, locale) : plainNumber(n, null, 2, locale);
}

// ---- How a view is drawn ---------------------------------------------------------------------------------------------

/** One series of a chart: what it is called, the key its colour follows, and the column its numbers are written as. */
export interface Series {
  /** What identifies the series whatever else is drawn: its column's name, or its value of the series column. */
  key: string;
  label: string;
  column: ShownColumn;
  /** True for the series that gathers what was folded together; it is drawn in the quiet tone, never a series colour. */
  other?: boolean;
}

/** One figure: a column of the answer's first row. */
export interface FigureItem {
  column: ShownColumn;
  value: string | null;
}

/** One category of a bar or column chart, with a value per series. */
export interface CategoryRow {
  /** The category as a person reads it. */
  label: string;
  /** The category's value as the query answered it: what a click sets a parameter to. */
  raw: string | null;
  values: (number | null)[];
}

export interface BarPlan {
  kind: "bar" | "column";
  by: ShownColumn;
  series: Series[];
  rows: CategoryRow[];
  stacked: boolean;
  /** How many categories a bar view shows before the rest wait behind "Show all". */
  top: number;
  /** The smallest and largest extent a mark reaches, zero included: the scale every bar is drawn on. */
  min: number;
  max: number;
  notes: string[];
}

export interface LinePlan {
  kind: "line";
  by: ShownColumn;
  /** The position labels, one per point. */
  labels: string[];
  series: Series[];
  /** A value per series for each point. */
  points: (number | null)[][];
  notes: string[];
}

export interface SharePart {
  label: string;
  raw: string | null;
  value: number;
  /** The part's fraction of the whole, 0 to 1. */
  share: number;
  other?: boolean;
}

export interface SharePlan {
  kind: "share";
  by: ShownColumn;
  value: ShownColumn;
  parts: SharePart[];
  total: number;
  notes: string[];
}

export interface MatrixPlan {
  kind: "matrix";
  by: ShownColumn;
  across: ShownColumn;
  value: ShownColumn;
  rows: { label: string; raw: string | null }[];
  columns: string[];
  /** A value per row and column; null where the answer holds none. */
  cells: (number | null)[][];
  max: number;
  notes: string[];
}

export type ViewPlan =
  | { kind: "empty" }
  | { kind: "problem"; message: string }
  | { kind: "figure"; items: FigureItem[]; notes: string[] }
  | BarPlan
  | LinePlan
  | SharePlan
  | MatrixPlan
  | { kind: "table"; columns: ShownColumn[]; by: ShownColumn | null };

/** The most series a chart draws; a ninth is never given a colour of its own. */
export const MAX_SERIES = 8;

/** The most columns a matrix lays out across, and the most rows it lists. */
export const MAX_MATRIX_COLUMNS = 12;
export const MAX_MATRIX_ROWS = 60;

/** The most points a column or line chart draws. */
export const MAX_POINTS = 400;

const DEFAULT_BAR_TOP = 12;
const DEFAULT_SHARE_TOP = 6;
const OTHER = "Other";

class ViewProblem extends Error {}

function fail(message: string): never {
  throw new ViewProblem(message);
}

function named(columns: readonly ShownColumn[], name: string, role: string): ShownColumn {
  const found = columns.find((column) => column.name.toLowerCase() === name.toLowerCase());
  return found ?? fail(
    `The view's ${role} is the column "${name}", which the query does not answer. Its columns are: ${columns.map((c) => c.name).join(", ")}.`);
}

function numericOnes(columns: readonly ShownColumn[], except: readonly (ShownColumn | null)[]): ShownColumn[] {
  return columns.filter((column) => column.kind === "numeric" && column.format !== "flag" && !except.includes(column));
}

function categoryLabel(value: string | null, column: ShownColumn, locale?: string): string {
  return value === null ? "(no value)" : formatValue(value, column, locale) ?? "(no value)";
}

/**
 * The series of a bar, column or line view and a value per series for each category. With a series column the rows are
 * in long form (one row per category and series) and are gathered by category; without one each value column is a
 * series. Categories keep the order the query answered them in.
 */
function gather(
  view: ReportView, columns: readonly ShownColumn[], rows: readonly Row[], by: ShownColumn, kind: string, fold: boolean,
  locale?: string,
): { series: Series[]; rows: CategoryRow[]; notes: string[] } {
  const notes: string[] = [];
  const seriesColumn = view.series === null ? null : named(columns, view.series, "series");
  if (seriesColumn !== null && seriesColumn === by) {
    fail(`The view splits its series by "${by.name}", the column it also lays its marks out by; name another column under 'series'.`);
  }

  const given = view.values.map((name) => named(columns, name, "value"));
  const candidates = given.length > 0 ? given : numericOnes(columns, [by, seriesColumn]);
  if (candidates.length === 0) {
    fail(`A ${kind} view draws a column of numbers, and the query answers none beside "${by.name}".`);
  }

  if (seriesColumn === null) {
    const values = candidates.slice(0, MAX_SERIES);
    if (candidates.length > values.length) {
      notes.push(`The first ${values.length} of ${candidates.length} value columns are drawn.`);
    }

    return {
      series: values.map((column) => ({ key: column.name, label: column.label, column })),
      rows: rows.map((row) => ({
        label: categoryLabel(row[by.index], by, locale),
        raw: row[by.index],
        values: values.map((column) => toNumber(row[column.index])),
      })),
      notes,
    };
  }

  // Long form: one value, split by the series column. A category and series named twice add up.
  const value = candidates[0];
  const seriesKeys: string[] = [];
  const categories = new Map<string, { row: CategoryRow; bySeries: Map<string, number> }>();
  for (const row of rows) {
    const categoryKey = row[by.index] ?? "\u0000";
    const seriesKey = categoryLabel(row[seriesColumn.index], seriesColumn, locale);
    if (!seriesKeys.includes(seriesKey)) seriesKeys.push(seriesKey);
    let entry = categories.get(categoryKey);
    if (entry === undefined) {
      entry = { row: { label: categoryLabel(row[by.index], by, locale), raw: row[by.index], values: [] }, bySeries: new Map() };
      categories.set(categoryKey, entry);
    }

    const n = toNumber(row[value.index]);
    if (n !== null) entry.bySeries.set(seriesKey, (entry.bySeries.get(seriesKey) ?? 0) + n);
  }

  let kept = seriesKeys;
  let folded: string[] = [];
  if (seriesKeys.length > MAX_SERIES) {
    kept = seriesKeys.slice(0, fold ? MAX_SERIES - 1 : MAX_SERIES);
    folded = seriesKeys.slice(kept.length);
    notes.push(fold
      ? `${folded.length} of ${seriesKeys.length} series are added together as "${OTHER}".`
      : `The first ${kept.length} of ${seriesKeys.length} series are drawn.`);
  }

  const series: Series[] = kept.map((key) => ({ key, label: key, column: value }));
  if (fold && folded.length > 0) series.push({ key: OTHER, label: OTHER, column: value, other: true });
  const gathered = [...categories.values()].map(({ row, bySeries }) => {
    const values: (number | null)[] = kept.map((key) => bySeries.get(key) ?? null);
    if (fold && folded.length > 0) {
      const rest = folded.filter((key) => bySeries.has(key));
      values.push(rest.length === 0 ? null : rest.reduce((sum, key) => sum + (bySeries.get(key) ?? 0), 0));
    }

    return { ...row, values };
  });
  return { series, rows: gathered, notes };
}

function firstOf(columns: readonly ShownColumn[], kinds: readonly ColumnKind[], except: readonly (ShownColumn | null)[]): ShownColumn | null {
  for (const kind of kinds) {
    const found = columns.find((column) => column.kind === kind && !except.includes(column));
    if (found !== undefined) return found;
  }

  return columns.find((column) => !except.includes(column)) ?? null;
}

function barPlan(
  kind: "bar" | "column", view: ReportView, columns: readonly ShownColumn[], rows: readonly Row[], locale?: string,
): BarPlan {
  const by = view.by !== null
    ? named(columns, view.by, "'by'")
    : firstOf(columns, kind === "bar" ? ["text", "date"] : ["date", "text"], []) ?? fail("The query answers no column.");
  const gathered = gather(view, columns, rows, by, kind, true, locale);
  const notes = [...gathered.notes];
  let categories = gathered.rows;
  if (kind === "column" && categories.length > MAX_POINTS) {
    notes.push(`The first ${MAX_POINTS} of ${categories.length} rows are drawn.`);
    categories = categories.slice(0, MAX_POINTS);
  }

  const anyNegative = categories.some((row) => row.values.some((value) => value !== null && value < 0));
  const wantsStack = view.stacked ?? view.series !== null;
  const stacked = wantsStack && gathered.series.length > 1 && !anyNegative;
  if (wantsStack && gathered.series.length > 1 && anyNegative) {
    notes.push("The series are set side by side: a stack cannot hold a value below zero.");
  }

  let min = 0;
  let max = 0;
  for (const row of categories) {
    if (stacked) {
      max = Math.max(max, row.values.reduce<number>((sum, value) => sum + (value ?? 0), 0));
    } else {
      for (const value of row.values) {
        if (value !== null) {
          min = Math.min(min, value);
          max = Math.max(max, value);
        }
      }
    }
  }

  return {
    kind, by, series: gathered.series, rows: categories, stacked, top: view.top ?? DEFAULT_BAR_TOP, min, max, notes,
  };
}

function linePlan(view: ReportView, columns: readonly ShownColumn[], rows: readonly Row[], locale?: string): LinePlan {
  const by = view.by !== null
    ? named(columns, view.by, "'by'")
    : firstOf(columns, ["date", "text"], []) ?? fail("The query answers no column.");
  const gathered = gather(view, columns, rows, by, "line", false, locale);
  const notes = [...gathered.notes];
  let points = gathered.rows;

  // A line reads left to right in the order of what it is laid out by: dates and numbers are put in order, names
  // keep the order the query answered them in.
  if (by.kind === "date") {
    points = [...points].sort((a, b) => (a.raw ?? "").localeCompare(b.raw ?? ""));
  } else if (by.kind === "numeric") {
    points = [...points].sort((a, b) => (toNumber(a.raw) ?? 0) - (toNumber(b.raw) ?? 0));
  }

  if (points.length > MAX_POINTS) {
    notes.push(`The first ${MAX_POINTS} of ${points.length} points are drawn.`);
    points = points.slice(0, MAX_POINTS);
  }

  // A date's labels are as short as the whole run allows (a date alone when no value carries a time of day).
  const labels = by.kind === "date" && (by.format === "date" || by.format === "datetime")
    ? axisLabels(points.map((point) => point.raw), by.format === "date" ? "date" : "datetime")
    : points.map((point) => point.label);
  return { kind: "line", by, labels, series: gathered.series, points: points.map((point) => point.values), notes };
}

function sharePlan(view: ReportView, columns: readonly ShownColumn[], rows: readonly Row[], locale?: string): SharePlan {
  const by = view.by !== null
    ? named(columns, view.by, "'by'")
    : firstOf(columns, ["text", "date"], []) ?? fail("The query answers no column.");
  const value = view.values.length > 0
    ? named(columns, view.values[0], "value")
    : numericOnes(columns, [by])[0] ?? fail(`A share view divides a column of numbers, and the query answers none beside "${by.name}".`);
  const notes: string[] = [];

  const read = rows.map((row) => ({ label: categoryLabel(row[by.index], by, locale), raw: row[by.index], value: toNumber(row[value.index]) }));
  const positive = read.filter((part): part is { label: string; raw: string | null; value: number } => part.value !== null && part.value > 0);
  if (positive.length < read.length) {
    const left = read.length - positive.length;
    notes.push(`${left} row${left === 1 ? "" : "s"} without a value above zero ${left === 1 ? "is" : "are"} not part of the whole.`);
  }

  const top = Math.min(view.top ?? DEFAULT_SHARE_TOP, MAX_SERIES - 1);
  const kept = positive.length > top + 1 ? positive.slice(0, top) : positive;
  const rest = positive.slice(kept.length);
  const total = positive.reduce((sum, part) => sum + part.value, 0);
  const parts: SharePart[] = kept.map((part) => ({ ...part, share: total === 0 ? 0 : part.value / total }));
  if (rest.length > 0) {
    const sum = rest.reduce((acc, part) => acc + part.value, 0);
    parts.push({ label: `${OTHER} (${rest.length})`, raw: null, value: sum, share: total === 0 ? 0 : sum / total, other: true });
  }

  return { kind: "share", by, value, parts, total, notes };
}

function matrixPlan(view: ReportView, columns: readonly ShownColumn[], rows: readonly Row[], locale?: string): MatrixPlan {
  const across = named(columns, view.across ?? fail("A matrix view names the column it lays out across under 'across'."), "'across'");
  const by = view.by !== null
    ? named(columns, view.by, "'by'")
    : firstOf(columns, ["text", "date"], [across]) ?? fail("A matrix view needs a column for its rows beside the one it lays out across.");
  if (by === across) {
    fail(`The matrix lists its rows by "${by.name}", the column it also lays out across; name two different columns.`);
  }

  const value = view.values.length > 0
    ? named(columns, view.values[0], "value")
    : numericOnes(columns, [by, across])[0] ?? fail(`A matrix view fills its cells from a column of numbers, and the query answers none beside "${by.name}" and "${across.name}".`);
  const notes: string[] = [];

  const rowKeys: { label: string; raw: string | null }[] = [];
  const columnKeys: string[] = [];
  const held = new Map<string, number>();
  for (const row of rows) {
    const rowLabel = categoryLabel(row[by.index], by, locale);
    const columnLabel = categoryLabel(row[across.index], across, locale);
    if (!rowKeys.some((key) => key.label === rowLabel)) rowKeys.push({ label: rowLabel, raw: row[by.index] });
    if (!columnKeys.includes(columnLabel)) columnKeys.push(columnLabel);
    const n = toNumber(row[value.index]);
    if (n !== null) {
      const key = `${rowLabel}\u0000${columnLabel}`;
      held.set(key, (held.get(key) ?? 0) + n);
    }
  }

  const shownRows = rowKeys.slice(0, MAX_MATRIX_ROWS);
  const shownColumnKeys = columnKeys.slice(0, MAX_MATRIX_COLUMNS);
  if (rowKeys.length > shownRows.length) notes.push(`The first ${shownRows.length} of ${rowKeys.length} rows are shown.`);
  if (columnKeys.length > shownColumnKeys.length) {
    notes.push(`The first ${shownColumnKeys.length} of ${columnKeys.length} columns are shown.`);
  }

  const cells = shownRows.map((rowKey) => shownColumnKeys.map((columnKey) => held.get(`${rowKey.label}\u0000${columnKey}`) ?? null));
  const max = Math.max(0, ...cells.flat().map((cell) => Math.abs(cell ?? 0)));
  return { kind: "matrix", by, across, value, rows: shownRows, columns: shownColumnKeys, cells, max, notes };
}

/**
 * What an answer's shape reads best as when the document says nothing: one row of a few values is a set of figures;
 * rows over one date column with numbers are a line; rows of one name and one number are bars; anything else is a
 * table. A form is chosen only when it loses nothing the answer holds.
 */
function autoPlan(columns: readonly ShownColumn[], rows: readonly Row[], view: ReportView, locale?: string): ViewPlan {
  if (rows.length === 1 && columns.length <= 6) {
    return { kind: "figure", items: columns.map((column) => ({ column, value: rows[0][column.index] })), notes: [] };
  }

  const numbers = columns.filter((column) => column.kind === "numeric" && column.format !== "flag");
  const dates = columns.filter((column) => column.kind === "date");
  const texts = columns.filter((column) => !numbers.includes(column) && !dates.includes(column));
  if (rows.length > 1 && dates.length === 1 && texts.length === 0 && numbers.length >= 1 && numbers.length <= 4) {
    return linePlan({ ...view, by: dates[0].name }, columns, rows, locale);
  }

  if (rows.length > 1 && texts.length === 1 && dates.length === 0 && numbers.length === 1) {
    return barPlan("bar", { ...view, by: texts[0].name }, columns, rows, locale);
  }

  return { kind: "table", columns: [...columns], by: null };
}

/**
 * How one view of an answer is drawn. What the document left out is decided from the answer; what it named and the
 * answer does not hold is a problem said in words, with the columns the answer does hold, so the document can be put
 * right without running the query by hand.
 */
export function planView(view: ReportView, columns: readonly ShownColumn[], rows: readonly Row[], locale?: string): ViewPlan {
  if (columns.length === 0) {
    return { kind: "problem", message: "The query answered no column." };
  }

  if (rows.length === 0) {
    return { kind: "empty" };
  }

  try {
    switch (view.type) {
      case "figure": {
        const chosen = view.values.length > 0 ? view.values.map((name) => named(columns, name, "value")) : [...columns];
        return {
          kind: "figure",
          items: chosen.map((column) => ({ column, value: rows[0][column.index] })),
          notes: rows.length > 1 ? [`The figures are the first of the ${rows.length} rows the query answered.`] : [],
        };
      }

      case "bar":
      case "column":
        return barPlan(view.type, view, columns, rows, locale);
      case "line":
        return linePlan(view, columns, rows, locale);
      case "share":
        return sharePlan(view, columns, rows, locale);
      case "matrix":
        return matrixPlan(view, columns, rows, locale);
      case "table":
        return {
          kind: "table",
          columns: view.values.length > 0 ? view.values.map((name) => named(columns, name, "column")) : [...columns],
          by: view.by === null ? null : named(columns, view.by, "'by'"),
        };
      default:
        return autoPlan(columns, rows, view, locale);
    }
  } catch (error) {
    if (error instanceof ViewProblem) {
      return { kind: "problem", message: error.message };
    }

    throw error;
  }
}

/**
 * How far apart the series' largest values are: the largest over the smallest of those above zero, or 1 when fewer than
 * two series have any size. Past a spread of five one axis flattens the smaller series, so a line view draws a small
 * chart per series instead (one axis each, never two on one chart).
 */
export function seriesSpread(points: readonly (readonly (number | null)[])[], seriesCount: number): number {
  const maxima = Array.from({ length: seriesCount }, (_, s) => Math.max(0, ...points.map((point) => Math.abs(point[s] ?? 0))));
  const positive = maxima.filter((m) => m > 0);
  return positive.length > 1 ? Math.max(...positive) / Math.min(...positive) : 1;
}

/**
 * The colour slot of each series, kept from one drawing to the next so a colour follows the series and never its place
 * in the list: a series seen before keeps its slot, a new one takes the lowest free slot, and when every slot is taken
 * the slots of series no longer drawn are given up. Returns `held` itself when nothing changed.
 */
export function assignSlots(
  held: ReadonlyMap<string, number>, keys: readonly string[], slots: number = MAX_SERIES,
): ReadonlyMap<string, number> {
  if (keys.every((key) => held.has(key))) return held;
  const next = new Map(held);
  for (const key of keys) {
    if (next.has(key)) continue;
    let used = new Set(next.values());
    if (used.size >= slots) {
      for (const [name] of [...next]) {
        if (!keys.includes(name)) next.delete(name);
      }

      used = new Set(next.values());
    }

    let slot = 0;
    while (used.has(slot) && slot < slots - 1) slot++;
    next.set(key, slot);
  }

  return next;
}

// ---- Parameters and the address ----------------------------------------------------------------------------------------

/** What a report's parameters are written under in the page's address: `?p.source=ERP`. */
export const PARAMETER_PREFIX = "p.";

/**
 * The values a person set, by parameter name: a parameter that is not in the map takes its default, and one that is
 * there with null was cleared (it binds NULL whatever its default).
 */
export type GivenValues = Record<string, string | null>;

/** The values the address holds for the report's parameters; a parameter written with nothing after it was cleared. */
export function readValues(search: URLSearchParams, parameters: readonly ReportParameter[]): GivenValues {
  const given: GivenValues = {};
  for (const parameter of parameters) {
    const value = search.get(PARAMETER_PREFIX + parameter.name);
    if (value !== null) given[parameter.name] = value === "" ? null : value;
  }

  return given;
}

/**
 * The address with `parameter` set to `value`. Setting a parameter to its default takes it out of the address, since
 * that is what leaving it out means; clearing one writes it with nothing after it when it has a default to override,
 * and takes it out otherwise.
 */
export function writeValue(search: URLSearchParams, parameter: ReportParameter, value: string | null): URLSearchParams {
  const next = new URLSearchParams(search);
  const key = PARAMETER_PREFIX + parameter.name;
  const cleared = value === null || value === "";
  if (cleared) {
    if (parameter.default === null) next.delete(key);
    else next.set(key, "");
  } else if (value === parameter.default) {
    next.delete(key);
  } else {
    next.set(key, value);
  }

  return next;
}

/** The address with every parameter of the report taken out, so each is back at its default. */
export function clearValues(search: URLSearchParams, parameters: readonly ReportParameter[]): URLSearchParams {
  const next = new URLSearchParams(search);
  for (const parameter of parameters) next.delete(PARAMETER_PREFIX + parameter.name);
  return next;
}

/** The value a parameter has now: what was set, nothing when it was cleared, its default otherwise. */
export function effectiveValue(parameter: ReportParameter, given: GivenValues): string | null {
  return parameter.name in given ? given[parameter.name] : parameter.default;
}

/** Whether a person moved the parameter off its default. */
export function isSet(parameter: ReportParameter, given: GivenValues): boolean {
  return parameter.name in given && given[parameter.name] !== parameter.default;
}

/** The given values one query is run with: those of the parameters its statement binds, and no others. */
export function valuesFor(query: Pick<ReportQuery, "parameters">, given: GivenValues): GivenValues {
  const own: GivenValues = {};
  for (const name of query.parameters) {
    const key = Object.keys(given).find((candidate) => candidate.toLowerCase() === name.toLowerCase());
    if (key !== undefined) own[name] = given[key];
  }

  return own;
}

/** A query's values as one text that is the same for the same values, whatever order they were set in. */
export function valuesKey(values: GivenValues): string {
  return JSON.stringify(Object.keys(values).sort().map((name) => [name, values[name]]));
}

/** The required parameters a query binds that have no value yet, by the caption a person knows each by. */
export function missingRequired(
  query: Pick<ReportQuery, "parameters">, parameters: readonly ReportParameter[], given: GivenValues,
): string[] {
  return parameters
    .filter((parameter) => parameter.required
      && query.parameters.some((name) => name.toLowerCase() === parameter.name.toLowerCase())
      && effectiveValue(parameter, given) === null)
    .map(parameterLabel);
}

/**
 * The value a click on a mark gives a parameter: the mark's value as the query answered it, written the way the
 * parameter's type is written (a date column answers a full timestamp; a date parameter takes its date).
 */
export function selectValue(raw: string | null, type: ReportParameterType): string | null {
  if (raw === null) return null;
  const match = DATE_TIME.exec(raw);
  switch (type) {
    case "date":
      return match === null ? raw : match[1];
    case "datetime":
      return match === null ? raw : `${match[1]}T${match[2] ?? "00"}:${match[3] ?? "00"}:${match[4] ?? "00"}`;
    case "flag": {
      const text = raw.trim().toLowerCase();
      return text === "true" || text === "1" ? "true" : text === "false" || text === "0" ? "false" : raw;
    }

    default:
      return raw;
  }
}

/** The choices a query's answer gives a parameter: its first column the value, its second (when it has one) the label. */
export function choicesFrom(rows: readonly Row[]): { value: string; label: string }[] {
  const seen = new Set<string>();
  const choices: { value: string; label: string }[] = [];
  for (const row of rows) {
    const value = row[0];
    if (value === null || value === undefined || seen.has(value)) continue;
    seen.add(value);
    choices.push({ value, label: row.length > 1 && row[1] !== null && row[1] !== undefined ? row[1] : value });
  }

  return choices;
}

// ---- The page's layout -------------------------------------------------------------------------------------------------

/** One view of one query, placed on the page. */
export interface Tile {
  id: string;
  query: ReportQuery;
  view: ReportView;
}

/** The tiles under one heading; the heading is null for what comes before the first one. */
export interface Section {
  title: string | null;
  tiles: Tile[];
}

/**
 * The page's tiles under their headings, in the document's order. A query that names a section starts it (or carries on
 * under it when the query before named the same one); a query that names none stays under the heading before it. A
 * query hidden with `show: none` has no tile.
 */
export function layOut(report: Pick<Report, "queries">): Section[] {
  const sections: Section[] = [];
  let current: Section | null = null;
  for (const query of report.queries) {
    if (current === null || (query.section !== null && query.section !== current.title)) {
      current = { title: query.section, tiles: [] };
      sections.push(current);
    }

    query.views.forEach((view, index) => current!.tiles.push({ id: `${query.name}:${index}`, query, view }));
  }

  return sections.filter((section) => section.tiles.length > 0);
}

/** How many of the page's twelve columns a tile takes: what the document says, or what its form reads best at. */
export function tileSpan(width: ReportWidth | null, kind: ViewPlan["kind"] | null): number {
  switch (width) {
    case "quarter": return 3;
    case "third": return 4;
    case "half": return 6;
    case "two-thirds": return 8;
    case "full": return 12;
    default:
      return kind === "bar" || kind === "column" || kind === "line" || kind === "share" ? 6 : 12;
  }
}

// ---- Taking an answer away -----------------------------------------------------------------------------------------------

function csvCell(value: string | null): string {
  if (value === null) return "";
  return /[",\r\n]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value;
}

/**
 * An answer as CSV (RFC 4180): a header line of the columns' names, then the rows with their values as the query
 * answered them, so a number stays a number wherever the file is opened. SQL NULL is an empty cell.
 */
export function toCsv(columns: readonly Pick<QueryResultColumn, "name">[], rows: readonly Row[]): string {
  const lines = [columns.map((column) => csvCell(column.name)).join(",")];
  for (const row of rows) lines.push(row.map(csvCell).join(","));
  return lines.join("\r\n") + "\r\n";
}
