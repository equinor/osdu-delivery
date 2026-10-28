import { useNavigate } from "react-router-dom";
import { PackageCheck } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "@/api/client";
import type { DeliveryFlowStats } from "../../api/delivery";
import type { PipelineSummary } from "@/api/types";
import { CorrelationError } from "@/components/CorrelationError";
import { EmptyState } from "@/components/EmptyState";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { SubmissionStatusBadge } from "./DeliveryBadges";
import { Count } from "./DeliveryCounts";
import { deliversTo, useDeliveryTotals, type DeliveryTotals } from "./deliveryTotals";
import { FindRecordForm } from "./FindRecordForm";
import { OrphanedLedgersNotice } from "./OrphanedLedgersNotice";

/**
 * One delivery flow's card, in the workbench's partition. A flow that delivers there is counted there, and its partition
 * badges mark that one; a flow that delivers only to other partitions says where, and shows no counts, since none of its
 * records are the workbench partition's.
 */
function FlowCard({ pipeline, stats, active, elsewhere, onOpen }: {
  pipeline: PipelineSummary;
  stats: DeliveryFlowStats | undefined;
  active: string | null;
  elsewhere: boolean;
  onOpen: () => void;
}) {
  const partitions = deliversTo(stats) ?? [];
  const counted = stats?.partition ?? stats?.headerPartition ?? null;
  const total = stats?.total ?? 0;
  const delivered = stats?.delivered ?? 0;
  const ratio = total === 0 ? 0 : Math.round((delivered / total) * 100);
  return (
    <Card
      className={`cursor-pointer gap-3 rounded-lg p-4 hover:bg-muted/40 ${elsewhere ? "opacity-60" : ""}`}
      onClick={onOpen}
      data-testid={`delivery-flow-${pipeline.id}`}
      data-elsewhere={elsewhere ? "true" : undefined}
    >
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-mono text-[13px] font-medium">{pipeline.name}</span>
        <Badge variant="outline">{pipeline.batch ?? "default"}</Badge>
        {!pipeline.active && <Badge variant="outline" className="border-warning/50 text-warning">inactive</Badge>}
        {stats && stats.drifted > 0 && <Badge variant="secondary" className="bg-warning/15 text-warning">{stats.drifted} drifted</Badge>}
        {stats && (stats.stepsHeld ?? 0) > 0 && (
          <Badge variant="outline" title="Records an enrichment held in and was set aside on: each went ahead with what the enrichment added to its last delivery" data-testid={`delivery-flow-steps-held-${pipeline.id}`}>
            {`${(stats.stepsHeld ?? 0).toLocaleString()} with a step held`}
          </Badge>
        )}
        {partitions.length > 0 && (
          <span
            className="ml-auto flex flex-wrap items-center gap-1"
            title={elsewhere
              ? `The partitions this flow delivers to; ${active} is not one of them.`
              : counted !== null
                ? `The partitions this flow delivers to; the counts are ${counted}'s.`
                : "The partitions this flow delivers to; the counts add every partition's ledger up."}
            data-testid={`delivery-flow-${pipeline.id}-partitions`}
          >
            {partitions.map((name) => (
              <Badge
                key={name}
                variant={name === counted ? "default" : "secondary"}
                className="font-mono text-[11px]"
              >
                {name}
              </Badge>
            ))}
          </span>
        )}
      </div>
      {stats === undefined ? (
        <Skeleton className="h-14 w-full" />
      ) : elsewhere ? (
        <p className="text-[12px] text-muted-foreground" data-testid={`delivery-flow-${pipeline.id}-elsewhere`}>
          Delivers to {partitions.join(", ")}, not {active}. Pick one of those in the title bar for its counts.
        </p>
      ) : (
        <>
          <div className="h-2 w-full overflow-hidden rounded bg-muted">
            <div className="h-full bg-success" style={{ width: `${ratio}%` }} />
          </div>
          <div className="grid grid-cols-3 gap-3 sm:grid-cols-6">
            <Count label="Records" value={stats.total} />
            <Count label="Delivered" value={stats.delivered} tone="success" />
            <Count label="Pending" value={stats.pending + stats.delivering} tone="info" />
            <Count label="Held" value={stats.held} tone="warning" />
            <Count label="Failed" value={stats.failed} tone="destructive" />
            <Count label="Last 24h" value={stats.deliveredLast24h} />
          </div>
          <div className="flex flex-wrap items-center gap-2 text-[12px] text-muted-foreground">
            <span>{ratio}% delivered</span>
            <span>last delivery <RelativeTime value={stats.lastDeliveredUtc} /></span>
            <span>last verify <RelativeTime value={stats.lastVerifiedUtc} /></span>
            {stats.lastSubmission && (
              <span className="inline-flex items-center gap-1">
                last submission <SubmissionStatusBadge status={stats.lastSubmission.status} testId={`delivery-flow-${pipeline.id}-submission`} />
                <RelativeTime value={stats.lastSubmission.receivedUtc} />
              </span>
            )}
          </div>
        </>
      )}
    </Card>
  );
}

/** The page's one line of totals: the flows that deliver to the workbench's partition, and what they hold there. */
function subtitle(flows: number, active: string | null, totals: DeliveryTotals) {
  const where = active === null ? "" : ` in ${active}`;
  return `${flows} delivery flow${flows === 1 ? "" : "s"}${where}: ${totals.delivered.toLocaleString()} of ${totals.total.toLocaleString()} records delivered, `
    + `${totals.pending.toLocaleString()} pending, ${totals.held.toLocaleString()} held, ${totals.failed.toLocaleString()} failed, ${totals.drifted.toLocaleString()} drifted.`;
}

/**
 * Every delivery flow with what it has delivered in the workbench's partition: the "uploaded versus not" view. A flow that
 * delivers only to other partitions is listed, dimmed, with where it delivers, and left out of the totals.
 */
export default function DeliveryOverviewPage() {
  const navigate = useNavigate();
  const { flows, pipelines, active, stats, elsewhere, totals } = useDeliveryTotals();

  if (flows.isError) {
    return (
      <Page data-testid="page-delivery">
        {isApiError(flows.error) ? <CorrelationError error={flows.error} /> : <p className="text-[13px] text-destructive">{String(flows.error)}</p>}
      </Page>
    );
  }

  return (
    <Page data-testid="page-delivery">
      <PageHeader
        title="Delivery"
        subtitle={flows.data ? subtitle(pipelines.length - elsewhere.filter(Boolean).length, active, totals) : undefined}
        actions={<FindRecordForm />}
      />
      <OrphanedLedgersNotice partition={active} />
      {flows.data === undefined ? (
        <Skeleton className="h-40 w-full rounded-lg" />
      ) : pipelines.length === 0 ? (
        <EmptyState
          icon={<PackageCheck />}
          title="No delivery flows synced yet"
          description="Register a repository holding flowType: delivery documents; each flow appears here with what it has delivered."
          data-testid="delivery-empty"
        />
      ) : (
        <div className="grid gap-3 lg:grid-cols-2" data-testid="delivery-flows">
          {pipelines.map((pipeline, index) => (
            <FlowCard
              key={pipeline.id}
              pipeline={pipeline}
              stats={stats[index]?.data}
              active={active}
              elsewhere={elsewhere[index] ?? false}
              onOpen={() => navigate(`/pipelines/${pipeline.id}?tab=delivery${stats[index]?.data?.partition ? `&partition=${encodeURIComponent(stats[index]!.data!.partition!)}` : ""}`)}
            />
          ))}
        </div>
      )}
    </Page>
  );
}
