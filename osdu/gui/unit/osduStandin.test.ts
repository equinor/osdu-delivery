// The OSDU stand-in (standin/osdu-standin.mjs), as a local environment and the e2e suite each start it: a real process over
// real HTTP, because what matters is what a delivery meets. Taking writes, it answers as the services' contracts do; started
// for the e2e suite, a write is a 404, so a spec that sent a record would fail loudly.

import assert from "node:assert/strict";
import { type ChildProcess, spawn } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { after, before, describe, it } from "node:test";

const gui = join(import.meta.dirname, "..");
const samples = join(gui, "..", "samples", "welldb", "data", "curves", "PROJECT_A");
const chunk = readFileSync(join(samples, "LOG-0005_1", "chunk_00000.parquet"));

interface StandIn {
  process: ChildProcess;
  url: string;
  log: () => string;
}

/** Starts the stand-in and waits for it to listen. */
async function start(port: number, env: Record<string, string> = {}): Promise<StandIn> {
  const child = spawn(process.execPath, ["standin/osdu-standin.mjs"], {
    cwd: gui,
    env: { ...process.env, SQLFLOW_E2E_OSDU_PORT: String(port), ...env },
    stdio: ["ignore", "pipe", "pipe"],
  });
  let log = "";
  child.stdout.on("data", (text: Buffer) => (log += text.toString()));
  child.stderr.on("data", (text: Buffer) => (log += text.toString()));
  const url = `http://127.0.0.1:${port}`;
  for (let attempt = 0; attempt < 100; attempt++) {
    try {
      if ((await fetch(`${url}/health`)).ok) {
        return { process: child, url, log: () => log };
      }
    } catch {
      await new Promise((resolve) => setTimeout(resolve, 50));
    }
  }

  child.kill();
  throw new Error(`The stand-in did not come up: ${log}`);
}

function stop(standIn: StandIn): Promise<void> {
  return new Promise((resolve) => {
    standIn.process.once("exit", () => resolve());
    standIn.process.kill();
  });
}

const headers = { "content-type": "application/json", "data-partition-id": "dev" };

function record(id: string, name = "first", extra: Record<string, unknown> = {}) {
  return {
    id,
    kind: "osdu:wks:master-data--Well:1.2.0",
    acl: { owners: ["o@dev.dataservices.energy"], viewers: ["v@dev.dataservices.energy"] },
    legal: { legaltags: ["dev-sample"], otherRelevantDataCountries: ["US"] },
    data: { FacilityName: name },
    ...extra,
  };
}

async function put(url: string, records: unknown[], query = ""): Promise<Response> {
  return fetch(`${url}/api/storage/v2/records${query}`, { method: "PUT", headers, body: JSON.stringify(records) });
}

async function search(url: string, query: string): Promise<{ totalCount: number; results: { id: string }[] }> {
  const body = JSON.stringify({ kind: "*:*:*:*", query, limit: 100, trackTotalCount: true });
  return (await fetch(`${url}/api/search/v2/query`, { method: "POST", headers, body })).json() as Promise<{ totalCount: number; results: { id: string }[] }>;
}

describe("the stand-in for the e2e suite", () => {
  let standIn: StandIn;
  before(async () => {
    standIn = await start(5391);
  });
  after(() => stop(standIn));

  it("refuses a write, so a spec that sends a record fails loudly", async () => {
    assert.equal((await put(standIn.url, [record("dev:master-data--Well:a")])).status, 404);
    const ddms = await fetch(`${standIn.url}/api/os-wellbore-ddms/ddms/v3/welllogs`, { method: "POST", headers, body: "[]" });
    assert.equal(ddms.status, 404);
  });

  it("still holds the sample wellbores", async () => {
    const found = await fetch(`${standIn.url}/api/search/v2/query`, {
      method: "POST",
      headers,
      body: JSON.stringify({ kind: "osdu:wks:master-data--Wellbore:1.3.0", query: 'data.FacilityName.keyword:"Wellbore A-1"' }),
    });
    assert.equal(((await found.json()) as { totalCount: number }).totalCount, 1);
  });
});

describe("the stand-in taking writes", () => {
  let standIn: StandIn;
  const log = "dev:work-product-component--WellLog:one";
  const logs = (path = "") => `${standIn.url}/api/os-wellbore-ddms/ddms/v3/welllogs${path}`;
  const logRecord = (extra: Record<string, unknown> = {}) => ({ ...record(log), kind: "osdu:wks:work-product-component--WellLog:1.4.0", ...extra });

  before(async () => {
    standIn = await start(5392, { OSDU_STANDIN_WRITES: "true" });
  });
  after(() => stop(standIn));

  it("starts empty, holding none of the e2e suite's wellbores", async () => {
    assert.equal((await search(standIn.url, "*")).totalCount, 0);
  });

  it("takes a record and gives it back, by read and by search", async () => {
    const written = await put(standIn.url, [record("dev:master-data--Well:read")]);
    assert.equal(written.status, 201);
    const answer = (await written.json()) as { recordCount: number; recordIds: string[]; recordIdVersions: string[] };
    assert.equal(answer.recordCount, 1);
    assert.deepEqual(answer.recordIds, ["dev:master-data--Well:read"]);
    assert.match(answer.recordIdVersions[0]!, /^dev:master-data--Well:read:\d{16}$/);

    const read = (await (await fetch(`${standIn.url}/api/storage/v2/records/dev:master-data--Well:read`, { headers })).json()) as { data: unknown; createUser: string };
    assert.deepEqual(read.data, { FacilityName: "first" });
    assert.ok(read.createUser.length > 0);
    assert.equal((await search(standIn.url, 'data.FacilityName.keyword:"first"')).totalCount, 1);
  });

  it("reads a property inside an array from every element, as the index flattens one", async () => {
    await put(standIn.url, [record("dev:master-data--Well:curves", "curves", { data: { Curves: [{ Mnemonic: "GR" }, { Mnemonic: "DT" }] } })]);

    assert.equal((await search(standIn.url, 'data.Curves.Mnemonic:"DT"')).totalCount, 1);
    const body = JSON.stringify({ kind: "*:*:*:*", query: 'data.Curves.Mnemonic:"GR"', aggregateBy: "data.Curves.Mnemonic", limit: 0, trackTotalCount: true });
    const answer = (await (await fetch(`${standIn.url}/api/search/v2/query`, { method: "POST", headers, body })).json()) as { aggregations: unknown };
    assert.deepEqual(answer.aggregations, [{ key: "DT", count: 1 }, { key: "GR", count: 1 }]);
  });

  it("reads a nested array as the contract names it, counting its items", async () => {
    const curves = { Curves: [{ Mnemonic: "NPHI" }, { Mnemonic: "NPHI" }, { Mnemonic: "SP" }] };
    await put(standIn.url, [record("dev:master-data--Well:nested", "nested", { data: curves })]);

    assert.equal((await search(standIn.url, 'nested(data.Curves, (Mnemonic.keyword:"SP"))')).totalCount, 1);
    assert.equal((await search(standIn.url, 'nested(data.Curves, (Mnemonic.keyword:"ABSENT"))')).totalCount, 0);
    const body = JSON.stringify({ kind: "*:*:*:*", query: 'nested(data.Curves, (Mnemonic:"SP"))', aggregateBy: "nested(data.Curves, Mnemonic.keyword)", limit: 0, trackTotalCount: true });
    const answer = (await (await fetch(`${standIn.url}/api/search/v2/query`, { method: "POST", headers, body })).json()) as { aggregations: unknown };
    assert.deepEqual(answer.aggregations, [{ key: "NPHI", count: 2 }, { key: "SP", count: 1 }]);
  });

  it("mints an id for a record that names none", async () => {
    const minted = record("");
    delete (minted as { id?: string }).id;
    const answer = (await (await put(standIn.url, [minted])).json()) as { recordIds: string[] };
    assert.match(answer.recordIds[0]!, /^dev:master-data--Well:[0-9a-f-]{36}$/);
  });

  it("keeps a version per write, each readable", async () => {
    const id = "dev:master-data--Well:versions";
    await put(standIn.url, [record(id, "one")]);
    await put(standIn.url, [record(id, "two")]);
    const { versions } = (await (await fetch(`${standIn.url}/api/storage/v2/records/versions/${id}`, { headers })).json()) as { versions: number[] };
    assert.equal(versions.length, 2);
    assert.ok(versions[1]! > versions[0]!);
    const first = (await (await fetch(`${standIn.url}/api/storage/v2/records/${id}/${versions[0]}`, { headers })).json()) as { data: { FacilityName: string } };
    assert.equal(first.data.FacilityName, "one");
  });

  it("skips a write that changes nothing when asked, whatever the order of its keys", async () => {
    const id = "dev:master-data--Well:same";
    await put(standIn.url, [record(id, "same")]);
    const { acl, data, kind, legal } = record(id, "same");
    const reordered = { legal, data, acl, kind, id };
    const again = (await (await put(standIn.url, [reordered], "?skipdupes=true")).json()) as { skippedRecordIds: string[]; recordIds: string[] };
    assert.deepEqual(again.skippedRecordIds, [id]);
    assert.deepEqual(again.recordIds, []);
    const changed = (await (await put(standIn.url, [record(id, "changed")], "?skipdupes=true")).json()) as { skippedRecordIds: string[]; recordIds: string[] };
    assert.deepEqual(changed.recordIds, [id]);
  });

  for (const [why, bad, says] of [
    ["a kind that is not one", { ...record("dev:master-data--Well:bad"), kind: "nonsense" }, /kind/],
    ["no acl", { ...record("dev:master-data--Well:bad"), acl: { owners: [], viewers: [] } }, /acl/],
    ["no legal tag", { ...record("dev:master-data--Well:bad"), legal: { legaltags: [] } }, /legal tag/],
    ["an id in another partition", record("other:master-data--Well:bad"), /partition/],
  ] as const) {
    it(`refuses a record with ${why}`, async () => {
      const refused = await put(standIn.url, [bad]);
      assert.equal(refused.status, 400);
      assert.match(((await refused.json()) as { message: string }).message, says);
    });
  }

  it("takes a record out and brings it back, as the reversible scope does", async () => {
    const id = "dev:master-data--Well:gone";
    await put(standIn.url, [record(id, "gone")]);
    assert.equal((await fetch(`${standIn.url}/api/storage/v2/records/${id}:delete`, { method: "POST", headers })).status, 204);
    assert.equal((await fetch(`${standIn.url}/api/storage/v2/records/${id}`, { headers })).status, 404);
    assert.equal((await search(standIn.url, 'data.FacilityName.keyword:"gone"')).totalCount, 0);

    assert.equal((await fetch(`${standIn.url}/api/storage/v2/records/${id}`, { method: "PATCH", headers, body: "{}" })).status, 200);
    assert.equal((await fetch(`${standIn.url}/api/storage/v2/records/${id}`, { headers })).status, 200);
    assert.equal((await fetch(`${standIn.url}/api/storage/v2/records/${id}`, { method: "PATCH", headers, body: "{}" })).status, 400);
    assert.equal((await fetch(`${standIn.url}/api/storage/v2/records/dev:master-data--Well:never:delete`, { method: "POST", headers })).status, 404);
  });

  it("deletes a batch of records", async () => {
    await put(standIn.url, [record("dev:master-data--Well:b1", "batch"), record("dev:master-data--Well:b2", "batch")]);
    const body = JSON.stringify(["dev:master-data--Well:b1", "dev:master-data--Well:b2"]);
    assert.equal((await fetch(`${standIn.url}/api/storage/v2/records/delete`, { method: "POST", headers, body })).status, 204);
    assert.equal((await search(standIn.url, 'data.FacilityName.keyword:"batch"')).totalCount, 0);
  });

  it("reads through a cursor to its end, and closes one", async () => {
    await put(standIn.url, [1, 2, 3, 4, 5].map((n) => record(`dev:master-data--Well:cursor${n}`, "cursor")));
    const query = { kind: "*:*:*:*", query: 'data.FacilityName.keyword:"cursor"', limit: 2 };
    const page = async (body: unknown) => (await (await fetch(`${standIn.url}/api/search/v2/query_with_cursor`, { method: "POST", headers, body: JSON.stringify(body) })).json()) as { cursor: string | null; results: unknown[]; totalCount: number };
    let answer = await page(query);
    assert.equal(answer.totalCount, 5);
    let read = answer.results.length;
    while (answer.results.length > 0) {
      answer = await page({ cursor: answer.cursor });
      read += answer.results.length;
    }

    assert.equal(read, 5);
    const closing = await page(query);
    assert.equal((await fetch(`${standIn.url}/api/search/v2/query_with_cursor/${closing.cursor}`, { method: "DELETE", headers })).status, 204);
    assert.equal((await fetch(`${standIn.url}/api/search/v2/query_with_cursor`, { method: "POST", headers, body: JSON.stringify({ cursor: closing.cursor }) })).status, 400);
  });

  it("validates any legal tag, and reads one", async () => {
    const valid = await fetch(`${standIn.url}/api/legal/v1/legaltags:validate`, { method: "POST", headers, body: JSON.stringify({ names: ["x"] }) });
    assert.deepEqual(await valid.json(), { invalidLegalTags: [] });
    assert.equal((await fetch(`${standIn.url}/api/legal/v1/legaltags/dev-sample`, { headers })).status, 200);
  });

  it("answers that the indexer failed nothing", async () => {
    assert.equal((await search(standIn.url, "index.statusCode:[201 TO *]")).totalCount, 0);
    assert.doesNotMatch(standIn.log(), /does not read the query clause/);
  });

  it("takes a well log and its curves whole, and reads them back as the file holds them", async () => {
    assert.equal((await fetch(logs(), { method: "POST", headers, body: JSON.stringify([logRecord()]) })).status, 200);
    const sent = await fetch(logs(`/${log}/data`), { method: "POST", headers: { ...headers, "content-type": "application/x-parquet" }, body: chunk });
    assert.equal(sent.status, 200);

    const described = (await (await fetch(logs(`/${log}/data?describe=true`), { headers })).json()) as { numberOfRows: number; columns: string[] };
    assert.deepEqual(described.columns, ["MD", "GR", "RD"]);
    assert.ok(described.numberOfRows > 100);

    const page = (await (await fetch(logs(`/${log}/data?curves=MD,RD&offset=2&limit=3&orient=split`), { headers })).json()) as { columns: string[]; index: number[]; data: (number | null)[][] };
    assert.deepEqual(page.columns, ["MD", "RD"]);
    assert.deepEqual(page.index, [2, 3, 4]);
    assert.equal(page.data.length, 3);
    assert.ok((page.data[1]![0] as number) > (page.data[0]![0] as number));
  });

  it("links a record to its bulk data, and refuses a write that drops the link", async () => {
    const held = (await (await fetch(logs(`/${log}`), { headers })).json()) as { data: { ExtensionProperties: { wdms: { bulkURI: string } } } };
    assert.match(held.data.ExtensionProperties.wdms.bulkURI, /^urn:wdms-1:uuid:/);
    const dropped = await fetch(logs(), { method: "POST", headers, body: JSON.stringify([logRecord()]) });
    assert.equal(dropped.status, 400);
    assert.match(((await dropped.json()) as { message: string }).message, /bulk link/);
  });

  it("commits a session's chunks as one, in order", async () => {
    const opened = (await (await fetch(logs(`/${log}/sessions`), { method: "POST", headers, body: JSON.stringify({ mode: "overwrite", fromVersion: 0 }) })).json()) as { id: string; state: string };
    assert.equal(opened.state, "open");
    const chunks = logs(`/${log}/sessions/${opened.id}/data`);
    for (const part of [chunk, chunk]) {
      assert.equal((await fetch(chunks, { method: "POST", headers: { ...headers, "content-type": "application/x-parquet" }, body: part })).status, 200);
    }

    const session = logs(`/${log}/sessions/${opened.id}`);
    assert.equal((await fetch(session, { method: "PATCH", headers, body: JSON.stringify({ state: "commit" }) })).status, 200);
    assert.equal(((await (await fetch(session, { headers })).json()) as { state: string }).state, "committed");
    const described = (await (await fetch(logs(`/${log}/data?describe=true`), { headers })).json()) as { numberOfRows: number };
    assert.ok(described.numberOfRows > 200);
    assert.equal((await fetch(session, { method: "PATCH", headers, body: JSON.stringify({ state: "commit" }) })).status, 409);
  });

  it("drops an abandoned session's chunks, and lists a record's sessions", async () => {
    const before = (await (await fetch(logs(`/${log}/data?describe=true`), { headers })).json()) as { numberOfRows: number };
    const opened = (await (await fetch(logs(`/${log}/sessions`), { method: "POST", headers, body: "{}" })).json()) as { id: string };
    await fetch(logs(`/${log}/sessions/${opened.id}/data`), { method: "POST", headers: { ...headers, "content-type": "application/x-parquet" }, body: chunk });
    await fetch(logs(`/${log}/sessions/${opened.id}`), { method: "PATCH", headers, body: JSON.stringify({ state: "abandon" }) });
    const after = (await (await fetch(logs(`/${log}/data?describe=true`), { headers })).json()) as { numberOfRows: number };
    assert.equal(after.numberOfRows, before.numberOfRows);
    const listed = (await (await fetch(logs(`/${log}/sessions`), { headers })).json()) as { id: string; state: string }[];
    assert.ok(listed.some((s) => s.id === opened.id && s.state === "abandoned"));
  });

  it("answers 404 for bulk data, a session and a record it does not hold", async () => {
    assert.equal((await fetch(logs("/dev:work-product-component--WellLog:none/data?describe=true"), { headers })).status, 404);
    assert.equal((await fetch(logs(`/${log}/sessions/none`), { headers })).status, 404);
    const data = await fetch(logs("/dev:work-product-component--WellLog:none/data"), { method: "POST", headers: { ...headers, "content-type": "application/x-parquet" }, body: chunk });
    assert.equal(data.status, 404);
  });

  it("answers bad input with a 400 and keeps running", async () => {
    assert.equal((await fetch(`${standIn.url}/api/storage/v2/records`, { method: "PUT", headers, body: "not json" })).status, 400);
    assert.equal((await fetch(logs(`/${log}/data`), { method: "POST", headers: { ...headers, "content-type": "application/x-parquet" }, body: "not parquet" })).status, 400);
    assert.equal((await fetch(`${standIn.url}/api/storage/v2/records/%E0%A4%A:delete`, { method: "POST", headers })).status, 400);
    assert.ok((await fetch(`${standIn.url}/health`)).ok);
  });

  it("logs a route it does not answer, so a flow's need is found at once", async () => {
    assert.equal((await fetch(`${standIn.url}/api/nothing/here`, { headers })).status, 404);
    assert.match(standIn.log(), /answered 404 to GET \/api\/nothing\/here/);
  });
});

describe("the schemas a demo's stand-in holds", () => {
  it("serves the published schemas the checkouts keep, and leaves an estate's own kind unregistered", async () => {
    const checkouts = mkdtempSync(join(tmpdir(), "standin-checkouts-"));
    const templates = join(checkouts, "repo", "commit", "welldb", "templates");
    mkdirSync(templates, { recursive: true });
    writeFileSync(join(templates, "osdu_wks_master-data--Well_1.2.0.json"), JSON.stringify({ title: "Well" }));
    writeFileSync(join(templates, "welldb_wks_work-product-component--WellDbWellLog_1.0.0.json"), JSON.stringify({ title: "Own" }));
    writeFileSync(join(templates, "osdu_wks_master-data--Broken_1.0.0.json"), "{ not json");
    const standIn = await start(5395, { OSDU_STANDIN_WRITES: "true", SQLFLOW_NODE_CACHE: checkouts });
    try {
      const schema = (id: string) => fetch(`${standIn.url}/api/schema-service/v1/schema/${encodeURIComponent(id)}`, { headers });

      assert.deepEqual(await (await schema("osdu:wks:master-data--Well:1.2.0")).json(), { title: "Well" });
      assert.equal((await schema("welldb:wks:work-product-component--WellDbWellLog:1.0.0")).status, 404);
      assert.equal((await schema("osdu:wks:master-data--Broken:1.0.0")).status, 404);
      assert.match(standIn.log(), /skipped the schema/);

      const listing = (await (await fetch(`${standIn.url}/api/schema-service/v1/schema?scope=SHARED`, { headers })).json()) as { totalCount: number };
      assert.equal(listing.totalCount, 1);
    } finally {
      await stop(standIn);
      rmSync(checkouts, { recursive: true, force: true });
    }
  });
});

describe("what the stand-in keeps", () => {
  const folder = mkdtempSync(join(tmpdir(), "osdu-standin-"));
  const state = join(folder, "state.json");
  const env = { OSDU_STANDIN_WRITES: "true", OSDU_STANDIN_STATE: state };
  after(() => rmSync(folder, { recursive: true, force: true }));

  it("keeps what was delivered across a restart", async () => {
    const first = await start(5393, env);
    await put(first.url, [record("dev:master-data--Well:kept", "kept")]);
    for (let attempt = 0; attempt < 100 && !existsSync(state); attempt++) {
      await new Promise((resolve) => setTimeout(resolve, 50));
    }

    await stop(first);
    const second = await start(5393, env);
    assert.equal((await search(second.url, 'data.FacilityName.keyword:"kept"')).totalCount, 1);
    await stop(second);
  });

  it("starts empty from a file a crash cut short, keeping it aside", async () => {
    writeFileSync(state, '{"records": [["dev:');
    const standIn = await start(5393, env);
    assert.equal((await search(standIn.url, "*")).totalCount, 0);
    assert.match(standIn.log(), /could not read/);
    assert.ok(existsSync(`${state}.unreadable`));
    await stop(standIn);
  });

  it("says a port that is taken, rather than failing with a stack", async () => {
    const holder = await start(5394, env);
    const second = spawn(process.execPath, ["standin/osdu-standin.mjs"], { cwd: gui, env: { ...process.env, SQLFLOW_E2E_OSDU_PORT: "5394", ...env }, stdio: ["ignore", "pipe", "pipe"] });
    let said = "";
    second.stderr.on("data", (text: Buffer) => (said += text.toString()));
    const code = await new Promise((resolve) => second.once("exit", resolve));
    await stop(holder);
    assert.equal(code, 1);
    assert.match(said, /Port 5394 is taken/);
  });
});
