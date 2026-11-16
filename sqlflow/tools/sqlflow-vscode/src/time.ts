/**
 * Reads an instant the control plane gave. Its timestamps are UTC, and one read back from the catalog may come without a
 * zone, which `Date.parse` would read as this machine's local time: east or west of UTC, "a minute ago" would read as an
 * hour ago or as the future. A timestamp with no zone is read as UTC.
 */
export function parseUtc(utc: string): number {
    const zoned = /(Z|[+-]\d{2}:?\d{2})$/i.test(utc.trim());
    return Date.parse(zoned ? utc : `${utc.trim()}Z`);
}

/** How long ago an instant was, the way a person says it. */
export function ago(utc: string | null | undefined, now: number = Date.now()): string {
    if (!utc) {
        return '';
    }
    const seconds = Math.round((now - parseUtc(utc)) / 1000);
    if (Number.isNaN(seconds)) {
        return '';
    }
    if (seconds < 90) {
        return 'just now';
    }
    const minutes = Math.round(seconds / 60);
    if (minutes < 90) {
        return `${minutes}m ago`;
    }
    const hours = Math.round(minutes / 60);
    return hours < 36 ? `${hours}h ago` : `${Math.round(hours / 24)}d ago`;
}
