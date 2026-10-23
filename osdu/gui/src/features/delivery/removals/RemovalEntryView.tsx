import { useState, type ReactNode } from "react";
import { Link, useNavigate } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowRight, ShieldAlert, ShieldCheck } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { ExplainMark } from "@/components/ExplainTip";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { removalApi, type RemovalEntry, type RemovalFlow, type RemovalSource, type RemovedKeyState } from "../../../api/removals";
import { ProblemView } from "../TemplateSheet";
import { DeletionCounts, RemovalRunPill } from "./RemovalBadges";
import { ApproveDialog } from "./RemovalDialogs";
import { RemovalRunsTable } from "./RemovalRunsTable";
import { RemovedKeysGrid } from "./RemovedKeysGrid";
import { RemovalRunsToolbar, RemovalViewSwitch, type RemovalShow, type RemovalView } from "./RemovalViewSwitch";
import { columnsLine, deletionsRoute, finished, guardLine, heldText, ledgerName, sourceLine } from "./removalFormat";

/** How often the runs are read again, so a run under way shows as it lands. */
const REFRESH_MS = 15000;

/** The runs the view lists of a removal. */
const RUNS = 50;

/** One fact of the removal, in a line with the others: a quiet label, its value, and on hover all of it. */
function Fact({ label, value, title, body, testId }: { label: string; value: string; title: string; body: string; testId: string }) {
  return (
    <RichTooltip title={title} body={body} mono>
      <span className="inline-flex min-w-0 max-w-full items-baseline gap-1.5" data-testid={testId}>
        <span className="shrink-0 text-muted-foreground">{label}</span>
        <span className="min-w-0 truncate font-mono text-[11.5px] text-foreground">{value}</span>
      </span>
    </RichTooltip>
  );
}

function SourceFact({ label, source, testId }: { label: string; source: RemovalSource; testId: string }) {
  return (
    <Fact
      label={label}
      value={sourceLine(source)}
      title={source.reads === "table" ? "A table of a source database" : "Files a SQLFlow reader reads"}
      body={`Read from ${sourceLine(source)}\nKey from ${columnsLine(source)}`}
      testId={testId}
    />
  );
}

/**
 * One removal of a removal flow, as the Removals page shows it: a compact heading (the removal, the delivery flow whose ledger
 * it writes to, that ledger's deletions by state), what it reads and compares under in one line (all of it on hover), its last
 * run, and when its guard held what it found, one line saying so with the way to review and approve them. Under it, the
 * ledger's deletions as a grid that fills what the window leaves and reads thousands a page at a time, or the removal's runs;
 * the switch between them leads the grid's toolbar.
 */
export function RemovalEntryView({ flow, entry, view, state, onShow, omitName = false }: {
  flow: RemovalFlow;
  entry: RemovalEntry;
  view: RemovalView;
  state: RemovedKeyState | null;
  onShow: (show: RemovalShow) => void;
  /** Leaves the removal's name out of the heading, for a page whose own title names it. */
  omitName?: boolean;
}) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [approving, setApproving] = useState(false);
  const runs = useQuery({
    queryKey: ["delivery", "removals", "runs", flow.pipelineId, flow.partition, entry.name],
    queryFn: () => removalApi.runs(flow.pipelineId, { partition: flow.partition, entry: entry.name, limit: RUNS }),
    enabled: flow.partition !== undefined && view === "runs",
    refetchInterval: REFRESH_MS,
  });
  const route = deletionsRoute(entry, flow.partition);
  const last = entry.lastRun;
  const values = Object.entries(entry.values);
  const held = last?.status === "guarded" ? last : undefined;
  const review = () => onShow({ view: "deletions", state: "awaiting-approval" });

  let below: ReactNode;
  if (entry.ledgerFlowId === undefined) {
    below = (
      <p className="text-[13px] text-muted-foreground" data-testid="removal-entry-no-ledger">
        {`${ledgerName(entry)} keeps no ledger in ${flow.partition ?? "this partition"} yet: it has delivered nothing there, so there is nothing to compare. Its first run registers the ledger.`}
      </p>
    );
  } else if (view === "deletions") {
    below = (
      <RemovedKeysGrid
        key={entry.ledgerFlowId}
        ledgerFlowId={entry.ledgerFlowId}
        state={state}
        onState={(next) => onShow({ state: next })}
        leading={<RemovalViewSwitch view={view} onShow={onShow} />}
        lift={entry.deliveryPipelineId === undefined
          ? undefined
          : { pipelineId: entry.deliveryPipelineId, scope: { interfaceName: entry.interface ?? null, partition: flow.partition ?? null }, flowName: ledgerName(entry) }}
      />
    );
  } else {
    below = runs.isError
      ? <ProblemView error={runs.error} testId="removal-runs-error" />
      : (
        <RemovalRunsTable
          runs={runs.data}
          toolbar={<RemovalRunsToolbar onShow={onShow} />}
          emptyMessage="This removal has not run in the partition yet."
        />
      );
  }

  return (
    <div className="flex min-w-0 flex-col gap-3" data-testid="removal-entry" data-removal={entry.name}>
      <Card className="gap-2 rounded-lg px-3 py-2.5">
        <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
          {omitName
            ? <span className="text-[12.5px] text-muted-foreground">Writes to</span>
            : (
              <>
                <span className="text-[13.5px] font-medium" data-testid="removal-entry-name">{entry.name}</span>
                <ArrowRight className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
              </>
            )}
          {route === null
            ? <span className="font-mono text-[12.5px]">{ledgerName(entry)}</span>
            : <Link to={route} className="font-mono text-[12.5px] text-primary hover:underline" data-testid="removal-entry-flow">{ledgerName(entry)}</Link>}
          <span className="ml-auto"><DeletionCounts counts={entry.deletions} testId="removal-entry-deletions" /></span>
        </div>
        <div className="flex min-w-0 flex-wrap items-baseline gap-x-5 gap-y-1 text-[12px]" data-testid="removal-entry-facts">
          {entry.keys !== undefined && <SourceFact label="Keys" source={entry.keys} testId="removal-entry-keys" />}
          {entry.removed !== undefined && <SourceFact label="Removed" source={entry.removed} testId="removal-entry-removed" />}
          <Fact
            label="Under"
            value={values.length === 0 ? "every record" : values.map(([name, value]) => `${name}=${value}`).join(", ")}
            title="Compared under"
            body={values.length === 0
              ? "Every record of the ledger is compared."
              : `Only the records the delivery flow planned under these values, and the record table's rows in that scope, are compared:\n${values.map(([name, value]) => `${name}=${value}`).join("\n")}`}
            testId="removal-entry-scope"
          />
          <Fact label="Guard" value={`${flow.guardPercent}%${flow.guardRecords === undefined ? "" : `, ${flow.guardRecords.toLocaleString("en-US")}`}`} title="Guard" body={`${guardLine(flow)}.`} testId="removal-entry-guard" />
          <Fact
            label="Referenced"
            value={entry.keepReferenced ? "kept" : "removed"}
            title="Records other records name"
            body={entry.keepReferenced
              ? "A record other records in OSDU still name stays there until they are gone; its deletion stays pending, and the next run tries again."
              : "Taken out of OSDU whatever names it."}
            testId="removal-entry-referenced"
          />
        </div>
        <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1 border-t pt-2 text-[12.5px]" data-testid="removal-entry-last">
          <span className="font-medium">Last run</span>
          {last === undefined
            ? <span className="text-muted-foreground">none yet: a run of the flow reads the sources, compares them with the ledger and takes out what is gone.</span>
            : (
              <>
                <RemovalRunPill status={last.status} error={last.error} testId="removal-entry-last-status" />
                <span className="min-w-0 text-muted-foreground">
                  {`#${last.removalRunId}, `}
                  <RelativeTime value={last.startedUtc} absolute={false} />
                  {`: ${(last.tally.sourceKeys + last.tally.listedKeys).toLocaleString("en-US")} keys read, ${last.tally.detected.toLocaleString("en-US")} found removed, ${finished(last.tally).toLocaleString("en-US")} taken out`}
                </span>
                {last.runId !== undefined && (
                  <Link to={`/runs/${last.runId}`} className="ml-auto text-[12px] text-primary hover:underline" data-testid="removal-entry-last-run">Open run</Link>
                )}
              </>
            )}
        </div>
        {entry.awaitingApproval > 0 && flow.partition !== undefined && (
          <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1 rounded-md border border-warning/40 bg-warning/5 px-2.5 py-1.5 text-[12.5px]" data-testid="removal-approval">
            <ShieldAlert className="size-4 shrink-0 text-warning" aria-hidden />
            <span className="font-medium">{`${entry.awaitingApproval.toLocaleString("en-US")} removed key${entry.awaitingApproval === 1 ? "" : "s"} wait for approval`}</span>
            <ExplainMark
              title="Why they wait"
              text={`${held === undefined ? "A run's guard held them" : `Removal run ${held.removalRunId} held them: ${heldText(held)}`}. A source that suddenly holds far fewer keys is more often a broken read than a real mass removal. Nothing is done with them, and no row of them is kept out, until an operator approves the count.`}
              testId="removal-approval-why"
            />
            <span className="ml-auto flex gap-1.5">
              <Button variant="ghost" size="xs" onClick={review} data-testid="removal-approval-review">Review</Button>
              <Button variant="outline" size="xs" className="border-warning/60" onClick={() => setApproving(true)} data-testid="removal-approval-approve">
                <ShieldCheck />
                Approve
              </Button>
            </span>
          </div>
        )}
      </Card>

      {below}

      {flow.partition !== undefined && (
        <ApproveDialog
          open={approving}
          onClose={() => setApproving(false)}
          pipelineId={flow.pipelineId}
          partition={flow.partition}
          entry={entry}
          run={held}
          onReview={review}
          onQueued={(accepted) => {
            toast.success(
              `Approval of ${accepted.expected.toLocaleString("en-US")} removed key${accepted.expected === 1 ? "" : "s"} queued as a run; it takes them out of OSDU.`,
              { action: { label: "Open run", onClick: () => navigate(`/runs/${accepted.runId}`) } });
            void queryClient.invalidateQueries({ queryKey: ["delivery", "removals"] });
          }}
        />
      )}
    </div>
  );
}
