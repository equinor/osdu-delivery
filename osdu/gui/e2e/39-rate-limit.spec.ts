import type { Page, Request, Route } from "@playwright/test";
import { E2E } from "../playwright.config";
import { expect, FIRST_PAGE_MS, test } from "./helpers";

// Staying inside the control plane's rate limit (sqlflow/docs/reference/concepts/control-plane.md, Health probes and rate
// limiting): a page left open asks for little, every flow's counts in one call; a hidden tab asks for nothing; and a page
// refused with a 429 waits as long as it is told, says so, and comes back by itself, with no reload. The e2e control plane
// runs with a limit no test reaches, so what a page asks is counted at the browser, and a 429 is the browser's to answer.

/** How long a page is watched while it sits open. */
const WATCH_MS = 30_000;

/** The most a page sitting open may ask for in WATCH_MS: 60 a minute, half the control plane's 120. */
const BUDGET = 30;

/** A delivery flow's own counts, which the pages that show every flow no longer ask for one flow at a time. */
const PER_FLOW_STATS = /\/api\/v1\/delivery\/flows\/[0-9a-f-]{36}\/stats$/;

/** Every call the page makes to the control plane from now on, by path; preflights left out. */
function recordCalls(page: Page): string[] {
  const calls: string[] = [];
  page.on("request", (request: Request) => {
    if (request.url().startsWith(`${E2E.apiBaseUrl}/api/`) && request.method() !== "OPTIONS") {
      calls.push(new URL(request.url()).pathname);
    }
  });
  return calls;
}

/** Watches the page sit open for `ms` and answers what it asked for meanwhile. */
async function callsWhileOpen(page: Page, calls: string[], ms: number): Promise<string[]> {
  calls.length = 0;
  await page.waitForTimeout(ms);
  return [...calls];
}

/** Makes the page's document read as hidden, or as shown again, as a browser does when its tab is moved behind another. */
async function setHidden(page: Page, hidden: boolean): Promise<void> {
  await page.evaluate((nowHidden) => {
    Object.defineProperty(document, "visibilityState", { configurable: true, get: () => (nowHidden ? "hidden" : "visible") });
    Object.defineProperty(document, "hidden", { configurable: true, get: () => nowHidden });
    document.dispatchEvent(new Event("visibilitychange", { bubbles: true }));
  }, hidden);
}

test.describe("Staying inside the rate limit", () => {
  // Each test watches a page sit open for half a minute or more, longer than a test is given by default.
  test.describe.configure({ timeout: 150_000 });

  test("Home and the Delivery page read every flow's counts in one call, and ask for no more than half the limit while open", async ({ adminPage }) => {
    const calls = recordCalls(adminPage);
    await expect(adminPage.getByTestId("home-delivery-share").or(adminPage.getByTestId("home-delivery-empty"))).toBeVisible({ timeout: 60_000 });

    const onHome = await callsWhileOpen(adminPage, calls, WATCH_MS);
    test.info().annotations.push({ type: "calls on Home", description: `${onHome.length} in ${WATCH_MS / 1000} s` });
    expect(onHome.filter((path) => PER_FLOW_STATS.test(path)), "Home asked for a flow's own counts").toEqual([]);
    expect(onHome.length, `Home asked for ${onHome.length} in ${WATCH_MS / 1000} s:\n${onHome.join("\n")}`).toBeLessThanOrEqual(BUDGET);

    await adminPage.getByTestId("home-delivery-open").click();
    await expect(adminPage.getByTestId("delivery-flows").or(adminPage.getByTestId("delivery-empty"))).toBeVisible({ timeout: FIRST_PAGE_MS });
    const onDelivery = await callsWhileOpen(adminPage, calls, WATCH_MS);
    test.info().annotations.push({ type: "calls on Delivery", description: `${onDelivery.length} in ${WATCH_MS / 1000} s` });
    expect(onDelivery.filter((path) => PER_FLOW_STATS.test(path)), "the Delivery page asked for a flow's own counts").toEqual([]);
    expect(onDelivery.length, `the Delivery page asked for ${onDelivery.length} in ${WATCH_MS / 1000} s:\n${onDelivery.join("\n")}`)
      .toBeLessThanOrEqual(BUDGET);
  });

  test("a tab moved behind another asks for nothing, and reads again once it is in front", async ({ adminPage }) => {
    const calls = recordCalls(adminPage);
    await expect(adminPage.getByTestId("home-delivery-share").or(adminPage.getByTestId("home-delivery-empty"))).toBeVisible({ timeout: 60_000 });

    await setHidden(adminPage, true);
    const hidden = await callsWhileOpen(adminPage, calls, 20_000);
    expect(hidden, `a hidden Home asked for:\n${hidden.join("\n")}`).toEqual([]);

    calls.length = 0;
    await setHidden(adminPage, false);
    await expect.poll(() => calls.length, { timeout: 10_000 }).toBeGreaterThan(0);
  });

  test("a 429 pauses the page for as long as it says, the page says so, and it comes back by itself", async ({ adminPage }) => {
    // What Home read is left to go stale first, so the Delivery page asks for it again as it opens, and those first calls
    // are the ones refused.
    await expect(adminPage.getByTestId("home-delivery-share").or(adminPage.getByTestId("home-delivery-empty"))).toBeVisible({ timeout: 60_000 });
    await adminPage.waitForTimeout(6_000);

    let refusing = true;
    const refused: number[] = [];
    const answered: number[] = [];
    await adminPage.route(`${E2E.apiBaseUrl}/api/v1/**`, async (route: Route) => {
      const request = route.request();
      if (request.method() === "OPTIONS" || !refusing) {
        answered.push(Date.now());
        return route.continue();
      }

      refused.push(Date.now());
      return route.fulfill({
        status: 429,
        headers: {
          "access-control-allow-origin": request.headers()["origin"] ?? "*",
          "access-control-allow-credentials": "true",
          "access-control-expose-headers": "Retry-After, X-Correlation-ID",
          "content-type": "application/problem+json",
          "retry-after": "5",
        },
        body: JSON.stringify({
          status: 429,
          title: "Too many requests",
          detail: "This caller has made more calls than the control plane takes in its rate limit's window; it takes them again in 5 s.",
        }),
      });
    });

    // The Delivery page asks again for what Home had read, and every call of it is refused.
    await adminPage.getByTestId("home-delivery-open").click();
    await expect(adminPage.getByTestId("rate-limit-banner")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await expect(adminPage.getByTestId("page-delivery")).toContainText("Too many requests");
    const first = refused[0]!;

    // While the pause holds, the page asks for nothing; the control plane answers again before the pause ends.
    await adminPage.waitForTimeout(Math.max(0, first + 4_000 - Date.now()));
    const duringPause = refused.filter((at) => at > first + 1_000);
    expect(duringPause, "calls made while the 429 said to wait").toEqual([]);
    refusing = false;

    // Once the pause ends the refusal leaves the page by itself: the flows show, and the banner goes.
    await expect(adminPage.getByTestId("delivery-flows").or(adminPage.getByTestId("delivery-empty"))).toBeVisible({ timeout: 15_000 });
    await expect(adminPage.getByTestId("page-delivery")).not.toContainText("Too many requests");
    await expect(adminPage.getByTestId("rate-limit-banner")).toBeHidden();
    expect(answered.some((at) => at >= first + 5_000), "the page asked again once the pause ended").toBe(true);
  });
});
