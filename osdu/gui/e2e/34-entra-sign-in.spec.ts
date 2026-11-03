import type { Page, Route } from "@playwright/test";
import { E2E } from "../playwright.config";
import { adminSession, expect, FIRST_PAGE_MS, test, type SessionSeed } from "./helpers";

// "Sign in with Microsoft" (sqlflow/docs/reference/concepts/authentication-and-identity.md): the tab leaves for
// Microsoft's sign-in, naming the account the Username field holds, and Entra answers at the GUI origin, where the login
// page finishes the sign-in. The suite has no Entra tenant, so the browser's requests to login.microsoftonline.com are
// answered here as Entra answers them; the exchange is answered too, since the control plane checks an ID token's
// signature against Entra's keys, and it hands back the e2e admin's own session.

const TENANT = "00000000-0000-4000-8000-0000000000e2";
const CLIENT_ID = "00000000-0000-4000-8000-0000000000c1";
const ENTRA = "https://login.microsoftonline.com";
const ACCOUNT = "e2e.admin@example.com";

/** Base64url, as JWT segments and Entra's client_info are written. */
function base64Url(value: unknown): string {
  return Buffer.from(JSON.stringify(value)).toString("base64url");
}

/** Lets the GUI's origin read an answer from Entra's origin, as Entra's own headers do. */
const CORS = { "access-control-allow-origin": "*", "access-control-allow-headers": "*", "access-control-allow-methods": "GET, POST, OPTIONS" };

interface FakeEntra {
  /** The authorize request the tab left for, once it has. */
  authorize: () => URL;
  /** The ID token the GUI exchanged at the control plane, once it has. */
  exchanged: () => string | null;
  /** Requests to Entra the fake does not answer, which the tests expect none of. */
  unexpected: string[];
}

/**
 * Answers the control plane's providers (Entra on) and exchange, and Entra's metadata, authorize and token endpoints.
 * `answer` is what Entra's authorize endpoint sends the tab back with: a code, or an error.
 */
async function fakeEntra(page: Page, admin: SessionSeed, answer: "code" | "error"): Promise<FakeEntra> {
  let authorize: URL | null = null;
  let exchanged: string | null = null;
  const unexpected: string[] = [];
  const idToken = (nonce: string) => {
    const now = Math.floor(Date.now() / 1000);
    return [
      base64Url({ typ: "JWT", alg: "RS256", kid: "e2e" }),
      base64Url({
        aud: CLIENT_ID, iss: `${ENTRA}/${TENANT}/v2.0`, iat: now, nbf: now, exp: now + 3600, ver: "2.0",
        tid: TENANT, oid: "00000000-0000-4000-8000-00000000a0a0", sub: "e2e-subject", nonce,
        preferred_username: ACCOUNT, name: "E2E Admin",
      }),
      "e2e-signature",
    ].join(".");
  };

  await page.route("**/api/v1/auth/providers", (route) => route.fulfill({
    json: { local: true, bootstrap: true, entra: { enabled: true, clientId: CLIENT_ID, authority: `${ENTRA}/${TENANT}` } },
  }));

  await page.route("**/api/v1/auth/exchange", async (route) => {
    exchanged = (route.request().postDataJSON() as { token: string }).token;
    await route.fulfill({
      json: {
        accessToken: admin.token, tokenType: "Bearer", expiresIn: Math.floor((admin.expiresAtMs - Date.now()) / 1000),
        subject: admin.subject, role: admin.role, scopes: admin.scopes,
      },
    });
  });

  await page.route(`${ENTRA}/**`, async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    if (request.method() === "OPTIONS") {
      await route.fulfill({ status: 204, headers: CORS });
      return;
    }

    if (url.pathname.endsWith("/.well-known/openid-configuration")) {
      await route.fulfill({
        headers: CORS,
        json: {
          authorization_endpoint: `${ENTRA}/${TENANT}/oauth2/v2.0/authorize`,
          token_endpoint: `${ENTRA}/${TENANT}/oauth2/v2.0/token`,
          end_session_endpoint: `${ENTRA}/${TENANT}/oauth2/v2.0/logout`,
          issuer: `${ENTRA}/${TENANT}/v2.0`,
          jwks_uri: `${ENTRA}/${TENANT}/discovery/v2.0/keys`,
          response_modes_supported: ["query", "fragment", "form_post"],
        },
      });
      return;
    }

    if (url.pathname.endsWith("/discovery/instance")) {
      await route.fulfill({
        headers: CORS,
        json: {
          tenant_discovery_endpoint: `${ENTRA}/${TENANT}/v2.0/.well-known/openid-configuration`,
          "api-version": "1.1",
          metadata: [{
            preferred_network: "login.microsoftonline.com",
            preferred_cache: "login.windows.net",
            aliases: ["login.microsoftonline.com", "login.windows.net", "login.microsoft.com", "sts.windows.net"],
          }],
        },
      });
      return;
    }

    if (url.pathname.endsWith("/oauth2/v2.0/authorize")) {
      authorize = url;
      const state = url.searchParams.get("state") ?? "";
      const back = answer === "code"
        ? new URLSearchParams({ code: "e2e-code", client_info: base64Url({ uid: "e2e-uid", utid: TENANT }), state })
        : new URLSearchParams({ error: "access_denied", error_description: "AADSTS50105: The signed in user is not assigned to a role for the application.", state });
      const target = `${url.searchParams.get("redirect_uri")}#${back.toString()}`;
      // Entra answers by sending the tab back to the redirect URI with its answer in the fragment.
      await route.fulfill({ contentType: "text/html", body: `<script>location.replace(${JSON.stringify(target)});</script>` });
      return;
    }

    if (url.pathname.endsWith("/oauth2/v2.0/token")) {
      const nonce = authorize?.searchParams.get("nonce") ?? "";
      await route.fulfill({
        headers: CORS,
        json: {
          token_type: "Bearer", scope: "openid profile email", expires_in: 3600, ext_expires_in: 3600,
          access_token: "e2e-entra-access-token", id_token: idToken(nonce),
          client_info: base64Url({ uid: "e2e-uid", utid: TENANT }),
        },
      });
      return;
    }

    unexpected.push(`${request.method()} ${request.url()}`);
    await route.fulfill({ status: 404, headers: CORS, body: "" });
  });

  return {
    authorize: () => {
      if (authorize === null) {
        throw new Error("The tab never left for Entra's authorize endpoint.");
      }

      return authorize;
    },
    exchanged: () => exchanged,
    unexpected,
  };
}

test.describe("Sign in with Microsoft", () => {
  test("signs in in the same tab, naming the account the username holds, and returns to the page asked for", async ({ page, request }) => {
    const admin = await adminSession(request);
    const entra = await fakeEntra(page, admin, "code");
    let popups = 0;
    page.on("popup", () => { popups += 1; });

    await page.goto("/runs");
    await expect(page.getByTestId("login-card")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await page.getByTestId("login-username").fill(ACCOUNT);
    await page.getByTestId("login-remember").check();
    await page.getByTestId("login-entra").click();

    await expect(page.getByTestId("page-runs")).toBeVisible({ timeout: FIRST_PAGE_MS });
    // Entra was asked for that account and was not told to ask which one; nothing opened beside the tab.
    expect(entra.authorize().searchParams.get("login_hint")).toBe(ACCOUNT);
    expect(entra.authorize().searchParams.has("prompt")).toBe(false);
    expect(popups).toBe(0);
    expect(entra.unexpected).toEqual([]);
    // The ID token Entra issued is what the control plane was handed, and the answer left no trace in the address.
    expect(entra.exchanged()).toMatch(/^[\w-]+\.[\w-]+\.[\w-]+$/);
    expect(page.url()).toBe(`${E2E.guiBaseUrl}/runs`);
    // "Keep me signed in" went to Entra and back with the sign-in.
    const stored = await page.evaluate(() => ({
      local: window.localStorage.getItem("sqlflow.session"),
      session: window.sessionStorage.getItem("sqlflow.session"),
    }));
    expect(stored.local).not.toBeNull();
    expect(stored.session).toBeNull();
  });

  test("names no account for a local user name, and shows Entra's refusal on the login page", async ({ page, request }) => {
    const admin = await adminSession(request);
    const entra = await fakeEntra(page, admin, "error");

    await page.goto("/login");
    await expect(page.getByTestId("login-card")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await page.getByTestId("login-username").fill(E2E.adminUsername);
    await page.getByTestId("login-entra").click();

    await expect(page.getByTestId("login-error")).toContainText("AADSTS50105", { timeout: FIRST_PAGE_MS });
    expect(entra.authorize().searchParams.has("login_hint")).toBe(false);
    expect(entra.authorize().searchParams.has("prompt")).toBe(false);
    expect(entra.exchanged()).toBeNull();
    expect(entra.unexpected).toEqual([]);
    await expect(page).toHaveURL(`${E2E.guiBaseUrl}/login`);
    await expect(page.getByTestId("login-entra")).toBeEnabled();
  });
});

// Every sign-in goes on from the login page the same way, so the password sign-in is checked beside Microsoft's.
test.describe("where a sign-in goes on to", () => {
  test("a password sign-in returns to the page asked for, and a signed-in visit to the login page goes straight on", async ({ page, request }) => {
    await adminSession(request); // bootstrap provisioning has created the admin
    await page.goto("/runs");
    await expect(page.getByTestId("login-card")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await page.getByTestId("login-username").fill(E2E.adminUsername);
    await page.getByTestId("login-password").fill(E2E.adminPassword);
    await page.getByTestId("login-submit").click();
    await expect(page.getByTestId("page-runs")).toBeVisible({ timeout: FIRST_PAGE_MS });

    await page.goto("/login?returnTo=%2Fruns");
    await expect(page.getByTestId("page-runs")).toBeVisible({ timeout: FIRST_PAGE_MS });
    expect(page.url()).toBe(`${E2E.guiBaseUrl}/runs`);
  });
});
