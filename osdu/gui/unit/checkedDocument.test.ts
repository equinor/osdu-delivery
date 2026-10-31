// What a data check needs of a record's document (osdu/docs/reference/concepts/gui.md, Mappings): where an attribute landed,
// the records a check lists for it, and the workbook a node answers with.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  branchesToFold,
  columnsOfRow,
  missingFromDocument,
  pathsOfVariable,
  recordsOfFinding,
  sourceColumn,
  workbookBlob,
} from "../src/features/delivery/checkedDocument.ts";
import { documentNode } from "../src/features/delivery/osduRecordModel.ts";

const document = {
  id: "dev:reference-data--LogCurveType:GR",
  kind: "osdu:wks:reference-data--LogCurveType:1.1.0",
  acl: { owners: ["data.default.owners@dev"] },
  data: {
    Name: "Gamma ray",
    NameAlias: [
      { AliasName: "GR", DefinitionOrganisationID: "dev:reference-data--StandardsOrganisation:Recall:" },
      { DefinitionOrganisationID: "dev:reference-data--StandardsOrganisation:Recall:" },
      { AliasName: "GAMMA", AliasNameTypeID: "dev:reference-data--AliasNameType:Mnemonic:" },
    ],
    Curves: [{ Points: [{ Depth: 1 }, { Depth: 2 }] }],
  },
};

describe("where an attribute landed in a document", () => {
  const root = documentNode(document);

  it("finds a value by its template path, the envelope's among them", () => {
    assert.deepEqual(pathsOfVariable(root, "osdu.data.Name"), ["data.Name"]);
    assert.deepEqual(pathsOfVariable(root, "osdu.acl.owners"), ["acl.owners"]);
  });

  it("finds a value of every item of a repeated array, and only the items that have it", () => {
    assert.deepEqual(pathsOfVariable(root, "osdu.data.NameAlias[].AliasName"), ["data.NameAlias[0].AliasName", "data.NameAlias[2].AliasName"]);
    assert.deepEqual(pathsOfVariable(root, "osdu.data.Curves[].Points[].Depth"), ["data.Curves[0].Points[0].Depth", "data.Curves[0].Points[1].Depth"]);
  });

  it("finds a group as the branch it is", () => {
    assert.deepEqual(pathsOfVariable(root, "osdu.data.NameAlias"), ["data.NameAlias"]);
  });

  it("finds nothing of a value the document does not hold, which is what a value left out comes to", () => {
    assert.deepEqual(pathsOfVariable(root, "osdu.data.NoSuchThing"), []);
    assert.deepEqual(pathsOfVariable(documentNode({ data: {} }), "osdu.data.NameAlias[].AliasName"), []);
  });
});

describe("where an attribute is left out of a document", () => {
  const root = documentNode(document);

  it("is each item of a repeated array without it, and only those", () => {
    assert.deepEqual([...missingFromDocument(root, "osdu.data.NameAlias[].AliasName")], [["data.NameAlias[1]", "AliasName"]]);
    assert.deepEqual(
      [...missingFromDocument(root, "osdu.data.NameAlias[].AliasNameTypeID")],
      [["data.NameAlias[0]", "AliasNameTypeID"], ["data.NameAlias[1]", "AliasNameTypeID"]],
    );
  });

  it("is the object that should hold it, naming what it lacks from there", () => {
    assert.deepEqual([...missingFromDocument(root, "osdu.data.Description")], [["data", "Description"]]);
    assert.deepEqual([...missingFromDocument(root, "osdu.data.Extra[].Name")], [["data", "Extra[].Name"]]);
  });

  it("is nowhere for a value every place holds, or a list with no items", () => {
    assert.deepEqual([...missingFromDocument(root, "osdu.data.Name")], []);
    assert.deepEqual([...missingFromDocument(documentNode({ data: { NameAlias: [] } }), "osdu.data.NameAlias[].AliasName")], []);
  });
});

describe("a document folded down to an attribute", () => {
  const root = documentNode(document);

  it("keeps open the way down to each place and folds the rest", () => {
    const fold = branchesToFold(root, ["data.NameAlias[0].AliasName", "data.NameAlias[2].AliasName"]);
    assert.equal(fold.has("data"), false);
    assert.equal(fold.has("data.NameAlias"), false);
    assert.equal(fold.has("data.NameAlias[0]"), false);
    assert.equal(fold.has("data.NameAlias[1]"), true);
    assert.equal(fold.has("data.NameAlias[2]"), false);
    assert.equal(fold.has("acl"), true);
    assert.equal(fold.has("data.Curves"), true);
  });

  it("keeps open what a place names itself, and never folds a leaf", () => {
    const fold = branchesToFold(root, ["data.NameAlias[1]"]);
    assert.equal(fold.has("data.NameAlias[1]"), false);
    assert.equal(fold.has("data.Name"), false);
  });
});

describe("the records a reason lists", () => {
  it("are each listed once, in the order they were met, with every example of them", () => {
    const first = { sourceKey: "a", label: "Log A", item: 2 };
    const second = { sourceKey: "b" };
    const third = { sourceKey: "a", item: 5 };
    assert.deepEqual(recordsOfFinding([first, second, third]), [
      { sourceKey: "a", samples: [first, third] },
      { sourceKey: "b", samples: [second] },
    ]);
    assert.deepEqual(recordsOfFinding([]), []);
  });
});

describe("the source columns an attribute reads", () => {
  it("read apart as the mapping names them", () => {
    assert.deepEqual(sourceColumn("dataset.curve"), { child: null, column: "curve" });
    assert.deepEqual(sourceColumn("dataset.aliases.alias_name"), { child: "aliases", column: "alias_name" });
  });

  it("come first in a row, in the order the attribute reads them, whatever their case, and the rest apart", () => {
    const row = { Project: "VALHALL", alias_type: "NoSuchType", alias_name: "DEPTH", not_read: null };
    assert.deepEqual(columnsOfRow(row, ["alias_name", "ALIAS_TYPE"]), { read: ["alias_name", "alias_type"], others: ["Project", "not_read"] });
  });

  it("leave the row as it is when the attribute reads none of them", () => {
    assert.deepEqual(columnsOfRow({ a: "1" }, []), { read: [], others: ["a"] });
    assert.deepEqual(columnsOfRow({ a: "1" }, ["no_such_column"]), { read: [], others: ["a"] });
  });
});

describe("the workbook a node answers with", () => {
  it("is the file's own bytes, typed as a workbook", async () => {
    const bytes = new Uint8Array([0x50, 0x4b, 0x03, 0x04, 0x00, 0xff, 0x80, 0x7f]);
    const blob = workbookBlob(Buffer.from(bytes).toString("base64"));
    assert.equal(blob.type, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    assert.deepEqual(new Uint8Array(await blob.arrayBuffer()), bytes);
  });
});
