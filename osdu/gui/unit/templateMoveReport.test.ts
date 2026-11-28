// The report on moving from one version of a kind to another, as the compare sheet takes it: its file is named as the
// control plane names it, and it reaches the clipboard while it is still being written, or says why it did not.

import assert from "node:assert/strict";
import { afterEach, describe, it } from "node:test";
import { copyPending, reportAbout, reportFileParts, weightCounts } from "../src/features/delivery/templateMoveReport.ts";

const FROM = { release: "v0.29.0", kind: "osdu:wks:master-data--Wellbore:1.0.0" };
const TO = { release: "v0.30.0", kind: "osdu:wks:master-data--Wellbore:1.3.0" };

describe("a report's name", () => {
  it("is the entity, then each side's schema version and release", () => {
    assert.deepEqual(reportFileParts(FROM, TO), ["Wellbore", "1.0.0", "v0.29.0", "to", "1.3.0", "v0.30.0", "report"]);
  });

  it("keeps an entity type without a group as it is", () => {
    assert.equal(reportFileParts({ release: "r", kind: "acme:wks:Thing:1.0.0" }, { release: "r", kind: "acme:wks:Thing:2.0.0" })[0], "Thing");
  });

  it("says the move it is about before it is taken", () => {
    assert.match(reportAbout(FROM, TO), /^Moving from 1\.0\.0 to 1\.3\.0, read against what each synced mapping of this kind writes: which changes are significant/);
  });
});

describe("a change's weight", () => {
  const changes = [
    { path: "a", weight: "Unused" as const },
    { path: "b", weight: "Significant" as const },
    { path: "c", weight: "Minor" as const },
    { path: "d", weight: "Significant" as const },
    { path: "e", weight: null },
  ];

  it("is counted per weight, a change nothing was weighed against counting as none", () => {
    assert.deepEqual(weightCounts(changes), { Significant: 2, Minor: 1, Unused: 1 });
  });
});

/** A clipboard that records what it was given, and refuses what it is told to. */
class FakeClipboard {
  written: string[] = [];
  refuseItems = false;
  refuseText = false;

  async write(items: { types: string[]; getType: (type: string) => Promise<Blob> }[]): Promise<void> {
    // A browser reads the pending item itself, so a text that could not be read fails the write.
    const blob = await items[0].getType("text/plain");
    if (this.refuseItems) {
      throw new Error("NotAllowedError: the pending item");
    }

    this.written.push(await blob.text());
  }

  async writeText(text: string): Promise<void> {
    if (this.refuseText) {
      throw new Error("NotAllowedError: the text");
    }

    this.written.push(text);
  }
}

/** A clipboard item as a browser holds one: each type's data, maybe still pending. */
class FakeClipboardItem {
  private readonly data: Record<string, Blob | Promise<Blob>>;

  constructor(data: Record<string, Blob | Promise<Blob>>) {
    this.data = data;
  }

  get types(): string[] {
    return Object.keys(this.data);
  }

  getType(type: string): Promise<Blob> {
    return Promise.resolve(this.data[type]);
  }
}

const globals = globalThis as unknown as { ClipboardItem?: unknown };

function useClipboard(clipboard: FakeClipboard, items: boolean) {
  Object.defineProperty(globalThis.navigator, "clipboard", { value: clipboard, configurable: true });
  if (items) {
    globals.ClipboardItem = FakeClipboardItem;
  } else {
    delete globals.ClipboardItem;
  }
}

describe("copying a report that is still being written", () => {
  afterEach(() => {
    delete globals.ClipboardItem;
    Reflect.deleteProperty(globalThis.navigator, "clipboard");
  });

  it("hands the clipboard the pending text, which arrives once it is read", async () => {
    const clipboard = new FakeClipboard();
    useClipboard(clipboard, true);
    await copyPending(Promise.resolve("# Moving Wellbore"));
    assert.deepEqual(clipboard.written, ["# Moving Wellbore"]);
  });

  it("writes the text itself where the clipboard refuses a pending item, or takes none", async () => {
    const refusing = new FakeClipboard();
    refusing.refuseItems = true;
    useClipboard(refusing, true);
    await copyPending(Promise.resolve("report"));
    assert.deepEqual(refusing.written, ["report"]);

    const plain = new FakeClipboard();
    useClipboard(plain, false);
    await copyPending(Promise.resolve("report"));
    assert.deepEqual(plain.written, ["report"]);
  });

  it("fails with why the report could not be read, and writes nothing", async () => {
    const clipboard = new FakeClipboard();
    useClipboard(clipboard, true);
    await assert.rejects(copyPending(Promise.reject(new Error("502 data definitions unavailable"))), /502 data definitions unavailable/);
    assert.deepEqual(clipboard.written, []);
  });

  it("fails with the clipboard's refusal when it takes neither", async () => {
    const clipboard = new FakeClipboard();
    clipboard.refuseItems = true;
    clipboard.refuseText = true;
    useClipboard(clipboard, true);
    await assert.rejects(copyPending(Promise.resolve("report")), /the text/);
  });
});
