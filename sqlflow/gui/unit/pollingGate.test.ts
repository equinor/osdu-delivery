// The page's polling gate: what it tells TanStack's focus manager, and what that does to TanStack's own query client,
// which the gate relies on: a poll's ticks are skipped while the gate holds and go on by themselves once it opens, and a
// query a 429 refused is asked again once it opens, however fresh its data.

import assert from "node:assert/strict";
import { after, afterEach, before, describe, it } from "node:test";
import { environmentManager, focusManager, QueryClient, QueryObserver } from "@tanstack/react-query";
import { askRefusedAgainOnFocus, createPollingGate } from "../src/api/pollingGate.ts";

const delay = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

async function until(condition: () => boolean, what: string): Promise<void> {
  for (let waited = 0; waited < 3_000; waited += 10) {
    if (condition()) {
      return;
    }

    await delay(10);
  }

  assert.fail(`Waited three seconds for ${what}.`);
}

describe("createPollingGate", () => {
  it("holds while paused or hidden, and hands focus back to the document only when neither holds", () => {
    const told: (boolean | undefined)[] = [];
    const gate = createPollingGate({ setFocused: (focused) => told.push(focused) });

    gate.setPaused(true);
    gate.setPaused(true);
    assert.equal(gate.holding(), true);
    gate.setShown(false);
    gate.setPaused(false);
    assert.equal(gate.holding(), true);
    gate.setShown(true);
    gate.setShown(true);

    assert.equal(gate.holding(), false);
    // Told only when what it holds changes; never "focused" outright, so a minimized window still counts as hidden.
    assert.deepEqual(told, [false, false, false, undefined]);
  });
});

describe("the gate over TanStack's query client", () => {
  const clients: QueryClient[] = [];
  const stops: (() => void)[] = [];

  // TanStack keeps no interval running where it takes the runtime for a server, as it takes Node; a page is a browser.
  before(() => environmentManager.setIsServer(() => false));
  after(() => environmentManager.setIsServer(() => !("window" in globalThis)));

  function client(): QueryClient {
    const made = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 60_000 } } });
    made.mount();
    clients.push(made);
    return made;
  }

  afterEach(() => {
    stops.splice(0).forEach((stop) => stop());
    clients.splice(0).forEach((made) => {
      made.unmount();
      made.clear();
    });
    focusManager.setFocused(undefined);
  });

  it("skips a poll's ticks while it holds, and the poll goes on by itself once it opens", async () => {
    const gate = createPollingGate(focusManager);
    let calls = 0;
    const observer = new QueryObserver(client(), { queryKey: ["poll"], queryFn: async () => ++calls, refetchInterval: 25 });
    stops.push(observer.subscribe(() => undefined));
    await until(() => calls >= 3, "the poll to run");

    gate.setPaused(true);
    await delay(40);
    const held = calls;
    await delay(150);
    assert.equal(calls, held, "no tick fetched while the gate held");

    gate.setPaused(false);
    await until(() => calls >= held + 3, "the poll to go on without being asked");
  });

  it("asks again a query a 429 refused once it opens, however fresh, and leaves a fresh answer alone", async () => {
    const gate = createPollingGate(focusManager);
    const made = client();
    stops.push(askRefusedAgainOnFocus(focusManager, made, (error) => (error as { status?: number } | null)?.status === 429));
    const answers: (string | number)[] = ["old", 429, "new"];
    let refusedCalls = 0;
    const refused = new QueryObserver(made, {
      queryKey: ["refused"],
      queryFn: async () => {
        const answer = answers[refusedCalls++];
        if (answer === 429) {
          throw Object.assign(new Error("Too many requests"), { status: 429 });
        }

        return answer;
      },
    });
    let freshCalls = 0;
    const fresh = new QueryObserver(made, { queryKey: ["fresh"], queryFn: async () => ++freshCalls });
    stops.push(refused.subscribe(() => undefined), fresh.subscribe(() => undefined));
    await until(() => refused.getCurrentResult().data === "old" && freshCalls === 1, "the first answers");

    await refused.refetch();
    assert.equal(refused.getCurrentResult().status, "error");
    gate.setPaused(true);
    gate.setPaused(false);

    await until(() => refused.getCurrentResult().data === "new", "the refused query to be asked again");
    assert.equal(refused.getCurrentResult().status, "success");
    assert.equal(freshCalls, 1, "a fresh answer is not asked again");
  });

  it("holds a hidden surface's polls as it holds a pause's", async () => {
    const gate = createPollingGate(focusManager);
    let calls = 0;
    const observer = new QueryObserver(client(), { queryKey: ["hidden"], queryFn: async () => ++calls, refetchInterval: 25 });
    stops.push(observer.subscribe(() => undefined));
    await until(() => calls >= 2, "the poll to run");

    gate.setShown(false);
    await delay(40);
    const held = calls;
    await delay(150);
    assert.equal(calls, held);

    gate.setShown(true);
    await until(() => calls >= held + 2, "the poll to go on once shown");
  });
});
