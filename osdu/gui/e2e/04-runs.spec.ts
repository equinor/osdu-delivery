import { DELIVERY_FLOW, LOG_SOURCE, REPO_NAME, SOURCE } from "./global-setup";
import { expect, test } from "./helpers";

// The run lifecycle from the GUI: trigger a plan of the delivery flow, watch the in-process worker execute it, and
// check the run page records what was asked.
//
// The plan operation renders the flow's records against the templates of the flow's template folders, the partition cache and the ledger,
// and asks the platform's search for the wellbore each log names, which the suite's OSDU stand-in answers; nothing here
// writes to OSDU. It does read the flow's ingestion tables through the connection its document declares
// (source.connection), so the sample database the pre and ingestion flows load has to be reachable from the control
// plane's node for this run to succeed.

test.describe.serial("runs", () => {
  test("trigger a plan run and watch it succeed with populated drill-downs", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-runs").click();
    await expect(adminPage.getByTestId("page-runs")).toBeVisible();

    await adminPage.getByTestId("open-trigger-run").click();
    await expect(adminPage.getByTestId("trigger-run-dialog")).toBeVisible();
    await adminPage.getByTestId("trigger-repo").fill(REPO_NAME);
    await adminPage.getByRole("option", { name: REPO_NAME }).click();
    await adminPage.getByTestId("trigger-flow").fill(SOURCE);
    // Exactly: the estate holds the pre and ingestion flows too, whose names start with the delivery flow's.
    await adminPage.getByRole("option", { name: DELIVERY_FLOW, exact: true }).click();
    // The flow declares logSource as required: it fills the scope predicate the plan reads the ingestion table with.
    await adminPage.getByTestId("trigger-operation").click();
    await adminPage.getByRole("option", { name: /^Plan/ }).click();
    // A delivery flow's parameters are fields; the one its scope reads offers the log sources the record table holds.
    await adminPage.getByTestId("trigger-parameter-logSource").fill(LOG_SOURCE);
    await adminPage.getByTestId("trigger-parameters").getByText("Flow parameters").click();
    await adminPage.getByTestId("trigger-submit").click();

    // 202 accepted: the dialog navigates to the run detail, which polls to a terminal state.
    await expect(adminPage.getByTestId("page-run-detail")).toBeVisible({ timeout: 15_000 });
    await expect(adminPage.getByTestId("status-badge").filter({ hasText: "succeeded" }).first())
      .toBeVisible({ timeout: 180_000 });

    // The run page names the kind of flow it ran, as its pipeline's page does.
    await expect(adminPage.getByTestId("run-kind")).toHaveText("Delivery");
    await expect(adminPage.getByTestId("run-kind")).toHaveAttribute("data-kind", "delivery");

    // The run records what was asked: the operation and the flow parameter show on the run page.
    await expect(adminPage.getByTestId("run-parameters")).toBeVisible();
    await expect(adminPage.getByTestId("run-operation")).toHaveText("plan");
    await expect(adminPage.getByTestId("run-parameters").getByText(`logSource=${LOG_SOURCE}`)).toBeVisible();
  });
});
