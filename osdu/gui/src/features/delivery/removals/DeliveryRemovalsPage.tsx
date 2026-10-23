import { useMemo, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ChevronRight, ChevronsUpDown, CircleAlert, ListX, Play, ShieldAlert } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { Skeleton } from "@/components/ui/skeleton";
import { DataTable, type Column } from "@/components/DataTable";
import { EmptyState } from "@/components/EmptyState";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { TruncatedText } from "@/components/TruncatedText";
import { TriggerRunDialog } from "@/features/runs/TriggerRunDialog";
import { cn } from "@/lib/utils";
import { REMOVED_KEY_STATES, removalApi, type RemovalEntry, type RemovalFlow, type RemovedKeyState } from "../../../api/removals";
import { useActivePartition } from "../activePartition";
import { ProblemView } from "../TemplateSheet";
import { DeletionCounts, RemovalRunPill } from "./RemovalBadges";
import { RemovalEntryView } from "./RemovalEntryView";
import type { RemovalShow } from "./RemovalViewSwitch";
import { ledgerName, removalRoute } from "./removalFormat";

/** How often the removals are read again, so a run under way shows what it found as it lands. */
const REFRESH_MS = 15000;

/** One removal of the partition, with the flow that declares it. */
interface Listed {
  flow: RemovalFlow;
  entry: RemovalEntry;
}

/** The way back to every removal, as the first words of one removal's heading. */
function RemovalsCrumb({ onBack }: { onBack: () => void }) {
  return (
    <>
      <button
        type="button"
        onClick={onBack}
        className="rounded-sm font-normal text-muted-foreground outline-none hover:text-foreground hover:underline focus-visible:underline"
        data-testid="removals-back"
      >
        Removals
      </button>
      <ChevronRight className="size-4 shrink-0 text-muted-foreground/60" />
    </>
  );
}

/** The other removals of the partition, one click away from the one in view. */
function Switcher({ current, listed, onOpen }: { current: Listed; listed: readonly Listed[]; onOpen: (item: Listed) => void }) {
  if (listed.length < 2) {
    return null;
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="icon" className="size-6 text-muted-foreground" aria-label="Open another removal" data-testid="removal-switcher">
          <ChevronsUpDown />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="max-h-80 min-w-80 overflow-y-auto">
        {listed.map((item) => (
          <DropdownMenuItem
            key={`${item.flow.pipelineId}:${item.entry.name}`}
            onSelect={() => onOpen(item)}
            className={cn("gap-2", item.flow.pipelineId === current.flow.pipelineId && item.entry.name === current.entry.name && "bg-accent/60")}
            data-testid="removal-switcher-item"
          >
            {item.entry.awaitingApproval > 0
              ? <ShieldAlert className="size-3.5 shrink-0 text-warning" aria-label="waiting for approval" />
              : <ListX className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />}
            <span className="min-w-0 flex-1 truncate">{item.entry.name}</span>
            <span className="shrink-0 truncate font-mono text-[11px] text-muted-foreground">{item.flow.name}</span>
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

function stateOf(value: string | null): RemovedKeyState | null {
  return REMOVED_KEY_STATES.find((state) => state === value) ?? null;
}

/**
 * The removals of the partition picked in the title bar (osdu/docs/reference/flow/removal.md): every removal every removal
 * flow declares, each with the delivery flow whose ledger it writes to, that ledger's deletions by state, its last run and
 * what waits for approval. The page is two views of one address, as the Inventories page is: every removal, and one removal,
 * whose deletions (or runs) fill the window under a compact heading and scroll inside it, thousands read a page at a time.
 * The removal, the view and the state live in the address, so a link lands on the same view.
 */
export default function DeliveryRemovalsPage() {
  const [params, setParams] = useSearchParams();
  const navigate = useNavigate();
  const [active] = useActivePartition();
  const [launching, setLaunching] = useState(false);
  const list = useQuery({
    queryKey: ["delivery", "removals", "list", active],
    queryFn: () => removalApi.list(),
    refetchInterval: REFRESH_MS,
  });
  const listed = useMemo<Listed[]>(
    () => (list.data ?? []).flatMap((flow) => flow.removals.map((entry) => ({ flow, entry }))),
    [list.data]);
  const unreadable = (list.data ?? []).filter((flow) => flow.problem !== undefined);
  const askedFlow = params.get("flow");
  const askedRemoval = params.get("removal");
  const current = askedFlow === null
    ? undefined
    : listed.find((item) => item.flow.pipelineId === askedFlow && (askedRemoval === null || item.entry.name === askedRemoval))
      ?? listed.find((item) => item.flow.pipelineId === askedFlow);
  const view = params.get("view") === "runs" ? "runs" : "deletions";
  const state = stateOf(params.get("state"));

  const show = (change: RemovalShow) => setParams((was) => {
    const next = new URLSearchParams(was);
    if (change.view !== undefined) {
      if (change.view === "deletions") {
        next.delete("view");
      } else {
        next.set("view", change.view);
      }
    }

    if (change.state !== undefined) {
      if (change.state === null) {
        next.delete("state");
      } else {
        next.set("state", change.state);
      }
    }

    return next;
  }, { replace: true });
  const open = (item: Listed | null) => navigate(item === null ? "/delivery/removals" : removalRoute(item.flow.pipelineId, item.entry.name));

  if (list.isError) {
    return (
      <Page data-testid="page-delivery-removals">
        <PageHeader title="Removals" />
        <ProblemView error={list.error} testId="removals-error" />
      </Page>
    );
  }

  if (list.data === undefined) {
    return (
      <Page data-testid="page-delivery-removals">
        <PageHeader title="Removals" />
        <Skeleton className="h-96 w-full rounded-lg" />
      </Page>
    );
  }

  if (askedFlow !== null && current !== undefined) {
    const { flow, entry } = current;
    return (
      <Page data-testid="page-delivery-removals">
        <PageHeader
          title={(
            <span className="flex min-w-0 items-center gap-x-1.5">
              <RemovalsCrumb onBack={() => open(null)} />
              <span className="min-w-0 truncate" data-testid="removal-name">{entry.name}</span>
              <Switcher current={current} listed={listed} onOpen={open} />
            </span>
          )}
          subtitle={(
            <span className="flex min-w-0 items-center gap-x-1.5" data-testid="removal-flow">
              <span>of</span>
              <button type="button" className="min-w-0 truncate font-mono text-[12.5px] hover:text-foreground hover:underline" onClick={() => navigate(`/pipelines/${flow.pipelineId}`)}>
                {flow.name}
              </button>
              {flow.partition !== undefined && <span>{`in ${flow.partition}`}</span>}
            </span>
          )}
          actions={(
            <RichTooltip body={`Runs ${flow.name} with this removal picked: it reads the source's keys whole, compares them with the ledger, and takes out of OSDU what it has not taken out before. The dialog can pick a plan, which writes nothing.`}>
              <Button size="sm" onClick={() => setLaunching(true)} data-testid="removal-run">
                <Play />
                Run pipeline
              </Button>
            </RichTooltip>
          )}
        />
        <RemovalEntryView key={`${flow.pipelineId}:${entry.name}`} flow={flow} entry={entry} view={view} state={state} onShow={show} omitName />
        {launching && (
          <TriggerRunDialog
            open
            onClose={() => setLaunching(false)}
            repoId={flow.repoId}
            flowName={flow.name}
            flowId={flow.pipelineId}
            flowKind="removal"
            initialOperation="remove"
            initialValues={null}
            initialPayload={{ entries: [entry.name] }}
          />
        )}
      </Page>
    );
  }

  const held = listed.reduce((sum, item) => sum + item.entry.awaitingApproval, 0);
  const columns: Column<Listed>[] = [
    {
      id: "removal",
      header: "Removal",
      render: (row) => (
        <span className="flex items-start gap-2">
          <span className="mt-0.5">
            {row.entry.awaitingApproval > 0
              ? (
                <RichTooltip title="Waiting for approval" body={`${row.entry.awaitingApproval.toLocaleString("en-US")} removed keys the guard held wait for an operator's approval.`}>
                  <ShieldAlert className="size-4 text-warning" aria-label="waiting for approval" />
                </RichTooltip>
              )
              : <ListX className="size-4 text-muted-foreground" aria-hidden />}
          </span>
          <span className="flex min-w-0 flex-col">
            <span className="font-medium">{row.entry.name}</span>
            <TruncatedText text={row.flow.name} mono maxWidth={240} className="text-[11.5px] text-muted-foreground" />
          </span>
        </span>
      ),
    },
    { id: "flow", header: "Delivery flow", fill: true, floor: 140, render: (row) => <span className="min-w-0 truncate font-mono text-[12px]">{ledgerName(row.entry)}</span> },
    {
      id: "deletions",
      header: "Deletions",
      render: (row) => (row.entry.ledgerFlowId === undefined
        ? <span className="text-[12px] text-muted-foreground">no ledger</span>
        : <DeletionCounts counts={row.entry.deletions} testId={`removals-counts-${row.entry.name}`} />),
    },
    {
      id: "last",
      header: "Last run",
      render: (row) => (row.entry.lastRun === undefined
        ? <span className="text-[12px] text-muted-foreground">never run</span>
        : (
          <span className="flex items-center gap-2 whitespace-nowrap">
            <RemovalRunPill status={row.entry.lastRun.status} error={row.entry.lastRun.error} testId={`removals-last-${row.entry.name}`} />
            <span className="text-[12px] text-muted-foreground"><RelativeTime value={row.entry.lastRun.startedUtc} absolute={false} /></span>
          </span>
        )),
    },
  ];

  return (
    <Page data-testid="page-delivery-removals">
      <PageHeader
        title="Removals"
        subtitle={listed.length === 0
          ? `No removal${active === null ? "" : ` in ${active}`} yet.`
          : `${listed.length.toLocaleString("en-US")} ${listed.length === 1 ? "removal" : "removals"}${active === null ? "" : ` in ${active}`}${held === 0 ? "" : `, ${held.toLocaleString("en-US")} removed keys waiting for approval`}.`}
      />
      {unreadable.map((flow) => (
        <Alert key={flow.pipelineId} variant="destructive" data-testid="removals-unreadable">
          <CircleAlert />
          <AlertDescription>{`${flow.name}: ${flow.problem}`}</AlertDescription>
        </Alert>
      ))}
      {listed.length === 0
        ? (
          <Card className="gap-0 rounded-lg p-0">
            <EmptyState
              icon={<ListX />}
              title="No removal flow removes here"
              description="A removal flow (flowType: removal) reads the keys a source holds now, and the keys it lists as removed, compares them with a delivery flow's ledger, and takes out of OSDU the records the source no longer holds."
              data-testid="removals-empty"
            />
          </Card>
        )
        : (
          <DataTable
            columns={columns}
            rows={listed}
            rowKey={(row) => `${row.flow.pipelineId}:${row.entry.name}`}
            onRowClick={(row) => open(row)}
            emptyMessage="No removal."
            data-testid="removals-table"
          />
        )}
    </Page>
  );
}
