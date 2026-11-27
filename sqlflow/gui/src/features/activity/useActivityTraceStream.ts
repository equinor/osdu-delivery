import { useEffect, useRef, useState } from "react";
import { followLiveStream } from "../../api/client";
import { activityApi } from "../../api/endpoints";
import { refusalText } from "../../api/followStream";
import type { ActivityEvent } from "../../api/types";

export interface LiveActivityTrace {
  /** Every line received so far, in id (timeline) order. */
  entries: ActivityEvent[];
  /** True while the SSE connection is open; false during a reconnect backoff. */
  connected: boolean;
  /** True once the server sent the terminal `end` frame for the current activity. */
  ended: boolean;
  /** Why the stream stopped for good (a refusal asking again cannot change, such as a 403); null while it follows. */
  refused: string | null;
}

/**
 * Subscribes to one operation's live activity trace for a (kind, subject) pair: lines accumulate in id order as the
 * operation persists them, and a dropped connection reconnects (followLiveStream: after a wait that grows while it
 * keeps failing, after the page's pause on a 429, never after a refusal) resuming from the last id (no replay, no
 * gaps). The append-only log is keyed by a stable, monotonic id, so every line arrives exactly once and
 * there is no client-side de-duplication. When the server sends the terminal `end` frame `onEnded` fires once (so a
 * page can refresh at-rest state) and the stream stops reconnecting. The subscription closes when the component
 * unmounts; a repeat trigger (a new "Sync now") mounts the view again, as the trace tray starts a held trace over, so a
 * completed trace restarts from the top.
 *
 * This is the general-purpose twin of useRunTraceStream: the same tail-an-append-only-log mechanism, keyed by a
 * kind + subject instead of a run id, so any traced operation reuses it.
 */
export function useActivityTraceStream(kind: string, subject: string, onEnded?: () => void): LiveActivityTrace {
  const [entries, setEntries] = useState<ActivityEvent[]>([]);
  const [connected, setConnected] = useState(false);
  const [ended, setEnded] = useState(false);
  const [refused, setRefused] = useState<string | null>(null);
  // The latest callback without re-subscribing the stream when the parent re-renders.
  const onEndedRef = useRef(onEnded);
  onEndedRef.current = onEnded;

  useEffect(() => {
    const controller = new AbortController();
    const cursor = { afterId: 0 };
    setEntries([]);
    setEnded(false);
    setRefused(null);

    // A reconnect resumes from the cursor, so nothing replays and nothing is lost.
    const attempt = async (signal: AbortSignal, onOpen: () => void) => {
      let endedNow = false;
      await activityApi.streamTrace(kind, subject, cursor, (frame) => {
        if (frame.event === "entry") {
          const entry = JSON.parse(frame.data) as ActivityEvent;
          // The tail never re-sends a row and the cursor resumes past what we hold, so append unconditionally.
          cursor.afterId = Math.max(cursor.afterId, entry.id);
          setEntries((previous) => [...previous, entry]);
        } else if (frame.event === "end") {
          endedNow = true;
        }
      }, signal, onOpen);
      return endedNow;
    };

    void followLiveStream(attempt, controller.signal, setConnected).then((outcome) => {
      // A stream that ended or was refused as the page let go of it is no longer the page's to report.
      if (controller.signal.aborted) {
        return;
      }

      if (outcome.kind === "ended") {
        setEnded(true);
        onEndedRef.current?.();
      } else if (outcome.kind === "refused") {
        setRefused(refusalText(outcome.error));
      }
    });
    return () => {
      controller.abort();
      setConnected(false);
    };
  }, [kind, subject]);

  return { entries, connected, ended, refused };
}
