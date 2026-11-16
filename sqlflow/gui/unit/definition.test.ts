// What a pipeline page reads from a stored definition: the derived health check of an ingestion flow, and the step a
// lineage description describes.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { describedStepFacts, embeddedHealthCheckName } from "../src/lib/definition.ts";
import { DESCRIPTION_HIDDEN_FACTS, DESCRIPTION_HIDDEN_TABS } from "../src/features/pipelines/pipelineTabs.ts";

describe("describedStepFacts", () => {
  // The definition a catalog sync stores for a lineage description: its document, parsed.
  const stored = JSON.stringify({
    flow: {
      name: "orders-u1-curate-lin",
      system: "lakehouse",
      unit: "job orders-daily, task curate_orders",
      owner: "data-platform@example.com",
      reads: [{ system: "erp", name: "sales.orders" }, { system: "erp", name: "sales.rates" }],
      writes: [
        { name: "curated.orders", columns: [{ name: "order_id" }, { name: "amount_eur" }] },
        { name: "curated.rejects", columns: [] },
      ],
    },
  });

  it("says who runs the step, where it is found, who to ask, and what it connects", () => {
    assert.deepEqual(describedStepFacts(stored), {
      system: "lakehouse",
      unit: "job orders-daily, task curate_orders",
      owner: "data-platform@example.com",
      reads: 2,
      writes: 2,
      mappedColumns: 2,
    });
  });

  it("leaves out what the document does not say", () => {
    const facts = describedStepFacts(JSON.stringify({ flow: { system: "manual", unit: " ", writes: [{ name: "export.csv" }] } }));
    assert.deepEqual(facts, { system: "manual", unit: null, owner: null, reads: 0, writes: 1, mappedColumns: 0 });
  });

  it("answers nothing for a definition that is not a description's, or does not read", () => {
    assert.equal(describedStepFacts("{}"), null);
    assert.equal(describedStepFacts(JSON.stringify({ document: { healthCheck: { sysAlias: "hc" } } })), null);
    assert.equal(describedStepFacts(JSON.stringify({ flow: "text" })), null);
    assert.equal(describedStepFacts(""), null);
    assert.equal(describedStepFacts("{ not json"), null);
  });
});

describe("a lineage description's page", () => {
  const builtIn = ["yaml", "transforms", "runs", "files", "schedules", "definition"];

  it("keeps the tabs that hold its document, and drops the ones a run fills", () => {
    assert.deepEqual(builtIn.filter((tab) => !DESCRIPTION_HIDDEN_TABS.includes(tab)), ["yaml", "definition"]);
  });

  it("drops the facts of a flow that runs: its wave and the servers it connects to", () => {
    assert.deepEqual([...DESCRIPTION_HIDDEN_FACTS].sort(), ["sourceServer", "targetServer", "wave"]);
  });
});

describe("embeddedHealthCheckName", () => {
  it("reads the derived flow of an ingestion document, and nothing from any other", () => {
    assert.equal(embeddedHealthCheckName(JSON.stringify({ document: { healthCheck: { sysAlias: "orders-hc" } } })), "orders-hc");
    assert.equal(embeddedHealthCheckName(JSON.stringify({ flow: { system: "lakehouse" } })), null);
    assert.equal(embeddedHealthCheckName("{ not json"), null);
  });
});
