// What a hosted page and its host may say to each other (src/embed/protocol.ts): the checks both sides read a message
// with, since a host reads a page's message as coming from anyone.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  CARRIED_METHODS,
  carriedBytes,
  carriedPath,
  HOST_ACTIONS,
  isGuiPath,
  isHostAction,
  passedHeaders,
} from "../src/embed/protocol.ts";

describe("the address a call is carried to", () => {
  it("is the path and query under the control plane's base", () => {
    assert.equal(carriedPath("https://cp.example.com/api/v1/runs?page=2", "https://cp.example.com"), "/api/v1/runs?page=2");
    assert.equal(carriedPath("https://cp.example.com/api/v1/runs", "https://cp.example.com/"), "/api/v1/runs");
  });

  it("stays under a base that has a path of its own", () => {
    assert.equal(carriedPath("https://example.com/sqlflow/api/v1/runs", "https://example.com/sqlflow"), "/sqlflow/api/v1/runs");
    assert.equal(carriedPath("https://example.com/other/api/v1/runs", "https://example.com/sqlflow"), null);
    assert.equal(carriedPath("https://example.com/sqlflow-two/api", "https://example.com/sqlflow"), null);
  });

  it("is refused for any other origin, scheme or port", () => {
    assert.equal(carriedPath("https://evil.example.com/api/v1/runs", "https://cp.example.com"), null);
    assert.equal(carriedPath("http://cp.example.com/api/v1/runs", "https://cp.example.com"), null);
    assert.equal(carriedPath("https://cp.example.com:8443/api/v1/runs", "https://cp.example.com"), null);
    assert.equal(carriedPath("file:///etc/passwd", "https://cp.example.com"), null);
  });

  it("is refused for an address that carries a credential of its own, or is no address", () => {
    assert.equal(carriedPath("https://someone:secret@cp.example.com/api/v1/runs", "https://cp.example.com"), null);
    assert.equal(carriedPath("/api/v1/runs", "https://cp.example.com"), null);
    assert.equal(carriedPath("https://cp.example.com/api", "not an address"), null);
  });
});

describe("the headers of a page's call a host passes on", () => {
  it("are what a page has reason to set", () => {
    assert.deepEqual(
      passedHeaders({ Accept: "application/json", "Content-Type": "application/json", "X-Partition": "dev" }),
      { Accept: "application/json", "Content-Type": "application/json", "X-Partition": "dev" },
    );
  });

  it("never the credential, a cookie, the host or a forwarding claim that is not the page's to make", () => {
    assert.deepEqual(
      passedHeaders({ Authorization: "Bearer stolen", Cookie: "a=b", Host: "other", "Proxy-Authorization": "x", Origin: "https://other" }),
      {},
    );
  });

  it("never a value that would start another header", () => {
    assert.deepEqual(passedHeaders({ "X-Partition": "dev\r\nAuthorization: Bearer stolen" }), {});
  });
});

describe("what a page names", () => {
  it("is a page of the GUI only as a path inside it", () => {
    assert.equal(isGuiPath("/runs/1?tab=trace"), true);
    assert.equal(isGuiPath("/"), true);
    for (const other of ["//evil.example.com/x", "https://evil.example.com", "runs", "", "/a\\b", "/a\nb", 3, null, undefined]) {
      assert.equal(isGuiPath(other), false, String(other));
    }
  });

  it("is an action of the host only by a name on its list", () => {
    for (const action of HOST_ACTIONS) {
      assert.equal(isHostAction(action), true, action);
    }

    for (const other of ["workbench.action.terminal.new", "sqlflow.signIn", "", 1, null]) {
      assert.equal(isHostAction(other), false, String(other));
    }
  });

  it("is a call only by a method the API has", () => {
    assert.deepEqual([...CARRIED_METHODS].sort(), ["DELETE", "GET", "PATCH", "POST", "PUT"]);
  });
});

describe("the bytes of a carried body", () => {
  const bytes = Uint8Array.from([0, 1, 2, 250, 255]);

  it("are read however a host's channel hands them over", () => {
    assert.deepEqual([...carriedBytes(bytes)], [...bytes]);
    assert.deepEqual([...carriedBytes(bytes.buffer)], [...bytes]);
    assert.deepEqual([...carriedBytes(new DataView(bytes.buffer, 1, 3))], [1, 2, 250]);
    // A channel that wrote a typed array as text hands back its entries by number, and Node's own buffers by `data`.
    assert.deepEqual([...carriedBytes({ 0: 0, 1: 1, 2: 2, 3: 250, 4: 255 })], [...bytes]);
    assert.deepEqual([...carriedBytes({ type: "Buffer", data: [0, 1, 2, 250, 255] })], [...bytes]);
  });

  it("are refused when they are not bytes", () => {
    assert.throws(() => carriedBytes("text"), /not bytes/);
    assert.throws(() => carriedBytes({ 0: 256 }), /not bytes/);
    assert.throws(() => carriedBytes(null), /not bytes/);
  });
});
