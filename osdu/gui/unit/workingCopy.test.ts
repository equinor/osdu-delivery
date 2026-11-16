// A mapping being written in an editor, placed in the estate (src/hosted/workingCopy.ts): what its text says it is, and
// which synced mapping it is the working copy of.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { declaredMapping, placeWorkingCopy, sameFile, textKey, type PlacedMapping } from "../src/hosted/workingCopy.ts";

const text = `# Mapping: well logs into the WellLog template.
documentType: mapping
name: WellLog
version: 1.4.0
template:
  kind: osdu:wks:work-product-component--WellLog:1.4.0
  version: 26a3c3441882db4f
description: Well logs of the well database.

dataset:
  kind: not-the-template's
`;

describe("what a mapping document declares", () => {
  it("is read from its root keys", () => {
    assert.deepEqual(declaredMapping(text), {
      name: "WellLog",
      version: "1.4.0",
      reference: "WellLog@1.4.0",
      kind: "osdu:wks:work-product-component--WellLog:1.4.0",
    });
  });

  it("is read with its quotes and comments off, and with Windows line ends", () => {
    const written = "name: \"Well\" # the master data\r\nversion: '1.3.0'\r\ntemplate:   # the kind it fills\r\n  kind: \"osdu:wks:master-data--Well:1.3.0\"\r\n";
    assert.deepEqual(declaredMapping(written), { name: "Well", version: "1.3.0", reference: "Well@1.3.0", kind: "osdu:wks:master-data--Well:1.3.0" });
  });

  it("is still read from a document half written", () => {
    assert.deepEqual(declaredMapping("name: Well\ntemplate:\n  kin"), { name: "Well", version: null, reference: null, kind: null });
    assert.deepEqual(declaredMapping(""), { name: null, version: null, reference: null, kind: null });
  });

  it("takes the kind under template, never one deeper in the document", () => {
    assert.equal(declaredMapping("name: A\nversion: 1.0.0\ndataset:\n  kind: other\n").kind, null);
  });
});

describe("the key a text is cached by", () => {
  it("moves when the text does and holds when it does not", () => {
    assert.equal(textKey(text), textKey(text));
    assert.notEqual(textKey(text), textKey(`${text} `));
    assert.notEqual(textKey("ab"), textKey("ba"));
    assert.match(textKey(""), /^working-copy-0-/);
  });
});

describe("one file seen from two roots", () => {
  it("is the same when one path is the tail of the other, segment by segment", () => {
    assert.equal(sameFile("welldb/mappings/Well@1.3.0.yaml", "welldb/mappings/Well@1.3.0.yaml"), true);
    assert.equal(sameFile("mappings/Well@1.3.0.yaml", "estate/welldb/mappings/Well@1.3.0.yaml"), true);
    assert.equal(sameFile("welldb\\mappings\\Well@1.3.0.yaml", "mappings/Well@1.3.0.yaml"), true);
    assert.equal(sameFile("mappings/Well@1.3.0.yaml", "othermappings/Well@1.3.0.yaml"), false);
    assert.equal(sameFile("mappings/Well@1.3.0.yaml", "mappings/Well@1.4.0.yaml"), false);
    assert.equal(sameFile("", "mappings/Well@1.3.0.yaml"), false);
  });
});

describe("a working copy among the estate's mappings", () => {
  const mappings: PlacedMapping[] = [
    { id: "m1", repoId: "r1", reference: "Well@1.3.0", relativePath: "welldb/mappings/Well@1.3.0.yaml" },
    { id: "m2", repoId: "r1", reference: "WellLog@1.4.0", relativePath: "welldb/mappings/WellLog@1.4.0.yaml" },
    { id: "m3", repoId: "r2", reference: "Well@1.3.0", relativePath: "other/mappings/Well@1.3.0.yaml" },
  ];

  it("is the working copy of the synced mapping at its path", () => {
    assert.deepEqual(placeWorkingCopy(mappings, "welldb/mappings/WellLog@1.4.0.yaml", "WellLog@1.4.0"), { synced: mappings[1], repoId: "r1" });
    // The repository is open from a folder above it, and from a folder inside it.
    assert.equal(placeWorkingCopy(mappings, "estates/welldb/mappings/Well@1.3.0.yaml", "Well@1.3.0").synced?.id, "m1");
    assert.equal(placeWorkingCopy(mappings, "mappings/WellLog@1.4.0.yaml", "WellLog@1.4.0").synced?.id, "m2");
  });

  it("stays that mapping's working copy when its name or version was edited since the sync", () => {
    assert.equal(placeWorkingCopy(mappings, "welldb/mappings/WellLog@1.4.0.yaml", "WellLog@1.5.0").synced?.id, "m2");
    assert.equal(placeWorkingCopy(mappings, "welldb/mappings/WellLog@1.4.0.yaml", null).synced?.id, "m2");
  });

  it("belongs to the repository its neighbours were synced from when the estate has not seen it", () => {
    assert.deepEqual(placeWorkingCopy(mappings, "welldb/mappings/Wellbore@1.0.0.yaml", "Wellbore@1.0.0"), { synced: null, repoId: "r1" });
    assert.deepEqual(placeWorkingCopy(mappings, "other/mappings/New@1.0.0.yaml", "New@1.0.0"), { synced: null, repoId: "r2" });
  });

  it("belongs to no repository when nothing of its folder was synced", () => {
    assert.deepEqual(placeWorkingCopy(mappings, "elsewhere/New@1.0.0.yaml", "New@1.0.0"), { synced: null, repoId: null });
    assert.deepEqual(placeWorkingCopy([], "welldb/mappings/Well@1.3.0.yaml", "Well@1.3.0"), { synced: null, repoId: null });
  });
});
