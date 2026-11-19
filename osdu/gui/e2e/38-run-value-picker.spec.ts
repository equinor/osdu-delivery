import type { Page } from "@playwright/test";
import { E2E } from "../playwright.config";
import { DELIVERY_FLOW, REPO_NAME, SOURCE } from "./global-setup";
import { expect, test } from "./helpers";

// Picking several values from a lookup in the run form (sqlflow/docs/reference/concepts/run-inputs.md, The GUI): the list
// holds still under the pointer while the chips wrap onto another line. Below the field, the field grows into the list;
// above it, the field takes the focus back after each pick, which scrolled the form to an input the chips had wrapped below
// its edge. Either way the list used to move under a still pointer, and the next click dropped a value picked already or
// closed the list. The estate's record table holds one log source, so the list is answered with the ten a recall estate's
// holds; nothing is triggered.

const FIELD = "trigger-parameter-logSource";

const LOG_SOURCES: readonly (readonly [string, number])[] = [
  ["STAT_COMP", 5462], ["STAT_CPI", 4779], ["STAT_PRESS", 1886], ["STAT_WI", 1713], ["STAT_CORE", 167],
  ["STAT_PLT", 143], ["STAT_ILT", 68], ["STAT_DIP", 60], ["STAT_DIP_ROTATED", 9], ["STAT_PN", 6],
];

/** Answers the delivery flow's logSource lookup with the ten log sources, as the control plane answers one. */
async function answerLogSources(page: Page): Promise<void> {
  await page.route(`${E2E.apiBaseUrl}/api/v1/pipelines/*/parameters/logSource/values`, async (route) => {
    const request = route.request();
    const cors = {
      "access-control-allow-origin": request.headers()["origin"] ?? "*",
      "access-control-allow-credentials": "true",
      "access-control-allow-headers": "*",
      "access-control-allow-methods": "*",
    };
    if (request.method() === "OPTIONS") {
      return route.fulfill({ status: 204, headers: cors });
    }

    return route.fulfill({
      status: 200,
      headers: { ...cors, "content-type": "application/json" },
      body: JSON.stringify({
        parameter: "logSource",
        source: "column log_source",
        values: LOG_SOURCES.map(([value, count]) => ({ value, count })),
        more: false,
      }),
    });
  });
}

/** Opens the run form on the delivery flow's plan, whose logSource takes several log sources. */
async function openDeliveryPlan(page: Page): Promise<void> {
  await page.getByTestId("nav-runs").click();
  await expect(page.getByTestId("page-runs")).toBeVisible();
  await page.getByTestId("open-trigger-run").click();
  await expect(page.getByTestId("trigger-run-dialog")).toBeVisible();
  await page.getByTestId("trigger-repo").fill(REPO_NAME);
  await page.getByRole("option", { name: REPO_NAME }).click();
  await page.getByTestId("trigger-flow").fill(SOURCE);
  await page.getByRole("option", { name: DELIVERY_FLOW, exact: true }).click();
  await page.getByTestId("trigger-operation").click();
  await page.getByRole("option", { name: /^Plan/ }).click();
  await formSettled(page);
}

/**
 * Waits until the form stops changing: the flow's parameters, the control plane's explanation (which names logSource as
 * required) and the flow's schedules arrive one after another, and each moves the field.
 */
async function formSettled(page: Page): Promise<void> {
  await expect(page.getByTestId(`${FIELD}-problem`)).toBeVisible();
  let previous = "";
  await expect.poll(async () => {
    const now = await page.getByTestId(FIELD).evaluate((input) => {
      // The form is the field's nearest ancestor that scrolls; what it holds grows as each answer arrives.
      let form = input.parentElement;
      while (form !== null && getComputedStyle(form).overflowY !== "auto") {
        form = form.parentElement;
      }

      const rect = input.getBoundingClientRect();
      return `${rect.top}:${rect.height}:${form?.scrollHeight ?? 0}`;
    });
    const same = now === previous;
    previous = now;
    return same;
  }, { message: "the run form stops changing", intervals: [250] }).toBe(true);
}

interface Placement {
  open: boolean;
  side: string | null;
  listTop: number;
  listBottom: number;
  fieldTop: number;
  fieldBottom: number;
  formScroll: number;
  picked: number;
}

/** Where the list and the field are, how far the form is scrolled, and how many values the field holds. */
function placement(page: Page): Promise<Placement> {
  return page.evaluate((field) => {
    const input = document.getElementById(field);
    const list = document.querySelector(`[data-testid="${field}-values"]`);
    if (input === null || input.parentElement === null) {
      throw new Error(`The field ${field} is not on the page.`);
    }

    // The form is the field's nearest ancestor that scrolls.
    let form: HTMLElement | null = input.parentElement;
    while (form !== null && getComputedStyle(form).overflowY !== "auto") {
      form = form.parentElement;
    }

    if (form === null) {
      throw new Error(`No ancestor of ${field} scrolls.`);
    }

    const fieldRect = input.parentElement.getBoundingClientRect();
    const listRect = list?.getBoundingClientRect();
    return {
      open: list !== null,
      side: list?.getAttribute("data-side") ?? null,
      listTop: listRect?.top ?? Number.NaN,
      listBottom: listRect?.bottom ?? Number.NaN,
      fieldTop: fieldRect.top,
      fieldBottom: fieldRect.bottom,
      formScroll: form.scrollTop,
      picked: document.querySelectorAll(`[data-testid="${field}-picked"]`).length,
    };
  }, FIELD);
}

/**
 * Picks each log source in turn as a person does, the pointer on its name and the button held a moment, and checks after
 * every pick that it is held, that the list is open where it was and that the form did not scroll to the field. Then
 * checks that the picks wrapped the chips onto another line, and that the list goes back against the field once the
 * pointer leaves it.
 */
async function pickEachWithoutTheListMoving(page: Page, side: "top" | "bottom"): Promise<void> {
  await page.getByTestId(FIELD).click();
  const list = page.getByTestId(`${FIELD}-values`);
  await expect(list.getByTestId(`${FIELD}-value`)).toHaveCount(LOG_SOURCES.length);
  const opened = await placement(page);
  expect(opened.side).toBe(side);

  for (const [index, [value]] of LOG_SOURCES.slice(0, 7).entries()) {
    const name = list.getByTestId(`${FIELD}-value`).filter({ hasText: value }).getByText(value, { exact: true });
    const box = await name.boundingBox();
    if (box === null) {
      throw new Error(`The list shows no ${value}.`);
    }

    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2, { steps: 4 });
    const before = await placement(page);
    await page.mouse.down();
    await page.waitForTimeout(120);
    await page.mouse.up();
    await expect(page.getByTestId(`${FIELD}-picked`)).toHaveCount(index + 1);
    // What a pick sets off settles in a frame or two; the list is checked after it has had them.
    await page.waitForTimeout(300);
    const after = await placement(page);
    expect(after.open, `the list is open after picking ${value}`).toBe(true);
    expect(after.listTop, `the list holds still under the pointer as ${value} is picked`).toBe(before.listTop);
    // A pick can change the form around the field (the problem it named goes once a value is given), and a form scrolled
    // to its end moves up as it shrinks. What a pick must not do is scroll the form down to the field's input.
    expect(after.formScroll, `the form does not scroll to the field as ${value} is picked`)
      .toBeLessThanOrEqual(before.formScroll);
  }

  const picked = await placement(page);
  expect(picked.picked).toBe(7);
  expect(picked.fieldBottom - picked.fieldTop, "the picks wrapped the chips onto another line").toBeGreaterThan(
    opened.fieldBottom - opened.fieldTop,
  );

  // Off the list, it follows the field again, 4 px from its top edge above it or its bottom edge below it (placed on whole
  // pixels, so within one of that).
  await page.mouse.move(10, 10, { steps: 4 });
  await expect.poll(async () => {
    const left = await placement(page);
    const gap = side === "top" ? left.fieldTop - left.listBottom : left.listTop - left.fieldBottom;
    return Math.abs(gap - 4) <= 1;
  }, { message: "the list sits against the field once the pointer leaves it" }).toBe(true);
}

test.describe("picking several run values", () => {
  test.describe("with the whole form in the window", () => {
    // Tall enough for the form to need no scrolling, so the list has the room to open under the field.
    test.use({ viewport: { width: 1280, height: 1600 } });

    test("a list under the field holds still as the chips wrap into it", async ({ adminPage }) => {
      await answerLogSources(adminPage);
      await openDeliveryPlan(adminPage);
      await pickEachWithoutTheListMoving(adminPage, "bottom");
    });
  });

  test("a list over the field holds still, and the form stays put, as the chips wrap past the form's edge", async ({ adminPage }) => {
    await answerLogSources(adminPage);
    await openDeliveryPlan(adminPage);
    await adminPage.getByTestId(FIELD).evaluate((input) => input.parentElement?.scrollIntoView({ block: "end" }));
    await formSettled(adminPage);
    await pickEachWithoutTheListMoving(adminPage, "top");
  });
});
