// How a host's side bar lists the working copy (src/embed/workspaceListing.ts): by the sort a document is, and by
// what a typed term finds.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { WorkspaceDocument } from "../src/embed/protocol.ts";
import { documentMatches, folderOf, groupDocuments, groupOf, termWords } from "../src/embed/workspaceListing.ts";

function document(name: string, path: string, group: string): WorkspaceDocument {
  return { id: `file:///repo/${path}`, name, path, group, standing: "unknown", actions: [] };
}

const documents = [
  document("orders-load", "flows/sales/orders-load.yaml", "Ingestion flows"),
  document("customers-load", "flows/sales/customers-load.yaml", "Ingestion flows"),
  document("orders-export", "flows/finance/orders-export.yaml", "Export flows"),
  document("nightly", "schedules.yaml", "Schedules"),
];

describe("the working copy by sort", () => {
  it("lists every group in name order, each in path order", () => {
    const groups = groupDocuments(documents, "");
    assert.deepEqual(groups.map((group) => group.label), ["Export flows", "Ingestion flows", "Schedules"]);
    assert.deepEqual(groups[1].documents.map((entry) => entry.name), ["customers-load", "orders-load"]);
  });

  it("finds the group a document is in", () => {
    const groups = groupDocuments(documents, "");
    assert.equal(groupOf(groups, "file:///repo/schedules.yaml"), "Schedules");
    assert.equal(groupOf(groups, "file:///repo/missing.yaml"), null);
    assert.equal(groupOf(groups, null), null);
  });
});

describe("a typed term", () => {
  it("finds a document by the start of a word of its name or of its path", () => {
    const names = (term: string) => groupDocuments(documents, term).flatMap((group) => group.documents.map((entry) => entry.name));
    assert.deepEqual(names("ord"), ["orders-export", "orders-load"]);
    assert.deepEqual(names("load"), ["customers-load", "orders-load"]);
    assert.deepEqual(names("finance"), ["orders-export"]);
    assert.deepEqual(names("SALES ord"), ["orders-load"]);
  });

  it("does not find a word by its middle", () => {
    assert.equal(documentMatches(documents[0], termWords("rders")), false);
    assert.equal(documentMatches(documents[0], termWords("oad")), false);
  });

  it("leaves out a group it finds nothing in, and finds everything when blank", () => {
    assert.deepEqual(groupDocuments(documents, "nightly").map((group) => group.label), ["Schedules"]);
    assert.equal(groupDocuments(documents, "   ").length, 3);
    assert.deepEqual(groupDocuments(documents, "nothing-here"), []);
  });
});

describe("the folder a row shows", () => {
  it("is the path without the file, and nothing at the root", () => {
    assert.equal(folderOf("flows/sales/orders-load.yaml"), "flows/sales");
    assert.equal(folderOf("schedules.yaml"), "");
  });
});
