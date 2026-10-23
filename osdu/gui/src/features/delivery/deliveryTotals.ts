import { useQueries, useQuery } from "@tanstack/react-query";
import type { PipelineSummary } from "@/api/types";
import { fetchAllPipelines } from "@/features/pipelines/fetchAllPipelines";
import { deliveryApi, type DeliveryFlowStats } from "../../api/delivery";
import { useActivePartition } from "./activePartition";
import { useFollowDeliveryRuns } from "./followRuns";

/** What the delivery flows that deliver to the workbench's partition hold there, added up. */
export interface DeliveryTotals {
  total: number;
  delivered: number;
  /** Pending and being delivered. */
  pending: number;
  held: number;
  failed: number;
  drifted: number;
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
  total: 0, delivered: 0, pending: 0, held: 0, failed: 0, drifted: 0, deleted: 0, deliveredLast24h: 0, lastDeliveredUtc: null,
};

/**
 * Every delivery flow with its counts in the workbench's partition, and their totals: the Delivery page's cards and the
 * home page's section read the same. A flow that names or serves the partition is counted in it, unless its whole counts
 * already are that partition's (a flow of one partition, or one whose partition is its header's); a flow that delivers
 * only elsewhere is listed as such and left out of the totals. The counts poll on their own for what no run does (a
 * release, a removal), and a run's outcome reaches them through its flow's newest run, within seconds of it.
 */
export function useDeliveryTotals() {
  const flows = useQuery({
    queryKey: ["pipelines", "grouped", "", "delivery", ""],
    queryFn: () => fetchAllPipelines({ kind: "delivery" }),
  });
  const pipelines: PipelineSummary[] = flows.data?.items ?? [];
  const [active] = useActivePartition();
  useFollowDeliveryRuns(null, pipelines.length > 0);
  const whole = useQueries({
    queries: pipelines.map((pipeline) => ({
      queryKey: ["delivery", "stats", pipeline.id],
      queryFn: () => deliveryApi.stats(pipeline.id),
      refetchInterval: 15000,
    })),
  });
  const inActive = useQueries({
    queries: pipelines.map((pipeline, index) => {
      const counted = whole[index]?.data;
      return {
        queryKey: ["delivery", "stats", pipeline.id, null, active],
        queryFn: () => deliveryApi.stats(pipeline.id, { partition: active }),
        enabled: active !== null && (counted?.partitions ?? []).includes(active) && counted?.partition !== active,
        refetchInterval: 15000,
      };
    }),
  });
  const stats = pipelines.map((_, index) => (inActive[index]?.data !== undefined ? inActive[index] : whole[index]));
  const elsewhere = pipelines.map((_, index) => {
    const delivers = deliversTo(whole[index]?.data);
    return active !== null && delivers !== null && !delivers.includes(active);
  });

  const totals = stats.reduce<DeliveryTotals>((acc, query, index) => {
    const s = query?.data;
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
      deleted: acc.deleted + s.deleted,
      deliveredLast24h: acc.deliveredLast24h + s.deliveredLast24h,
      lastDeliveredUtc: s.lastDeliveredUtc !== null && (acc.lastDeliveredUtc === null || s.lastDeliveredUtc > acc.lastDeliveredUtc)
        ? s.lastDeliveredUtc
        : acc.lastDeliveredUtc,
    };
  }, NONE);
  // Counted once every flow that delivers here has answered, so a total never grows as the answers come in.
  const counted = flows.data !== undefined && stats.every((query, index) => elsewhere[index] || query?.data !== undefined);

  return { flows, pipelines, active, stats, elsewhere, totals, counted };
}
