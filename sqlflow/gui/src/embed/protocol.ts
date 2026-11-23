// What a hosted page and its host say to each other. A page of this GUI can be shown by a host other than a browser tab
// (an editor's webview): the host serves the page's files, carries its calls to the control plane under the person's
// sign-in, and opens what the page asks for in the host's own tabs. Both sides compile this one file, the page here and
// the host in tools/sqlflow-vscode, so a message one side sends is a message the other reads. Kept free of imports but the
// rules of environments beside it, which are kept the same way, and of anything a browser or Node alone has, so both
// compile it and the unit suites read it.

import type { SavedEnvironment } from "./environments";

/** The version of this contract. A host and a page of different versions do not talk: each is built with the other. */
export const PROTOCOL_VERSION = 4;

/** Who is signed in, as the host knows it. The credential itself never reaches the page. */
export interface HostSession {
  subject: string;
  /** The person's role; null for a credential with no account behind it. */
  role: string | null;
  scopes: string[];
}

/**
 * A browser sign-in waiting for the person: the code they approve, which their browser's page shows too, and that page's
 * address, to open it again. The code is the one a person reads and compares; what the host polls with never leaves it.
 */
export interface HostSignIn {
  code: string;
  url: string;
}

/**
 * An environment the person saved (environments.ts), with whether a sign-in is kept for it: the shared credential file
 * keeps one per control plane, so switching to an environment that has one signs in with it and opens no browser.
 */
export interface HostEnvironment extends SavedEnvironment {
  /** Whether a sign-in is kept for its control plane that has not ended. */
  signedIn: boolean;
  /** Who that sign-in is, when it says. */
  account: string | null;
}

/** What a page needs before it renders, given once it says it is ready and again whenever any of it changes. */
export interface HostStart {
  protocol: number;
  /** The product's name, as the host's own windows and messages call it. */
  product: string;
  /** The control plane's base address, which the page's calls are built on and the host carries. Null when none is set. */
  apiBaseUrl: string | null;
  session: HostSession | null;
  /** Why there is no session, when the host knows: the control plane cannot be reached, say. */
  sessionProblem: string | null;
  /** The browser sign-in waiting for the person's approval, while one is. */
  signingIn: HostSignIn | null;
  /** The GUI's public address, when the host knows it: where "open in the browser" goes. */
  guiBaseUrl: string | null;
  /** The environments the person saved, in their order. */
  environments: HostEnvironment[];
  /** The saved environment the control plane in use is; null when it is none of them, or none is set. */
  environment: HostEnvironment | null;
}

/** Whether an address answers as a control plane, and in a sentence what it answered. */
export interface ControlPlaneReach {
  answers: boolean;
  detail: string;
}

/**
 * Something done to the person's environments, which only the host's side bar may ask for: work against a saved one,
 * save one (new, or in place of the one at `replaces`, and with `use` work against it), remove one, or ask whether an
 * address answers. A switch names a saved environment by its address and moves to nothing else, so no page can send a
 * person's sign-in to an address of its choosing.
 */
export type EnvironmentRequest =
  | { kind: "use"; url: string }
  | { kind: "save"; replaces: string | null; environment: SavedEnvironment; use: boolean }
  | { kind: "remove"; url: string }
  | { kind: "probe"; url: string };

/** The host's answer to an `EnvironmentRequest`: done (with what a probe found), or why not. */
export type EnvironmentAnswer =
  | { ok: true; reach: ControlPlaneReach | null }
  | { ok: false; problem: string };

/** The text of a working copy document a page watches, as the editor holds it now, saved or not. */
export interface WatchedDocument {
  /** The name the page asked by. */
  document: string;
  /** The text, or null when the document is gone or was never there. */
  text: string | null;
  /** Its path from the working copy's root, forward slashed; null with the text. */
  path: string | null;
  /** Whether the editor holds changes the file does not. */
  unsaved: boolean;
}

/** A call's body as it crosses: text as it is, anything else as bytes. */
export type CarriedBody = { text: string } | { bytes: Uint8Array };

/**
 * The bytes of a carried body as they arrive. A host's channel hands bytes over as a typed array, as the buffer under
 * one, or (an older channel, which wrote them as text) as an object of numbered entries; this reads all three.
 */
export function carriedBytes(value: unknown): Uint8Array {
  if (value instanceof Uint8Array) {
    return value;
  }

  if (value instanceof ArrayBuffer) {
    return new Uint8Array(value);
  }

  if (ArrayBuffer.isView(value)) {
    return new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
  }

  if (value !== null && typeof value === "object") {
    const entries = (value as { data?: unknown }).data ?? value;
    const numbers = Array.isArray(entries) ? entries : Object.values(entries as Record<string, unknown>);
    if (numbers.every((entry) => typeof entry === "number" && Number.isInteger(entry) && entry >= 0 && entry <= 255)) {
      return Uint8Array.from(numbers as number[]);
    }
  }

  throw new TypeError("A carried body's bytes arrived as something that is not bytes.");
}

/** What a page sends its host. */
export type PageMessage =
  /** The page has loaded and listens: the host answers with `start`. */
  | { type: "ready"; protocol: number }
  /** A call to the control plane. `stream` asks for the answer's body as it arrives (server-sent events). */
  | { type: "request"; id: number; method: string; url: string; headers: Record<string, string>; body?: CarriedBody; stream: boolean }
  /** The page no longer waits for a call. */
  | { type: "abort"; id: number }
  /** The page shown and what it is called, for the host's tab and for bringing the page back after a restart. */
  | { type: "located"; path: string; title: string; flowKind?: string }
  /** Open a page of the GUI in a tab of its own. */
  | { type: "open"; path: string }
  /** Open an address outside the GUI where the person browses. */
  | { type: "external"; url: string }
  /** Hand the person a file the page made or was served. */
  | { type: "save"; name: string; contentType: string; bytes: Uint8Array }
  /** Something the host does for the page, by a name from `HOST_ACTIONS`. */
  | { type: "act"; action: HostAction }
  /** Tell the page the text of a working copy document now and whenever it changes, until `unwatch`. */
  | { type: "watch"; document: string }
  | { type: "unwatch"; document: string }
  /** Something done to the person's environments; the host answers with `environmentAnswer` and the same id. */
  | { type: "environment"; id: number; request: EnvironmentRequest };

/** What a host sends a page. */
export type HostMessage =
  | { type: "start"; start: HostStart }
  /** Show another page in this tab. */
  | { type: "navigate"; path: string }
  /** A call was answered. With `streamed`, the body follows as `chunk`s until `end`. */
  | { type: "response"; id: number; status: number; statusText: string; headers: Record<string, string>; body?: CarriedBody; streamed: boolean }
  | { type: "chunk"; id: number; text: string }
  | { type: "end"; id: number }
  /** A call never reached the control plane, or was dropped. */
  | { type: "failed"; id: number; reason: string; aborted: boolean }
  /** A watched document's text. */
  | { type: "document"; watched: WatchedDocument }
  | { type: "environmentAnswer"; id: number; answer: EnvironmentAnswer }
  /** Show the person's environments, as the host's own command asks. */
  | { type: "showEnvironments" };

/**
 * What a page may ask its host to do. A fixed list, so a page (and anything that got to run in one) can never name a
 * command of the host's own choosing.
 */
export const HOST_ACTIONS = [
  "signIn",
  "signInWithToken",
  /** Stop waiting for the browser sign-in the page shows. */
  "cancelSignIn",
  "signOut",
  /** Show the person's environments, where they are switched, added and changed: the host's side bar. */
  "manageEnvironments",
  /** What the control plane runs has changed (a repository was synced): the host reads again what it keeps of it. */
  "refreshEstate",
  "connectAssistant",
  "searchDocs",
] as const;

export type HostAction = (typeof HOST_ACTIONS)[number];

export function isHostAction(value: unknown): value is HostAction {
  return typeof value === "string" && (HOST_ACTIONS as readonly string[]).includes(value);
}

/** The token a hosted page's client presents: a placeholder the host replaces with the person's credential. */
export const HOSTED_TOKEN = "hosted";

/**
 * The control plane address a call is for, as the path and query the host carries, or null when the address is not under
 * the control plane's base: a page asks its host only for the control plane, never for another origin.
 */
export function carriedPath(url: string, apiBaseUrl: string): string | null {
  let target: URL;
  let base: URL;
  try {
    target = new URL(url);
    base = new URL(apiBaseUrl);
  } catch {
    return null;
  }

  if (target.origin !== base.origin || target.username !== "" || target.password !== "") {
    return null;
  }

  const prefix = base.pathname.replace(/\/+$/, "");
  if (prefix !== "" && target.pathname !== prefix && !target.pathname.startsWith(`${prefix}/`)) {
    return null;
  }

  return `${target.pathname}${target.search}`;
}

/** The headers of a page's call the host passes on. The credential and what the connection itself sets are the host's. */
const PASSED_HEADERS = /^(accept|content-type|x-[a-z0-9-]+)$/i;

export function passedHeaders(headers: Record<string, string>): Record<string, string> {
  const passed: Record<string, string> = {};
  for (const [name, value] of Object.entries(headers)) {
    if (PASSED_HEADERS.test(name) && typeof value === "string" && !/[\r\n]/.test(value)) {
      passed[name] = value;
    }
  }

  return passed;
}

/** The methods a page's call may use. */
export const CARRIED_METHODS: readonly string[] = ["GET", "POST", "PUT", "DELETE", "PATCH"];

/** A path of the GUI, as a page or its host names one: absolute within the GUI, never an address of its own. */
export function isGuiPath(value: unknown): value is string {
  return typeof value === "string" && value.startsWith("/") && !value.startsWith("//") && !/[\r\n\\]/.test(value);
}
