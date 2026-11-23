// The Delivery page's filter (osdu/docs/reference/concepts/gui-ledger.md, Delivery): it narrows the flow cards to those
// whose card shows every word typed, and keeps the cards' order.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { elsewhereLine } from "../src/features/delivery/flowHealth.ts";
import { filteredLine, flowSearchText, matchingFlows, type SearchedFlow } from "../src/features/delivery/flowSearch.ts";
import { rowsMatching } from "../src/features/delivery/rowSearch.ts";

const logs: SearchedFlow = {
  name: "welldb_welllog_delivery",
  relativePath: "welldb/flows/welllog.delivery.yaml",
  batch: "welldb",
  active: true,
  partitions: ["dev"],
  headline: "3 failed",
};
const bores: SearchedFlow = {
  name: "welldb_wellbore_delivery",
  relativePath: "welldb/flows/wellbore.delivery.yaml",
  batch: null,
  active: false,
  partitions: ["dev", "test"],
  headline: "All 120 delivered",
};
const seismic: SearchedFlow = {
  name: "seismic_survey_delivery",
  relativePath: "seismic/flows/survey.delivery.yaml",
  batch: "seismic",
  active: true,
  partitions: ["prod"],
  headline: elsewhereLine(["prod"], "dev"),
};

const names = (search: string) => matchingFlows([logs, bores, seismic], search).map((flow) => flow.name);

describe("the Delivery page's filter", () => {
  it("shows every flow, in the cards' order, until something is typed", () => {
    assert.deepEqual(names(""), ["welldb_welllog_delivery", "welldb_wellbore_delivery", "seismic_survey_delivery"]);
    assert.deepEqual(names("   "), names(""));
  });

  it("finds a flow by part of its name, ignoring case", () => {
    assert.deepEqual(names("WELLBORE"), ["welldb_wellbore_delivery"]);
    assert.deepEqual(names("survey"), ["seismic_survey_delivery"]);
  });

  it("finds a flow by its path, its batch and the partitions it delivers to", () => {
    assert.deepEqual(names("welllog.delivery.yaml"), ["welldb_welllog_delivery"]);
    assert.deepEqual(names("seismic"), ["seismic_survey_delivery"]);
    assert.deepEqual(names("test"), ["welldb_wellbore_delivery"]);
    assert.deepEqual(names("prod"), ["seismic_survey_delivery"]);
  });

  it("finds a flow by the line under its name, so the ones that need someone are a word away", () => {
    assert.deepEqual(names("failed"), ["welldb_welllog_delivery"]);
    assert.deepEqual(names("delivered"), ["welldb_wellbore_delivery"]);
    assert.deepEqual(names("delivers to"), ["seismic_survey_delivery"]);
    assert.deepEqual(names("inactive"), ["welldb_wellbore_delivery"]);
  });

  it("needs every word typed, in any order", () => {
    assert.deepEqual(names("welldb dev"), ["welldb_welllog_delivery", "welldb_wellbore_delivery"]);
    assert.deepEqual(names("test welldb"), ["welldb_wellbore_delivery"]);
    assert.deepEqual(names("welldb prod"), []);
  });

  it("reads nothing a card does not show: the default batch and an unanswered count add no words", () => {
    const waiting: SearchedFlow = { ...bores, batch: null, active: true, partitions: [], headline: null };
    assert.equal(flowSearchText(waiting).trim(), "welldb_wellbore_delivery welldb/flows/wellbore.delivery.yaml");
  });

  it("says how many flows a filter shows", () => {
    assert.equal(filteredLine(2, 12, "welldb"), "2 of 12 delivery flows match 'welldb'.");
    assert.equal(filteredLine(1, 1, "logs"), "1 of 1 delivery flow match 'logs'.");
  });

  it("names where a flow that delivers elsewhere delivers, as its card does", () => {
    assert.equal(elsewhereLine(["prod", "test"], "dev"), "Delivers to prod, test, not dev");
    assert.equal(elsewhereLine(["prod"], null), "Delivers to prod");
  });
});

describe("a search over rows", () => {
  it("is the one rule the Mappings and Delivery pages share: every word in what the row shows", () => {
    const rows = ["Alpha beta", "beta gamma", "gamma alpha"];
    assert.deepEqual(rowsMatching(rows, "alpha", (row) => row), ["Alpha beta", "gamma alpha"]);
    assert.deepEqual(rowsMatching(rows, "a b", (row) => row), ["Alpha beta", "beta gamma"]);
    assert.deepEqual(rowsMatching(rows, "", (row) => row), rows);
  });
});
