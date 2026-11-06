import type { APIRequestContext, Page } from "@playwright/test";
import { adminSession, expect, FIRST_PAGE_MS, seedSession, seedSideBarOpen, test } from "./helpers";

// The workbench's designs (osdu/docs/reference/concepts/gui-designs.md): SQLFlow's and Equinor's, each light or dark,
// picked from the title bar's menu or from the sign-in page, and put on again at the person's next visit; a first visit
// opens in Equinor's dark mode. Equinor's typeface and mark are served by Equinor's design system, which the suite answers
// itself, so no test depends on reaching it. Nothing here needs the seeded estate.

const EQUINOR = "https://cdn.eds.equinor.com";

/** Answers Equinor's services, and records what the page asked them for. */
async function answerEquinor(page: Page): Promise<string[]> {
  const asked: string[] = [];
  await page.route(`${EQUINOR}/**`, async (route) => {
    const url = route.request().url();
    asked.push(url);
    await route.fulfill(url.endsWith(".css")
      ? { status: 200, contentType: "text/css", body: "" }
      : { status: 200, contentType: "image/svg+xml", body: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 285 235"></svg>' });
  });
  return asked;
}

/** Opens a page of the workbench signed in as the admin, with nothing kept in the browser but the session and side bar. */
async function openSignedIn(page: Page, request: APIRequestContext, path = "/"): Promise<string[]> {
  const asked = await answerEquinor(page);
  await seedSession(page, await adminSession(request));
  await seedSideBarOpen(page);
  await page.goto(path);
  await expect(page.getByTestId("app-bar")).toBeVisible({ timeout: FIRST_PAGE_MS });
  return asked;
}

const html = (page: Page) => page.locator("html");

test.describe("designs", () => {
  test("a first visit opens in Equinor's dark mode, with Equinor's mark in the title bar and its typeface loaded", async ({ page, request }) => {
    const asked = await openSignedIn(page, request);

    await expect(html(page)).toHaveAttribute("data-sf-design", "equinor");
    await expect(html(page)).toHaveClass(/(^|\s)dark(\s|$)/);
    await expect(page.getByTestId("app-bar-mark").locator("img")).toHaveAttribute("src", `${EQUINOR}/logo/equinor-logo-primary.svg#red`);
    await expect(page.locator(`link[rel="stylesheet"][href="${EQUINOR}/font/eds-uprights-vf.css"]`)).toHaveCount(1);
    expect(asked.some((url) => url === `${EQUINOR}/font/eds-uprights-vf.css`)).toBe(true);
    // The first appearance is kept, so the next visit paints in it from the start.
    expect(await page.evaluate(() => [localStorage.getItem("sqlflow.design"), localStorage.getItem("sqlflow.theme")])).toEqual(["equinor", "dark"]);
  });

  test("the title bar's menu puts on a design and a mode, stays open while they are tried, and the next visit wears them", async ({ page, request }) => {
    await openSignedIn(page, request);

    await page.getByTestId("appearance-menu-button").click();
    const menu = page.getByTestId("appearance-menu");
    await expect(menu).toBeVisible();
    await expect(page.getByTestId("appearance-design-equinor")).toHaveAttribute("aria-checked", "true");
    await expect(page.getByTestId("appearance-mode-dark")).toHaveAttribute("aria-checked", "true");

    await page.getByTestId("appearance-design-sqlflow").click();
    await expect(html(page)).toHaveAttribute("data-sf-design", "sqlflow");
    await expect(menu).toBeVisible();
    await page.getByTestId("appearance-mode-light").click();
    await expect(html(page)).not.toHaveClass(/(^|\s)dark(\s|$)/);
    await expect(html(page)).toHaveAttribute("data-sf-theme", "light");
    await page.keyboard.press("Escape");
    await expect(menu).toBeHidden();
    // SQLFlow's design shows the branding's own mark.
    await expect(page.getByTestId("app-bar-mark")).toHaveAttribute("src", "/brand/logo-white.png");

    await page.reload();
    await expect(page.getByTestId("app-bar")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await expect(html(page)).toHaveAttribute("data-sf-design", "sqlflow");
    await expect(html(page)).toHaveAttribute("data-sf-theme", "light");

    await page.getByTestId("appearance-menu-button").click();
    await page.getByTestId("appearance-design-equinor").click();
    await expect(html(page)).toHaveAttribute("data-sf-design", "equinor");
    await expect(html(page)).toHaveAttribute("data-sf-theme", "light");
    await expect(page.getByTestId("app-bar-mark").locator("img")).toHaveAttribute("src", `${EQUINOR}/logo/equinor-logo-primary.svg#red`);
  });

  test("the sign-in page offers the same menu, and the workbench opens in what was picked there", async ({ page, request }) => {
    await answerEquinor(page);
    await page.goto("/login");
    await expect(page.getByTestId("login-card")).toBeVisible({ timeout: FIRST_PAGE_MS });

    await page.getByTestId("login-appearance-menu-button").click();
    await expect(page.getByTestId("login-appearance-menu")).toBeVisible();
    await page.getByTestId("login-appearance-design-sqlflow").click();
    await page.getByTestId("login-appearance-mode-light").click();
    await expect(html(page)).toHaveAttribute("data-sf-design", "sqlflow");
    await expect(html(page)).toHaveAttribute("data-sf-theme", "light");
    await page.keyboard.press("Escape");
    // The sign-in page stays on its brand field in either mode.
    await expect(page.getByTestId("login-card")).toBeVisible();

    await seedSession(page, await adminSession(request));
    await page.goto("/");
    await expect(page.getByTestId("app-bar")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await expect(html(page)).toHaveAttribute("data-sf-design", "sqlflow");
    await expect(html(page)).toHaveAttribute("data-sf-theme", "light");
  });
});
