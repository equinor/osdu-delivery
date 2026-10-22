import { useCallback, useEffect, useMemo, useState } from "react";
import { Navigate, useNavigate, useParams, useSearchParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Eraser, Link2, RefreshCw, RotateCcw, Send, ShieldCheck, Trash2, Unlock } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { isApiError } from "@/api/client";
import { useAuth } from "@/auth/AuthContext";
import { deliveryApi, flowLedgerRoute, ledgerLabel, type DeliveryFlowScope, type DeliveryRecordRef, type RemovalScope } from "../../api/delivery";
import { artifactApi } from "../../api/artifacts";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { CorrelationError } from "@/components/CorrelationError";
import { EmptyState } from "@/components/EmptyState";
import { IdChip } from "@/components/IdChip";
import { Page } from "@/components/Page";
import { useTabTitle } from "@/layout/workbench/TabsContext";
import { BlockedBadge, RecordStatusBadge } from "./DeliveryBadges";
import { RecordJourney, RecordMilestones } from "./RecordJourney";
import { RecordName } from "./RecordName";
import { RECORD_ARTIFACTS_SHOWN, RecordArtifacts, RecordUndoNotice } from "./RecordArtifacts";
import { RecordOsduView } from "./RecordOsduView";
import { RecordParentChips } from "./RecordParentChips";
import { settle } from "./answers";
import { RecordRenderTab, type RenderResults } from "./RecordRenderTab";
import { RecordSituation } from "./RecordSituation";
import { stranded } from "./strandedRecord";
import { RecordSourceTab } from "./RecordSourceTab";
import { RedeliverDialog } from "./RedeliverDialog";
import { ReleaseDialog } from "./ReleaseDialog";
import { RemovalDialog } from "./RemovalDialog";
import { TaskResultCard } from "./TaskResultCard";
import { isTerminalTask, useComputeTask } from "./useComputeTask";
import { useRecordOsduRead } from "./useRecordOsduRead";
import { shortId } from "./idTail";

/**
 * The record page's tabs, one question each: what happened to it, where it came from, what the mapping makes of it
 * (and whether OSDU holds that), what OSDU holds, and what its deliveries created there beside it. `?tab=` opens the page
 * on one of them, and `?tab=osdu` also reads the record from OSDU.
 */
const RECORD_TABS = ["timeline", "source", "render", "osdu", "artifacts"] as const;
type RecordTab = (typeof RECORD_TABS)[number];

/** What the task card under the header calls a removal this page queued, by its scope. */
const REMOVAL_LABELS: Record<RemovalScope, string> = {
  record: "Remove from OSDU",
  previous: "Restore the previous version",
  history: "Purge history in OSDU",
  everything: "Purge from OSDU",
};

/** The tabs the page's earlier tabs went by, and the tab each now lands on, so an old link still lands somewhere. */
const EARLIER_TABS = new Map<string, RecordTab>([
  ["history", "timeline"],
  ["activity", "timeline"],
  ["document", "render"],
  ["compare", "render"],
  ["context", "render"],
  // The records waiting for this one are a line of the header's situation, on every tab.
  ["references", "timeline"],
]);

/** The tab a `?tab=` names, or the one an earlier name of it went by; the timeline for anything else. */
function tabFromParam(value: string | null): RecordTab {
  if (value === null) {
    return "timeline";
  }

  return (RECORD_TABS as readonly string[]).includes(value) ? value as RecordTab : EARLIER_TABS.get(value) ?? "timeline";
}

/**
 * One record of the ledger, top down: who it is and where it stands (the header: identity, custody state, the
 * situation its state calls for, and the operations on it), how far it has come (the milestones), and the evidence
 * behind each answer, one tab per question.
 */
export default function DeliveryRecordPage() {
  const { flowId, key } = useParams<{ flowId: string; key: string }>();
  if (!flowId || !key) {
    return <Navigate to="/delivery" replace />;
  }

  return <DeliveryRecordContent flowId={flowId} deliveryKey={key} />;
}

/** One flow's record: the same source row read by another flow is that flow's record, with a page of its own. */
function DeliveryRecordContent({ flowId, deliveryKey }: DeliveryRecordRef) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [confirm, setConfirm] = useState<"send-now" | null>(null);
  const [redeliverOpen, setRedeliverOpen] = useState(false);
  const [adoptOpen, setAdoptOpen] = useState(false);
  const [removeOpen, setRemoveOpen] = useState(false);
  // The removal a node runs for this record, shown under the header whatever tab is open, since it changes the record.
  const [removal, setRemoval] = useState<{ taskId: string; label: string } | null>(null);
  // A render of the record from its current source row, with a read of what OSDU holds beside it, shown on the Render
  // tab where it was asked for; kept here so it is still there after a look at another tab.
  const [renderResults, setRenderResults] = useState<RenderResults | null>(null);
  const ref = useMemo<DeliveryRecordRef>(() => ({ flowId, deliveryKey }), [flowId, deliveryKey]);
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");
  const [searchParams] = useSearchParams();
  const askedTab = searchParams.get("tab");
  const [tab, setTab] = useState<RecordTab>(tabFromParam(askedTab));

  const query = useQuery({
    queryKey: ["delivery", "record", flowId, deliveryKey],
    queryFn: () => deliveryApi.record(ref),
    refetchInterval: (q) => {
      const status = q.state.data?.record.status;
      return status === "pending" || status === "delivering" || status === "waiting" ? 3000 : 15000;
    },
  });
  const attempts = useQuery({
    queryKey: ["delivery", "record", flowId, deliveryKey, "attempts"],
    queryFn: () => deliveryApi.attempts(ref, 200),
    refetchInterval: 10000,
  });
  const activities = useQuery({
    queryKey: ["delivery", "record", flowId, deliveryKey, "activities"],
    queryFn: () => deliveryApi.recordActivities(ref, 200),
    refetchInterval: 10000,
  });
  // The record's row through its ingestion table: its arrival and every change of it, each with the runs that made it.
  // A plan that records a change moves it, so it is read again with the record's history, if less often: the runs
  // behind a change are past runs, and one change is one read of the catalog.
  const chain = useQuery({
    queryKey: ["delivery", "record", flowId, deliveryKey, "chain"],
    queryFn: () => deliveryApi.recordChain(ref),
    refetchInterval: 30000,
  });
  // What the record's deliveries created in OSDU: its own tab, and the header's line while an undo still has some to take.
  const artifacts = useQuery({
    queryKey: ["delivery", "record", flowId, deliveryKey, "artifacts"],
    queryFn: () => artifactApi.recordArtifacts(ref, RECORD_ARTIFACTS_SHOWN),
    refetchInterval: 15000,
  });
  const removalTask = useComputeTask(removal?.taskId ?? null);
  useTabTitle(query.data ? (query.data.record.label ?? query.data.record.sourceKey) : undefined);

  const refresh = useCallback(() => void queryClient.invalidateQueries({ queryKey: ["delivery"] }), [queryClient]);

  // A removal that finished on a node changed the ledger (the record is now deleted and blocked): refetch the
  // record once the task reaches a terminal state, so the page reflects it without a manual reload.
  const finishedRemoval = isTerminalTask(removalTask.data) ? removalTask.data?.taskId ?? null : null;
  useEffect(() => {
    if (finishedRemoval !== null) {
      refresh();
    }
  }, [finishedRemoval, refresh]);
  const fail = (error: unknown) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error));

  const verify = useMutation({
    mutationFn: () => deliveryApi.verify(ref),
    onSuccess: (accepted) => { toast.success("Verify run queued."); navigate(`/runs/${accepted.runId}`); },
    onError: fail,
  });
  // A sync reads the record's row from its ingestion table and consolidates the ledger with it; it sends nothing, so it
  // stays on the page, and the page reads the record again as the run lands.
  const sync = useMutation({
    mutationFn: () => deliveryApi.syncRecord(ref),
    onSuccess: (accepted) => {
      toast.success("Timeline sync queued.", { action: { label: "Open run", onClick: () => navigate(`/runs/${accepted.runId}`) } });
      refresh();
    },
    onError: fail,
  });
  const release = useMutation({
    mutationFn: () => deliveryApi.release(ref),
    onSuccess: (result) => {
      setConfirm(null);
      toast.success(result.released > 0 ? "Record released." : "Nothing to release.");
      refresh();
    },
    onError: (error) => { setConfirm(null); fail(error); },
  });
  // Taking over the record OSDU already holds at the id this record's key makes: the record claims it, and the next run
  // (queued now when asked) writes this row there.
  const adopt = useMutation({
    mutationFn: (run: boolean) => deliveryApi.adopt(ref, run),
    onSuccess: (result) => {
      setAdoptOpen(false);
      toast.success(
        result.runId ? "Adoption asked: a deliver run takes the record over now." : "Adoption asked: the flow's next run takes the record over.",
        result.runId ? { action: { label: "Open run", onClick: () => navigate(`/runs/${result.runId}`) } } : undefined);
      refresh();
    },
    onError: (error) => { setAdoptOpen(false); fail(error); },
  });
  // A read of the record from OSDU, shown on the OSDU tab and kept here so it is still there after a look at another
  // tab. A page opened with ?tab=osdu reads it once, as soon as the record is known to have an id OSDU may hold.
  const readable = query.data !== undefined && query.data.record.targetId !== null && query.data.record.status !== "deleted" && canOperate;
  const osdu = useRecordOsduRead(ref, { readable, readAtOnce: askedTab === "osdu" });
  // Where the record came from: its rows as the ingestion tables hold them now, read with the flow's own connection, with
  // the origin file and row the ledger records against every delivered version. The Source tab shows the answer, or why
  // there is none.
  const readSource = useMutation({ mutationFn: () => deliveryApi.readSource(ref) });
  // What the mapping makes of the record now: rendered from its current source row, and, when OSDU may hold the record,
  // read from OSDU through its flow's route to set beside it, both at once. Neither sends nor writes anything; each says
  // what it found, or why it found nothing.
  const render = useMutation({
    mutationFn: async (readOsdu: boolean): Promise<RenderResults> => {
      const [preview, read] = await Promise.all([
        settle(deliveryApi.previewRecord(ref)),
        readOsdu ? settle(deliveryApi.read(ref)) : Promise.resolve(null),
      ]);
      return { preview, read, renderedUtc: new Date().toISOString() };
    },
    onSuccess: setRenderResults,
    onError: fail,
  });
  if (query.isError) {
    // A record deleted from the ledger after it was removed from OSDU: the line the ledger keeps of it says who and when.
    if (isApiError(query.error) && query.error.status === 404 && query.error.title === "Deleted from the ledger") {
      return (
        <Page data-testid="page-delivery-record">
          <EmptyState
            icon={<Eraser />}
            title="Deleted from the ledger"
            description={query.error.detail ?? undefined}
            data-testid="record-purged"
          />
        </Page>
      );
    }

    return (
      <Page data-testid="page-delivery-record">
        {isApiError(query.error) ? <CorrelationError error={query.error} /> : <p className="text-[13px] text-destructive">{String(query.error)}</p>}
      </Page>
    );
  }

  const detail = query.data;
  if (detail === undefined) {
    return (
      <Page data-testid="page-delivery-record">
        <Skeleton className="h-28 w-full rounded-lg" />
        <Skeleton className="h-16 w-full rounded-lg" />
        <Skeleton className="h-80 w-full rounded-lg" />
      </Page>
    );
  }

  const record = detail.record;
  const busy = verify.isPending || sync.isPending || release.isPending || adopt.isPending || osdu.pending || readSource.isPending
    || render.isPending;
  const canActOnTarget = record.targetId !== null && record.status !== "deleted";
  // The record's ledger: the interface of its source and the partition it delivers to, which every view of it acts in.
  const flowScope: DeliveryFlowScope = { interfaceName: detail.interface ?? null, partition: detail.partition ?? null };
  const flowLabel = detail.flowName === null ? undefined : ledgerLabel(detail.flowName, { interfaceName: flowScope.interfaceName });

  return (
    <Page data-testid="page-delivery-record">
      <Card className="gap-2 rounded-lg p-3" data-testid="record-header">
        <div className="flex flex-wrap items-center gap-2">
          <h1 className="min-w-0 break-words text-lg font-semibold leading-7">{record.label ?? record.sourceKey}</h1>
          <RecordStatusBadge status={record.status} />
          {record.blocked && <BlockedBadge />}
          <div className="grow" />
          <div className="flex flex-wrap items-center gap-2">
            <Button
              variant="outline"
              size="sm"
              onClick={() => sync.mutate()}
              disabled={busy || detail.pipelineId === null || !canOperate}
              title={canOperate
                ? "Read the record's row from its ingestion table and consolidate the ledger with it: its arrival, a change the ledger never saw (planned by the next run), a row that is gone. Nothing is sent."
                : "A sync runs on a node, which takes a signed-in session."}
              data-testid="record-sync"
            >
              <RefreshCw />
              Sync timeline
            </Button>
            <Button variant="outline" size="sm" onClick={() => verify.mutate()} disabled={busy || !canActOnTarget} title="Queue a verify run scoped to this record: compares what OSDU holds against the ledger" data-testid="record-verify">
              <ShieldCheck />
              Verify
            </Button>
            <Button variant="outline" size="sm" onClick={() => setRedeliverOpen(true)} disabled={busy || detail.pipelineId === null} title="Bring the record up to date from its current source row, or send it again" data-testid="record-redeliver">
              <RotateCcw />
              Redeliver
            </Button>
            {(record.blocked || stranded(record)) && (
              <Button
                variant="outline"
                size="sm"
                onClick={() => release.mutate()}
                disabled={busy}
                title="Let the record go again: a document it kept is sent as it is; otherwise the next run plans it from its row"
                data-testid="record-release"
              >
                <Unlock />
                Release
              </Button>
            )}
            {record.adoptable && (
              <Button
                variant="outline"
                size="sm"
                onClick={() => setAdoptOpen(true)}
                disabled={busy || !canOperate}
                title="Take over the record OSDU already holds at the id this record's key makes, written by another system or another instance of the delivery: the next run claims it and writes this row there. To adopt many, use Adopt on the flow's Delivery tab or Issues tab"
                data-testid="record-adopt"
              >
                <Link2 />
                Adopt
              </Button>
            )}
            {record.status === "waiting" && (
              <Button variant="outline" size="sm" onClick={() => setConfirm("send-now")} disabled={busy} data-testid="record-send-now">
                <Send />
                Send without waiting
              </Button>
            )}
            <Button
              variant="destructive-outline"
              size="sm"
              onClick={() => setRemoveOpen(true)}
              // A record removed from OSDU already is still the dialog's: deleting it from the ledger starts there.
              disabled={busy || record.targetId === null || detail.pipelineId === null}
              title={record.status === "deleted" ? "Removed from OSDU: delete it from the ledger" : "Remove from OSDU, and from the ledger if you choose"}
              data-testid="record-delete"
            >
              <Trash2 />
              Remove
            </Button>
          </div>
        </div>
        <div className="flex flex-wrap items-center gap-1.5">
          {record.targetId && (
            <IdChip
              label="osdu"
              value={record.targetId}
              display={<RecordName id={record.targetId} className="max-w-[260px]" />}
              testId="record-target"
              copyTestId="copy-record-target"
            />
          )}
          <RecordParentChips
            record={record}
            parent={detail.parent}
            readBy={detail.readBy ?? []}
            explorerPartition={record.partition ?? detail.partition ?? null}
            canExplore={canOperate}
          />
          {detail.pipelineId && <IdChip label="flow" value={detail.pipelineId} display={flowLabel} to={flowLedgerRoute(detail.pipelineId, flowScope)} testId="record-pipeline-link" copyTestId="copy-record-pipeline" />}
          {(detail.record.partition ?? detail.partition) && (
            <IdChip label="partition" value={(detail.record.partition ?? detail.partition)!} testId="record-partition" copyTestId="copy-record-partition" />
          )}
          {record.lastSubmissionId && <IdChip label="submission" value={record.lastSubmissionId} display={shortId(record.lastSubmissionId)} to={`/delivery/submissions/${record.lastSubmissionId}`} testId="record-submission-link" copyTestId="copy-record-submission" />}
        </div>
        <RecordSituation record={record} waitsOn={detail.waitsOn} waitedOnBy={detail.waitedOnBy ?? []} />
        <RecordUndoNotice artifacts={artifacts.data} onOpen={() => setTab("artifacts")} />
      </Card>

      <RecordMilestones record={record} attempts={attempts.data} chain={chain.data} />

      {removal !== null && <TaskResultCard key={removal.taskId} label={removal.label} task={removalTask.data} testId="record-task" />}

      <Tabs value={tab} onValueChange={(next) => setTab(tabFromParam(next))}>
        <TabsList data-testid="record-tabs">
          <TabsTrigger value="timeline" data-testid="record-tab-timeline">Timeline</TabsTrigger>
          <TabsTrigger value="source" data-testid="record-tab-source">Source</TabsTrigger>
          <TabsTrigger value="render" data-testid="record-tab-render">Render</TabsTrigger>
          <TabsTrigger value="osdu" data-testid="record-tab-osdu">OSDU</TabsTrigger>
          <TabsTrigger value="artifacts" data-testid="record-tab-artifacts">
            Artifacts
            {(artifacts.data?.length ?? 0) > 0 && (
              <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{artifacts.data!.length.toLocaleString()}</span>
            )}
          </TabsTrigger>
        </TabsList>
        <TabsContent value="timeline">
          <RecordJourney record={record} attempts={attempts.data} activities={activities.data} chain={chain.data} />
        </TabsContent>
        <TabsContent value="source">
          <RecordSourceTab
            record={record}
            keyColumns={detail.keyColumns ?? null}
            canRead={detail.pipelineId !== null && !busy}
            reading={readSource.isPending}
            onRead={() => readSource.mutate()}
            answer={readSource.isIdle || readSource.isPending ? null : { rows: readSource.data ?? null, error: readSource.error ?? undefined }}
          />
        </TabsContent>
        <TabsContent value="render">
          <RecordRenderTab
            record={record}
            canOperate={canOperate}
            canRender={detail.pipelineId !== null && !busy}
            rendering={render.isPending}
            onRender={() => render.mutate(canActOnTarget)}
            results={renderResults}
          />
        </TabsContent>
        <TabsContent value="osdu">
          <RecordOsduView
            record={record}
            deliveryRef={ref}
            pipelineId={detail.pipelineId}
            flowScope={flowScope}
            canOperate={canOperate}
            disabled={busy}
            osdu={osdu}
            popout
          />
        </TabsContent>
        <TabsContent value="artifacts">
          <RecordArtifacts query={artifacts} />
        </TabsContent>
      </Tabs>

      <ConfirmDialog
        open={confirm === "send-now"}
        title="Send without waiting"
        message="Send this record on its next claim as it is, without waiting for the record it refers to. Until that record lands, the reference points at nothing, and a route that checks references can refuse it. The release is recorded under your name."
        confirmLabel="Send without waiting"
        busy={release.isPending}
        onConfirm={() => release.mutate()}
        onClose={() => setConfirm(null)}
      />
      {record.adoptable && (
        <ReleaseDialog
          open={adoptOpen}
          title="Adopt the record OSDU holds"
          message={`OSDU already holds a record at ${record.occupiedTargetId ?? "the id this record's key makes"}${record.occupiedTargetVersion == null ? "" : ` (version ${record.occupiedTargetVersion} when the record was held)`}, and no ledger claimed it. Adopting links this record to it: the flow's next run makes the id from the record's key, takes over the record OSDU holds there at the version it holds then, and writes this row there as an update of it. If OSDU holds nothing there by then, the row is delivered as a new record. The adoption is recorded under your name.`}
          confirmLabel="Adopt"
          busy={adopt.isPending}
          testId="record-adopt-dialog"
          onConfirm={(run) => adopt.mutate(run)}
          onClose={() => setAdoptOpen(false)}
        />
      )}
      {detail.pipelineId !== null && redeliverOpen && (
        <RedeliverDialog
          open
          onClose={() => setRedeliverOpen(false)}
          pipelineId={detail.pipelineId}
          flowId={ref.flowId}
          flowScope={flowScope}
          flowName={ledgerLabel(detail.flowName ?? "this flow", flowScope)}
          selection={{ kind: "keys", keys: [deliveryKey] }}
          singleLabel={record.label ?? record.sourceKey}
          startMode={record.status === "delivered" ? "uptodate" : "again"}
          onDone={(outcome) => {
            const what = outcome.mode === "uptodate" ? "Bringing the record up to date" : "Sending the record again";
            toast.success(
              outcome.runId ? `${what}: a deliver run is queued.` : `${what} is asked of the flow's next run.`,
              outcome.runId ? { action: { label: "Open run", onClick: () => navigate(`/runs/${outcome.runId}`) } } : undefined);
            refresh();
          }}
        />
      )}
      {detail.pipelineId !== null && (
        <RemovalDialog
          open={removeOpen}
          onClose={() => setRemoveOpen(false)}
          pipelineId={detail.pipelineId}
          flowScope={flowScope}
          flowName={ledgerLabel(detail.flowName ?? "this flow", flowScope)}
          selection={{ kind: "keys", keys: [deliveryKey] }}
          singleLabel={record.label ?? record.sourceKey}
          alreadyRemoved={record.status === "deleted"}
          onPurged={(result) => {
            toast.success(result.summary);
            refresh();
          }}
          onQueued={(accepted) => {
            setRemoval({
              taskId: accepted.taskId,
              label: REMOVAL_LABELS[accepted.scope],
            });
            toast.success(accepted.scope === "previous"
              ? "Restoring the previous version: queued on a node."
              : accepted.purgeLedger ? "Removal, then deletion from the ledger: queued on a node." : "Removal queued on a node.");
          }}
        />
      )}
    </Page>
  );
}
