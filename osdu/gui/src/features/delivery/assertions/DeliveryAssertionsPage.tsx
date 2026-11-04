import { useCallback, useMemo, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ListChecks, Play } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { EmptyState } from "@/components/EmptyState";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { deliveryApi } from "../../../api/delivery";
import { useActivePartition } from "../activePartition";
import { ProblemView } from "../TemplateSheet";
import { AssertionRunDialog, type AssertionLaunch } from "./AssertionRunDialog";
import { AssertionSetRunDialog, type AssertionSetLaunch } from "./AssertionSetRunDialog";
import { DashboardCheckActions, DashboardDownloads, DashboardTestActions } from "./DashboardHostActions";
import { readResults } from "./assertionFormat";
import { AssertionDashboard } from "./dashboard/AssertionDashboard";
import { fromBoard } from "./dashboard/dashboardModel";

/** The set an address picks (`?set=`): none for every set, an empty value for the flows that name no set. */
function setOf(value: string | null): string | null | undefined {
  return value === null ? undefined : value === "" ? null : value;
}

/**
 * The Assertions page (osdu/docs/reference/concepts/gui.md, OSDU): the dashboard of every assertion flow in the workbench's
 * partition, each assertion by its latest result. A set picked is in the address, so a link names it; a set runs from
 * here, an assertion runs again from its row, and the dashboard downloads as one page to present.
 */
export default function DeliveryAssertionsPage() {
  const [active] = useActivePartition();
  const [params, setParams] = useSearchParams();
  const [launch, setLaunch] = useState<AssertionLaunch | null>(null);
  const [runOfSet, setRunOfSet] = useState<AssertionSetLaunch | null>(null);
  const board = useQuery({
    queryKey: ["delivery", "assertions", "board", active],
    queryFn: () => deliveryApi.assertionBoard(),
    refetchInterval: 15000,
  });
  const data = useMemo(
    () => (board.data === undefined ? undefined : fromBoard(board.data, new Date(board.dataUpdatedAt).toISOString())),
    [board.data, board.dataUpdatedAt],
  );

  const set = setOf(params.get("set"));
  const onSet = useCallback((next: string | null | undefined) => setParams((was) => {
    const changed = new URLSearchParams(was);
    if (next === undefined) {
      changed.delete("set");
    } else {
      changed.set("set", next ?? "");
    }

    return changed;
  }, { replace: true }), [setParams]);

  if (board.isError || data === undefined) {
    return (
      <Page data-testid="page-delivery-assertions">
        <PageHeader title="Assertions" />
        {board.isError ? <ProblemView error={board.error} testId="assertion-board-error" /> : <Skeleton className="h-72 w-full rounded-lg" />}
      </Page>
    );
  }

  const flows = board.data?.flows ?? [];
  return (
    <Page data-testid="page-delivery-assertions">
      <AssertionDashboard
        data={data}
        title="Assertions"
        set={set}
        onSet={onSet}
        showSets
        loadResults={readResults}
        actions={(view) => (
          <>
            {typeof view.set === "string" && (
              <Button
                size="sm"
                onClick={() => setRunOfSet({ set: view.set as string, flows: flows.filter((flow) => flow.set === view.set) })}
                data-testid="dashboard-run-set"
              >
                <Play />
                Run the set
              </Button>
            )}
            <DashboardDownloads data={data} view={view} />
          </>
        )}
        testActions={(test) => (
          <DashboardTestActions
            test={test}
            flow={flows.find((flow) => flow.pipelineId === test.pipelineId)}
            onRun={(flow, which) => setLaunch({ pipelineId: flow.pipelineId, repoId: flow.repoId, flowName: flow.name, tests: [which.name], tags: [] })}
            showFlow
          />
        )}
        checkActions={(test, check) => <DashboardCheckActions test={test} check={check} />}
        empty={(
          <EmptyState
            icon={<ListChecks />}
            title="No assertion flows synced yet"
            description="Register a repository holding flowType: assertion documents; their assertions appear here with where they stand, grouped by set and by the dimensions they name."
            data-testid="assertion-board-empty"
          />
        )}
      />
      <AssertionRunDialog launch={launch} onClose={() => setLaunch(null)} />
      <AssertionSetRunDialog launch={runOfSet} onClose={() => setRunOfSet(null)} />
    </Page>
  );
}
