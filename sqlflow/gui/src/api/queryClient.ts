import { focusManager, QueryClient } from "@tanstack/react-query";
import { isApiError, rateLimitPausedUntil, subscribeRateLimit } from "./client";
import { askRefusedAgainOnFocus, createPollingGate } from "./pollingGate";

// The query client every page renders under, in a browser tab (bootstrap.tsx) and in an editor's webview
// (embed/startHosted.tsx) alike, and the page's polling gate (pollingGate.ts) it polls under.

/** The page's one gate, over TanStack's focus manager, which every query client of the page reads. */
const gate = createPollingGate({ setFocused: (focused) => focusManager.setFocused(focused) });
gate.setPaused(rateLimitPausedUntil() !== null);
subscribeRateLimit((until) => gate.setPaused(until !== null));

/** Whether an answer refused the call for calling too often. */
function refusedForRate(error: unknown): boolean {
  return isApiError(error) && error.status === 429;
}

/**
 * The page's query client. A 4xx answer is the answer and is not asked again (a 429 included: the pause handles it), and
 * anything else gets two more tries; an answer is fresh for five seconds. Its polls run under the page's polling gate:
 * held while the control plane has the GUI paused for calling too often or the surface is hidden, and once the page has
 * focus again, what went stale is read again (TanStack's own refetch on focus), and so is every query on the page a 429
 * refused, however fresh its data, so no refusal stays on screen. Made once per page.
 */
export function createQueryClient(): QueryClient {
  const client = new QueryClient({
    defaultOptions: {
      queries: {
        retry: (failureCount, error) => {
          if (isApiError(error) && error.status >= 400 && error.status < 500) {
            return false;
          }

          return failureCount < 2;
        },
        staleTime: 5_000,
      },
    },
  });
  askRefusedAgainOnFocus(focusManager, client, refusedForRate);
  return client;
}

/** Says whether the surface is in front of the person: what an editor's webview host tells its page (embed/hostState.ts). */
export function setSurfaceShown(shown: boolean): void {
  gate.setShown(shown);
}
