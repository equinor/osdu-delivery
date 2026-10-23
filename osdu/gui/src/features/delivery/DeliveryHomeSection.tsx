import { Link } from "react-router-dom";
import { ArrowRight, PackageCheck } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "@/api/client";
import { CorrelationError } from "@/components/CorrelationError";
import { EmptyState } from "@/components/EmptyState";
import { HomeSection } from "@/components/HomeSection";
import { RelativeTime } from "@/components/RelativeTime";
import { Count, DeliveryBar } from "./DeliveryCounts";
import { useDeliveryTotals } from "./deliveryTotals";
import { FindRecordForm } from "./FindRecordForm";

/**
 * What the delivery flows hold in the workbench's partition, on the home page: how much of it is delivered, the records
 * by where they stand, and the ways into them (a record found by any key, the Delivery page). The counts are the Delivery
 * page's, added up the same way (useDeliveryTotals), so the two never disagree.
 */
export function DeliveryHomeSection() {
  const { flows, pipelines, active, elsewhere, totals, counted } = useDeliveryTotals();
  const here = pipelines.filter((_, index) => !elsewhere[index]).length;
  const share = totals.total === 0 ? 0 : Math.floor((totals.delivered / totals.total) * 100);

  return (
    <HomeSection
      title={(
        <>
          Delivery
          {active !== null && <Badge variant="secondary" className="font-mono text-[11px]" data-testid="home-delivery-partition">{active}</Badge>}
        </>
      )}
      aside={(
        <div className="flex flex-wrap items-center gap-2">
          <FindRecordForm testId="home-find-record" />
          <Button asChild variant="outline" size="sm">
            <Link to="/delivery" data-testid="home-delivery-open">
              Open Delivery
              <ArrowRight />
            </Link>
          </Button>
        </div>
      )}
      testId="home-delivery"
    >
      <Card className="gap-4 rounded-lg p-4">
        {flows.isError ? (
          isApiError(flows.error) ? <CorrelationError error={flows.error} /> : <p className="text-[13px] text-destructive">{String(flows.error)}</p>
        ) : flows.data === undefined || (pipelines.length > 0 && !counted) ? (
          <div className="flex flex-col gap-3">
            <Skeleton className="h-7 w-72" />
            <Skeleton className="h-2 w-full" />
            <Skeleton className="h-11 w-full" />
          </div>
        ) : here === 0 ? (
          <EmptyState
            icon={<PackageCheck />}
            title={pipelines.length === 0 ? "No delivery flows synced yet" : `No delivery flow delivers to ${active ?? "this partition"}`}
            description={pipelines.length === 0
              ? "Register a repository holding flowType: delivery documents; what they deliver is counted here."
              : "Pick another partition in the title bar to see what its flows have delivered."}
            data-testid="home-delivery-empty"
          />
        ) : (
          <>
            <div className="flex flex-wrap items-baseline justify-between gap-x-6 gap-y-1">
              <p className="text-sm text-muted-foreground" data-testid="home-delivery-share">
                <span className="mr-1.5 font-mono text-2xl font-semibold text-foreground tabular-nums">{`${share}%`}</span>
                {`of ${totals.total.toLocaleString()} records delivered`}
              </p>
              <p className="text-xs text-muted-foreground">
                {`${here} delivery flow${here === 1 ? "" : "s"}`}
                {totals.lastDeliveredUtc !== null && (
                  <>
                    {", last delivery "}
                    <RelativeTime value={totals.lastDeliveredUtc} />
                  </>
                )}
              </p>
            </div>
            <DeliveryBar totals={totals} />
            <div className="grid grid-cols-3 gap-3 sm:grid-cols-6">
              <Count label="Delivered" value={totals.delivered} tone="success" testId="home-delivery-delivered" />
              <Count label="Pending" value={totals.pending} tone="info" testId="home-delivery-pending" />
              <Count label="Held" value={totals.held} tone="warning" testId="home-delivery-held" />
              <Count label="Failed" value={totals.failed} tone="destructive" testId="home-delivery-failed" />
              <Count label="Drifted" value={totals.drifted} tone="warning" testId="home-delivery-drifted" />
              <Count label="Last 24h" value={totals.deliveredLast24h} testId="home-delivery-24h" />
            </div>
          </>
        )}
      </Card>
    </HomeSection>
  );
}
