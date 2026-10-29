// What the download flow's tab shows of its downloads (osdu/docs/reference/flow/download.md): where a record stands, how a run
// ended, and why a run wrote every record again, each as a glyph in its tone with a label and what it means on hover; and the
// payload a run's trigger sends.

import { Archive, CircleAlert, CircleCheck, CircleX, Hourglass, Loader2, type LucideIcon } from "lucide-react";
import type { DownloadRecordState, DownloadRewrite, DownloadRunStatus } from "../../../api/downloads";

/** How a state or a status is drawn: its glyph, the tone of the glyph alone, its label and what it means. */
export interface DownloadVisual {
  icon: LucideIcon;
  label: string;
  tone: string;
  hint: string;
  spin?: boolean;
}

export const STATE_VISUALS: Record<DownloadRecordState, DownloadVisual> = {
  written: { icon: CircleCheck, label: "Written", tone: "text-success", hint: "OSDU serves it, and its file holds the version listed." },
  due: { icon: Hourglass, label: "Due", tone: "text-info", hint: "OSDU serves it, and no file holds the version listed yet: the next run writes it." },
  problem: {
    icon: CircleAlert, label: "Problem", tone: "text-warning",
    hint: "Its last write did not happen: storage did not return the record. The row says why, and the next run tries again.",
  },
  gone: { icon: Archive, label: "Gone", tone: "text-muted-foreground", hint: "OSDU no longer serves it. Its file stays where it is." },
};

export const RUN_STATUS_VISUALS: Record<DownloadRunStatus, DownloadVisual & { pill: "success" | "info" | "destructive" }> = {
  running: { icon: Loader2, label: "running", tone: "text-info", hint: "The run is listing the kind or writing files.", spin: true, pill: "info" },
  completed: {
    icon: CircleCheck, label: "completed", tone: "text-success",
    hint: "Listed the kind whole and wrote every record due; a record storage did not return waits for the next run.", pill: "success",
  },
  failed: { icon: CircleX, label: "failed", tone: "text-destructive", hint: "Stopped on an error; the files it wrote are recorded.", pill: "destructive" },
};

export const REWRITE_TEXT: Record<DownloadRewrite, string> = {
  forced: "Wrote every record OSDU serves again, as the run asked.",
  moved: "Wrote every record OSDU serves again: its folder is not the one the last completed run wrote, so it held none of the files.",
};

/** A state's visual; an unknown one is drawn as the state a page cannot place. */
export function stateVisual(state: string): DownloadVisual {
  return STATE_VISUALS[state as DownloadRecordState] ?? { icon: CircleAlert, label: state, tone: "text-muted-foreground", hint: state };
}

/** A run status's visual; an unknown one is drawn as a failure, which says nothing it does not know. */
export function runStatusVisual(status: string): DownloadVisual & { pill: "success" | "info" | "destructive" } {
  return RUN_STATUS_VISUALS[status as DownloadRunStatus] ?? { ...RUN_STATUS_VISUALS.failed, label: status };
}

/** The payload a run's trigger sends: the downloads picked (none takes every one), and whether it writes every record again. */
export function downloadsPayload(names: readonly string[], force: boolean): Record<string, unknown> {
  return {
    ...(names.length === 0 ? {} : { downloads: [...names] }),
    ...(force ? { force: true } : {}),
  };
}

