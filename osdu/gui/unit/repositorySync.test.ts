// What the Delivery page's repository sync offers and follows (osdu/docs/reference/concepts/gui.md, Delivery): which
// repository Sync now is for, and the value that moves when a sync ends, so the page reads its flows again.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { pickedSource, syncsEnded, type SyncableSource } from "../src/features/delivery/repositorySync.ts";

function source(name: string, fields: Partial<SyncableSource> = {}): SyncableSource {
  return { id: `id-${name}`, name, enabled: true, lastSyncUtc: null, ...fields };
}

describe("pickedSource", () => {
  const sources = [source("alpha"), source("bravo"), source("charlie")];

  it("offers the repository picked", () => {
    assert.equal(pickedSource(sources, "bravo")?.name, "bravo");
  });

  it("offers the first repository while none was picked", () => {
    assert.equal(pickedSource(sources, null)?.name, "alpha");
  });

  it("offers the first repository when the one picked is gone", () => {
    assert.equal(pickedSource(sources, "removed")?.name, "alpha");
  });

  it("never offers a paused repository, which the control plane refuses to sync", () => {
    const paused = [source("alpha", { enabled: false }), source("bravo")];
    assert.equal(pickedSource(paused, "alpha")?.name, "bravo");
    assert.equal(pickedSource(paused, null)?.name, "bravo");
  });

  it("offers nothing while every repository is paused", () => {
    assert.equal(pickedSource([source("alpha", { enabled: false })], "alpha"), undefined);
  });
});

describe("syncsEnded", () => {
  const before = [source("alpha", { lastSyncUtc: "2026-10-30T10:00:00Z" }), source("bravo")];

  it("stays the same while no sync ends", () => {
    assert.equal(syncsEnded(before.map((item) => ({ ...item, syncPending: true }))), syncsEnded(before));
  });

  it("moves when a repository's sync ends", () => {
    const after = [before[0]!, { ...before[1]!, lastSyncUtc: "2026-10-30T10:05:00Z" }];
    assert.notEqual(syncsEnded(after), syncsEnded(before));
  });

  it("moves when a repository comes or goes", () => {
    assert.notEqual(syncsEnded(before.slice(0, 1)), syncsEnded(before));
    assert.notEqual(syncsEnded([...before, source("charlie")]), syncsEnded(before));
  });
});
