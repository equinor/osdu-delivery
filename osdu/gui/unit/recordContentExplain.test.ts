// Record content never goes to the assistant's language model through Explain: every view that shows a source row, a
// rendered or previewed record, or cache values shows it through RecordContentView, which turns Explain off (decision
// 0046). A new code view of such content in these files fails here until it uses the wrapper too.

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, it } from "node:test";

const delivery = join(import.meta.dirname, "..", "src", "features", "delivery");

/** The views of record content, and how many each shows. */
const RECORD_CONTENT_VIEWS: Record<string, number> = {
  "DeliveryCacheHistory.tsx": 1,
  "DeliveryCacheRecords.tsx": 1,
  "overview/OverviewRendered.tsx": 1,
  "RecordPreviewView.tsx": 5,
  "RecordRenderTab.tsx": 1,
  "RecordSourceTab.tsx": 1,
};

describe("record content views", () => {
  for (const [file, views] of Object.entries(RECORD_CONTENT_VIEWS)) {
    it(`${file} shows record content only through RecordContentView`, () => {
      const source = readFileSync(join(delivery, file), "utf8");
      assert.equal(source.match(/<CodeView\b/g)?.length ?? 0, 0, `${file} renders a CodeView, which offers Explain`);
      assert.equal(source.match(/<RecordContentView\b/g)?.length ?? 0, views);
    });
  }

  it("RecordContentView turns Explain off, whatever its caller passes", () => {
    const source = readFileSync(join(delivery, "RecordContentView.tsx"), "utf8");
    assert.match(source, /Omit<ComponentProps<typeof CodeView>, "explain">/);
    assert.match(source, /<CodeView \{\.\.\.props\} explain=\{false\} \/>/);
  });
});
