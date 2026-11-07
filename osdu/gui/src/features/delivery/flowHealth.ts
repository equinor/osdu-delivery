// What the Delivery page says of each delivery flow (osdu/docs/reference/concepts/gui-ledger.md, Delivery): where its
// records stand, the one thing that most needs an operator (its verdict), and where to go to act on it; the ring that draws
// the shares; the order the cards take, the flows that need someone first; and the line of totals heading the page. The
// shapes are the API's counts as far as these read them, so the unit suite can check the words without the client.

/** A tone of the status palette (DESIGN.md 3.2), or the quiet text tone for what asks nothing of anyone. */
export type HealthTone = "success" | "warning" | "destructive" | "info" | "muted";

/** A flow's counts, as the stats endpoint answers them (DeliveryFlowStats). */
export interface FlowCounts {
  total: number;
  pending: number;
  delivering: number;
  delivered: number;
  held: number;
  failed: number;
  deleted: number;
  waiting: number;
  drifted: number;
  reverted?: number;
  adoptable?: number;
  stranded?: number;
  restricted?: number;
  stepsHeld?: number;
  lastSubmission: { submissionId: string; status: string; error: string | null } | null;
}

/** Each status a record can be in, in the order a record moves through them, so the ring reads from done to stuck. */
export type StatusKey = "delivered" | "pending" | "waiting" | "held" | "failed" | "deleted" | "reverted";

/** One status's share of a flow's records: its word, its count and what it means. */
export interface StatusShare {
  key: StatusKey;
  label: string;
  count: number;
  meaning: string;
}

/**
 * Every status with its count, the ones holding no records included, in the order a record moves through them. The
 * Delivery page's ring and a flow's status bar both read it, so the two say a status the same way.
 */
export function statusShares(counts: FlowCounts): StatusShare[] {
  const sending = counts.pending + counts.delivering;
  return [
    { key: "delivered", label: "delivered", count: counts.delivered, meaning: "Delivered, as its latest version." },
    {
      key: "pending",
      label: "pending",
      count: sending,
      meaning: counts.delivering > 0
        ? `Planned and not yet delivered; ${counts.delivering.toLocaleString()} being sent now.`
        : "Planned and not yet delivered.",
    },
    { key: "waiting", label: "waiting", count: counts.waiting, meaning: "Waiting for a record it refers to." },
    { key: "held", label: "held", count: counts.held, meaning: "Held back until its row changes or someone releases it." },
    { key: "failed", label: "failed", count: counts.failed, meaning: "Failed on every try." },
    {
      key: "deleted",
      label: "deleted",
      count: counts.deleted,
      meaning: "Removed from OSDU: by an operator, sent again when its row is read; by a reversal, blocked until its row changes or it is released; by a removal flow, kept out until a row of its key newer than its removal arrives.",
    },
    {
      key: "reverted",
      label: "reverted",
      count: counts.reverted ?? 0,
      meaning: "Put back by a reversal to the version OSDU held before, and blocked until its source changes or it is released.",
    },
  ];
}

/** A count's share of the whole, as a reader takes it in: whole percents, and "<1%" for a share too small to round up to one. */
export function percentOf(count: number, total: number): string {
  if (total <= 0) {
    return "0%";
  }

  const share = (count / total) * 100;
  return share > 0 && share < 1 ? "<1%" : `${Math.round(share)}%`;
}

/**
 * How much of a flow is delivered, for the ring's middle: never 100% while one record is not, however many there are, and
 * "<1%" while some are and too few to make one percent. Null for a flow that holds no records, which has no share.
 */
export function deliveredPercent(counts: Pick<FlowCounts, "total" | "delivered">): string | null {
  if (counts.total <= 0) {
    return null;
  }

  if (counts.delivered >= counts.total) {
    return "100%";
  }

  const share = (counts.delivered / counts.total) * 100;
  return share > 0 && share < 1 ? "<1%" : `${Math.floor(share)}%`;
}

/** Where a verdict sends a reader to act on it: one of the flow's tabs, narrowed as its address allows, or a submission. */
export type VerdictTarget =
  | { tab: "delivery" | "issues" | "records"; status?: StatusKey; drifted?: boolean }
  | { submissionId: string };

/** The one thing a flow's card says first: what most needs someone, in a line, and why at more length on hover. */
export interface Verdict {
  kind: "empty" | "submission-failed" | "failed" | "held" | "drifted" | "delivering" | "waiting" | "reverted" | "delivered";
  tone: HealthTone;
  /** The line under the flow's name: a few words a reader takes in at a glance. */
  headline: string;
  /** What the line means and what to do about it, for its hover; plain words, whole sentences. */
  explanation: string;
  /** Whether the work is moving now (records being sent, a submission being planned), so its glyph turns. */
  moving: boolean;
  /** Lower comes first: the flows that need someone lead the page. */
  rank: number;
  /** Where acting on it starts; null when there is nothing to act on, and the card itself opens the flow. */
  target: VerdictTarget | null;
}

const MOVING_SUBMISSION = new Set(["received", "planned", "running"]);

function records(count: number): string {
  return `${count.toLocaleString()} record${count === 1 ? "" : "s"}`;
}

function these(count: number): string {
  return count === 1 ? "It is" : "They are";
}

/**
 * The verdict on a flow's records, worst first: a failed submission or failed records, then held ones, then drift, then
 * work still moving, and last a flow whose records are all delivered. Each says what it is in a few words and, on hover,
 * what it means and where to act on it.
 */
export function flowVerdict(counts: FlowCounts): Verdict {
  const submission = counts.lastSubmission;
  const sending = counts.pending + counts.delivering;
  const reverted = counts.reverted ?? 0;
  const adoptable = counts.adoptable ?? 0;
  const stranded = counts.stranded ?? 0;

  if (submission !== null && submission.status === "failed") {
    return {
      kind: "submission-failed",
      tone: "destructive",
      headline: "Last submission failed",
      explanation: `The flow's last submission failed before its records were planned${submission.error ? `: ${submission.error}` : "."}`
        + `${submission.error && !/[.!?]$/.test(submission.error) ? "." : ""} The submission's page has its trace; the next run plans the records again.`,
      moving: false,
      rank: 0,
      target: { submissionId: submission.submissionId },
    };
  }

  if (counts.failed > 0) {
    const alsoHeld = counts.held > 0 ? ` and ${counts.held.toLocaleString()} held` : "";
    return {
      kind: "failed",
      tone: "destructive",
      headline: `${counts.failed.toLocaleString()} failed${counts.held > 0 ? `, ${counts.held.toLocaleString()} held` : ""}`,
      explanation: `${records(counts.failed)} failed on every try${alsoHeld}. The flow's Issues tab groups them by why, says what to fix, and releases each issue's records to be sent again.`,
      moving: false,
      rank: 0,
      target: { tab: "issues" },
    };
  }

  if (counts.held > 0) {
    const allAdoptable = adoptable >= counts.held;
    const headline = allAdoptable
      ? `${counts.held.toLocaleString()} held, ${counts.held === 1 ? "adoptable" : "all adoptable"}`
      : adoptable > 0
        ? `${counts.held.toLocaleString()} held, ${adoptable.toLocaleString()} adoptable`
        : `${counts.held.toLocaleString()} held`;
    const adopt = adoptable > 0
      ? ` ${allAdoptable ? (counts.held === 1 ? "It is" : "Every one is") : `${adoptable.toLocaleString()} of them are`} held because OSDU already holds a record at the id ${adoptable === 1 ? "its key makes" : "their keys make"}, written by another system or another instance of the delivery: Adopt, on the flow's Delivery tab, takes ${adoptable === 1 ? "it" : "them"} over, and the next run delivers ${adoptable === 1 ? "it" : "them"}.`
      : "";
    const still = stranded > 0
      ? ` ${stranded.toLocaleString()} will not move by ${stranded === 1 ? "itself" : "themselves"}: nothing plans ${stranded === 1 ? "it" : "them"} again until ${stranded === 1 ? "it is" : "they are"} released.`
      : "";
    const why = allAdoptable ? "" : " The flow's Issues tab groups them by why and releases each issue's records.";
    return {
      kind: "held",
      tone: "warning",
      headline,
      explanation: `${records(counts.held)} held back until ${counts.held === 1 ? "its row changes or someone releases it" : "their rows change or someone releases them"}.${adopt}${still}${why}`,
      moving: false,
      rank: 1,
      target: allAdoptable ? { tab: "delivery" } : { tab: "issues" },
    };
  }

  if (counts.drifted > 0) {
    return {
      kind: "drifted",
      tone: "warning",
      headline: `${counts.drifted.toLocaleString()} drifted since the last verify`,
      explanation: `The last verify found OSDU holding another version, other content, or nothing, for ${records(counts.drifted)} the ledger says ${counts.drifted === 1 ? "is" : "are"} delivered, or still holding ${counts.drifted === 1 ? "one" : "some"} the ledger removed. Redeliver, on the flow's Delivery tab, sends the ledger's version again.`,
      moving: false,
      rank: 2,
      target: { tab: "records", drifted: true },
    };
  }

  const submitting = submission !== null && MOVING_SUBMISSION.has(submission.status);
  if (sending > 0 || submitting) {
    return {
      kind: "delivering",
      tone: "info",
      headline: sending > 0 ? `Delivering, ${sending.toLocaleString()} to go` : "Planning a submission",
      explanation: sending > 0
        ? `${records(sending)} planned and not yet delivered${counts.delivering > 0 ? `, ${counts.delivering.toLocaleString()} being sent now` : ""}. The counts follow the run within seconds.`
        : `The flow's last submission is ${submission?.status ?? "received"}: its records are counted once it has planned them.`,
      moving: counts.delivering > 0 || submitting,
      rank: 3,
      target: sending > 0 ? { tab: "records", status: "pending" } : null,
    };
  }

  if (counts.waiting > 0) {
    return {
      kind: "waiting",
      tone: "info",
      headline: `${counts.waiting.toLocaleString()} waiting for records they refer to`,
      explanation: `${records(counts.waiting)} wait for a record ${counts.waiting === 1 ? "it refers" : "they refer"} to that has not landed. ${these(counts.waiting)} sent once it does.`,
      moving: false,
      rank: 3,
      target: { tab: "records", status: "waiting" },
    };
  }

  if (counts.total === 0) {
    return {
      kind: "empty",
      tone: "muted",
      headline: "No records yet",
      explanation: "The ledger holds no records for this flow yet: they appear once its first submission has been planned, when the flow next runs.",
      moving: false,
      rank: 5,
      target: null,
    };
  }

  if (reverted > 0) {
    return {
      kind: "reverted",
      tone: "muted",
      headline: `${reverted.toLocaleString()} reverted`,
      explanation: `${records(reverted)} put back by a reversal to the version OSDU held before. ${these(reverted)} blocked until ${reverted === 1 ? "its" : "their"} source changes or someone releases ${reverted === 1 ? "it" : "them"}.`,
      moving: false,
      rank: 4,
      target: { tab: "records", status: "reverted" },
    };
  }

  const removed = counts.deleted > 0 ? `, ${counts.deleted.toLocaleString()} removed` : "";
  return {
    kind: "delivered",
    tone: "success",
    headline: counts.delivered === 1 && counts.deleted === 0 ? "Delivered" : `All ${counts.delivered.toLocaleString()} delivered${removed}`,
    explanation: `Every record the ledger holds for this flow is delivered as its latest version${counts.deleted > 0 ? `, and ${counts.deleted.toLocaleString()} ${counts.deleted === 1 ? "was" : "were"} removed` : ""}. Nothing waits on anyone.`,
    moving: false,
    rank: 6,
    target: null,
  };
}

/** One arc of the ring: where along the circle it starts and how long it is, both in the circle's own length. */
export interface RingArc {
  key: StatusKey;
  start: number;
  length: number;
}

/**
 * The ring's arcs, one per status holding records, laid around a circle of the given length with a gap between two arcs.
 * Every arc keeps at least `minLength`, so one failed record among thousands still shows, and the others share what is
 * left by their counts. A single status is the whole circle, with no gap.
 */
export function ringArcs(shares: readonly StatusShare[], circumference: number, gap: number, minLength: number): RingArc[] {
  const shown = shares.filter((share) => share.count > 0);
  const total = shown.reduce((sum, share) => sum + share.count, 0);
  if (total === 0 || circumference <= 0) {
    return [];
  }

  if (shown.length === 1) {
    return [{ key: shown[0]!.key, start: 0, length: circumference }];
  }

  const room = Math.max(0, circumference - gap * shown.length);
  // Arcs below the floor take the floor; the rest share what the floors leave, in proportion, until no share falls short.
  let floored = new Set<StatusKey>();
  for (;;) {
    const free = room - floored.size * minLength;
    const sharing = shown.filter((share) => !floored.has(share.key));
    const counted = sharing.reduce((sum, share) => sum + share.count, 0);
    const short = sharing.filter((share) => (share.count / counted) * free < minLength);
    if (short.length === 0 || short.length === sharing.length) {
      break;
    }

    floored = new Set([...floored, ...short.map((share) => share.key)]);
  }

  const free = Math.max(0, room - floored.size * minLength);
  const counted = shown.filter((share) => !floored.has(share.key)).reduce((sum, share) => sum + share.count, 0);
  const arcs: RingArc[] = [];
  let start = gap / 2;
  for (const share of shown) {
    const length = floored.has(share.key) || counted === 0 ? Math.min(minLength, room / shown.length) : (share.count / counted) * free;
    arcs.push({ key: share.key, start, length });
    start += length + gap;
  }

  return arcs;
}

/**
 * The order the cards take: the flows that need someone first (worst verdict first), then the ones still moving, then
 * the delivered, and last those that deliver only to other partitions; flows of one standing keep the order they came in.
 * A flow whose counts have not answered yet keeps its place among the rest, ranked as the quietest.
 */
export function attentionOrder(ranks: readonly (number | null)[], elsewhere: readonly boolean[]): number[] {
  const LAST = Number.MAX_SAFE_INTEGER;
  return ranks
    .map((rank, index) => ({ index, rank: elsewhere[index] ? LAST : rank ?? LAST - 1 }))
    .sort((a, b) => a.rank - b.rank || a.index - b.index)
    .map((entry) => entry.index);
}

/** What the page's flows hold in its partition, added up (DeliveryTotals). */
export interface OverviewTotals {
  total: number;
  delivered: number;
  pending: number;
  held: number;
  failed: number;
  drifted: number;
}

/**
 * The page's line of totals: how many flows deliver here, how much of what they hold is delivered, and only the counts
 * that ask for someone (failed, held, drifted, pending), so a partition where everything is delivered reads as one fact.
 */
export function overviewSubtitle(flows: number, active: string | null, totals: OverviewTotals, needing: number): string {
  const where = active === null ? "" : ` in ${active}`;
  const head = `${flows.toLocaleString()} delivery flow${flows === 1 ? "" : "s"}${where}`;
  if (totals.total === 0) {
    return `${head}, holding no records yet.`;
  }

  const share = deliveredPercent(totals) ?? "0%";
  const delivered = totals.delivered >= totals.total
    ? `all ${totals.total.toLocaleString()} records delivered`
    : `${share} of ${totals.total.toLocaleString()} records delivered`;
  const asks = [
    totals.failed > 0 ? `${totals.failed.toLocaleString()} failed` : null,
    totals.held > 0 ? `${totals.held.toLocaleString()} held` : null,
    totals.drifted > 0 ? `${totals.drifted.toLocaleString()} drifted` : null,
    totals.pending > 0 ? `${totals.pending.toLocaleString()} pending` : null,
  ].filter((part): part is string => part !== null);
  const attention = needing > 0 ? ` ${needing.toLocaleString()} flow${needing === 1 ? " needs" : "s need"} attention, listed first.` : "";
  return `${head}: ${delivered}${asks.length > 0 ? `; ${asks.join(", ")}` : ""}.${attention}`;
}
