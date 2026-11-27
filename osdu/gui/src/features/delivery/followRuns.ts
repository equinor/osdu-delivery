import { useEffect, useMemo, useRef } from "react";
import { useQuery, useQueryClient, type Query } from "@tanstack/react-query";
import { runApi } from "@/api/endpoints";
import type { RunSummary } from "@/api/types";
import { countsDue, moved, runsUnderWay, signatures, underWay } from "./followRunsState";

/** The query key of the probe, which an action that queues a run asks again at once so the page shows the run. */
export const FOLLOW_RUNS_KEY = "delivery-run-follow";

/**
 * The query key every delivery flow's counts are read under, in one call (useDeliveryTotals), followed by the partition
 * they are counted in: what the Delivery page's cards and the home page's totals show.
 */
export const FLOW_COUNTS_KEY = ["delivery", "flow-counts"] as const;

/**
 * How many of one flow's newest runs the probe reads: the run asked for last, and enough beside it that the parts a
 * delivery split over nodes, and a run queued behind another, are counted among the runs under way.
 */
const FLOW_RUNS_READ = 10;

/** Whether a delivery query reads what the flow of `pipelineId` holds: its counts, submissions, records and the rest. */
function readsFlow(query: Query, pipelineId: string): boolean {
  return query.queryKey[0] === "delivery" && query.queryKey.includes(pipelineId);
}

/**
 * Keeps a page's delivery views in step with the runs of the flows it shows, on an API that is polled rather than pushed.
 * One probe reads the newest runs of one flow (`pipelineId`) or the newest run of every delivery flow (null), the run
 * board's own listing, every five seconds, and every three while a run is queued or running. When one of a flow's runs
 * starts, moves on or ends, every delivery view of that flow, and every flow's counts, are read again at once. While a
 * flow's run is under way, its counts and submissions (and every flow's counts) are read again whenever they have grown
 * older than the running cadence (countsDue), so they move with the run while the probe asks for them no oftener than
 * that. A page then shows what a run did within seconds of it, and asks for no more than the probe while nothing runs.
 *
 * It answers the runs under way, newest first, which a flow's page shows above its tabs' content (RunsUnderWay).
 */
export function useFollowDeliveryRuns(pipelineId: string | null, enabled = true): RunSummary[] {
  const queryClient = useQueryClient();
  const seen = useRef<Map<string, string> | null>(null);
  const runs = useQuery({
    queryKey: [FOLLOW_RUNS_KEY, pipelineId ?? "every"],
    queryFn: () => (pipelineId === null
      ? runApi.list({ flowKind: "delivery", latest: true, pageSize: 200 })
      : runApi.list({ pipelineId, pageSize: FLOW_RUNS_READ })),
    enabled,
    refetchInterval: (query) => ((query.state.data?.items ?? []).some(underWay) ? 3000 : 5000),
  });

  useEffect(() => {
    const items = runs.data?.items;
    if (items === undefined) {
      return;
    }

    const now = signatures(items);
    const changed = new Set(seen.current === null ? [] : moved(seen.current, now));
    seen.current = now;
    for (const flow of changed) {
      void queryClient.invalidateQueries({ predicate: (query) => readsFlow(query, flow) });
    }

    if (changed.size > 0) {
      void queryClient.invalidateQueries({ queryKey: FLOW_COUNTS_KEY });
    }

    const running = [...new Set(runsUnderWay(items).map((run) => run.pipelineId))].filter((flow) => !changed.has(flow));
    if (running.length === 0) {
      return;
    }

    // Only what has grown older than the running cadence is read again, so a probe every three seconds asks for each count
    // no oftener than that, whichever of the probe, a page's own poll or an action read it last.
    const clock = Date.now();
    const due = (query: Query) => countsDue(query.state.dataUpdatedAt, clock);
    for (const flow of running) {
      void queryClient.invalidateQueries({ queryKey: ["delivery", "stats", flow], predicate: due });
      void queryClient.invalidateQueries({ queryKey: ["delivery", "submissions", flow], predicate: due });
    }

    if (changed.size === 0) {
      void queryClient.invalidateQueries({ queryKey: FLOW_COUNTS_KEY, predicate: due });
    }
  }, [runs.data, queryClient]);

  return useMemo(() => runsUnderWay(runs.data?.items ?? []), [runs.data]);
}
