import type { Locator } from "@playwright/test";
import { adminSession, expect, FIRST_PAGE_MS, seedSession, test } from "./helpers";

// The OSDU group (osdu/docs/reference/concepts/gui.md, The OSDU menu): one branch of the side bar's tree, its entries
// under four sections in the order an operator reaches for them, each folding on its own, and the mapping builder
// reached from Mappings rather than listed. The side bar starts closed on a first visit, its menu folded to its headers,
// and the person's choices stand from then on. Nothing
// here needs the seeded estate: the navigation is the module's, whatever the catalog holds.

const SECTIONS = [
  { id: "ledger", label: "Ledger", entries: ["nav-delivery", "nav-delivery-records", "nav-delivery-removals", "nav-delivery-activity", "nav-delivery-orphaned-ledgers"] },
  {
    id: "in-osdu",
    label: "In OSDU",
    entries: ["nav-delivery-explorer", "nav-delivery-assertions", "nav-delivery-inventories", "nav-delivery-dimensions"],
  },
  { id: "build", label: "Build", entries: ["nav-delivery-documents", "nav-delivery-templates", "nav-delivery-cache", "nav-delivery-order"] },
  { id: "setup", label: "Setup", entries: ["nav-delivery-partitions", "nav-delivery-search-terms"] },
];

/** A section's own row in the OSDU group: a branch of the menu's tree, with its entries under it. */
const sectionHeader = (group: Locator, section: { id: string; label: string }) =>
  group.getByTestId(`sidebar-part-osdu-${section.id}`).getByRole("treeitem", { name: section.label, exact: true });

test.describe("OSDU navigation", () => {
  test("the side bar starts closed on a first visit with its menu folded, and from then on as it was left", async ({ page, request }) => {
    // A browser that holds no choice: signed in, and nothing else seeded.
    await seedSession(page, await adminSession(request));
    await page.goto("/");
    await expect(page.getByTestId("page-dashboard")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await expect(page.getByTestId("activity-osdu")).toBeVisible();
    await expect(page.locator("#sidebar-section-osdu")).toHaveCount(0);

    // The activity bar opens the side bar on the group it names, which shows its sections folded; every other group is
    // folded to its header.
    const osdu = page.locator("#sidebar-section-osdu");
    const foldedAsFirstOpened = async () => {
      await expect(osdu).toBeVisible();
      await expect(page.locator("#sidebar-section-osdu > [role='treeitem']")).toHaveAttribute("aria-expanded", "true");
      for (const section of SECTIONS) {
        await expect(sectionHeader(osdu, section)).toHaveAttribute("aria-expanded", "false");
      }

      await expect(osdu.locator("[data-testid^='nav-']")).toHaveCount(0);
      await expect(page.locator("#sidebar-section-workspace > [role='treeitem']")).toHaveAttribute("aria-expanded", "false");
      await expect(page.locator("#sidebar-section-workspace [data-testid^='nav-']")).toHaveCount(0);
    };

    await page.getByTestId("activity-osdu").click();
    await foldedAsFirstOpened();

    await page.reload();
    await expect(page.getByTestId("page-dashboard")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await foldedAsFirstOpened();
  });

  test("the OSDU group lists its entries under its four sections, in order, and the builder under none", async ({ adminPage }) => {
    const group = adminPage.locator("#sidebar-section-osdu");
    await expect(group).toBeVisible();

    const parts = await group.locator("[data-testid^='sidebar-part-osdu-']").evaluateAll(
      (elements) => elements.map((element) => element.getAttribute("data-testid")),
    );
    expect(parts).toEqual(SECTIONS.map((section) => `sidebar-part-osdu-${section.id}`));

    for (const section of SECTIONS) {
      const part = group.getByTestId(`sidebar-part-osdu-${section.id}`);
      await expect(sectionHeader(group, section)).toBeVisible();
      const entries = await part.locator("[data-testid^='nav-']").evaluateAll(
        (elements) => elements.map((element) => element.getAttribute("data-testid")),
      );
      expect(entries).toEqual(section.entries);
    }

    // Each level sits one indent further in than the level above it: the group, its sections, their entries.
    const indent = (row: Locator) => row.evaluate((element) => Number.parseFloat(getComputedStyle(element).paddingLeft));
    const groupIndent = await indent(group.locator(":scope > [role='treeitem']"));
    for (const section of SECTIONS) {
      const sectionIndent = await indent(sectionHeader(group, section));
      expect(sectionIndent).toBeGreaterThan(groupIndent);
      expect(await indent(adminPage.getByTestId(section.entries[0]))).toBeGreaterThan(sectionIndent);
    }

    await expect(adminPage.getByTestId("nav-delivery-mapping-builder")).toHaveCount(0);
  });

  test("a section folds on its own, and stays as the person left it", async ({ adminPage }) => {
    const group = adminPage.locator("#sidebar-section-osdu");
    const build = group.getByTestId("sidebar-part-osdu-build");
    const header = sectionHeader(group, { id: "build", label: "Build" });
    const entries = build.locator("[data-testid^='nav-']");
    await expect(header).toHaveAttribute("aria-expanded", "true");

    await header.click();
    await expect(header).toHaveAttribute("aria-expanded", "false");
    await expect(entries).toHaveCount(0);
    await expect(adminPage.getByTestId("nav-delivery-explorer")).toBeVisible();
    await expect(adminPage.getByTestId("nav-delivery-partitions")).toBeVisible();

    await adminPage.reload();
    await expect(adminPage.getByTestId("page-dashboard")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await expect(header).toHaveAttribute("aria-expanded", "false");
    await expect(entries).toHaveCount(0);

    await header.click();
    await expect(header).toHaveAttribute("aria-expanded", "true");
    expect(await entries.evaluateAll((elements) => elements.map((element) => element.getAttribute("data-testid")))).toEqual(
      SECTIONS.find((section) => section.id === "build")?.entries,
    );
  });

  test("the menu is one tree: Tab enters it at the page shown, the arrow keys walk and fold it, Shift+Tab leaves it", async ({ adminPage }) => {
    const records = adminPage.getByTestId("nav-delivery-records");
    await records.click();
    await expect(records).toHaveAttribute("aria-current", "page");

    // In from the search above it, as a person tabs into the menu.
    const menu = adminPage.getByRole("tree", { name: "Pages" });
    await adminPage.getByTestId("global-search").focus();
    await adminPage.keyboard.press("Tab");
    await expect(records).toBeFocused();

    // Down to the next entry, left to its section, left again folds the section, right opens it.
    const ledger = sectionHeader(adminPage.locator("#sidebar-section-osdu"), SECTIONS[0]);
    await adminPage.keyboard.press("ArrowDown");
    await expect(adminPage.getByTestId("nav-delivery-removals")).toBeFocused();
    await adminPage.keyboard.press("ArrowLeft");
    await expect(ledger).toBeFocused();
    await adminPage.keyboard.press("ArrowLeft");
    await expect(ledger).toHaveAttribute("aria-expanded", "false");
    await expect(records).toHaveCount(0);
    await adminPage.keyboard.press("ArrowRight");
    await expect(ledger).toHaveAttribute("aria-expanded", "true");
    await expect(records).toBeVisible();

    // The menu is one tab stop: the first Shift+Tab from a row lands on the tree itself, the next leaves it.
    await adminPage.keyboard.press("Shift+Tab");
    await expect(menu).toBeFocused();
    await adminPage.keyboard.press("Shift+Tab");
    const focusInTree = () => adminPage.evaluate(() => document.activeElement !== null && document.activeElement.closest("[role='tree']") !== null);
    expect(await focusInTree()).toBe(false);
    await expect(adminPage.getByTestId("global-search")).toBeFocused();

    // Back in, Enter on an entry opens its page.
    await adminPage.keyboard.press("Tab");
    await expect(records).toBeFocused();
    await adminPage.keyboard.press("ArrowUp");
    await expect(adminPage.getByTestId("nav-delivery")).toBeFocused();
    await adminPage.keyboard.press("Enter");
    await expect(adminPage.getByTestId("nav-delivery")).toHaveAttribute("aria-current", "page");
    await expect(records).not.toHaveAttribute("aria-current", "page");
  });

  test("the command palette heads each section by the group and the section", async ({ adminPage }) => {
    await adminPage.keyboard.press("Control+k");
    const palette = adminPage.getByRole("dialog");
    await expect(palette).toBeVisible();
    for (const section of SECTIONS) {
      await expect(palette.getByText(`OSDU · ${section.label}`, { exact: true })).toBeVisible();
    }

    await palette.getByRole("combobox").fill("build cache");
    await expect(palette.getByRole("option", { name: "Cache" })).toBeVisible();
  });

  test("the builder is opened from Mappings, which stays lit while it is shown", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-documents").click();
    await adminPage.getByTestId("delivery-documents-new-mapping").click();
    await expect(adminPage.getByTestId("page-delivery-mapping-builder")).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("nav-delivery-documents")).toHaveAttribute("aria-current", "page");
    await expect(adminPage.getByTestId("nav-delivery")).not.toHaveAttribute("aria-current", "page");
    await expect(adminPage.getByRole("tab", { name: /Mapping builder/ })).toBeVisible();
  });
});
