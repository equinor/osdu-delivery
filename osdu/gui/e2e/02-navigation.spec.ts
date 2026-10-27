import type { Locator, Page } from "@playwright/test";
import { adminSession, expect, FIRST_PAGE_MS, seedSession, test } from "./helpers";

// The menu (osdu/docs/reference/concepts/gui.md, The menu): the module lays out the whole of it, SQLFlow's pages and its
// own side by side, as one tree whose groups are the activity bar's, a group of more than one sort of page listing each
// sort under a section that folds on its own, and the mapping builder reached from Mappings rather than listed. The side
// bar starts closed on a first visit, its menu folded to its headers, and the person's choices stand from then on.
// Nothing here needs the seeded estate: the navigation is the module's, whatever the catalog holds.

interface MenuSection {
  id: string;
  label: string;
  entries: string[];
}

interface MenuGroup {
  id: string;
  label: string;
  entries: string[];
  sections: MenuSection[];
}

const MENU: MenuGroup[] = [
  {
    id: "operate",
    label: "Operate",
    entries: ["nav-dashboard", "nav-chat", "nav-runs", "nav-schedule-timeline"],
    sections: [{ id: "health", label: "Health", entries: ["nav-datastreams", "nav-insights", "nav-nodes", "nav-settings-maintenance"] }],
  },
  {
    id: "ledger",
    label: "Ledger",
    entries: ["nav-delivery", "nav-delivery-records", "nav-delivery-removals", "nav-delivery-activity", "nav-delivery-orphaned-ledgers"],
    sections: [],
  },
  {
    id: "osdu",
    label: "OSDU",
    entries: [
      "nav-delivery-explorer",
      "nav-delivery-search-terms",
      "nav-delivery-assertions",
      "nav-delivery-inventories",
      "nav-delivery-dimensions",
      "nav-delivery-partitions",
    ],
    sections: [],
  },
  {
    id: "build",
    label: "Build",
    entries: [],
    sections: [
      { id: "flows", label: "Flows", entries: ["nav-repos", "nav-pipelines", "nav-schedules"] },
      { id: "delivery", label: "Delivery", entries: ["nav-delivery-documents", "nav-delivery-templates", "nav-delivery-cache", "nav-delivery-order"] },
      { id: "sources", label: "Sources", entries: ["nav-datasources", "nav-integrations", "nav-discover", "nav-key-detection"] },
    ],
  },
  { id: "explore", label: "Explore", entries: ["nav-catalog", "nav-lineage", "nav-subscribers", "nav-schema-changes", "nav-search"], sections: [] },
  { id: "admin", label: "Admin", entries: ["nav-users", "nav-external-triggers", "nav-semantic-layer"], sections: [] },
  { id: "settings", label: "Settings", entries: ["nav-settings-tokens", "nav-settings-notifications"], sections: [] },
];

const BUILD = MENU.find((group) => group.id === "build") as MenuGroup;

/** A group's element in the side bar, holding its own row and everything under it. */
const groupOf = (page: Page, id: string) => page.locator(`#sidebar-section-${id}`);

/** A group's own row: the branch of the menu's tree its entries and sections hang under. */
const groupHeader = (page: Page, id: string) => page.locator(`#sidebar-section-${id} > [role='treeitem']`);

/** A section's own row in its group: a branch of the menu's tree, with its entries under it. */
const sectionHeader = (group: Locator, groupId: string, section: { id: string; label: string }) =>
  group.getByTestId(`sidebar-part-${groupId}-${section.id}`).getByRole("treeitem", { name: section.label, exact: true });

const testIds = (entries: Locator) => entries.evaluateAll((elements) => elements.map((element) => element.getAttribute("data-testid")));

test.describe("navigation", () => {
  test("the side bar starts closed on a first visit with its menu folded, and from then on as it was left", async ({ page, request }) => {
    // A browser that holds no choice: signed in, and nothing else seeded.
    await seedSession(page, await adminSession(request));
    await page.goto("/");
    await expect(page.getByTestId("page-dashboard")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await expect(page.getByTestId("activity-build")).toBeVisible();
    await expect(groupOf(page, "build")).toHaveCount(0);

    // The activity bar opens the side bar on the group it names, which shows its sections folded; every other group is
    // folded to its header.
    const build = groupOf(page, "build");
    const foldedAsFirstOpened = async () => {
      await expect(build).toBeVisible();
      await expect(groupHeader(page, "build")).toHaveAttribute("aria-expanded", "true");
      for (const section of BUILD.sections) {
        await expect(sectionHeader(build, "build", section)).toHaveAttribute("aria-expanded", "false");
      }

      await expect(build.locator("[data-testid^='nav-']")).toHaveCount(0);
      for (const other of MENU.filter((group) => group.id !== "build")) {
        await expect(groupHeader(page, other.id)).toHaveAttribute("aria-expanded", "false");
        await expect(groupOf(page, other.id).locator("[data-testid^='nav-']")).toHaveCount(0);
      }
    };

    await page.getByTestId("activity-build").click();
    await foldedAsFirstOpened();

    await page.reload();
    await expect(page.getByTestId("page-dashboard")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await foldedAsFirstOpened();
  });

  test("the menu lists its groups, their sections and every entry in order, SQLFlow's pages and the module's together", async ({ adminPage }) => {
    const activity = await adminPage.locator("[data-testid^='activity-']").evaluateAll(
      (elements) => elements.map((element) => element.getAttribute("data-testid")),
    );
    expect(activity).toEqual(MENU.map((group) => `activity-${group.id}`));

    const sideBarGroups = await adminPage.locator("[id^='sidebar-section-']").evaluateAll((elements) => elements.map((element) => element.id));
    expect(sideBarGroups).toEqual(MENU.map((group) => `sidebar-section-${group.id}`));

    for (const group of MENU) {
      const element = groupOf(adminPage, group.id);
      await expect(groupHeader(adminPage, group.id)).toHaveAccessibleName(group.label);

      // The entries of no section first, then each section's, every entry of the group exactly once.
      expect(await testIds(element.locator("[data-testid^='nav-']"))).toEqual(
        [...group.entries, ...group.sections.flatMap((section) => section.entries)],
      );
      expect(await testIds(element.locator(`[data-testid^='sidebar-part-${group.id}-']`))).toEqual(
        group.sections.map((section) => `sidebar-part-${group.id}-${section.id}`),
      );
      for (const section of group.sections) {
        await expect(sectionHeader(element, group.id, section)).toBeVisible();
        expect(await testIds(element.getByTestId(`sidebar-part-${group.id}-${section.id}`).locator("[data-testid^='nav-']"))).toEqual(section.entries);
      }
    }

    // Each level sits one indent further in than the level above it: the group, its sections, their entries.
    const indent = (row: Locator) => row.evaluate((element) => Number.parseFloat(getComputedStyle(element).paddingLeft));
    const buildIndent = await indent(groupHeader(adminPage, "build"));
    for (const section of BUILD.sections) {
      const sectionIndent = await indent(sectionHeader(groupOf(adminPage, "build"), "build", section));
      expect(sectionIndent).toBeGreaterThan(buildIndent);
      expect(await indent(adminPage.getByTestId(section.entries[0]))).toBeGreaterThan(sectionIndent);
    }

    await expect(adminPage.getByTestId("nav-delivery-mapping-builder")).toHaveCount(0);
  });

  test("a section folds on its own, and stays as the person left it", async ({ adminPage }) => {
    const build = groupOf(adminPage, "build");
    const delivery = build.getByTestId("sidebar-part-build-delivery");
    const header = sectionHeader(build, "build", { id: "delivery", label: "Delivery" });
    const entries = delivery.locator("[data-testid^='nav-']");
    await expect(header).toHaveAttribute("aria-expanded", "true");

    await header.click();
    await expect(header).toHaveAttribute("aria-expanded", "false");
    await expect(entries).toHaveCount(0);
    await expect(adminPage.getByTestId("nav-pipelines")).toBeVisible();
    await expect(adminPage.getByTestId("nav-datasources")).toBeVisible();

    await adminPage.reload();
    await expect(adminPage.getByTestId("page-dashboard")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await expect(header).toHaveAttribute("aria-expanded", "false");
    await expect(entries).toHaveCount(0);

    await header.click();
    await expect(header).toHaveAttribute("aria-expanded", "true");
    expect(await testIds(entries)).toEqual(BUILD.sections.find((section) => section.id === "delivery")?.entries);
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

    // Down to the next entry, left to its group, left again folds the group, right opens it.
    const ledger = groupHeader(adminPage, "ledger");
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

  test("the command palette heads each part by its group, and a section's by the group and the section", async ({ adminPage }) => {
    await adminPage.keyboard.press("Control+k");
    const palette = adminPage.getByRole("dialog");
    await expect(palette).toBeVisible();
    for (const group of MENU) {
      if (group.entries.length > 0) {
        await expect(palette.getByText(group.label, { exact: true })).toBeVisible();
      }

      for (const section of group.sections) {
        await expect(palette.getByText(`${group.label} · ${section.label}`, { exact: true })).toBeVisible();
      }
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
