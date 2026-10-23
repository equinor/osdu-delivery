import { useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Skeleton } from "@/components/ui/skeleton";
import { ledgerLabel } from "../../../api/delivery";
import { REMOVED_KEY_STATES, removalApi, type RemovedKeyState } from "../../../api/removals";
import { InterfacePicker } from "../InterfacePicker";
import { OutsidePartition } from "../PartitionNotice";
import { ProblemView } from "../TemplateSheet";
import { useInterfaceChoice } from "../useInterfaceChoice";
import { RemovalRunsTable } from "./RemovalRunsTable";
import { RemovedKeysGrid } from "./RemovedKeysGrid";
import { RemovalRunsToolbar, RemovalViewSwitch, type RemovalShow } from "./RemovalViewSwitch";

/** The removal runs the tab lists. */
const RUNS = 50;

/** The URL parameters that mean something only for the ledger that was showing. */
const SCOPED_TO_LEDGER = ["state"] as const;

/**
 * A delivery flow's Deletions tab (osdu/docs/reference/flow/removal.md, The ledger's deletions): the keys removal flows found
 * this ledger's source no longer holds, whichever removal flow found them and whichever pipeline runs it, as the grid every
 * removal page shows, with the records picked delivered again from here; and the removal runs that wrote to the ledger. It is
 * about one ledger, as every view of a flow is: the interface picked and the partition in the title bar.
 */
export function DeliveryDeletionsPanel({ pipelineId, flowName }: { pipelineId: string; flowName: string }) {
  const [params, setParams] = useSearchParams();
  const {
    names, many, interfaceName, current, partitions, headerPartition, outside, unplaced, active, scope, ready, selectInterface,
  } = useInterfaceChoice(pipelineId, SCOPED_TO_LEDGER);
  const ledgerFlowId = current?.flowId;
  const view = params.get("view") === "runs" ? "runs" : "deletions";
  const state: RemovedKeyState | null = REMOVED_KEY_STATES.find((s) => s === params.get("state")) ?? null;
  const runs = useQuery({
    queryKey: ["delivery", "removals", "ledger-runs", ledgerFlowId],
    queryFn: () => removalApi.ledgerRuns(ledgerFlowId!, RUNS),
    enabled: ready && ledgerFlowId !== undefined && view === "runs",
    refetchInterval: 15000,
  });
  const show = (change: RemovalShow) => setParams((was) => {
    const next = new URLSearchParams(was);
    if (change.view !== undefined) {
      if (change.view === "deletions") {
        next.delete("view");
      } else {
        next.set("view", change.view);
      }
    }

    if (change.state !== undefined) {
      if (change.state === null) {
        next.delete("state");
      } else {
        next.set("state", change.state);
      }
    }

    return next;
  }, { replace: true });

  if (outside !== null || unplaced) {
    return (
      <div className="flex flex-col gap-4" data-testid="delivery-deletions">
        <OutsidePartition flowName={flowName} active={active} partitions={partitions} headerPartition={headerPartition} />
      </div>
    );
  }

  if (!ready || current === undefined) {
    return <Skeleton className="h-48 w-full rounded-lg" />;
  }

  return (
    <div className="flex min-w-0 flex-col gap-3" data-testid="delivery-deletions">
      {many && (
        <InterfacePicker names={names} interfaceName={interfaceName} onSelect={selectInterface} caption={`of ${names.length} interfaces of ${flowName}`} />
      )}
      <p className="text-[13px] text-muted-foreground" data-testid="delivery-deletions-about">
        The keys a removal flow found this ledger&apos;s source no longer holds. Each is taken out of OSDU once, and a row of it newer than its removal is delivered again.
      </p>
      {view === "deletions"
        ? (
          <RemovedKeysGrid
            key={current.flowId}
            ledgerFlowId={current.flowId}
            state={state}
            onState={(next) => show({ state: next })}
            keyColumns={current.keyColumns ?? undefined}
            lift={{ pipelineId, scope, flowName: ledgerLabel(flowName, scope) }}
            leading={<RemovalViewSwitch view={view} onShow={show} runsLabel="Removal runs" />}
          />
        )
        : runs.isError
          ? <ProblemView error={runs.error} testId="delivery-deletions-runs-error" />
          : (
            <RemovalRunsTable
              runs={runs.data}
              showRemoval
              toolbar={<RemovalRunsToolbar onShow={show} runsLabel="Removal runs" />}
              emptyMessage="No removal flow has run against this ledger yet."
              testId="delivery-removal-runs"
            />
          )}
    </div>
  );
}
