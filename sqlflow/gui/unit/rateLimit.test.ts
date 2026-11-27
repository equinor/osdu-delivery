// The GUI's pause for calling too often: how long a 429's Retry-After pauses it, and that the pause ends on its own and
// says so to everyone waiting, however often it was read meanwhile.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  createRateLimitPause, LONGEST_PAUSE_SECONDS, pauseSeconds, UNSTATED_PAUSE_SECONDS, type PauseClock,
} from "../src/api/rateLimit.ts";

/** A clock moved by hand, whose timers fire as it passes them. */
function handClock(start = 1_700_000_000_000): PauseClock & { advance(ms: number): void; pending(): number } {
  let now = start;
  let nextId = 1;
  const timers = new Map<number, { at: number; callback: () => void }>();
  return {
    now: () => now,
    setTimeout: (callback, ms) => {
      const id = nextId++;
      timers.set(id, { at: now + ms, callback });
      return id;
    },
    clearTimeout: (handle) => {
      timers.delete(handle as number);
    },
    advance: (ms) => {
      now += ms;
      for (const [id, timer] of [...timers].sort((a, b) => a[1].at - b[1].at)) {
        if (timer.at <= now) {
          timers.delete(id);
          timer.callback();
        }
      }
    },
    pending: () => timers.size,
  };
}

describe("pauseSeconds", () => {
  const now = Date.parse("2026-11-27T10:00:00Z");

  it("reads whole seconds, as the control plane sends them", () => {
    assert.equal(pauseSeconds("12", now), 12);
    assert.equal(pauseSeconds(" 7 ", now), 7);
  });

  it("reads an HTTP date as the seconds until it, rounded up", () => {
    assert.equal(pauseSeconds("Fri, 27 Nov 2026 10:00:30 GMT", now), 30);
    assert.equal(pauseSeconds(new Date(now + 2_500).toUTCString(), now), 2);
  });

  it("never pauses for less than a second, a date already past included", () => {
    assert.equal(pauseSeconds("0", now), 1);
    assert.equal(pauseSeconds("Fri, 27 Nov 2026 09:59:00 GMT", now), 1);
  });

  it("pauses no longer than the longest pause, whatever the header says", () => {
    assert.equal(pauseSeconds("86400", now), LONGEST_PAUSE_SECONDS);
  });

  it("pauses for the unstated pause when the header is absent or unreadable", () => {
    for (const header of [null, "", "soon", "-5", "1.5"]) {
      assert.equal(pauseSeconds(header, now), UNSTATED_PAUSE_SECONDS, String(header));
    }
  });
});

describe("createRateLimitPause", () => {
  it("pauses for what the 429 says and tells its listeners when it starts and when it ends", () => {
    const clock = handClock();
    const pause = createRateLimitPause(clock);
    const heard: (number | null)[] = [];
    pause.subscribe((until) => heard.push(until));

    pause.pause("12");
    const until = clock.now() + 12_000;
    assert.equal(pause.pausedUntil(), until);
    assert.deepEqual(heard, [until]);

    clock.advance(11_999);
    assert.equal(pause.pausedUntil(), until);
    clock.advance(1);
    assert.equal(pause.pausedUntil(), null);
    assert.deepEqual(heard, [until, null]);
    assert.equal(clock.pending(), 0);
  });

  it("tells its listeners it ended even when it was read as over before its timer fired", () => {
    // The pause the client used to keep cleared itself when read late and then never said it ended, so whatever waited
    // for the end (the polling gate) waited for good.
    let fire: (() => void) | null = null;
    let now = 0;
    const pause = createRateLimitPause({
      now: () => now,
      setTimeout: (callback) => {
        fire = callback;
        return 1;
      },
      clearTimeout: () => undefined,
    });
    const heard: (number | null)[] = [];
    pause.subscribe((until) => heard.push(until));

    pause.pause("5");
    now = 6_000;
    assert.equal(pause.pausedUntil(), null);
    assert.ok(fire !== null);
    (fire as () => void)();

    assert.deepEqual(heard, [5_000, null]);
  });

  it("keeps a pause that ends later than a new 429 says, and moves one that ends sooner", () => {
    const clock = handClock();
    const pause = createRateLimitPause(clock);
    const heard: (number | null)[] = [];
    pause.subscribe((until) => heard.push(until));
    const start = clock.now();

    pause.pause("30");
    pause.pause("10");
    assert.equal(pause.pausedUntil(), start + 30_000);
    clock.advance(5_000);
    pause.pause("40");
    assert.equal(pause.pausedUntil(), start + 45_000);
    assert.equal(clock.pending(), 1);

    clock.advance(30_000);
    assert.equal(pause.pausedUntil(), start + 45_000);
    clock.advance(10_000);
    assert.deepEqual(heard, [start + 30_000, start + 45_000, null]);
  });

  it("stops telling a listener that has stopped listening", () => {
    const clock = handClock();
    const pause = createRateLimitPause(clock);
    const heard: (number | null)[] = [];
    const stop = pause.subscribe((until) => heard.push(until));
    stop();

    pause.pause("3");
    clock.advance(3_000);

    assert.deepEqual(heard, []);
  });
});
