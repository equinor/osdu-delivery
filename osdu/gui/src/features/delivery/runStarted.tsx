import { useCallback } from "react";
import { useNavigate } from "react-router-dom";
import { useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { useTraceTray } from "@/layout/workbench/TraceTray";
import { runTraceSpec } from "@/features/runs/runTrace";
import { FOLLOW_RUNS_KEY } from "./followRuns";

/** What an action that asked for work says once it has been accepted. */
export interface RunStartedNotice {
  /** The toast's line: what was asked, and what does it. */
  message: string;
  /** The run the action queued; null or absent when it queued none and left the work to the flow's next run. */
  runId?: string | null;
  /** What the run is, which titles its trace in the bottom panel and the title bar: the action and what it acts on. */
  title: string;
  /**
   * False leaves the bottom panel to what it shows, when that is where the action was asked from (an issue stepped
   * through), and holds the trace behind the title bar's trace indicator alone. True (the default) opens it there.
   */
  openTrace?: boolean;
}

/**
 * What every delivery action that queues a run does once it is accepted (osdu/docs/reference/concepts/gui.md, A run an
 * action starts): its live trace opens in the bottom panel, held in the title bar's trace indicator as the run's own page
 * holds it, so the run is watched from where it was asked for, and its end is signalled however far the reader has moved
 * on; the toast says what was queued and opens the run, and the flow's page lists the run under way at once rather than
 * at its next probe. An action that queued no run says so in the toast alone.
 */
export function useRunStarted(): (notice: RunStartedNotice) => void {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { open, follow } = useTraceTray();
  return useCallback(({ message, runId, title, openTrace = true }: RunStartedNotice) => {
    if (runId === null || runId === undefined) {
      toast.success(message);
      return;
    }

    const trace = runTraceSpec(runId, title);
    if (openTrace) {
      open(trace);
    } else {
      follow(trace);
    }

    toast.success(message, { action: { label: "Open run", onClick: () => navigate(`/runs/${runId}`) } });
    void queryClient.invalidateQueries({ queryKey: [FOLLOW_RUNS_KEY] });
  }, [navigate, open, follow, queryClient]);
}
