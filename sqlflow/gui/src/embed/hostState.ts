// What a hosted page knows from its host (protocol.ts), kept where React reads it: who is signed in and where the
// control plane is, the working copy a side bar lists, the text of the documents a page watches, and the mode of the
// host's own theme. Each is a small store read with useSyncExternalStore, so a message from the host re-renders exactly
// what reads it, and nothing here sets state from an effect.

import { useEffect, useSyncExternalStore } from "react";
import { pageHost } from "./hostBridge";
import { PROTOCOL_VERSION, type HostStart, type WatchedDocument, type WorkspaceState } from "./protocol";
import type { ThemeMode } from "../theme/designs";

/** A value that changes, and who wants to know. */
class Store<T> {
  private value: T;
  private readonly listeners = new Set<() => void>();

  constructor(initial: T) {
    this.value = initial;
  }

  readonly read = (): T => this.value;

  readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  set(next: T): void {
    if (Object.is(next, this.value)) {
      return;
    }

    this.value = next;
    for (const listener of [...this.listeners]) {
      listener();
    }
  }
}

const start = new Store<HostStart | null>(null);
const workspace = new Store<WorkspaceState | null>(null);
const watched = new Map<string, { store: Store<WatchedDocument | null>; readers: number }>();

let connected: Promise<HostStart> | null = null;

/**
 * Tells the host this page is ready and waits for what it starts the page with. Called once, before the page renders:
 * a page cannot make a call until it knows where the control plane is. What the host says afterwards (another person
 * signed in, another control plane, the working copy, a watched document) lands in the stores above.
 */
export function connectHost(): Promise<HostStart> {
  connected ??= new Promise<HostStart>((resolve, reject) => {
    const host = pageHost();
    host.listen((message) => {
      switch (message.type) {
        case "start":
          if (message.start.protocol !== PROTOCOL_VERSION) {
            reject(new Error(
              `This page speaks version ${PROTOCOL_VERSION} of the host protocol and its host version ${message.start.protocol}; `
              + "they are built together, so reinstall the extension.",
            ));
            return;
          }

          start.set(message.start);
          resolve(message.start);
          return;
        case "workspace":
          workspace.set(message.workspace);
          return;
        case "document":
          watched.get(message.watched.document)?.store.set(message.watched);
          return;
        default:
          return;
      }
    });
    host.send({ type: "ready", protocol: PROTOCOL_VERSION });
  });
  return connected;
}

/**
 * Hears what the host starts the page with each time it says so again (another person signed in, another control
 * plane), outside React: what the page's entry keeps in step with it before anything renders with the new values.
 */
export function onHostStart(listener: (next: HostStart) => void): () => void {
  return start.subscribe(() => {
    const next = start.read();
    if (next !== null) {
      listener(next);
    }
  });
}

/** What the host started the page with, as it stands now. Read only below a render that waited for `connectHost`. */
export function useHostStart(): HostStart {
  const value = useSyncExternalStore(start.subscribe, start.read);
  if (value === null) {
    throw new Error("The host has not started this page yet; connectHost() resolves before the page renders.");
  }

  return value;
}

/** The working copy as the host lists it; null until the host has said. */
export function useWorkspace(): WorkspaceState | null {
  return useSyncExternalStore(workspace.subscribe, workspace.read);
}

const NOTHING_WATCHED = new Store<WatchedDocument | null>(null);

/** The entry a document's text is kept in, made the first time anything asks for it and kept from then on. */
function watchedEntry(document: string): { store: Store<WatchedDocument | null>; readers: number } {
  let entry = watched.get(document);
  if (entry === undefined) {
    entry = { store: new Store<WatchedDocument | null>(null), readers: 0 };
    watched.set(document, entry);
  }

  return entry;
}

/**
 * The text of a document of the working copy as the editor holds it now, and again whenever it changes, typed or
 * saved: what a page that shows a document being written reads. Null until the host has answered, and for no document.
 * The host is asked while at least one reader is mounted, and told to stop when the last one goes.
 */
export function useWatchedDocument(document: string | null): WatchedDocument | null {
  useEffect(() => {
    if (document === null) {
      return undefined;
    }

    const entry = watchedEntry(document);
    entry.readers += 1;
    if (entry.readers === 1) {
      pageHost().send({ type: "watch", document });
    }

    return () => {
      entry.readers -= 1;
      if (entry.readers === 0) {
        pageHost().send({ type: "unwatch", document });
        // The next reader waits for the host's answer rather than starting from text that may have moved on since.
        entry.store.set(null);
      }
    };
  }, [document]);

  const store = document === null ? NOTHING_WATCHED : watchedEntry(document).store;
  return useSyncExternalStore(store.subscribe, store.read);
}

// ---- The host's theme ---------------------------------------------------------------------------------------------

/** The mode of the host's theme, and a number that moves whenever its colours do. */
export interface HostTheme {
  mode: ThemeMode;
  revision: number;
}

/** An editor marks a webview's body with the kind of its theme; both light kinds are light, everything else dark. */
function modeOfHost(): ThemeMode {
  const kinds = document.body.classList;
  return kinds.contains("vscode-light") || kinds.contains("vscode-high-contrast-light") ? "light" : "dark";
}

let theme: Store<HostTheme> | null = null;

function themeStore(): Store<HostTheme> {
  if (theme !== null) {
    return theme;
  }

  const store = new Store<HostTheme>({ mode: modeOfHost(), revision: 0 });
  // The editor rewrites the theme's variables on <html> and the theme's kind and name on <body> when the person
  // changes theme, between two dark ones as well: either is a change of colours.
  const changed = new MutationObserver(() => {
    store.set({ mode: modeOfHost(), revision: store.read().revision + 1 });
  });
  changed.observe(document.documentElement, { attributes: true, attributeFilter: ["style"] });
  changed.observe(document.body, { attributes: true, attributeFilter: ["class", "data-vscode-theme-id"] });
  theme = store;
  return store;
}

/** The host's theme now, without subscribing: what the first paint reads. */
export function hostThemeNow(): HostTheme {
  return themeStore().read();
}

/** The host's theme, followed as the person changes it. */
export function useHostTheme(): HostTheme {
  const store = themeStore();
  return useSyncExternalStore(store.subscribe, store.read);
}
