// What the Delivery page says of each delivery flow (osdu/docs/reference/concepts/gui-ledger.md, Delivery): the verdict a
// card leads with and where it sends a reader, the share in the ring's middle, the ring's arcs, the order the cards take,
// and the line of totals heading the page.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  attentionOrder, deliveredPercent, flowVerdict, overviewSubtitle, percentOf, ringArcs, statusShares, type FlowCounts,
} from "../src/features/delivery/flowHealth.ts";

const NONE: FlowCounts = {
  total: 0, pending: 0, delivering: 0, delivered: 0, held: 0, failed: 0, deleted: 0, waiting: 0, drifted: 0, lastSubmission: null,
};

function counts(over: Partial<FlowCounts>): FlowCounts {
  return { ...NONE, ...over };
}

const COMPLETED = { submissionId: "s1", status: "completed", error: null };

describe("flowVerdict", () => {
  it("says a flow with no records has none yet, quietly", () => {
    const verdict = flowVerdict(NONE);
    assert.equal(verdict.kind, "empty");
    assert.equal(verdict.tone, "muted");
    assert.equal(verdict.headline, "No records yet");
    assert.equal(verdict.target, null);
  });

  it("says every record is delivered, with the count, and sends nowhere", () => {
    const verdict = flowVerdict(counts({ total: 98, delivered: 98, lastSubmission: COMPLETED }));
    assert.equal(verdict.kind, "delivered");
    assert.equal(verdict.tone, "success");
    assert.equal(verdict.headline, "All 98 delivered");
    assert.equal(verdict.target, null);
  });

  it("counts removed records beside the delivered ones", () => {
    const verdict = flowVerdict(counts({ total: 100, delivered: 98, deleted: 2 }));
    assert.equal(verdict.headline, "All 98 delivered, 2 removed");
    assert.match(verdict.explanation, /2 were removed/);
  });

  it("puts a failed submission before everything, linking the submission and quoting its error", () => {
    const verdict = flowVerdict(counts({
      total: 10, failed: 3, held: 2, lastSubmission: { submissionId: "abc", status: "failed", error: "The record table is missing" },
    }));
    assert.equal(verdict.kind, "submission-failed");
    assert.equal(verdict.tone, "destructive");
    assert.deepEqual(verdict.target, { submissionId: "abc" });
    assert.match(verdict.explanation, /: The record table is missing\. The submission's page/);
  });

  it("names failed and held records together and sends to the Issues tab", () => {
    const verdict = flowVerdict(counts({ total: 300, failed: 3, held: 254, delivered: 43, lastSubmission: COMPLETED }));
    assert.equal(verdict.kind, "failed");
    assert.equal(verdict.headline, "3 failed, 254 held");
    assert.deepEqual(verdict.target, { tab: "issues" });
    assert.ok(verdict.rank < flowVerdict(counts({ total: 1, held: 1 })).rank);
  });

  it("says held records OSDU already holds are adoptable, and sends to the Delivery tab's Adopt when all are", () => {
    const all = flowVerdict(counts({ total: 254, held: 254, adoptable: 254 }));
    assert.equal(all.kind, "held");
    assert.equal(all.tone, "warning");
    assert.equal(all.headline, "254 held, all adoptable");
    assert.deepEqual(all.target, { tab: "delivery" });
    assert.match(all.explanation, /Every one is held because OSDU already holds a record/);

    const some = flowVerdict(counts({ total: 254, held: 254, adoptable: 4 }));
    assert.equal(some.headline, "254 held, 4 adoptable");
    assert.deepEqual(some.target, { tab: "issues" });
    assert.match(some.explanation, /4 of them are held/);

    const plain = flowVerdict(counts({ total: 10, held: 1, stranded: 1 }));
    assert.equal(plain.headline, "1 held");
    assert.match(plain.explanation, /1 record held back until its row changes/);
    assert.match(plain.explanation, /will not move by itself/);
  });

  it("puts drift after held records, sending to the drifted records", () => {
    const verdict = flowVerdict(counts({ total: 98, delivered: 98, drifted: 5 }));
    assert.equal(verdict.kind, "drifted");
    assert.equal(verdict.headline, "5 drifted since the last verify");
    assert.deepEqual(verdict.target, { tab: "records", drifted: true });
  });

  it("says a flow still delivering is moving, and how many are left", () => {
    const verdict = flowVerdict(counts({ total: 500, delivered: 160, pending: 300, delivering: 40 }));
    assert.equal(verdict.kind, "delivering");
    assert.equal(verdict.tone, "info");
    assert.equal(verdict.headline, "Delivering, 340 to go");
    assert.equal(verdict.moving, true);
    assert.deepEqual(verdict.target, { tab: "records", status: "pending" });

    const planning = flowVerdict(counts({ total: 0, lastSubmission: { submissionId: "s", status: "running", error: null } }));
    assert.equal(planning.headline, "Planning a submission");
    assert.equal(planning.moving, true);
    assert.equal(planning.target, null);
  });

  it("says what waits for the records it refers to", () => {
    const verdict = flowVerdict(counts({ total: 10, delivered: 8, waiting: 2 }));
    assert.equal(verdict.kind, "waiting");
    assert.equal(verdict.headline, "2 waiting for records they refer to");
    assert.deepEqual(verdict.target, { tab: "records", status: "waiting" });
  });

  it("says reverted records are blocked, without alarm", () => {
    const verdict = flowVerdict(counts({ total: 10, delivered: 9, reverted: 1 }));
    assert.equal(verdict.kind, "reverted");
    assert.equal(verdict.tone, "muted");
    assert.match(verdict.explanation, /It is blocked until its source changes/);
  });
});

describe("deliveredPercent", () => {
  it("has no share for a flow with no records", () => {
    assert.equal(deliveredPercent({ total: 0, delivered: 0 }), null);
  });

  it("never says 100% while a record is not delivered, and says <1% for a sliver", () => {
    assert.equal(deliveredPercent({ total: 1000, delivered: 999 }), "99%");
    assert.equal(deliveredPercent({ total: 1000, delivered: 1000 }), "100%");
    assert.equal(deliveredPercent({ total: 1000, delivered: 3 }), "<1%");
    assert.equal(deliveredPercent({ total: 254, delivered: 0 }), "0%");
  });

  it("rounds a part's share in a tooltip, with <1% for a sliver", () => {
    assert.equal(percentOf(1, 3), "33%");
    assert.equal(percentOf(1, 1000), "<1%");
    assert.equal(percentOf(0, 0), "0%");
  });
});

describe("ringArcs", () => {
  const C = 100;

  it("draws one status as the whole circle", () => {
    assert.deepEqual(ringArcs(statusShares(counts({ total: 98, delivered: 98 })), C, 2, 3), [{ key: "delivered", start: 0, length: C }]);
  });

  it("draws nothing for a flow with no records", () => {
    assert.deepEqual(ringArcs(statusShares(NONE), C, 2, 3), []);
  });

  it("shares the circle by count, with a gap between arcs, in the order a record moves", () => {
    const arcs = ringArcs(statusShares(counts({ total: 100, delivered: 50, held: 50 })), C, 2, 3);
    assert.deepEqual(arcs.map((arc) => arc.key), ["delivered", "held"]);
    assert.equal(arcs[0]!.length, 48);
    assert.equal(arcs[1]!.length, 48);
    assert.equal(arcs[1]!.start, arcs[0]!.start + 48 + 2);
    const drawn = arcs.reduce((sum, arc) => sum + arc.length, 0) + 2 * arcs.length;
    assert.ok(Math.abs(drawn - C) < 1e-9);
  });

  it("keeps a single failure among thousands visible", () => {
    const arcs = ringArcs(statusShares(counts({ total: 10001, delivered: 10000, failed: 1 })), C, 2, 3);
    const failed = arcs.find((arc) => arc.key === "failed")!;
    assert.equal(failed.length, 3);
    const drawn = arcs.reduce((sum, arc) => sum + arc.length, 0) + 2 * arcs.length;
    assert.ok(Math.abs(drawn - C) < 1e-9);
  });
});

describe("attentionOrder", () => {
  it("puts the worst first, keeps the order among equals, and the flows of other partitions last", () => {
    // 0 delivered, 1 held, 2 elsewhere, 3 failed, 4 delivered, 5 not answered.
    assert.deepEqual(attentionOrder([6, 1, null, 0, 6, null], [false, false, true, false, false, false]), [3, 1, 0, 4, 5, 2]);
  });
});

describe("overviewSubtitle", () => {
  // Counts are grouped as the machine's locale groups them, as the page draws them.
  const n = (value: number) => value.toLocaleString();
  const totals = { total: 12901, delivered: 10231, pending: 0, held: 2670, failed: 0, drifted: 0 };

  it("says the share delivered and only the counts that ask for someone", () => {
    assert.equal(
      overviewSubtitle(10, "dev", totals, 1),
      `10 delivery flows in dev: 79% of ${n(12901)} records delivered; ${n(2670)} held. 1 flow needs attention, listed first.`,
    );
  });

  it("reads as one fact when everything is delivered", () => {
    assert.equal(
      overviewSubtitle(1, null, { ...totals, delivered: 12901, held: 0 }, 0),
      `1 delivery flow: all ${n(12901)} records delivered.`,
    );
  });

  it("says when the flows hold no records", () => {
    assert.equal(overviewSubtitle(2, "dev", { ...totals, total: 0, delivered: 0, held: 0 }, 0), "2 delivery flows in dev, holding no records yet.");
  });
});
