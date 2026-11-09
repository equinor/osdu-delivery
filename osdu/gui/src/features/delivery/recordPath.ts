// A place in an OSDU record as the record views write it (`data.GeoContexts[1].GeoPoliticalEntityID`), read as the property
// names and list places it steps through. The dimension builder and the duplicate key's builder both pick values by it.
//
// The paths the server names a property by (osdu/docs/reference/flow/mapping-property-names.md) write a name after a dot
// when it can be written bare, and in brackets and quotes when it holds a dot, a bracket or a space, as JSONPath writes a
// name: `tags['lra.securityClassification']`. A template path (`osdu.tags['lra.dataID']`), a place a check of a record
// names and an outline's path all write a name so. The functions below read and write that notation as the server's
// RecordPathNotation does.

/** Where a value is in a record: property names and array places, from the record's root. */
export type Location = (string | number)[];

/**
 * The location an inspector's path names: `data.GeoContexts[1].GeoPoliticalEntityID` is data, GeoContexts, item 1 of it,
 * then GeoPoliticalEntityID.
 */
export function locationOf(path: string): Location {
  const location: Location = [];
  for (const match of path.matchAll(/\[(\d+)\]|([^.[\]]+)/g)) {
    location.push(match[1] !== undefined ? Number(match[1]) : match[2]);
  }

  return location;
}

/** The property names of a location, its array places left out, as a path names them. */
export function namesOf(location: Location): string[] {
  return location.filter((part): part is string => typeof part === "string");
}

/** One step of a path: a property by its name, every item of an array (`[]`, `[*]`), or one item (`[2]`). */
export type PathStep =
  | { kind: "property"; name: string }
  | { kind: "items" }
  | { kind: "wildcard" }
  | { kind: "index"; index: number };

/** A space as the server's paths read one (the regular expression class `\s` of .NET), which a bare name never holds. */
const SPACE = /[\f\n\r\t\v\u0085\p{Z}]/u;

/** Whether a name is written bare in a path: not empty, not `*`, and holding no dot, bracket or space. */
export function isPlainName(name: string): boolean {
  if (name === "" || name === "*") {
    return false;
  }

  for (const c of name) {
    if (c === "." || c === "[" || c === "]" || SPACE.test(c)) {
      return false;
    }
  }

  return true;
}

/** A name in brackets and single quotes, a quote and a backslash escaped and a control character written as its escape. */
export function quoteName(name: string): string {
  let text = "['";
  for (const c of name) {
    const code = c.codePointAt(0) ?? 0;
    if (c === "\\") {
      text += "\\\\";
    } else if (c === "'") {
      text += "\\'";
    } else if (c === "\b") {
      text += "\\b";
    } else if (c === "\f") {
      text += "\\f";
    } else if (c === "\n") {
      text += "\\n";
    } else if (c === "\r") {
      text += "\\r";
    } else if (c === "\t") {
      text += "\\t";
    } else if (code < 0x20) {
      text += `\\u${code.toString(16).padStart(4, "0")}`;
    } else {
      text += c;
    }
  }

  return `${text}']`;
}

/** The name as a path writes it as a step: bare when it can be, else quoted. */
export function nameStep(name: string): string {
  return isPlainName(name) ? name : quoteName(name);
}

/** The path of property `name` of what `path` names; the empty path is the record. */
export function appendName(path: string, name: string): string {
  if (!isPlainName(name)) {
    return path + quoteName(name);
  }

  return path === "" ? name : `${path}.${name}`;
}

/** The steps written as one path, each name bare where it can be. */
export function writePath(steps: readonly PathStep[]): string {
  let path = "";
  for (const step of steps) {
    switch (step.kind) {
      case "property":
        path = appendName(path, step.name);
        break;
      case "items":
        path += "[]";
        break;
      case "wildcard":
        path += "[*]";
        break;
      case "index":
        path += `[${step.index}]`;
        break;
    }
  }

  return path;
}

/** A path read into its steps, or why it is not one. */
export type ParsedPath = { steps: PathStep[]; error: null } | { steps: null; error: string };

/** The escapes a quoted name takes, by the character after the backslash. */
const ESCAPES: Record<string, string> = { "\\": "\\", "'": "'", '"': '"', "/": "/", b: "\b", f: "\f", n: "\n", r: "\r", t: "\t" };

/** Reads the quoted name whose quote is at `start`: the name, and the place just after its closing quote. */
function readQuoted(path: string, start: number): { name: string; end: number } | { error: string } {
  const quote = path[start];
  let name = "";
  let i = start + 1;
  while (i < path.length) {
    const c = path[i];
    if (c === quote) {
      return { name, end: i + 1 };
    }

    if (c < " ") {
      return { error: `the quoted name '${name}' holds a control character, which is written as its escape, such as \\n or \\u0001` };
    }

    if (c !== "\\") {
      name += c;
      i++;
      continue;
    }

    if (i + 1 >= path.length) {
      break;
    }

    const escaped = path[i + 1];
    if (escaped === "u" && /^[0-9a-fA-F]{4}$/.test(path.slice(i + 2, i + 6))) {
      name += String.fromCharCode(parseInt(path.slice(i + 2, i + 6), 16));
      i += 6;
      continue;
    }

    if (!(escaped in ESCAPES)) {
      return { error: `\\${escaped} in the quoted name '${name}' is not an escape: a quoted name escapes a quote, a backslash, /, b, f, n, r, t and uXXXX` };
    }

    name += ESCAPES[escaped];
    i += 2;
  }

  return { error: `the quoted name '${name}' is not closed with ${quote}` };
}

function skipBlanks(path: string, at: number): number {
  while (at < path.length && (path[at] === " " || path[at] === "\t")) {
    at++;
  }

  return at;
}

/**
 * Reads a path into its steps: names after dots or in quotes in brackets, and the steps into arrays, `[]`, `[*]` and `[n]`.
 * A bare `*` reads as `[*]`.
 */
export function parsePath(path: string): ParsedPath {
  const scanned = scanPath(path);
  return scanned.steps === null ? { steps: null, error: scanned.error } : { steps: scanned.steps, error: null };
}

/** A path read into its steps and where each starts in it, or why it is not one. */
function scanPath(path: string): { steps: PathStep[]; starts: number[]; error: null } | { steps: null; starts: null; error: string } {
  const refused = (error: string) => ({ steps: null, starts: null, error });
  if (path === "") {
    return refused("the path is empty");
  }

  const steps: PathStep[] = [];
  const starts: number[] = [];
  let afterDot = false;
  let i = 0;
  while (i < path.length) {
    const c = path[i];
    if (c === ".") {
      if (steps.length === 0 || afterDot) {
        return refused("it holds an empty property name: a dot follows nothing, or another dot");
      }

      afterDot = true;
      i++;
      continue;
    }

    if (c === "]") {
      return refused("a ']' closes no '['");
    }

    if (c === "[") {
      const open = skipBlanks(path, i + 1);
      if (open < path.length && (path[open] === "'" || path[open] === '"')) {
        const read = readQuoted(path, open);
        if ("error" in read) {
          return refused(read.error);
        }

        const close = skipBlanks(path, read.end);
        if (close >= path.length || path[close] !== "]") {
          return refused(`the quoted name '${read.name}' is not closed with ']'`);
        }

        if (read.name === "") {
          return refused("it holds an empty property name: [''] names nothing");
        }

        steps.push({ kind: "property", name: read.name });
        starts.push(i);
        afterDot = false;
        i = close + 1;
        continue;
      }

      const shut = path.indexOf("]", i + 1);
      if (shut < 0) {
        return refused("a '[' is not closed with ']'");
      }

      const inner = path.slice(i + 1, shut).trim();
      if (steps.length === 0) {
        return refused(`it starts with [${inner}], and a step into an array follows the array it steps into`);
      }

      starts.push(i);
      if (inner === "") {
        steps.push({ kind: "items" });
      } else if (inner === "*") {
        steps.push({ kind: "wildcard" });
      } else if (/^\d+$/.test(inner) && Number.isSafeInteger(Number(inner))) {
        steps.push({ kind: "index", index: Number(inner) });
      } else {
        return refused(`[${inner}] is not a step: a name in brackets is written in quotes, ['${inner}'], and an item by its place, such as [0]`);
      }

      afterDot = false;
      i = shut + 1;
      continue;
    }

    const start = i;
    while (i < path.length && path[i] !== "." && path[i] !== "[" && path[i] !== "]") {
      i++;
    }

    const bare = path.slice(start, i);
    if (steps.length > 0 && !afterDot) {
      return refused(`'${bare}' follows a step without a dot`);
    }

    if (bare === "*") {
      steps.push({ kind: "wildcard" });
    } else if (!isPlainName(bare)) {
      return refused(`'${bare}' holds a space, and a name holding one is written in quotes, ${quoteName(bare)}`);
    } else {
      steps.push({ kind: "property", name: bare });
    }

    starts.push(start);
    afterDot = false;
  }

  return afterDot ? refused("it ends with a dot") : { steps, starts, error: null };
}

/** The property names a path walks through, its steps into arrays left out; null for what is not a path. */
export function pathNames(path: string): string[] | null {
  const parsed = parsePath(path);
  return parsed.steps === null ? null : parsed.steps.flatMap((step) => (step.kind === "property" ? [step.name] : []));
}

/**
 * Whether `inner` is `outer` or lies inside it: a property of it, a quoted one among them, or anything in its items. Both are
 * paths as this notation writes them.
 */
export function isWithin(inner: string, outer: string): boolean {
  return inner === outer || (inner.length > outer.length && inner.startsWith(outer) && (inner[outer.length] === "." || inner[outer.length] === "["));
}

/**
 * Where the last property step of a path starts: just after its dot, or at its bracket when its name is quoted; -1 for what
 * is not a path, or one that names no property.
 */
export function lastStepStart(path: string): number {
  const scanned = scanPath(path);
  if (scanned.steps === null) {
    return -1;
  }

  for (let index = scanned.steps.length - 1; index >= 0; index--) {
    if (scanned.steps[index].kind === "property") {
      return scanned.starts[index];
    }
  }

  return -1;
}

/**
 * The path an inspector of a record names a place by, from a path the server names it by: each name after a dot whatever it
 * holds, and an item by its place (`tags.lra.dataID` for `tags['lra.dataID']`). The record views key their nodes so.
 */
export function inspectorPath(path: string): string {
  const parsed = parsePath(path);
  if (parsed.steps === null) {
    return path;
  }

  let written = "";
  for (const step of parsed.steps) {
    switch (step.kind) {
      case "property":
        written = written === "" ? step.name : `${written}.${step.name}`;
        break;
      case "index":
        written += `[${step.index}]`;
        break;
      case "items":
        written += "[]";
        break;
      case "wildcard":
        written += "[*]";
        break;
    }
  }

  return written;
}

/**
 * The array a path steps into the items of: `osdu.data.Curves` for `osdu.data.Curves[].CurveUnit`; null for a path that
 * steps into none, or that is not a path.
 */
export function repeaterOfPath(path: string): string | null {
  const parsed = parsePath(path);
  const at = parsed.steps === null ? -1 : parsed.steps.findIndex((step) => step.kind === "items");
  return at <= 0 || parsed.steps === null ? null : writePath(parsed.steps.slice(0, at));
}

/** How many times a path steps into the items of an array (`[]`); 0 for none, or for what is not a path. */
export function itemSteps(path: string): number {
  return parsePath(path).steps?.filter((step) => step.kind === "items").length ?? 0;
}

/**
 * The path of what holds the last property a path names, every step after that property left out with it: `osdu.data` for
 * `osdu.data.Name`, `osdu.data.Curves[]` for `osdu.data.Curves[].CurveUnit`, `osdu.tags` for `osdu.tags['lra.dataID']`.
 * Null for a path of one name, or what is not a path.
 */
export function holderOfPath(path: string): string | null {
  const start = lastStepStart(path);
  if (start <= 0) {
    return null;
  }

  return path[start - 1] === "." ? path.slice(0, start - 1) : path.slice(0, start);
}

/**
 * An outline's path without the place it names among a node's alternatives or a list's items: `data.Name` of
 * `data.Name|2`. The cut is at the first `|` outside a quoted name, so a name holding one stays whole.
 */
export function outlinePathOf(path: string): string {
  let quote: string | null = null;
  for (let i = 0; i < path.length; i++) {
    const c = path[i];
    if (quote !== null) {
      if (c === "\\") {
        i++;
      } else if (c === quote) {
        quote = null;
      }
    } else if ((c === "'" || c === '"') && i > 0 && path[i - 1] === "[") {
      quote = c;
    } else if (c === "|") {
      return path.slice(0, i);
    }
  }

  return path;
}
