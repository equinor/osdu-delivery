import { useEffect, useRef, useState } from "react";
import { followLiveStream } from "../../api/client";
import { runApi } from "../../api/endpoints";
import { refusalText } from "../../api/followStream";
import type { RunTraceEntry } from "../../api/types";

/** Orders live entries exactly like the paged endpoint: timestamp, then per-stream ordinal, kind, id. ISO-8601
 * UTC strings compare correctly as strings, and entries without a timestamp (legacy statements) sort first. */
function compareEntries(a: RunTraceEntry, b: RunTraceEntry): number {
  const ta = a.timestampUtc ?? "";
  const tb = b.timestampUtc ?? "";
  if (ta !== tb) {
    return ta < tb ? -1 : 1;
  }

  if (a.ordinal !== b.ordinal) {
    return a.ordinal - b.ordinal;
  }

  if (a.kind !== b.kind) {
    return a.kind < b.kind ? -1 : 1;
  }

  return a.id - b.id;
}

export interface LiveRunTrace {
  /** Every entry received so far, in timeline order. Resets when a new stream opens. */
  entries: RunTraceEntry[];
  /** True while the SSE connection is open; false during a reconnect backoff. */
  connected: boolean;
  /** Why the stream stopped for good (a refusal asking again cannot change, such as a 404); null while it follows. */
  refused: string | null;
}

/**
 * Subscribes to a run's live trace stream while `live` is true: entries accumulate in timeline order as the
 * executing node persists them, and a dropped connection reconnects (followLiveStream: after a wait that grows while
 * it keeps failing, after the page's pause on a 429, never after a refusal) resuming from per-stream id cursors (no
 * replay, no gaps). The server's tail is an append-only log keyed by a stable id, so every entry arrives exactly once
 * and there is no client-side de-duplication; the entries are only sorted for a stable timeline. When the server sends
 * the terminal `end` frame, `onEnded` fires exactly once so the page can refetch the authoritative at-rest timeline.
 * The subscription closes when `live` turns false or the component unmounts.
 */
export function useRunTraceStream(runId: string, live: boolean, onEnded: () => void): LiveRunTrace {
  const [entries, setEntries] = useState<RunTraceEntry[]>([]);
  const [connected, setConnected] = useState(false);
  const [refused, setRefused] = useState<string | null>(null);
  // The latest callback without re-subscribing the stream when the parent re-renders.
  const onEndedRef = useRef(onEnded);
  onEndedRef.current = onEnded;

  useEffect(() => {
    if (!live) {
      return undefined;
    }

    const controller = new AbortController();
    const cursors = { afterEventId: 0, afterStatementId: 0 };
    setEntries([]);
    setRefused(null);

    // A reconnect resumes from the cursors, so nothing replays and nothing is lost.
    const attempt = async (signal: AbortSignal, onOpen: () => void) => {
      let ended = false;
      await runApi.streamTrace(runId, cursors, (frame) => {
        if (frame.event === "entry") {
          const entry = JSON.parse(frame.data) as RunTraceEntry;
          // The tail never re-sends a row and the cursors resume past what we hold, so append unconditionally;
          // the sort only keeps the two interleaved streams in timeline order.
          if (entry.kind === "event") {
            cursors.afterEventId = Math.max(cursors.afterEventId, entry.id);
          } else {
            cursors.afterStatementId = Math.max(cursors.afterStatementId, entry.id);
          }

          setEntries((previous) => [...previous, entry].sort(compareEntries));
        } else if (frame.event === "end") {
          ended = true;
        }
      }, signal, onOpen);
      return ended;
    };

    void followLiveStream(attempt, controller.signal, setConnected).then((outcome) => {
      // A stream that ended or was refused as the page let go of it is no longer the page's to report.
      if (controller.signal.aborted) {
        return;
      }

      if (outcome.kind === "ended") {
        onEndedRef.current();
      } else if (outcome.kind === "refused") {
        setRefused(refusalText(outcome.error));
      }
    });
    return () => {
      controller.abort();
      setConnected(false);
    };
  }, [runId, live]);

  return { entries, connected, refused };
}
