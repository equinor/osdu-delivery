import { useMemo, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Badge } from "@/components/ui/badge";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { cn } from "@/lib/utils";
import { deliveryApi, type DeliveryActivity } from "../../api/delivery";
import { FilterBar } from "@/components/FilterBar";
import { FilterCombobox, type FilterOption } from "@/components/FilterCombobox";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { PagedTable, type Column } from "@/components/PagedTable";
import { RelativeTime } from "@/components/RelativeTime";
import { RunDuration } from "@/components/RunDuration";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import { useActivePartition } from "./activePartition";
import { useLinkedPartition } from "./useLinkedPartition";
import { Actor } from "./ActivityActor";
import { endingOf, TONE_TEXT } from "./activityEnding";
import { DeliveryActivitySheet } from "./DeliveryActivitySheet";
import { RecordRef, RunRef, SubmissionRef } from "./DeliveryRefs";
import { NoFact } from "./Facts";
import { ledgerFlowLabel, ledgerFlowOptions } from "./ledgerFlowOptions";

const ALL = "all";
const KINDS = ["deliver", "intake", "drain", "verify", "probe", "sync", "release", "adopt", "redeliver", "rerender", "delete", "restore-previous", "purge", "reverse", "undo", "delete-ledger", "remove-dimension", "inventory-remove", "inventory-repair", "reassign-id", "source-remove", "lift-removal", "ledger-move"];
const OUTCOMES = ["running", "completed", "failed", "cancelled"];

/** What the idle-runs switch says on hover: which runs it shows, and why they are left out otherwise. */
const IDLE_HINT = "Runs that completed having changed nothing: a schedule fired and found nothing new to plan, send or hold. "
  + "They stay in the ledger as the proof that it fired; the trail leaves them out unless you show them.";

/**
 * How the ending and the summary share one cell: the icon says how it ended, the text what it did or why it failed. An
 * idle run says only that it changed nothing, whatever its summary lists, and keeps the summary on hover: a run recorded
 * before summaries left out their zeros would otherwise fill the cell with them.
 */
function Result({ row }: { row: DeliveryActivity }) {
  const ending = endingOf(row);
  const Icon = ending.icon;
  return (
    <span className="flex min-w-0 items-center gap-1.5">
      <span className="inline-flex shrink-0" title={ending.label}>
        <Icon className={cn("size-3.5", TONE_TEXT[ending.tone], ending.spin && "animate-spin")} aria-hidden />
        <span className="sr-only">{ending.label}</span>
      </span>
      {row.idle
        ? <span className="truncate text-muted-foreground" title={row.summary ?? undefined}>changed nothing</span>
        : (
          <span className="min-w-0 flex-1">
            <TruncatedText
              text={row.summary ?? ending.label.toLowerCase()}
              maxWidth={1600}
              className={cn(row.outcome === "failed" && "text-destructive")}
            />
          </span>
        )}
    </span>
  );
}

/**
 * What an activity worked on, the most particular first: the record it acted on, else the submission it delivered under,
 * else the run it was. The detail names all three in full.
 */
function Target({ row }: { row: DeliveryActivity }) {
  if (row.deliveryKey !== null) {
    return <RecordRef flowId={row.flowId} deliveryKey={row.deliveryKey} />;
  }

  if (row.submissionId !== null) {
    return <SubmissionRef submissionId={row.submissionId} />;
  }

  return row.runId !== null ? <RunRef runId={row.runId} /> : <NoFact />;
}

/**
 * The audit trail across every delivery flow in the workbench's partition: who did what, when, and how it ended. The runs
 * that changed nothing are left out unless asked for, and counted. Each entry opens with the ids it worked on, its
 * recorded parameters and, for runs, the captured log.
 *
 * The flow follows the actor search, and every other filter works within it. It is kept in the address (`?flow=`, a
 * ledger identity) rather than with the other filters, so a link can open one flow's trail and the trail opened from the
 * menu is every flow's again; a link that names the ledger's partition (`?partition=`) opens it in that one. The choices are
 * the flows with activity in the partition: delivery flows, each interface of a source, and dimensions.
 */
export default function DeliveryActivityPage() {
  useLinkedPartition();
  const [active] = useActivePartition();
  const [searchParams, setSearchParams] = useSearchParams();
  const flow = searchParams.get("flow") ?? "";
  const [actor, setActor] = useLocalStorageState("sqlflow.filters.delivery-activity.actor", "");
  const [kind, setKind] = useLocalStorageState("sqlflow.filters.delivery-activity.kind", ALL);
  const [outcome, setOutcome] = useLocalStorageState("sqlflow.filters.delivery-activity.outcome", ALL);
  const [showIdle, setShowIdle] = useLocalStorageState<boolean>("sqlflow.filters.delivery-activity.idle", false);
  const [selected, setSelected] = useState<number | null>(null);

  const flows = useQuery({ queryKey: ["delivery", "activity-flows", active], queryFn: () => deliveryApi.activityFlows(active) });
  const flowOptions = useMemo<FilterOption[]>(
    () => ledgerFlowOptions(
      flows.data?.map((f) => ({ ...f, hint: f.kind === "delivery" ? undefined : f.kind })),
      active,
      flow,
      flows.isSuccess,
      `${flow}: no activity${active === null ? "" : ` in ${active}`}`,
    ),
    [flows.data, flows.isSuccess, flow, active],
  );
  const chosen = flows.data?.find((f) => f.flowId === flow);
  const selectFlow = (next: string) => setSearchParams((current) => {
    const params = new URLSearchParams(current);
    if (next === "") {
      params.delete("flow");
    } else {
      params.set("flow", next);
    }

    return params;
  }, { replace: true });

  const filters = {
    partition: active ?? undefined,
    flowId: flow === "" ? undefined : flow,
    actor: actor.trim() === "" ? undefined : actor.trim(),
    kind: kind === ALL ? undefined : kind,
    outcome: outcome === ALL ? undefined : outcome,
  };

  // How many idle runs match the other filters: what the switch says it shows, or leaves out.
  const idle = useQuery({
    queryKey: ["delivery", "activities", "idle", flow, actor, kind, outcome, active],
    queryFn: () => deliveryApi.activities({ ...filters, idle: true, page: 1, pageSize: 1 }),
    refetchInterval: 10000,
  });
  const idleCount = idle.data?.total ?? 0;

  const everyColumn: Column<DeliveryActivity>[] = [
    { id: "started", header: "When", render: (row) => <RelativeTime value={row.startedUtc} absolute /> },
    {
      id: "duration",
      header: "Took",
      render: (row) => (
        <RunDuration
          durationSeconds={null}
          startUtc={row.startedUtc}
          endUtc={row.completedUtc}
          live={row.outcome === "running"}
        />
      ),
    },
    // The page never scrolls sideways: the flow keeps to a width that steps down as the table narrows, the result takes
    // what the other columns leave, and both clip with the whole value on hover.
    {
      id: "flow",
      header: "Flow",
      render: (row) => (
        <span className="flex items-center gap-1.5">
          {/* Every row is the workbench partition's when one is picked; with none, each says which it is. */}
          {active === null && row.partition && (
            <Badge variant="secondary" className="shrink-0 font-mono text-[10px]" title="The OSDU partition this activity's ledger is kept under">{row.partition}</Badge>
          )}
          <span className="block max-w-[260px] @max-4xl/table:max-w-[150px] @max-3xl/table:max-w-[110px]">
            <TruncatedText text={row.flowName} mono maxWidth={1600} className="font-medium" />
          </span>
        </span>
      ),
    },
    { id: "kind", header: "Action", render: (row) => row.kind },
    { id: "actor", header: "By", render: (row) => <Actor actor={row.actor} /> },
    { id: "result", header: "Result", fill: true, floor: 120, render: (row) => <Result row={row} /> },
    { id: "target", header: "Target", render: (row) => <Target row={row} /> },
  ];
  // One flow's trail names it once, in the filter and the subtitle, and gives its column's width to the result.
  const columns = flow === "" ? everyColumn : everyColumn.filter((column) => column.id !== "flow");

  const emptyMessage = !showIdle && idleCount > 0
    ? `Only idle runs match: ${idleCount} run(s) that changed nothing. Show idle runs to see them.`
    : flow === ""
      ? "No delivery activity recorded yet."
      : "No activity of this flow matches.";
  const scope = flow === "" ? "every delivery flow" : chosen === undefined ? "the chosen flow" : ledgerFlowLabel(chosen, active);

  return (
    <Page data-testid="page-delivery-activity">
      <PageHeader
        title="Delivery audit trail"
        subtitle={`Every run and intervention on ${scope}${active === null ? "" : ` in ${active}`}, newest first.`}
      />
      <FilterBar>
        <SearchInput value={actor} onChange={setActor} placeholder="Actor, e.g. admin or schedule:" label="Filter by actor" testId="delivery-activity-actor" className="sm:w-64" />
        <FilterCombobox
          options={flowOptions}
          value={flow}
          onChange={selectFlow}
          placeholder="All flows"
          searchPlaceholder="Search flows"
          emptyText="No flow matches."
          ariaLabel="Filter by flow"
          testId="delivery-activity-flow"
          className="w-64"
        />
        <Select value={kind} onValueChange={setKind}>
          <SelectTrigger size="sm" className="h-8 w-40" data-testid="delivery-activity-kind"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>All actions</SelectItem>
            {KINDS.map((value) => <SelectItem key={value} value={value}>{value}</SelectItem>)}
          </SelectContent>
        </Select>
        <Select value={outcome} onValueChange={setOutcome}>
          <SelectTrigger size="sm" className="h-8 w-40" data-testid="delivery-activity-outcome"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>All outcomes</SelectItem>
            {OUTCOMES.map((value) => <SelectItem key={value} value={value}>{value}</SelectItem>)}
          </SelectContent>
        </Select>
        <Tooltip>
          <TooltipTrigger asChild>
            <Label className="flex items-center gap-2 text-[13px] font-normal">
              <Switch checked={showIdle} onCheckedChange={setShowIdle} data-testid="delivery-activity-idle" />
              Show idle runs
              <span className="font-mono text-[12px] tabular-nums text-muted-foreground" data-testid="delivery-activity-idle-count">
                {idle.data === undefined ? "" : idleCount}
              </span>
            </Label>
          </TooltipTrigger>
          <TooltipContent className="max-w-80">{IDLE_HINT}</TooltipContent>
        </Tooltip>
      </FilterBar>
      <PagedTable
        queryKey={["delivery", "activities", flow, actor, kind, outcome, active, showIdle]}
        fetchPage={(page, pageSize) => deliveryApi.activities({ ...filters, idle: showIdle ? undefined : false, page, pageSize })}
        columns={columns}
        rowKey={(row) => row.activityId}
        onRowClick={(row) => setSelected(row.activityId)}
        rowSx={(row) => (row.idle ? { opacity: 0.7 } : undefined)}
        pollMs={10000}
        emptyMessage={emptyMessage}
        data-testid="delivery-activity-table"
      />

      <DeliveryActivitySheet activityId={selected} onClose={() => setSelected(null)} />
    </Page>
  );
}
