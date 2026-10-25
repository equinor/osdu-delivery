import type { RunDetail } from "@/api/types";
import { firstRecordsOf } from "./firstRecords";

// What a delivery, retrieval or cache run was asked to do and what it reported, read from the run page's generic fields:
// the kind-owned payload the trigger sent, and the bounded result JSON the run wrote when it finished. The field names
// are the ones the delivery kind's outcomes carry (a deliver run's planned and delivered counts, a verify run's checked,
// matched, drifted and missing ones).

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

/** The run's result object, or null: before the run finished, for a kind that reports none, or for text that is not an object. */
export function runResult(run: RunDetail): Record<string, unknown> | null {
  if (run.resultJson === null) {
    return null;
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(run.resultJson);
  } catch {
    // The result is bounded when it is stored, so an oversized one arrives cut off; the outcome card still shows the text.
    return null;
  }

  return isRecord(parsed) ? parsed : null;
}

function count(result: Record<string, unknown>, field: string): number | null {
  const value = result[field];
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

export interface RunRecordCounts {
  planned: number | null;
  delivered: number | null;
  held: number | null;
  failed: number | null;
  unchanged: number | null;
  /** Records left waiting for a record they refer to; null when the run reports none. */
  waiting: number | null;
}

/** The record counts a run reported: a deliver run's own, or a verify run's read as the same headline numbers. */
export function runRecordCounts(result: Record<string, unknown> | null): RunRecordCounts {
  // An undo run counts artifacts, not records: its own card says what became of them.
  if (result === null || result.operation === "undo") {
    return { planned: null, delivered: null, held: null, failed: null, unchanged: null, waiting: null };
  }

  const drifted = count(result, "drifted");
  const missing = count(result, "missing");
  return {
    planned: count(result, "planned") ?? count(result, "checked"),
    delivered: count(result, "delivered") ?? count(result, "matched"),
    held: count(result, "held") ?? (drifted === null && missing === null ? null : (drifted ?? 0) + (missing ?? 0)),
    failed: count(result, "failed") ?? count(result, "errors"),
    unchanged: count(result, "skippedUnchanged"),
    waiting: count(result, "waiting"),
  };
}

/** What a remove run reported of the records it selected; each null when the run reports none of it. */
export interface RunRemovalCounts {
  selected: number | null;
  removed: number | null;
  restored: number | null;
  alreadyGone: number | null;
  skipped: number | null;
  failed: number | null;
  /** Of the records removed or already gone, those deleted from the ledger as well. */
  purged: number | null;
}

/** The counts a remove run reported; null before it finished, and for every other run. */
export function runRemovalCounts(result: Record<string, unknown> | null): RunRemovalCounts | null {
  if (result === null || result.operation !== "remove") {
    return null;
  }

  return {
    selected: count(result, "selected"),
    removed: count(result, "removed"),
    restored: count(result, "restored"),
    alreadyGone: count(result, "alreadyGone"),
    skipped: count(result, "skipped"),
    failed: count(result, "failed"),
    purged: count(result, "purged"),
  };
}

/** What one of a pipeline's cache steps said: before or after the run, its sentence, and whether it left the cache as it was. */
export interface RunCacheStep {
  step: "before" | "after";
  scope: string;
  summary: string;
  skipped: boolean;
}

function cacheStepsOf(value: unknown): RunCacheStep[] {
  if (!isRecord(value)) {
    return [];
  }

  const steps: RunCacheStep[] = [];
  for (const [field, step] of [["cacheBefore", "before"], ["cacheAfter", "after"]] as const) {
    const said = value[field];
    if (isRecord(said) && typeof said.summary === "string") {
      steps.push({ step, scope: typeof said.scope === "string" ? said.scope : "", summary: said.summary, skipped: typeof said.skipped === "string" });
    }
  }

  return steps;
}

/**
 * What a pipeline's cache steps did (osdu/docs/reference/flow/delivery.md, cache): the step before the run renders and the
 * step after it changed OSDU, the run's own or, for a run of a source of several interfaces, each interface's. Empty when
 * the pipeline names no cache step.
 */
export function runCacheSteps(result: Record<string, unknown> | null): RunCacheStep[] {
  const own = cacheStepsOf(result);
  if (own.length > 0) {
    return own;
  }

  const interfaces = isRecord(result) && Array.isArray(result.interfaces) ? result.interfaces : [];
  return interfaces.flatMap((entry) => cacheStepsOf(isRecord(entry) ? entry.result : null));
}

/** The submission a run worked on: the one its trigger named, else the one its result reports. */
export function runSubmissionId(run: RunDetail, result: Record<string, unknown> | null): string | null {
  const requested = run.payload?.submissionId;
  if (typeof requested === "string" && UUID.test(requested)) {
    return requested;
  }

  const reported = result?.submissionId;
  return typeof reported === "string" && UUID.test(reported) ? reported : null;
}

/** What a run's trigger asked of the kind, read from its payload; anything the payload does not carry reads as absent. */
export interface RunRequest {
  force: boolean;
  submissionId: string | null;
  recordKeys: string[];
  /** How many of the first records of its scope the run asked for (maxRecords), 0 for the whole scope; null when it asked nothing. */
  maxRecords: number | null;
  redeliver: string | null;
  /** Whether the run brings records up to date: renders them again and sends only what renders differently. */
  rerender: boolean;
  /** The key slices of the submission a fan-out intake member plans. */
  slices: number[];
  /** The tests an assertion run was asked to run, by name and by tag; both empty when it ran every test. */
  tests: string[];
  tags: string[];
  /** The run a reverse run reverses (a reverse run of a submission names it in `submissionId`). */
  reversesRun: string | null;
  /** The one interface of a source the run works on, when it names one. */
  interfaceName: string | null;
  /** How much of each record a remove run takes (record, previous, history, everything); null for every other run. */
  removalScope: string | null;
  /** Whether a remove run deletes from the ledger too what OSDU answered for. */
  purgeLedger: boolean;
  /** The listing a remove run empties, its parts as the payload names them; null for a removal by key and every other run. */
  filter: Record<string, unknown> | null;
}

function names(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((name): name is string => typeof name === "string") : [];
}

export function runRequest(run: RunDetail): RunRequest {
  const payload = run.payload ?? {};
  return {
    force: payload.force === true,
    submissionId: typeof payload.submissionId === "string" ? payload.submissionId : null,
    recordKeys: Array.isArray(payload.recordKeys)
      ? payload.recordKeys.filter((key): key is string => typeof key === "string")
      : [],
    maxRecords: firstRecordsOf(payload),
    redeliver: typeof payload.redeliver === "string" ? payload.redeliver : null,
    rerender: payload.rerender === true,
    slices: Array.isArray(payload.slices)
      ? payload.slices.filter((slice): slice is number => typeof slice === "number" && Number.isInteger(slice))
      : [],
    tests: names(payload.tests),
    tags: names(payload.tags),
    reversesRun: typeof payload.runId === "string" && UUID.test(payload.runId) ? payload.runId : null,
    interfaceName: typeof payload.interface === "string" ? payload.interface : null,
    removalScope: typeof payload.scope === "string" ? payload.scope : null,
    purgeLedger: payload.purgeLedger === true,
    filter: isRecord(payload.filter) ? payload.filter : null,
  };
}

/** The ledger a run worked in, as a page scopes the flow's requests: the interface its payload names and its partition value. */
export function runScope(run: RunDetail): { interfaceName: string | null; partition: string | null } {
  const request = runRequest(run);
  const partition = run.values?.partition;
  return { interfaceName: request.interfaceName, partition: typeof partition === "string" ? partition : null };
}

/** The operations of a delivery flow whose runs send records to OSDU, which a reversal can undo. */
export const DELIVERING_OPERATIONS: readonly string[] = ["deliver", "replan", "drain", "intake"];

/** One record a plan would send, as its outcome names it. */
export interface PlanSample {
  key: string;
  label: string | null;
  sourceKey: string;
  action: string;
  reason: string;
  metadata: boolean;
  payload: boolean;
}

/**
 * What a plan run said a delivery would do: how many it read, would send (the record, its payload), leave, hold, and the
 * first it would send; and of those it would send, how many fail an assertion of the mapping and how many of them the
 * check before sending would hold.
 */
export interface PlanPreview {
  records: number;
  deliveries: number;
  metadata: number;
  payload: number;
  unchanged: number;
  held: number;
  blocked: number;
  other: number;
  /** Of the deliveries, those whose document fails an assertion of the mapping (osdu/docs/reference/flow/mapping-assertions.md). */
  failingAssertions: number;
  /** Of those, the ones the check before sending would hold, their documents kept. */
  heldByAssertions: number;
  sample: PlanSample[];
}

/** The outcome of a plan run, read from its result; null before it finished, or for a run that is not a plan. */
export function planPreview(run: RunDetail): PlanPreview | null {
  const result = runResult(run);
  if (result === null || result.operation !== "plan") {
    return null;
  }

  const number = (field: string) => count(result, field) ?? 0;
  const sample = Array.isArray(result.sample) ? result.sample.filter(isRecord).map((entry): PlanSample => ({
    key: typeof entry.key === "string" ? entry.key : "",
    label: typeof entry.label === "string" ? entry.label : null,
    sourceKey: typeof entry.sourceKey === "string" ? entry.sourceKey : "",
    action: typeof entry.action === "string" ? entry.action : "",
    reason: typeof entry.reason === "string" ? entry.reason : "",
    metadata: entry.metadata === true,
    payload: entry.payload === true,
  })).filter((entry) => entry.key !== "") : [];
  return {
    records: number("records"),
    deliveries: number("deliveries"),
    metadata: number("metadata"),
    payload: number("payload"),
    unchanged: number("skips"),
    held: number("holds"),
    blocked: number("blocked"),
    other: number("awaitingApproval") + number("stale") + number("untracked"),
    failingAssertions: number("failingAssertions"),
    heldByAssertions: number("heldByAssertions"),
    sample,
  };
}
