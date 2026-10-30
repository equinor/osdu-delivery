import type { UseQueryOptions } from "@tanstack/react-query";
import { datasourceApi } from "@/api/endpoints";
import type { ComputeTask } from "@/api/types";

const TERMINAL = new Set(["succeeded", "failed", "cancelled", "skipped"]);

/** How long one poll waits on the control plane for the task to finish before it answers with the task as it stands. */
const WAIT_MS = 10_000;

/** How long to leave a task that answered unfinished before asking again. */
const POLL_MS = 250;

/**
 * The query that polls one compute task a node runs for the delivery pages (a value check) until it has finished, through
 * SQLFlow's compute task endpoint. Each request long-polls, so the result arrives in one round trip once a node has produced
 * it. A page that polls several at once gives each its own.
 */
export function computeTaskQuery(taskId: string | null): UseQueryOptions<ComputeTask> {
  return {
    queryKey: ["compute-tasks", taskId],
    queryFn: ({ signal }) => {
      if (taskId === null) {
        throw new Error("There is no compute task to poll.");
      }

      return datasourceApi.task(taskId, WAIT_MS, signal);
    },
    enabled: taskId !== null,
    refetchInterval: (query) => {
      const task = query.state.data;
      return task !== undefined && TERMINAL.has(task.status) ? false : 250;
    },
  };
}

export function isTerminalTask(task: ComputeTask | undefined): boolean {
  return task !== undefined && TERMINAL.has(task.status);
}

/** One compute task followed to its end, for what acts on its result itself rather than drawing it: a file to save. */
export async function settledTask(taskId: string): Promise<ComputeTask> {
  for (;;) {
    const task = await datasourceApi.task(taskId, WAIT_MS);
    if (isTerminalTask(task)) {
      return task;
    }

    await new Promise((resolve) => setTimeout(resolve, POLL_MS));
  }
}
