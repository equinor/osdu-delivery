// What a record's page may do with what OSDU holds of it (osdu/docs/reference/concepts/record-lifecycle.md).

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { osduEmptyText, wroteToOsdu } from "../src/features/delivery/recordTarget.ts";

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

  it("says a removed record and one without an id hold nothing to read", () => {
    assert.equal(osduEmptyText({ targetId: ID, status: "deleted", claimed: true }).title, "Removed from OSDU");
    assert.equal(osduEmptyText({ targetId: null, status: "pending", claimed: false }).title, "No OSDU id yet");
  });
});
