// The interventions on a flow's records through the delivery API, and what each answers: release, the issues keeping
// records blocked, adoption, sync, redelivery and rerendering, and the filter naming the records they act on.

import type { RunStatus } from "@/api/types";
import type { DeliveryTriggerKind } from "./attempts";
import type { DeliveryRecord, DeliveryRecordStatus } from "./records";

export interface DeliveryRunAccepted {
  runId: string;
  status: RunStatus;
}

export interface DeliveryReleaseResult {
  released: number;
  /** The deliver run queued with the release, when one was asked for. */
  runId?: string | null;
}

/**
 * One issue keeping a flow's records blocked: the error its records share with every part that names a record (a value,
 * an id, a moment, a number, a file) replaced by a placeholder such as `<value>`, how many records it keeps blocked, held
 * and failed, when they last changed, and its most recently changed record, with that record's own error.
 */
export interface DeliveryIssue {
  /** The issue's id: sixteen hexadecimal characters, what the records listing's `issue` filter takes. */
  issue: string;
  pattern: string;
  records: number;
  held: number;
  failed: number;
  /** When the record of the issue that changed longest ago last changed. */
  oldestUtc: string;
  /** When its most recently changed record last changed. */
  newestUtc: string;
  example: DeliveryRecord | null;
  /**
   * Where the issue lies: `set` when its records carry the same error (no value of their own, or the same values in
   * each), the mistake made once for the whole set; `rows` when they name values of their own rows, each to be fixed in
   * its row. The listing reads it from the newest and the oldest record; an issue's samples tell it more surely.
   */
  shape: DeliveryIssueShape;
  /** The values the example's error names (what its row held); for a set error, the values every record names. */
  values: string[];
  /**
   * The error as the records looked at say it: the pattern with every part they all name written in (a value, a number, a
   * moment), and the placeholder kept where they differ. The listing reads it from the newest and the oldest record; an
   * issue's own page from its samples.
   */
  shared: string;
}

export type DeliveryIssueShape = "set" | "rows";

/** A record of an issue looked at closely, with the values its error names. */
export interface DeliveryIssueSample {
  record: DeliveryRecord;
  values: string[];
}

/** A flow's issues, the most records first, with how many there are in all and how many blocked records are not sorted yet. */
export interface DeliveryIssueList {
  issues: DeliveryIssue[];
  totalIssues: number;
  totalRecords: number;
  /** Blocked records held or failed before the ledger kept issues, which the control plane sorts a page at a time. */
  unsorted: number;
}

/** An ingestion file some of an issue's records came from; a null name for the records that name none. */
export interface DeliveryIssueFile {
  fileName: string | null;
  records: number;
}

export interface DeliveryIssueDetail {
  /** The issue, its shape as its samples tell it. */
  issue: DeliveryIssue;
  /** The files its records came from, the most records first. */
  files: DeliveryIssueFile[];
  /** Records spread evenly across the issue: its newest, its oldest and the ones between. */
  samples: DeliveryIssueSample[];
}

/** What a release of an issue's records did, and the deliver run it queued when asked to. */
export interface DeliveryIssueReleaseResult {
  issue: string;
  released: number;
  runId: string | null;
}

/**
 * What an adoption asked: how many records it reached (named, matched, or held for an id OSDU holds), how many it asked to
 * adopt, and the deliver run it queued, which takes them over, when asked to.
 */
export interface DeliveryAdoptResult {
  selected: number;
  asked: number;
  runId: string | null;
}

/** What an adoption of an issue's records asked, and the deliver run it queued when asked to. */
export interface DeliveryIssueAdoptResult {
  issue: string;
  asked: number;
  runId: string | null;
}

/**
 * An adoption asked of many records: the ones `keys` names, every one `filter` matches (with the `expected` count shown), or
 * with neither every record held for an id OSDU holds, of every interface when the scope names none. `confirm` is the
 * partition the ledgers are kept in, typed back.
 */
export interface DeliveryAdoptRequest extends DeliveryManyRecords {
  confirm: string;
}

export interface DeliveryRedeliverResult {
  marked: number;
  runId: string | null;
}

/** The listing a removal is aimed at: the same filter the records list is built from. */
export interface DeliveryRecordFilter {
  status?: DeliveryRecordStatus;
  search?: string;
  mode?: "prefix" | "contains";
  submissionId?: string;
  /** The records a submission delivered: what "remove the batch we ran" acts on. */
  deliveredBy?: string;
  runId?: string;
  drifted?: boolean;
  /** The blocked records one issue keeps blocked, by its id. */
  issue?: string;
  /** The records whose last render set an enrichment aside on its hold. */
  stepHeld?: boolean;
  /** The records an operator restricted in OSDU, or asked to: restricted by an operator. */
  restricted?: boolean;
  /** The records OSDU holds restricted until complete. */
  incomplete?: boolean;
  /** The records restricted until complete for one set of missing values, by its group. */
  missing?: string;
  /** The records whose access in OSDU is restricted either way, until complete or by an operator: what the Restricted figure counts. */
  accessRestricted?: boolean;
  /**
   * With `deliveredBy` or `runId`: only the records it delivered for one kind of trigger, as its count of that cause stands
   * for them (osdu/docs/reference/concepts/delivery-reasons.md).
   */
  trigger?: DeliveryTriggerKind;
}

/**
 * Which records of a flow a timeline sync reads: those `keys` names, every one `filter` matches (resolved when the sync
 * is queued; `expected` is the count the operator was shown, and the sync is refused when the filter no longer resolves to
 * it), or, with neither, every record of the flow's interface.
 */
export interface DeliverySyncRequest {
  keys?: string[];
  filter?: DeliveryRecordFilter;
  expected?: number;
}

/**
 * Which of a flow's records a many-record intervention acts on: those `keys` names, every one `filter` matches (refused when
 * it no longer matches the `expected` count the operator was shown), or with `all` every record the flow has delivered.
 * Exactly one of the three is given; the API refuses a request that names none rather than taking it for every record.
 * With `run` (the default) a deliver run is queued that acts on them; without it the flow's next run does.
 */
export interface DeliveryManyRecords {
  keys?: string[];
  filter?: DeliveryRecordFilter;
  expected?: number;
  all?: boolean;
  run?: boolean;
}

/** A redelivery of many records: `scope` is the part to send again whatever the hashes say, all when it names none. */
export interface DeliveryFlowRedeliverRequest extends DeliveryManyRecords {
  scope?: string;
}

/** How many records were asked to be brought up to date, and the deliver run that brings them when one was queued. */
export interface DeliveryRerenderResult {
  marked: number;
  runId?: string | null;
}

/**
 * The plan run that says what bringing a selection up to date would send: the run to watch, how many delivered records of
 * the selection it checks, and how many the selection holds (`selectedCapped` when the count stopped at the listing's bound).
 */
export interface DeliveryRerenderPreview {
  runId: string;
  checked: number;
  selected: number;
  selectedCapped: boolean;
}
