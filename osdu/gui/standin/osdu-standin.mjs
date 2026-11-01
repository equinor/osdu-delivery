// The OSDU platform the e2e suite's flows reach, as far as a plan needs one: a token for the flows' client credentials,
// and the search service answering the lookups the sample mappings make when they render. A WellLog names its wellbore,
// and a render finds that wellbore by searching the platform; here the platform holds the wellbores of the five sample
// well database logs and the two the fixture documents name, in whichever partition the request names.
//
// A record can be read back as well, which is what a record page's In OSDU and Compare tabs do: through the storage
// service, or through the wellbore DDMS a well log flow delivers by. The wellbores are held as records too, and a spec
// can hold a record of its own for as long as it needs one, through /__e2e/records: a path of this stand-in, not of the
// platform, so no flow reaches it. Holding a record again keeps a version of it, so a record can have a history. The
// explorer's searches are answered over the same records: the kinds they are of, a page of them by type, a phrase, the
// start of an id or the records that name one, and the dimension builder's: the records holding any of several values,
// every record but those, and a property's values grouped commonest first. Storage's batch read answers for the same
// records, as the explorer's checks read them, and the Schema service answers for the schemas a spec holds through
// /__e2e/schemas, as a check of records against their kind's schema reads them, and lists them a status and scope at a
// time, as the explorer's Referenced by reads every schema of a partition. The OSDU data definitions are here too,
// under /__e2e/data-definitions: one release tag, and the example records a spec holds through /__e2e/examples, which a
// check's guidance quotes. Everything else answers 404, so a spec that tried to send a record would fail loudly rather
// than reach a real OSDU.
//
// Started by playwright.config.ts beside the control plane, on SQLFLOW_E2E_OSDU_PORT (5301 by default).
//
// With OSDU_STANDIN_WRITES=true it is the local environment's OSDU instead (osdu/docs/reference/cli/dev.md, demo): it starts
// empty, holds no wellbore of its own, and takes what a delivery sends (osdu-writes.mjs), keeping it in OSDU_STANDIN_STATE
// across restarts. A request it does not answer is written to its log, so a route a flow needs and it lacks is found at once.
import { createServer } from "node:http";

const port = Number(process.env.SQLFLOW_E2E_OSDU_PORT ?? 5301);

/** Whether this is a local environment's OSDU, which takes writes, rather than the e2e suite's, which refuses them. */
const acceptsWrites = process.env.OSDU_STANDIN_WRITES === "true";

/**
 * The wellbores the platform holds, by the name a search asks for: the wellbores of the sample well database logs
 * (osdu/samples/welldb/data/welllog) and the two the fixture documents name.
 */
const WELLBORES = new Set(acceptsWrites ? [] : [
  "Wellbore A-1",
  "Wellbore B-1",
  "Wellbore B-2 A",
  "Wellbore B-2 B",
  "Wellbore C-1",
  "OSDU-DEV-1-A",
  "OSDU-DEV-1-B",
]);

/**
 * The id a wellbore of that name has here: its name with spaces and slashes as hyphens (Wellbore B-2 B is
 * Wellbore-B-2-B), which is the id the delivery suites give it and one the WellboreID pattern takes.
 */
function wellboreId(name) {
  return name.replace(/[ /]/g, "-");
}

/** The kind the sample mappings search for wellbores in. */
const WELLBORE_KIND = /^osdu:wks:master-data--Wellbore:/;

/** The query a render sends to find a wellbore by name: the name's keyword, quoted and escaped. */
const BY_NAME = /^data\.FacilityName\.keyword:"((?:[^"\\]|\\.)*)"$/;

/** Answers with a JSON body. */
function send(response, status, body) {
  const text = JSON.stringify(body);
  response.writeHead(status, { "content-type": "application/json", "content-length": Buffer.byteLength(text) });
  response.end(text);
}

/** The request's body as bytes. */
function readBytes(request) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    request.on("data", (chunk) => chunks.push(chunk));
    request.on("end", () => resolve(Buffer.concat(chunks)));
    request.on("error", reject);
  });
}

/** The request's body as text. */
async function read(request) {
  return (await readBytes(request)).toString("utf8");
}

/** The records a search finds: the sample wellbore the query names, in the partition that asked, or none. */
function search(body, partition) {
  const match = BY_NAME.exec(typeof body.query === "string" ? body.query : "");
  const name = match === null ? null : match[1].replace(/\\(.)/g, "$1");
  const found = typeof partition === "string" && partition !== ""
    && typeof body.kind === "string" && WELLBORE_KIND.test(body.kind)
    && name !== null && WELLBORES.has(name);
  return found
    ? { results: [{ id: `${partition}:master-data--Wellbore:${wellboreId(name)}` }], totalCount: 1 }
    : { results: [], totalCount: 0 };
}

/** The records a spec asked the stand-in to hold, by id (its version set aside): every version held, oldest first. */
const held = new Map();

/** A record reference without its version: p:t:k: and p:t:k:123 read as p:t:k, as the control plane reads them. */
function withoutVersion(id) {
  const last = id.lastIndexOf(":");
  const colons = id.split(":").length - 1;
  return last >= 0 && colons >= 3 && /^\d*$/.test(id.slice(last + 1)) ? id.slice(0, last) : id;
}

/** The version every record the stand-in holds reads at: a storage version is a microsecond timestamp. */
const VERSION = 1727280000000000;

/** A wellbore of the platform as a record: what a read of one of the wellbores the search finds gives back. */
function wellboreRecord(id) {
  const [partition, type, unique] = id.split(":");
  const name = [...WELLBORES].find((candidate) => wellboreId(candidate) === unique);
  if (type !== "master-data--Wellbore" || name === undefined) {
    return null;
  }

  return {
    id,
    kind: "osdu:wks:master-data--Wellbore:1.1.0",
    version: VERSION,
    acl: { viewers: [`data.default.viewers@${partition}.dataservices.energy`], owners: [`data.default.owners@${partition}.dataservices.energy`] },
    legal: { legaltags: [`${partition}-reference-data-default`], otherRelevantDataCountries: ["US"], status: "compliant" },
    data: { FacilityName: name },
    createUser: "e2e-stand-in",
    createTime: "2026-09-25T00:00:00.000Z",
  };
}

/** The record a read of `id` finds at its latest: one a spec holds, or a wellbore of the platform; null when there is none. */
function readRecord(id) {
  const versions = recordVersions(id);
  return versions.length === 0 ? null : versions[versions.length - 1];
}

/** Every version of the record `id` names, oldest first: those a spec held, or the one a wellbore of the platform has. */
function recordVersions(id) {
  const key = withoutVersion(id);
  if (writes?.isRemoved(key)) {
    return [];
  }

  const own = held.get(key);
  if (own !== undefined) {
    return own;
  }

  const wellbore = wellboreRecord(key);
  return wellbore === null ? [] : [wellbore];
}

/** Every record of a partition the stand-in holds, at its latest: the platform's wellbores and what specs hold. */
function partitionRecords(partition) {
  const wellbores = [...WELLBORES].map((name) => wellboreRecord(`${partition}:master-data--Wellbore:${wellboreId(name)}`));
  const own = [...held.entries()]
    .filter(([key]) => !writes?.isRemoved(key))
    .map(([, versions]) => versions[versions.length - 1])
    .filter((record) => record.id.startsWith(`${partition}:`));
  return [...wellbores, ...own].sort((a, b) => a.id.localeCompare(b.id));
}

/** Whether `kind` is one a search's kind pattern names, each `*` standing for any run of characters. */
function kindMatches(pattern, kind) {
  const expression = String(pattern).split("*").map((part) => part.replace(/[.+?^${}()|[\]\\]/g, "\\$&")).join(".*");
  return new RegExp(`^${expression}$`).test(kind);
}

/** Every text a record holds, at any depth. */
function texts(value) {
  if (typeof value === "string") {
    return [value];
  }

  if (Array.isArray(value)) {
    return value.flatMap(texts);
  }

  return value !== null && typeof value === "object" ? Object.values(value).flatMap(texts) : [];
}

/** A Lucene term the explorer wrote with its reserved characters escaped, read back as the text it stands for. */
function unescape(term) {
  return term.replace(/\\(.)/g, "$1");
}

/**
 * Whether a record answers one clause of the queries the explorer and the dimension builder write: every record, a phrase
 * anywhere in it, an id exactly, an id that starts with a prefix or ends with a unique part, a property's whole value, the
 * words of a property of the record's data (any case), one of several whole values, the start of a property's whole
 * value, or a property that is there; and a clause of clauses joined by AND, OR and AND NOT. Enough of Lucene to answer
 * them, and nothing more: what it does not read matches nothing.
 */
function matches(record, query) {
  const text = query.trim();
  if (text === "*") {
    return true;
  }

  if (text.startsWith("(") && text.endsWith(")") && balanced(text.slice(1, -1))) {
    return matches(record, text.slice(1, -1));
  }

  for (const [joint, all] of [[" OR ", false], [" AND ", true]]) {
    const parts = splitTop(text, joint);
    if (parts.length > 1) {
      return all
        ? parts.every((part) => (part.startsWith("NOT ") ? !matches(record, part.slice(4)) : matches(record, part)))
        : parts.some((part) => matches(record, part));
    }
  }

  const phrase = /^"((?:[^"\\]|\\.)*)"$/.exec(text);
  if (phrase !== null) {
    const wanted = unescape(phrase[1]).toLowerCase();
    return texts(record).some((value) => value.toLowerCase().includes(wanted));
  }

  const exactId = /^id:"((?:[^"\\]|\\.)*)"$/.exec(text);
  if (exactId !== null) {
    return record.id === unescape(exactId[1]);
  }

  const idPattern = /^id:(.+)\*$/.exec(text);
  if (idPattern !== null) {
    const [head, tail] = idPattern[1].split("\\:*\\:");
    return tail === undefined
      ? record.id.startsWith(unescape(head))
      : record.id.startsWith(`${unescape(head)}:`) && record.id.split(":").slice(2).join(":").startsWith(unescape(tail));
  }

  const property = /^([\w.]+?)(\.keyword)?:"((?:[^"\\]|\\.)*)"$/.exec(text);
  if (property !== null) {
    const wanted = unescape(property[3]);
    const held = texts(valueAt(record, property[1]));
    // A property of the record's data asked by its words (no keyword sub-field named): the words anywhere in a value, any case.
    return property[2] === undefined && property[1].startsWith("data.")
      ? held.some((value) => value.toLowerCase().includes(wanted.toLowerCase()))
      : held.includes(wanted);
  }

  // One of several whole values, as a dimension's filter and the reads of the records a key names ask: id:("a" OR "b").
  const anyOf = /^([\w.]+?)(?:\.keyword)?:\((.+)\)$/.exec(text);
  if (anyOf !== null && balanced(anyOf[2])) {
    const wanted = splitTop(anyOf[2], " OR ").map((part) => /^"((?:[^"\\]|\\.)*)"$/.exec(part));
    if (wanted.some((phrase) => phrase === null)) {
      return false;
    }

    const held = anyOf[1] === "id" ? [record.id] : texts(valueAt(record, anyOf[1]));
    return wanted.some((phrase) => held.includes(unescape(phrase[1])));
  }

  // The start of a property's whole value, as a condition asks it: data.FacilityName.keyword:Wellbore\ B*.
  const start = /^([\w.]+?)(?:\.keyword)?:((?:\\.|[^\\\s*"():])+)\*$/.exec(text);
  if (start !== null) {
    return texts(valueAt(record, start[1])).some((value) => value.startsWith(unescape(start[2])));
  }

  // A property that is there, which is how every record is asked for alongside a NOT: _exists_:id.
  const exists = /^_exists_:([\w.]+)$/.exec(text);
  if (exists !== null) {
    return exists[1] === "id" ? record.id !== "" : texts(valueAt(record, exists[1])).length > 0;
  }

  // Whether the indexer failed to map a record, which here it never does: the assertion that asks finds none.
  if (/^index\.statusCode:/.test(text)) {
    return false;
  }

  // A clause on the items of a nested array, each read apart: nested(data.Curves, (Mnemonic.keyword:"GR")).
  const nested = /^nested\(\s*([\w.]+)\s*,\s*\((.+)\)\s*\)$/.exec(text);
  if (nested !== null && balanced(nested[2])) {
    return [valueAt(record, nested[1])].flat().some((item) => item !== null && typeof item === "object" && matches(item, nested[2]));
  }

  if (acceptsWrites) {
    console.warn(`The OSDU stand-in does not read the query clause: ${text}`);
  }

  return false;
}

/** Whether a query's parentheses and quotes balance, so a pair around it encloses the whole of it. */
function balanced(text) {
  let depth = 0;
  let quoted = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === "\\") {
      i++;
    } else if (c === '"') {
      quoted = !quoted;
    } else if (!quoted && c === "(") {
      depth++;
    } else if (!quoted && c === ")" && --depth < 0) {
      return false;
    }
  }

  return depth === 0 && !quoted;
}

/** A query split at `joint` where it stands outside quotes and parentheses. */
function splitTop(text, joint) {
  const parts = [];
  let depth = 0;
  let quoted = false;
  let start = 0;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === "\\") {
      i++;
    } else if (c === '"') {
      quoted = !quoted;
    } else if (!quoted && c === "(") {
      depth++;
    } else if (!quoted && c === ")") {
      depth--;
    } else if (!quoted && depth === 0 && text.startsWith(joint, i)) {
      parts.push(text.slice(start, i));
      start = i + joint.length;
      i += joint.length - 1;
    }
  }

  parts.push(text.slice(start));
  return parts.map((part) => part.trim());
}

/**
 * The value a record holds at a dotted path, for grouping (a `.keyword` sub-field read as its property). A path runs through an
 * array to every element's value, as the index flattens one: `data.Curves.Mnemonic` is the mnemonic of each curve, and
 * `nested(data.Curves, Mnemonic.keyword)` is how aggregateBy names the same values of a nested array.
 */
function valueAt(record, path) {
  const step = (node, key) => {
    if (Array.isArray(node)) {
      return node.flatMap((element) => step(element, key) ?? []);
    }

    return node !== null && typeof node === "object" ? node[key] : undefined;
  };

  return path.replace(/^nested\(\s*([\w.]+)\s*,\s*([\w.]+)\s*\)$/, "$1.$2").replace(/\.keyword$/, "").split(".").reduce(step, record);
}

/**
 * An explorer's search (POST /query with trackTotalCount): the records of the partition of the kinds asked that answer the
 * query, a page of them from the offset, the exact count, and the groups of the property asked for.
 */
function explore(body, partition) {
  const found = partitionRecords(partition)
    .filter((record) => kindMatches(body.kind ?? "*:*:*:*", record.kind))
    .filter((record) => typeof body.query !== "string" || body.query.trim() === "" || matches(record, body.query));
  const offset = Number(body.offset ?? 0);
  const limit = Number(body.limit ?? 10);
  const answer = { results: found.slice(offset, offset + limit), totalCount: found.length };
  if (typeof body.aggregateBy === "string") {
    const groups = new Map();
    for (const record of found) {
      for (const value of texts(valueAt(record, body.aggregateBy))) {
        groups.set(value, (groups.get(value) ?? 0) + 1);
      }
    }

    // Commonest first, and by value among groups as common, as the service's terms aggregation orders them.
    answer.aggregations = [...groups.entries()]
      .sort(([a, x], [b, y]) => y - x || (a < b ? -1 : a > b ? 1 : 0))
      .map(([key, count]) => ({ key, count }));
  }

  return answer;
}

/** A record read: storage's (GET /api/storage/v2/records/{id}) or a wellbore DDMS collection's (GET .../ddms/v3/{collection}/{id}). */
const RECORD_READ = /^\/api\/(?:storage\/v2\/records|os-wellbore-ddms\/ddms\/v3\/[A-Za-z]+)\/([^/]+)$/;
/** The version list beside a record (GET /api/storage/v2/records/versions/{id}): every version a spec held of it, oldest first. */
const RECORD_VERSIONS = /^\/api\/storage\/v2\/records\/versions\/([^/]+)$/;
/** A record at one version (GET /api/storage/v2/records/{id}/{version}); only a version held answers. */
const RECORD_AT_VERSION = /^\/api\/storage\/v2\/records\/([^/]+)\/(\d+)$/;

/** A schema read from the Schema service by its id (GET /api/schema-service/v1/schema/{id}). */
const SCHEMA_READ = /^\/api\/schema-service\/v1\/schema\/([^/]+)$/;

/** The Schema service's listing of the schemas it holds (GET /api/schema-service/v1/schema). */
const SCHEMA_LIST = "/api/schema-service/v1/schema";

/** The schemas a spec asked the Schema service to hold, by id. */
const schemas = new Map();

/** The status and scope each held schema is listed in, by id: published and shared unless the spec said otherwise. */
const schemaInfos = new Map();

/**
 * One page of the Schema service's listing (openapi schema_service, getSchemaInfoList): the schemas held in the status
 * and scope asked (published and internal when the listing names neither, as the specification defaults them), in id
 * order, a page of `limit` from `offset`.
 */
function listSchemas(query) {
  const status = (query.get("status") ?? "PUBLISHED").toUpperCase();
  const scope = (query.get("scope") ?? "INTERNAL").toUpperCase();
  const offset = Number(query.get("offset") ?? 0);
  const limit = Number(query.get("limit") ?? 100);
  const listed = [...schemas.keys()]
    .filter((id) => {
      const info = schemaInfos.get(id) ?? { status: "PUBLISHED", scope: "SHARED" };
      return info.status === status && info.scope === scope;
    })
    .sort();
  const page = listed.slice(offset, offset + limit);
  return {
    schemaInfos: page.map((id) => {
      const [authority, source, entityType, version] = id.split(":");
      const [major, minor, patch] = (version ?? "0.0.0").split(".").map(Number);
      return {
        schemaIdentity: { authority, source, entityType, schemaVersionMajor: major, schemaVersionMinor: minor, schemaVersionPatch: patch, id },
        createdBy: "e2e-stand-in",
        dateCreated: "2026-07-17T00:00:00Z",
        status,
        scope,
      };
    }),
    offset,
    count: page.length,
    totalCount: listed.length,
  };
}

/** Where the control plane reads the OSDU data definitions here: the GitLab API of the repository, as its paths go. */
const DATA_DEFINITIONS = "/__e2e/data-definitions/";

/**
 * The one release the stand-in's data definitions have. Its commit is new each time the stand-in starts, so a control
 * plane's local copy never holds an example an earlier run held.
 */
const RELEASE = {
  name: "v0.30.0",
  commit: [...crypto.getRandomValues(new Uint8Array(20))].map((b) => b.toString(16).padStart(2, "0")).join(""),
};

/** The example records a spec asked the data definitions to publish, by their path (Examples/<group>/<entity>.<version>.json). */
const examples = new Map();

/**
 * Storage's batch read (POST /api/storage/v2/query/records): the records it holds among the ids asked, at their latest,
 * and the ids it holds nothing under named under invalidRecords, as storage answers.
 */
function readMany(body) {
  const ids = Array.isArray(body.records) ? body.records.filter((id) => typeof id === "string") : [];
  const records = [];
  const invalidRecords = [];
  for (const id of ids) {
    const record = readRecord(id);
    if (record === null) {
      invalidRecords.push(id);
    } else {
      records.push(record);
    }
  }

  return { records, invalidRecords, retryRecords: [] };
}

/** The id a storage path names, decoded; null when it cannot be. */
function decodeId(encoded) {
  try {
    return decodeURIComponent(encoded);
  } catch {
    return null;
  }
}

/** What the stand-in adds when it takes writes; never loaded for the e2e suite. */
const writes = acceptsWrites
  ? (await import("./osdu-writes.mjs")).createWrites(
    { held, withoutVersion, partitionRecords, explore, send, read, readBytes },
    process.env.OSDU_STANDIN_STATE ?? "",
    process.env.SQLFLOW_NODE_CACHE ?? "")
  : null;

const server = createServer((request, response) => {
  const url = new URL(request.url ?? "/", "http://stand-in");
  const path = url.pathname;
  if (request.method === "GET" && path === "/health") {
    send(response, 200, { status: "up" });
    return;
  }

  try {
    if (writes?.handle(request, response, path, url)) {
      return;
    }
  } catch (error) {
    send(response, 400, { message: `The OSDU stand-in could not read ${request.method} ${path}: ${error instanceof Error ? error.message : String(error)}` });
    return;
  }

  if (path === "/__e2e/records") {
    if (request.method === "DELETE") {
      held.clear();
      send(response, 200, { held: 0 });
      return;
    }

    if (request.method === "PUT") {
      read(request)
        .then((text) => {
          const record = JSON.parse(text);
          if (record === null || typeof record !== "object" || typeof record.id !== "string" || record.id === "") {
            send(response, 400, { message: "A record to hold is a JSON object with an id." });
            return;
          }

          // Holding a record again keeps a version of it, numbered after the last, as storage numbers a record written again.
          const key = withoutVersion(record.id);
          const versions = held.get(key) ?? [];
          const version = record.version ?? (versions.length === 0 ? VERSION : versions[versions.length - 1].version + 1000);
          held.set(key, [...versions, { ...record, version }]);
          send(response, 200, { id: record.id, version, held: held.size });
        })
        .catch((error) => send(response, 400, { message: `The OSDU stand-in could not read the record: ${error instanceof Error ? error.message : String(error)}` }));
      return;
    }
  }

  if (path === "/__e2e/schemas") {
    if (request.method === "DELETE") {
      schemas.clear();
      schemaInfos.clear();
      send(response, 200, { held: 0 });
      return;
    }

    if (request.method === "PUT") {
      read(request)
        .then((text) => {
          const body = JSON.parse(text);
          if (body === null || typeof body !== "object" || typeof body.id !== "string" || body.id === ""
            || body.schema === null || typeof body.schema !== "object" || Array.isArray(body.schema)) {
            send(response, 400, { message: "A schema to hold is a JSON object with an id and the schema as an object." });
            return;
          }

          if (!/^[\w.-]+:[\w.-]+:[\w.-]+:\d+\.\d+\.\d+$/.test(body.id)) {
            send(response, 400, { message: `A schema's id is authority:source:entityType:major.minor.patch, not ${body.id}.` });
            return;
          }

          schemas.set(body.id, body.schema);
          schemaInfos.set(body.id, {
            status: typeof body.status === "string" ? body.status.toUpperCase() : "PUBLISHED",
            scope: typeof body.scope === "string" ? body.scope.toUpperCase() : "SHARED",
          });
          send(response, 200, { id: body.id, held: schemas.size });
        })
        .catch((error) => send(response, 400, { message: `The OSDU stand-in could not read the schema: ${error instanceof Error ? error.message : String(error)}` }));
      return;
    }
  }

  if (path === "/__e2e/examples") {
    if (request.method === "DELETE") {
      examples.clear();
      send(response, 200, { held: 0 });
      return;
    }

    if (request.method === "PUT") {
      read(request)
        .then((text) => {
          const body = JSON.parse(text);
          if (body === null || typeof body !== "object" || typeof body.path !== "string" || !body.path.startsWith("Examples/")
            || body.record === null || typeof body.record !== "object" || Array.isArray(body.record)) {
            send(response, 400, { message: "An example to publish is a JSON object with its path under Examples/ and the record as an object." });
            return;
          }

          examples.set(body.path, body.record);
          send(response, 200, { path: body.path, held: examples.size });
        })
        .catch((error) => send(response, 400, { message: `The OSDU stand-in could not read the example: ${error instanceof Error ? error.message : String(error)}` }));
      return;
    }
  }

  if (request.method === "GET" && path.startsWith(DATA_DEFINITIONS)) {
    const asked = path.slice(DATA_DEFINITIONS.length);
    if (asked === "repository/tags") {
      // One page: no X-Next-Page, so the list ends here.
      send(response, 200, [{ name: RELEASE.name, commit: { id: RELEASE.commit, committed_date: "2026-07-17T14:55:57.000+08:00" } }]);
      return;
    }

    const file = /^repository\/files\/([^/]+)\/raw$/.exec(asked);
    const ref = new URL(request.url ?? "/", "http://stand-in").searchParams.get("ref");
    const example = file === null || ref !== RELEASE.commit ? undefined : examples.get(decodeId(file[1]) ?? "");
    if (example !== undefined) {
      send(response, 200, example);
    } else {
      send(response, 404, { message: "404 File Not Found" });
    }

    return;
  }

  if (request.method === "GET" && path === SCHEMA_LIST) {
    writes?.holdPublishedSchemas(schemas);
    send(response, 200, listSchemas(new URL(request.url ?? "/", "http://stand-in").searchParams));
    return;
  }

  const schemaRead = request.method === "GET" ? SCHEMA_READ.exec(path) : null;
  if (schemaRead !== null) {
    writes?.holdPublishedSchemas(schemas);
    const id = decodeId(schemaRead[1]);
    const schema = id === null ? undefined : schemas.get(id);
    if (schema === undefined) {
      send(response, 404, { code: 404, reason: "Schema not found", message: `The OSDU stand-in holds no schema ${id ?? path}.` });
    } else {
      send(response, 200, schema);
    }

    return;
  }

  if (request.method === "POST" && path === "/api/storage/v2/query/records") {
    read(request)
      .then((text) => send(response, 200, readMany(JSON.parse(text))))
      .catch((error) => send(response, 400, { message: `The OSDU stand-in could not read the batch: ${error instanceof Error ? error.message : String(error)}` }));
    return;
  }

  const versions = request.method === "GET" ? RECORD_VERSIONS.exec(path) : null;
  if (versions !== null) {
    const id = decodeId(versions[1]);
    const kept = id === null ? [] : recordVersions(id);
    if (kept.length === 0) {
      send(response, 404, { code: 404, reason: "Record not found", message: `The OSDU stand-in holds no record ${id ?? path}.` });
    } else {
      send(response, 200, { recordId: withoutVersion(id), versions: kept.map((record) => record.version) });
    }

    return;
  }

  const atVersion = request.method === "GET" ? RECORD_AT_VERSION.exec(path) : null;
  if (atVersion !== null) {
    const id = decodeId(atVersion[1]);
    const record = id === null ? null : recordVersions(id).find((kept) => String(kept.version) === atVersion[2]) ?? null;
    if (record === null) {
      send(response, 404, { code: 404, reason: "Record version not found", message: `The OSDU stand-in holds no version ${atVersion[2]} of ${id ?? path}.` });
    } else {
      send(response, 200, record);
    }

    return;
  }

  const reading = request.method === "GET" ? RECORD_READ.exec(path) : null;
  if (reading !== null) {
    let id;
    try {
      id = decodeURIComponent(reading[1]);
    } catch {
      send(response, 400, { message: `The OSDU stand-in cannot read the id in ${path}.` });
      return;
    }

    const record = readRecord(id);
    if (record === null) {
      send(response, 404, { code: 404, reason: "Record not found", message: `The OSDU stand-in holds no record ${id}.` });
    } else {
      send(response, 200, record);
    }

    return;
  }

  if (request.method === "POST" && path === "/token") {
    // The flows authenticate with client credentials; any will do here, since nothing here holds anything to protect.
    send(response, 200, { access_token: "e2e-stand-in", token_type: "Bearer", expires_in: 3600 });
    return;
  }

  if (request.method === "POST" && path === "/api/search/v2/query") {
    read(request)
      .then((text) => {
        const body = JSON.parse(text);
        const partition = request.headers["data-partition-id"];
        // The explorer asks for the exact count; a render's lookup asks for a handful of ids by a wellbore's name, which
        // on a platform that holds what was delivered is a search like any other.
        const explores = (acceptsWrites || body.trackTotalCount === true) && typeof partition === "string" && partition !== "";
        send(response, 200, explores ? explore(body, partition) : search(body, partition));
      })
      .catch((error) => send(response, 400, { message: `The OSDU stand-in could not read the search: ${error instanceof Error ? error.message : String(error)}` }));
    return;
  }

  if (acceptsWrites) {
    console.warn(`The OSDU stand-in answered 404 to ${request.method} ${path}.`);
  }

  send(response, 404, { message: `The OSDU stand-in answers a token, the search service, record reads and the schemas it holds, not ${request.method} ${path}.` });
});

server.on("error", (error) => {
  console.error(error.code === "EADDRINUSE"
    ? `Port ${port} is taken, so the OSDU stand-in cannot start. Stop what holds it, then start again.`
    : `The OSDU stand-in could not start: ${error.message}`);
  process.exit(1);
});

server.listen(port, "127.0.0.1", () => {
  console.log(`OSDU stand-in ${acceptsWrites ? "taking writes" : "for the e2e suite"} listening on http://127.0.0.1:${port}`);
});
