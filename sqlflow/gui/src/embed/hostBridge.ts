// The page's end of the conversation with what hosts it (protocol.ts). An editor's webview gives a page one channel to
// its host, taken once: this module takes it, and everything a hosted page says or hears goes through here.

import type { HostMessage, PageMessage } from "./protocol";

/** The channel an editor's webview hands its page: `acquireVsCodeApi()`, which may be called once per page load. */
interface EditorChannel {
  postMessage(message: unknown): void;
  getState(): unknown;
  setState(state: unknown): void;
}

declare global {
  interface Window {
    acquireVsCodeApi?: () => EditorChannel;
  }
}

type Listener = (message: HostMessage) => void;

/** A page's host: what it is told, what it says, and the little the host keeps for the page between loads. */
export interface PageHost {
  send(message: PageMessage): void;
  /** Hears everything the host says from now on; the function returned stops listening. */
  listen(listener: Listener): () => void;
  /** What the host kept for this page the last time it was shown, or null. */
  kept<T>(): T | null;
  /** Has the host keep this for the page, to bring it back as it was after a restart. */
  keep(state: unknown): void;
}

let host: PageHost | null = null;

/**
 * The host of this page. Throws when the page is not hosted: a hosted entry is built for a host, and opened in a
 * browser tab it has nobody to carry its calls, which is better said at once than found one failed call at a time.
 */
export function pageHost(): PageHost {
  if (host !== null) {
    return host;
  }

  const acquire = window.acquireVsCodeApi;
  if (typeof acquire !== "function") {
    throw new Error("This page is built to be shown by an editor, which carries its calls; a browser tab cannot show it.");
  }

  const channel = acquire();
  const listeners = new Set<Listener>();
  window.addEventListener("message", (event: MessageEvent<unknown>) => {
    const message = event.data;
    if (message === null || typeof message !== "object" || typeof (message as { type?: unknown }).type !== "string") {
      return;
    }

    for (const listener of [...listeners]) {
      listener(message as HostMessage);
    }
  });

  host = {
    send: (message) => channel.postMessage(message),
    listen: (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    kept: <T>() => (channel.getState() ?? null) as T | null,
    keep: (state) => channel.setState(state),
  };
  return host;
}
