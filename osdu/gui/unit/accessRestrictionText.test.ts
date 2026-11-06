// What a page checks of an access restriction before it registers it, and says of one: the groups typed one per line,
// checked as storage checks an ACL entry, the name, and the groups in one line.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  aclText, groupsOf, groupsProblem, isDataGroup, MAX_RESTRICTION_GROUPS, nameProblem,
} from "../src/features/delivery/accessRestrictionText.ts";

describe("isDataGroup", () => {
  it("takes a data group's email as storage does", () => {
    assert.equal(isDataGroup("data.legal-hold@opendes.contoso.com"), true);
    assert.equal(isDataGroup("data.quarantine.owners@dev.example.com"), true);
  });

  it("refuses a group that is not a data group, or not an email", () => {
    assert.equal(isDataGroup("users.data.root@opendes.contoso.com"), false);
    assert.equal(isDataGroup("data.legal-hold"), false);
    assert.equal(isDataGroup("data.legal hold@opendes.contoso.com"), false);
    assert.equal(isDataGroup(""), false);
  });
});

describe("groupsOf", () => {
  it("reads one group a line, trimmed, blank lines left out", () => {
    assert.deepEqual(groupsOf("  data.a@x.com \n\n data.b@x.com\n"), ["data.a@x.com", "data.b@x.com"]);
  });

  it("also splits on commas and semicolons, and keeps each group once whatever its case", () => {
    assert.deepEqual(groupsOf("data.a@x.com, DATA.A@X.COM; data.b@x.com"), ["data.a@x.com", "data.b@x.com"]);
  });

  it("reads nothing from nothing", () => {
    assert.deepEqual(groupsOf("  \n "), []);
  });
});

describe("groupsProblem", () => {
  it("passes a list of data groups", () => {
    assert.equal(groupsProblem(["data.a@x.com"], "owners"), null);
  });

  it("refuses an empty list, naming which", () => {
    assert.match(groupsProblem([], "viewers") ?? "", /at least one group as its viewers/);
  });

  it("refuses more groups than a restriction keeps", () => {
    const many = Array.from({ length: MAX_RESTRICTION_GROUPS + 1 }, (_, i) => `data.g${i}@x.com`);
    assert.match(groupsProblem(many, "owners") ?? "", new RegExp(`At most ${MAX_RESTRICTION_GROUPS} groups`));
  });

  it("names each group storage would refuse", () => {
    assert.equal(
      groupsProblem(["data.a@x.com", "nobody", "users.x@x.com"], "owners"),
      "nobody, users.x@x.com are not data groups storage accepts in an ACL: data.<name>@<domain>.");
    assert.match(groupsProblem(["nobody"], "owners") ?? "", /^nobody is not a data group/);
  });
});

describe("nameProblem", () => {
  it("passes a name operators can pick", () => {
    assert.equal(nameProblem("legal-hold 2026"), null);
    assert.equal(nameProblem("  quarantine "), null);
  });

  it("refuses an empty name and one with other characters", () => {
    assert.match(nameProblem(" ") ?? "", /Name it/);
    assert.match(nameProblem("-hold") ?? "", /letter or digit/);
    assert.match(nameProblem("hold/2026") ?? "", /letter or digit/);
    assert.match(nameProblem("x".repeat(101)) ?? "", /at most 100/);
  });
});

describe("aclText", () => {
  it("says one list when viewers and owners are the same groups", () => {
    assert.equal(aclText(["data.a@x.com"], ["DATA.A@x.com"]), "data.a@x.com (viewers and owners)");
  });

  it("says each list when they differ", () => {
    assert.equal(aclText(["data.v@x.com", "data.w@x.com"], ["data.o@x.com"]), "viewers data.v@x.com, data.w@x.com; owners data.o@x.com");
  });
});
