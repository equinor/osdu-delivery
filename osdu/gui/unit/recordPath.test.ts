// How a path names a property whose name holds a dot, a bracket or a space (osdu/docs/reference/flow/mapping-property-names.md):
// in brackets and quotes, as the server writes it, while every other name is written as it always was.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { missingFromDocument, pathsOfVariable } from "../src/features/delivery/checkedDocument.ts";
import { issueHeadline, issueHolds } from "../src/features/delivery/issueText.ts";
import { documentNode } from "../src/features/delivery/osduRecordModel.ts";
import { valueAt } from "../src/features/delivery/overview/outlineModel.ts";
import {
  appendName, holderOfPath, inspectorPath, isPlainName, isWithin, itemSteps, lastStepStart, outlinePathOf, parsePath, pathNames,
  quoteName, repeaterOfPath, writePath,
} from "../src/features/delivery/recordPath.ts";

/** A backslash, built here so nothing between the test and the runtime reads an escape in it. */
const SLASH = String.fromCharCode(92);

describe("a name in a path", () => {
  it("is written bare only when it holds no dot, bracket or space", () => {
    for (const name of ["Name", "$name", "it's", "WellLog-1"]) {
      assert.equal(isPlainName(name), true, name);
    }

    for (const name of ["lra.dataID", "Data Owner", "a[0]", "a]b", "*", "", `a${String.fromCharCode(0xa0)}b`, `a${String.fromCharCode(0x2028)}b`]) {
      assert.equal(isPlainName(name), false, name);
    }
  });

  it("is quoted with the escapes the server writes", () => {
    assert.equal(quoteName("lra.dataID"), "['lra.dataID']");
    assert.equal(quoteName("it's"), `['it${SLASH}'s']`);
    assert.equal(quoteName(`a${SLASH}b`), `['a${SLASH}${SLASH}b']`);
    assert.equal(quoteName("line\nnext"), `['line${SLASH}nnext']`);
    assert.equal(quoteName(`bell${String.fromCharCode(7)}`), `['bell${SLASH}u0007']`);
  });

  it("is appended after a dot, or in brackets", () => {
    assert.equal(appendName("", "data"), "data");
    assert.equal(appendName("data", "Name"), "data.Name");
    assert.equal(appendName("osdu.tags", "lra.dataID"), "osdu.tags['lra.dataID']");
    assert.equal(appendName("data.Curves[]", "a b"), "data.Curves[]['a b']");
  });

  it("reads back as it was written", () => {
    for (const name of ["Name", "lra.dataID", "Data Owner", "a]b", "[x]", "it's", 'a"b', `back${SLASH}slash`, "tab\there", "*", "."]) {
      const path = appendName("tags", name);
      const parsed = parsePath(path);
      assert.deepEqual(parsed.steps, [{ kind: "property", name: "tags" }, { kind: "property", name }], path);
      assert.equal(writePath(parsed.steps ?? []), path);
    }
  });
});

describe("a path", () => {
  it("reads into names and the steps into arrays", () => {
    assert.deepEqual(parsePath("data.Curves[].CurveID").steps, [
      { kind: "property", name: "data" }, { kind: "property", name: "Curves" }, { kind: "items" }, { kind: "property", name: "CurveID" },
    ]);
    assert.deepEqual(parsePath("data.Curves[*][3]").steps, [
      { kind: "property", name: "data" }, { kind: "property", name: "Curves" }, { kind: "wildcard" }, { kind: "index", index: 3 },
    ]);
    for (const written of ["tags['lra.dataID']", 'tags["lra.dataID"]', "tags[ 'lra.dataID' ]", "tags.['lra.dataID']"]) {
      assert.deepEqual(pathNames(written), ["tags", "lra.dataID"], written);
    }

    assert.deepEqual(pathNames(`tags['a${SLASH}'b']`), ["tags", "a'b"]);
    assert.deepEqual(pathNames(`tags['${SLASH}u0041']`), ["tags", "A"]);
  });

  it("is refused, saying why, when it is not one", () => {
    const refusals: [string, string][] = [
      ["", "empty"], [".a", "empty property name"], ["a..b", "empty property name"], ["a.", "ends with a dot"], ["a['x'", "not closed"],
      ["a['x']b", "without a dot"], ["a[x]", "is not a step"], ["a]b", "closes no"], ["[0]", "starts with [0]"], ["a.b c", "holds a space"],
      ["a['']", "empty property name"], [`a['${SLASH}q']`, "is not an escape"],
    ];
    for (const [path, reason] of refusals) {
      const parsed = parsePath(path);
      assert.equal(parsed.steps, null, path);
      assert.match(parsed.error ?? "", new RegExp(reason.replace(/[[\]]/g, "\\$&")), path);
    }
  });

  it("lies within another at a step boundary only", () => {
    assert.equal(isWithin("osdu.tags['lra.dataID']", "osdu.tags"), true);
    assert.equal(isWithin("osdu.data.Curves[].Unit", "osdu.data.Curves"), true);
    assert.equal(isWithin("osdu.tagsX", "osdu.tags"), false);
  });

  it("names its holder, its last step and its array by its steps", () => {
    assert.equal(holderOfPath("osdu.tags['lra.dataID']"), "osdu.tags");
    assert.equal(holderOfPath("osdu.data.Curves[].Unit"), "osdu.data.Curves[]");
    assert.equal(holderOfPath("osdu"), null);
    assert.equal(lastStepStart("osdu.tags['lra.dataID']"), "osdu.tags".length);
    assert.equal(lastStepStart("osdu.tags['Source']"), "osdu.tags".length);
    assert.equal(lastStepStart("osdu.data.Name"), "osdu.data.".length);
    assert.equal(repeaterOfPath("osdu.data.Curves[]['a[]b']"), "osdu.data.Curves");
    assert.equal(repeaterOfPath("osdu.tags['a[]b']"), null);
    assert.equal(itemSteps("osdu.tags['a[]b']"), 0);
  });

  it("is the inspector's path a record view keys a node by", () => {
    assert.equal(inspectorPath("tags['lra.dataID']"), "tags.lra.dataID");
    assert.equal(inspectorPath("data.Curves[2].CurveID"), "data.Curves[2].CurveID");
  });

  it("of an outline loses only the place among alternatives outside a quoted name", () => {
    assert.equal(outlinePathOf("data.Name|2"), "data.Name");
    assert.equal(outlinePathOf("tags['a|b']"), "tags['a|b']");
    assert.equal(outlinePathOf("tags['a|b']|1"), "tags['a|b']");
  });
});

describe("a dotted tag where the views read it", () => {
  const record = { tags: { "lra.dataID": "6632", "Data Owner": "Ops", lra: { nested: "no" } }, data: { Curves: [{ "a.b": 1 }, {}] } };
  const root = documentNode(record);

  it("is found as the one key it is, never as nested objects", () => {
    assert.deepEqual(pathsOfVariable(root, "osdu.tags['lra.dataID']"), ["tags.lra.dataID"]);
    assert.deepEqual(pathsOfVariable(root, "osdu.tags['Data Owner']"), ["tags.Data Owner"]);
    assert.deepEqual(pathsOfVariable(root, "osdu.data.Curves[]['a.b']"), ["data.Curves[0].a.b"]);
    assert.deepEqual([...missingFromDocument(root, "osdu.data.Curves[]['a.b']")], [["data.Curves[1]", "a.b"]]);
    assert.deepEqual([...missingFromDocument(root, "osdu.tags['lra.other']")], [["tags", "lra.other"]]);
  });

  it("is read from a rendered record by an outline's path", () => {
    assert.equal(valueAt(record, "tags['lra.dataID']"), "6632");
    assert.equal(valueAt(record, "tags['lra.dataID']|1"), "6632");
    assert.equal(valueAt(record, "data.Curves[]['a.b']"), 1);
    assert.equal(valueAt(record, "tags.lra.dataID"), undefined);
  });

  it("heads a render's hold as the property it names", () => {
    assert.deepEqual(issueHeadline("osdu.tags['lra.dataID']: no LRA row: the record is held"), {
      property: "tags['lra.dataID']", scope: null, reason: "no LRA row: the record is held",
    });
    assert.deepEqual(issueHeadline("osdu.tags['Data: Owner, x']: empty"), { property: "tags['Data: Owner, x']", scope: null, reason: "empty" });
    assert.deepEqual(issueHolds("osdu.tags['a.b']: one; osdu.data.Name: two"), ["osdu.tags['a.b']: one", "osdu.data.Name: two"]);
  });
});
