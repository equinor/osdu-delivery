// Whether a UTC instant the control plane sends has passed, the same in every time zone: a record's page says its lease ran
// out by it.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { hasPassed } from "../src/features/delivery/utcInstant.ts";

// East of UTC, where an instant without a zone read as local time comes an hour early. Each test file runs in a process of
// its own, so the zone is this file's alone.
process.env.TZ = "Europe/Oslo";

describe("a UTC instant the API sends without a zone", () => {
  // As System.Text.Json writes a DateTime EF read back: seven fractional digits and no zone. 19:08:09 UTC is 20:08:09 in Oslo.
  const lease = "2026-10-27T19:08:09.1234567";

  it("has not passed while its UTC time is still ahead, though the local clock already reads later", () => {
    assert.equal(hasPassed(lease, Date.UTC(2026, 9, 27, 19, 5, 14)), false);
  });

  it("has passed once its UTC time has", () => {
    assert.equal(hasPassed(lease, Date.UTC(2026, 9, 27, 19, 8, 10)), true);
  });
});

describe("a UTC instant the API sends with a zone", () => {
  it("is read in the zone it names", () => {
    assert.equal(hasPassed("2026-10-27T19:08:09Z", Date.UTC(2026, 9, 27, 19, 8, 8)), false);
    assert.equal(hasPassed("2026-10-27T20:08:09+01:00", Date.UTC(2026, 9, 27, 19, 8, 10)), true);
  });
});

describe("no instant", () => {
  it("never passes, nor does one that does not parse", () => {
    assert.equal(hasPassed(null, Date.UTC(2026, 9, 27)), false);
    assert.equal(hasPassed("not a time", Date.UTC(2026, 9, 27)), false);
  });
});
