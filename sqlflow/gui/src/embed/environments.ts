// The environments a person works against from a host (an editor): each a control plane of its own, saved by the person
// with a name and the stage it is (development, test or production), so every surface of the host says which one it acts
// on, in the stage's colour, and the person moves between them without typing an address again. The rules here are what
// a saved environment is and how one typed into a form is checked; the page checks with them as the person types and the
// host checks again before it saves, so both say the same. Kept free of imports, so the page, the host and the unit
// suites read it.

/** The stages an environment can be, in the order a person moves work through them. */
export const ENVIRONMENT_STAGES = ["development", "test", "production"] as const;

export type EnvironmentStage = (typeof ENVIRONMENT_STAGES)[number];

/** A stage as a person reads it. */
export const STAGE_WORDS: Readonly<Record<EnvironmentStage, string>> = {
  development: "Development",
  test: "Test",
  production: "Production",
};

export function isEnvironmentStage(value: unknown): value is EnvironmentStage {
  return typeof value === "string" && (ENVIRONMENT_STAGES as readonly string[]).includes(value);
}

/** An environment as the host saves it: one control plane, what the person calls it, and the stage it is. */
export interface SavedEnvironment {
  /** What the person calls it: `dev`, `test`, `prod`. */
  name: string;
  /** The control plane's base address. */
  url: string;
  stage: EnvironmentStage;
  /** The public address of the environment's GUI, where "open in the browser" goes; null when the person gave none. */
  guiUrl: string | null;
}

/** The most environments a person keeps: more than any estate runs, few enough to list without a search. */
export const MAX_ENVIRONMENTS = 20;

/** The longest a name may be: the control plane's own limit for an environment's name (ControlPlane:Environments). */
export const MAX_NAME_LENGTH = 64;

/** The longest an address may be. */
export const MAX_URL_LENGTH = 2000;

/** The control plane's own rule for an environment's name, so a name saved here reads the same as the title bar's. */
const NAME = /^[A-Za-z0-9][A-Za-z0-9._-]*$/;

/** Why `name` cannot name an environment, or null when it can. */
export function environmentNameProblem(name: string): string | null {
  const trimmed = name.trim();
  if (trimmed === "") {
    return "Name the environment, for example dev, test or prod.";
  }

  if (trimmed.length > MAX_NAME_LENGTH) {
    return `A name is at most ${MAX_NAME_LENGTH} characters.`;
  }

  return NAME.test(trimmed)
    ? null
    : "A name is letters, digits, '-', '_' and '.', starting with a letter or a digit.";
}

/**
 * Why `url` cannot be the address of a control plane (or, with `gui`, of a GUI), or null when it can: an absolute http or
 * https address, with no user name, password, query or fragment, since only the address belongs there and a credential
 * typed into one would be sent with every call.
 */
export function addressProblem(url: string, what: "control plane" | "GUI" = "control plane"): string | null {
  const trimmed = url.trim();
  if (trimmed === "") {
    return what === "GUI"
      ? "Enter the GUI's address, or leave it empty."
      : "Enter the control plane's address, for example https://sqlflow-dev.example.com.";
  }

  if (trimmed.length > MAX_URL_LENGTH) {
    return `An address is at most ${MAX_URL_LENGTH} characters.`;
  }

  let parsed: URL;
  try {
    parsed = new URL(trimmed);
  } catch {
    return "This is not an address: it starts with https:// or http://.";
  }

  if (parsed.protocol !== "https:" && parsed.protocol !== "http:") {
    return "The address starts with https:// or http://.";
  }

  if (parsed.username !== "" || parsed.password !== "") {
    return "An address never carries a user name or a password.";
  }

  return parsed.search !== "" || parsed.hash !== ""
    ? "Leave out the query and the fragment: only the address belongs here."
    : null;
}

/** An address as it is saved: trimmed, without a trailing slash. */
export function savedAddress(url: string): string {
  return url.trim().replace(/\/+$/, "");
}

/**
 * The key a control plane is known by: scheme and host in lower case, a default port left out and no trailing slash, as
 * the shared credential file keys a sign-in (`credentialKey` in the extension, `credential_key` in the MCP server), so an
 * environment and the sign-in kept for it are found by the same address.
 */
export function controlPlaneKey(url: string): string {
  let key: string;
  try {
    const parsed = new URL(url.trim());
    key = `${parsed.origin}${parsed.pathname}`;
  } catch {
    key = url.trim();
  }

  return key.replace(/\/+$/, "").toLowerCase();
}

export function sameControlPlane(a: string, b: string): boolean {
  return controlPlaneKey(a) === controlPlaneKey(b);
}

/** The saved environment the control plane at `url` is, or null when it is none of them. */
export function environmentOf<T extends SavedEnvironment>(environments: readonly T[], url: string | null): T | null {
  if (url === null) {
    return null;
  }

  return environments.find((environment) => sameControlPlane(environment.url, url)) ?? null;
}

// ---- The stage a name says ------------------------------------------------------------------------------------------

const STAGE_WORDS_SAID: Readonly<Record<EnvironmentStage, readonly string[]>> = {
  development: ["dev", "develop", "development", "local", "localhost", "sandbox", "sbx"],
  test: ["test", "tst", "testing", "qa", "uat", "stage", "staging", "stg", "preprod", "acc", "acceptance"],
  production: ["prod", "prd", "production", "live"],
};

/** The stages the words of `text` name, each once. "pre-prod" is one word, so it says test and not production. */
function stagesSaid(text: string): Set<EnvironmentStage> {
  const said = new Set<EnvironmentStage>();
  const words = text.toLowerCase().replace(/pre[-_.]prod/g, "preprod").split(/[^a-z0-9]+/);
  for (const raw of words) {
    // dev1, prod02: a number after the word is an instance, not another word.
    const word = raw.replace(/\d+$/, "");
    for (const stage of ENVIRONMENT_STAGES) {
      if (STAGE_WORDS_SAID[stage].includes(word)) {
        said.add(stage);
      }
    }
  }

  return said;
}

/**
 * The stage an environment's name says, or else its address's host, or null when neither says exactly one: a guess the
 * person sees chosen and can change, never a default. A name that says two stages (`test-prod`) says none, and so does
 * a name that says nothing, so a production control plane at a plain address is never shown in development's colour
 * because nobody picked.
 */
export function guessStage(name: string, url: string): EnvironmentStage | null {
  const fromName = stagesSaid(name);
  if (fromName.size === 1) {
    return [...fromName][0];
  }

  if (fromName.size > 1) {
    return null;
  }

  let host: string;
  try {
    host = new URL(url.trim()).hostname;
  } catch {
    return null;
  }

  if (host === "127.0.0.1" || host === "[::1]") {
    return "development";
  }

  const fromHost = stagesSaid(host);
  return fromHost.size === 1 ? [...fromHost][0] : null;
}

// ---- An environment typed into a form -------------------------------------------------------------------------------

/** What a person typed into the form: the stage is null until they pick one or the name says it. */
export interface EnvironmentDraft {
  name: string;
  url: string;
  stage: EnvironmentStage | null;
  /** Empty when the person gives no GUI address. */
  guiUrl: string;
}

/** What is wrong with a draft, by the field it is about. */
export interface DraftProblems {
  name?: string;
  url?: string;
  stage?: string;
  guiUrl?: string;
}

/**
 * The environment a draft saves as, or what is wrong with it. `others` are the saved environments it sits beside (the
 * one being edited left out), since two environments with one name, or one control plane saved twice, could not be told
 * apart in a list or in the colour it is shown in.
 */
export function checkDraft(
  draft: EnvironmentDraft,
  others: readonly SavedEnvironment[],
): { environment: SavedEnvironment; problems: null } | { environment: null; problems: DraftProblems } {
  const problems: DraftProblems = {};
  const name = draft.name.trim();
  const url = savedAddress(draft.url);
  const guiUrl = savedAddress(draft.guiUrl);

  const nameProblem = environmentNameProblem(name);
  if (nameProblem !== null) {
    problems.name = nameProblem;
  } else {
    const named = others.find((other) => other.name.toLowerCase() === name.toLowerCase());
    if (named !== undefined) {
      problems.name = `Another environment is called ${named.name}.`;
    }
  }

  const urlProblem = addressProblem(url);
  if (urlProblem !== null) {
    problems.url = urlProblem;
  } else {
    const same = others.find((other) => sameControlPlane(other.url, url));
    if (same !== undefined) {
      problems.url = `${same.name} is already saved with this address.`;
    }
  }

  if (draft.stage === null) {
    problems.stage = "Pick the stage: it is the colour every surface shows this environment in.";
  }

  if (guiUrl !== "") {
    const guiProblem = addressProblem(guiUrl, "GUI");
    if (guiProblem !== null) {
      problems.guiUrl = guiProblem;
    }
  }

  if (Object.keys(problems).length > 0 || draft.stage === null) {
    return { environment: null, problems };
  }

  return { environment: { name, url, stage: draft.stage, guiUrl: guiUrl === "" ? null : guiUrl }, problems: null };
}

// ---- The saved list, as the host's settings hold it -----------------------------------------------------------------

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/**
 * The saved environments, read from what the host's settings hold, which a person may have edited by hand: an entry that
 * cannot be read, names an environment or a control plane an earlier entry names, or is past the
 * {@link MAX_ENVIRONMENTS}th, is left out, and `problems` says why, naming the entry by `setting`. Nothing here throws, so
 * a mistake in the setting never stops the host: it shows what it can.
 */
export function readSavedEnvironments(raw: unknown, setting: string): { environments: SavedEnvironment[]; problems: string[] } {
  const environments: SavedEnvironment[] = [];
  const problems: string[] = [];
  if (raw === undefined || raw === null) {
    return { environments, problems };
  }

  if (!Array.isArray(raw)) {
    problems.push(`${setting} is not a list of environments, so none is shown.`);
    return { environments, problems };
  }

  raw.forEach((entry: unknown, index) => {
    const at = `${setting}[${index}]`;
    if (index >= MAX_ENVIRONMENTS) {
      if (index === MAX_ENVIRONMENTS) {
        problems.push(`${setting} lists ${raw.length} environments; the first ${MAX_ENVIRONMENTS} are shown.`);
      }

      return;
    }

    if (!isRecord(entry)) {
      problems.push(`${at} is not an environment, so it is left out.`);
      return;
    }

    const stage = entry.stage;
    if (!isEnvironmentStage(stage)) {
      problems.push(`${at} is left out: its stage is not one of ${ENVIRONMENT_STAGES.join(", ")}.`);
      return;
    }

    const draft: EnvironmentDraft = {
      name: typeof entry.name === "string" ? entry.name : "",
      url: typeof entry.url === "string" ? entry.url : "",
      stage,
      guiUrl: typeof entry.guiUrl === "string" ? entry.guiUrl : "",
    };
    const checked = checkDraft(draft, environments);
    if (checked.problems !== null) {
      // An address is never repeated in a sentence: a mistyped one could carry a credential.
      const why = [checked.problems.name, checked.problems.url, checked.problems.guiUrl]
        .filter((text): text is string => text !== undefined)
        .map((text) => text.replace(/\s+$/, ""));
      problems.push(`${at} is left out: ${why.join(" ")}`);
      return;
    }

    environments.push(checked.environment);
  });

  return { environments, problems };
}
