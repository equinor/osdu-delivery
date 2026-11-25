import type { FlowActivity, FlowActivityDay, FlowFire, FlowFireRun } from "../../api/types.ts";
import type { RunMeasureOf } from "../../modules/runMeasure.ts";
import { formatBytes, parseUtc } from "../../lib/time.ts";

// The rules of the lineage graph's data view that hold no React: what a flow did in a day or in one fire, what that
// means for the objects it reads and writes, how the lines between them are inked and weighed, and the words a card
// says it in. The canvas draws; this decides.

/**
 * What happened at a node in the period looked at. "moved": data went through. "empty": its flows ran and nothing
 * changed. "failed": every run failed. "running": a run is under way and none has finished. "absent": no run at
 * all, which is not a fault (a flow need not run every day, and a fire need not run every flow).
 */
export type Outcome = "moved" | "empty" | "failed" | "running" | "absent";

/** One flow's runs in a period, totalled: a day of them, or its runs in one fire. */
export interface FlowTotals {
  /** Runs that finished, succeeded or failed. */
  runs: number;
  failed: number;
  /** Runs still queued or running. A day's totals count none: they are told from the day's fires. */
  active: number;
  rowsRead: number;
  /** Whether any run reported what it inserted, updated or deleted. One that did not wrote what it read. */
  breakdown: boolean;
  rowsInserted: number;
  rowsUpdated: number;
  rowsDeleted: number;
  files: number;
  fileRows: number;
  fileBytes: number;
  seconds: number;
  /** The counts the runs' own results report, summed; null when none reported any. */
  counts: Record<string, number> | null;
}

/** A day's totals as the server sent them. */
export function totalsOfDay(day: FlowActivityDay): FlowTotals {
  return {
    runs: day.runs,
    failed: day.failed,
    active: 0,
    rowsRead: day.rowsRead,
    breakdown: day.breakdown,
    rowsInserted: day.rowsInserted,
    rowsUpdated: day.rowsUpdated,
    rowsDeleted: day.rowsDeleted,
    files: day.files,
    fileRows: day.fileRows,
    fileBytes: day.fileBytes,
    seconds: day.seconds,
    counts: day.counts,
  };
}

/** One flow's runs in a fire, totalled the way a day is; null for no runs. A cancelled run adds nothing. */
export function totalsOfRuns(runs: readonly FlowFireRun[]): FlowTotals | null {
  if (runs.length === 0) {
    return null;
  }

  const totals: FlowTotals = {
    runs: 0, failed: 0, active: 0, rowsRead: 0, breakdown: false, rowsInserted: 0, rowsUpdated: 0, rowsDeleted: 0,
    files: 0, fileRows: 0, fileBytes: 0, seconds: 0, counts: null,
  };
  for (const { run, files } of runs) {
    if (run.status === "queued" || run.status === "running") {
      totals.active += 1;
    } else if (run.status === "succeeded" || run.status === "failed") {
      totals.runs += 1;
      totals.failed += run.status === "failed" ? 1 : 0;
    }

    totals.rowsRead += run.rowsLoaded ?? 0;
    totals.breakdown ||= run.rowsInserted !== null || run.rowsUpdated !== null || run.rowsDeleted !== null;
    totals.rowsInserted += run.rowsInserted ?? 0;
    totals.rowsUpdated += run.rowsUpdated ?? 0;
    totals.rowsDeleted += run.rowsDeleted ?? 0;
    totals.files += files?.files ?? 0;
    totals.fileRows += files?.rows ?? 0;
    totals.fileBytes += files?.bytes ?? 0;
    totals.seconds += run.durationSeconds ?? 0;
    for (const [name, value] of Object.entries(run.resultCounts ?? {})) {
      totals.counts ??= {};
      totals.counts[name] = (totals.counts[name] ?? 0) + value;
    }
  }

  return totals;
}

/** What one flow did in a period, in the numbers a card and a line are drawn from. */
export interface Figure {
  outcome: Outcome;
  runs: number;
  failed: number;
  active: number;
  /** How much it moved on: rows written, or the amount its kind measures by. */
  moved: number;
  /** The words after `moved`: "new rows", "rows changed", "records delivered". */
  label: string;
  /** How much reached it; null when that is not counted apart from what it moved. */
  taken: number | null;
  /** The word after `taken`: "read", "planned". */
  takenLabel: string;
  /** How many of what reached it could not be moved on, for a kind that counts those. */
  problems: number;
  problemsLabel: string;
  /** What it did to its target, when its runs say: null for a kind that reports a row count or its own counts. */
  changes: { inserted: number; updated: number; deleted: number } | null;
  files: number;
  fileRows: number;
  fileBytes: number;
  seconds: number;
  /** Other counts of the kind worth listing, zeroes left out. */
  details: readonly { label: string; value: number }[];
}

function plural(count: number, one: string, many: string): string {
  return count === 1 ? one : many;
}

/**
 * What one flow did, from its totals. A kind that measures by its own counts (`measure`) says how much it moved; one
 * of SQLFlow's own kinds moved what it inserted, updated and deleted, or what it read when it reports no such
 * breakdown (a file load, an export). An ingestion that read rows and changed none ran and moved nothing on.
 */
export function figureOf(totals: FlowTotals | null, measure?: RunMeasureOf): Figure {
  const none: Figure = {
    outcome: "absent", runs: 0, failed: 0, active: 0, moved: 0, label: "rows", taken: null, takenLabel: "read",
    problems: 0, problemsLabel: "failed", changes: null, files: 0, fileRows: 0, fileBytes: 0, seconds: 0, details: [],
  };
  if (totals === null || (totals.runs === 0 && totals.active === 0)) {
    return none;
  }

  let figure: Figure;
  if (measure !== undefined) {
    const measured = measure(totals.counts ?? {});
    figure = {
      ...none,
      moved: measured.moved,
      label: measured.label,
      taken: measured.taken ?? null,
      takenLabel: measured.takenLabel ?? "taken in",
      problems: measured.problems ?? 0,
      problemsLabel: measured.problemsLabel ?? "failed",
      details: (measured.details ?? []).filter((detail) => detail.value !== 0),
    };
  } else if (totals.breakdown) {
    const moved = totals.rowsInserted + totals.rowsUpdated + totals.rowsDeleted;
    const onlyNew = totals.rowsUpdated === 0 && totals.rowsDeleted === 0;
    figure = {
      ...none,
      moved,
      label: onlyNew ? plural(moved, "new row", "new rows") : plural(moved, "row changed", "rows changed"),
      taken: totals.rowsRead,
      changes: { inserted: totals.rowsInserted, updated: totals.rowsUpdated, deleted: totals.rowsDeleted },
    };
  } else {
    figure = { ...none, moved: totals.rowsRead, label: plural(totals.rowsRead, "row", "rows") };
  }

  return {
    ...figure,
    outcome: totals.runs === 0 ? "running" : totals.failed === totals.runs ? "failed" : figure.moved > 0 ? "moved" : "empty",
    runs: totals.runs,
    failed: totals.failed,
    active: totals.active,
    files: totals.files,
    fileRows: totals.fileRows,
    fileBytes: totals.fileBytes,
    seconds: totals.seconds,
  };
}

/** A count as a card says it: grouped digits, compact past a hundred thousand so a card stays one line. */
export function formatCount(value: number): string {
  if (Math.abs(value) < 100_000) {
    return Math.round(value).toLocaleString("en-US");
  }

  const scaled = (amount: number, unit: string) => `${amount.toFixed(amount < 10 ? 1 : 0).replace(/\.0$/, "")}${unit}`;
  return Math.abs(value) >= 1_000_000 ? scaled(value / 1_000_000, "M") : scaled(value / 1_000, "k");
}

/** "3 new, 2 updated, 1 removed": what a flow did to its target, the parts that are zero left out. */
export function changesText(changes: { inserted: number; updated: number; deleted: number }): string {
  return [
    [changes.inserted, "new"],
    [changes.updated, "updated"],
    [changes.deleted, "removed"],
  ]
    .filter(([count]) => (count as number) > 0)
    .map(([count, word]) => `${formatCount(count as number)} ${word as string}`)
    .join(", ");
}

/** What a node of the drawing stands for, as the data view needs it: flows, or objects with the flows around them. */
export interface LensNodeSpec {
  id: string;
  type: "flow" | "object";
  /** A flow node: the flows it stands for (itself, or the flows of a folder or a stack). */
  flows: readonly string[];
  /** An object node: the flows of the scope that write it, and those that read it. */
  writers: readonly string[];
  readers: readonly string[];
}

/** What a card says about its node in the period looked at, and what its lines are drawn from. */
export interface NodeFigure {
  outcome: Outcome;
  /** The number the card leads with, as text; empty when it leads with words alone. */
  value: string;
  /** The words beside the number, or the whole statement: "new rows", "No change", "No run". */
  label: string;
  /** A second, quieter fact: "of 495k read", "83.3 MiB", "2 of 3 runs failed". Empty when there is none. */
  note: string;
  /** True when a run failed here though others did not: the card marks it without calling the whole node failed. */
  someFailed: boolean;
  /** How much left the node (a flow wrote, an object received), for weighing the lines out of a flow. */
  volume: number;
  /** A flow node: what reached it, for inking and weighing the lines into it. */
  intake: { outcome: Outcome; volume: number };
}

const ABSENT_INTAKE = { outcome: "absent" as Outcome, volume: 0 };

function intakeOf(figure: Figure): { outcome: Outcome; volume: number } {
  if (figure.outcome === "absent" || figure.outcome === "running" || figure.outcome === "failed") {
    return { outcome: figure.outcome, volume: 0 };
  }

  // What reached a flow: the rows it read, the rows of the files it took in, or (for a kind that counts neither) what
  // it moved on. An ingestion that read rows and changed none was reached by data, so the line into it shows it.
  const volume = Math.max(figure.taken ?? 0, figure.fileRows, figure.moved);
  return { outcome: volume > 0 || figure.files > 0 ? "moved" : "empty", volume };
}

function flowFigure(figure: Figure): NodeFigure {
  const base = { someFailed: figure.failed > 0 && figure.outcome !== "failed", volume: figure.moved, intake: intakeOf(figure) };
  switch (figure.outcome) {
    case "absent":
      return { ...base, outcome: "absent", value: "", label: "No run", note: "" };
    case "running":
      return { ...base, outcome: "running", value: "", label: "Running", note: "" };
    case "failed":
      return {
        ...base, outcome: "failed", value: "", label: "Failed",
        note: figure.runs > 1 ? `all ${figure.runs} runs` : "",
      };
    case "empty":
      return {
        ...base, outcome: "empty", value: "", label: "No change",
        note: figure.taken !== null && figure.taken > 0
          ? `${formatCount(figure.taken)} ${figure.takenLabel}`
          : figure.problems > 0 ? `${formatCount(figure.problems)} ${figure.problemsLabel}` : "",
      };
    default:
      return {
        ...base, outcome: "moved", value: formatCount(figure.moved), label: figure.label,
        note: figure.problems > 0
          ? `${formatCount(figure.problems)} ${figure.problemsLabel}`
          : figure.taken !== null && figure.taken !== figure.moved ? `of ${formatCount(figure.taken)} ${figure.takenLabel}` : "",
      };
  }
}

function severalFlows(figures: readonly Figure[]): NodeFigure {
  const ran = figures.filter((figure) => figure.outcome !== "absent");
  const failed = ran.filter((figure) => figure.failed > 0).length;
  const moved = ran.filter((figure) => figure.outcome === "moved").length;
  const volume = figures.reduce((sum, figure) => sum + figure.moved, 0);
  const intake = figures.map(intakeOf);
  const reached = intake.some((item) => item.outcome === "moved");
  const base = {
    someFailed: failed > 0 && failed < ran.length,
    volume,
    intake: {
      outcome: ran.length === 0 ? "absent" as Outcome : reached ? "moved" as Outcome : "empty" as Outcome,
      volume: intake.reduce((sum, item) => sum + item.volume, 0),
    },
  };
  if (ran.length === 0) {
    return { ...base, outcome: "absent", value: "", label: "No run", note: "" };
  }

  const outcome: Outcome = failed === ran.length ? "failed" : moved > 0 ? "moved" : "empty";
  return {
    ...base,
    outcome,
    value: String(ran.length),
    label: `of ${figures.length} ${plural(figures.length, "flow", "flows")} ran`,
    note: failed > 0 ? `${failed} failed` : moved > 0 ? `${moved} moved data` : "no change",
  };
}

function writtenFigure(writers: readonly Figure[]): NodeFigure {
  const ran = writers.filter((figure) => figure.outcome !== "absent");
  const base = { someFailed: false, intake: ABSENT_INTAKE };
  if (ran.length === 0) {
    return { ...base, outcome: "absent", value: "", label: "Not written", note: "", volume: 0 };
  }

  const moved = ran.reduce((sum, figure) => sum + figure.moved, 0);
  const failed = ran.filter((figure) => figure.failed > 0).length;
  if (moved === 0) {
    return failed === ran.length
      ? { ...base, outcome: "failed", value: "", label: "Write failed", note: "", volume: 0 }
      : ran.every((figure) => figure.outcome === "running")
        ? { ...base, outcome: "running", value: "", label: "Being written", note: "", volume: 0 }
        : { ...base, outcome: "empty", value: "", label: "No change", note: "", volume: 0 };
  }

  const movers = ran.filter((figure) => figure.moved > 0);
  // That a writer also had a failed run is said on the writer: here it would read as the table's own fault.
  const someFailed = false;
  if (movers.every((figure) => figure.changes !== null)) {
    const changes = movers.reduce(
      (sum, figure) => ({
        inserted: sum.inserted + figure.changes!.inserted,
        updated: sum.updated + figure.changes!.updated,
        deleted: sum.deleted + figure.changes!.deleted,
      }),
      { inserted: 0, updated: 0, deleted: 0 });
    const onlyNew = changes.updated === 0 && changes.deleted === 0;
    return {
      ...base, someFailed, outcome: "moved", volume: moved,
      value: onlyNew ? `+${formatCount(moved)}` : formatCount(moved),
      label: onlyNew ? plural(moved, "row", "rows") : plural(moved, "row changed", "rows changed"),
      note: onlyNew ? "" : changesText(changes),
    };
  }

  const labels = new Set(movers.map((figure) => figure.label));
  return {
    ...base, someFailed, outcome: "moved", volume: moved,
    value: formatCount(moved),
    label: labels.size === 1 ? movers[0].label : "moved here",
    note: "",
  };
}

function arrivedFigure(readers: readonly Figure[]): NodeFigure {
  const ran = readers.filter((figure) => figure.outcome !== "absent");
  const base = { someFailed: false, intake: ABSENT_INTAKE };
  if (ran.length === 0) {
    return { ...base, outcome: "absent", value: "", label: "Not read", note: "", volume: 0 };
  }

  const files = ran.reduce((sum, figure) => sum + figure.files, 0);
  const fileRows = ran.reduce((sum, figure) => sum + figure.fileRows, 0);
  const fileBytes = ran.reduce((sum, figure) => sum + figure.fileBytes, 0);
  if (files > 0) {
    return {
      ...base, outcome: "moved", volume: fileRows,
      value: formatCount(files),
      label: plural(files, "file", "files"),
      note: `${formatCount(fileRows)} ${plural(fileRows, "row", "rows")} · ${formatBytes(fileBytes)}`,
    };
  }

  const taken = ran.reduce((sum, figure) => sum + intakeOf(figure).volume, 0);
  if (taken > 0) {
    return {
      ...base, outcome: "moved", volume: taken,
      value: formatCount(taken), label: plural(taken, "row read", "rows read"), note: "",
    };
  }

  return ran.every((figure) => figure.outcome === "failed")
    ? { ...base, outcome: "failed", value: "", label: "Read failed", note: "", volume: 0 }
    : ran.every((figure) => figure.outcome === "running")
      ? { ...base, outcome: "running", value: "", label: "Being read", note: "", volume: 0 }
      : { ...base, outcome: "empty", value: "", label: "Nothing new", note: "", volume: 0 };
}

/**
 * What a node's card says for a period. A flow says what it did. An object some flow in scope writes says what
 * landed in it; one nothing writes is where data enters the estate, and says what arrived (the files its readers
 * took in, or the rows they read). `figureFor` gives a flow's figure in the period.
 */
export function nodeFigure(spec: LensNodeSpec, figureFor: (flowId: string) => Figure): NodeFigure {
  if (spec.type === "flow") {
    return spec.flows.length === 1 ? flowFigure(figureFor(spec.flows[0])) : severalFlows(spec.flows.map(figureFor));
  }

  return spec.writers.length > 0 ? writtenFigure(spec.writers.map(figureFor)) : arrivedFigure(spec.readers.map(figureFor));
}

/** A node's figure in one line, as a list row or a tooltip says it: "495k new rows · of 495k read", "No change ·
 * 5 read", "No run". */
export function figureLine(figure: NodeFigure): string {
  const lead = figure.value === "" ? figure.label : `${figure.value} ${figure.label}`;
  const note = figure.someFailed && figure.note === "" ? "a run failed" : figure.note;
  return note === "" ? lead : `${lead} · ${note}`;
}

/**
 * How a line of the drawing is inked and weighed. A line out of a flow carries what the flow moved; a line into a
 * flow carries what reached it; a line between two objects (the data-only drawing) carries what landed in the
 * second. So a chain whose ingestion read rows and changed none shows data reaching the ingestion and stopping there.
 */
export function edgeFigure(
  source: { type: "flow" | "object"; figure: NodeFigure } | undefined,
  target: { type: "flow" | "object"; figure: NodeFigure } | undefined,
): { outcome: Outcome; volume: number } {
  if (source?.type === "flow") {
    return { outcome: source.figure.outcome, volume: source.figure.volume };
  }

  if (target?.type === "flow") {
    return target.figure.intake;
  }

  return target === undefined ? ABSENT_INTAKE : { outcome: target.figure.outcome, volume: target.figure.volume };
}

/** A line's width for what it carries: the widest line of a drawing carries the most, and width grows with the square
 * root of the volume, so a hundred times the rows is ten times the ink and a small flow is still seen. */
export function edgeWidth(volume: number, largest: number): number {
  const thinnest = 1.5;
  const widest = 5;
  if (volume <= 0 || largest <= 0) {
    return thinnest;
  }

  return thinnest + (widest - thinnest) * Math.sqrt(Math.min(1, volume / largest));
}

/** One day of a node's chart: how much, and how the day went. */
export interface LensBar {
  value: number;
  outcome: Outcome;
  someFailed: boolean;
}

/** A node's chart over the window: one bar per day, the same measure its card leads with. */
export function nodeBars(spec: LensNodeSpec, days: number, figureOn: (flowId: string, day: number) => Figure): LensBar[] {
  const bars: LensBar[] = [];
  for (let day = 0; day < days; day++) {
    const figure = nodeFigure(spec, (flowId) => figureOn(flowId, day));
    // A node standing for several flows charts how many of them ran: their amounts are of different things.
    const value = spec.type === "flow" && spec.flows.length > 1
      ? spec.flows.filter((flowId) => figureOn(flowId, day).outcome !== "absent").length
      : figure.volume;
    bars.push({ value, outcome: figure.outcome, someFailed: figure.someFailed });
  }
  return bars;
}

/** Each flow's figure for each day of the window, computed once: `figures.get(flowId)?.[day]`, undefined for no run. */
export function figuresByDay(
  activity: FlowActivity, measureOf: (flowId: string) => RunMeasureOf | undefined,
): Map<string, (Figure | undefined)[]> {
  const figures = new Map<string, (Figure | undefined)[]>();
  for (const flow of activity.flows) {
    const days = new Array<Figure | undefined>(activity.windowDays).fill(undefined);
    const measure = measureOf(flow.pipelineId);
    for (const day of flow.days) {
      if (day.day >= 0 && day.day < activity.windowDays) {
        days[day.day] = figureOf(totalsOfDay(day), measure);
      }
    }
    figures.set(flow.pipelineId, days);
  }
  return figures;
}

/** The figure of a flow with no run: what `figuresByDay` leaves undefined. */
export const NO_RUN: Figure = figureOf(null);

/** Each flow's figure in one fire, from its runs there. A flow with no run in the fire has no entry. */
export function figuresOfFire(fire: FlowFire, measureOf: (flowId: string) => RunMeasureOf | undefined): Map<string, Figure> {
  const runs = new Map<string, FlowFireRun[]>();
  for (const entry of fire.runs) {
    const known = runs.get(entry.run.pipelineId);
    if (known === undefined) {
      runs.set(entry.run.pipelineId, [entry]);
    } else {
      known.push(entry);
    }
  }

  const figures = new Map<string, Figure>();
  for (const [flowId, entries] of runs) {
    figures.set(flowId, figureOf(totalsOfRuns(entries), measureOf(flowId)));
  }
  return figures;
}

/** How a day went over a set of flows: how many moved data, ran with no change, and had a failed run. */
export interface DayTally {
  moved: number;
  empty: number;
  failed: number;
}

/** The tally of each day of the window over the flows given: what the day rail charts. A flow with a failed run
 * counts as failed whatever its other runs did, because a failure is what a reader picks a day for. */
export function dayTallies(
  flowIds: readonly string[], days: number, figures: ReadonlyMap<string, readonly (Figure | undefined)[]>,
): DayTally[] {
  const tallies: DayTally[] = [];
  for (let day = 0; day < days; day++) {
    const tally: DayTally = { moved: 0, empty: 0, failed: 0 };
    for (const flowId of flowIds) {
      const figure = figures.get(flowId)?.[day];
      if (figure === undefined || figure.outcome === "absent") {
        continue;
      }

      if (figure.failed > 0) {
        tally.failed += 1;
      } else if (figure.outcome === "moved") {
        tally.moved += 1;
      } else {
        tally.empty += 1;
      }
    }
    tallies.push(tally);
  }
  return tallies;
}

/** The latest day any of the flows ran on; null when none ran in the window. */
export function latestDay(tallies: readonly DayTally[]): number | null {
  for (let day = tallies.length - 1; day >= 0; day--) {
    if (tallies[day].moved + tallies[day].empty + tallies[day].failed > 0) {
      return day;
    }
  }
  return null;
}

/** The nearest day with a run before (`step` -1) or after (`step` 1) the one given; null when there is none. */
export function neighbourDay(tallies: readonly DayTally[], day: number, step: -1 | 1): number | null {
  for (let at = day + step; at >= 0 && at < tallies.length; at += step) {
    if (tallies[at].moved + tallies[at].empty + tallies[at].failed > 0) {
      return at;
    }
  }
  return null;
}

/** A drawn flow the day rail may count: where data reaches it in the order of execution, and its kind's measure. */
export interface MovingFlow {
  id: string;
  /** Its place in the reading order (its wave): the rail lists what moved in the order data went through. */
  order: number;
  measure?: RunMeasureOf;
}

/**
 * One measure of what moved through a drawing: the drawn flows that count the same thing, so their amounts add up
 * to a figure that means something. Rows read by the loads, rows changed by the ingestions and records delivered are
 * three measures, never one sum.
 */
export interface MovementRow {
  key: string;
  /** The measure as a heading: "Rows read", "Rows changed", "Records delivered". */
  title: string;
  flows: readonly string[];
}

/** What a measure's flows moved in one period, added up. */
export interface MovementAmount {
  /** How many of its flows ran, and how many of those had a failed run. */
  ran: number;
  failed: number;
  moved: number;
  /** The files its flows took in, for a measure of flows that read files. */
  files: number;
  fileBytes: number;
}

const ROWS_READ = "Rows read";
const ROWS_CHANGED = "Rows changed";

/**
 * The measures of a drawing, in the order data goes through them. A kind with a measure of its own is counted under
 * that measure's words, and one that moves no data on (a check) is left out: what it counts is not data that moved.
 * One of SQLFlow's own kinds counts the rows it changed when its runs say what they inserted, updated and deleted,
 * and the rows it read otherwise (a file load, an export); one with no run in the window says neither, and has
 * nothing to chart.
 */
export function movementRows(
  flows: readonly MovingFlow[], figures: ReadonlyMap<string, readonly (Figure | undefined)[]>,
): MovementRow[] {
  const rows = new Map<string, { title: string; order: number; flows: string[] }>();
  for (const flow of flows) {
    let title: string;
    if (flow.measure !== undefined) {
      const measured = flow.measure({});
      if (measured.movesData === false) {
        continue;
      }
      title = measured.label === "" ? "Moved" : measured.label.charAt(0).toUpperCase() + measured.label.slice(1);
    } else {
      const ran = (figures.get(flow.id) ?? []).filter((figure) => figure !== undefined && figure.outcome !== "absent");
      if (ran.length === 0) {
        continue;
      }
      title = ran.some((figure) => figure !== undefined && figure.changes !== null) ? ROWS_CHANGED : ROWS_READ;
    }

    const row = rows.get(title);
    if (row === undefined) {
      rows.set(title, { title, order: flow.order, flows: [flow.id] });
    } else {
      row.order = Math.min(row.order, flow.order);
      row.flows.push(flow.id);
    }
  }

  return [...rows.values()]
    .sort((a, b) => a.order - b.order || a.title.localeCompare(b.title))
    .map((row) => ({ key: row.title.toLowerCase().replace(/[^a-z0-9]+/g, "-"), title: row.title, flows: row.flows }));
}

/** What a measure's flows moved in one period: `figureFor` gives a flow's figure in it. */
export function movementAmount(row: MovementRow, figureFor: (flowId: string) => Figure): MovementAmount {
  const amount: MovementAmount = { ran: 0, failed: 0, moved: 0, files: 0, fileBytes: 0 };
  for (const flowId of row.flows) {
    const figure = figureFor(flowId);
    if (figure.outcome === "absent") {
      continue;
    }

    amount.ran += 1;
    amount.failed += figure.failed > 0 ? 1 : 0;
    amount.moved += figure.moved;
    amount.files += figure.files;
    amount.fileBytes += figure.fileBytes;
  }
  return amount;
}

/** A measure's amount on each day of the window, oldest first: what its row of the day rail charts. */
export function movementDays(
  row: MovementRow, days: number, figureOn: (flowId: string, day: number) => Figure,
): MovementAmount[] {
  return Array.from({ length: days }, (_, day) => movementAmount(row, (flowId) => figureOn(flowId, day)));
}

/** A measure's amount in words: "991k in 57 files", "0", "1,204, 2 flows failed", "no run". */
export function movementText(amount: MovementAmount): string {
  if (amount.ran === 0) {
    return "no run";
  }

  const moved = amount.files > 0
    ? `${formatCount(amount.moved)} in ${formatCount(amount.files)} ${plural(amount.files, "file", "files")}`
    : formatCount(amount.moved);
  return amount.failed > 0 ? `${moved}, ${amount.failed} ${plural(amount.failed, "flow", "flows")} failed` : moved;
}

const DAY_MS = 86_400_000;

/** The UTC day a window's day index stands for, as the address and the API name it: `2026-01-31`. */
export function dayKey(fromUtc: string, day: number): string {
  return new Date(parseUtc(fromUtc).getTime() + day * DAY_MS).toISOString().slice(0, 10);
}

/** The window's day index of a day named `2026-01-31`; null for a day outside the window or not a date. */
export function dayIndex(fromUtc: string, key: string, days: number): number | null {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(key)) {
    return null;
  }

  const at = Date.parse(`${key}T00:00:00Z`);
  if (Number.isNaN(at)) {
    return null;
  }

  const index = Math.round((at - parseUtc(fromUtc).getTime()) / DAY_MS);
  return index >= 0 && index < days ? index : null;
}

/** A day as the page names it: "Mon 16 Nov". Days are the control plane's UTC days, so the name is the UTC date. */
export function dayLabel(fromUtc: string, day: number, withWeekday = true): string {
  return new Date(parseUtc(fromUtc).getTime() + day * DAY_MS).toLocaleDateString("en-GB", {
    ...(withWeekday ? { weekday: "short" as const } : {}), day: "numeric", month: "short", timeZone: "UTC",
  });
}

/** Whether a window's day is a Monday: where the day rail writes a date. */
export function startsWeek(fromUtc: string, day: number): boolean {
  return new Date(parseUtc(fromUtc).getTime() + day * DAY_MS).getUTCDay() === 1;
}

/** A fire's start as a chip names it within its day: the time, in the reader's own zone, to the second when asked
 * (two starts of one minute are told apart by it). */
export function fireTime(startedUtc: string, seconds = false): string {
  return parseUtc(startedUtc).toLocaleTimeString(undefined, {
    hour: "2-digit", minute: "2-digit", ...(seconds ? { second: "2-digit" as const } : {}),
  });
}

/** Whether a day's fires need their seconds to be told apart: two of them started in the same minute. */
export function firesShareAMinute(fires: readonly FlowFire[]): boolean {
  return new Set(fires.map((fire) => fireTime(fire.startedUtc))).size < fires.length;
}

/** What a fire ran, for its chip's tooltip: its flows by name, the first few and how many more. */
export function fireFlows(fire: FlowFire, shown = 4): string {
  const names = [...new Set(fire.runs.map((entry) => entry.run.flowName))];
  const more = names.length - shown;
  return more > 0 ? `${names.slice(0, shown).join(", ")} and ${more} more` : names.join(", ");
}

/** How a fire went: the count of runs that failed, are still under way, and that ran. */
export function fireTally(fire: FlowFire): { runs: number; failed: number; underWay: number } {
  let failed = 0;
  let underWay = 0;
  for (const { run } of fire.runs) {
    failed += run.status === "failed" ? 1 : 0;
    underWay += run.status === "running" || run.status === "queued" ? 1 : 0;
  }
  return { runs: fire.runs.length, failed, underWay };
}

/** "3 runs, 1 failed": a fire's tally in words, the bad news first after the size. */
export function fireTallyText(fire: FlowFire): string {
  const tally = fireTally(fire);
  const parts = [tally.runs === 1 ? "1 run" : `${tally.runs} runs`];
  if (tally.failed > 0) {
    parts.push(`${tally.failed} failed`);
  }
  if (tally.underWay > 0) {
    parts.push(`${tally.underWay} under way`);
  }
  if (parts.length === 1) {
    parts.push(tally.runs === 1 ? "succeeded" : "all succeeded");
  }
  return parts.join(", ");
}

/** The fires of a day that ran any of the flows given, each cut down to those flows' runs, oldest first. */
export function firesOf(fires: readonly FlowFire[], flowIds: ReadonlySet<string>): FlowFire[] {
  return fires
    .map((fire) => ({ ...fire, runs: fire.runs.filter((entry) => flowIds.has(entry.run.pipelineId)) }))
    .filter((fire) => fire.runs.length > 0)
    .sort((a, b) => a.startedUtc.localeCompare(b.startedUtc) || a.key.localeCompare(b.key));
}
