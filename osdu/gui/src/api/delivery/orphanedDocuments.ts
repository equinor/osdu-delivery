// The ledgers a missing flow document left behind, as the delivery API answers them: each document with its ledgers and
// who can take them over, a takeover, and deleting what it left.

/** The run that deletes the ledger: it removes every record from OSDU reversibly, then deletes everything the ledgers keep. */
/** One orphaned ledger of a missing document: one interface's in one partition, with what it holds. */
export interface OrphanedLedger {
  ledger: string;
  partition: string;
  /** Empty for a flow in the single form, and for every kind but delivery. */
  interface: string;
  ledgerName: string;
  rows: number;
  claimed: number;
  mapping?: string | null;
  recordObject?: string | null;
}

/** A declared flow that can take over a missing document's ledgers, as the Orphaned ledgers page offers it. */
export interface TakeoverCandidate {
  repoId: string;
  flowName: string;
  relativePath: string;
  interfaces: string[];
  mappings: string[];
  /** What it shares with the missing document: file, mapping, record table, interface, folder. */
  shares: string[];
  /** How many of the missing document's ledgers it would take. */
  takes: number;
  suggested: boolean;
  /** Where a takeover by this flow would hand each of the missing document's ledgers. */
  plan: TakeoverPlanItem[];
}

/** Where a takeover would hand one orphaned ledger: the interface and ledger it goes to, or nothing when it stays orphaned. */
export interface TakeoverPlanItem {
  ledger: string;
  /** The interface it goes to: empty for a flow in the single form and every kind but delivery. */
  toInterface?: string | null;
  /** The ledger it goes to; absent when the flow keeps no counterpart of it, and it stays orphaned. */
  toLedgerName?: string | null;
}

/**
 * A missing document: a flow no repository declares any more whose ledgers hold anything, where it was last declared, and
 * the declared flows that can take its ledgers over, the likeliest first.
 */
export interface OrphanedDocument {
  kind: string;
  flowName: string;
  relativePath?: string | null;
  leftUtc?: string | null;
  rows: number;
  claimed: number;
  ledgers: OrphanedLedger[];
  candidates: TakeoverCandidate[];
}

/** What one ledger's move came to: completed, running (it waits for work in flight), failed, or skipped (it stays orphaned). */
export interface LedgerMoveResult {
  activityId: number;
  partition: string;
  from: string;
  to: string;
  outcome: "completed" | "running" | "failed" | "skipped";
  records: number;
  superseded: number;
  summary: string;
}

/** What a takeover did to each ledger of the missing document, and how many ended each way. */
export interface DeliveryTakeoverAnswer {
  ledgers: LedgerMoveResult[];
  completed: number;
  waiting: number;
  failed: number;
  skipped: number;
}

/** A deletion, or its preview: the missing flow of `kind` and the one partition whose orphaned ledgers of it go; a deletion names that partition again as `confirm`. */
export interface OrphanDeleteRequest {
  kind: string;
  flow: string;
  partition: string;
  confirm?: string;
}

/** One orphaned ledger as a deletion's preview shows it: what it holds, and why it cannot be deleted now when it cannot. */
export interface OrphanDeleteLedger {
  ledger: string;
  partition: string;
  /** Empty for a flow in the single form, and for every kind but delivery. */
  interface: string;
  ledgerName: string;
  rows: number;
  claimed: number;
  /** Why it stays, in full, with what to do instead: it knows something of OSDU nothing else does, or something declared still reads it. Absent when it can go. */
  refusal?: string | null;
  /** The same in a few words, for the line beside the ledger: `OSDU may still hold 12 of its records`. */
  refusedFor?: string | null;
}

/** What deleting a missing document would do in a partition: each ledger, and how many can go and how many are refused. */
export interface OrphanDeletePreview {
  kind: string;
  flowName: string;
  partition: string;
  ledgers: OrphanDeleteLedger[];
  deletable: number;
  refused: number;
}

/** What one ledger's deletion came to: completed, running (it waits for work in flight, or another process is at it), or failed with why. */
export interface LedgerDeleteResult {
  /** 0 for a ledger refused before anything was asked. */
  activityId: number;
  partition: string;
  ledger: string;
  interface: string;
  outcome: "completed" | "running" | "failed";
  rows: number;
  summary: string;
}

/** What a deletion did to each ledger of the missing document, and how many ended each way. */
export interface OrphanDeleteAnswer {
  ledgers: LedgerDeleteResult[];
  completed: number;
  waiting: number;
  failed: number;
}

/** A takeover: the missing flow of `kind`, the declared flow that takes it over, and the one partition it acts in. */
export interface DeliveryTakeoverRequest {
  kind: string;
  from: string;
  to: string;
  partition?: string | null;
}
