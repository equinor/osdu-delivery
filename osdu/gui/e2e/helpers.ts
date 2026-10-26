import { readFileSync } from "node:fs";
import { expect, test as base, type APIRequestContext, type Page } from "@playwright/test";
import { E2E } from "../playwright.config";

export interface SessionSeed {
  token: string;
  subject: string;
  role: string | null;
  scopes: string[];
  expiresAtMs: number;
}

interface LoginBody {
  accessToken: string;
  expiresIn: number;
  subject: string;
  role: string;
  scopes: string[];
}

/**
 * Signs in through the API, retrying while bootstrap provisioning (which creates the e2e admin on a fresh
 * database) completes in the control plane's background. Fails loudly after the deadline.
 */
export async function apiLogin(
  request: APIRequestContext, username: string, password: string, deadlineMs = 120_000,
): Promise<SessionSeed> {
  const deadline = Date.now() + deadlineMs;
  let lastStatus = 0;
  while (Date.now() < deadline) {
    const response = await request.post(`${E2E.apiBaseUrl}/api/v1/auth/login`, {
      data: { username, password },
    });
    lastStatus = response.status();
    if (response.ok()) {
      const body = (await response.json()) as LoginBody;
      return {
        token: body.accessToken,
        subject: body.subject,
        role: body.role,
        scopes: body.scopes,
        expiresAtMs: Date.now() + body.expiresIn * 1000,
      };
    }

    await new Promise((resolve) => setTimeout(resolve, 1000));
  }

  throw new Error(
    `Could not sign in as ${username} within ${deadlineMs}ms (last status ${lastStatus}); did bootstrap provisioning run?`,
  );
}

let cachedAdmin: SessionSeed | null = null;

export async function adminSession(request: APIRequestContext): Promise<SessionSeed> {
  if (!cachedAdmin || cachedAdmin.expiresAtMs < Date.now() + 60_000) {
    cachedAdmin = await apiLogin(request, E2E.adminUsername, E2E.adminPassword);
  }

  return cachedAdmin;
}

/** Seeds the SPA's sessionStorage before any page script runs, exactly as AuthContext persists it. */
export async function seedSession(page: Page, session: SessionSeed): Promise<void> {
  await page.addInitScript((value) => {
    window.sessionStorage.setItem("sqlflow.session", JSON.stringify(value));
  }, session);
}

/**
 * Starts the workbench's side bar open and its menu expanded, as a person finds them once they have opened both: the
 * module starts the side bar closed and its menu folded on a first visit (osdu/gui/src/module.tsx), and the tests reach
 * most pages through its entries. Each is seeded only while the browser holds no choice, so a test that closes the side
 * bar or folds a section finds it so after a reload.
 */
export async function seedSideBarOpen(page: Page): Promise<void> {
  await page.addInitScript(({ sideBar, sections }) => {
    if (window.localStorage.getItem(sideBar) === null) {
      window.localStorage.setItem(sideBar, "open");
    }

    if (window.localStorage.getItem(sections) === null) {
      window.localStorage.setItem(sections, "[]");
    }
  }, { sideBar: "sqlflow.workbench.sidebar", sections: "sqlflow.workbench.sections" });
}

/**
 * How long the first page of a test may take to appear. Each test opens a new browser context, which loads the whole
 * app from the GUI's dev server and renews its session against the control plane; on a busy machine that takes longer
 * than the default wait, and a test failing there has tested nothing.
 */
export const FIRST_PAGE_MS = 30_000;

/** A page already signed in as the bootstrap-provisioned admin, its side bar open, parked on the dashboard. */
export const test = base.extend<{ adminPage: Page }>({
  adminPage: async ({ page, request }, use) => {
    const session = await adminSession(request);
    await seedSession(page, session);
    await seedSideBarOpen(page);
    await page.goto("/");
    await expect(page.getByTestId("page-dashboard")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await use(page);
  },
});

/**
 * Checks that `file` is the Excel workbook a records export writes: a zip archive (its first local file header's signature)
 * holding the workbook, the Records sheet and the About sheet. A zip names its parts uncompressed, so they are read from the
 * bytes without unpacking it; the cells themselves are checked by the control plane's own suites.
 */
export function expectRecordsWorkbook(file: string): void {
  const bytes = readFileSync(file);
  expect(bytes.subarray(0, 4).equals(Buffer.from([0x50, 0x4b, 0x03, 0x04]))).toBe(true);
  for (const part of ["[Content_Types].xml", "xl/workbook.xml", "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml", "xl/styles.xml"]) {
    expect(bytes.includes(part), `the workbook holds ${part}`).toBe(true);
  }
}

export { expect } from "@playwright/test";
