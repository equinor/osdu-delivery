import { PackageCheck } from "lucide-react";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "@/api/client";
import { CorrelationError } from "@/components/CorrelationError";
import { EmptyState } from "@/components/EmptyState";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { useDeliveryTotals } from "./deliveryTotals";
import { FindRecordForm } from "./FindRecordForm";
import { FlowCard } from "./FlowCard";
import { attentionOrder, flowVerdict, overviewSubtitle } from "./flowHealth";
import { OrphanedLedgersNotice } from "./OrphanedLedgersNotice";
import { RepositorySyncControl } from "./RepositorySyncControl";

/**
 * Every delivery flow with what it has delivered in the workbench's partition: the "uploaded versus not" view. Each card
 * leads with its verdict, and once every flow's counts have answered, the flows that need someone come first (failed, then
 * held, then drifted), then the ones still delivering, then the delivered. A flow that delivers only to other partitions
 * is listed last, dimmed, with where it delivers, and left out of the totals.
 */
export default function DeliveryOverviewPage() {
  const { flows, pipelines, active, stats, elsewhere, totals, counted } = useDeliveryTotals();
  const verdicts = stats.map((query, index) => (query?.data === undefined || elsewhere[index] ? null : flowVerdict(query.data)));
  // Sorted once every count has answered, so cards do not move while the answers come in; afterwards a card moves only
  // when its verdict does.
  const order = counted
    ? attentionOrder(verdicts.map((verdict) => verdict?.rank ?? null), elsewhere)
    : pipelines.map((_, index) => index);
  const here = pipelines.length - elsewhere.filter(Boolean).length;
  const needing = verdicts.filter((verdict) => verdict !== null && (verdict.tone === "destructive" || verdict.tone === "warning")).length;

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
        subtitle={flows.data ? overviewSubtitle(here, active, totals, counted ? needing : 0) : undefined}
        actions={(
          <>
            <FindRecordForm />
            <RepositorySyncControl />
          </>
        )}
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
          {order.map((index) => {
            const pipeline = pipelines[index]!;
            return (
              <FlowCard
                key={pipeline.id}
                pipeline={pipeline}
                stats={stats[index]?.data}
                active={active}
                elsewhere={elsewhere[index] ?? false}
              />
            );
          })}
        </div>
      )}
    </Page>
  );
}
