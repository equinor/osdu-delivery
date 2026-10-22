import { useCallback, useEffect, useMemo, useState } from "react";
import { Link as RouterLink, useNavigate, useSearchParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link2, Loader2, Radar, RefreshCcw, RefreshCw, Trash2, Unlock } from "lucide-react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { Label } from "@/components/ui/label";
import { isApiError } from "@/api/client";
import {
  DELIVERY_RECORD_STATUSES, deliveryApi, deliveryRecordRoute, ledgerLabel,
  type DeliveryFlowStats, type DeliveryInterface, type DeliveryRecord, type DeliveryRecordFilter, type DeliveryRecordStatus,
  type DeliverySubmission, type DeliverySyncRequest,
} from "../../api/delivery";
import { CodeView } from "@/components/CodeView";
import { CopyButton } from "@/components/CopyButton";
import { CorrelationError } from "@/components/CorrelationError";
import { DataTable, type Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { PagedTable } from "@/components/PagedTable";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import { BlockedBadge, RecordStatusBadge, SubmissionStatusBadge, VerifyOutcomeBadge } from "./DeliveryBadges";
import { FlowStatusBar } from "./FlowStatusBar";
import { InterfacePicker } from "./InterfacePicker";
import { KindText } from "./KindText";
import { OutsidePartition } from "./PartitionNotice";
import { CompactTime, OsduTarget, RecordIdentity, type RecordOrigin } from "./RecordCells";
import { useInterfaceChoice } from "./useInterfaceChoice";
import { useFollowDeliveryRuns } from "./followRuns";
import { RemovalDialog, type RemovalSelection } from "./RemovalDialog";
import { DeleteLedgerDialog } from "./DeleteLedgerDialog";
import { OpenUndosCard, UndoUnfinishedButton } from "./DeliveryOpenUndos";
import { UndoneFirst } from "./ArtifactMarks";
import { undoneFirst } from "../../api/artifacts";
import { RedeliverDialog, type RedeliverSelection } from "./RedeliverDialog";
import { AdoptDialog, type AdoptSelection } from "./AdoptDialog";
import { PLANNED_PER_PASS, ReleaseDialog } from "./ReleaseDialog";
import { failureText } from "./answers";
import { isTerminalTask, taskResultJson, useComputeTask } from "./useComputeTask";
import { shortId } from "./idTail";

const ALL = "all";

/** The flow id a card that adds several ledgers up carries: no one ledger, so no record of it can be linked to. */
const EMPTY_ID = "00000000-0000-0000-0000-000000000000";

/** The URL filters that name records of one ledger, which the view of another interface or partition drops. */
const SCOPED_TO_LEDGER = ["submission", "delivered", "run", "issue"] as const;

/** The file and row a record's newest version came from: the queued version's while work waits, as the lookup names it. */
function originOf(row: DeliveryRecord): RecordOrigin {
  return row.pendingSourceFileName !== null
    ? { fileName: row.pendingSourceFileName, rowNumber: row.pendingSourceRowNumber }
    : { fileName: row.sourceFileName, rowNumber: row.sourceRowNumber };
}

const recordColumns: Column<DeliveryRecord>[] = [
  { id: "status", header: "Status", render: (row) => <RecordStatusBadge status={row.status} /> },
  // The record whole, with the file it came from and its last error under it; everything beside it is compact, so the
  // grid fits its panel, and OSDU comes last. The OSDU version is on the record's OSDU tab: in a row it is sixteen
  // digits that repeat the delivery time.
  {
    id: "label",
    header: "Record",
    fill: true,
    floor: 220,
    render: (row) => <RecordIdentity label={row.label} sourceKey={row.sourceKey} origin={originOf(row)} error={row.lastError} />,
  },
  {
    id: "delivered",
    header: "Delivered",
    render: (row) => <CompactTime value={row.lastDeliveredUtc} caption="Delivered" absent="not yet" className="text-[12px]" />,
  },
  { id: "verify", header: "Verify", render: (row) => <VerifyOutcomeBadge outcome={row.lastVerifyOutcome} /> },
  { id: "attempts", header: "Attempts", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.attemptCount}</span> },
  {
    id: "flags",
    header: "",
    render: (row) => (
      <span className="inline-flex gap-1">
        {row.blocked && <BlockedBadge />}
        {row.hasPendingDocument && <Badge variant="outline">pending</Badge>}
      </span>
    ),
  },
  { id: "target", header: "OSDU", render: (row) => <OsduTarget record={row} /> },
];

/** The flow's stats strip, its submissions, and its searchable records, with the flow-level interventions. */
export function DeliveryFlowPanel({ pipelineId, flowName, section }: { pipelineId: string; flowName: string; section: "overview" | "records" | "submissions" }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [searchParams, setSearchParams] = useSearchParams();
  // A submission or run page links here scoped to the records it touched; clearing the chip widens the list again.
  // `submission` is the records a submission last planned, `delivered` the records it delivered, which stay its own
  // however many submissions touch them afterwards.
  const submissionFilter = searchParams.get("submission");
  const deliveredFilter = searchParams.get("delivered");
  const runFilter = searchParams.get("run");
  // The Issues tab links here to the records one issue keeps blocked.
  const issueFilter = searchParams.get("issue");
  // The chips point at records of the ledger that was showing; another interface's or partition's records are not those.
  const {
    rows, names, many, interfaceName, partitions, partition, headerPartition, outside, unplaced, active, scope, ready, selectInterface,
  } = useInterfaceChoice(pipelineId, SCOPED_TO_LEDGER);
  const [search, setSearch] = useState("");
  const [status, setStatus] = useState<string>(ALL);
  const [drifted, setDrifted] = useState(false);
  const [contains, setContains] = useState(false);
  const [releaseOpen, setReleaseOpen] = useState(false);
  const [deleteLedgerOpen, setDeleteLedgerOpen] = useState(false);
  // Ticked rows survive paging and filter changes because the page owns them, not the table. `allMatching` is the
  // other selection: not a list of keys but the filter itself, resolved when the removal runs.
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [allMatching, setAllMatching] = useState(false);
  const [matched, setMatched] = useState(0);
  const [matchedCapped, setMatchedCapped] = useState(false);
  const [removeOpen, setRemoveOpen] = useState(false);
  const [removalTaskId, setRemovalTaskId] = useState<string | null>(null);
  // The records a redelivery was opened for: the selection, the filtered listing, or every delivered record.
  const [redeliverFor, setRedeliverFor] = useState<RedeliverSelection | null>(null);
  // The records an adoption was opened for: every record held for an id OSDU holds, or the selection.
  const [adoptFor, setAdoptFor] = useState<AdoptSelection | null>(null);
  // The records of the listing's loaded pages an adoption may be asked of, so the selection bar offers Adopt only when the
  // selection holds one.
  const [adoptable, setAdoptable] = useState<ReadonlySet<string>>(new Set());

  const filter = useMemo<DeliveryRecordFilter>(() => ({
    search: search.trim() === "" ? undefined : search.trim(),
    mode: contains ? "contains" : undefined,
    status: status === ALL ? undefined : (status as DeliveryRecordStatus),
    drifted: drifted || undefined,
    submissionId: submissionFilter ?? undefined,
    deliveredBy: deliveredFilter ?? undefined,
    runId: runFilter ?? undefined,
    issue: issueFilter ?? undefined,
  }), [search, contains, status, drifted, submissionFilter, deliveredFilter, runFilter, issueFilter]);

  const clearSelection = useCallback(() => {
    setSelected(new Set());
    setAllMatching(false);
  }, []);

  // A filter change makes the ticked keys mean something different from what the operator sees, and an
  // all-matching selection would silently re-aim at the new filter. Both are dropped.
  const filterKey = JSON.stringify(filter);
  const [shownFilter, setShownFilter] = useState(filterKey);
  if (filterKey !== shownFilter) {
    setShownFilter(filterKey);
    clearSelection();
    setAdoptable(new Set());
  }

  const onPageLoaded = useCallback((rows: DeliveryRecord[], total: number, capped: boolean) => {
    setMatched(total);
    setMatchedCapped(capped);
    const found = rows.filter((row) => row.adoptable === true).map((row) => row.deliveryKey);
    setAdoptable((known) => {
      const next = new Set(known);
      for (const row of rows) {
        next.delete(row.deliveryKey);
      }

      for (const key of found) {
        next.add(key);
      }

      return next.size === known.size && [...next].every((key) => known.has(key)) ? known : next;
    });
  }, []);
  const removal = useComputeTask(removalTaskId);

  // The overview shows the source in the partition in view: its counts are every interface's, with the breakdown below
  // them. The records and submissions views show one interface, because that is what their ledgers are.
  const statsOf = section === "overview" ? null : interfaceName;
  // A run of the flow reaches its counts, submissions and records within seconds of it, not at their next poll.
  useFollowDeliveryRuns(pipelineId, ready);
  const stats = useQuery({
    queryKey: ["delivery", "stats", pipelineId, statsOf, partition],
    queryFn: () => deliveryApi.stats(pipelineId, { interfaceName: statsOf, partition }),
    enabled: ready && (section === "overview" || !many || interfaceName !== null),
    refetchInterval: 10000,
  });
  const submissions = useQuery({
    queryKey: ["delivery", "submissions", pipelineId, interfaceName, partition],
    queryFn: () => deliveryApi.submissions(pipelineId, 100, scope),
    enabled: ready && section !== "records" && (!many || interfaceName !== null),
    refetchInterval: 10000,
  });

  // A removal that finished on a node changed the ledger for every record it touched: refetch the list and the
  // stats once the task settles, so the page shows what it did without a manual reload.
  const finishedRemoval = isTerminalTask(removal.data) ? removal.data!.taskId : null;
  useEffect(() => {
    if (finishedRemoval !== null) {
      void queryClient.invalidateQueries({ queryKey: ["delivery"] });
    }
  }, [finishedRemoval, queryClient]);

  // A probe asks the target's info endpoint under the flow's credentials and answers at once, reachable or not. It is
  // recorded on the audit trail whatever it found (a probe that could not run is a failed entry), so the trail's listings
  // and its flow list are read again once it settles: the trail opened next shows the probe, not what it cached before.
  const probeTarget = useMutation({
    mutationFn: () => deliveryApi.probe(pipelineId, scope),
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: ["delivery", "activities"] });
      void queryClient.invalidateQueries({ queryKey: ["delivery", "activity-flows"] });
    },
  });
  const releaseAll = useMutation({
    mutationFn: (run: boolean) => deliveryApi.releaseFlow(pipelineId, undefined, scope, run),
    onSuccess: (result) => {
      setReleaseOpen(false);
      const released = `Released ${result.released.toLocaleString()} record${result.released === 1 ? "" : "s"}`;
      toast.success(
        result.released === 0
          ? "Nothing was blocked: no record released."
          : result.runId ? `${released}; a deliver run plans them now.` : `${released}; the flow's next run plans them.`,
        result.runId ? { action: { label: "Open run", onClick: () => navigate(`/runs/${result.runId}`) } } : undefined);
      void queryClient.invalidateQueries({ queryKey: ["delivery"] });
    },
    onError: (error) => {
      setReleaseOpen(false);
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
    },
  });

  // A sync reads the records' rows from the ingestion tables and consolidates the ledger with them; it sends nothing to
  // OSDU, so it asks for no confirmation. Its run says what it found; the lists poll, and pick up what it wrote.
  const syncSource = useMutation({
    mutationFn: (request: DeliverySyncRequest | undefined) => deliveryApi.syncFlow(pipelineId, scope, request),
    onSuccess: (accepted, request) => {
      if (request !== undefined) {
        clearSelection();
      }

      toast.success("Timeline sync queued.", { action: { label: "Open run", onClick: () => navigate(`/runs/${accepted.runId}`) } });
    },
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });
  // A flow's sync reads one interface's ledger: the single form's, or the interface picked.
  const canSync = !many || interfaceName !== null;
  const syncTitle = canSync
    ? "Read every record's row from the ingestion table and consolidate the ledger with it: arrivals, changes the ledger never saw (planned by the next run), rows that are gone. Nothing is sent."
    : "Pick an interface: a sync reads one interface's records.";

  // The title bar's partition is not one this flow delivers to: the view says where it does, rather than show another
  // partition's ledger here.
  if (outside !== null || unplaced) {
    return (
      <div className="flex flex-col gap-4" data-testid={`delivery-panel-${section}`}>
        <OutsidePartition flowName={flowName} active={active} partitions={partitions} headerPartition={headerPartition} />
      </div>
    );
  }

  if (stats.isError) {
    return isApiError(stats.error)
      ? <CorrelationError error={stats.error} />
      : <p className="text-[13px] text-destructive">{String(stats.error)}</p>;
  }

  const s = stats.data;
  // What a release of every blocked record reaches: the blocked records, and the held or failed ones nothing moves. An older
  // control plane counts neither, so its held and failed records stand in for them.
  const blocked = s ? releasable(s) : 0;
  const probeResult = probeTarget.data;
  const probeJson = probeResult === undefined ? null : JSON.stringify(probeResult, null, 2);
  const removalJson = isTerminalTask(removal.data) ? taskResultJson(removal.data) : null;
  // What the ledger in view is called wherever the view names it: the flow, its interface, and its partition.
  const ledgerName = ledgerLabel(flowName, scope);
  // The partition the ledgers are kept in, which deleting them asks to be typed back: the one in view, or for a flow whose
  // partition is its header, the one its ledger is kept under. None until the flow has run, or while every partition is in view.
  const ledgerPartition = partition ?? headerPartition;

  return (
    <div className="flex flex-col gap-4" data-testid={`delivery-panel-${section}`}>
      {partitions.length > 1 && partition !== null && (
        <p className="text-[13px] text-muted-foreground" data-testid="delivery-partition-in-view">
          In <span className="font-mono text-foreground">{partition}</span>, the partition picked in the title bar: one of the{" "}
          {partitions.length} this flow delivers to, each keeping its own ledger, so every count and action below is this one&apos;s.
        </p>
      )}
      {partitions.length === 0 && headerPartition !== null && (
        <p className="text-[13px] text-muted-foreground" data-testid="delivery-header-partition">
          Delivers to <span className="font-mono text-foreground">{headerPartition}</span>, the partition its target.headers name.
        </p>
      )}
      {many && (
        <InterfacePicker
          names={names}
          interfaceName={interfaceName}
          onSelect={selectInterface}
          caption={section === "overview"
            ? `of ${names.length} interfaces; the counts below are the whole source`
            : `of ${names.length} interfaces of ${flowName}`}
        />
      )}

      {section === "overview" && (
        <>
          {s === undefined ? (
            <Skeleton className="h-28 w-full rounded-lg" />
          ) : (
            <Card className="gap-3 rounded-lg p-3" data-testid="delivery-overview">
              <FlowStatusBar
                stats={s}
                actions={(
                  <>
                    <Button variant="outline" size="sm" onClick={() => probeTarget.mutate()} disabled={probeTarget.isPending} data-testid="delivery-probe">
                      {probeTarget.isPending ? <Loader2 className="animate-spin" /> : <Radar />}
                      Probe target
                    </Button>
                    <Button variant="outline" size="sm" onClick={() => setReleaseOpen(true)} disabled={blocked === 0} data-testid="delivery-release-all">
                      <Unlock />
                      Release blocked ({blocked})
                    </Button>
                    {(s.adoptable ?? 0) > 0 && (
                      <Button
                        variant="outline"
                        size="sm"
                        onClick={() => setAdoptFor({ kind: "every", count: s.adoptable ?? 0 })}
                        disabled={ledgerPartition === null}
                        title={ledgerPartition === null
                          ? "Pick a partition in the title bar: the records are kept in one, and adopting them names that one."
                          : "Records held because OSDU already holds a record at the id their key makes, written by another system or another instance of the delivery: adopt them all, and the next run takes each over"}
                        data-testid="delivery-adopt-all"
                      >
                        <Link2 />
                        Adopt ({(s.adoptable ?? 0).toLocaleString()})
                      </Button>
                    )}
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => setRedeliverFor({ kind: "all", delivered: s.delivered })}
                      disabled={!canSync || s.delivered === 0}
                      title={canSync
                        ? "Bring every delivered record up to date, sending only what renders differently, or send records again"
                        : "Pick an interface first: a redelivery acts on one ledger"}
                      data-testid="delivery-redeliver-all"
                    >
                      <RefreshCcw />
                      Redeliver
                    </Button>
                    <Button variant="outline" size="sm" onClick={() => syncSource.mutate(undefined)} disabled={!canSync || syncSource.isPending} title={syncTitle} data-testid="delivery-sync-all">
                      <RefreshCw />
                      Sync timelines
                    </Button>
                    <UndoUnfinishedButton pipelineId={pipelineId} flowName={flowName} scope={scope} ready={ready} />
                    <Button
                      variant="outline"
                      size="sm"
                      className="text-destructive hover:text-destructive"
                      onClick={() => setDeleteLedgerOpen(true)}
                      disabled={ledgerPartition === null}
                      title={ledgerPartition === null
                        ? "Pick a partition in the title bar: a ledger is kept in one, and deleting it names that one."
                        : "Remove every record from OSDU (reversible), then delete the whole ledger of every interface; the next run reads every row and delivers each as a new record"}
                      data-testid="delivery-delete-ledger"
                    >
                      <Trash2 />
                      Delete ledger
                    </Button>
                  </>
                )}
              />
              {s.lastSubmission && (
                <div className="flex flex-wrap items-center gap-x-2 gap-y-1 border-t pt-2.5 text-[13px]" data-testid="delivery-last-submission">
                  <span className="font-medium">Last submission</span>
                  <SubmissionStatusBadge status={s.lastSubmission.status} />
                  <RouterLink
                    to={`/delivery/submissions/${s.lastSubmission.submissionId}`}
                    className="font-mono text-[12px] text-primary hover:underline"
                    title={s.lastSubmission.submissionId}
                  >
                    {shortId(s.lastSubmission.submissionId)}
                  </RouterLink>
                  <span className="text-muted-foreground">received <RelativeTime value={s.lastSubmission.receivedUtc} /></span>
                  <SubmissionCounts submission={s.lastSubmission} hideZeros />
                  <RouterLink to="?tab=submissions" className="ml-auto text-[12px] text-primary hover:underline" data-testid="delivery-kpi-submissions">
                    {`All ${s.submissions.toLocaleString()} ${s.submissions === 1 ? "submission" : "submissions"}`}
                  </RouterLink>
                </div>
              )}
            </Card>
          )}
          <OpenUndosCard pipelineId={pipelineId} scope={scope} many={many} onSelectInterface={selectInterface} ready={ready} />
          {many && (
            <Card className="gap-2 overflow-hidden rounded-lg p-0" data-testid="delivery-interfaces">
              <div className="flex flex-wrap items-baseline gap-2 px-3 pt-3 text-[13px]">
                <span className="font-medium">Interfaces</span>
                <span className="text-muted-foreground">
                  in the order a run takes them; every wave runs together, after the waves before it
                </span>
                {rows?.find((row) => row.orderProblem !== null)?.orderProblem && (
                  <span className="text-warning">
                    Order from <span className="font-mono">after:</span> alone: {rows.find((row) => row.orderProblem !== null)!.orderProblem}
                  </span>
                )}
              </div>
              <DataTable
                columns={interfaceColumns}
                rows={rows}
                rowKey={(row) => row.interface ?? row.flowId}
                onRowClick={(row) => row.interface !== null && selectInterface(row.interface)}
                emptyMessage="This source declares no interfaces."
                data-testid="delivery-interfaces-table"
              />
            </Card>
          )}
          {!probeTarget.isIdle && (
            <Card className="gap-2 rounded-lg p-3" data-testid="delivery-probe-result">
              <div className="flex items-center gap-2 text-[13px] font-medium">
                Target probe
                <Badge variant="outline">
                  {probeTarget.isPending ? "probing" : probeResult === undefined ? "failed" : probeResult.reachable ? "reachable" : "unreachable"}
                </Badge>
              </div>
              {probeTarget.isError && <p className="text-[13px] text-destructive">{failureText(probeTarget.error)}</p>}
              {probeJson !== null && (
                <CodeView value={probeJson} language="json" height={180} data-testid="delivery-probe-json" />
              )}
            </Card>
          )}
        </>
      )}

      {section === "records" && (
        <>
          <FilterBar>
            <SearchInput value={search} onChange={setSearch} placeholder="Delivery key, label, source key or OSDU id" label="Search records" testId="delivery-records-search" className="sm:w-96" />
            <Select value={status} onValueChange={setStatus}>
              <SelectTrigger size="sm" className="h-8 w-40" data-testid="delivery-records-status">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ALL}>All statuses</SelectItem>
                {DELIVERY_RECORD_STATUSES.map((value) => <SelectItem key={value} value={value}>{value}</SelectItem>)}
              </SelectContent>
            </Select>
            <Label className="flex items-center gap-2 text-[13px] font-normal">
              <Switch checked={drifted} onCheckedChange={setDrifted} data-testid="delivery-records-drifted" />
              Drifted only
            </Label>
            <Label className="flex items-center gap-2 text-[13px] font-normal">
              <Switch checked={contains} onCheckedChange={setContains} data-testid="delivery-records-contains" />
              Match anywhere
            </Label>
            {submissionFilter && (
              <Button variant="outline" size="sm" className="h-8" onClick={() => setSearchParams((current) => { const next = new URLSearchParams(current); next.delete("submission"); return next; })} data-testid="delivery-records-clear-submission">
                last planned by submission {shortId(submissionFilter)}: clear
              </Button>
            )}
            {deliveredFilter && (
              <Button variant="outline" size="sm" className="h-8" onClick={() => setSearchParams((current) => { const next = new URLSearchParams(current); next.delete("delivered"); return next; })} data-testid="delivery-records-clear-delivered">
                delivered by submission {shortId(deliveredFilter)}: clear
              </Button>
            )}
            {runFilter && (
              <Button variant="outline" size="sm" className="h-8" onClick={() => setSearchParams((current) => { const next = new URLSearchParams(current); next.delete("run"); return next; })} data-testid="delivery-records-clear-run">
                run {shortId(runFilter)}: clear
              </Button>
            )}
            {issueFilter && (
              <Button variant="outline" size="sm" className="h-8" onClick={() => setSearchParams((current) => { const next = new URLSearchParams(current); next.delete("issue"); return next; })} data-testid="delivery-records-clear-issue">
                kept blocked by issue {shortId(issueFilter)}: clear
              </Button>
            )}
          </FilterBar>
          {/* A flow that works in partitions has no records until the partition in view is known. */}
          {!ready ? <Skeleton className="h-40 w-full rounded-lg" /> : (
          <PagedTable
            queryKey={["delivery", "records", pipelineId, interfaceName, partition, search, status, drifted, contains, submissionFilter, deliveredFilter, runFilter, issueFilter]}
            fetchPage={(page, pageSize) => deliveryApi.records(pipelineId, {
              page, pageSize, interface: interfaceName ?? undefined, partition: partition ?? undefined, ...filter,
            })}
            columns={recordColumns}
            rowKey={(row) => row.deliveryKey}
            onRowClick={(row) => navigate(deliveryRecordRoute(row))}
            pollMs={selected.size > 0 || allMatching ? undefined : 10000}
            onPageLoaded={onPageLoaded}
            selection={{ selected, onChange: (next) => { setSelected(next); setAllMatching(false); } }}
            toolbar={(selected.size > 0 || allMatching) && (
              <div className="flex flex-wrap items-center gap-2 border-b border-border bg-accent/40 px-3 py-1.5" data-testid="delivery-selection-bar">
                <span className="text-[13px] font-medium tabular-nums" data-testid="delivery-selection-count">
                  {allMatching
                    ? `All ${matched.toLocaleString()} matching records selected`
                    : `${selected.size.toLocaleString()} selected`}
                </span>
                {!allMatching && matched > selected.size && !matchedCapped && (
                  <Button variant="link" size="sm" className="h-6 px-0 text-[13px]" onClick={() => setAllMatching(true)} data-testid="delivery-select-all-matching">
                    Select all {matched.toLocaleString()} matching
                  </Button>
                )}
                {!allMatching && matchedCapped && (
                  <span className="text-[13px] text-muted-foreground" data-testid="delivery-select-all-capped">
                    More than {matched.toLocaleString()} match; narrow the filter to remove them together
                  </span>
                )}
                <Button variant="link" size="sm" className="h-6 px-0 text-[13px] text-muted-foreground" onClick={clearSelection} data-testid="delivery-clear-selection">
                  Clear
                </Button>
                <Button
                  variant="outline"
                  size="sm"
                  className="ml-auto h-7"
                  onClick={() => setRedeliverFor(redeliverSelectionFor(allMatching, filter, matched, selected, s?.delivered ?? matched))}
                  disabled={!canSync}
                  title="Bring the selected records up to date, sending only what renders differently, or send them again."
                  data-testid="delivery-redeliver-selected"
                >
                  <RefreshCcw />
                  Redeliver
                </Button>
                {(allMatching ? adoptable.size > 0 : [...selected].some((key) => adoptable.has(key))) && (
                  <Button
                    variant="outline"
                    size="sm"
                    className="h-7"
                    onClick={() => setAdoptFor(allMatching ? { kind: "filter", filter, expected: matched } : { kind: "keys", keys: [...selected] })}
                    disabled={!canSync || ledgerPartition === null}
                    title="Adopt the records OSDU already holds at the ids the selected held records' keys make: the next run takes each over. A selected record held for anything else is left as it is."
                    data-testid="delivery-adopt-selected"
                  >
                    <Link2 />
                    Adopt
                  </Button>
                )}
                <Button
                  variant="outline"
                  size="sm"
                  className="h-7"
                  onClick={() => syncSource.mutate(syncRequestFor(allMatching, filter, matched, selected))}
                  disabled={!canSync || syncSource.isPending}
                  title="Read the selected records' rows from the ingestion table and consolidate the ledger with them. Nothing is sent."
                  data-testid="delivery-sync-selected"
                >
                  <RefreshCw />
                  Sync timelines
                </Button>
                <Button
                  variant="destructive-outline"
                  size="sm"
                  className="h-7"
                  onClick={() => setRemoveOpen(true)}
                  title="Remove the selected records from OSDU, and from the ledger if you choose"
                  data-testid="delivery-remove-selected"
                >
                  <Trash2 />
                  Remove
                </Button>
              </div>
            )}
            emptyMessage="No records match. A flow's records appear here once its first submission has been planned."
            data-testid="delivery-records-table"
          />
          )}
          {removalTaskId !== null && (
            <Card className="gap-2 rounded-lg p-3" data-testid="delivery-removal-result">
              <div className="flex items-center gap-2 text-[13px] font-medium">
                Removal
                <Badge variant="outline">{removal.data?.status ?? "queued"}</Badge>
                {removal.data?.claimedByNode && <span className="font-mono text-[11px] text-muted-foreground">{removal.data.claimedByNode}</span>}
              </div>
              {removal.data?.error && <p className="text-[13px] text-destructive">{removal.data.error}</p>}
              {isTerminalTask(removal.data) && <UndoneFirst undone={undoneFirst(removal.data?.result)} testId="delivery-removal-undone" />}
              {removalJson !== null && (
                <CodeView value={removalJson} language="json" height={260} data-testid="delivery-removal-json" />
              )}
            </Card>
          )}
          <RemovalDialog
            open={removeOpen}
            onClose={() => setRemoveOpen(false)}
            pipelineId={pipelineId}
            flowScope={scope}
            flowName={ledgerName}
            selection={selectionFor(allMatching, filter, matched, selected)}
            onPurged={(result) => {
              clearSelection();
              toast.success(result.summary);
              void queryClient.invalidateQueries({ queryKey: ["delivery"] });
            }}
            onQueued={(accepted) => {
              clearSelection();
              setRemovalTaskId(accepted.taskId);
              toast.success(accepted.scope === "previous"
                ? `Restoring the previous version of ${accepted.records.toLocaleString()} record(s): queued on a node.`
                : `Removal of ${accepted.records.toLocaleString()} record(s) queued on a node.`);
            }}
          />
        </>
      )}

      {section === "submissions" && (
        <DataTable
          columns={submissionColumns}
          rows={submissions.data}
          rowKey={(row) => row.submissionId}
          onRowClick={(row) => navigate(`/delivery/submissions/${row.submissionId}`)}
          emptyMessage="No submissions yet. A submission is one plan of this flow over its ingestion tables."
          data-testid="delivery-submissions-table"
        />
      )}

      {/* Beside the release dialog rather than in a tab: the overview opens it for every delivered record, the Records tab for a selection. */}
      {redeliverFor !== null && (
        <RedeliverDialog
          open
          onClose={() => setRedeliverFor(null)}
          pipelineId={pipelineId}
          flowId={s?.flowId && s.flowId !== EMPTY_ID ? s.flowId : null}
          flowScope={scope}
          flowName={ledgerName}
          selection={redeliverFor}
          onDone={(outcome) => {
            clearSelection();
            const what = outcome.mode === "uptodate" ? "brought up to date" : "sent again";
            const asked = redeliverFor.kind === "all" && outcome.marked === 0
              ? `Every delivered record will be ${what}`
              : `${outcome.marked.toLocaleString()} record${outcome.marked === 1 ? "" : "s"} will be ${what}`;
            toast.success(
              outcome.runId ? `${asked} by a deliver run.` : `${asked} by the flow's next run.`,
              outcome.runId ? { action: { label: "Open run", onClick: () => navigate(`/runs/${outcome.runId}`) } } : undefined);
            void queryClient.invalidateQueries({ queryKey: ["delivery"] });
          }}
        />
      )}
      {s !== undefined && ledgerPartition !== null && (
        <DeleteLedgerDialog
          open={deleteLedgerOpen}
          onClose={() => setDeleteLedgerOpen(false)}
          pipelineId={pipelineId}
          flowName={flowName}
          partition={partition}
          word={ledgerPartition}
          stats={s}
          interfaces={many ? rows ?? null : null}
          onQueued={(accepted) => {
            toast.success(
              `Deleting the ledger of ${flowName} in ${accepted.partition}: queued as a run.`,
              { action: { label: "Open run", onClick: () => navigate(`/runs/${accepted.runId}`) } });
            void queryClient.invalidateQueries({ queryKey: ["delivery"] });
          }}
        />
      )}
      {adoptFor !== null && ledgerPartition !== null && (
        <AdoptDialog
          pipelineId={pipelineId}
          // Every record of the source, from the overview, whose counts are the whole source's; a selection is the interface's.
          scope={adoptFor.kind === "every" ? { interfaceName: null, partition } : scope}
          partition={ledgerPartition}
          ledgerName={adoptFor.kind === "every" ? flowName : ledgerName}
          selection={adoptFor}
          onClose={() => setAdoptFor(null)}
          onDone={(result) => {
            setAdoptFor(null);
            clearSelection();
            const asked = `Adoption asked of ${result.asked.toLocaleString()} record${result.asked === 1 ? "" : "s"}`;
            toast.success(
              result.asked === 0
                ? "No record was asked: none of them is held because OSDU already holds a record at its id."
                : result.runId ? `${asked}; a deliver run takes them over now.` : `${asked}; the flow's next run takes them over.`,
              result.runId ? { action: { label: "Open run", onClick: () => navigate(`/runs/${result.runId}`) } } : undefined);
            void queryClient.invalidateQueries({ queryKey: ["delivery"] });
          }}
        />
      )}
      <ReleaseDialog
        open={releaseOpen}
        title="Release blocked records"
        message={`Every blocked record of ${ledgerName} (held, failed, reverted, or removed by a reversal) goes back to delivery, however many. Records that still hold a rendered document are sent as they are; the others are planned again from their rows, ${PLANNED_PER_PASS.toLocaleString()} to a pass, all of them by the next run, which holds a record again if its row still calls for it and sends nothing for one OSDU already holds. To release the records of one issue only, use the Issues tab.`}
        confirmLabel={`Release ${blocked.toLocaleString()}`}
        busy={releaseAll.isPending}
        testId="delivery-release-dialog"
        onConfirm={(run) => releaseAll.mutate(run)}
        onClose={() => setReleaseOpen(false)}
      />
    </div>
  );
}

/**
 * What a sync of the selection reads: the ticked records, every record the filter matches, or, for all matching an empty
 * filter, every record of the interface, which the sync pages itself rather than naming each.
 */
function syncRequestFor(
  allMatching: boolean, filter: DeliveryRecordFilter, matched: number, selected: ReadonlySet<string>,
): DeliverySyncRequest | undefined {
  if (!allMatching) {
    return { keys: [...selected] };
  }

  const narrowed = Object.values(filter).some((value) => value !== undefined);
  return narrowed ? { filter, expected: matched } : undefined;
}

/**
 * The records a redelivery acts on: the ticked ones, the filtered listing with the count it showed, or, for every record
 * matching no filter at all, every record the flow has delivered (the others are left as they are by either way).
 */
function redeliverSelectionFor(
  allMatching: boolean, filter: DeliveryRecordFilter, matched: number, selected: ReadonlySet<string>, delivered: number,
): RedeliverSelection {
  if (!allMatching) {
    return { kind: "keys", keys: [...selected] };
  }

  const narrowed = Object.values(filter).some((value) => value !== undefined);
  return narrowed ? { kind: "filter", filter, expected: matched } : { kind: "all", delivered };
}

/**
 * The selection the removal dialog acts on. Ticked keys travel as keys; "all matching" travels as the filter
 * itself with the count that was shown, so the removal covers records no page ever rendered and the API can
 * refuse it if that count has moved.
 */
function selectionFor(
  allMatching: boolean, filter: DeliveryRecordFilter, matched: number, selected: ReadonlySet<string>,
): RemovalSelection {
  return allMatching ? { kind: "filter", filter, expected: matched } : { kind: "keys", keys: [...selected] };
}

/**
 * The interfaces one waits for, in a width that does not grow with their number: the first, how many more, and every one
 * on hover.
 */
function WaitsFor({ waits }: { waits: DeliveryInterface["waitsFor"] }) {
  if (waits === null || waits.length === 0) {
    return <span className="text-[13px] text-muted-foreground">nothing</span>;
  }

  const names = waits.map((wait) => wait.interface);
  const line = (
    <span className="inline-flex max-w-40 items-center gap-1 text-[13px] text-muted-foreground" data-testid="delivery-interface-waits">
      <span className="min-w-0 truncate">{names[0]}</span>
      {names.length > 1 && <span className="shrink-0 text-[11px]">+{names.length - 1}</span>}
    </span>
  );
  return names.length > 1 ? <RichTooltip body={names.join("\n")} title="Waits for">{line}</RichTooltip> : line;
}

/** The route an interface's records go by, with why it goes by it on hover and a copy of the reason beside it. */
function InterfaceRoute({ route, reason }: { route: string; reason: string | null }) {
  const badge = <Badge variant="outline" className="w-fit font-mono text-[11px]">{route}</Badge>;
  if (reason === null || reason === "") {
    return badge;
  }

  return (
    <span className="inline-flex items-center gap-0.5">
      <RichTooltip body={reason} title="Why this route">
        <span className="inline-flex" data-testid="delivery-interface-route">{badge}</span>
      </RichTooltip>
      <CopyButton iconOnly label="Copy why this route" text={reason} testId="copy-interface-route" />
    </span>
  );
}

/**
 * One line per interface of a source: where its records go, when it runs, and how its ledger stands. The kind takes the
 * width the other columns leave, clipped there from the left with the whole kind on hover and a copy beside it; why the
 * route is the route is on hover too, and every other column is bounded, so the grid fits its panel rather than
 * scrolling sideways.
 */
const interfaceColumns: Column<DeliveryInterface>[] = [
  {
    id: "interface",
    header: "Interface",
    // A width in pixels that steps down as the table narrows: a table sizes an auto column to its content, which a
    // percentage cap does not hold.
    render: (row) => (
      <div className="flex min-w-0 max-w-[240px] flex-col @max-5xl/table:max-w-[128px]">
        <span className="truncate font-medium">{row.interface ?? "(single)"}</span>
        <TruncatedText text={row.mapping} mono maxWidth={1600} className="text-[11px] text-muted-foreground" />
      </div>
    ),
  },
  { id: "wave", header: "Wave", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.wave}</span> },
  { id: "route", header: "Route", render: (row) => <InterfaceRoute route={row.route} reason={row.routeReason} /> },
  {
    id: "kind",
    header: "Kind",
    fill: true,
    floor: 100,
    render: (row) => row.kind === null
      ? <span className="text-muted-foreground">-</span>
      : (
        <span className="flex min-w-0 items-center gap-1">
          <span className="min-w-0 shrink"><KindText kind={row.kind} /></span>
          <CopyButton iconOnly label="Copy the kind" text={row.kind} testId="copy-interface-kind" />
        </span>
      ),
  },
  { id: "waits", header: "Waits for", render: (row) => <WaitsFor waits={row.waitsFor} /> },
  { id: "total", header: "Records", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.stats.total.toLocaleString()}</span> },
  { id: "delivered", header: "Delivered", align: "right", render: (row) => <span className="font-mono tabular-nums text-success">{row.stats.delivered.toLocaleString()}</span> },
  { id: "pending", header: "Pending", align: "right", render: (row) => <span className="font-mono tabular-nums">{(row.stats.pending + row.stats.delivering).toLocaleString()}</span> },
  { id: "waiting", header: "Waiting", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.stats.waiting.toLocaleString()}</span> },
  {
    id: "blocked",
    header: "Blocked",
    align: "right",
    render: (row) => {
      const blocked = releasable(row.stats);
      return <span className={`font-mono tabular-nums${blocked > 0 ? " text-warning" : ""}`}>{blocked.toLocaleString()}</span>;
    },
  },
];

const submissionColumns: Column<DeliverySubmission>[] = [
  { id: "status", header: "Status", render: (row) => <SubmissionStatusBadge status={row.status} /> },
  {
    id: "id",
    header: "Submission",
    // The sending system's own name for the work sits above this ledger's id, because it is what an operator holding a
    // filename or a ticket recognises; the id stays, because it is what every other page is keyed by.
    render: (row) => (
      <div className="flex min-w-0 flex-col">
        <span className="font-mono text-[12px] text-muted-foreground">{row.submissionId}</span>
      </div>
    ),
  },
  { id: "kind", header: "Read", render: (row) => <Badge variant="outline" className="font-mono text-[11px]">{row.kind}</Badge> },
  { id: "received", header: "Received", render: (row) => <RelativeTime value={row.receivedUtc} /> },
  { id: "records", header: "Records", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.recordCount}</span> },
  { id: "planned", header: "Planned", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.planned}</span> },
  { id: "delivered", header: "Delivered", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.delivered}</span> },
  { id: "unchanged", header: "Unchanged", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.skippedUnchanged + row.unchangedAtPush}</span> },
  { id: "stale", header: "Stale", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.skippedStale}</span> },
  { id: "held", header: "Held", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.held}</span> },
  { id: "failed", header: "Failed", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.failed}</span> },
  { id: "waiting", header: "Waiting", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.waiting}</span> },
  { id: "error", header: "Error", render: (row) => <TruncatedText text={row.error} maxWidth={280} /> },
];

/**
 * The counts of one submission as one compact line. `hideZeros` keeps only the counts that hold something, for a line
 * that sits beside other facts: a submission that planned nothing then says so in two words instead of ten zeros.
 */
export function SubmissionCounts({ submission, hideZeros = false }: { submission: DeliverySubmission; hideZeros?: boolean }) {
  const parts: { label: string; value: number; className?: string }[] = [
    { label: "records", value: submission.recordCount },
    { label: "planned", value: submission.planned },
    { label: "delivered", value: submission.delivered, className: "text-success" },
    { label: "unchanged", value: submission.skippedUnchanged + submission.unchangedAtPush },
    { label: "awaiting approval", value: submission.awaitingApproval, className: submission.awaitingApproval > 0 ? "text-warning" : undefined },
    { label: "stale", value: submission.skippedStale },
    { label: "blocked", value: submission.blocked },
    { label: "held", value: submission.held, className: submission.held > 0 ? "text-warning" : undefined },
    { label: "failed", value: submission.failed, className: submission.failed > 0 ? "text-destructive" : undefined },
    { label: "waiting", value: submission.waiting, className: submission.waiting > 0 ? "text-info" : undefined },
  ];
  const shown = hideZeros ? parts.filter((part) => part.value > 0) : parts;
  if (shown.length === 0) {
    return <span className="text-[13px] text-muted-foreground" data-testid="submission-counts">nothing changed</span>;
  }

  return (
    <div className="flex flex-wrap gap-x-3 gap-y-1 text-[13px] text-muted-foreground" data-testid="submission-counts">
      {shown.map((part) => (
        <span key={part.label} className={part.className}>
          <span className="font-mono tabular-nums">{part.value.toLocaleString()}</span> {part.label}
        </span>
      ))}
    </div>
  );
}

/**
 * What a release of every blocked record reaches: the blocked records, and the held or failed ones nothing moves. A control
 * plane older than those counts gives neither, and its held and failed records stand in for them.
 */
function releasable(stats: Pick<DeliveryFlowStats, "held" | "failed" | "blocked" | "stranded">): number {
  return stats.blocked === undefined ? stats.held + stats.failed : stats.blocked + (stats.stranded ?? 0);
}
