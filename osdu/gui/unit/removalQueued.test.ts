// What a page says once a removal is queued: one sentence by its scope, naming how many records when there are several.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { removalQueuedText } from "../src/features/delivery/removalQueued.ts";

describe("removalQueuedText", () => {
  it("names a removal, and its deletion from the ledger", () => {
    assert.equal(removalQueuedText({ scope: "record", records: 1, purgeLedger: false }, false), "Removal: queued as a run.");
    assert.equal(
      removalQueuedText({ scope: "everything", records: 1200, purgeLedger: true }, true),
      "Removal of 1,200 records, then deletion from the ledger: queued as a run.");
  });

  it("names a step back", () => {
    assert.equal(removalQueuedText({ scope: "previous", records: 2, purgeLedger: false }, true), "Restoring the previous version of 2 records: queued as a run.");
  });

  it("names a restriction with the restriction it sets", () => {
    assert.equal(
      removalQueuedText({ scope: "restrict", records: 1, purgeLedger: false, restriction: "legal-hold" }, true),
      "Restricting the access of 1 record under legal-hold: queued as a run.");
    assert.equal(removalQueuedText({ scope: "restrict", records: 1, purgeLedger: false, restriction: null }, false), "Restricting the access: queued as a run.");
  });

  it("names a lift", () => {
    assert.equal(removalQueuedText({ scope: "unrestrict", records: 3, purgeLedger: false }, true), "Lifting the restriction of 3 records: queued as a run.");
  });
});
