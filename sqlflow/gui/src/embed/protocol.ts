// What a hosted page and its host say to each other. A page of this GUI can be shown by a host other than a browser tab
// (an editor's webview): the host serves the page's files, carries its calls to the control plane under the person's
// sign-in, and opens what the page asks for in the host's own tabs. Both sides compile this one file, the page here and
// the host in tools/sqlflow-vscode, so a message one side sends is a message the other reads. Kept free of imports and of
// anything a browser or Node alone has, so both compile it and the unit suites read it.

/** The version of this contract. A host and a page of different versions do not talk: each is built with the other. */
export const PROTOCOL_VERSION = 1;

/** Who is signed in, as the host knows it. The credential itself never reaches the page. */
export interface HostSession {
  subject: string;
  /** The person's role; null for a credential with no account behind it. */
  role: string | null;
  scopes: string[];
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
  /** The GUI's public address, when the host knows it: where "open in the browser" goes. */
  guiBaseUrl: string | null;
}

/**
 * How a document of the working copy stands against the estate: it is what the estate runs (`same`), it differs from it
 * (`differs`), the estate has it and the two were not compared (`estate`), the estate does not have it (`local`), or
 * the estate could not be asked (`unknown`).
 */
export type DocumentStanding = "same" | "differs" | "estate" | "local" | "unknown";

/** A document of the working copy the host has open, as its side bar lists it. */
export interface WorkspaceDocument {
  /** The host's name for the document, given back to open it or act on it. */
  id: string;
  /** What the document calls itself, or its file's name. */
  name: string;
  /** Its path from the working copy's root, forward slashed. */
  path: string;
  /** The sort of document, as a heading names its group: "Ingestion flows", "Mappings". */
  group: string;
  /** The kind of a flow, which draws its icon; absent for a document that is not a flow. */
  flowKind?: string;
  standing: DocumentStanding;
  /** What can be done with it, each by a name the host gave. */
  actions: DocumentAction[];
}

/** Something the host does with a document: run it, show its page, compare it with what runs. */
export interface DocumentAction {
  id: string;
  label: string;
  /** A name from the side bar's icon table (HostedSideBar.tsx); an unknown one draws no icon. */
  icon?: string;
  /** Shown on the row itself, beside the name, and not only in its menu. */
  primary?: boolean;
}

/** The working copy as the host's side bar shows it. */
export interface WorkspaceState {
  /** The documents, in path order. */
  documents: WorkspaceDocument[];
  /** The document in the editor in front of the person, when it is one of these. */
  active: string | null;
  /** True while the host has not finished reading the working copy. */
  reading: boolean;
}

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
  | { type: "act"; action: HostAction; argument?: string }
  /** Something the host offered for a document of the working copy (`WorkspaceDocument.actions`). */
  | { type: "documentAction"; document: string; action: string }
  /** Tell the page the text of a working copy document now and whenever it changes, until `unwatch`. */
  | { type: "watch"; document: string }
  | { type: "unwatch"; document: string };

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
  /** The working copy, to a side bar: once it is ready and whenever it changes. */
  | { type: "workspace"; workspace: WorkspaceState }
  /** A watched document's text. */
  | { type: "document"; watched: WatchedDocument };

/**
 * What a page may ask its host to do. A fixed list, so a page (and anything that got to run in one) can never name a
 * command of the host's own choosing.
 */
export const HOST_ACTIONS = [
  "signIn",
  "signInWithToken",
  "signOut",
  "setControlPlane",
  "openDocument",
  "newDocument",
  "validateWorkspace",
  "refreshWorkspace",
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
