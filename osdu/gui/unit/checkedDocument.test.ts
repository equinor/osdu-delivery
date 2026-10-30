// What a data check needs of a record's document (osdu/docs/reference/concepts/gui.md, Mappings): where an attribute landed,
// the records a check lists for it, and the workbook a node answers with.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { columnsOfRow, pathsOfVariable, recordsOfFindings, sourceColumn, workbookBlob } from "../src/features/delivery/checkedDocument.ts";
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

describe("the records a check lists for an attribute", () => {
  it("are each listed once, in the order of its findings, with what names them and the child rows found wrong", () => {
    assert.deepEqual(
      recordsOfFindings([
        { outcome: "asserted", samples: [{ sourceKey: "a", label: "Log A", item: 2 }, { sourceKey: "a", item: 5 }, { sourceKey: "b" }] },
        { outcome: "empty", samples: [{ sourceKey: "b", label: "Log B", item: 1 }, { sourceKey: "a", item: 2 }, { sourceKey: "c", label: null }] },
      ]),
      [{ sourceKey: "a", label: "Log A", items: [2, 5] }, { sourceKey: "b", label: null, items: [1] }, { sourceKey: "c", label: null, items: [] }],
    );
  });

  it("leave out a row the mapping means no value for, which has nothing wrong with it", () => {
    assert.deepEqual(recordsOfFindings([{ outcome: "notApplicable", samples: [{ sourceKey: "a" }] }]), []);
    assert.deepEqual(recordsOfFindings([]), []);
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
