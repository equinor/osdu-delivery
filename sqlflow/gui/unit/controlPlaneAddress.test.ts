// How a control plane's address is shown on the sign-in (src/embed/controlPlaneAddress.ts): its name first, the rest under it.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { controlPlaneAddress } from "../src/embed/controlPlaneAddress.ts";

describe("a control plane's address as a person reads it", () => {
  it("is a cloud host's first label, with the rest of the host under it", () => {
    assert.deepEqual(controlPlaneAddress("https://cp-dev-west.blue-1234.westeurope.example.io"), {
      name: "cp-dev-west",
      rest: "blue-1234.westeurope.example.io",
      secure: true,
    });
  });

  it("keeps a port and a path with the rest, without the trailing slash", () => {
    assert.deepEqual(controlPlaneAddress("https://sqlflow.example.com:8443/control/"), {
      name: "sqlflow",
      rest: "example.com:8443/control",
      secure: true,
    });
  });

  it("is a local address whole, with its port", () => {
    assert.deepEqual(controlPlaneAddress("http://localhost:5000"), { name: "localhost:5000", rest: null, secure: false });
    assert.deepEqual(controlPlaneAddress("http://10.0.0.75:5299/"), { name: "10.0.0.75:5299", rest: null, secure: false });
    assert.deepEqual(controlPlaneAddress("http://[::1]:5000"), { name: "[::1]:5000", rest: null, secure: false });
  });

  it("keeps a local address's path under it", () => {
    assert.deepEqual(controlPlaneAddress("http://localhost:5000/sqlflow"), { name: "localhost:5000", rest: "/sqlflow", secure: false });
  });

  it("shows what is no address as it was given", () => {
    assert.deepEqual(controlPlaneAddress(" not an address "), { name: "not an address", rest: null, secure: false });
  });
});
