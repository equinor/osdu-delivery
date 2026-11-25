/**
 * What a run of a flow kind moved, for a kind whose runs count something other than rows: a delivery counts the
 * records it delivered, a check the tests that passed. SQLFlow's own kinds report rows read, inserted, updated and
 * deleted, which need no translation; a kind a module adds reports whole-number counts in its result, and this says
 * which of them is the amount the run moved on, in the words a card puts beside it.
 */
export interface RunMeasure {
  /** How much the run moved on: the number a card leads with, and what its lines are weighed by. */
  moved: number;
  /** The words after that number: "records delivered", "rows built". Lower case, as they follow a figure. */
  label: string;
  /** How much reached the run, when that is counted and differs from what it moved on. */
  taken?: number;
  /** The words after `taken`: "planned", "checked". */
  takenLabel?: string;
  /** How many of what reached the run it could not move on: a problem, said in the status colour. */
  problems?: number;
  /** The word after `problems`: "failed", "blocked". */
  problemsLabel?: string;
  /** Other counts worth listing where there is room, largest news first. Zeroes are left out by the reader. */
  details?: readonly { label: string; value: number }[];
  /**
   * False for a kind that moves no data on: a check counts what passed, an inventory what it completed. Its cards
   * still lead with the amount, and the totals of what moved through a drawing leave it out. Absent means it does.
   */
  movesData?: boolean;
}

/**
 * A flow kind's measure, read from the counts a result reports (`RunSummary.resultCounts`). The counts may be one
 * run's or the sum over several runs of the kind, so the function only adds up or picks out: it never divides one
 * count by another, and never reads a count that is an identifier.
 */
export type RunMeasureOf = (counts: Readonly<Record<string, number>>) => RunMeasure;
