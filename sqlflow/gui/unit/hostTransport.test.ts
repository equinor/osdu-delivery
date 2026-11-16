// A hosted page's calls, carried by its host (src/embed/hostTransport.ts): the suite plays the host, so what is checked
// is what the API client gets back for what the host says.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { hostTransport, type TransportHost } from "../src/embed/hostTransport.ts";
import type { HostMessage, PageMessage } from "../src/embed/protocol.ts";

/** A host that keeps what a page sent and says what the test tells it to. */
function playedHost() {
  const sent: PageMessage[] = [];
  const listeners: ((message: HostMessage) => void)[] = [];
  const host: TransportHost = {
    send: (message) => sent.push(message),
    listen: (listener) => {
      listeners.push(listener);
      return () => listeners.splice(listeners.indexOf(listener), 1);
    },
  };
  return {
    host,
    sent,
    say: (message: HostMessage) => listeners.forEach((listener) => listener(message)),
    requests: () => sent.filter((message): message is Extract<PageMessage, { type: "request" }> => message.type === "request"),
  };
}

const url = new URL("https://cp.example.com/api/v1/runs?page=1");

/** Lets the transport reach the point where it has handed its call to the host. */
const settled = () => new Promise((resolve) => setImmediate(resolve));

describe("a call a host carries", () => {
  it("goes out as the client built it and comes back as the control plane answered", async () => {
    const played = playedHost();
    const call = hostTransport(played.host)(url, {
      method: "post",
      headers: { Accept: "application/json", "Content-Type": "application/json" },
      body: JSON.stringify({ name: "wells" }),
    });
    await settled();

    const [request] = played.requests();
    assert.equal(request.method, "POST");
    assert.equal(request.url, url.toString());
    assert.equal(request.headers.accept, "application/json");
    assert.deepEqual(request.body, { text: "{\"name\":\"wells\"}" });
    assert.equal(request.stream, false);

    played.say({
      type: "response",
      id: request.id,
      status: 201,
      statusText: "Created",
      headers: { "content-type": "application/json", "x-correlation-id": "abc" },
      body: { text: "{\"id\":7}" },
      streamed: false,
    });
    const response = await call;
    assert.equal(response.status, 201);
    assert.equal(response.headers.get("X-Correlation-ID"), "abc");
    assert.deepEqual(await response.json(), { id: 7 });
  });

  it("keeps calls in flight apart by their numbers", async () => {
    const played = playedHost();
    const transport = hostTransport(played.host);
    const first = transport(url, { method: "GET" });
    const second = transport(url, { method: "GET" });
    await settled();

    const [a, b] = played.requests();
    assert.notEqual(a.id, b.id);
    played.say({ type: "response", id: b.id, status: 200, statusText: "OK", headers: {}, body: { text: "second" }, streamed: false });
    played.say({ type: "response", id: a.id, status: 200, statusText: "OK", headers: {}, body: { text: "first" }, streamed: false });
    assert.equal(await (await first).text(), "first");
    assert.equal(await (await second).text(), "second");
  });

  it("answers an empty body and a status that has none", async () => {
    const played = playedHost();
    const transport = hostTransport(played.host);
    const none = transport(url, { method: "DELETE" });
    const empty = transport(url, { method: "POST" });
    await settled();

    const [a, b] = played.requests();
    played.say({ type: "response", id: a.id, status: 204, statusText: "No Content", headers: {}, streamed: false });
    played.say({ type: "response", id: b.id, status: 200, statusText: "OK", headers: {}, streamed: false });
    assert.equal((await none).status, 204);
    assert.equal(await (await empty).text(), "");
  });

  it("hands bytes over as bytes, both ways", async () => {
    const played = playedHost();
    const call = hostTransport(played.host)(url, { method: "POST", body: new Blob([Uint8Array.from([1, 2, 3])]) });
    await settled();

    const [request] = played.requests();
    assert.deepEqual([...(request.body as { bytes: Uint8Array }).bytes], [1, 2, 3]);
    played.say({
      type: "response",
      id: request.id,
      status: 200,
      statusText: "OK",
      headers: { "content-type": "application/octet-stream" },
      body: { bytes: Uint8Array.from([9, 8, 7]) },
      streamed: false,
    });
    assert.deepEqual([...new Uint8Array(await (await call).arrayBuffer())], [9, 8, 7]);
  });

  it("fails as an unreachable control plane does when the host could not make it", async () => {
    const played = playedHost();
    const call = hostTransport(played.host)(url, { method: "GET" });
    await settled();

    played.say({ type: "failed", id: played.requests()[0].id, reason: "The control plane did not answer: refused", aborted: false });
    await assert.rejects(call, (error: unknown) => error instanceof TypeError && /did not answer/.test(error.message));
  });
});

describe("a call that asks for a stream", () => {
  it("is asked for as one, and its body arrives in the pieces the host sends", async () => {
    const played = playedHost();
    const call = hostTransport(played.host)(url, { method: "GET", headers: { Accept: "text/event-stream" } });
    await settled();

    const [request] = played.requests();
    assert.equal(request.stream, true);
    played.say({ type: "response", id: request.id, status: 200, statusText: "OK", headers: { "content-type": "text/event-stream" }, streamed: true });
    const response = await call;
    const reader = response.body!.getReader();
    const decoder = new TextDecoder();

    played.say({ type: "chunk", id: request.id, text: "event: entry\ndata: one\n\n" });
    assert.equal(decoder.decode((await reader.read()).value), "event: entry\ndata: one\n\n");
    played.say({ type: "chunk", id: request.id, text: "data: two\n\n" });
    assert.equal(decoder.decode((await reader.read()).value), "data: two\n\n");
    played.say({ type: "end", id: request.id });
    assert.equal((await reader.read()).done, true);
  });

  it("tells the host to stop when the reader stops reading", async () => {
    const played = playedHost();
    const abort = new AbortController();
    const call = hostTransport(played.host)(url, { method: "GET", headers: { Accept: "text/event-stream" }, signal: abort.signal });
    await settled();

    const [request] = played.requests();
    played.say({ type: "response", id: request.id, status: 200, statusText: "OK", headers: {}, streamed: true });
    const reader = (await call).body!.getReader();
    abort.abort();
    await assert.rejects(reader.read(), (error: unknown) => (error as Error).name === "AbortError");
    assert.deepEqual(played.sent.at(-1), { type: "abort", id: request.id });

    // What the host had already sent arrives after: it is for a call nobody waits for, and is dropped.
    played.say({ type: "chunk", id: request.id, text: "late" });
    played.say({ type: "end", id: request.id });
  });
});

describe("a call the page gives up on", () => {
  it("is never sent when it was given up before it started", async () => {
    const played = playedHost();
    const abort = new AbortController();
    abort.abort();
    await assert.rejects(
      hostTransport(played.host)(url, { method: "GET", signal: abort.signal }),
      (error: unknown) => (error as Error).name === "AbortError",
    );
    assert.equal(played.sent.length, 0);
  });

  it("is dropped at the host, and its late answer is ignored", async () => {
    const played = playedHost();
    const abort = new AbortController();
    const call = hostTransport(played.host)(url, { method: "GET", signal: abort.signal });
    await settled();

    const [request] = played.requests();
    abort.abort();
    await assert.rejects(call, (error: unknown) => (error as Error).name === "AbortError");
    assert.deepEqual(played.sent.at(-1), { type: "abort", id: request.id });
    played.say({ type: "response", id: request.id, status: 200, statusText: "OK", headers: {}, streamed: false });
  });
});
