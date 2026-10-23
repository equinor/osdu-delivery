import { expect, FIRST_PAGE_MS, test } from "./helpers";

// The home page (osdu/docs/reference/concepts/gui.md, The home page): the assistant's question box with the questions the
// module offers, then what the title bar's partition's delivery flows hold, with the ways into them. Whether the assistant
// is configured, and what the estate has delivered, are the environment's, so each is checked for what it says either way.

const OFFERED = [
  "Which delivery flows have held or failed records, and why?",
  "What changed in this partition's cache since yesterday?",
  "Which mappings deliver well logs, and what do they read from the source?",
];

test.describe("Home", () => {
  test("opens on the assistant's question box, with the delivery questions offered or why it cannot be asked", async ({ adminPage }) => {
    await adminPage.goto("/");
    await expect(adminPage.getByTestId("page-dashboard")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await expect(adminPage.getByTestId("home-greeting")).toContainText("Welcome back");
    await expect(adminPage.getByTestId("home-ask-input")).toBeVisible();

    const off = adminPage.getByTestId("home-ask-off");
    const prompts = adminPage.getByTestId("home-prompt");
    await expect(off.or(prompts.first())).toBeVisible({ timeout: 30_000 });
    if (await off.isVisible()) {
      await expect(adminPage.getByTestId("home-ask-input")).toBeDisabled();
      await expect(prompts).toHaveCount(0);
    } else {
      await expect(prompts).toHaveText(OFFERED);
    }
  });

  test("says what the partition's delivery flows hold, and leads to a record and to the Delivery page", async ({ adminPage }) => {
    await adminPage.goto("/");
    const delivery = adminPage.getByTestId("home-delivery");
    await expect(delivery).toBeVisible({ timeout: FIRST_PAGE_MS });
    await expect(adminPage.getByTestId("home-delivery-share").or(adminPage.getByTestId("home-delivery-empty"))).toBeVisible({ timeout: 60_000 });

    await adminPage.getByTestId("home-find-record-term").fill("welldb");
    await adminPage.getByTestId("home-find-record-go").click();
    await expect(adminPage).toHaveURL(/\/delivery\/records\?q=welldb$/);

    await adminPage.goto("/");
    await adminPage.getByTestId("home-delivery-open").click();
    await expect(adminPage.getByTestId("page-delivery")).toBeVisible();
  });
});
