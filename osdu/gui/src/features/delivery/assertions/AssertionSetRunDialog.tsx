import { useState } from "react";
import { Link } from "react-router-dom";
import { useQueryClient } from "@tanstack/react-query";
import { CheckCircle2, CircleDashed, CircleSlash, Loader2, Play, XCircle } from "lucide-react";
import { toast } from "sonner";
import {
  AlertDialog, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { runApi } from "@/api/endpoints";
import { isApiError } from "@/api/client";
import type { DeliveryAssertionFlow } from "../../../api/delivery";
import { useActivePartition } from "../activePartition";
import { counted, partitionOf, selectionPayload } from "./assertionFormat";

/** A run of a set to launch: its flows, and for each the tests to run (none, or no entry, runs them all). */
export interface AssertionSetLaunch {
  set: string;
  flows: readonly DeliveryAssertionFlow[];
  tests?: ReadonlyMap<string, readonly string[]>;
}

/** Where one flow's run stands in the dialog: why it is not run, waiting its turn, being queued, queued, or refused. */
type Step =
  | { state: "skipped"; why: string }
  | { state: "waiting" }
  | { state: "queuing" }
  | { state: "queued"; runId: string | null }
  | { state: "refused"; message: string };

/**
 * Why a flow cannot run from the set's button, or null when it can: a flow that does not check the partition, whose
 * document does not parse, or whose run needs a parameter value its document gives no default for runs from its own line.
 */
function skipWhy(flow: DeliveryAssertionFlow, active: string | null): string | null {
  if (flow.problem !== null) {
    return flow.problem;
  }

  if (!flow.tests.some((test) => test.runsHere)) {
    return "None of its assertions checks this partition.";
  }

  const placed = partitionOf(flow, active);
  if (placed.error !== null) {
    return placed.error;
  }

  const needed = flow.parameters.filter((parameter) => parameter.required && (parameter.default ?? null) === null).map((parameter) => parameter.name);
  return needed.length > 0
    ? `Its runs need ${needed.join(", ")}, which its document gives no default for; run it from its own line to give ${needed.length === 1 ? "it" : "them"}.`
    : null;
}

function StepMark({ step }: { step: Step }) {
  switch (step.state) {
    case "skipped":
      return <CircleSlash className="size-3.5 shrink-0 text-muted-foreground" aria-label="not run" />;
    case "queuing":
      return <Loader2 className="size-3.5 shrink-0 animate-spin text-muted-foreground" aria-label="queuing" />;
    case "queued":
      return <CheckCircle2 className="size-3.5 shrink-0 text-success" aria-label="queued" />;
    case "refused":
      return <XCircle className="size-3.5 shrink-0 text-destructive" aria-label="refused" />;
    default:
      return <CircleDashed className="size-3.5 shrink-0 text-muted-foreground" aria-label="waiting" />;
  }
}

/**
 * Runs a set: one run of each of its flows that can run in the workbench's partition, queued one after another through the
 * platform's one trigger, each with its document's default parameters and the partition picked in the title bar. It lists
 * each flow and why one is left out before anything runs, then how each queuing went; a refused one says why and the rest
 * go on. The board shows the results as they land.
 */
export function AssertionSetRunDialog({ launch, onClose }: { launch: AssertionSetLaunch | null; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [active] = useActivePartition();
  const [steps, setSteps] = useState<Record<string, Step>>({});
  const [shown, setShown] = useState<AssertionSetLaunch | null>(launch);
  const [running, setRunning] = useState(false);

  // Every opening starts with nothing asked.
  if (launch !== null && launch !== shown) {
    setShown(launch);
    setSteps({});
  }

  if (shown === null) {
    return null;
  }

  const planned = shown.flows.map((flow) => ({ flow, why: skipWhy(flow, active), tests: shown.tests?.get(flow.pipelineId) ?? [] }));
  const runnable = planned.filter((entry) => entry.why === null);
  const started = Object.keys(steps).length > 0;
  const finished = started && runnable.every((entry) => {
    const step = steps[entry.flow.pipelineId];
    return step?.state === "queued" || step?.state === "refused";
  });

  const run = async () => {
    setRunning(true);
    let queued = 0;
    const refused: string[] = [];
    for (const { flow, tests } of runnable) {
      setSteps((current) => ({ ...current, [flow.pipelineId]: { state: "queuing" } }));
      try {
        const partition = partitionOf(flow, active).partition;
        const payload = selectionPayload(tests, []);
        const accepted = await runApi.trigger({
          repoId: flow.repoId,
          flowName: flow.name,
          operation: "test",
          values: partition === null ? undefined : { partition },
          payload: Object.keys(payload).length > 0 ? payload : undefined,
        });
        queued++;
        setSteps((current) => ({ ...current, [flow.pipelineId]: { state: "queued", runId: accepted.runId ?? null } }));
      } catch (error) {
        const message = isApiError(error) ? error.detail ?? error.title : error instanceof Error ? error.message : String(error);
        refused.push(flow.name);
        setSteps((current) => ({ ...current, [flow.pipelineId]: { state: "refused", message } }));
      }
    }

    setRunning(false);
    void queryClient.invalidateQueries({ queryKey: ["delivery", "assertions"] });
    if (refused.length === 0) {
      toast.success(`Queued ${counted(queued, "run")} of set ${shown.set}; its results show as they land.`);
    } else {
      toast.error(`Queued ${counted(queued, "run")} of set ${shown.set}; ${counted(refused.length, "flow")} refused: ${refused.join(", ")}.`);
    }
  };

  const picking = shown.tests !== undefined;
  return (
    <AlertDialog open={launch !== null} onOpenChange={(next) => (next || running ? undefined : onClose())}>
      <AlertDialogContent data-testid="set-run-dialog" className="max-h-[calc(100dvh-2rem)] grid-cols-[minmax(0,1fr)] gap-4 overflow-y-auto sm:max-w-2xl">
        <AlertDialogHeader className="gap-1 text-left">
          <AlertDialogTitle className="flex items-center gap-2">
            <Play className="size-4 shrink-0 text-primary" />
            {picking ? `Run the assertions picked in set ${shown.set}` : `Run set ${shown.set}`}
            {active !== null && <Badge variant="secondary" className="ml-auto font-mono">{active}</Badge>}
          </AlertDialogTitle>
          <AlertDialogDescription>
            {picking ? "One run of each flow below, of the assertions named beside it" : "One run of each flow below, of every assertion it declares"}
            {", queued one after another with each flow's default parameters. A flow that cannot run from here says why."}
          </AlertDialogDescription>
        </AlertDialogHeader>

        <ul className="flex flex-col divide-y divide-border rounded-md border border-border" data-testid="set-run-flows">
          {planned.map(({ flow, why, tests }) => {
            const step: Step = why !== null ? { state: "skipped", why } : steps[flow.pipelineId] ?? { state: "waiting" };
            return (
              <li key={flow.pipelineId} className="flex flex-col gap-0.5 px-3 py-2 text-[12px]" data-testid="set-run-flow" data-state={step.state}>
                <span className="flex min-w-0 items-center gap-2">
                  <StepMark step={step} />
                  <span className="min-w-0 flex-1 truncate font-mono font-medium" title={flow.name}>{flow.name}</span>
                  {step.state === "queued" && step.runId !== null && (
                    <Link to={`/runs/${step.runId}`} className="shrink-0 text-muted-foreground hover:text-foreground hover:underline">its run</Link>
                  )}
                  {why === null && step.state !== "queued" && (
                    <span className="shrink-0 text-muted-foreground">{tests.length > 0 ? tests.join(", ") : counted(flow.tests.filter((t) => t.runsHere).length, "assertion")}</span>
                  )}
                </span>
                {step.state === "skipped" && <span className="pl-5 text-muted-foreground">{step.why}</span>}
                {step.state === "refused" && <span className="pl-5 text-destructive">{step.message}</span>}
              </li>
            );
          })}
        </ul>

        <AlertDialogFooter>
          <Button variant="outline" onClick={onClose} disabled={running} data-testid="set-run-close">{finished ? "Close" : "Cancel"}</Button>
          {!finished && (
            <Button onClick={() => void run()} disabled={running || runnable.length === 0} data-testid="set-run-start">
              {running ? <Loader2 className="animate-spin" /> : <Play />}
              {`Run ${counted(runnable.length, "flow")}`}
            </Button>
          )}
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
