// What a record's page may do with what OSDU holds of it (osdu/docs/reference/concepts/record-lifecycle.md).

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { osduEmptyText, readableInOsdu, wroteToOsdu } from "../src/features/delivery/recordTarget.ts";
import { verifyOutcomeAgrees, verifyOutcomeLabel } from "../src/features/delivery/verifyOutcome.ts";

const ID = "dev:work-product-component--WellLog:408bf331-c86b-5a3e-8075-7510c092fcfa";

describe("whether a record's flow wrote to OSDU", () => {
  it("is so for a record that claimed its id and was not removed", () => {
    assert.equal(wroteToOsdu({ targetId: ID, status: "delivered", claimed: true }), true);
    assert.equal(wroteToOsdu({ targetId: ID, status: "held", claimed: true }), true);
  });

  it("is not so for a record that was only ever held, though it names the id its key makes", () => {
    assert.equal(wroteToOsdu({ targetId: ID, status: "held", claimed: false }), false);
  });

  it("is not so for a record without an id, or one removed from OSDU", () => {
    assert.equal(wroteToOsdu({ targetId: null, status: "pending", claimed: false }), false);
    assert.equal(wroteToOsdu({ targetId: ID, status: "deleted", claimed: true }), false);
  });

  it("takes a record at its id where the control plane does not say whether it claimed it", () => {
    assert.equal(wroteToOsdu({ targetId: ID, status: "delivered" }), true);
  });
});

describe("whether there is an id of the flow's to ask OSDU about", () => {
  it("is so for a record its flow wrote, removed since or not, so a removal can be checked", () => {
    assert.equal(readableInOsdu({ targetId: ID, status: "delivered", claimed: true }), true);
    assert.equal(readableInOsdu({ targetId: ID, status: "deleted", claimed: true }), true);
  });

  it("is not so for a record without an id, or one whose flow never wrote to it", () => {
    assert.equal(readableInOsdu({ targetId: null, status: "pending", claimed: false }), false);
    assert.equal(readableInOsdu({ targetId: ID, status: "deleted", claimed: false }), false);
  });
});

describe("how a verify outcome reads", () => {
  it("agrees for a delivered record OSDU holds as delivered and a removed one gone from it", () => {
    assert.deepEqual(["match", "gone"].map(verifyOutcomeAgrees), [true, true]);
    assert.deepEqual(["drifted", "missing", "served", "error"].map(verifyOutcomeAgrees), [false, false, false, false]);
  });

  it("names a removed record OSDU still holds plainly", () => {
    assert.equal(verifyOutcomeLabel("served"), "still in OSDU");
    assert.equal(verifyOutcomeLabel("gone"), "gone");
  });
});

describe("what a record's OSDU tab says before a read", () => {
  it("offers a read where the flow wrote the record", () => {
    assert.equal(osduEmptyText({ targetId: ID, status: "delivered", claimed: true }).title, "Not read yet");
  });

  it("says a record that was only ever held was never sent, rather than offering a read that is refused", () => {
    assert.deepEqual(osduEmptyText({ targetId: ID, status: "held", claimed: false }), {
      title: "Not sent yet",
      description: "The record has never queued a document for OSDU, so its flow wrote nothing there to read.",
    });
  });

  it("offers a removed record's read as the check that OSDU no longer holds it", () => {
    const removed = osduEmptyText({ targetId: ID, status: "deleted", claimed: true });
    assert.equal(removed.title, "Removed from OSDU");
    assert.match(removed.description, /a removal that stands answers that OSDU holds no record under it/);
  });

  it("says a record without an id holds nothing to read", () => {
    assert.equal(osduEmptyText({ targetId: null, status: "pending", claimed: false }).title, "No OSDU id yet");
  });
});
