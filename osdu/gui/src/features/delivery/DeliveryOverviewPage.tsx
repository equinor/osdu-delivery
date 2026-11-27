import { useState } from "react";
import { PackageCheck, SearchX } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "@/api/client";
import { CorrelationError } from "@/components/CorrelationError";
import { EmptyState } from "@/components/EmptyState";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { SearchInput } from "@/components/SearchInput";
import { deliversTo, useDeliveryTotals } from "./deliveryTotals";
import { FlowCard } from "./FlowCard";
import { attentionOrder, elsewhereLine, flowVerdict, overviewSubtitle } from "./flowHealth";
import { filteredLine, matchingFlows } from "./flowSearch";
import { OrphanedLedgersNotice } from "./OrphanedLedgersNotice";
import { RepositorySyncControl } from "./RepositorySyncControl";

/**
 * Every delivery flow with what it has delivered in the workbench's partition: the "uploaded versus not" view. Each card
 * leads with its verdict, and once the counts have answered, the flows that need someone come first (a flow that cannot
 * be counted, with why, then failed, held and drifted), then the ones still delivering, then the delivered. A flow that
 * delivers only to other partitions is listed last, dimmed, with where it delivers, and left out of the totals. The
 * header's filter narrows the cards to the flows whose card shows every word typed; the totals stay the partition's.
 */
export default function DeliveryOverviewPage() {
  const { flows, counts, pipelines, active, stats, problems, elsewhere, totals, counted, uncounted } = useDeliveryTotals();
  const [filter, setFilter] = useState("");
  const verdicts = stats.map((s, index) => (s === undefined || elsewhere[index] ? null : flowVerdict(s)));
  // Sorted once the counts have answered, so cards do not move while the answer comes in; afterwards a card moves only
  // when its verdict does. A flow that cannot be counted needs someone before any other.
  const order = counted
    ? attentionOrder(verdicts.map((verdict, index) => (problems[index] !== null ? -1 : verdict?.rank ?? null)), elsewhere)
    : pipelines.map((_, index) => index);
  const here = pipelines.length - elsewhere.filter(Boolean).length;
  const needing = uncounted
    + verdicts.filter((verdict) => verdict !== null && (verdict.tone === "destructive" || verdict.tone === "warning")).length;

  // What each card shows of its flow, read in the cards' order, so a match keeps its place among them.
  const searched = filter.trim();
  const shown = matchingFlows(order.map((index) => {
    const pipeline = pipelines[index]!;
    const partitions = deliversTo(stats[index]) ?? [];
    return {
      index,
      name: pipeline.name,
      relativePath: pipeline.relativePath,
      batch: pipeline.batch !== null && pipeline.batch !== "default" ? pipeline.batch : null,
      active: pipeline.active,
      partitions,
      headline: elsewhere[index] ? elsewhereLine(partitions, active) : verdicts[index]?.headline ?? problems[index]?.title ?? null,
    };
  }), searched).map((flow) => flow.index);

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
            <SearchInput
              value={filter}
              onChange={setFilter}
              placeholder="Filter flows: name, batch, partition or state"
              label="Filter the delivery flows"
              className="sm:w-80"
              testId="delivery-flows-filter"
            />
            <RepositorySyncControl />
          </>
        )}
      />
      <OrphanedLedgersNotice partition={active} />
      {counts.isError && counts.data === undefined && (
        isApiError(counts.error)
          ? <CorrelationError error={counts.error} />
          : <p className="text-[13px] text-destructive">{String(counts.error)}</p>
      )}
      {flows.data === undefined ? (
        <Skeleton className="h-40 w-full rounded-lg" />
      ) : pipelines.length === 0 ? (
        <EmptyState
          icon={<PackageCheck />}
          title="No delivery flows synced yet"
          description="Register a repository holding flowType: delivery documents; each flow appears here with what it has delivered."
          data-testid="delivery-empty"
        />
      ) : shown.length === 0 ? (
        <EmptyState
          icon={<SearchX />}
          title={`No delivery flow matches '${searched}'`}
          description="The filter reads each flow's name, its path, its batch, the partitions it delivers to and the line under its name, such as failed, held or delivered."
          action={(
            <Button variant="outline" size="sm" onClick={() => setFilter("")} data-testid="delivery-flows-filter-reset">
              Show every flow
            </Button>
          )}
          data-testid="delivery-flows-none"
        />
      ) : (
        <>
          {searched !== "" && (
            <p className="text-[12px] text-muted-foreground" data-testid="delivery-flows-shown">
              {filteredLine(shown.length, pipelines.length, searched)}
            </p>
          )}
          <div className="grid gap-3 lg:grid-cols-2" data-testid="delivery-flows">
            {shown.map((index) => {
              const pipeline = pipelines[index]!;
              return (
                <FlowCard
                  key={pipeline.id}
                  pipeline={pipeline}
                  stats={stats[index]}
                  problem={problems[index] ?? null}
                  active={active}
                  elsewhere={elsewhere[index] ?? false}
                />
              );
            })}
          </div>
        </>
      )}
    </Page>
  );
}
