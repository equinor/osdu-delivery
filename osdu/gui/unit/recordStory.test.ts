// The rules a record's timeline is told by (osdu/docs/reference/concepts/record-lifecycle.md, A record's timeline).

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  actorInitial, actorName, fileSize, ingestionTableName, isArrival, parametersOf, reachedTarget, reachOf, redeliveryOf, refusalOf,
  runDuration, scopeOf, SET_ASIDE_BEFORE, setAside, statusText, type StoryActivity, type StoryAttempt,
} from "../src/features/delivery/recordStory.ts";

const REFUSED = "HTTP 409 Conflict from PUT https://osdu.example/api/storage/v2/records (attempt 1 of 3): the record already exists";

const attempt = (overrides: Partial<StoryAttempt>): StoryAttempt =>
  ({ worker: "node-1", outcome: "delivered", phase: "send", error: null, result: null, ...overrides });

const activity = (overrides: Partial<StoryActivity>): StoryActivity =>
  ({ kind: "release", deliveryKey: null, parametersJson: null, summary: null, ...overrides });

describe("what a try's error says OSDU answered", () => {
  it("reads the status, its reason in words, the request and the answer", () => {
    assert.deepEqual(refusalOf(REFUSED), {
      status: 409, reason: "Conflict", method: "PUT", url: "https://osdu.example/api/storage/v2/records", answer: "the record already exists",
    });
    assert.deepEqual(refusalOf("HTTP 400 BadRequest from POST https://osdu.example/api/search/v2/query"), {
      status: 400, reason: "Bad Request", method: "POST", url: "https://osdu.example/api/search/v2/query", answer: null,
    });
  });

  it("reads a status alone when the error names no request, and nothing from an error that is no answer", () => {
    assert.deepEqual(refusalOf("HTTP 503 while the service restarted"), { status: 503, reason: "", method: null, url: null, answer: null });
    assert.equal(refusalOf("connection refused"), null);
    assert.equal(refusalOf(null), null);
  });

  it("names the status with its reason when it has one", () => {
    assert.equal(statusText({ status: 400, reason: "Bad Request", method: null, url: null, answer: null }), "HTTP 400 Bad Request");
    assert.equal(statusText({ status: 503, reason: "", method: null, url: null, answer: null }), "HTTP 503");
  });
});

describe("whether a try reached OSDU", () => {
  it("is so for a delivery, a removal or a failure, and not for what the intake decided", () => {
    assert.equal(reachedTarget(attempt({ outcome: "delivered" })), true);
    assert.equal(reachedTarget(attempt({ outcome: "deleted" })), true);
    assert.equal(reachedTarget(attempt({ outcome: "failed" })), true);
    assert.equal(reachedTarget(attempt({ outcome: "skipped" })), false);
  });

  it("is so for a hold OSDU's answer caused, and not for a hold the intake made or an undo waiting", () => {
    assert.equal(reachedTarget(attempt({ outcome: "held", error: REFUSED })), true);
    assert.equal(reachedTarget(attempt({ outcome: "held", error: REFUSED, worker: "intake" })), false);
    assert.equal(reachedTarget(attempt({ outcome: "held", error: REFUSED, phase: "undo-wait" })), false);
    assert.equal(reachedTarget(attempt({ outcome: "held", error: "the wellbore is not in the cache" })), false);
  });

  it("is so for a lift only when OSDU held the record and its ACL was written back, and for any try that took a step", () => {
    assert.equal(reachedTarget(attempt({ outcome: "unrestricted", result: { restriction: { acl: { viewers: ["data.default.viewers"] } } } })), true);
    assert.equal(reachedTarget(attempt({ outcome: "unrestricted", result: { restriction: {} } })), false);
    assert.equal(reachedTarget(attempt({ outcome: "skipped", result: { steps: [{ name: "upload" }] } })), true);
  });
});

describe("whether a try set the record aside", () => {
  it("is so for a hold in the document-missing phase, or one whose error said so before the phase existed", () => {
    assert.equal(setAside(attempt({ outcome: "held", phase: "document-missing" })), true);
    assert.equal(setAside(attempt({ outcome: "held", phase: "none", error: `${SET_ASIDE_BEFORE} (blob 0:12:40)` })), true);
    assert.equal(setAside(attempt({ outcome: "held", phase: "none", error: "another reason" })), false);
    assert.equal(setAside(attempt({ outcome: "failed", phase: "document-missing" })), false);
  });
});

describe("what a request on a record reached", () => {
  it("reads the parameters the ledger stored, as an object or not at all", () => {
    assert.deepEqual(parametersOf(activity({ parametersJson: "{\"keys\":[\"a\",\"b\"]}" })), { keys: ["a", "b"] });
    assert.equal(parametersOf(activity({ parametersJson: "[1,2]" })), null);
    assert.equal(parametersOf(activity({ parametersJson: "not json" })), null);
    assert.equal(parametersOf(activity({ parametersJson: null })), null);
  });

  it("counts the records a request named: its keys, its count, or this record alone", () => {
    assert.equal(scopeOf(activity({}), { keys: ["a", "b", "c"] }), 3);
    assert.equal(scopeOf(activity({}), { count: 42 }), 42);
    assert.equal(scopeOf(activity({ deliveryKey: "6f1c2a9e-4b7d-4c1e-9a3f-2d8e5b7c1a40" }), null), 1);
    assert.equal(scopeOf(activity({}), null), null);
  });

  it("says a release of every blocked record reached this one, with how many when the summary says", () => {
    assert.deepEqual(reachOf(activity({ summary: "released 12 record(s) of welldb-wellbore-03-delivery" }), {}), {
      title: "Release asked for every blocked record, 12 records in all, this one among them", count: 12, pattern: null,
    });
    assert.deepEqual(reachOf(activity({}), null), {
      title: "Release asked for every blocked record, this one among them", count: null, pattern: null,
    });
  });

  it("names the issue's pattern for a release or an adoption of one issue's records", () => {
    assert.deepEqual(reachOf(activity({ summary: "released 1 record(s)" }), { issue: "3fa1c09b5d2e7a44", pattern: "osdu.tags.Tag4: <value> is empty" }), {
      title: "Release asked for 1 record one issue kept blocked, this one among them", count: 1, pattern: "osdu.tags.Tag4: <value> is empty",
    });
    assert.equal(
      reachOf(activity({ kind: "adopt", summary: "asked 3 record(s) to adopt" }), { every: true })?.title,
      "Adoption asked for every record held for an id OSDU holds, 3 records in all, this one among them");
  });

  it("says a redelivery or a request to bring every delivered record up to date reached this one", () => {
    assert.equal(
      reachOf(activity({ kind: "redeliver", summary: "marked every delivered record, 7 in all, for redelivery of the record" }), {})?.title,
      "Redelivery asked for every delivered record, 7 in all, this one among them");
    assert.equal(
      reachOf(activity({ kind: "rerender" }), {})?.title,
      "Bringing up to date asked for every delivered record, this one among them");
  });

  it("is nothing for a request that named its records, or this record, or one that reaches no others", () => {
    assert.equal(reachOf(activity({}), { keys: ["a"] }), null);
    assert.equal(reachOf(activity({ deliveryKey: "6f1c2a9e-4b7d-4c1e-9a3f-2d8e5b7c1a40" }), {}), null);
    assert.equal(reachOf(activity({ kind: "verify" }), {}), null);
  });

  it("says what a redelivery re-sends, by its parts or its scope", () => {
    assert.equal(redeliveryOf({ parts: ["files", "bulk"] }), "the files and bulk");
    assert.equal(redeliveryOf({ scope: "All" }), "everything: record and payload");
    assert.equal(redeliveryOf({ scope: "Metadata" }), "the record");
    assert.equal(redeliveryOf({ scope: "Payload" }), "the payload");
    assert.equal(redeliveryOf(null), null);
  });
});

describe("how times, sizes and names read", () => {
  it("gives a run's duration in milliseconds, seconds or minutes", () => {
    assert.equal(runDuration(0.25), "250 ms");
    assert.equal(runDuration(12.34), "12.3 s");
    assert.equal(runDuration(150), "2.5 min");
    assert.equal(runDuration(null), null);
  });

  it("gives a file's size in bytes, kilobytes or megabytes", () => {
    assert.equal(fileSize(512), "512 B");
    assert.equal(fileSize(2048), "2.0 KB");
    assert.equal(fileSize(5 * 1024 * 1024), "5.0 MB");
  });

  it("names the ingestion table by its schema and table, and a person by their name and its first letter", () => {
    assert.equal(ingestionTableName("[OsduData].[ing].[WellLogs]"), "ing.WellLogs");
    assert.equal(ingestionTableName(null), "the ingestion table");
    assert.equal(actorName("user:alice@example.com"), "alice@example.com");
    assert.equal(actorInitial("user:alice@example.com"), "A");
    assert.equal(actorInitial("cli:bob@workstation"), "B");
    assert.equal(actorInitial("schedule:nightly"), "N");
    assert.equal(actorInitial("unknown"), "?");
  });

  it("takes a row's insert, reinsert or earliest version as its arrival", () => {
    assert.deepEqual(["loaded", "reloaded", "earliest", "changed"].map((kind) => isArrival({ kind })), [true, true, true, false]);
  });
});
