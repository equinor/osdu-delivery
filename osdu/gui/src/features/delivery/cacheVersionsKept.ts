// How many versions of a partition's cache keep their records, as the Cache page says and sets it
// (osdu/docs/reference/concepts/partition-cache.md, Retention).

/** How many versions a partition's cache keeps, the current one counted, until an admin sets another number. */
export const DEFAULT_VERSIONS_KEPT = 3;

/** The fewest versions a partition's cache keeps: the current one and the one it replaced, which are always kept. */
export const MIN_VERSIONS_KEPT = 2;

/** The most versions a partition's cache keeps, as the module bounds it. */
export const MAX_VERSIONS_KEPT = 1000;

/** The number typed, when it is a whole number of versions a partition may keep; null otherwise. */
export function versionsKeptOf(text: string): number | null {
  const trimmed = text.trim();
  if (!/^\d{1,4}$/.test(trimmed)) {
    return null;
  }

  const versions = Number(trimmed);
  return versions >= MIN_VERSIONS_KEPT && versions <= MAX_VERSIONS_KEPT ? versions : null;
}

/** A number of versions as the page says it. */
export function versionsText(count: number): string {
  return `${count.toLocaleString("en-US")} version${count === 1 ? "" : "s"}`;
}

/** A number of days as the page says it. */
export function daysText(count: number): string {
  return `${count.toLocaleString("en-US")} day${count === 1 ? "" : "s"}`;
}

/**
 * What the partition's cache keeps of its history, in one sentence: the newest versions, a replaced one for its days at
 * most, and what becomes of the rest.
 */
export function historyKeptText(versionsKept: number, retentionDays: number): string {
  return `The newest ${versionsText(versionsKept)} keep their records, a replaced one for up to ${daysText(retentionDays)}; `
    + "older ones are pruned by the next refresh and stay listed with what they changed.";
}
