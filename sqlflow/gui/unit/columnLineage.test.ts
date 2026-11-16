// What "Explain its lineage" asks the assistant about a column: the trace back to its source, how each step is known, and
// what lineage does not say, named as missing rather than filled in.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { columnLineageAsk, columnLineageQuestion, type LineageColumn } from "../src/features/chat/columnLineage.ts";
import { EXPLAIN_HEADING, askHeading } from "../src/features/chat/explain.ts";

const COLUMN: LineageColumn = { column: "customer_id", object: "ods.Orders", objectKey: "${env:data_db}|data|ods|orders" };

describe("columnLineageAsk", () => {
  it("is headed Explain, about the column's lineage, and opens wide", () => {
    const ask = columnLineageAsk(COLUMN);
    assert.equal(ask.subject, "the lineage of customer_id");
    assert.equal(askHeading(ask), EXPLAIN_HEADING);
    assert.equal(ask.wide, true);
    assert.equal(ask.question, columnLineageQuestion(COLUMN));
  });
});

describe("columnLineageQuestion", () => {
  it("asks where the column of the object comes from, and names the trace by the object's key and the column", () => {
    const question = columnLineageQuestion(COLUMN);
    assert.ok(question.startsWith(
      "Where does the value of the column `customer_id` of `ods.Orders` come from, back to its source, and what happens to it on the way?\n\n",
    ), question);
    assert.ok(question.includes(
      "\n\nTrace it with trace_column (object key `${env:data_db}|data|ods|orders`, column `customer_id`), then tell me:\n\n",
    ), question);
  });

  it("asks for the path, how each step is known, and what is missing, each as a point of its own", () => {
    const question = columnLineageQuestion(COLUMN);
    assert.match(question, /\n1\. The path from the source to this column, one step per line: /);
    assert.match(question, /\n2\. For each step, how it is known: stated by a lineage description, declared by a flow, or only assumed because the column keeps its name\.\n/);
    assert.match(question, /\n3\. What is missing: where the trace ends without reaching a source, or a step is described without this column, say so, and say what would supply it\.\n/);
  });

  it("asks for a gap to be said as missing, never filled from a name", () => {
    assert.ok(columnLineageQuestion(COLUMN).endsWith(
      "\n\nDo not fill a gap from the names or from general knowledge. "
        + "If nothing describes where the data comes from before the estate reads it, say that the information is missing.",
    ));
  });

  it("marks a name holding backticks with a longer run, set off by a space where it starts or ends with one", () => {
    const question = columnLineageQuestion({ column: "net`amount", object: "`ods`.`Orders`", objectKey: "db|ods|ord``ers" });
    assert.ok(question.startsWith("Where does the value of the column ``net`amount`` of `` `ods`.`Orders` `` come from,"), question);
    assert.ok(question.includes("(object key ```db|ods|ord``ers```, column ``net`amount``)"), question);
  });
});
