// How a live stream (a run's trace, a run group, an activity's trace) is followed through its connection closing or
// failing: connected again after a wait that grows while connecting keeps failing, after the page's pause when the control
// plane refused it for calling too often, and never again after a refusal that asking again cannot change (a 403, a 404).
// Kept free of the browser and the API client, which hands it what it needs (client.ts, followLiveStream), so the unit
// suite reads its rules (unit/followStream.test.ts).

/** The first wait before connecting again, and the most it grows to while connecting keeps failing. */
export const FIRST_RECONNECT_MS = 2_000;
export const LONGEST_RECONNECT_MS = 30_000;

/** What to do once a connection has closed or failed. */
export type Reconnect =
  | { kind: "again"; waitMs: number }
  | { kind: "afterPause" }
  | { kind: "stop" };

/**
 * What follows a connection that closed or failed. `status` is the HTTP status it was refused with, or null when no status
 * answered (the network failed, or a stream that opened was closed); `failures` is how many attempts in a row have failed
 * without opening, this one included, 0 after one that opened. A 429 waits for the page's pause; any other 4xx but 408 is
 * a refusal asking again cannot change; anything else is asked again after 2 s, doubling while it keeps failing, to 30 s.
 */
export function reconnectAfter(status: number | null, failures: number): Reconnect {
  if (status === 429) {
    return { kind: "afterPause" };
  }

  if (status !== null && status >= 400 && status < 500 && status !== 408) {
    return { kind: "stop" };
  }

  return { kind: "again", waitMs: Math.min(LONGEST_RECONNECT_MS, FIRST_RECONNECT_MS * 2 ** Math.max(0, failures - 1)) };
}

/** What following a stream needs from where it runs. */
export interface StreamFollowing {
  /** Opens the stream once and reads it until it closes: true when the server sent its terminal end frame. */
  attempt(signal: AbortSignal, onOpen: () => void): Promise<boolean>;
  /** The HTTP status a failed attempt was refused with; null when none answered. */
  statusOf(error: unknown): number | null;
  /** Resolves once the page's pause for calling too often has ended (at once when none holds), or the signal aborts. */
  pauseEnded(signal: AbortSignal): Promise<void>;
  /** Waits `ms`, or until the signal aborts. */
  sleep(ms: number, signal: AbortSignal): Promise<void>;
  /** The stream opened (true), or is no longer open (false). */
  connected(open: boolean): void;
}

/** What a refusal that stopped a stream says, for the page to show where it would have said "live". */
export function refusalText(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/** How following a stream ended: the server's end frame, the caller's abort, or a refusal asking again cannot change. */
export type StreamOutcome = { kind: "ended" } | { kind: "aborted" } | { kind: "refused"; error: unknown };

/** Follows a stream until it ends, `signal` aborts, or a refusal stops it (reconnectAfter). */
export async function followStream(following: StreamFollowing, signal: AbortSignal): Promise<StreamOutcome> {
  let failures = 0;
  while (!signal.aborted) {
    let opened = false;
    let status: number | null = null;
    let failure: unknown = null;
    try {
      const ended = await following.attempt(signal, () => {
        opened = true;
        following.connected(true);
      });
      if (ended) {
        return { kind: "ended" };
      }
    } catch (error) {
      if (signal.aborted) {
        break;
      }

      status = following.statusOf(error);
      failure = error;
    }

    following.connected(false);
    // A stream that opened and then closed (a control plane restart, a proxy's timeout) starts the waits over.
    failures = opened ? 0 : failures + 1;
    const next = reconnectAfter(status, failures);
    if (next.kind === "stop") {
      return { kind: "refused", error: failure };
    }

    if (next.kind === "afterPause") {
      await following.pauseEnded(signal);
    } else {
      await following.sleep(next.waitMs, signal);
    }
  }

  return { kind: "aborted" };
}
