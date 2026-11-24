// What a run of each of the module's flow kinds moved, read from the counts its result reports: the figure the lineage
// graph's data view leads the kind's cards with, charts day by day and weighs its lines by
// (sqlflow/docs/reference/guides/lineage-graph.md, What moved). The counts may be one run's or the sum over a day's
// runs, so each measure only adds up and picks out; none divides one count by another.

/** A kind's measure, in the shape SQLFlow's `RunMeasure` takes. */
export interface KindMeasure {
  moved: number;
  label: string;
  taken?: number;
  takenLabel?: string;
  problems?: number;
  problemsLabel?: string;
  details?: { label: string; value: number }[];
}

type Counts = Readonly<Record<string, number>>;

function of(counts: Counts, name: string): number {
  return counts[name] ?? 0;
}

function words(count: number, one: string, many: string): string {
  return count === 1 ? one : many;
}

/**
 * A delivery flow moves records into OSDU: what it delivered is what it moved on, of what it planned; what failed is its
 * problem, and what it found unchanged, held or left waiting is listed beside. A day of a flow that only verified or
 * planned delivered nothing, and says so.
 */
export function deliveryMeasure(counts: Counts): KindMeasure {
  const delivered = of(counts, "delivered");
  const planned = of(counts, "planned");
  return {
    moved: delivered,
    label: words(delivered, "record delivered", "records delivered"),
    ...(planned > 0 && planned !== delivered ? { taken: planned, takenLabel: "planned" } : {}),
    problems: of(counts, "failed"),
    problemsLabel: "failed",
    details: [
      { label: "unchanged", value: of(counts, "skippedUnchanged") + of(counts, "unchangedAtPush") },
      { label: "held", value: of(counts, "held") },
      { label: "blocked", value: of(counts, "blocked") },
      { label: "awaiting approval", value: of(counts, "awaitingApproval") },
      { label: "waiting for another record", value: of(counts, "waiting") },
      { label: "removed", value: of(counts, "removed") },
    ],
  };
}

/** A dimension flow builds tables from what OSDU holds: the rows it loaded into them are what it moved. */
export function dimensionMeasure(counts: Counts): KindMeasure {
  const rows = of(counts, "rowsLoaded");
  return {
    moved: rows,
    label: words(rows, "row built", "rows built"),
    problems: of(counts, "failed") + of(counts, "viewsFailed"),
    problemsLabel: "failed",
    details: [
      { label: "dimensions built", value: of(counts, "built") },
      { label: "dimensions skipped", value: of(counts, "skipped") },
    ],
  };
}

/** A cache flow brings reference data in from OSDU: the items it cached are what it moved. */
export function cacheMeasure(counts: Counts): KindMeasure {
  const items = of(counts, "items");
  return {
    moved: items,
    label: words(items, "item cached", "items cached"),
    details: [{ label: "records affected", value: of(counts, "affectedRecords") }],
  };
}

/** An assertion flow moves nothing on: it checks, so its measure is the checks that passed, of those it ran, and the
 * ones that failed or could not run are its problem. */
export function assertionMeasure(counts: Counts): KindMeasure {
  const passed = of(counts, "passed");
  const run = of(counts, "tests");
  return {
    moved: passed,
    label: words(passed, "check passed", "checks passed"),
    ...(run !== passed ? { taken: run, takenLabel: "run" } : {}),
    problems: of(counts, "failed") + of(counts, "errored"),
    problemsLabel: "failed",
    details: [
      { label: "warned", value: of(counts, "warned") },
      { label: "skipped", value: of(counts, "skipped") },
    ],
  };
}

/** An inventory flow reads what OSDU holds: the inventories it completed are its measure. */
export function inventoryMeasure(counts: Counts): KindMeasure {
  const completed = of(counts, "completed");
  return {
    moved: completed,
    label: words(completed, "inventory completed", "inventories completed"),
    problems: of(counts, "failed"),
    problemsLabel: "failed",
  };
}
