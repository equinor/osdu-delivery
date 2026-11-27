import { useEffect, useRef, useState } from "react";
import { followLiveStream } from "../../api/client";
import { runApi } from "../../api/endpoints";
import { refusalText } from "../../api/followStream";
import type { RunSummary } from "../../api/types";

/** The members in display order: the lineage step (the Step column), then flow name, then run id. */
function compareMembers(a: RunSummary, b: RunSummary): number {
  if (a.wave !== b.wave) {
    return a.wave - b.wave;
  }

  if (a.flowName !== b.flowName) {
    return a.flowName < b.flowName ? -1 : 1;
  }

  return a.runId < b.runId ? -1 : a.runId > b.runId ? 1 : 0;
}

export interface LiveRunGroup {
  /** Every member's latest summary, in display order; empty until the connect snapshot lands. */
  members: RunSummary[];
  /** True while the SSE connection is open; false during a reconnect backoff. */
  connected: boolean;
  /** Why the stream stopped for good (a refusal asking again cannot change, such as a 404); null while it follows. */
  refused: string | null;
}

/**
 * Subscribes to a run group's live stream while `live` is true: each `member` frame replaces that member's
 * summary (status transitions, the newest trace event as its "last action", rows, timing), the server sends a
 * full snapshot on connect so a reconnect (followLiveStream) self-heals, and the terminal `end` frame fires `onEnded`
 * exactly once so the page can refetch the authoritative at-rest views. The subscription closes when `live` turns false
 * or the component unmounts.
 */
export function useRunGroupStream(groupId: string, live: boolean, onEnded: () => void): LiveRunGroup {
  const [members, setMembers] = useState<RunSummary[]>([]);
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
    const byRunId = new Map<string, RunSummary>();
    setMembers([]);
    setRefused(null);

    // A reconnect's fresh snapshot replaces the map, so nothing is lost and nothing stale survives.
    const attempt = async (signal: AbortSignal, onOpen: () => void) => {
      let ended = false;
      await runApi.streamGroup(groupId, (frame) => {
        if (frame.event === "member") {
          const member = JSON.parse(frame.data) as RunSummary;
          byRunId.set(member.runId, member);
          setMembers([...byRunId.values()].sort(compareMembers));
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
  }, [groupId, live]);

  return { members, connected, refused };
}
