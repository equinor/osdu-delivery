import { useEffect, useMemo, useRef } from "react";
import { useQuery, useQueryClient, type Query } from "@tanstack/react-query";
import { runApi } from "@/api/endpoints";
import type { RunSummary } from "@/api/types";
import { pollingInterval } from "@/hooks/usePolling";
import { moved, runsUnderWay, signatures, underWay } from "./followRunsState";

/** The query key of the probe, which an action that queues a run asks again at once so the page shows the run. */
export const FOLLOW_RUNS_KEY = "delivery-run-follow";

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
 * board's own listing, every five seconds, and every three while a run is queued or running. While a flow's run is under
 * way, its counts and submissions are read again at each probe, so they move with the run; when one of a flow's runs
 * starts, moves on or ends, every delivery view of that flow is read again at once. A page then shows what a run did within
 * seconds of it, not at its own next poll, and asks for no more than the probe while nothing runs.
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
    refetchInterval: (query) => pollingInterval((query.state.data?.items ?? []).some(underWay) ? 3000 : 5000)(),
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

    for (const flow of new Set(runsUnderWay(items).map((run) => run.pipelineId))) {
      if (!changed.has(flow)) {
        void queryClient.invalidateQueries({ queryKey: ["delivery", "stats", flow] });
        void queryClient.invalidateQueries({ queryKey: ["delivery", "submissions", flow] });
      }
    }
  }, [runs.data, queryClient]);

  return useMemo(() => runsUnderWay(runs.data?.items ?? []), [runs.data]);
}
