import { useEffect, useMemo, useRef, useState, type CSSProperties } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { CircleCheck, Loader2, RefreshCw } from "lucide-react";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "@/api/client";
import { CorrelationError } from "@/components/CorrelationError";
import { DataTable, type Column } from "@/components/DataTable";
import { EmptyState } from "@/components/EmptyState";
import { IconAction } from "@/components/IconAction";
import { RichTooltip } from "@/components/RichTooltip";
import { SearchInput } from "@/components/SearchInput";
import { useOwnedPanel } from "@/layout/workbench/useOwnedPanel";
import { deliveryApi, ledgerLabel, type DeliveryIssue } from "../../api/delivery";
import { DimensionGrid } from "./dimensions/DimensionGrid";
import { IssuePanel, IssueStates, ShapeMark } from "./DeliveryIssuePanel";
import { IssueSummary } from "./IssueMessage";
import { InterfacePicker } from "./InterfacePicker";
import { issueHeadline, issueHolds, issueMatches, issuesFootText } from "./issueText";
import { arrowsKeptByTarget, typedInField } from "./panelKeys";
import { OutsidePartition } from "./PartitionNotice";
import { CompactTime } from "./RecordCells";
import { problemText } from "./problemText";
import { useInterfaceChoice } from "./useInterfaceChoice";

/** How often the counts are read again while the tab is open: each read counts the blocked records of the ledger in view. */
const REFRESH_MS = 30000;

/** The workbench's bottom panel content this tab raises: an issue's id after this prefix. */
const PANEL = "delivery-issue:";

/** The row of the issue open in the panel. */
const OPEN_ROW: CSSProperties = { backgroundColor: "var(--accent)" };

/**
 * A flow's blocked records grouped by the issue that keeps them blocked (osdu/docs/reference/concepts/record-lifecycle.md,
 * Issues): a million held records read as the handful of issues they share, the most records first, in a grid that fits
 * the window and scrolls in place. Each issue is the error its records share, headed by the property it names, where it
 * lies (a set error, fixed once for the whole set, or row errors, each its row's), how many records it keeps blocked and
 * how they stand, and when they last changed; a filter finds one by its words or id. An issue opens in the workbench's
 * bottom panel, over the grid as a run's trace does, where its records are checked and released together; Up and Down
 * step through the issues while it is open, and the issue open travels in the address (`issue`), so a link opens it. One
 * interface's ledger in the title bar's partition, as the Records tab is.
 */
export function DeliveryIssuesPanel({ pipelineId, flowName }: { pipelineId: string; flowName: string }) {
  const queryClient = useQueryClient();
  const [searchParams, setSearchParams] = useSearchParams();
  const {
    names, many, interfaceName, partitions, partition, headerPartition, outside, unplaced, active, scope, ready, selectInterface,
  } = useInterfaceChoice(pipelineId, ["issue"]);
  const [filter, setFilter] = useState("");

  const listing = useQuery({
    queryKey: ["delivery", "issues", pipelineId, interfaceName, partition],
    queryFn: () => deliveryApi.issues(pipelineId, scope),
    enabled: ready,
    refetchInterval: REFRESH_MS,
  });
  const data = listing.data;
  const issues = useMemo(() => data?.issues.filter((issue) => issueMatches(issue, filter)), [data, filter]);

  // The issue open in the address, which a click, a step, the panel's close and a link change. The issues asked for and
  // not in the address yet are kept in order, so the address catching up with one of them is told from a link.
  const openId = searchParams.get("issue");
  const asked = useRef<(string | null)[]>([]);
  const open = (issue: string | null) => {
    asked.current.push(issue);
    setSearchParams((current) => {
      const next = new URLSearchParams(current);
      if (issue === null) {
        next.delete("issue");
      } else {
        next.set("issue", issue);
      }

      return next;
    }, { replace: true });
  };

  const rows = issues ?? [];
  const at = openId === null ? -1 : rows.findIndex((row) => row.issue === openId);
  const listed = openId === null ? undefined : data?.issues.find((row) => row.issue === openId);
  const canBack = at > 0;
  const canForward = at >= 0 && at < rows.length - 1;
  // The issue stepped to last, ahead of the address that follows it, so keys pressed faster than the page renders (a key
  // held down) each step on from the one before rather than from the same row. The address reaching an issue asked for
  // with later ones still on the way leaves it ahead; one that names an issue nobody here asked for (a link, the back
  // button) is where the next step starts.
  const stepped = useRef(openId);
  useEffect(() => {
    const reached = asked.current.indexOf(openId);
    asked.current = reached < 0 ? [] : asked.current.slice(reached + 1);
    if (asked.current.length === 0) {
      stepped.current = openId;
    }
  }, [openId]);
  const step = (by: -1 | 1) => {
    const from = rows.findIndex((row) => row.issue === stepped.current);
    const next = from + by;
    if (from >= 0 && next >= 0 && next < rows.length) {
      stepped.current = rows[next].issue;
      open(rows[next].issue);
    }
  };

  // What the panel's content and the keys reach for: the latest rows and the page's own way to open an issue, so a panel
  // raised a while ago, or a listener set once, holds nothing stale.
  const latest = useRef({ step, open, openId });
  useEffect(() => {
    latest.current = { step, open, openId };
  });

  const ledgerName = ledgerLabel(flowName, scope);
  const recordsLink = (issue: string) => {
    const params = new URLSearchParams(searchParams);
    params.set("tab", "records");
    params.set("issue", issue);
    return `?${params.toString()}`;
  };

  // The issue open in the address is raised in the panel, and raised again as what the panel says of it changes (the
  // listing read again, the issues around it, a step to another); with none open, the panel is closed when it shows this
  // tab's. Whether it does is read, not followed: a panel closed by its own close must not be raised again on the way to
  // the address letting go of the issue.
  const { ownedId, show, close } = useOwnedPanel(PANEL);
  const showing = ownedId !== null;
  const showingNow = useRef(showing);
  useEffect(() => {
    showingNow.current = showing;
  });
  const wanted = ready ? openId : null;
  const placeAt = at >= 0 ? at + 1 : null;
  const placeOf = rows.length;
  const wantedRecords = wanted === null ? null : recordsLink(wanted);
  const panelTitle = listed === undefined ? "Issue" : `Issue · ${issueHeadline(issueHolds(listed.shared)[0]).property ?? listed.issue}`;
  useEffect(() => {
    if (wanted === null || wantedRecords === null) {
      if (showingNow.current) {
        close();
      }

      return;
    }

    show(wanted, panelTitle, (
      <IssuePanel
        pipelineId={pipelineId}
        scope={scope}
        issueId={wanted}
        listed={listed}
        ledgerName={ledgerName}
        partition={partition}
        place={placeAt === null ? null : { at: placeAt, of: placeOf }}
        canBack={canBack}
        canForward={canForward}
        onStep={(by) => latest.current.step(by)}
        onClose={() => latest.current.open(null)}
        recordsLink={wantedRecords}
      />
    ));
  }, [wanted, wantedRecords, listed, panelTitle, pipelineId, scope, ledgerName, partition, placeAt, placeOf, canBack, canForward, show, close]);

  // The panel closed by its own close, or taken by another surface (a run's trace), lets go of the issue in the address.
  // Only a panel that showed this tab's issue and no longer does counts, so the first raise is never mistaken for a close.
  const wasShowing = useRef(false);
  useEffect(() => {
    if (wasShowing.current && !showing && latest.current.openId !== null) {
      latest.current.open(null);
    }

    wasShowing.current = showing;
  }, [showing]);

  // The row of the issue open stays in view as the panel steps through the issues. A panel just opened takes its height
  // from the page a frame or two after it is shown, so the row is brought into view once the page has its new height.
  const grid = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const container = grid.current;
    if (at < 0 || !showing || container === null) {
      return undefined;
    }

    let frame = requestAnimationFrame(() => {
      frame = requestAnimationFrame(() => {
        container.querySelectorAll<HTMLTableRowElement>('[data-slot="table-container"] tbody tr')[at]?.scrollIntoView({ block: "nearest" });
      });
    });
    return () => cancelAnimationFrame(frame);
  }, [at, showing]);

  // Up and Down step through the issues while the panel shows one, unless they are for something that moves by arrows of
  // its own; Escape closes it from anywhere but a field, once whatever it would close first (a menu, a dialog) has taken it.
  useEffect(() => {
    if (!showing) {
      return undefined;
    }

    const keyed = (event: KeyboardEvent) => {
      if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey || typedInField(event.target)) {
        return;
      }

      if ((event.key === "ArrowDown" || event.key === "ArrowUp") && !arrowsKeptByTarget(event.target)) {
        event.preventDefault();
        latest.current.step(event.key === "ArrowDown" ? 1 : -1);
      } else if (event.key === "Escape") {
        latest.current.open(null);
      }
    };
    window.addEventListener("keydown", keyed);
    return () => window.removeEventListener("keydown", keyed);
  }, [showing]);

  if (outside !== null || unplaced) {
    return (
      <div className="flex flex-col gap-4" data-testid="delivery-panel-issues">
        <OutsidePartition flowName={flowName} active={active} partitions={partitions} headerPartition={headerPartition} />
      </div>
    );
  }

  const columns: Column<DeliveryIssue>[] = [
    {
      id: "issue",
      header: "Issue",
      fill: true,
      floor: 280,
      render: (row) => (
        // The issue's words are the row's control: a reader moving by Tab opens the issue with Enter, as a click does.
        <button
          type="button"
          className="block w-full min-w-0 rounded-sm py-0.5 text-left outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
          onClick={(event) => {
            event.stopPropagation();
            open(row.issue);
          }}
          data-testid="issue-open"
        >
          <IssueSummary text={row.shared} />
        </button>
      ),
    },
    { id: "shape", header: "Kind", render: (row) => <ShapeMark shape={row.shape} /> },
    {
      id: "records",
      header: "Records",
      align: "right",
      render: (row) => <span className="font-mono text-[13px] font-medium tabular-nums" data-testid="issue-records">{row.records.toLocaleString()}</span>,
    },
    { id: "states", header: "State", render: (row) => <IssueStates held={row.held} failed={row.failed} /> },
    {
      id: "newest",
      header: "Last changed",
      render: (row) => <CompactTime value={row.newestUtc} caption="A record of this issue last changed" className="text-[12px]" />,
    },
  ];

  const toolbar = (
    <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1.5 border-b border-border px-3 py-1.5" data-testid="issues-summary">
      {data === undefined ? <Skeleton className="h-4 w-56" /> : (
        <span className="text-[13px]">
          <span className="font-mono font-medium tabular-nums">{data.totalRecords.toLocaleString()}</span>
          {` blocked record${data.totalRecords === 1 ? "" : "s"} in `}
          <span className="font-mono font-medium tabular-nums">{data.totalIssues.toLocaleString()}</span>
          {` issue${data.totalIssues === 1 ? "" : "s"}`}
        </span>
      )}
      {data !== undefined && data.unsorted > 0 && (
        <RichTooltip
          title="Not sorted yet"
          body="Held or failed before the ledger kept issues. The control plane sorts them into theirs in the background, a page at a time; records blocked from now on are sorted as they are blocked. Releasing every blocked record from the Delivery tab reaches them too."
        >
          <span className="text-[12.5px] text-muted-foreground underline decoration-dotted underline-offset-2" data-testid="issues-unsorted">
            {`and ${data.unsorted.toLocaleString()} not sorted into an issue yet`}
          </span>
        </RichTooltip>
      )}
      <span className="ml-auto flex items-center gap-1">
        <SearchInput value={filter} onChange={setFilter} placeholder="Filter by words or id" label="Filter the issues by their words or id" className="h-8 sm:w-60" testId="issues-filter" />
        <IconAction
          label={listing.isFetching ? "Reading the issues" : "Read the issues again"}
          icon={listing.isFetching ? <Loader2 className="animate-spin" /> : <RefreshCw />}
          onClick={() => void queryClient.invalidateQueries({ queryKey: ["delivery"] })}
          disabled={listing.isFetching}
          className="size-8"
          data-testid="issues-refresh"
        />
      </span>
    </div>
  );

  const foot = data === undefined || data.issues.length === 0 ? null : (
    <div className="flex h-8 items-center border-t border-border px-3 text-xs text-muted-foreground" data-testid="issues-foot">
      <span className="font-mono tabular-nums">{issuesFootText(data.issues.length, data.totalIssues, rows.length, filter.trim() !== "")}</span>
      {showing && <span className="ml-auto hidden sm:inline">Up and Down step through the issues; Escape closes the panel</span>}
    </div>
  );

  return (
    <div className="flex min-w-0 flex-col gap-3" data-testid="delivery-panel-issues">
      {many && (
        <InterfacePicker names={names} interfaceName={interfaceName} onSelect={selectInterface} caption={`of ${names.length} interfaces of ${flowName}`} />
      )}

      {listing.isError ? (
        isApiError(listing.error) ? <CorrelationError error={listing.error} /> : <p className="text-[13px] text-destructive">{problemText(listing.error)}</p>
      ) : !ready ? <Skeleton className="h-40 w-full rounded-lg" /> : data !== undefined && data.totalIssues === 0 ? (
        <Card className="rounded-lg p-0">
          <EmptyState
            icon={<CircleCheck />}
            title={data.unsorted > 0 ? "No issue sorted yet" : "No blocked records"}
            description={data.unsorted > 0
              ? `${data.unsorted.toLocaleString()} blocked record${data.unsorted === 1 ? " is" : "s are"} still being sorted into issues, a page at a time.`
              : "A record that is held or fails shows here, grouped with the others blocked for the same reason, until its row changes or it is released."}
            data-testid="issues-empty"
          />
        </Card>
      ) : (
        <div ref={grid} className="min-w-0">
          <DimensionGrid testId="issues-grid" className="[&_td]:align-top [&_td]:py-1.5">
            <DataTable
              toolbar={toolbar}
              columns={columns}
              rows={issues}
              rowKey={(row) => row.issue}
              onRowClick={(row) => open(row.issue)}
              rowSx={(row) => (showing && row.issue === openId ? OPEN_ROW : undefined)}
              footer={foot}
              emptyMessage={filter.trim() === "" ? "No issue keeps a record of this ledger blocked." : "No issue matches the filter."}
              data-testid="issues-table"
            />
          </DimensionGrid>
        </div>
      )}
    </div>
  );
}
