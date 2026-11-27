// How the GUI pauses while the control plane refuses it for calling too often (an HTTP 429), kept free of the browser and
// of the API client so the unit suite reads its rules (unit/rateLimit.test.ts). The client (client.ts) keeps one pause and
// starts it from every 429 it is answered; the polling gate (pollingGate.ts) holds every poll while it lasts.

/** How long a 429 that does not say when to come back pauses the GUI. */
export const UNSTATED_PAUSE_SECONDS = 30;

/**
 * The longest one 429 pauses the GUI, whatever its Retry-After says: a pause that ran on would read as a page that froze.
 * Once it ends, the next call finds out again.
 */
export const LONGEST_PAUSE_SECONDS = 300;

/** An HTTP date as a Retry-After carries one (RFC 9110's IMF-fixdate), such as `Fri, 27 Nov 2026 10:00:30 GMT`. */
const HTTP_DATE = /^[A-Z][a-z]{2}, \d{2} [A-Z][a-z]{2} \d{4} \d{2}:\d{2}:\d{2} GMT$/;

/**
 * The seconds a 429's `Retry-After` says to wait (RFC 9110: whole seconds, or an HTTP date), kept between one and
 * LONGEST_PAUSE_SECONDS; UNSTATED_PAUSE_SECONDS when the header is absent or unreadable.
 */
export function pauseSeconds(retryAfter: string | null, nowMs: number): number {
  const value = retryAfter?.trim() ?? "";
  let seconds: number | null = null;
  if (/^\d+$/.test(value)) {
    seconds = Number.parseInt(value, 10);
  } else if (HTTP_DATE.test(value)) {
    const at = Date.parse(value);
    if (!Number.isNaN(at)) {
      seconds = Math.ceil((at - nowMs) / 1000);
    }
  }

  return seconds === null ? UNSTATED_PAUSE_SECONDS : Math.min(LONGEST_PAUSE_SECONDS, Math.max(1, seconds));
}

/** Hears a pause start or move (when it now ends, in epoch milliseconds) and end (null). */
export type RateLimitListener = (pausedUntilMs: number | null) => void;

/** The clock and the timer a pause runs on: the browser's, or a test's. */
export interface PauseClock {
  now(): number;
  setTimeout(callback: () => void, ms: number): unknown;
  clearTimeout(handle: unknown): void;
}

/** The GUI's one pause for calling too often. */
export interface RateLimitPause {
  /** When the pause ends, in epoch milliseconds; null while none holds. */
  pausedUntil(): number | null;
  /** Hears the pause start, move and end; answers the way to stop hearing it. */
  subscribe(listener: RateLimitListener): () => void;
  /** Pauses for what a 429's `Retry-After` says; a pause that already ends later is kept as it is. */
  pause(retryAfter: string | null): void;
}

/**
 * A pause on `clock`. It ends on its own timer, which always tells every listener, so whoever waits for the end hears it
 * however often the pause was read meanwhile; a later 429 that says to wait longer moves the end, and one that says less
 * leaves it.
 */
export function createRateLimitPause(clock: PauseClock): RateLimitPause {
  let until: number | null = null;
  let timer: unknown = null;
  const listeners = new Set<RateLimitListener>();
  const tell = (value: number | null) => {
    for (const listener of [...listeners]) {
      listener(value);
    }
  };

  return {
    pausedUntil: () => (until !== null && clock.now() < until ? until : null),
    subscribe: (listener) => {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    pause: (retryAfter) => {
      const now = clock.now();
      const next = now + pauseSeconds(retryAfter, now) * 1000;
      if (until !== null && next <= until) {
        return;
      }

      until = next;
      if (timer !== null) {
        clock.clearTimeout(timer);
      }

      timer = clock.setTimeout(() => {
        timer = null;
        until = null;
        tell(null);
      }, next - now);
      tell(next);
    },
  };
}
