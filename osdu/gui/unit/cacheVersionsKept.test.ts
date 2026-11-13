// How many versions of a partition's cache keep their records, as the Cache page reads a typed number and says the history kept.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  DEFAULT_VERSIONS_KEPT, MAX_VERSIONS_KEPT, MIN_VERSIONS_KEPT, historyKeptText, versionsKeptOf, versionsText,
} from "../src/features/delivery/cacheVersionsKept.ts";

describe("versionsKeptOf", () => {
  it("takes a whole number from two to a thousand", () => {
    assert.equal(versionsKeptOf("3"), 3);
    assert.equal(versionsKeptOf(" 12 "), 12);
    assert.equal(versionsKeptOf(String(MIN_VERSIONS_KEPT)), 2);
    assert.equal(versionsKeptOf(String(MAX_VERSIONS_KEPT)), 1000);
  });

  it("refuses what is not a number a partition may keep", () => {
    for (const typed of ["", "1", "0", "1001", "-3", "2.5", "3x", "10000"]) {
      assert.equal(versionsKeptOf(typed), null, typed);
    }
  });
});

describe("historyKeptText", () => {
  it("says the newest versions, the days a replaced one keeps, and what becomes of the rest", () => {
    assert.equal(
      historyKeptText(DEFAULT_VERSIONS_KEPT, 7),
      "The newest 3 versions keep their records, a replaced one for up to 7 days; older ones are pruned by the next refresh and stay listed with what they changed.");
    assert.equal(historyKeptText(1000, 1).startsWith("The newest 1,000 versions keep their records, a replaced one for up to 1 day;"), true);
  });

  it("counts a version as one", () => {
    assert.equal(versionsText(1), "1 version");
    assert.equal(versionsText(2), "2 versions");
  });
});
