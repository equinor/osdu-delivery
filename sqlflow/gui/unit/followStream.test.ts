// How a live stream is followed through its connection closing or failing: again after a wait that grows while it keeps
// failing, after the page's pause on a 429, and never again after a refusal asking again cannot change.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  FIRST_RECONNECT_MS, followStream, LONGEST_RECONNECT_MS, reconnectAfter, refusalText, type StreamFollowing,
} from "../src/api/followStream.ts";

/** One attempt of a scripted stream: what it does once opened, or the failure it throws. */
type Step = { opens: boolean; ends: boolean } | { throws: { status?: number; message: string } };

function scripted(steps: Step[], onSleep?: (ms: number) => void) {
  const log = { sleeps: [] as number[], pauses: 0, connected: [] as boolean[], attempts: 0 };
  const following: StreamFollowing = {
    attempt: async (_signal, onOpen) => {
      const step = steps[log.attempts++];
      if (step === undefined) {
        throw new Error("The script ran out of attempts.");
      }

      if ("throws" in step) {
        throw Object.assign(new Error(step.throws.message), step.throws.status === undefined ? {} : { status: step.throws.status });
      }

      if (step.opens) {
        onOpen();
      }

      return step.ends;
    },
    statusOf: (error) => (error as { status?: number }).status ?? null,
    pauseEnded: async () => {
      log.pauses += 1;
    },
    sleep: async (ms) => {
      log.sleeps.push(ms);
      onSleep?.(ms);
    },
    connected: (open) => {
      log.connected.push(open);
    },
  };
  return { following, log };
}

describe("reconnectAfter", () => {
  it("waits out the page's pause after a 429", () => {
    assert.deepEqual(reconnectAfter(429, 1), { kind: "afterPause" });
  });

  it("stops after a refusal asking again cannot change", () => {
    for (const status of [400, 401, 403, 404, 409, 410]) {
      assert.deepEqual(reconnectAfter(status, 1), { kind: "stop" }, String(status));
    }
  });

  it("asks again after a timeout, a server error or a failure no status answered", () => {
    for (const status of [408, 500, 502, 503, null]) {
      assert.equal(reconnectAfter(status, 1).kind, "again", String(status));
    }
  });

  it("waits two seconds at first, doubling while connecting keeps failing, to thirty", () => {
    const waits = [0, 1, 2, 3, 4, 5, 6].map((failures) => {
      const next = reconnectAfter(503, failures);
      return next.kind === "again" ? next.waitMs : -1;
    });
    assert.deepEqual(waits, [FIRST_RECONNECT_MS, 2_000, 4_000, 8_000, 16_000, LONGEST_RECONNECT_MS, LONGEST_RECONNECT_MS]);
  });
});

describe("followStream", () => {
  it("ends when the server sends its end frame, connected while it streamed", async () => {
    const { following, log } = scripted([{ opens: true, ends: true }]);

    assert.deepEqual(await followStream(following, new AbortController().signal), { kind: "ended" });
    assert.deepEqual(log.connected, [true]);
    assert.deepEqual(log.sleeps, []);
  });

  it("connects again after waits that grow while it fails, and start over once it has opened", async () => {
    const { following, log } = scripted([
      { throws: { message: "fetch failed" } },
      { throws: { status: 503, message: "HTTP 503" } },
      { opens: true, ends: false },
      { throws: { status: 502, message: "HTTP 502" } },
      { opens: true, ends: true },
    ]);

    assert.deepEqual(await followStream(following, new AbortController().signal), { kind: "ended" });
    assert.deepEqual(log.sleeps, [2_000, 4_000, 2_000, 2_000]);
    assert.equal(log.pauses, 0);
  });

  it("waits out the page's pause after a 429 rather than a timer, and then connects again", async () => {
    const { following, log } = scripted([{ throws: { status: 429, message: "Too many requests" } }, { opens: true, ends: true }]);

    assert.deepEqual(await followStream(following, new AbortController().signal), { kind: "ended" });
    assert.equal(log.pauses, 1);
    assert.deepEqual(log.sleeps, []);
  });

  it("stops for good on a refusal, saying which, rather than asking every two seconds", async () => {
    const { following, log } = scripted([{ throws: { status: 404, message: "Not found: no run 42" } }]);

    const outcome = await followStream(following, new AbortController().signal);

    assert.equal(outcome.kind, "refused");
    assert.equal(refusalText(outcome.kind === "refused" ? outcome.error : null), "Not found: no run 42");
    assert.equal(log.attempts, 1);
    assert.deepEqual(log.connected, [false]);
  });

  it("stops when the page lets go of the stream, without another attempt", async () => {
    const controller = new AbortController();
    const { following, log } = scripted([{ opens: true, ends: false }], () => controller.abort());

    assert.deepEqual(await followStream(following, controller.signal), { kind: "aborted" });
    assert.equal(log.attempts, 1);
  });
});
