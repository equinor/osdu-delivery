// A list of runs shows SQLFlow's row and file counts, unless the flow kind it lists counts something else: then the
// kind's own counts stand where SQLFlow's stood.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { withKindCounts } from "../src/features/runs/runCountColumns.ts";

const columns = ["status", "enqueued", "duration", "rowsLoaded", "rowsInserted", "rowsUpdated", "rowsDeleted", "fileCount", "commit"]
  .map((id) => ({ id }));
const counts = ["rowsLoaded", "rowsInserted", "rowsUpdated", "rowsDeleted", "fileCount"];
const kind = ["delivered", "held", "failed"].map((id) => ({ id }));

const ids = (list: readonly { id: string }[]) => list.map((column) => column.id);

describe("withKindCounts", () => {
  it("keeps the list as it is when the kind has no counts of its own", () => {
    assert.deepEqual(ids(withKindCounts(columns, counts, undefined)), ids(columns));
  });

  it("puts the kind's counts where SQLFlow's stood", () => {
    assert.deepEqual(ids(withKindCounts(columns, counts, kind)), ["status", "enqueued", "duration", "delivered", "held", "failed", "commit"]);
  });

  it("leaves the counts out when the kind gives none", () => {
    assert.deepEqual(ids(withKindCounts(columns, counts, [])), ["status", "enqueued", "duration", "commit"]);
  });

  it("takes the place of the first count when the counts are spread over the list", () => {
    const spread = ["status", "fileCount", "duration", "rowsLoaded", "commit"].map((id) => ({ id }));
    assert.deepEqual(ids(withKindCounts(spread, counts, kind)), ["status", "delivered", "held", "failed", "duration", "commit"]);
  });

  it("puts the kind's counts at the end of a list that holds none of SQLFlow's", () => {
    const bare = ["status", "commit"].map((id) => ({ id }));
    assert.deepEqual(ids(withKindCounts(bare, counts, kind)), ["status", "commit", "delivered", "held", "failed"]);
  });

  it("carries each column as it was given and leaves the lists it was given alone", () => {
    const given = [{ id: "status", header: "Status" }, { id: "rowsLoaded", header: "Rows loaded" }];
    const own = [{ id: "delivered", header: "Delivered" }];
    const listed = withKindCounts(given, counts, own);
    assert.equal(listed[0], given[0]);
    assert.equal(listed[1], own[0]);
    assert.deepEqual(ids(given), ["status", "rowsLoaded"]);
    assert.deepEqual(ids(own), ["delivered"]);
  });
});
