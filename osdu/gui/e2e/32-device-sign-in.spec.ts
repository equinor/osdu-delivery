import { E2E } from "../playwright.config";
import { expect, FIRST_PAGE_MS, test } from "./helpers";

// A client's browser sign-in (sqlflow/docs/reference/concepts/authentication-and-identity.md): an editor or an MCP server
// starts the device grant and opens its address, which the control plane hands to the GUI. The person is there behind
// the GUI's sign-in, which is what lets an account that signs in with Microsoft approve a client at all: the control
// plane's own page takes a password. The suite signs in with a local account, since the GUI's sign-in is what it tests
// here, not Microsoft's.

interface DeviceStart {
  deviceCode: string;
  userCode: string;
  verificationUri: string;
  verificationUriComplete: string;
}

async function startDevice(request: import("@playwright/test").APIRequestContext): Promise<DeviceStart> {
  const started = await request.post(`${E2E.apiBaseUrl}/api/v1/auth/device`, { data: { clientId: "e2e", scope: "read operate" } });
  expect(started.ok()).toBe(true);
  return (await started.json()) as DeviceStart;
}

test.describe("a client's browser sign-in", () => {
  test("the address a client opens lands on the GUI, and approving signs the client in", async ({ adminPage, request }) => {
    const device = await startDevice(request);
    expect(device.verificationUriComplete.startsWith(E2E.apiBaseUrl)).toBe(true);

    await adminPage.goto(device.verificationUriComplete);
    await expect(adminPage).toHaveURL(new RegExp(`^${E2E.guiBaseUrl}/device\\?code=`));
    await expect(adminPage.getByLabel("Code")).toHaveValue(device.userCode, { timeout: FIRST_PAGE_MS });
    await expect(adminPage.getByText(`act as ${E2E.adminUsername}`)).toBeVisible();

    // Nothing happens until the person approves: a link alone must never sign anyone in.
    const before = await request.post(`${E2E.apiBaseUrl}/api/v1/auth/device/token`, { data: { deviceCode: device.deviceCode } });
    expect(((await before.json()) as { error: string }).error).toBe("authorization_pending");

    await adminPage.getByRole("button", { name: "Approve" }).click();
    await expect(adminPage.getByText("Signed in", { exact: true })).toBeVisible();

    // A client polls at the grant's interval, and a poll sooner than that is told to slow down.
    let accessToken = "";
    await expect.poll(async () => {
      const token = await request.post(`${E2E.apiBaseUrl}/api/v1/auth/device/token`, { data: { deviceCode: device.deviceCode } });
      accessToken = ((await token.json()) as { access_token?: string }).access_token ?? "";
      return accessToken !== "";
    }, { timeout: 20_000, intervals: [6_000] }).toBe(true);
    const me = await request.get(`${E2E.apiBaseUrl}/api/v1/me`, { headers: { Authorization: `Bearer ${accessToken}` } });
    expect(((await me.json()) as { subject: string }).subject).toBe(E2E.adminUsername);
  });

  test("a person with no session signs in first and comes back to the code", async ({ page, request }) => {
    const device = await startDevice(request);

    await page.goto(device.verificationUriComplete);
    await expect(page).toHaveURL(/\/login\?returnTo=/, { timeout: FIRST_PAGE_MS });
    await page.getByLabel("Username").fill(E2E.adminUsername);
    await page.getByLabel("Password").fill(E2E.adminPassword);
    await page.getByRole("button", { name: /sign in/i }).click();

    await expect(page).toHaveURL(/\/device\?code=/, { timeout: FIRST_PAGE_MS });
    await expect(page.getByLabel("Code")).toHaveValue(device.userCode);
  });

  test("denying leaves the client signed out", async ({ adminPage, request }) => {
    const device = await startDevice(request);

    await adminPage.goto(device.verificationUriComplete);
    await expect(adminPage.getByLabel("Code")).toHaveValue(device.userCode, { timeout: FIRST_PAGE_MS });
    await adminPage.getByRole("button", { name: "Deny" }).click();
    await expect(adminPage.getByText("Sign-in denied")).toBeVisible();

    await expect.poll(async () => {
      const token = await request.post(`${E2E.apiBaseUrl}/api/v1/auth/device/token`, { data: { deviceCode: device.deviceCode } });
      return ((await token.json()) as { error: string }).error;
    }, { timeout: 20_000, intervals: [6_000] }).toBe("access_denied");
  });

  test("a code the control plane does not hold says so", async ({ adminPage }) => {
    await adminPage.goto("/device?code=NOPE-NOPE");
    await adminPage.getByRole("button", { name: "Approve" }).click();
    await expect(adminPage.getByTestId("api-error")).toContainText("Unknown or expired code", { timeout: FIRST_PAGE_MS });
  });
});
