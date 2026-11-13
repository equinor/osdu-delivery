// How the explorer arranges a partition's kinds: a partition's own kinds that name no group (acme:wells:Completion:1.0.0)
// are a group of their own by authority and source, and picking that group or one of its types asks a kind pattern the
// search finds them by; the address reads every place back as it was written; a kind the Schema service lists with no
// records is a row like any other, and says so on hover.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  groupOf, isCustomGroup, kindInScope, kindParts, kindTitle, sameScope, scopeKind, scopeLabel, scopeOf, typeScopeOf, typeTree, type ExplorerScope,
} from "../src/features/delivery/explorer/explorerKinds.ts";
import { isRecordIdIn, isRecordReference } from "../src/features/delivery/osduDocument.ts";

const kinds = [
  { kind: "osdu:wks:master-data--Wellbore:1.1.0", count: 40, status: "PUBLISHED", scope: "SHARED" },
  { kind: "osdu:wks:master-data--Wellbore:1.0.0", count: 3, status: "PUBLISHED", scope: "SHARED" },
  { kind: "acme:governance:data-governance--AccessGroupMap:1.0.0", count: 0, status: "PUBLISHED", scope: "INTERNAL" },
  { kind: "acme:wells:Completion:1.0.0", count: 12, status: "PUBLISHED", scope: "INTERNAL" },
  { kind: "acme:wells:StationReading:1.0.0", count: 0, status: "DEVELOPMENT", scope: "INTERNAL" },
  { kind: "acme:wells:SurveyRun:1.0.0", count: 5 },
];

describe("typeTree", () => {
  it("lists OSDU's groups first, then the custom ones: the groups OSDU does not name, then the namespaces of types naming none", () => {
    const tree = typeTree([...kinds, { kind: "osdu:wks:reference-data--UnitOfMeasure:1.0.0", count: 9 }]);
    assert.deepEqual(tree.map((g) => [g.group, g.custom, g.namespace, g.count]), [
      ["master-data", false, false, 43],
      ["reference-data", false, false, 9],
      ["data-governance", true, false, 0],
      ["acme:wells", true, true, 17],
    ]);

    const space = tree[3];
    assert.deepEqual(space.types.map((t) => [t.type, t.count]), [["Completion", 12], ["StationReading", 0], ["SurveyRun", 5]]);
    assert.deepEqual(space.types[0].scope, { level: "type", entityType: "Completion", namespace: "acme:wells" });
    assert.equal(scopeKind(space.types[0].scope), "acme:wells:Completion:*");
    assert.equal(scopeKind({ level: "group", group: space.group }), "acme:wells:*:*");

    // A type naming a group is that type in every namespace, as before.
    assert.deepEqual(tree[0].types[0].scope, { level: "type", entityType: "master-data--Wellbore" });
    assert.deepEqual(tree[0].types[0].kinds.map((k) => k.kind), ["osdu:wks:master-data--Wellbore:1.0.0", "osdu:wks:master-data--Wellbore:1.1.0"]);
  });

  it("keeps a partition's own kind of an OSDU type under that type, not under Custom", () => {
    const tree = typeTree([...kinds, { kind: "acme:custom:master-data--Wellbore:1.0.0", count: 2 }]);
    const master = tree.find((g) => g.group === "master-data")!;
    assert.equal(master.custom, false);
    assert.deepEqual(master.types[0].kinds.map((k) => k.kind), [
      "acme:custom:master-data--Wellbore:1.0.0", "osdu:wks:master-data--Wellbore:1.0.0", "osdu:wks:master-data--Wellbore:1.1.0",
    ]);
    assert.ok(!isCustomGroup("work-product") && !isCustomGroup("dataset") && isCustomGroup("data-governance-draft"));
  });

  it("counts a namespace by every kind of its authority and source, as its pattern finds them", () => {
    const tree = typeTree([...kinds, { kind: "acme:wells:work-product-component--WellTest:1.0.0", count: 4 }]);
    const space = tree.find((g) => g.group === "acme:wells")!;
    assert.equal(space.count, 21);
    assert.equal(space.types.length, 3);
    assert.ok(tree.some((g) => g.group === "work-product-component"));
  });

  it("keeps a schema's status and scope on its kind, and none where the Schema service listed none", () => {
    const space = typeTree(kinds).find((g) => g.group === "acme:wells")!;
    assert.deepEqual([space.types[1].kinds[0].status, space.types[1].kinds[0].scope], ["DEVELOPMENT", "INTERNAL"]);
    assert.deepEqual([space.types[2].kinds[0].status, space.types[2].kinds[0].scope], [null, null]);
  });
});

describe("scopeOf", () => {
  const places: ExplorerScope[] = [
    { level: "all" },
    { level: "group", group: "master-data" },
    { level: "group", group: "acme:wells" },
    { level: "type", entityType: "master-data--Wellbore" },
    { level: "type", entityType: "Completion", namespace: "acme:wells" },
    { level: "type", entityType: "Completion" },
    { level: "kind", kind: "acme:wells:Completion:1.0.0" },
    { level: "kind", kind: "osdu:wks:master-data--Wellbore:1.*" },
  ];

  it("reads every place back from the pattern the address keeps", () => {
    for (const place of places) {
      assert.deepEqual(scopeOf(scopeKind(place) ?? null), place, JSON.stringify(place));
    }
  });

  it("takes a pattern naming the namespace of a type that names a group as a kind pattern the explorer did not write", () => {
    assert.deepEqual(scopeOf("osdu:wks:master-data--Wellbore:*"), { level: "kind", kind: "osdu:wks:master-data--Wellbore:*" });
  });

  it("tells a type within its namespace apart from the same type in every namespace", () => {
    assert.equal(sameScope({ level: "type", entityType: "Completion", namespace: "acme:wells" }, { level: "type", entityType: "Completion" }), false);
    assert.equal(scopeLabel({ level: "type", entityType: "Completion", namespace: "acme:wells" }), "Completion");
    assert.equal(scopeLabel({ level: "group", group: "acme:wells" }), "acme:wells");
  });
});

describe("typeScopeOf", () => {
  it("places a kind naming no group in its namespace, and one naming a group in every namespace", () => {
    assert.deepEqual(typeScopeOf("acme:wells:Completion:1.0.0"), { level: "type", entityType: "Completion", namespace: "acme:wells" });
    assert.deepEqual(typeScopeOf("acme:custom:master-data--Wellbore:1.0.0"), { level: "type", entityType: "master-data--Wellbore" });
    assert.deepEqual(typeScopeOf("*:*:*:*"), { level: "all" });
    assert.equal(groupOf(kindParts("*:*:Completion:*")), "other");
  });
});

describe("kindInScope", () => {
  it("says whether a place's pattern finds a kind", () => {
    assert.ok(kindInScope("acme:wells:Completion:1.0.0", { level: "type", entityType: "Completion", namespace: "acme:wells" }));
    assert.ok(!kindInScope("other:src:Completion:1.0.0", { level: "type", entityType: "Completion", namespace: "acme:wells" }));
    assert.ok(kindInScope("other:src:Completion:1.0.0", { level: "type", entityType: "Completion" }));
    assert.ok(kindInScope("osdu:wks:master-data--Wellbore:1.1.0", { level: "kind", kind: "osdu:wks:master-data--Wellbore:1.*" }));
    assert.ok(!kindInScope("osdu:wks:master-data--Wellbore:2.0.0", { level: "kind", kind: "osdu:wks:master-data--Wellbore:1.*" }));
  });
});

describe("kindTitle", () => {
  it("says the kind, its schema's status and scope, and when the search finds none of its records", () => {
    assert.equal(
      kindTitle({ kind: "acme:wells:StationReading:1.0.0", count: 0, status: "DEVELOPMENT", scope: "INTERNAL" }),
      "acme:wells:StationReading:1.0.0\nDevelopment in the Schema service, the partition's own.\nThe search finds no record of it: none is stored, or none this connection may read.");
    assert.equal(
      kindTitle({ kind: "x:y:Z:1.0.0", count: 2, status: null, scope: null }),
      "x:y:Z:1.0.0\nNo schema of it is listed by the Schema service.");
  });
});

describe("isRecordIdIn", () => {
  it("opens an id of a type naming no group in the partition read, as the storage service takes it", () => {
    assert.ok(isRecordIdIn("dev:site-note:3b9c1f0e5a7d4e2b9c8f6a1d0e7b4c25", "dev"));
    assert.ok(isRecordIdIn("dev:site-note:3b9c1f0e5a7d4e2b9c8f6a1d0e7b4c25:1712345678901234", "dev"));
    assert.ok(isRecordIdIn("dev:master-data--Wellbore:WB-1", "other"));
    // Elsewhere three colon-separated parts may be any text, and a reference inside a record stays as strict as before.
    assert.ok(!isRecordIdIn("dev:site-note:3b9c", "prod"));
    assert.ok(!isRecordIdIn("dev:site-note:3b9c", null));
    assert.ok(!isRecordIdIn("dev:site note:3b9c", "dev"));
    assert.ok(!isRecordIdIn("dev:site-note", "dev"));
    assert.ok(!isRecordReference("dev:site-note:3b9c"));
  });
});
