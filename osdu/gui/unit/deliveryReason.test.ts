// Why a record was delivered, as the record's timeline titles a delivered try and a submission counts its deliveries.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  deliveredByText, deliveredTitle, NOT_RECORDED, pathsHeading, triggerLabel, triggerNames, triggerSides, type AttemptReason,
} from "../src/features/delivery/deliveryReason.ts";

const mappingChanged: AttemptReason = {
  recorded: true,
  action: "update-metadata",
  summary: "mapping WellLog@1.4.0 changed (852db64); acl.viewers changed",
  triggers: [{
    kind: "mapping-changed",
    from: "WellLog@1.4.0",
    to: "WellLog@1.4.0",
    fromFingerprint: "1f0c8b2e44d9a7c6b5e3f2a1908d7c6b5a4f3e2d1c0b9a8f7e6d5c4b3a291807",
    toFingerprint: "9a8b7c6d5e4f30211f0e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a6978",
    fromCommit: "268b2527d1c04e2fa3b9c8d7e6f5a4b3c2d1e0f9",
    toCommit: "852db64a9c0e4f7b8d1e2a3b4c5d6e7f80912345",
  }],
  paths: { compared: true, added: 0, removed: 0, changed: 1, listed: [{ path: "acl.viewers", change: "changed" }] },
};

describe("deliveredTitle", () => {
  it("titles a delivered try with what sent it", () => {
    assert.equal(deliveredTitle(mappingChanged), "Delivered: mapping WellLog@1.4.0 changed (852db64); acl.viewers changed");
  });

  it("says nothing it does not know of a try delivered before the ledger kept reasons", () => {
    assert.equal(deliveredTitle({ recorded: false, summary: NOT_RECORDED }), "Delivered");
    assert.equal(deliveredTitle(undefined), "Delivered");
    assert.equal(deliveredTitle(null), "Delivered");
  });
});

describe("triggerSides", () => {
  it("names a mapping by its reference, fingerprint and commit on each side", () => {
    const trigger = mappingChanged.recorded ? mappingChanged.triggers[0] : undefined;
    assert.deepEqual(triggerSides(trigger!), {
      was: "WellLog@1.4.0 · 1f0c8b2e44d9 · commit 268b252",
      now: "WellLog@1.4.0 · 9a8b7c6d5e4f · commit 852db64",
    });
  });

  it("leaves out a commit the run did not know", () => {
    assert.deepEqual(triggerSides({ kind: "mapping-changed", from: "WellLog@1.4.0", to: "WellLog@1.5.0" }), { was: "WellLog@1.4.0", now: "WellLog@1.5.0" });
  });

  it("says a payload that had none was none", () => {
    assert.deepEqual(triggerSides({ kind: "payload-changed", to: "abcdef0123456789" }), { was: "none", now: "abcdef012345" });
  });

  it("shows a cache's versions as they are", () => {
    assert.deepEqual(triggerSides({ kind: "cache-changed", from: "20261117T020000Z", to: "20261121T020000Z" }), { was: "20261117T020000Z", now: "20261121T020000Z" });
  });
});

describe("triggerNames", () => {
  it("names who asked for a request an intervention made", () => {
    assert.equal(triggerNames({ kind: "requested", activity: "redeliver", actor: "user:ada" }), "redeliver by user:ada");
  });

  it("lists names and counts the rest", () => {
    assert.equal(triggerNames({ kind: "parameters-changed", names: ["legalTag", "restrictedAccess"], more: 3 }), "legalTag, restrictedAccess and 3 more");
    assert.equal(triggerNames({ kind: "cache-changed" }), null);
  });
});

describe("pathsHeading", () => {
  it("counts the paths that changed by kind", () => {
    assert.equal(pathsHeading({ compared: true, changed: 1, added: 2, removed: 0 }), "Paths: 1 changed, 2 added");
    assert.equal(pathsHeading({ compared: true, changed: 0, added: 0, removed: 0 }), "No path changed");
  });

  it("says why paths were not compared", () => {
    assert.equal(pathsHeading({ compared: false, why: "OSDU holds no version of the record to compare with" }), "Paths not compared: OSDU holds no version of the record to compare with");
  });
});

describe("deliveredByText", () => {
  it("counts a submission's deliveries by what sent them", () => {
    assert.equal(
      deliveredByText({ delivered: 11768, notRecorded: 0, triggers: [{ kind: "mapping-changed", records: 11768 }, { kind: "cache-changed", records: 2104 }] }),
      "11,768 delivered: mapping changed 11,768; cache changed 2,104",
    );
  });

  it("counts deliveries made before the ledger kept reasons apart", () => {
    assert.equal(deliveredByText({ delivered: 5, notRecorded: 5, triggers: [] }), "5 delivered: reason not recorded 5");
    assert.equal(deliveredByText({ delivered: 0, notRecorded: 0, triggers: [] }), "0 delivered");
  });
});

describe("triggerLabel", () => {
  it("labels every kind and keeps one it does not know as named", () => {
    assert.equal(triggerLabel("child-or-lookup-changed"), "Child or lookup row changed");
    assert.equal(triggerLabel("a-later-kind"), "a-later-kind");
  });
});
