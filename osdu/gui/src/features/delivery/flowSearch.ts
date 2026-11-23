import { rowsMatching } from "./rowSearch.ts";

/** What the Delivery page's filter reads of a delivery flow: what its card shows, and where its document sits. */
export interface SearchedFlow {
  name: string;
  /** The flow document's path in its repository, whose folders are the project it belongs to. */
  relativePath: string;
  /** The batch its card names; null for the default batch, which the card leaves out. */
  batch: string | null;
  /** False for an inactive flow, whose card says so. */
  active: boolean;
  /** The partitions it delivers to, as its card names them; empty until its counts answer. */
  partitions: readonly string[];
  /** The line under its name (its verdict, or where it delivers instead); null until its counts answer. */
  headline: string | null;
}

/** Everything a flow's card shows of it, as the text the filter reads, so a reader finds a flow by what they read off its card. */
export function flowSearchText(flow: SearchedFlow): string {
  return [
    flow.name,
    flow.relativePath,
    flow.batch ?? "",
    flow.active ? "" : "inactive",
    flow.partitions.join(" "),
    flow.headline ?? "",
  ].join(" ");
}

/** The flows a filter names, in the order given: every word typed is found in what the flow's card shows. */
export function matchingFlows<T extends SearchedFlow>(flows: readonly T[], search: string): T[] {
  return rowsMatching(flows, search, flowSearchText);
}

/** The line over the cards while a filter narrows them: how many of the page's flows it shows. */
export function filteredLine(shown: number, total: number, search: string): string {
  return `${shown.toLocaleString()} of ${total.toLocaleString()} delivery flow${total === 1 ? "" : "s"} match '${search}'.`;
}
