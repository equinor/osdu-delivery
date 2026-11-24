// A record's story, as its timeline (RecordJourney) draws it: every entry in the order it happened, cut into chapters
// where something set the record moving, each settled by how it ended, and where the record stands now.

import { parseUtc } from "@/lib/time";
import type { DeliveryActivity, DeliveryAttempt, DeliveryRecord, DeliveryRecordChain } from "../../api/delivery";
import { ingestionTableName, setAside } from "./recordStory";
import { attemptEntry, foldRepeats } from "./recordJourneyAttempts";
import type { Chapter, Entry, Outcome } from "./recordJourneyEntry";
import { activityEntry, verifiedEntry } from "./recordJourneyRequests";
import { pickedUpEntry, sourceEntries } from "./recordJourneySource";

/** Whether a verify was the one whose outcome the record holds: the record was verified while the verify ran. */
function covers(activity: DeliveryActivity, at: string): boolean {
  const moment = parseUtc(at).getTime();
  const from = parseUtc(activity.startedUtc).getTime() - 2000;
  const to = activity.completedUtc === null ? Number.POSITIVE_INFINITY : parseUtc(activity.completedUtc).getTime() + 2000;
  return moment >= from && moment <= to;
}

/**
 * Where the record stands, in the words of a chapter's outcome, and whether work on it is in flight (a document queued
 * or being sent, a wait on a record it refers to): what the latest chapter says when nothing in it has settled yet, or
 * when something since has set the record going again. `newest` is its newest attempt: an adoption made before adoption
 * was asked of a run means a record asked to be planned with no document kept claimed the record OSDU already held and
 * waits for its first write there; a try that set the record aside means it waits to be planned again, released by no one.
 */
function standingOf(record: DeliveryRecord, newest: DeliveryAttempt | undefined): { outcome: Outcome; inFlight: boolean } {
  const adopted = newest?.phase === "adopt" && newest.worker !== "intake";
  switch (record.status) {
    case "delivered":
      return { outcome: { text: "Delivered", tone: "success" }, inFlight: false };
    case "pending":
      return record.hasPendingDocument
        ? { outcome: { text: "Queued to send", tone: "info" }, inFlight: true }
        : { outcome: { text: record.planRequestedUtc !== null ? "Waiting for the next run" : "Pending", tone: "info" }, inFlight: true };
    case "delivering":
      return { outcome: { text: "Being delivered", tone: "info" }, inFlight: true };
    case "waiting":
      return { outcome: { text: "Waiting for a record it refers to", tone: "info" }, inFlight: true };
    case "held":
    case "failed":
      // Released, asked to adopt the record OSDU holds at the id its key makes, or asked to be planned again, with no document
      // kept: the next run plans it from its row. A request to adopt that a run held the record over since waits for a release.
      if (!record.blocked && !record.hasPendingDocument && record.planRequestedUtc !== null) {
        const waiting = record.adoptRequestedUtc != null ? "Adoption asked"
          : adopted ? "Adopted"
          : newest !== undefined && setAside(newest) ? "Set aside"
          : "Released";
        return { outcome: { text: `${waiting}: waiting for the next run`, tone: "info" }, inFlight: true };
      }

      return record.status === "held"
        ? { outcome: { text: "Held", tone: "warning" }, inFlight: false }
        : { outcome: { text: "Failed", tone: "destructive" }, inFlight: false };
    case "deleted":
      return { outcome: { text: "Removed", tone: "muted" }, inFlight: false };
    case "reverted":
      return { outcome: { text: "Reverted", tone: "muted" }, inFlight: false };
    default:
      return { outcome: { text: record.status, tone: "muted" }, inFlight: false };
  }
}

/**
 * How a chapter ended: its last settled step, a verified delivery as both. The latest chapter says where the record
 * stands instead while work on it is in flight, or when nothing in it has settled yet.
 */
function settle(entries: Entry[], latest: boolean, standing: { outcome: Outcome; inFlight: boolean }): Outcome {
  const settled = entries.reduce<Outcome | null>((outcome, entry) => {
    if (entry.outcome === null) {
      return outcome;
    }

    return entry.verifies && entry.outcome.tone === "success" && outcome !== null && outcome.text === "Delivered"
      ? { text: "Delivered and verified", tone: "success" }
      : entry.outcome;
  }, null);

  if (latest && (standing.inFlight || settled === null)) {
    return standing.outcome;
  }

  return settled ?? { text: "Nothing sent", tone: "muted" };
}

interface Story {
  chapters: Chapter[];
  syncs: DeliveryActivity[];
  table: string;
}

/**
 * The record's story, in chapters. Each step is placed on the lane where it happened; the steps are read in the order
 * they happened and cut into chapters where something set the record moving (its row arrived, changed or was deleted,
 * or someone asked for a redelivery, a release or a removal), so a chapter is what started it and what came of it.
 * Steps that set the record moving one after another, before anything came of them (a file landing and the load of its
 * row, a request asked twice), open one chapter together. A sync of the timeline itself is kept apart: it is how the
 * ledger learned the row's history, not a step of it.
 */
export function buildStory(
  record: DeliveryRecord, attempts: DeliveryAttempt[], activities: DeliveryActivity[], chain: DeliveryRecordChain | undefined,
): Story {
  const lastVerified = record.lastVerifiedUtc;
  const verifyWithResult = lastVerified === null
    ? undefined
    : activities.filter((a) => a.kind === "verify" && covers(a, lastVerified)).at(-1);

  const entries: Entry[] = [...sourceEntries(chain), pickedUpEntry(record)];
  for (const activity of activities) {
    if (activity.kind !== "sync") {
      entries.push(activityEntry(activity, record, activity === verifyWithResult));
    }
  }

  if (lastVerified !== null && verifyWithResult === undefined) {
    entries.push(verifiedEntry(record, lastVerified));
  }

  const attemptEntries = attempts.map(attemptEntry);
  entries.push(...attemptEntries);

  // In the order it happened. The API writes some moments without their zone; every moment is UTC by contract.
  entries.sort((a, b) => {
    const byTime = parseUtc(a.at).getTime() - parseUtc(b.at).getTime();
    return byTime !== 0 && Number.isFinite(byTime) ? byTime : a.order - b.order;
  });

  const chapters: Chapter[] = [];
  // An adoption made at an operator's click, or a try that set the record aside, as its newest attempt is what it waits on.
  const newest = attempts.reduce<DeliveryAttempt | undefined>(
    (latest, attempt) => latest === undefined || parseUtc(attempt.startedUtc).getTime() > parseUtc(latest.startedUtc).getTime() ? attempt : latest,
    undefined);
  const standing = standingOf(record, newest);
  for (const entry of foldRepeats(entries)) {
    const current = chapters.at(-1);
    const onlyTriggers = current !== undefined && current.entries.every((e) => e.trigger !== null);
    if (current === undefined || (entry.trigger !== null && !onlyTriggers)) {
      chapters.push({ number: chapters.length + 1, trigger: entry.trigger ?? entry.title, entries: [entry], outcome: standing.outcome });
    } else {
      current.entries.push(entry);
    }
  }

  chapters.forEach((chapter, index) => {
    chapter.outcome = settle(chapter.entries, index === chapters.length - 1, standing);
  });

  const syncs = activities
    .filter((a) => a.kind === "sync")
    .sort((a, b) => parseUtc(b.startedUtc).getTime() - parseUtc(a.startedUtc).getTime());
  return { chapters, syncs, table: ingestionTableName(chain?.sourceTable ?? null) };
}
