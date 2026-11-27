// When the GUI's polls run. TanStack Query asks nothing on its own (a refetch interval's tick, a retry) while its focus
// manager says the page is not focused, and reads again what went stale once focus returns. So the gate tells it the page
// is not focused while the control plane has paused the GUI for calling too often (rateLimit.ts) or while the surface is
// hidden from the person, and hands focus back to the document's own visibility when neither holds. An editor's tab kept
// alive behind another is such a surface: its document still counts as visible, so its host says when it is shown.
//
// Every poll of every page is held this way, whatever its own interval says, so no page can forget the pause, and none
// has to start its polling again afterwards: a refetch interval's timer keeps running through the hold, and only its ticks
// are skipped. Kept free of the browser, and of TanStack but its types, so the unit suite reads its rules against
// TanStack's own query client (unit/pollingGate.test.ts); the query client (queryClient.ts) wires it to TanStack's focus
// manager.

import type { FocusManager, Query, QueryClient } from "@tanstack/react-query";

/** What the gate sets: TanStack's focus manager, where false holds every poll and undefined follows the document's visibility. */
export interface FocusTarget {
  setFocused(focused: boolean | undefined): void;
}

/** The page's polling gate. */
export interface PollingGate {
  /** The control plane has paused the GUI for calling too often (true), or that pause has ended (false). */
  setPaused(paused: boolean): void;
  /** Whether the surface is in front of the person, as its host says; a surface no host speaks for is never told. */
  setShown(shown: boolean): void;
  /** Whether the gate holds every poll now. */
  holding(): boolean;
}

/** A gate over `target`, open until it is told of a pause or a hidden surface. */
export function createPollingGate(target: FocusTarget): PollingGate {
  let paused = false;
  let hidden = false;
  const apply = () => target.setFocused(paused || hidden ? false : undefined);
  return {
    setPaused: (next) => {
      if (paused !== next) {
        paused = next;
        apply();
      }
    },
    setShown: (shown) => {
      if (hidden === shown) {
        hidden = !shown;
        apply();
      }
    },
    holding: () => paused || hidden,
  };
}

/**
 * Once the page has focus again, asks again every query on it whose last answer `refused` names (a 429), however fresh its
 * data: TanStack's own refetch on focus reads only what went stale, and a refusal must not stay on screen. A query
 * already being asked is not asked twice. Answers the way to stop.
 */
export function askRefusedAgainOnFocus(
  focus: Pick<FocusManager, "subscribe">,
  client: QueryClient,
  refused: (error: unknown) => boolean,
): () => void {
  return focus.subscribe((focused) => {
    if (focused) {
      void client.refetchQueries({ type: "active", predicate: (query: Query) => refused(query.state.error) }, { cancelRefetch: false });
    }
  });
}
