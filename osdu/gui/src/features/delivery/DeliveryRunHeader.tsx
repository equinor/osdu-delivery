import { Link as RouterLink, useNavigate } from "react-router-dom";
import { DatabaseZap, ListTree } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import type { RunDetail, RunSummary } from "@/api/types";
import { DetailPair } from "@/components/DetailPair";
import { IdChip } from "@/components/IdChip";
import { deliveryRunRecordsRoute, ledgerLabel } from "../../api/delivery";
import {
  DELIVERING_OPERATIONS, listedRunCounts, runRecordCounts, runRemovalCounts, runRequest, runResult, runScope, runSubmissionId,
  type RunRecordCounts, type RunRemovalCounts,
} from "./runOutcome";
import { ReverseButton } from "./ReverseButton";
import { shortId } from "./idTail";

/** A record counter: grouped for readability, a real zero kept distinct from an unreported value (muted dash). */
function RecordCount({ value, testId }: { value: number | null; testId: string }) {
  if (value === null) {
    return <span className="text-muted-foreground" data-testid={testId}>-</span>;
  }

  return <span className="font-mono tabular-nums" data-testid={testId}>{value.toLocaleString("en-US")}</span>;
}

/**
 * The delivery run's way to the records it touched (the flow's Records tab, filtered to this run, in the interface and
 * partition it ran in), and, once a run that
 * sends records has ended, its reversal when it left anything to reverse: what it put into OSDU put back as OSDU held it
 * before (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a run or a submission). A run of a source of
 * several interfaces that names none is reversed one interface at a time, from each interface's entry in the audit trail
 * or each submission's page: the reversal works in one ledger.
 */
export function DeliveryRunActions({ run }: { run: RunDetail }) {
  const navigate = useNavigate();
  const ended = run.status === "succeeded" || run.status === "failed" || run.status === "cancelled";
  const sends = DELIVERING_OPERATIONS.includes(run.operation ?? "deliver");
  const scope = runScope(run);
  return (
    <>
      <Button
        variant="outline"
        size="sm"
        onClick={() => navigate(deliveryRunRecordsRoute(run.pipelineId, run.runId, scope))}
        data-testid="run-records"
      >
        <ListTree />
        Records of this run
      </Button>
      {sends && ended && run.pipelineId !== null && (
        <ReverseButton
          pipelineId={run.pipelineId}
          flowScope={scope}
          flowName={ledgerLabel(run.flowName, scope)}
          source={{ kind: "run", id: run.runId }}
          label="Reverse this run"
          testId="run-reverse"
        />
      )}
    </>
  );
}

/** The operations whose runs touch no record: a plan changes nothing, and deleting the ledger deletes every record it held. */
const TOUCHES_NO_RECORD: readonly string[] = ["plan", "delete-ledger"];

/**
 * A delivery run's way to the records it touched from a list of runs (the flow's Runs tab, the runs board narrowed to
 * delivery flows), whose rows open the run itself: an icon that opens the flow's Records tab narrowed to the run. A plan
 * and a ledger's deletion leave no record to lead to.
 */
export function DeliveryRunRecordsLink({ run }: { run: Pick<RunSummary, "runId" | "pipelineId" | "operation"> }) {
  if (TOUCHES_NO_RECORD.includes(run.operation ?? "deliver")) {
    return <span className="text-muted-foreground" title={`A ${run.operation} run touches no record.`}>-</span>;
  }

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Button asChild variant="ghost" size="icon-xs" className="size-5 align-middle text-muted-foreground hover:text-primary">
          <RouterLink
            to={deliveryRunRecordsRoute(run.pipelineId, run.runId)}
            aria-label="Records this run touched"
            // Inside a row that opens the run: the icon opens its records instead.
            onClick={(event) => event.stopPropagation()}
            data-testid="run-records-link"
            data-run={run.runId}
          >
            <ListTree />
          </RouterLink>
        </Button>
      </TooltipTrigger>
      <TooltipContent>Records this run touched</TooltipContent>
    </Tooltip>
  );
}

/**
 * One of a delivery run's record counts on a list of runs (the flow's Runs tab, the runs board narrowed to delivery
 * flows), in place of the row counts SQLFlow's own runs carry: what its result reports, as its page shows it, and a muted
 * dash for a count the run does not report (a plan delivers nothing; an intake plans, and its drain delivers).
 */
export function DeliveryRunListCount({ run, count }: { run: Pick<RunSummary, "operation" | "resultCounts">; count: keyof RunRecordCounts }) {
  const value = listedRunCounts(run)[count];
  return value === null
    ? <span className="text-muted-foreground">-</span>
    : <span className="font-mono tabular-nums" data-testid={`run-list-${count}`}>{value}</span>;
}

/** The cache run's way to the cache it refreshed. */
export function CacheRunActions({ run }: { run: RunDetail }) {
  const navigate = useNavigate();
  return (
    <Button
      variant="outline"
      size="sm"
      onClick={() => navigate(`/delivery/cache?flow=${encodeURIComponent(run.flowName)}`)}
      data-testid="run-cache"
    >
      <DatabaseZap />
      Open the cache
    </Button>
  );
}

/** The submission a delivery run worked on, as a chip that opens it. */
export function DeliveryRunMeta({ run }: { run: RunDetail }) {
  const submissionId = runSubmissionId(run, runResult(run));
  return submissionId === null
    ? null
    : (
      <IdChip
        label="submission"
        value={submissionId}
        display={shortId(submissionId)}
        to={`/delivery/submissions/${submissionId}`}
        testId="run-submission"
        copyTestId="copy-run-submission"
      />
    );
}

/** What a remove run changed of each record, by its scope: the headline count's label and value. */
function removalHeadline(scope: string | null, counts: RunRemovalCounts | null): { label: string; value: number | null } {
  switch (scope) {
    case "previous":
      return { label: "Restored", value: counts?.restored ?? null };
    case "restrict":
      return { label: "Restricted", value: counts?.restricted ?? null };
    case "unrestrict":
      return { label: "Lifted", value: counts?.lifted ?? null };
    default:
      return { label: "Removed", value: counts?.removed ?? null };
  }
}

/**
 * What a remove run did to the records it selected: removed (or, a step back, restored; a restriction, restricted; a lift,
 * lifted), already gone, skipped, failed, and deleted from the ledger when it was asked to.
 */
function RemovalCounts({ run }: { run: RunDetail }) {
  const counts = runRemovalCounts(runResult(run));
  const request = runRequest(run);
  // Only a removal takes a record out of OSDU, so only it finds one already gone.
  const removing = request.removalScope !== "previous" && request.removalScope !== "restrict" && request.removalScope !== "unrestrict";
  const headline = removalHeadline(request.removalScope, counts);
  return (
    <>
      <DetailPair label="Selected"><RecordCount value={counts?.selected ?? null} testId="run-removal-selected" /></DetailPair>
      <DetailPair label={headline.label}>
        <RecordCount value={headline.value} testId="run-removal-removed" />
      </DetailPair>
      {removing && <DetailPair label="Already gone"><RecordCount value={counts?.alreadyGone ?? null} testId="run-removal-gone" /></DetailPair>}
      <DetailPair label="Skipped"><RecordCount value={counts?.skipped ?? null} testId="run-removal-skipped" /></DetailPair>
      <DetailPair label="Failed"><RecordCount value={counts?.failed ?? null} testId="run-removal-failed" /></DetailPair>
      {request.purgeLedger && (
        <DetailPair label="Deleted from the ledger"><RecordCount value={counts?.purged ?? null} testId="run-removal-purged" /></DetailPair>
      )}
    </>
  );
}

/** The record counts a delivery run reported, in place of the row counts SQLFlow's own runs carry. */
export function DeliveryRunCounts({ run }: { run: RunDetail }) {
  if (run.operation === "remove") {
    return <RemovalCounts run={run} />;
  }

  const counts = runRecordCounts(runResult(run));
  return (
    <>
      <DetailPair label="Planned"><RecordCount value={counts.planned} testId="run-records-planned" /></DetailPair>
      <DetailPair label="Delivered"><RecordCount value={counts.delivered} testId="run-records-delivered" /></DetailPair>
      <DetailPair label="Held"><RecordCount value={counts.held} testId="run-records-held" /></DetailPair>
      <DetailPair label="Failed"><RecordCount value={counts.failed} testId="run-records-failed" /></DetailPair>
      <DetailPair label="Unchanged"><RecordCount value={counts.unchanged} testId="run-records-unchanged" /></DetailPair>
      <DetailPair label="Waiting"><RecordCount value={counts.waiting} testId="run-records-waiting" /></DetailPair>
    </>
  );
}
