// The rules a record's timeline is told by (osdu/docs/reference/concepts/record-lifecycle.md, A record's timeline): what a
// try's error says OSDU answered, whether a try reached OSDU, what a request reached, and how times, sizes and names read.
// RecordJourney draws the story; these decide it, and the unit tier tests them without rendering anything.

/** What a try says of itself, as the ledger answers it (DeliveryAttempt). */
export interface StoryAttempt {
  worker: string;
  outcome: string;
  phase: string;
  error: string | null;
  result: { steps?: readonly unknown[]; restriction?: { acl?: unknown } } | null;
}

/** What a request on a record says of itself, as the ledger answers it (DeliveryActivity). */
export interface StoryActivity {
  kind: string;
  deliveryKey: string | null;
  parametersJson: string | null;
  summary: string | null;
}

/** A change of a record's source row, by its kind (DeliverySourceChange). */
export interface StoryChange {
  kind: string;
}

/** How long a run took, for a run's line. */
export function runDuration(seconds: number | null): string | null {
  if (seconds === null || !Number.isFinite(seconds)) {
    return null;
  }

  return seconds < 1 ? `${Math.round(seconds * 1000)} ms` : seconds < 90 ? `${seconds.toFixed(1)} s` : `${(seconds / 60).toFixed(1)} min`;
}

export function fileSize(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  if (bytes < 1024 * 1024) {
    return `${(bytes / 1024).toFixed(1)} KB`;
  }

  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/** The ingestion table as an operator names it: its schema and table, without brackets or the database. */
export function ingestionTableName(sourceTable: string | null): string {
  if (sourceTable === null) {
    return "the ingestion table";
  }

  const parts = sourceTable.split(".").map((part) => part.trim().replace(/^\[/, "").replace(/\]$/, "")).filter((part) => part !== "");
  return parts.length === 0 ? sourceTable : parts.slice(-2).join(".");
}

/** Whether a change is the row's arrival as far as the ledger knows it: its insert, a reinsert, or its earliest version. */
export function isArrival(change: StoryChange): boolean {
  return change.kind === "loaded" || change.kind === "reloaded" || change.kind === "earliest";
}

/** Who asked, as the ledger names them without the prefix of a signed-in user. */
export function actorName(actor: string): string {
  return actor.startsWith("user:") ? actor.slice("user:".length) : actor;
}

/** The letter a person's node carries: the first of their name, past a schedule's or a workstation's prefix. */
export function actorInitial(actor: string): string {
  const name = actorName(actor).replace(/^(cli|schedule):/, "");
  return name === "" || name === "unknown" ? "?" : name.charAt(0).toUpperCase();
}

/** What OSDU answered a refused request with, read from the error the worker stored. */
export interface Refusal {
  status: number;
  reason: string;
  method: string | null;
  url: string | null;
  answer: string | null;
}

export function refusalOf(error: string | null): Refusal | null {
  if (error === null) {
    return null;
  }

  const head = /HTTP (\d{3}) ([A-Za-z]+) from (GET|POST|PUT|PATCH|DELETE|HEAD) (\S+)/.exec(error);
  if (head === null) {
    const bare = /^HTTP (\d{3})\b/.exec(error);
    return bare === null ? null : { status: Number(bare[1]), reason: "", method: null, url: null, answer: null };
  }

  const answer = /\):\s*(.+)$/s.exec(error)?.[1]?.trim();
  return {
    status: Number(head[1]),
    reason: head[2].replace(/([a-z])([A-Z])/g, "$1 $2"),
    method: head[3],
    url: head[4],
    answer: answer === undefined || answer === "" ? null : answer,
  };
}

export function urlPath(url: string): string {
  try {
    return new URL(url).pathname;
  } catch {
    return url;
  }
}

/**
 * Whether a try reached OSDU: a delivery, a removal, a purge, a try that failed, one that took a step, or one OSDU
 * refused. What the intake decided (a hold of a row it could not build, a change that rendered what was delivered, an
 * older version) and the final hash check that found nothing to send are the ledger's decisions. A refused request
 * completes no step, so a refusal is known by the answer the worker stored with its hold.
 */
export function reachedTarget(attempt: StoryAttempt): boolean {
  return attempt.outcome === "delivered"
    || attempt.outcome === "failed"
    || attempt.outcome === "deleted"
    || attempt.outcome === "historypurged"
    || attempt.outcome === "restored"
    || attempt.outcome === "undone"
    || attempt.outcome === "restricted"
    // A lift asks OSDU only when it holds the record: the ACL it wrote back is named, or none when nothing was asked.
    || (attempt.outcome === "unrestricted" && attempt.result?.restriction?.acl != null)
    || (attempt.result?.steps?.length ?? 0) > 0
    || (attempt.outcome === "held" && attempt.worker !== "intake" && attempt.phase !== "undo-wait" && refusalOf(attempt.error) !== null);
}

export function statusText(refusal: Refusal): string {
  return `HTTP ${refusal.status}${refusal.reason !== "" ? ` ${refusal.reason}` : ""}`;
}

/** How a try set aside for an unreadable queued document said so before the ledger gave that its own phase. */
export const SET_ASIDE_BEFORE = "the rendered document queued for the record could not be read";

/**
 * Whether a held try was the worker setting the record aside because the document queued for it was not there to read
 * (phase `document-missing`; before it had a phase, its error said so): the record dropped what it had queued and asks to
 * be planned again, unblocked.
 */
export function setAside(attempt: StoryAttempt): boolean {
  return attempt.outcome === "held"
    && (attempt.phase === "document-missing" || (attempt.phase === "none" && attempt.error?.startsWith(SET_ASIDE_BEFORE) === true));
}

/** The activity's parameters, as the ledger stored them; null when they are absent or not an object. */
export function parametersOf(activity: StoryActivity): Record<string, unknown> | null {
  if (activity.parametersJson === null) {
    return null;
  }

  try {
    const parsed: unknown = JSON.parse(activity.parametersJson);
    return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed as Record<string, unknown> : null;
  } catch {
    return null;
  }
}

/** How many records a request named: the keys it listed, or this record alone when the ledger tied it to one. */
export function scopeOf(activity: StoryActivity, parameters: Record<string, unknown> | null): number | null {
  const keys = parameters?.keys;
  if (Array.isArray(keys) && keys.length > 0) {
    return keys.length;
  }

  // An intervention on many records names how many rather than each one; the ledger names every record it reached.
  const count = parameters?.count;
  if (typeof count === "number" && Number.isFinite(count) && count > 0) {
    return count;
  }

  return activity.deliveryKey !== null ? 1 : null;
}

/**
 * A release that named no record and reached this one among others: a release of every blocked record, or of every record
 * one issue kept blocked. The ledger names each record such a release changed, which is how it is on this record's
 * timeline at all; its summary says how many it released once it is done, and an issue's release names the issue's
 * pattern.
 */
export interface Reach {
  title: string;
  count: number | null;
  pattern: string | null;
}

export function reachOf(activity: StoryActivity, parameters: Record<string, unknown> | null): Reach | null {
  const keys = parameters?.keys;
  if ((activity.kind !== "release" && activity.kind !== "adopt" && activity.kind !== "redeliver" && activity.kind !== "rerender")
    || activity.deliveryKey !== null || (Array.isArray(keys) && keys.length > 0) || typeof parameters?.count === "number") {
    return null;
  }

  // "released 1,204 record(s) ...", "asked 1204 record(s) ... to adopt ...", or "marked every delivered record, 1204 in all, for
  // redelivery of ...". An adoption made before adoption was asked of a run said "adopted 1204 record(s) ...".
  const reached = /^(?:released|adopted|asked) (\d+) record|, (\d+) in all,/.exec(activity.summary ?? "");
  const said = reached?.[1] ?? reached?.[2];
  const count = said === undefined ? null : Number(said);
  if (activity.kind === "rerender") {
    return {
      title: count === null
        ? "Bringing up to date asked for every delivered record, this one among them"
        : `Bringing up to date asked for every delivered record, ${count.toLocaleString()} in all, this one among them`,
      count,
      pattern: null,
    };
  }

  if (activity.kind === "redeliver") {
    return {
      title: count === null
        ? "Redelivery asked for every delivered record, this one among them"
        : `Redelivery asked for every delivered record, ${count.toLocaleString()} in all, this one among them`,
      count,
      pattern: null,
    };
  }

  const records = count === null ? "the records" : `${count.toLocaleString()} record${count === 1 ? "" : "s"}`;
  // An adoption that names no record is asked of every record held because OSDU holds a record at the id its key makes,
  // or of the records one issue kept held for that.
  if (activity.kind === "adopt") {
    if (parameters?.every !== undefined) {
      return {
        title: count === null
          ? "Adoption asked for every record held for an id OSDU holds, this one among them"
          : `Adoption asked for every record held for an id OSDU holds, ${records} in all, this one among them`,
        count,
        pattern: null,
      };
    }

    return {
      title: `Adoption asked for ${records} one issue kept held, this one among them`,
      count,
      pattern: typeof parameters?.pattern === "string" ? parameters.pattern : null,
    };
  }

  // A release of one issue's records names the issue (under "problem" in what the first releases wrote).
  if (typeof (parameters?.issue ?? parameters?.problem) === "string") {
    return {
      title: `Release asked for ${records} one issue kept blocked, this one among them`,
      count,
      pattern: typeof parameters?.pattern === "string" ? parameters.pattern : null,
    };
  }

  return {
    title: count === null
      ? "Release asked for every blocked record, this one among them"
      : `Release asked for every blocked record, ${records} in all, this one among them`,
    count,
    pattern: null,
  };
}

/** What a redelivery re-sends, from the scope and parts it was asked with. */
export function redeliveryOf(parameters: Record<string, unknown> | null): string | null {
  const parts = parameters?.parts;
  if (Array.isArray(parts) && parts.length > 0) {
    return `the ${parts.map(String).join(" and ")}`;
  }

  switch (parameters?.scope) {
    case "All":
      return "everything: record and payload";
    case "Metadata":
      return "the record";
    case "Payload":
      return "the payload";
    default:
      return null;
  }
}
