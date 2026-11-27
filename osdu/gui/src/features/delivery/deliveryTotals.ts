import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import type { PipelineSummary } from "@/api/types";
import { fetchAllPipelines } from "@/features/pipelines/fetchAllPipelines";
import { deliveryApi, type DeliveryFlowCounts, type DeliveryFlowProblem, type DeliveryFlowStats } from "../../api/delivery";
import { useActivePartition } from "./activePartition";
import { restrictedFigure } from "./recordAccess";
import { FLOW_COUNTS_KEY, useFollowDeliveryRuns } from "./followRuns";

/** What the delivery flows that deliver to the workbench's partition hold there, added up. */
export interface DeliveryTotals {
  total: number;
  delivered: number;
  /** Pending and being delivered. */
  pending: number;
  held: number;
  failed: number;
  drifted: number;
  /**
   * Records whose access in OSDU is restricted either way, until complete or by an operator, each once: the Restricted count.
   * Each is counted under its status too, so it is no part of the total.
   */
  accessRestricted: number;
  /** Records the ledger has taken out of OSDU. */
  deleted: number;
  deliveredLast24h: number;
  /** The newest delivery among them, or null when none has delivered. */
  lastDeliveredUtc: string | null;
}

/**
 * The partitions a flow's counts say it delivers to: those it names or serves, or the one its header's ledger is kept
 * under; null while that is not known (a flow whose partition is its header's, before it has run).
 */
export function deliversTo(stats: DeliveryFlowStats | undefined): string[] | null {
  if (stats === undefined) {
    return null;
  }

  return stats.partitions ?? (stats.headerPartition ? [stats.headerPartition] : null);
}

const NONE: DeliveryTotals = {
  total: 0, delivered: 0, pending: 0, held: 0, failed: 0, drifted: 0, accessRestricted: 0, deleted: 0, deliveredLast24h: 0, lastDeliveredUtc: null,
};

/** How often every flow's counts are read for what no run does (a release, a removal); a run's own moves reach them sooner. */
const COUNTS_POLL_MS = 15_000;

/**
 * Every delivery flow with its counts in the workbench's partition, and their totals: the Delivery page's cards and the
 * home page's section read the same. A flow that names or serves the partition is counted in it, unless its whole counts
 * already are that partition's (a flow of one partition, or one whose partition is its header's); a flow that delivers
 * only elsewhere is listed as such and left out of the totals, and so is a flow whose counts could not be read, which says
 * why (`problems`). Every flow's counts come in one call (GET /delivery/flows/stats), polled on their own for what no run
 * does (a release, a removal); a run's outcome reaches them through its flow's newest run, within seconds of it.
 */
export function useDeliveryTotals() {
  const flows = useQuery({
    queryKey: ["pipelines", "grouped", "", "delivery", ""],
    queryFn: () => fetchAllPipelines({ kind: "delivery" }),
  });
  const pipelines: PipelineSummary[] = useMemo(() => flows.data?.items ?? [], [flows.data]);
  const [active] = useActivePartition();
  useFollowDeliveryRuns(null, pipelines.length > 0);
  const counts = useQuery({
    queryKey: [...FLOW_COUNTS_KEY, active],
    queryFn: () => deliveryApi.flowCounts(active),
    enabled: pipelines.length > 0,
    refetchInterval: COUNTS_POLL_MS,
  });

  const entries = useMemo(() => {
    const byFlow = new Map<string, DeliveryFlowCounts>((counts.data ?? []).map((entry) => [entry.pipelineId, entry]));
    return pipelines.map((pipeline) => byFlow.get(pipeline.id));
  }, [counts.data, pipelines]);
  const stats: (DeliveryFlowStats | undefined)[] = entries.map((entry) => entry?.inPartition ?? entry?.stats ?? undefined);
  const problems: (DeliveryFlowProblem | null)[] = entries.map((entry) => (entry !== undefined && entry.stats === null ? entry.problem ?? null : null));
  const elsewhere = entries.map((entry) => {
    const delivers = deliversTo(entry?.stats ?? undefined);
    return active !== null && delivers !== null && !delivers.includes(active);
  });

  const totals = stats.reduce<DeliveryTotals>((acc, s, index) => {
    if (s === undefined || elsewhere[index]) {
      return acc;
    }

    return {
      total: acc.total + s.total,
      delivered: acc.delivered + s.delivered,
      pending: acc.pending + s.pending + s.delivering,
      held: acc.held + s.held,
      failed: acc.failed + s.failed,
      drifted: acc.drifted + s.drifted,
      accessRestricted: acc.accessRestricted + restrictedFigure(s).total,
      deleted: acc.deleted + s.deleted,
      deliveredLast24h: acc.deliveredLast24h + s.deliveredLast24h,
      lastDeliveredUtc: s.lastDeliveredUtc !== null && (acc.lastDeliveredUtc === null || s.lastDeliveredUtc > acc.lastDeliveredUtc)
        ? s.lastDeliveredUtc
        : acc.lastDeliveredUtc,
    };
  }, NONE);
  // Every flow's counts answer at once, so a total never grows as they come in; a flow synced since they were read is
  // counted at their next read.
  const counted = flows.data !== undefined && counts.data !== undefined;
  const uncounted = problems.filter((problem) => problem !== null).length;

  return { flows, counts, pipelines, active, stats, problems, elsewhere, totals, counted, uncounted };
}
