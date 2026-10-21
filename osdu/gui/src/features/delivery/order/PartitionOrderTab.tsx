import { useMemo, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, Layers } from "lucide-react";
import { EmptyState } from "@/components/EmptyState";
import { ExplainMark, ExplainTip } from "@/components/ExplainTip";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { orderApi, type OrderDelivery, type OrderPartition } from "../../../api/order";
import { useActivePartition } from "../activePartition";
import { ProblemView } from "../TemplateSheet";
import { useWindowFit } from "../useWindowFit";

const BELOW = 24;
const MIN_HEIGHT = 240;
const EVERY = "*";

function partitionLabel(partition: string): string {
  return partition === EVERY ? "Every registered partition" : partition;
}

function deliveryLabel(delivery: OrderDelivery): string {
  const flow = delivery.interface === null ? delivery.flow : `${delivery.flow}/${delivery.interface}`;
  return delivery.repository === null ? flow : `${delivery.repository}: ${flow}`;
}

/** One entity type of a level: the deliveries writing it, and the types its records wait for. */
function TypeCard({ entityType, plan }: { entityType: string; plan: OrderPartition }) {
  const deliveries = plan.deliveries.filter((d) => d.entityType.toLowerCase() === entityType.toLowerCase());
  const waits = plan.edges.filter((e) => e.from.toLowerCase() === entityType.toLowerCase());
  const unknown = deliveries.some((d) => !d.known);
  return (
    <div className="flex min-w-0 flex-col gap-1 rounded-md border bg-card p-2" data-testid={`order-type-${entityType}`}>
      <div className="flex min-w-0 items-baseline gap-1.5" title={entityType}>
        <span className="truncate text-[12.5px] font-medium">{entityType.slice(entityType.indexOf("--") + 2)}</span>
        <span className="shrink-0 text-[11px] text-muted-foreground">{entityType.slice(0, Math.max(0, entityType.indexOf("--")))}</span>
        {unknown && (
          <ExplainTip
            title="Not ordered by its references"
            testId={`order-type-${entityType}-unknown`}
            text="No template folder and no built release holds this kind, so what its records refer to cannot be read: it waits for nothing in the order. Put its template in a template folder on the mapping's path, or build a release of OSDU's lineage that holds it."
          >
            <AlertTriangle className="size-3.5 shrink-0 self-center text-warning" aria-label="Not ordered by its references" />
          </ExplainTip>
        )}
      </div>
      <ul className="flex flex-col text-[11.5px] text-muted-foreground">
        {deliveries.map((d) => (
          <li key={`${d.repository ?? ""}|${d.flow}|${d.interface ?? ""}`} className="truncate font-mono" title={`${deliveryLabel(d)} writes ${d.kind}`}>
            {deliveryLabel(d)}
          </li>
        ))}
      </ul>
      {waits.length > 0 && (
        <div className="truncate text-[11.5px]" title={waits.map((e) => `${e.to}: ${e.because.map((b) => b.property).join(", ")}`).join("\n")}>
          <span className="text-muted-foreground">waits for </span>
          {waits.map((e) => e.to.slice(e.to.indexOf("--") + 2)).join(", ")}
        </div>
      )}
    </div>
  );
}

/**
 * The OSDU order of one partition with every repository's deliveries together: its entity types level by level, each
 * level's records referring only to records of earlier levels, with the deliveries writing each type and what it waits
 * for. The references the order had to cut and its warnings are behind marks on the facts line.
 */
export function PartitionOrderTab() {
  const [active] = useActivePartition();
  const [picked, setPicked] = useState<string | null>(null);
  const order = useQuery({ queryKey: ["delivery", "order", "partitions"], queryFn: orderApi.partitions });
  const frame = useRef<HTMLDivElement>(null);
  useWindowFit(frame, BELOW, MIN_HEIGHT);

  const plans = useMemo(() => order.data?.partitions ?? [], [order.data]);
  const partition = picked !== null && plans.some((p) => p.partition === picked)
    ? picked
    : active !== null && plans.some((p) => p.partition === active) ? active : plans[0]?.partition ?? null;
  const plan = plans.find((p) => p.partition === partition) ?? null;

  if (order.isError) {
    return <ProblemView error={order.error} testId="order-partition-error" />;
  }

  if (order.data !== undefined && plans.length === 0) {
    return (
      <EmptyState
        icon={<Layers />}
        title="No delivery is synced"
        description="Once a repository with delivery flows is synced, the order their records reach each partition in shows here."
        data-testid="order-partition-empty"
      />
    );
  }

  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-[12.5px]" data-testid="order-partition-facts">
        {order.data === undefined || plan === null ? <Skeleton className="h-4 w-64" /> : (
          <>
            <Select value={plan.partition} onValueChange={setPicked}>
              <SelectTrigger size="sm" className="h-7 w-56" aria-label="Partition" data-testid="order-partition-picker">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {plans.map((p) => <SelectItem key={p.partition} value={p.partition}>{partitionLabel(p.partition)}</SelectItem>)}
              </SelectContent>
            </Select>
            <span><span className="font-mono tabular-nums">{plan.levels.length}</span> level{plan.levels.length === 1 ? "" : "s"}</span>
            <span><span className="font-mono tabular-nums">{plan.deliveries.length}</span> deliver{plan.deliveries.length === 1 ? "y" : "ies"}</span>
            <ExplainTip
              title="References not ordered"
              testId="order-partition-cuts"
              text={plan.cuts.length === 0
                ? "Every reference between the partition's deliveries is part of the order."
                : plan.cuts.map((c) => c.why).join("\n\n")}
            >
              <span className={cn("cursor-default", plan.cuts.length === 0 && "text-muted-foreground")}>
                <span className="font-mono tabular-nums">{plan.cuts.length}</span> reference{plan.cuts.length === 1 ? "" : "s"} not ordered
              </span>
            </ExplainTip>
            {(plan.warnings.length > 0 || (order.data?.problems.length ?? 0) > 0) && (
              <ExplainTip
                title="Warnings"
                testId="order-partition-warnings"
                text={[...plan.warnings, ...(order.data?.problems ?? [])].join("\n\n")}
              >
                <span className="inline-flex cursor-default items-center gap-1">
                  <AlertTriangle className="size-3.5 text-warning" aria-hidden />
                  <span className="font-mono tabular-nums">{plan.warnings.length + (order.data?.problems.length ?? 0)}</span> warning{plan.warnings.length + (order.data?.problems.length ?? 0) === 1 ? "" : "s"}
                </span>
              </ExplainTip>
            )}
            <ExplainMark
              title="The order of a partition"
              testId="order-partition-about"
              text={"Every repository's deliveries to the partition, ordered by the references their mappings fill, as OSDU's schemas declare them. "
                + "A level's records refer only to records of earlier levels. Lineage orders each repository's flows this way, so a schedule runs them in this order; "
                + "a reference two types make to each other is cut by rule, the later group waiting for the earlier, a searched reference winning, or, in one group, left unordered."}
            />
          </>
        )}
      </div>
      <div ref={frame} className="min-h-0 overflow-y-auto" data-testid="order-partition-levels">
        {plan !== null && (
          <ol className="flex flex-col gap-3">
            {plan.levels.map((level, index) => (
              <li key={index} className="flex flex-col gap-1.5" data-testid={`order-level-${index + 1}`}>
                <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Level {index + 1}</h3>
                <div className="grid grid-cols-[repeat(auto-fill,minmax(300px,1fr))] gap-2">
                  {level.map((entityType) => <TypeCard key={entityType} entityType={entityType} plan={plan} />)}
                </div>
              </li>
            ))}
          </ol>
        )}
      </div>
    </div>
  );
}
