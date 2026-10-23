import { useMemo, useState, type ReactNode } from "react";
import { Link, useNavigate } from "react-router-dom";
import { keepPreviousData, useInfiniteQuery, useQueryClient } from "@tanstack/react-query";
import { RotateCcw } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { DataTable, type Column } from "@/components/DataTable";
import { ExplainTip } from "@/components/ExplainTip";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { TruncatedText } from "@/components/TruncatedText";
import { cn } from "@/lib/utils";
import { deliveryRecordRoute, type DeliveryFlowScope } from "../../../api/delivery";
import { MAX_LIFT, REMOVED_KEY_STATES, removalApi, type RemovedKey, type RemovedKeyState } from "../../../api/removals";
import { ProblemView } from "../TemplateSheet";
import { DimensionGrid, GridFooter, GridViewMenu } from "../dimensions/DimensionGrid";
import { useHiddenColumns, type GridColumnChoice } from "../dimensions/dimensionGridState";
import { OutcomeMark, RemovalStateStrip, StateGlyph } from "./RemovalBadges";
import { LiftDialog } from "./RemovalDialogs";
import { instant, keyText, outcomeVisual, reasonVisual, removalWhy, stateVisual } from "./removalFormat";

const PAGE = 200;

/** The columns the grid starts without. */
const AT_FIRST: readonly string[] = ["by", "done", "record"];

/** Where an operator may deliver the picked records again: the delivery flow's pipeline and the ledger the deletions are of. */
export interface LiftTarget {
  pipelineId: string;
  scope?: DeliveryFlowScope;
  /** The ledger as the page names it. */
  flowName: string;
}

/** The key, as its values, which opens its record when the ledger holds one; the delivery key on hover. */
function KeyCell({ row, ledgerFlowId, columns }: { row: RemovedKey; ledgerFlowId: string; columns?: readonly string[] }) {
  const text = keyText(row, columns);
  const body = `Delivery key ${row.deliveryKey}${row.recordStatus === undefined ? "\nThe ledger holds no record of it: only the record table held the key, and no run delivered it." : ""}`;
  return (
    <RichTooltip title={text} body={body} mono>
      {row.recordStatus === undefined
        ? <span className="min-w-0 truncate font-mono text-[12px]" data-testid="removed-key-text">{text}</span>
        : (
          <Link
            to={deliveryRecordRoute({ flowId: ledgerFlowId, deliveryKey: row.deliveryKey })}
            onClick={(event) => event.stopPropagation()}
            className="min-w-0 truncate font-mono text-[12px] text-primary hover:underline"
            data-testid="removed-key-link"
          >
            {text}
          </Link>
        )}
    </RichTooltip>
  );
}

/**
 * A delivery ledger's deletions (osdu/docs/reference/flow/removal.md, The ledger's deletions) as a grid that fits the window
 * and scrolls inside the page: the states to pick from lead its toolbar, each with its count, and more deletions are read as it
 * is scrolled, a page at a time after the last key read, so a ledger with hundreds of thousands of them reads as quickly as
 * one with ten. Each key is named by its values (its delivery key on hover), opening its record when the ledger holds one;
 * why it counts as removed, where that was found, and what taking it out came to are on hover of its reason and outcome, to
 * copy. Columns are left out under View. Where the reader may, deletions are picked, or every one loaded, and their records
 * delivered again through the lift dialog.
 */
export function RemovedKeysGrid({ ledgerFlowId, state, onState, keyColumns, lift, leading, trailing, testId = "removed-keys" }: {
  ledgerFlowId: string;
  /** The state in view; null for every deletion. */
  state: RemovedKeyState | null;
  onState: (state: RemovedKeyState | null) => void;
  /** The record key's columns, which name the values of each key. */
  keyColumns?: readonly string[];
  /** Where picked records are delivered again; absent where the reader cannot. */
  lift?: LiftTarget;
  /** What leads the toolbar, before the states. */
  leading?: ReactNode;
  /** What the toolbar holds before View at its end. */
  trailing?: ReactNode;
  testId?: string;
}) {
  const [hidden, toggleColumn] = useHiddenColumns("osdu.removals.keys.columns", AT_FIRST);
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const pages = useInfiniteQuery({
    queryKey: ["delivery", "removals", "keys", ledgerFlowId, state],
    queryFn: ({ pageParam }) => removalApi.keys(ledgerFlowId, { state: state ?? undefined, after: pageParam, limit: PAGE }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.next,
    placeholderData: keepPreviousData,
  });
  const rows = useMemo(() => pages.data?.pages.flatMap((page) => page.keys), [pages.data]);
  const counts = pages.data?.pages[0]?.counts ?? {};
  const total = state === null ? REMOVED_KEY_STATES.reduce((sum, s) => sum + (counts[s] ?? 0), 0) : counts[state] ?? 0;
  const more = () => {
    if (pages.hasNextPage && !pages.isFetchingNextPage) {
      void pages.fetchNextPage();
    }
  };

  // What is picked belongs to the state it was picked in: another state starts with nothing picked, so nothing out of sight
  // is ever delivered again.
  const [picked, setPicked] = useState<ReadonlySet<string>>(() => new Set());
  const [pickedIn, setPickedIn] = useState(state);
  const [lifting, setLifting] = useState(false);
  if (pickedIn !== state) {
    setPickedIn(state);
    setPicked(new Set());
  }

  const pickedKeys = (rows ?? []).filter((row) => picked.has(row.deliveryKey)).map((row) => row.deliveryKey);
  const tooMany = pickedKeys.length > MAX_LIFT;

  const choices: GridColumnChoice[] = [
    { id: "outcome", label: "Outcome" },
    { id: "record", label: "Record" },
    { id: "found", label: "Found" },
    { id: "done", label: "Done" },
    { id: "by", label: "Found by" },
  ];
  const all: Column<RemovedKey>[] = [
    {
      id: "key",
      header: "Key",
      fill: true,
      floor: 220,
      render: (row) => <KeyCell row={row} ledgerFlowId={ledgerFlowId} columns={keyColumns} />,
    },
    {
      id: "state",
      header: "State",
      render: (row) => (
        <RichTooltip title={stateVisual(row.state).label} body={stateVisual(row.state).hint}>
          <span className="inline-flex items-center gap-1 whitespace-nowrap text-[12px]" data-testid="removed-key-state" data-state={row.state}>
            <StateGlyph state={row.state} />
            {row.state === "awaiting-approval" ? "held" : stateVisual(row.state).label.toLowerCase()}
          </span>
        </RichTooltip>
      ),
    },
    {
      id: "why",
      header: "Why",
      render: (row) => {
        const visual = reasonVisual(row.reason);
        const Icon = visual.icon;
        return (
          <ExplainTip title={`Why ${row.reason === "listed" ? "listed" : "absent"}`} text={removalWhy(row)} testId="removed-key-why">
            <button
              type="button"
              className="inline-flex items-center gap-1 whitespace-nowrap rounded-sm text-[12px] outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
              data-testid="removed-key-why"
              data-reason={row.reason}
            >
              <Icon className={cn("size-3.5 shrink-0", visual.tone)} aria-hidden />
              <span>{row.reason === "listed" ? "listed" : "absent"}</span>
              <span className="text-muted-foreground"><RelativeTime value={row.removedUtc} absolute={false} /></span>
            </button>
          </ExplainTip>
        );
      },
    },
    {
      id: "outcome",
      header: "Outcome",
      render: (row) => (row.outcome === undefined
        ? <span className="text-[12px] text-muted-foreground">{row.state === "awaiting-approval" ? "held" : "not tried"}</span>
        : (
          <RichTooltip
            title={`${outcomeVisual(row.outcome).label}, ${row.tries} ${row.tries === 1 ? "try" : "tries"}`}
            body={row.detail === undefined ? outcomeVisual(row.outcome).hint : `${outcomeVisual(row.outcome).hint}\n\n${row.detail}`}
          >
            <span className="inline-flex"><OutcomeMark outcome={row.outcome} testId="removed-key-outcome" /></span>
          </RichTooltip>
        )),
    },
    {
      id: "record",
      header: "Record",
      render: (row) => (row.recordStatus === undefined
        ? <span className="text-[12px] text-muted-foreground">none</span>
        : <span className="font-mono text-[12px]" data-testid="removed-key-record">{row.recordStatus}</span>),
    },
    {
      id: "found",
      header: "Found",
      render: (row) => (
        <RichTooltip title={`Removal run ${row.detectedRunId}`} body={`${row.removalFlow} (${row.entry}), ${instant(row.createdUtc)}`} mono>
          <span className="whitespace-nowrap text-[12px] text-muted-foreground"><RelativeTime value={row.createdUtc} absolute={false} /></span>
        </RichTooltip>
      ),
    },
    {
      id: "done",
      header: "Done",
      render: (row) => (row.doneUtc === undefined
        ? <span className="text-[12px] text-muted-foreground">-</span>
        : (
          <RichTooltip title={row.doneRunId === undefined ? "Done" : `Removal run ${row.doneRunId}`} body={instant(row.doneUtc)} mono>
            <span className="whitespace-nowrap text-[12px] text-muted-foreground"><RelativeTime value={row.doneUtc} absolute={false} /></span>
          </RichTooltip>
        )),
    },
    {
      id: "by",
      header: "Found by",
      render: (row) => <TruncatedText text={`${row.removalFlow} / ${row.entry}`} mono maxWidth={220} />,
    },
  ];
  // A grid of one state says it once, in its tab, not on every row.
  const columns = all.filter((column) => !hidden.has(column.id) && !(column.id === "state" && state !== null));
  const noun = state === null ? "deletion" : `${stateVisual(state).label.toLowerCase()} deletion`;
  const picking = lift !== undefined && picked.size > 0;
  const allLoaded = (rows ?? []).length;

  const toolbar = (
    <div className="flex min-w-0 flex-col">
      <div className="flex min-w-0 items-center gap-1 border-b border-border pl-1.5 pr-2">
        {leading}
        <RemovalStateStrip counts={counts} selected={state} onSelect={onState} testId={`${testId}-states`} />
        <div className="ml-auto flex shrink-0 items-center gap-0.5">
          {trailing}
          <GridViewMenu columns={choices} hidden={hidden} atFirst={AT_FIRST} onToggle={toggleColumn} compact testId={`${testId}-view`} />
        </div>
      </div>
      {picking && (
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1 border-b border-border bg-accent/40 px-3 py-1.5" data-testid={`${testId}-selection-bar`}>
          <span className="text-[13px] font-medium tabular-nums" data-testid={`${testId}-selection-count`}>
            {`${pickedKeys.length.toLocaleString("en-US")} selected`}
          </span>
          {pickedKeys.length < allLoaded && allLoaded <= MAX_LIFT && (
            <Button
              variant="link"
              size="sm"
              className="h-6 px-0 text-[13px]"
              onClick={() => setPicked(new Set((rows ?? []).map((row) => row.deliveryKey)))}
              data-testid={`${testId}-select-loaded`}
            >
              {`Select the ${allLoaded.toLocaleString("en-US")} loaded`}
            </Button>
          )}
          {tooMany && (
            <span className="text-[12.5px] text-muted-foreground" data-testid={`${testId}-too-many`}>
              {`A lift names at most ${MAX_LIFT.toLocaleString("en-US")} records`}
            </span>
          )}
          <Button variant="link" size="sm" className="h-6 px-0 text-[13px] text-muted-foreground" onClick={() => setPicked(new Set())} data-testid={`${testId}-clear`}>
            Clear
          </Button>
          <Button
            variant="outline"
            size="sm"
            className="ml-auto h-7"
            onClick={() => setLifting(true)}
            disabled={tooMany || pickedKeys.length === 0}
            title="Stop keeping these keys out and deliver their records again under their ids"
            data-testid={`${testId}-lift`}
          >
            <RotateCcw />
            Deliver again
          </Button>
        </div>
      )}
    </div>
  );

  const emptyMessage = state === null
    ? "No removal flow has found a key of this ledger removed by its source."
    : `No deletion is ${stateVisual(state).label.toLowerCase()}.`;

  return (
    <div className="flex min-w-0 flex-col" data-testid={testId}>
      {pages.isError
        ? (
          <Card className="gap-0 overflow-hidden rounded-lg p-0">
            {toolbar}
            <div className="p-3"><ProblemView error={pages.error} testId={`${testId}-error`} /></div>
          </Card>
        )
        : (
          <DimensionGrid onNearEnd={more} className="[&_tbody_tr]:scroll-mt-8" testId={`${testId}-grid`}>
            <DataTable
              toolbar={toolbar}
              columns={columns}
              rows={rows}
              rowKey={(row) => row.deliveryKey}
              selection={lift === undefined ? undefined : { selected: picked, onChange: setPicked }}
              onRowClick={(row) => {
                if (row.recordStatus !== undefined) {
                  navigate(deliveryRecordRoute({ flowId: ledgerFlowId, deliveryKey: row.deliveryKey }));
                }
              }}
              rowClickable={(row) => row.recordStatus !== undefined}
              footer={(
                <GridFooter
                  shown={rows?.length ?? 0}
                  total={total}
                  noun={noun}
                  hasMore={pages.hasNextPage}
                  loading={pages.isFetchingNextPage}
                  onMore={more}
                  testId={`${testId}-footer`}
                />
              )}
              emptyMessage={emptyMessage}
              data-testid={`${testId}-table`}
            />
          </DimensionGrid>
        )}
      {lift !== undefined && (
        <LiftDialog
          open={lifting}
          onClose={() => setLifting(false)}
          pipelineId={lift.pipelineId}
          scope={lift.scope}
          flowName={lift.flowName}
          keys={pickedKeys}
          onLifted={(result) => {
            setPicked(new Set());
            toast.success(
              result.lifted === 0
                ? "None of them was kept out any more: nothing lifted."
                : `Lifted the removal of ${result.lifted.toLocaleString("en-US")} record${result.lifted === 1 ? "" : "s"}${result.runId ? "; a deliver run delivers them now." : "; the flow's next run delivers them."}`,
              result.runId ? { action: { label: "Open run", onClick: () => navigate(`/runs/${result.runId}`) } } : undefined);
            void queryClient.invalidateQueries({ queryKey: ["delivery"] });
          }}
        />
      )}
    </div>
  );
}
