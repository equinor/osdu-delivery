import { Link as RouterLink } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { CirclePlay, Clock, Loader2, Terminal } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { kindApi } from "@/api/endpoints";
import type { FlowKindDescriptor, RunSummary } from "@/api/types";
import { RelativeTime } from "@/components/RelativeTime";
import { RunDuration } from "@/components/RunDuration";
import { TruncatedText } from "@/components/TruncatedText";
import { runTraceSpec } from "@/features/runs/runTrace";
import { useTraceTray } from "@/layout/workbench/TraceTray";

/**
 * What a run performs, as the trigger dialog names it ("Delete ledger", "Sync timelines"): the label its kind registers
 * for the operation, the kind's default operation for a run that names none, and the operation's own name while the kinds
 * are being read.
 */
function operationLabel(run: RunSummary, kinds: FlowKindDescriptor[] | undefined): string {
  const kind = kinds?.find((candidate) => candidate.flowType === run.flowKind);
  const name = run.operation ?? kind?.defaultOperation ?? null;
  const operation = name === null ? undefined : kind?.operations.find((candidate) => candidate.name === name);
  return operation?.label ?? name ?? "Run";
}

/**
 * The runs of a flow under way (osdu/docs/reference/concepts/gui.md, A run an action starts): the newest, with what it
 * performs, how long it has run or how long it has waited for a node, what it is doing now, its live trace in the bottom
 * panel and its page; when more are under way (the parts a delivery split over nodes, a run queued behind another), how
 * many, leading to the flow's Runs tab. Nothing at all while no run is under way.
 *
 * `inCard` draws it as a row of the card it sits in, under that card's actions, so a run one of them starts never moves
 * them from under the pointer; otherwise it is a card of its own above a tab's content.
 */
export function RunsUnderWay({ runs, flowName, inCard = false }: { runs: RunSummary[]; flowName: string; inCard?: boolean }) {
  const { open } = useTraceTray();
  const kinds = useQuery({ queryKey: ["kinds"], queryFn: kindApi.list, staleTime: 5 * 60_000, enabled: runs.length > 0 });
  const run = runs[0];
  if (run === undefined) {
    return null;
  }

  const label = operationLabel(run, kinds.data);
  const running = run.status === "running";
  const more = runs.length - 1;
  const Frame = inCard ? "div" : Card;
  return (
    <Frame
      className={inCard
        ? "flex flex-wrap items-center gap-x-3 gap-y-1.5 border-t pt-2.5 text-[13px]"
        : "flex-row flex-wrap items-center gap-x-3 gap-y-1.5 rounded-lg px-3 py-2 text-[13px]"}
      data-testid="delivery-run-under-way"
      data-status={run.status}
    >
      <span className="inline-flex min-w-0 items-center gap-1.5">
        {running
          ? <Loader2 className="size-4 shrink-0 animate-spin text-primary" aria-hidden />
          : <Clock className="size-4 shrink-0 text-muted-foreground" aria-hidden />}
        <span className="font-medium" data-testid="delivery-run-under-way-operation">{label}</span>
        <span className="text-muted-foreground" data-testid="delivery-run-under-way-state">
          {running
            ? <>running for <RunDuration durationSeconds={null} startUtc={run.startUtc} enqueuedUtc={run.enqueuedUtc} live /></>
            : <>queued <RelativeTime value={run.enqueuedUtc ?? run.writtenUtc} />, waiting for a node</>}
        </span>
      </span>
      {run.lastAction !== null && run.lastAction !== "" && (
        <span className="min-w-0 flex-1 text-muted-foreground" data-testid="delivery-run-under-way-action">
          <TruncatedText text={run.lastAction} maxWidth={720} />
        </span>
      )}
      <span className="ml-auto inline-flex shrink-0 items-center gap-1.5">
        {more > 0 && (
          <RouterLink
            to="?tab=runs"
            className="text-[12px] text-primary hover:underline"
            title="The flow's runs, every one under way among them"
            data-testid="delivery-run-under-way-more"
          >
            {`${more.toLocaleString()} more under way`}
          </RouterLink>
        )}
        <Button
          variant="outline"
          size="sm"
          className="h-7"
          onClick={() => open(runTraceSpec(run.runId, `${label} · ${flowName}`))}
          title="Open the run's live trace in the bottom panel"
          data-testid="delivery-run-under-way-trace"
        >
          <Terminal />
          Trace
        </Button>
        <Button asChild variant="outline" size="sm" className="h-7">
          <RouterLink to={`/runs/${run.runId}`} data-testid="delivery-run-under-way-open">
            <CirclePlay />
            Open run
          </RouterLink>
        </Button>
      </span>
    </Frame>
  );
}
