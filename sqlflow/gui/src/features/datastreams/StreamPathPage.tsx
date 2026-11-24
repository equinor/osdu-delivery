import { useEffect, useMemo, useState, type ReactNode } from "react";
import { useInfiniteQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useParams, useSearchParams } from "react-router-dom";
import { ChevronRight, CircleCheck, CircleSlash, RefreshCw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import { dataStreamApi } from "../../api/endpoints";
import type { StreamPath, StreamPathFire, StreamPathNode } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { RelativeTime } from "../../components/RelativeTime";
import { RunStartedByCell } from "../../components/RunStartedBy";
import { useLocalStorageState } from "../../hooks/useLocalStorageState";
import { pollingInterval } from "../../hooks/usePolling";
import { StreamFireStrip } from "./StreamFireStrip";
import { StreamPathCanvas } from "./StreamPathCanvas";
import { StreamPathPanel } from "./StreamPathPanel";
import { StreamSwitcher } from "./StreamSwitcher";
import { statusIcons, statusInk } from "./StreamStatusBadge";
import { finding } from "./streamPresentation";
import {
  fireTallyText, fireTime, firesOldestFirst, flowsInRunningOrder, pathEnds, runOutcome,
} from "./streamPathModel";

const windowChoices = [
  { days: 30, label: "30d" },
  { days: 60, label: "60d" },
  { days: 90, label: "90d" },
];

/** The width the node panel takes over the right of the canvas, and the gap it leaves. */
const PANEL_WIDTH = 380;

/** The first line of a run's error, short enough for the page's opening sentence. */
function firstLine(text: string): string {
  const line = text.split(/\r?\n/, 1)[0] ?? text;
  return line.length > 160 ? `${line.slice(0, 157)}...` : line;
}

/**
 * What the page says first: what is wrong, in one or two sentences, each naming the flow it is about as a button that
 * opens it. The newest fire's failure leads, because it is what is broken now; the worst verdict over the window
 * follows, because it is what has been going wrong. A healthy path says so in a quiet line.
 */
function PathVerdict({ path, newest, onPick }: {
  path: StreamPath;
  newest: StreamPathFire | null;
  onPick: (nodeId: string) => void;
}) {
  const nodeOf = (pipelineId: string) => path.nodes.find((node) => node.pipelineId === pipelineId);
  const failed = newest?.runs.find((entry) => runOutcome(entry.run) === "failed") ?? null;
  const problems = path.streams.filter((stream) => stream.status === "stalled" || stream.status === "degraded" || stream.status === "watch");
  const worst = problems[0] ?? null;

  const name = (pipelineId: string, flowName: string) => {
    const node = nodeOf(pipelineId);
    return node === undefined
      ? <span className="font-mono">{flowName}</span>
      : (
        <button
          type="button"
          className="cursor-pointer font-mono underline decoration-dotted underline-offset-4"
          onClick={() => onPick(node.id)}
        >
          {flowName}
        </button>
      );
  };

  return (
    <div className="flex flex-col gap-1 text-[13px]" data-testid="stream-path-verdict-line">
      {failed !== null && newest !== null && (
        <p className="flex items-start gap-1.5" data-testid="stream-path-latest-failure">
          <CircleSlash className="mt-0.5 size-4 shrink-0 text-destructive" aria-hidden />
          <span>
            The latest fire, {fireTime(newest.startedUtc)}, failed at {name(failed.run.pipelineId, failed.run.flowName)}
            {failed.run.error !== null && <span className="text-muted-foreground">: {firstLine(failed.run.error)}</span>}
          </span>
        </p>
      )}
      {worst !== null && (
        <p className="flex items-start gap-1.5" data-testid="stream-path-worst">
          {(() => {
            const Icon = statusIcons[worst.status];
            return <Icon className={cn("mt-0.5 size-4 shrink-0", statusInk[worst.status])} aria-hidden />;
          })()}
          <span>
            {name(worst.pipelineId, worst.flowName)}: {finding(worst)}
            {problems.length > 1 && (
              <span className="text-muted-foreground">
                , and {problems.length - 1} more flow{problems.length === 2 ? "" : "s"} on this path need a look
              </span>
            )}
          </span>
        </p>
      )}
      {failed === null && worst === null && (
        <p className="flex items-center gap-1.5 text-muted-foreground" data-testid="stream-path-healthy">
          <CircleCheck className="size-4 shrink-0" aria-hidden />
          {path.streams.length === 0
            ? `No flow on this path ran in the last ${path.windowDays} days.`
            : `Every flow on this path is delivering as usual over the last ${path.windowDays} days.`}
        </p>
      )}
      {!path.lineageKnown && (
        <p className="text-muted-foreground" data-testid="stream-path-no-lineage">
          This flow has no lineage yet, so its path is the flow alone: lineage is computed when its repository syncs.
        </p>
      )}
    </div>
  );
}

/** What the lines and marks on the canvas mean, in its corner. */
function Legend() {
  const item = (mark: ReactNode, words: string) => (
    <span className="inline-flex items-center gap-1.5">{mark}{words}</span>
  );
  return (
    <div className="pointer-events-none absolute bottom-2 left-3 flex flex-wrap items-center gap-x-3 gap-y-1 rounded-md bg-card/85 px-2 py-1 text-[11px] text-muted-foreground" aria-hidden>
      {item(<span className="h-0.5 w-5 bg-chart-1" />, "data moved")}
      {item(<span className="h-0 w-5 border-t-[1.5px] border-dashed border-muted-foreground/70" />, "ran, moved nothing")}
      {item(<span className="h-0.5 w-5 bg-destructive" />, "failed")}
      {item(<span className="h-0 w-5 border-t-[1.5px] border-dotted border-border" />, "no run in this fire")}
    </div>
  );
}

/**
 * One data stream followed end to end, for the question the board's split cannot answer on its own: what did the
 * vendor deliver, and what did each of our flows do with it.
 *
 * The page reads top to bottom in that order. First what is wrong, in a sentence. Then the fires: every start that ran
 * the path's flows, oldest on the left, one row of dots per flow, so a stage that stopped while the one above kept
 * delivering is the row that goes hollow. Then the path itself, drawn in the order the data moves (where it enters, each
 * flow in its wave's band, what each writes under it, where it ends) and told for the fire picked: the files that
 * arrived, what each run took in and did, and the line the data travelled, which stops where the data stopped. A node
 * opens on the right with its run in that fire and its verdict over the window, the board's own analysis, so the two
 * surfaces can never disagree.
 *
 * The fire and the node picked are kept in the address, so a link shows the same fire and node to whoever opens it.
 */
export default function StreamPathPage() {
  const { pipelineId = "" } = useParams<{ pipelineId: string }>();
  const [params, setParams] = useSearchParams();
  const [days, setDays] = useLocalStorageState<number>("datastreams.windowDays", 60);
  const [includeBackfills, setIncludeBackfills] = useLocalStorageState<boolean>("datastreams.includeBackfills", false);
  const [fireKey, setFireKey] = useState<string | null>(() => params.get("fire"));
  const [nodeId, setNodeId] = useState<string | null>(() => params.get("node"));
  const [shownFor, setShownFor] = useState(pipelineId);
  const queryClient = useQueryClient();

  // Following another stream starts over: its own newest fire, nothing opened.
  if (shownFor !== pipelineId) {
    setShownFor(pipelineId);
    setFireKey(null);
    setNodeId(null);
  }

  // The page's state is the source of truth and the address follows it in one write, so quick clicks never race it.
  useEffect(() => {
    setParams((current) => {
      const next = new URLSearchParams(current);
      if (fireKey === null) {
        next.delete("fire");
      } else {
        next.set("fire", fireKey);
      }
      if (nodeId === null) {
        next.delete("node");
      } else {
        next.set("node", nodeId);
      }
      return next;
    }, { replace: true });
  }, [fireKey, nodeId, setParams]);

  useEffect(() => {
    if (nodeId === null) {
      return;
    }
    const close = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setNodeId(null);
      }
    };
    window.addEventListener("keydown", close);
    return () => window.removeEventListener("keydown", close);
  }, [nodeId]);

  const query = useInfiniteQuery({
    queryKey: ["datastreams", "path", pipelineId, days, includeBackfills],
    queryFn: ({ pageParam }) => dataStreamApi.path(pipelineId, {
      days, includeBackfills, before: pageParam ?? undefined,
    }),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.olderThanUtc,
    refetchInterval: pollingInterval(60_000),
    enabled: pipelineId !== "",
  });

  const path = query.data?.pages[0];
  const fires = useMemo(() => {
    const seen = new Set<string>();
    const all: StreamPathFire[] = [];
    for (const page of query.data?.pages ?? []) {
      for (const fire of page.fires) {
        if (!seen.has(fire.key)) {
          seen.add(fire.key);
          all.push(fire);
        }
      }
    }
    return firesOldestFirst(all);
  }, [query.data]);
  const flows = useMemo(() => (path === undefined ? [] : flowsInRunningOrder(path)), [path]);
  const newest = fires.length === 0 ? null : fires[fires.length - 1];
  const fire = fires.find((candidate) => candidate.key === fireKey) ?? newest;
  const selectedNode: StreamPathNode | null = path?.nodes.find((node) => node.id === nodeId) ?? null;

  const recheck = () => {
    void queryClient.invalidateQueries({ queryKey: ["datastreams"] });
  };

  const controls = (
    <div className="flex flex-wrap items-center gap-3">
      <StreamSwitcher
        pipelineId={pipelineId}
        flowName={path?.flowName ?? "Pick a stream"}
        windowDays={days}
        includeBackfills={includeBackfills}
      />
      <Button
        variant="outline"
        size="sm"
        onClick={recheck}
        disabled={query.isFetching}
        aria-label="Recheck this stream"
        data-testid="stream-path-recheck"
      >
        <RefreshCw className={query.isFetching ? "animate-spin" : undefined} />
        Recheck
      </Button>
      <div className="flex items-center gap-2">
        <Switch
          id="stream-path-include-backfills"
          checked={includeBackfills}
          onCheckedChange={setIncludeBackfills}
          data-testid="stream-path-include-backfills"
        />
        <Label htmlFor="stream-path-include-backfills" className="text-xs font-normal text-muted-foreground">
          Include backfills in the verdicts
        </Label>
      </div>
      <ToggleGroup
        type="single"
        value={String(days)}
        onValueChange={(value) => { if (value !== "") { setDays(Number(value)); } }}
        variant="outline"
        size="sm"
        data-testid="stream-path-window"
      >
        {windowChoices.map((choice) => (
          <ToggleGroupItem key={choice.days} value={String(choice.days)} className="px-2.5 text-xs">
            {choice.label}
          </ToggleGroupItem>
        ))}
      </ToggleGroup>
    </div>
  );

  const title = (
    <span className="flex min-w-0 items-center gap-1.5">
      <Link to="/datastreams" className="shrink-0 text-muted-foreground hover:text-foreground" data-testid="stream-path-back">
        Data streams
      </Link>
      <ChevronRight className="size-4 shrink-0 text-muted-foreground" aria-hidden />
      <span className="min-w-0 truncate font-mono text-[16px]">{path?.flowName ?? ""}</span>
    </span>
  );

  if (query.isError && path === undefined) {
    return (
      <Page data-testid="page-stream-path">
        <PageHeader title={title} actions={controls} />
        {isApiError(query.error) && query.error.status === 404
          ? (
            <EmptyState
              title="No such stream"
              description="The flow this link names is not in the catalog. Pick a stream above, or go back to the board."
            />
          )
          : isApiError(query.error)
            ? <CorrelationError error={query.error} />
            : <p className="text-[13px] text-destructive">{String(query.error)}</p>}
      </Page>
    );
  }

  if (path === undefined) {
    return (
      <Page data-testid="page-stream-path">
        <PageHeader title={title} actions={controls} />
        <Skeleton className="h-6 w-[480px] rounded-md" />
        <Skeleton className="h-24 rounded-lg" />
        <Skeleton className="h-[440px] rounded-lg" />
      </Page>
    );
  }

  const ends = pathEnds(path);
  const flowCount = flows.length;
  const firstRun = fire?.runs[0]?.run ?? null;

  return (
    <Page data-testid="page-stream-path">
      <PageHeader
        title={title}
        subtitle={(
          <>
            {ends.from.length > 0 ? `From ${ends.from.join(", ")}` : "Where the data enters is not in lineage"}
            {ends.to.length > 0 && ` to ${ends.to.join(", ")}`}
            {`, through ${flowCount} flow${flowCount === 1 ? "" : "s"}`}
            {" · checked "}
            <RelativeTime value={path.asOfUtc} absolute={false} />
          </>
        )}
        actions={controls}
      />

      <PathVerdict path={path} newest={newest} onPick={setNodeId} />

      <Card className="gap-2 px-4 py-3" data-testid="stream-path-fires">
        <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
          <div className="flex min-w-0 flex-wrap items-baseline gap-x-2 gap-y-1">
            <h2 className="text-sm font-medium">Fires</h2>
            {fire !== null && (
              <span className="flex min-w-0 flex-wrap items-center gap-x-2 text-[12px] text-muted-foreground" data-testid="stream-path-fire-caption">
                <span className="text-foreground">{fireTime(fire.startedUtc)}</span>
                <span>{fireTallyText(fire)}</span>
                {firstRun !== null && <RunStartedByCell startedBy={firstRun.startedBy} />}
                {fire.groupId !== null && (
                  <Link to={`/runs/groups/${fire.groupId}`} className="underline decoration-dotted underline-offset-4 hover:text-foreground">
                    Open the fire
                  </Link>
                )}
              </span>
            )}
          </div>
          <span className="flex items-center gap-2 text-[11px] text-muted-foreground">
            {fires.length === path.totalFires
              ? `${path.totalFires} fire${path.totalFires === 1 ? "" : "s"} in the last ${path.windowDays} days`
              : `${fires.length} of ${path.totalFires} fires in the last ${path.windowDays} days`}
            {query.hasNextPage && (
              <Button
                variant="ghost"
                size="xs"
                className="text-muted-foreground"
                onClick={() => void query.fetchNextPage()}
                disabled={query.isFetchingNextPage}
                data-testid="stream-fire-older"
              >
                {query.isFetchingNextPage ? "Reading..." : "Show older"}
              </Button>
            )}
          </span>
        </div>
        {fires.length === 0
          ? (
            <p className="text-[12px] text-muted-foreground">
              No flow on this path ran in the last {path.windowDays} days. Widen the window, or run the flow.
            </p>
          )
          : (
            <StreamFireStrip
              fires={fires}
              flows={flows}
              selectedKey={fire?.key ?? null}
              onSelect={setFireKey}
            />
          )}
      </Card>

      <Card className="relative h-[68vh] min-h-[440px] gap-0 overflow-hidden p-0" data-testid="stream-path-card">
        <StreamPathCanvas
          path={path}
          fire={fire}
          windowDays={path.windowDays}
          selectedId={selectedNode?.id ?? null}
          onSelect={setNodeId}
          inset={selectedNode === null ? 0 : PANEL_WIDTH + 16}
        />
        <Legend />
        {selectedNode !== null && (
          <div className="absolute top-2 right-2 bottom-2 max-w-[85%]" style={{ width: PANEL_WIDTH }}>
            <StreamPathPanel
              node={selectedNode}
              path={path}
              fire={fire}
              fireLabel={fire === null ? null : fireTime(fire.startedUtc)}
              windowDays={path.windowDays}
              includeBackfills={includeBackfills}
              onClose={() => setNodeId(null)}
              onPick={setNodeId}
            />
          </div>
        )}
      </Card>
    </Page>
  );
}
