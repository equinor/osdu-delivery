// Explain and Copy over the mapping's YAML in the mapping overview (osdu/docs/reference/concepts/mapping-overview.md): the
// lines asked about, each whole, or the whole mapping, numbered as the pane numbers them.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { explainedYaml, linesLabel, yamlLines } from "../src/features/delivery/overview/yamlExplain.ts";

const MAPPING = "kind: osdu:wks:master-data--Wellbore:1.3.0\nsource:\n  table: wells\ndata:\n  FacilityName: name\n";

describe("yamlLines", () => {
  it("numbers the lines as the pane does, a file's last line break leaving an empty last line", () => {
    assert.deepEqual(yamlLines("a: 1\nb: 2\n"), ["a: 1", "b: 2", ""]);
  });

  it("reads a file written with Windows line breaks as the same lines", () => {
    assert.deepEqual(yamlLines("a: 1\r\nb: 2"), ["a: 1", "b: 2"]);
  });
});

describe("explainedYaml", () => {
  it("asks about the whole mapping when no lines are selected", () => {
    assert.deepEqual(explainedYaml(MAPPING, null), { code: MAPPING, language: "yaml", lines: null, totalLines: 6 });
  });

  it("asks about the lines selected, each whole, and says which they are of how many", () => {
    assert.deepEqual(explainedYaml(MAPPING, { start: 2, end: 3 }), {
      code: "source:\n  table: wells",
      language: "yaml",
      lines: { start: 2, end: 3 },
      totalLines: 6,
    });
    assert.equal(explainedYaml(MAPPING, { start: 5, end: 5 }).code, "  FacilityName: name");
  });

  it("reads a range given backwards in order, and keeps one running past the mapping inside it", () => {
    assert.deepEqual(explainedYaml(MAPPING, { start: 3, end: 2 }).lines, { start: 2, end: 3 });
    assert.deepEqual(explainedYaml(MAPPING, { start: 4, end: 40 }).lines, { start: 4, end: 6 });
    assert.deepEqual(explainedYaml(MAPPING, { start: 0, end: 1 }).lines, { start: 1, end: 1 });
  });

  it("sends a Windows file's lines without their carriage returns", () => {
    assert.equal(explainedYaml("a: 1\r\nb: 2\r\n", null).code, "a: 1\nb: 2\n");
  });
});

describe("linesLabel", () => {
  it("names one line or a range of them", () => {
    assert.equal(linesLabel({ start: 12, end: 12 }), "Line 12");
    assert.equal(linesLabel({ start: 12, end: 15 }), "Lines 12 to 15");
  });
});
