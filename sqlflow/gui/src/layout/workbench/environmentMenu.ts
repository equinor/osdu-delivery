// What the title bar shows of the estate's environments (ControlPlane:Environments): the one this deployment is, and the
// others a person opens from it. Kept apart from the control so the rules are tested without a browser.

import type { EstateEnvironments } from "../../api/types";

/** One environment as the menu lists it. */
export interface EnvironmentEntry {
  name: string;
  description: string | null;
  /** The address the entry opens: the environment's GUI. Null for the current one when the list does not hold it. */
  url: string | null;
  /** The address as a person reads it: host and path, without the scheme or a trailing slash. */
  address: string | null;
  current: boolean;
}

/** The title bar's environment control. */
export interface EnvironmentMenu {
  /** The environment this deployment is. */
  current: EnvironmentEntry;
  /** Every environment in the configured order, the current one among them; empty when only the current one is named. */
  entries: EnvironmentEntry[];
  /** Whether another environment can be opened, so the title bar offers a menu rather than only naming this one. */
  canSwitch: boolean;
}

/**
 * An http(s) address as a person reads it (host, then the path without a trailing slash), or null for anything else. The
 * control plane refuses an address that is not http(s) at start; the GUI checks again, since a link it renders is a link
 * a person follows.
 */
export function readableAddress(url: string): string | null {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return null;
  }

  if (parsed.protocol !== "https:" && parsed.protocol !== "http:") {
    return null;
  }

  return parsed.host + parsed.pathname.replace(/\/+$/, "");
}

/** Whether `value` is an object, so its fields can be read. */
function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/**
 * The control the title bar shows for `environments`, or null when no environment is configured, the list has not
 * loaded, or the answer is not one it can read, and the title bar then shows none. The title bar is on every page, so
 * nothing in the answer may break it: an entry it cannot read, or whose address is not http(s), is left out rather than
 * linked.
 */
export function environmentMenu(environments: EstateEnvironments | undefined): EnvironmentMenu | null {
  const answer: unknown = environments;
  if (!isRecord(answer) || typeof answer.current !== "string" || answer.current.trim() === "") {
    return null;
  }

  const currentName = answer.current.trim();
  const listed: unknown[] = Array.isArray(answer.environments) ? answer.environments : [];
  const entries = listed.flatMap((environment): EnvironmentEntry[] => {
    if (!isRecord(environment) || typeof environment.name !== "string" || environment.name.trim() === ""
      || typeof environment.url !== "string") {
      return [];
    }

    const current = environment.current === true;
    const address = readableAddress(environment.url);
    if (address === null && !current) {
      return [];
    }

    return [{
      name: environment.name,
      description: typeof environment.description === "string" && environment.description.trim() !== ""
        ? environment.description
        : null,
      url: address === null ? null : environment.url,
      address,
      current,
    }];
  });

  const current = entries.find((entry) => entry.current)
    ?? { name: currentName, description: null, url: null, address: null, current: true };
  return { current, entries, canSwitch: entries.some((entry) => !entry.current) };
}
