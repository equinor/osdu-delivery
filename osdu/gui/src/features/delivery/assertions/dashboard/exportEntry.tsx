import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { AssertionDashboard } from "./AssertionDashboard";
import type { DashSnapshot } from "./dashboardModel";

// The script a downloaded dashboard runs (exportDashboard.ts writes the page): the same dashboard the workbench shows,
// over the data and results the page carries, with nothing to fetch. vite.config.ts bundles it into one script.

const holder = document.getElementById("assertion-dashboard-data");
const mount = document.getElementById("root");
if (holder === null || mount === null) {
  throw new Error("This page carries no assertion dashboard: it has no #assertion-dashboard-data and #root to render it from.");
}

const snapshot = JSON.parse(holder.textContent ?? "") as DashSnapshot;

createRoot(mount).render(
  <StrictMode>
    <AssertionDashboard
      data={snapshot.data}
      title="Assertions"
      set={snapshot.set}
      showSets
      initialIncludeDisabled={snapshot.includeDisabled}
      results={snapshot.results}
      standalone
    />
  </StrictMode>,
);
