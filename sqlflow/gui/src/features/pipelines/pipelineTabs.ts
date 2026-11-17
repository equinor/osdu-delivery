/** A tab of a pipeline's page, a kind's own or a built-in one, by the value the address names it with (`?tab=`). */
export interface PipelineTabEntry {
  value: string;
}

/**
 * The built-in tabs and header facts that say nothing of a lineage description (flowType: lin). It is a document that
 * describes a step another system runs: it has no run, no file, no schedule and no transform of its own, no server it
 * connects to and no wave. Its page is its document.
 */
export const DESCRIPTION_HIDDEN_TABS: readonly string[] = ["transforms", "runs", "files", "schedules"];
export const DESCRIPTION_HIDDEN_FACTS: readonly string[] = ["wave", "sourceServer", "targetServer"];

/**
 * The built-in tabs and header facts that say nothing of a report (flowType: rpt). It is a document of read-only
 * queries run from the report's own page: it has no run, no file, no schedule and no transform, writes to no server and
 * is in no wave. It does read a connection, so the source server stays.
 */
export const REPORT_HIDDEN_TABS: readonly string[] = DESCRIPTION_HIDDEN_TABS;
export const REPORT_HIDDEN_FACTS: readonly string[] = ["wave", "targetServer"];

/**
 * A pipeline page's tabs in the order its kind asks (`FlowKindContribution.pipelineTabOrder`): the tabs it names, in its
 * order, then the others as they come (the kind's own, then the built-in ones). A name that is no tab of the page, such
 * as a tab the kind hides or one this version does not have, is passed over, and a name given twice keeps its first
 * place, so an order written against one version of the page keeps working on the next.
 */
export function orderPipelineTabs<T extends PipelineTabEntry>(tabs: readonly T[], order: readonly string[] | undefined): T[] {
  if (order === undefined || order.length === 0) {
    return [...tabs];
  }

  const rank = new Map<string, number>();
  for (const value of order) {
    if (!rank.has(value)) {
      rank.set(value, rank.size);
    }
  }

  const named = tabs.filter((tab) => rank.has(tab.value));
  named.sort((a, b) => (rank.get(a.value) ?? 0) - (rank.get(b.value) ?? 0));
  return [...named, ...tabs.filter((tab) => !rank.has(tab.value))];
}
