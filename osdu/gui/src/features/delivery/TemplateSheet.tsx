import type { ReactNode } from "react";
import { Loader2 } from "lucide-react";
import { Sheet, SheetContent } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "@/api/client";
import type { ComputeTask } from "@/api/types";
import type { DeliveryTemplateDetail } from "../../api/delivery";
import { CorrelationError } from "@/components/CorrelationError";
import { RunStatusBadge } from "@/components/StatusBadge";
import { TemplateHeader } from "./TemplateHeader";
import { TemplateView } from "./TemplateView";

/** A failed call, rendered the one way API failures render: the problem with its correlation id. */
export function ProblemView({ error, testId }: { error: unknown; testId?: string }) {
  return isApiError(error)
    ? <CorrelationError error={error} data-testid={testId} />
    : <p className="text-[13px] text-destructive" data-testid={testId}>{String(error)}</p>;
}

/** Work under way: what is happening and, when a node does it, the task's status and the node that took it. */
export function TaskProgress({ label, task, testId }: { label: string; task?: ComputeTask; testId: string }) {
  return (
    <div className="flex flex-wrap items-center gap-2 text-[13px] text-muted-foreground" data-testid={testId}>
      <Loader2 className="size-4 animate-spin" />
      <span>{label}</span>
      {task !== undefined && <RunStatusBadge status={task.status} testId={`${testId}-status`} />}
      {task !== undefined && task.claimedByNode !== null && <span className="font-mono text-[11px]">{task.claimedByNode}</span>}
    </div>
  );
}

interface TemplateSheetProps {
  open: boolean;
  onClose: () => void;
  /** The kind the sheet is about, known before the template is laid out. */
  title: string;
  /** Where a schema of the OSDU data definitions is read from, for the header's facts; null for a repository's template, which names its own file. */
  source: ReactNode | null;
  /** Progress while the schema is fetched or laid out. */
  progress: ReactNode;
  /** A failure to show above the template, or instead of it. */
  problem: ReactNode;
  /** The template once it is laid out. */
  detail: DeliveryTemplateDetail | undefined;
  /** The schema being looked at from the OSDU data definitions. */
  previewSchema?: Record<string, unknown>;
  actions?: ReactNode;
  /** Holds the sheet open while a download is in flight. */
  busy?: boolean;
  testId: string;
}

/** The side sheet every template opens in on the Templates page: a repository's template, or a schema fetched from the OSDU data definitions. */
export function TemplateSheet({
  open, onClose, title, source, progress, problem, detail, previewSchema, actions, busy = false, testId,
}: TemplateSheetProps) {
  const waiting = detail === undefined && progress === null && problem === null;
  return (
    <Sheet open={open} onOpenChange={(next) => { if (!next && !busy) { onClose(); } }}>
      <SheetContent
        className="w-full gap-0 sm:max-w-5xl"
        // Focus lands on the sheet itself rather than the header's first button, whose tooltip would otherwise open with it.
        onOpenAutoFocus={(event) => { event.preventDefault(); (event.currentTarget as HTMLElement).focus(); }}
        data-testid={testId}
      >
        <TemplateHeader kind={title} detail={detail} source={source} actions={actions} />
        <div className="flex flex-1 flex-col gap-3 overflow-y-auto px-4 pb-4 pt-3">
          {problem}
          {progress}
          {waiting && <Skeleton className="h-64 w-full rounded-lg" />}
          {detail !== undefined && <TemplateView detail={detail} previewSchema={previewSchema} />}
        </div>
      </SheetContent>
    </Sheet>
  );
}
