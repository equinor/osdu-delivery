import { createContext, useContext, useSyncExternalStore } from "react";

/**
 * The day under the pointer in the data view, shared by every chart on the page: pointing at a day in one card's
 * chart, or in the day rail, lights the same day in all of them, so a day is read down the whole chain at a glance.
 * Held outside React's state on purpose. It changes with every pointer move across a chart, and only the charts need
 * to hear of it: through the page's state each move would redraw the canvas, its cards and its lines.
 */
export interface LensHoverStore {
  get: () => number | null;
  set: (day: number | null) => void;
  subscribe: (listener: () => void) => () => void;
}

export function createLensHoverStore(): LensHoverStore {
  let day: number | null = null;
  const listeners = new Set<() => void>();
  return {
    get: () => day,
    set: (next) => {
      if (next === day) {
        return;
      }

      day = next;
      for (const listener of listeners) {
        listener();
      }
    },
    subscribe: (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };
}

export const LensHoverContext = createContext<LensHoverStore>(createLensHoverStore());

/** The store itself, for a chart that sets the day under the pointer. */
export function useLensHover(): LensHoverStore {
  return useContext(LensHoverContext);
}

/** The day under the pointer anywhere on the page; null when the pointer is on no chart. */
export function useLensHoverDay(): number | null {
  const store = useContext(LensHoverContext);
  return useSyncExternalStore(store.subscribe, store.get);
}
