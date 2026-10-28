import { useState, type ReactNode } from "react";
import { Link as RouterLink, useNavigate } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  ChevronDown, ChevronUp, CirclePause, CircleX, FileText, Layers, Link2, ListFilter, Loader2, Play, Rows3, ScanSearch, Unlock,
} from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { isApiError } from "@/api/client";
import { CopyButton } from "@/components/CopyButton";
import { CorrelationError } from "@/components/CorrelationError";
import { IconAction } from "@/components/IconAction";
import { cn } from "@/lib/utils";
import {
  deliveryApi, deliveryRecordRoute,
  type DeliveryFlowScope, type DeliveryIssue, type DeliveryIssueFile, type DeliveryIssueSample, type DeliveryIssueShape, type DeliveryRecord,
} from "../../api/delivery";
import { IssueMessage } from "./IssueMessage";
import { issueParts, issueStatesText, samplesText } from "./issueText";
import { CompactTime, RecordIdentity } from "./RecordCells";
import { PLANNED_PER_PASS, ReleaseDialog } from "./ReleaseDialog";
import { problemText } from "./problemText";

/** How often an open issue is read again: its counts, its samples and the files its records came from. */
const REFRESH_MS = 30000;

/** What each shape says, in a word and a sentence. */
const SHAPES: Record<DeliveryIssueShape, { label: string; icon: typeof Layers; tip: string }> = {
  set: {
    label: "Set error",
    icon: Layers,
    tip: "Every record looked at carries the same error: one mistake in the set they came from (the dataset, a cache entry, the mapping, a legal tag). Fix it once, check a sample, and release them all together.",
  },
  rows: {
    label: "Row errors",
    icon: Rows3,
    tip: "The records name different values, each its own row's: each row needs its own fix in the source. A corrected row is planned again on its own; release only once the rows, or a cause outside them, are fixed.",
  },
};

/** Where an issue lies, as a neutral chip led by its glyph, with what it means on hover. */
export function ShapeMark({ shape }: { shape: DeliveryIssueShape }) {
  const { label, icon: Icon, tip } = SHAPES[shape];
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span
          className="inline-flex shrink-0 items-center gap-1 whitespace-nowrap rounded-md border border-border bg-muted/60 px-1.5 py-px text-[11.5px] font-medium"
          data-testid="issue-shape"
          data-shape={shape}
        >
          <Icon className={cn("size-3.5 shrink-0", shape === "set" ? "text-primary" : "text-warning")} aria-hidden />
          {label}
        </span>
      </TooltipTrigger>
      <TooltipContent className="max-w-xs">{tip}</TooltipContent>
    </Tooltip>
  );
}

/**
 * Where an issue's records stand: all held, all failed, or how many of each, led by the glyph of each in its tone. A held
 * record met a data issue or a refusal that is not retried; a failed one ran out of retries.
 */
export function IssueStates({ held, failed }: { held: number; failed: number }) {
  return (
    <span
      className="inline-flex items-center gap-1 whitespace-nowrap text-[12px] text-muted-foreground"
      title={failed > 0 ? "Failed: the retries ran out. Held: a data issue, or a refusal that is not retried." : "Held: a data issue, or a refusal that is not retried."}
      data-testid="issue-states"
    >
      {held > 0 && <CirclePause className="size-3.5 shrink-0 text-warning" aria-hidden />}
      {failed > 0 && <CircleX className="size-3.5 shrink-0 text-destructive" aria-hidden />}
      {issueStatesText(held, failed)}
    </span>
  );
}

/**
 * Whether an issue's records are held because OSDU already holds a record at the ids their keys make, so they can be
 * adopted: every record of an issue shares its error, so its example, or a sample, says it for all of them.
 */
function adoptable(issue: DeliveryIssue, samples: DeliveryIssueSample[] | undefined): boolean {
  return issue.example?.adoptable === true || (samples ?? []).some((sample) => sample.record.adoptable === true);
}

/** A heading of the panel's sections, with what it shows beside it in a quieter voice. */
function SectionHeading({ title, note, testId }: { title: string; note?: ReactNode; testId?: string }) {
  return (
    <div className="flex min-w-0 items-baseline gap-2 pb-1.5">
      <span className="shrink-0 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">{title}</span>
      {note !== undefined && <span className="min-w-0 truncate text-[12px] text-muted-foreground" data-testid={testId}>{note}</span>}
    </div>
  );
}

/**
 * One issue of a flow, in the workbench's bottom panel over the Issues tab, as a run's trace opens
 * (osdu/docs/reference/concepts/record-lifecycle.md, Issues). Its bar says where the issue lies, how many records it keeps
 * blocked and how they stand, when they last changed, where it is among the issues listed with the way to the one before
 * and after, and what can be done: see every record of it, adopt what OSDU holds for them, or release them all. Below, the
 * error as its records share it, and samples spread across them, each to check (rendered as it would be now, which sends
 * nothing) or to try (released alone and run at once), beside the files they came from. Everything is read again while it
 * is open.
 */
export function IssuePanel({ pipelineId, scope, issueId, listed, ledgerName, partition, place, canBack, canForward, onStep, onClose, recordsLink }: {
  pipelineId: string;
  scope: DeliveryFlowScope;
  /** The issue's id: sixteen hexadecimal characters. */
  issueId: string;
  /** The issue as the listing has it; undefined for one the listing does not name (a link to an issue beyond its largest). */
  listed: DeliveryIssue | undefined;
  /** The ledger the issue is of, as a sentence names it. */
  ledgerName: string;
  /** The partition in view, which an adoption takes typed back when the issue's records do not name theirs. */
  partition: string | null;
  /** Where the issue is among those listed, counted from one; null when it is not among them. */
  place: { at: number; of: number } | null;
  canBack: boolean;
  canForward: boolean;
  onStep: (by: -1 | 1) => void;
  /** Closes the panel: after a release, whose issue keeps no record any more. */
  onClose: () => void;
  /** The Records tab narrowed to the issue's records. */
  recordsLink: string;
}) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [releaseOpen, setReleaseOpen] = useState(false);
  const [adoptOpen, setAdoptOpen] = useState(false);
  const [adoptTyped, setAdoptTyped] = useState("");

  const detail = useQuery({
    queryKey: ["delivery", "issue", pipelineId, scope.interfaceName ?? null, scope.partition ?? null, issueId],
    queryFn: () => deliveryApi.issue(pipelineId, issueId, scope),
    refetchInterval: REFRESH_MS,
  });
  // The samples know more of where the issue lies, and of what its records share, than the listing's newest and oldest.
  const issue = detail.data?.issue ?? listed;
  const samples = detail.data?.samples;
  const files = detail.data?.files;

  const refresh = () => void queryClient.invalidateQueries({ queryKey: ["delivery"] });
  const openRun = (runId: string) => ({ label: "Open run", onClick: () => navigate(`/runs/${runId}`) });

  // One record released and run at once: whether its issue is fixed shows on its timeline, and in this issue's count.
  const trySample = useMutation({
    mutationFn: (record: DeliveryRecord) => deliveryApi.release(record, true),
    onSuccess: (result) => {
      refresh();
      toast.success(
        result.released === 0 ? "The record was no longer blocked; a run for it is queued." : "The record is released and a run for it is queued.",
        result.runId ? { action: openRun(result.runId) } : undefined);
    },
    onError: (error) => toast.error(problemText(error)),
  });

  const release = useMutation({
    mutationFn: (run: boolean) => deliveryApi.releaseIssue(pipelineId, issueId, run, scope),
    onSuccess: (result) => {
      setReleaseOpen(false);
      refresh();
      const released = `Released ${result.released.toLocaleString()} record${result.released === 1 ? "" : "s"}`;
      toast.success(
        result.released === 0
          ? "The issue keeps no record blocked any more: none released."
          : result.runId ? `${released}; a deliver run plans them now.` : `${released}; the flow's next run plans them.`,
        result.runId ? { action: openRun(result.runId) } : undefined);
      // Released, the issue keeps no record blocked: there is nothing left to show of it.
      onClose();
    },
    onError: (error) => {
      setReleaseOpen(false);
      toast.error(problemText(error));
    },
  });

  // Every record the issue keeps held because OSDU already holds a record at the id its key makes, taken over together:
  // each record claims its id, and the next run (queued now when asked) writes the rows there.
  const adopt = useMutation({
    mutationFn: ({ confirm, run }: { confirm: string; run: boolean }) => deliveryApi.adoptIssue(pipelineId, issueId, confirm, run, scope),
    onSuccess: (result) => {
      setAdoptOpen(false);
      setAdoptTyped("");
      refresh();
      const asked = `Adoption asked of ${result.asked.toLocaleString()} record${result.asked === 1 ? "" : "s"}`;
      toast.success(
        result.asked === 0
          ? "No record of the issue was asked: they were held for something else, or a run took them up since."
          : result.runId ? `${asked}; a deliver run takes them over now.` : `${asked}; the flow's next run takes them over.`,
        result.runId ? { action: openRun(result.runId) } : undefined);
    },
    onError: (error) => {
      setAdoptOpen(false);
      toast.error(problemText(error));
    },
  });

  if (issue === undefined) {
    return (
      <div className="flex h-full min-h-0 flex-col" data-testid="issue-panel" data-issue={issueId}>
        <div className="p-3">
          {detail.isError
            ? isApiError(detail.error) ? <CorrelationError error={detail.error} /> : <p className="text-[13px] text-destructive">{problemText(detail.error)}</p>
            : <Skeleton className="h-24 w-full" />}
        </div>
      </div>
    );
  }

  const shape = issue.shape;
  const records = issue.records.toLocaleString();
  const canAdopt = adoptable(issue, samples);
  const kept = issue.example?.partition ?? partition ?? null;
  const releaseTitle = `Release ${records} record${issue.records === 1 ? "" : "s"}`;
  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="issue-panel" data-issue={issueId}>
      <div className="flex h-10 shrink-0 items-center gap-2 border-b border-border px-3" data-testid="issue-panel-header">
        <ShapeMark shape={shape} />
        <span className="flex min-w-0 items-center gap-2 overflow-hidden whitespace-nowrap text-[12.5px] text-muted-foreground" data-testid="issue-facts">
          <span>
            <span className="font-mono font-medium tabular-nums text-foreground" data-testid="issue-records">{records}</span>
            {` record${issue.records === 1 ? "" : "s"}`}
          </span>
          <IssueStates held={issue.held} failed={issue.failed} />
          <span className="inline-flex items-center gap-1">
            <span className="text-muted-foreground/60" aria-hidden>·</span>
            last changed
            {issue.oldestUtc !== issue.newestUtc && (
              <>
                <CompactTime value={issue.oldestUtc} caption="The record that changed longest ago last changed" />
                <span aria-hidden>to</span>
              </>
            )}
            <CompactTime value={issue.newestUtc} caption="The most recently changed record last changed" />
          </span>
        </span>
        <div className="ml-auto flex shrink-0 items-center gap-1">
          {place !== null && (
            <span className="mr-0.5 font-mono text-[11.5px] tabular-nums text-muted-foreground" data-testid="issue-panel-place">
              {`${place.at.toLocaleString("en-US")} of ${place.of.toLocaleString("en-US")}`}
            </span>
          )}
          <IconAction label="The issue before (Up)" icon={<ChevronUp />} disabled={!canBack} onClick={() => onStep(-1)} className="size-7" data-testid="issue-panel-back" />
          <IconAction label="The issue after (Down)" icon={<ChevronDown />} disabled={!canForward} onClick={() => onStep(1)} className="size-7" data-testid="issue-panel-forward" />
          <span className="mx-1 h-4 w-px bg-border" aria-hidden />
          <Button asChild variant="ghost" size="sm" className="h-7" data-testid="issue-records-link">
            <RouterLink to={recordsLink} title="Every record this issue keeps blocked, on the Records tab">
              <ListFilter />
              Records
            </RouterLink>
          </Button>
          {canAdopt && (
            <Button
              variant="outline"
              size="sm"
              className="h-7"
              onClick={() => setAdoptOpen(true)}
              title="Take over the records OSDU already holds at these records' ids, written by another system or another instance of the delivery: each record claims its id, and the next run writes it there"
              data-testid="issue-adopt"
            >
              <Link2 />
              {`Adopt ${records}`}
            </Button>
          )}
          <Button
            size="sm"
            className="h-7"
            onClick={() => setReleaseOpen(true)}
            title="Send every record of the issue back to delivery, once its cause is fixed"
            data-testid="issue-release"
          >
            <Unlock />
            {`Release ${records}`}
          </Button>
        </div>
      </div>

      <div className="grid min-h-0 flex-1 grid-cols-1 overflow-auto lg:grid-cols-2 lg:overflow-hidden">
        <section className="flex min-w-0 flex-col gap-3 p-3 lg:overflow-auto" aria-label="The error">
          <div className="flex min-w-0 items-start gap-2">
            <div className="min-w-0 flex-1"><IssueMessage text={issue.shared} /></div>
            <CopyButton iconOnly label="Copy the error as its records share it" text={issue.shared} testId="copy-issue-error" />
          </div>
          <div className="mt-auto flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 border-t border-border pt-2 text-[11.5px] text-muted-foreground">
            <span className="inline-flex items-center gap-1" title="The issue's id, which the Records tab and the records verb of the command line take" data-testid="issue-id">
              Issue
              <span className="font-mono text-foreground">{issue.issue}</span>
              <CopyButton iconOnly label="Copy the issue's id" text={issue.issue} testId="copy-issue-id" />
            </span>
            {issueParts(issue.shared).some((part) => part.kind === "placeholder") && (
              <span className="inline-flex items-center gap-1">
                <span className="rounded border border-border bg-muted px-1 font-mono text-[10.5px]">n</span>
                marks what each record says of its own
              </span>
            )}
          </div>
        </section>

        <div className="flex min-w-0 flex-col gap-4 border-t border-border p-3 lg:overflow-auto lg:border-l lg:border-t-0">
          <section aria-label="Samples" data-testid="issue-samples">
            <SectionHeading
              title="Samples"
              note={samples === undefined ? "spread across the issue's records" : samplesText(samples.length, issue.records, shape)}
              testId="issue-samples-say"
            />
            {samples === undefined ? <Skeleton className="h-24 w-full" /> : samples.length > 0 && (
              <ul className="flex flex-col divide-y divide-border rounded-md border border-border">
                {samples.map((sample) => (
                  <SampleRow
                    key={sample.record.deliveryKey}
                    sample={sample}
                    rows={shape === "rows"}
                    trying={trySample.isPending && trySample.variables?.deliveryKey === sample.record.deliveryKey}
                    onTry={(record) => trySample.mutate(record)}
                  />
                ))}
              </ul>
            )}
          </section>
          <FileList files={files} />
        </div>
      </div>

      <ReleaseDialog
        open={releaseOpen}
        title={releaseTitle}
        message={`Every record of ${ledgerName} this issue keeps blocked goes back to delivery, however many. A record that still holds its rendered document is sent as it is; the others are planned again from their rows, ${PLANNED_PER_PASS.toLocaleString()} to a pass, all of them by the next run, which holds a record again if its row still calls for it. A record a run blocks with this issue while the release goes is released too.`}
        confirmLabel={`Release ${records}`}
        busy={release.isPending}
        testId="issue-release-dialog"
        onConfirm={(run) => release.mutate(run)}
        onClose={() => setReleaseOpen(false)}
      >
        <div className="max-h-40 overflow-auto rounded-md bg-muted/50 px-2.5 py-2"><IssueMessage text={issue.shared} /></div>
        {shape === "rows" && (
          <p className="flex items-start gap-1.5 text-[12px] text-muted-foreground" data-testid="issue-release-rows">
            <Rows3 className="mt-0.5 size-3.5 shrink-0 text-warning" aria-hidden />
            These are row errors: a record whose row still names the same value is held again. Release them once their rows,
            or the cause outside them, are fixed.
          </p>
        )}
      </ReleaseDialog>

      {canAdopt && (
        <ReleaseDialog
          open={adoptOpen}
          title={`Adopt ${records} record${issue.records === 1 ? "" : "s"}`}
          message={`OSDU already holds a record at the id each of these records' keys makes, and no ledger claimed it: another system wrote it, or another instance of the delivery whose ledger this database does not hold. Adopting links every record of ${ledgerName} this issue keeps held to the record OSDU holds: the flow's next run takes each over at the version OSDU holds then, and writes its row there. A record whose id OSDU no longer holds by then is delivered as a new one, and one whose id another record claimed since stays that record's.`}
          confirmLabel={`Adopt ${records}`}
          busy={adopt.isPending}
          confirmDisabled={kept === null || adoptTyped.trim().toLowerCase() !== kept.toLowerCase()}
          testId="issue-adopt-dialog"
          onConfirm={(run) => adopt.mutate({ confirm: adoptTyped.trim(), run })}
          onClose={() => { setAdoptOpen(false); setAdoptTyped(""); }}
        >
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="issue-adopt-confirm" className="text-[12px] font-normal text-muted-foreground">
              <span>
                {"Type "}
                <span className="font-mono font-medium text-foreground">{kept ?? "the partition"}</span>
                {" to confirm."}
              </span>
            </Label>
            <Input
              id="issue-adopt-confirm"
              value={adoptTyped}
              onChange={(event) => setAdoptTyped(event.target.value)}
              disabled={adopt.isPending}
              autoComplete="off"
              spellCheck={false}
              className="h-8 font-mono"
              data-testid="issue-adopt-confirm-input"
            />
          </div>
        </ReleaseDialog>
      )}
    </div>
  );
}

/** The ingestion files an issue's records came from, the most records first, each with how many. */
function FileList({ files }: { files: DeliveryIssueFile[] | undefined }) {
  return (
    <section aria-label="Source files" data-testid="issue-files">
      <SectionHeading
        title="Source files"
        note={files === undefined ? undefined : files.length === 0 ? "none recorded" : `${files.length.toLocaleString("en-US")} file${files.length === 1 ? "" : "s"}, the most records first`}
      />
      {files === undefined ? <Skeleton className="h-10 w-full" /> : files.length > 0 && (
        <ul className="flex flex-col gap-0.5">
          {files.map((file) => (
            <li key={file.fileName ?? ""} className="flex min-w-0 items-baseline gap-2 text-[12px]" data-testid="issue-file">
              <span className="w-12 shrink-0 text-right font-mono tabular-nums text-muted-foreground">{file.records.toLocaleString()}</span>
              <FileText className="size-3.5 shrink-0 translate-y-0.5 text-muted-foreground" aria-hidden />
              <span className={cn("min-w-0 break-all font-mono", file.fileName === null && "text-muted-foreground")}>{file.fileName ?? "no file recorded"}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/**
 * One sample: the record as it is known (its label, source key, and the file and row it came from), the values its error
 * names when they differ from record to record, and the two checks: Check renders it as it would be now and sends nothing,
 * Try releases it alone and runs it.
 */
function SampleRow({ sample, rows, trying, onTry }: {
  sample: DeliveryIssueSample;
  rows: boolean;
  trying: boolean;
  onTry: (record: DeliveryRecord) => void;
}) {
  const record = sample.record;
  const origin = record.pendingSourceFileName !== null
    ? { fileName: record.pendingSourceFileName, rowNumber: record.pendingSourceRowNumber }
    : { fileName: record.sourceFileName, rowNumber: record.sourceRowNumber };
  return (
    <li className="flex min-w-0 items-start gap-2 px-2.5 py-1.5" data-testid="issue-sample">
      <RouterLink to={deliveryRecordRoute(record)} className="min-w-0 flex-1 rounded text-[13px] outline-none hover:underline focus-visible:ring-2 focus-visible:ring-ring/50" data-testid="issue-sample-record">
        <RecordIdentity label={record.label} sourceKey={record.sourceKey} origin={origin} />
        {rows && sample.values.length > 0 && (
          <span className="mt-0.5 flex flex-wrap gap-1" data-testid="issue-sample-values">
            {sample.values.map((value, index) => (
              <span key={`${index}-${value}`} className="rounded border border-border bg-muted/60 px-1 font-mono text-[11px]">{value === "" ? "(empty)" : value}</span>
            ))}
          </span>
        )}
      </RouterLink>
      <span className="flex shrink-0 items-center gap-0.5">
        <Tooltip>
          <TooltipTrigger asChild>
            <Button asChild variant="ghost" size="icon" className="size-7 text-muted-foreground hover:text-foreground" data-testid="issue-sample-check">
              <RouterLink to={`${deliveryRecordRoute(record)}?tab=render`} aria-label="Check: render it as it would be now">
                <ScanSearch />
              </RouterLink>
            </Button>
          </TooltipTrigger>
          <TooltipContent className="max-w-xs">
            Check: opens the record on its Render tab, which builds it from its current row with the mapping and the cache as they are now and sends nothing. Whether it still holds says whether the cause is fixed.
          </TooltipContent>
        </Tooltip>
        <Tooltip>
          <TooltipTrigger asChild>
            <Button variant="ghost" size="icon" className="size-7 text-muted-foreground hover:text-foreground" onClick={() => onTry(record)} disabled={trying} aria-label="Try: release it alone and run it" data-testid="issue-sample-try">
              {trying ? <Loader2 className="animate-spin" /> : <Play />}
            </Button>
          </TooltipTrigger>
          <TooltipContent className="max-w-xs">
            Try: releases this record alone and queues a deliver run for it, so OSDU is asked again at once: the way to check a refusal the render cannot show. What it answers lands on the record's timeline.
          </TooltipContent>
        </Tooltip>
      </span>
    </li>
  );
}
