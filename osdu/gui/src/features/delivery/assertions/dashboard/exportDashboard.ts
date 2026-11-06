// Downloads the assertion dashboard as one HTML file (osdu/docs/reference/flow/assertion-sets.md, Download the dashboard):
// the same component the workbench shows, with the data it shows and the results it opens, so the file works offline, in
// a board meeting or attached to an email, and needs nothing from the control plane.

import { saveFile } from "../assertionFormat";
import type { DashData, DashSnapshot, ResultFacts } from "./dashboardModel";
import { FILE_TOKENS, wornTokenStyle, type WornTokens } from "./dashboardTokens";

/** The results a read asks the control plane for at most. */
const PER_READ = 100;

function escapeHtml(text: string): string {
  return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

/** The file name the dashboard downloads as: the set, or every set, and the partition. */
export function dashboardFileName(set: string | null | undefined, partition: string | null): string {
  const what = set === undefined ? "all-sets" : set ?? "no-set";
  return `${`${what}-${partition ?? "partition"}-assertion-dashboard`.replace(/[^A-Za-z0-9._-]+/g, "_")}.html`;
}

/**
 * The values the file's tokens hold in the design worn, in its light and its dark mode, read off the workbench's <html>.
 * The mode is switched there and back within this one task, which the browser never paints between.
 */
function readWornTokens(): WornTokens {
  const root = document.documentElement;
  const read = () => {
    const computed = getComputedStyle(root);
    return Object.fromEntries(FILE_TOKENS.map((name) => [name, computed.getPropertyValue(name)]));
  };
  const wasDark = root.classList.contains("dark");
  root.classList.remove("dark");
  const light = read();
  root.classList.add("dark");
  const dark = read();
  root.classList.toggle("dark", wasDark);
  return { light, dark };
}

/**
 * The dashboard's page: its styles, with the design worn in the workbench put over dashboard.css's own values, the
 * snapshot as JSON a script cannot break out of, and the bundled script that renders it. Disabled assertions travel with
 * the file, so its reader can include them as the workbench's can.
 */
export function dashboardPage(snapshot: DashSnapshot, script: string, style: string, worn: WornTokens): string {
  const title = ["Assertions", snapshot.set === undefined ? null : snapshot.set ?? "No set", snapshot.data.partition].filter((part) => part !== null).join(", ");
  // In a script element only "</script" ends it; JSON escapes "<" so no value can, and the bundle's own text is guarded alike.
  const json = JSON.stringify(snapshot).replace(/</g, "\\u003c");
  return [
    "<!doctype html>",
    `<html lang="en" class="adb-standalone${snapshot.dark ? " dark" : ""}">`,
    "<head>",
    '<meta charset="utf-8">',
    '<meta name="viewport" content="width=device-width, initial-scale=1">',
    `<title>${escapeHtml(title)}</title>`,
    `<style>${`${style}\n${wornTokenStyle(worn)}`.replace(/<\/style/gi, "<\\/style")}</style>`,
    "</head>",
    "<body>",
    '<div id="root"></div>',
    `<script type="application/json" id="assertion-dashboard-data">${json}</script>`,
    `<script>${script.replace(/<\/script/gi, "<\\/script")}</script>`,
    "</body>",
    "</html>",
    "",
  ].join("\n");
}

/**
 * Builds the dashboard of a set, or of every set, with the result of every assertion in it that has one, and offers it as
 * a file to save.
 */
export async function downloadDashboard(options: {
  data: DashData;
  set: string | null | undefined;
  includeDisabled: boolean;
  loadResults: (resultIds: readonly number[]) => Promise<{ resultId: number; result: ResultFacts }[]>;
}): Promise<void> {
  const { data, set } = options;
  const tests = set === undefined ? data.tests : data.tests.filter((test) => test.set === set);
  const flows = set === undefined ? data.flows : data.flows.filter((flow) => flow.set === set);
  const ids = tests.map((test) => test.resultId).filter((id): id is number => id !== null);
  const results: Record<string, ResultFacts> = {};
  for (let at = 0; at < ids.length; at += PER_READ) {
    for (const row of await options.loadResults(ids.slice(at, at + PER_READ))) {
      results[String(row.resultId)] = row.result;
    }
  }

  const { script, style } = await import("virtual:assertion-dashboard-export");
  const snapshot: DashSnapshot = {
    data: { ...data, flows, tests },
    set,
    includeDisabled: options.includeDisabled,
    results,
    dark: document.documentElement.classList.contains("dark"),
  };
  saveFile(dashboardPage(snapshot, script, style, readWornTokens()),"text/html;charset=utf-8", dashboardFileName(set, data.partition));
}
