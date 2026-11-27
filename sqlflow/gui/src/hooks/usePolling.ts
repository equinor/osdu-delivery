import { useSyncExternalStore } from "react";
import { rateLimitPausedUntil, subscribeRateLimit } from "../api/client";

// A page polls by giving its query a refetchInterval, and nothing more: the page's polling gate (api/pollingGate.ts) holds
// every poll while the control plane has the GUI paused for calling too often, while the browser tab is hidden, or while an
// editor's tab is behind another, and reads again what went stale once it opens.

/** Reactive view of the rate-limit pause, for the "rate limited, resuming shortly" banner: when it ends, or null. */
export function useRateLimitPause(): number | null {
  return useSyncExternalStore(
    (onStoreChange) => subscribeRateLimit(() => onStoreChange()),
    () => rateLimitPausedUntil(),
  );
}
