// What the OSDU stand-in adds when it takes writes (OSDU_STANDIN_WRITES=true), which makes it a small local OSDU a delivery can
// reach: Storage's writes, deletes and restores, a Wellbore DDMS with its bulk data and sessions, Legal's checks, and a cursor
// over the search it already answers. Each answers as the service's contract does (osdu/specs), in as much as the delivery
// routes, the assertions and the explorer read of it. Nothing here is a platform: it holds no entitlements and enforces no
// legal tag, and its point is that a local environment can show a delivery end to end without reaching a real OSDU.
//
// The e2e suite never loads this file: there a write answers 404, so a spec that tried to send a record fails loudly.
import { randomUUID } from "node:crypto";
import { existsSync, readdirSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { parquetMetadata, parquetReadObjects } from "hyparquet";

/** A Storage path naming a record, with what follows the id. */
const STORAGE_RECORD = /^\/api\/storage\/v2\/records\/([^/]+?)(:delete|\/versions)?$/;

/** A DDMS collection, a record of it, its bulk data, its sessions and one session's chunks. */
const DDMS = /^\/api\/os-wellbore-ddms\/ddms\/v3\/([A-Za-z]+)(?:\/([^/]+)(?:\/(data|sessions)(?:\/([^/]+)(?:\/(data))?)?)?)?$/;

const LEGAL_TAG = /^\/api\/legal\/v1\/legaltags\/([^/:]+)$/;

/** A service's info endpoint (GET /api/storage/v2/info), which says it is there. */
const SERVICE_INFO = /^\/api\/[\w-]+(?:\/v\d+)?\/info$/;

/** A cursor a reader has finished with, closed (DELETE /api/search/v2/query_with_cursor/{cursor}). */
const CURSOR = /^\/api\/search\/v2\/query_with_cursor\/([^/]+)$/;

/** How many records a cursor keeps ready between its pages at most, as a guard against one that is never read to its end. */
const MOST_CURSORS = 200;

/** A published schema as an estate keeps it, in a template folder: templates/osdu_wks_<entity type>_<version>.json. */
const PUBLISHED_TEMPLATE = /(?:^|[\\/])templates[\\/]osdu_wks_(.+)_(\d+\.\d+\.\d+)\.json$/;

/** How long a look through the checkouts stands, since a schema is read for each kind of each page. */
const SCHEMAS_FRESH_MS = 5000;

/**
 * The write side over the records `store` holds: { held, withoutVersion, partitionRecords, explore, send, read, readBytes }.
 * Answers a request it takes and returns true; returns false for a request that is not one of its own. `checkouts` is where the
 * estates are checked out, whose template folders hold the published schemas a platform's Schema service would.
 */
export function createWrites(store, stateFile, checkouts = "") {
  const { held, withoutVersion, partitionRecords, explore, send, read, readBytes } = store;
  const removed = new Set();
  const bulk = new Map();
  const sessions = new Map();
  const cursors = new Map();
  let lastVersion = 0;
  let saving = null;
  let schemasRead = 0;

  /** A storage version: a microsecond timestamp, later than every one before it. */
  function nextVersion() {
    lastVersion = Math.max(Date.now() * 1000, lastVersion + 1);
    return lastVersion;
  }

  /** What a restart keeps: every record's versions, what is deleted, and the bulk data. */
  function saveSoon() {
    if (stateFile === "" || saving !== null) {
      return;
    }

    saving = setTimeout(() => {
      saving = null;
      const temporary = `${stateFile}.tmp`;
      writeFileSync(temporary, JSON.stringify({ records: [...held], removed: [...removed], bulk: [...bulk], lastVersion }));
      renameSync(temporary, stateFile);
    }, 500);
  }

  function loadState() {
    if (stateFile === "" || !existsSync(stateFile)) {
      return;
    }

    let state;
    try {
      state = JSON.parse(readFileSync(stateFile, "utf8"));
    } catch (error) {
      // A file cut short by a crash: what it held is set aside, and the stand-in starts empty rather than not at all.
      renameSync(stateFile, `${stateFile}.unreadable`);
      console.warn(`The OSDU stand-in could not read ${stateFile} (${error instanceof Error ? error.message : String(error)}), so it starts empty; the file is kept as ${stateFile}.unreadable.`);
      return;
    }

    for (const [key, versions] of state.records) {
      held.set(key, versions);
    }

    for (const key of state.removed) {
      removed.add(key);
    }

    for (const [key, frame] of state.bulk) {
      bulk.set(key, frame);
    }

    lastVersion = state.lastVersion;
  }

  /** A value's JSON with its keys in order, so two values compare by what they hold. */
  function canonical(value) {
    if (Array.isArray(value)) {
      return `[${value.map(canonical).join(",")}]`;
    }

    if (value !== null && typeof value === "object") {
      return `{${Object.keys(value).sort().map((key) => `${JSON.stringify(key)}:${canonical(value[key])}`).join(",")}}`;
    }

    return JSON.stringify(value) ?? "null";
  }

  /** What storage compares for skipdupes (osdu/specs/core/INTEGRATION.md section 2.2): not the kind, and not its own fields. */
  function comparable(record) {
    const { data, meta, acl, legal, tags, ancestry } = record;
    const { legaltags, otherRelevantDataCountries } = legal ?? {};
    return canonical({ data, meta: meta ?? [], acl, legaltags, otherRelevantDataCountries, tags: tags ?? {}, ancestry: ancestry ?? {} });
  }

  /** Why storage would refuse the record, or null. */
  function refusal(record, partition) {
    if (record === null || typeof record !== "object" || Array.isArray(record)) {
      return "A record is a JSON object.";
    }

    if (!/^[\w.-]+:[\w.-]+:[\w.-]+:\d+(\.\d+\.\d+)?$/.test(String(record.kind))) {
      return `The kind '${record.kind}' is not authority:source:type:version.`;
    }

    if (typeof record.id === "string" && !record.id.startsWith(`${partition}:`)) {
      return `The record id '${record.id}' does not start with the partition '${partition}'.`;
    }

    if (!Array.isArray(record.acl?.viewers) || !Array.isArray(record.acl?.owners) || record.acl.viewers.length === 0 || record.acl.owners.length === 0) {
      return `The record ${record.id ?? record.kind} carries no acl.`;
    }

    return Array.isArray(record.legal?.legaltags) && record.legal.legaltags.length > 0 ? null : `The record ${record.id ?? record.kind} carries no legal tag.`;
  }

  /** Writes a version of the record, minting an id for one that names none, and brings a deleted one back. */
  function write(record, partition) {
    const type = String(record.kind).split(":")[2];
    const id = typeof record.id === "string" && record.id !== "" ? record.id : `${partition}:${type}:${randomUUID()}`;
    const key = withoutVersion(id);
    const versions = held.get(key) ?? [];
    const now = new Date().toISOString();
    const version = nextVersion();
    const first = versions[0];
    const stored = {
      ...record,
      id: key,
      version,
      legal: { ...record.legal, status: "compliant" },
      createUser: first?.createUser ?? "local-stand-in",
      createTime: first?.createTime ?? now,
      ...(first === undefined ? {} : { modifyUser: "local-stand-in", modifyTime: now }),
    };
    held.set(key, [...versions, stored]);
    removed.delete(key);
    saveSoon();
    return { id: key, version };
  }

  /** PUT /records: every record written, or skipped when it changes nothing and the caller asked for that. */
  function putRecords(records, partition, skipDuplicates) {
    const recordIds = [];
    const recordIdVersions = [];
    const skippedRecordIds = [];
    for (const record of records) {
      const latest = held.get(withoutVersion(String(record.id ?? "")));
      if (skipDuplicates && latest !== undefined && comparable(latest[latest.length - 1]) === comparable(record)) {
        skippedRecordIds.push(latest[latest.length - 1].id);
        continue;
      }

      const { id, version } = write(record, partition);
      recordIds.push(id);
      recordIdVersions.push(`${id}:${version}`);
    }

    return { recordCount: recordIds.length + skippedRecordIds.length, recordIds, recordIdVersions, skippedRecordIds };
  }

  function latestOf(key) {
    const versions = held.get(key);
    return versions === undefined || removed.has(key) ? null : versions[versions.length - 1];
  }

  function bulkLink(record) {
    return record?.data?.ExtensionProperties?.wdms?.bulkURI;
  }

  /** A value as JSON writes it: a parquet int64 arrives as a BigInt and a timestamp as a Date. */
  function plain(value) {
    return typeof value === "bigint" ? Number(value) : value instanceof Date ? value.toISOString() : value;
  }

  /** Parquet or JSON bulk data as a frame of named columns and rows. */
  async function frameOf(bytes, contentType) {
    if (/json/i.test(contentType)) {
      const body = JSON.parse(bytes.toString("utf8"));
      const columns = Array.isArray(body.columns) ? body.columns : Object.keys(body[0] ?? {});
      const rows = Array.isArray(body.data)
        ? body.data.map((row) => Object.fromEntries(columns.map((name, i) => [name, row[i] ?? null])))
        : body;
      return { columns, rows };
    }

    const file = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
    const columns = parquetMetadata(file).schema.slice(1).filter((element) => element.num_children === undefined).map((element) => element.name);
    const read = await parquetReadObjects({ file });
    return { columns, rows: read.map((row) => Object.fromEntries(columns.map((name) => [name, plain(row[name]) ?? null]))) };
  }

  /** Bulk data taking effect: the record gets a new version whose link names it, as the DDMS writes one. */
  function linkBulk(key, frame) {
    bulk.set(key, frame);
    const record = latestOf(key);
    const properties = record.data.ExtensionProperties ?? {};
    const linked = {
      ...record,
      data: { ...record.data, ExtensionProperties: { ...properties, wdms: { ...properties.wdms, bulkURI: `urn:wdms-1:uuid:${randomUUID()}` } } },
    };
    write(linked, key.split(":")[0]);
  }

  /** GET .../data: its description with describe=true, or its rows in the split orientation. */
  function bulkAnswer(key, query) {
    const frame = bulk.get(key);
    if (frame === undefined) {
      return null;
    }

    if (query.get("describe") === "true") {
      return { numberOfRows: frame.rows.length, columns: frame.columns };
    }

    const columns = query.get("curves")?.split(",") ?? frame.columns;
    const offset = Number(query.get("offset") ?? 0);
    const limit = query.has("limit") ? Number(query.get("limit")) : frame.rows.length;
    const page = frame.rows.slice(offset, offset + limit);
    return { columns, index: page.map((_, i) => offset + i), data: page.map((row) => columns.map((name) => row[name] ?? null)) };
  }

  /** A session's chunks committed: their rows in the order they came, the columns of them all. */
  function merged(chunks) {
    return { columns: [...new Set(chunks.flatMap((chunk) => chunk.columns))], rows: chunks.flatMap((chunk) => chunk.rows) };
  }

  /** The search behind the cursor: the whole answer at once, then a page at a time. */
  function cursorPage(body, partition) {
    if (typeof body.cursor === "string" && body.cursor !== "") {
      const pending = cursors.get(body.cursor);
      if (pending === undefined) {
        return null;
      }

      const results = pending.records.splice(0, pending.limit);
      if (results.length === 0) {
        cursors.delete(body.cursor);
      }

      return { cursor: results.length === 0 ? null : body.cursor, results, totalCount: pending.total };
    }

    const limit = Number(body.limit ?? 1000);
    const everything = explore({ ...body, offset: 0, limit: Number.MAX_SAFE_INTEGER }, partition);
    const results = everything.results.slice(0, limit);
    const rest = everything.results.slice(limit);
    const cursor = randomUUID();
    if (cursors.size >= MOST_CURSORS) {
      cursors.delete(cursors.keys().next().value);
    }

    cursors.set(cursor, { records: rest, limit, total: everything.totalCount });
    return { cursor, results, totalCount: everything.totalCount };
  }

  function json(text) {
    return JSON.parse(text);
  }

  /** Answers a write route's work, and its failure as the 400 it is. */
  function answering(response, work) {
    work().catch((error) => send(response, 400, { message: `The OSDU stand-in could not take the request: ${error instanceof Error ? error.message : String(error)}` }));
  }

  /** The DDMS's routes: records through a collection, bulk data at once or through a session. */
  function ddms(request, response, url, match) {
    const [, , encodedId, part, encodedSession, sessionPart] = match;
    const id = encodedId === undefined ? undefined : decodeURIComponent(encodedId);
    const key = id === undefined ? undefined : withoutVersion(id);
    const partition = String(request.headers["data-partition-id"] ?? "");
    const method = request.method;

    if (id === undefined && method === "POST") {
      answering(response, async () => {
        const records = json(await read(request));
        for (const record of records) {
          const problem = refusal(record, partition);
          const latest = latestOf(withoutVersion(String(record.id ?? "")));
          if (problem !== null) {
            send(response, 400, { message: problem });
            return;
          }

          if (bulkLink(latest) !== bulkLink(record)) {
            send(response, 400, { message: `The bulk link of ${record.id} differs from the latest version's.` });
            return;
          }
        }

        send(response, 200, putRecords(records, partition, false));
      });
      return true;
    }

    if (part === undefined && method === "DELETE") {
      if (latestOf(key) === null) {
        send(response, 404, { message: `The OSDU stand-in holds no record ${id}.` });
      } else {
        removed.add(key);
        saveSoon();
        response.writeHead(204).end();
      }

      return true;
    }

    if (part === "data" && method === "POST") {
      if (latestOf(key) === null) {
        send(response, 404, { message: `The OSDU stand-in holds no record ${id}.` });
        return true;
      }

      answering(response, async () => {
        linkBulk(key, await frameOf(await readBytes(request), String(request.headers["content-type"] ?? "")));
        send(response, 200, {});
      });
      return true;
    }

    if (part === "data" && method === "GET") {
      const answer = bulkAnswer(key, url.searchParams);
      if (answer === null) {
        send(response, 404, { message: `The OSDU stand-in holds no bulk data for ${id}.` });
      } else {
        send(response, 200, answer);
      }

      return true;
    }

    if (part !== "sessions") {
      return false;
    }

    if (encodedSession === undefined && method === "POST") {
      answering(response, async () => {
        const options = json(await read(request));
        const session = { id: randomUUID(), key, state: "open", mode: options.mode ?? "overwrite", fromVersion: options.fromVersion ?? 0, chunks: [] };
        sessions.set(session.id, session);
        send(response, 200, { id: session.id, state: session.state, mode: session.mode, fromVersion: session.fromVersion });
      });
      return true;
    }

    if (encodedSession === undefined && method === "GET") {
      send(response, 200, [...sessions.values()].filter((s) => s.key === key).map(({ id: sessionId, state }) => ({ id: sessionId, state })));
      return true;
    }

    const session = encodedSession === undefined ? undefined : sessions.get(decodeURIComponent(encodedSession));
    if (session === undefined || session.key !== key) {
      send(response, 404, { message: `The OSDU stand-in holds no session ${encodedSession} of ${id}.` });
      return true;
    }

    if (sessionPart === "data" && method === "POST") {
      answering(response, async () => {
        session.chunks.push(await frameOf(await readBytes(request), String(request.headers["content-type"] ?? "")));
        send(response, 200, {});
      });
      return true;
    }

    if (sessionPart === undefined && method === "GET") {
      send(response, 200, { id: session.id, state: session.state });
      return true;
    }

    if (sessionPart === undefined && method === "PATCH") {
      answering(response, async () => {
        const { state } = json(await read(request));
        if (session.state !== "open") {
          send(response, 409, { message: `Session ${session.id} is ${session.state}.` });
          return;
        }

        if (state === "commit") {
          linkBulk(key, merged(session.chunks));
          session.state = "committed";
        } else {
          session.state = "abandoned";
        }

        session.chunks = [];
        send(response, 200, { id: session.id, state: session.state });
      });
      return true;
    }

    return false;
  }

  /** Storage's writes: PUT /records, the two soft deletes, a restore and a purge of earlier versions. */
  function storage(request, response, path, url) {
    const partition = String(request.headers["data-partition-id"] ?? "");
    if (path === "/api/storage/v2/records" && request.method === "PUT") {
      answering(response, async () => {
        const records = json(await read(request));
        const problem = records.map((record) => refusal(record, partition)).find((found) => found !== null);
        if (problem !== undefined) {
          send(response, 400, { message: problem });
          return;
        }

        send(response, 201, putRecords(records, partition, url.searchParams.get("skipdupes") === "true"));
      });
      return true;
    }

    if (path === "/api/storage/v2/records/delete" && request.method === "POST") {
      answering(response, async () => {
        for (const id of json(await read(request))) {
          removed.add(withoutVersion(id));
        }

        saveSoon();
        response.writeHead(204).end();
      });
      return true;
    }

    const record = STORAGE_RECORD.exec(path);
    if (record === null) {
      return false;
    }

    const id = decodeURIComponent(record[1]);
    const key = withoutVersion(id);
    if (record[2] === ":delete" && request.method === "POST") {
      if (latestOf(key) === null) {
        send(response, 404, { message: `The OSDU stand-in holds no record ${id}.` });
      } else {
        removed.add(key);
        saveSoon();
        response.writeHead(204).end();
      }

      return true;
    }

    if (record[2] === "/versions" && request.method === "DELETE") {
      const versions = held.get(key);
      if (versions !== undefined) {
        held.set(key, versions.slice(-1));
        saveSoon();
      }

      response.writeHead(204).end();
      return true;
    }

    if (record[2] === undefined && request.method === "PATCH") {
      if (!held.has(key)) {
        send(response, 404, { message: "Record not found" });
      } else if (!removed.delete(key)) {
        send(response, 400, { message: "Record State already updated" });
      } else {
        saveSoon();
        send(response, 200, { recordCount: 1, recordIds: [id] });
      }

      return true;
    }

    return false;
  }

  loadState();

  return {
    /** Whether the record `key` names is soft-deleted, which a read and a search answer as if it were not there. */
    isRemoved: (key) => removed.has(key),

    /** Answers a request of the write side and returns true; false when it is not one of them. */
    handle(request, response, path, url) {
      if (request.method === "GET" && SERVICE_INFO.test(path)) {
        send(response, 200, { version: "local-stand-in", artifactId: path.split("/")[2] });
        return true;
      }

      if (request.method === "GET" && path === "/api/os-wellbore-ddms/about") {
        send(response, 200, { service: "Wellbore DDMS", version: "local-stand-in" });
        return true;
      }

      const collection = DDMS.exec(path);
      if (collection !== null && ddms(request, response, url, collection)) {
        return true;
      }

      if (storage(request, response, path, url)) {
        return true;
      }

      if (request.method === "POST" && path === "/api/legal/v1/legaltags:validate") {
        answering(response, async () => {
          await read(request);
          send(response, 200, { invalidLegalTags: [] });
        });
        return true;
      }

      const legalTag = request.method === "GET" ? LEGAL_TAG.exec(path) : null;
      if (legalTag !== null) {
        send(response, 200, { name: decodeURIComponent(legalTag[1]), description: "Every tag is valid here", properties: { countryOfOrigin: ["US"], dataType: "Public Domain Data" } });
        return true;
      }

      const closing = request.method === "DELETE" ? CURSOR.exec(path) : null;
      if (closing !== null) {
        cursors.delete(decodeURIComponent(closing[1]));
        response.writeHead(204).end();
        return true;
      }

      if (request.method === "POST" && path === "/api/search/v2/query_with_cursor") {
        const partition = String(request.headers["data-partition-id"] ?? "");
        answering(response, async () => {
          const answer = cursorPage(json(await read(request)), partition);
          if (answer === null) {
            send(response, 400, { message: "Can't find the given cursor." });
          } else {
            send(response, 200, answer);
          }
        });
        return true;
      }

      return false;
    },

    /** How many records a partition holds, for the line a start prints. */
    count: (partition) => partitionRecords(partition).length,

    /**
     * Holds in `schemas` every published schema the checkouts keep, as a platform holds the standard kinds. A kind of an estate's
     * own stays out of it: nobody registered that here, so it reads as unregistered, as it would on a platform.
     */
    holdPublishedSchemas(schemas) {
      if (checkouts === "" || !existsSync(checkouts) || Date.now() - schemasRead < SCHEMAS_FRESH_MS) {
        return;
      }

      schemasRead = Date.now();
      for (const file of readdirSync(checkouts, { recursive: true, encoding: "utf8" })) {
        const template = PUBLISHED_TEMPLATE.exec(file);
        const id = template === null ? null : `osdu:wks:${template[1]}:${template[2]}`;
        if (id !== null && !schemas.has(id)) {
          try {
            schemas.set(id, JSON.parse(readFileSync(join(checkouts, file), "utf8")));
          } catch (error) {
            console.warn(`The OSDU stand-in skipped the schema ${file}: ${error instanceof Error ? error.message : error}`);
          }
        }
      }
    },
  };
}
