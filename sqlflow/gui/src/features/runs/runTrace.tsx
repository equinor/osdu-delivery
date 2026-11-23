import type { TraceSpec } from "../../layout/workbench/TraceTray";
import { RunTracePanel } from "./RunTracePanel";

/** The id the trace tray holds a run's trace under: one per run, whoever asks for it. */
export function runTraceId(runId: string): string {
  return `run:${runId}`;
}

/**
 * A run's live trace as the trace tray holds it, under a title that says what the run is. The run's page follows it while
 * the run is under way, and a page that starts a run (a module's action on its flow, say) opens it in the bottom panel at
 * once. Either way it is the one trace: opening the run's page afterwards reaches it as it is, still streaming, rather
 * than starting a second.
 */
export function runTraceSpec(runId: string, title: string): TraceSpec {
  return { id: runTraceId(runId), title, node: <RunTracePanel runId={runId} /> };
}
