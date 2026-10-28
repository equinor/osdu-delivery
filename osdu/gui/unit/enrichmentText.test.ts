// How a record's enrichments read on its Enrichments tab (osdu/docs/reference/flow/enrich.md, What the ledger keeps).

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { addedKeyText, addsDetail, addsText, rowsText, shortKind, writesText } from "../src/features/delivery/enrichmentText.ts";

describe("what an enrichment adds", () => {
  it("names its paths, records, bulk data and workflow run, in the order the ledger lists them", () => {
    assert.equal(addsText({ adds: ["records", "workflow"] }), "records, workflow run");
    assert.equal(addsText({ adds: ["paths"] }), "paths");
    assert.equal(addsText({ adds: ["bulk"] }), "bulk data");
    assert.equal(addsText({ adds: [] }), "nothing");
  });

  it("says the kind of the records it adds and how they are written, or where its paths and bulk data go", () => {
    assert.equal(
      addsDetail({ adds: ["records"], recordKind: "osdu:wks:dataset--File.Generic:1.0.0", writtenThrough: "dataset" }),
      "dataset--File.Generic:1.0.0 with files, via the Dataset service");
    assert.equal(
      addsDetail({ adds: ["records"], recordKind: "eqnr:wks:work-product-component--WellLogDetail:1.0.0", writtenThrough: "storage" }),
      "work-product-component--WellLogDetail:1.0.0 via storage");
    assert.equal(addsDetail({ adds: ["paths"], recordKind: null, writtenThrough: null }), "into the record itself");
    assert.equal(addsDetail({ adds: ["bulk"], recordKind: null, writtenThrough: null }), "streamed beside the record");
    assert.equal(addsDetail({ adds: ["workflow"], recordKind: null, writtenThrough: null }), null);
  });

  it("names a kind without its authority and source", () => {
    assert.equal(shortKind("osdu:wks:dataset--File.Generic:1.0.0"), "dataset--File.Generic:1.0.0");
    assert.equal(shortKind("not a kind"), "not a kind");
  });
});

describe("where an enrichment's rows came from", () => {
  it("names the one row by its file and row number", () => {
    assert.equal(rowsText({ count: 1, files: ["q.csv"], filesCounted: 1, row: "q.csv#118" }), "1 row: q.csv#118");
  });

  it("names the files many rows came from, and counts those it does not list", () => {
    assert.equal(rowsText({ count: 2, files: ["a.csv", "b.csv"], filesCounted: 2, row: null }), "2 rows from a.csv, b.csv");
    assert.equal(rowsText({ count: 40, files: ["a.csv"], filesCounted: 21, row: null }), "40 rows from a.csv and 20 more");
    assert.equal(rowsText({ count: 3, files: [], filesCounted: 0, row: null }), "3 rows");
  });

  it("is nothing for an enrichment reading no rows", () => {
    assert.equal(rowsText(null), null);
  });
});

describe("a record an enrichment added", () => {
  it("is told from the others by the start of its row key's hash, and needs none when the enrichment adds one", () => {
    assert.equal(addedKeyText("dev:dataset--File.Generic:5596f42c-enrich.lasfile.8f2c41d07a6b4e1c", "lasfile"), "8f2c41d0");
    assert.equal(addedKeyText("dev:work-product-component--WellLogDetail:5596f42c-enrich.detail", "detail"), null);
  });

  it("lists every write made of it, newest first, with when, what it replaced, its version and a try that did not deliver", () => {
    assert.equal(
      writesText([
        { atUtc: "2026-10-28T07:39:40Z", outcome: "delivered", version: 3, priorVersion: 2 },
        { atUtc: "2026-10-27T23:05:00Z", outcome: "held", version: null, priorVersion: 1 },
        { atUtc: "2026-10-27T23:01:12Z", outcome: "delivered", version: 1, priorVersion: null },
      ]),
      "2026-10-28 07:39 UTC: version 3, replacing 2\n"
        + "2026-10-27 23:05 UTC: no version answered, replacing 1; the record's try ended held\n"
        + "2026-10-27 23:01 UTC: created, version 1");
  });
});
