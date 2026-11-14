import { useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { Download, Gauge, ListTree, Loader2, Tags, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { EmptyState } from "@/components/EmptyState";
import { ExplainMark } from "@/components/ExplainTip";
import { IconAction } from "@/components/IconAction";
import { KpiCard } from "@/components/KpiCard";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { TreeNode, TreeRoot, type TreeState } from "@/components/Tree";
import { cn } from "@/lib/utils";
import { COMPUTE_USAGE_KEY, computeUsageApi, type ComputeUsage, type ComputeWork, type ComputeWorkFilter } from "../../../api/computeUsage";
import { problemText } from "../problemText";
import {
  PERIODS, branchIds, chargedCores, daysOf, divisionCsv, divisionOf, formatShare, formatUnits, formatWallClock, labelOf, periodOf, periodRange,
  shareOf, unassignedUnits, type ComputeDay, type DivisionFilter, type DivisionLevel, type DivisionNode, type PeriodId,
} from "./computeUsageModel";

const TABS = ["division", "work", "teams"] as const;
type UsageTab = (typeof TABS)[number];

/** How many pieces of work the list asks for at a time. */
const WORK_PAGE = 100;

const HOW_IT_IS_COUNTED =
  "One compute unit is one core held for one hour.\n\n"
  + "A node's cores are shared evenly over its slots: its run slots and its compute task slots. A run holds one slot, a run that "
  + "takes a whole node holds every run slot, and a compute task holds one.\n\n"
  + "compute units = wall-clock hours x the node's cores x slots held / the node's slots\n\n"
  + "The wall clock runs on the node, from the moment it is handed the work until it reports the outcome. Work is counted on the "
  + "UTC day it ended.";

const HOW_TO_NAME_A_TEAM =
  "A flow belongs to the team its repository document names. Add a team to osdu-delivery.yaml at the repository's root:\n\n"
  + "documentType: repository\ncode: wl\nteam: Subsurface data\n\n"
  + "A folder can hold a document of its own, and the flows under it take its team. Sync the repository; the work that ends after "
  + "the sync is charged to the team. Work recorded before it stays as it was recorded.";

/** The filter of the work list an address names: a value that is present, even empty, is asked for. */
function filterOf(params: URLSearchParams): DivisionFilter | null {
  const team = params.get("team");
  if (team === null) {
    return null;
  }

  return { team, repo: params.get("repo") ?? undefined, project: params.get("project") ?? undefined, flow: params.get("flow") ?? undefined };
}

/** A filter as the words the page heads the work list with: each level it names, by its label. */
function filterParts(filter: DivisionFilter): string[] {
  const levels: [DivisionLevel, string | undefined][] = [["team", filter.team], ["repo", filter.repo], ["project", filter.project], ["flow", filter.flow]];
  return levels.flatMap(([level, value]) => (value === undefined ? [] : [labelOf(level, value)]));
}

function download(name: string, text: string) {
  const url = URL.createObjectURL(new Blob([text], { type: "text/csv;charset=utf-8" }));
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = name;
  anchor.click();
  URL.revokeObjectURL(url);
}

/** A period's compute units a day: one bar per day, the days nothing ran left at the baseline. */
function DayChart({ days }: { days: ComputeDay[] }) {
  const most = Math.max(...days.map((day) => day.computeUnits), 0);
  return (
    <div data-testid="compute-usage-days">
      <div className="flex h-20 items-end gap-px">
        {days.map((day) => (
          <Tooltip key={day.day}>
            <TooltipTrigger asChild>
              <div className="group flex h-full min-w-0 flex-1 items-end" data-testid={`compute-usage-day-${day.day}`}>
                <div
                  className={cn("w-full rounded-t-[2px]", day.computeUnits > 0 ? "bg-primary/75 group-hover:bg-primary" : "bg-border")}
                  style={{ height: day.computeUnits > 0 && most > 0 ? `${Math.max(2, (day.computeUnits / most) * 100)}%` : "1px" }}
                />
              </div>
            </TooltipTrigger>
            <TooltipContent>
              <div className="flex flex-col gap-0.5 text-[12px]">
                <span className="font-medium">{day.day}</span>
                <span>{formatUnits(day.computeUnits)} compute units</span>
                <span>{formatWallClock(day.wallClockMs)} of work, {day.work.toLocaleString()} {day.work === 1 ? "piece" : "pieces"}</span>
              </div>
            </TooltipContent>
          </Tooltip>
        ))}
      </div>
      <div className="mt-1 flex justify-between text-[11px] text-muted-foreground">
        <span>{days[0]?.day}</span>
        <span>most in a day: {formatUnits(most)}</span>
        <span>{days[days.length - 1]?.day}</span>
      </div>
    </div>
  );
}

/** The figures of one row of the division, in the columns the heading names. */
function Figures({ node, total, onWork }: { node: DivisionNode; total: number; onWork: (filter: DivisionFilter) => void }) {
  const percent = shareOf(node.computeUnits, total);
  return (
    <>
      <span className="w-24 shrink-0 text-right font-mono text-[12.5px] tabular-nums">{formatUnits(node.computeUnits)}</span>
      <span className="flex w-28 shrink-0 items-center gap-1.5">
        <span className="h-1.5 flex-1 overflow-hidden rounded-full bg-border">
          <span className="block h-full rounded-full bg-primary/75" style={{ width: `${percent ?? 0}%` }} />
        </span>
        <span className="w-12 text-right font-mono text-[11.5px] tabular-nums text-muted-foreground">{formatShare(percent)}</span>
      </span>
      <span className="hidden w-24 shrink-0 text-right font-mono text-[12px] tabular-nums text-muted-foreground md:inline">
        {formatWallClock(node.wallClockMs)}
      </span>
      <span className="hidden w-16 shrink-0 text-right font-mono text-[12px] tabular-nums text-muted-foreground lg:inline">
        {node.work.toLocaleString()}
      </span>
      <span className="w-6 shrink-0" onClick={(event) => event.stopPropagation()} onKeyDown={(event) => event.stopPropagation()}>
        <IconAction
          label={`List the work of ${node.label}`}
          icon={<ListTree />}
          onClick={() => onWork(node.filter)}
          data-testid="compute-usage-row-work"
        />
      </span>
    </>
  );
}

/** One node of the division with everything under it. */
function DivisionRows({ nodes, total, onWork }: { nodes: DivisionNode[]; total: number; onWork: (filter: DivisionFilter) => void }) {
  return (
    <>
      {nodes.map((node) => (
        <TreeNode
          key={node.id}
          id={node.id}
          testId={`compute-usage-${node.level}`}
          label={(
            <>
              <span className={cn("min-w-0 flex-1 truncate", node.unnamed && "italic text-muted-foreground", node.level === "flow" && "font-mono text-[12.5px]")} title={node.label}>
                {node.label}
                {node.kind === "task" && <span className="ml-1.5 font-sans text-[11px] not-italic text-muted-foreground">compute task</span>}
              </span>
              <Figures node={node} total={total} onWork={onWork} />
            </>
          )}
        >
          {node.children.length > 0 ? <DivisionRows nodes={node.children} total={total} onWork={onWork} /> : undefined}
        </TreeNode>
      ))}
    </>
  );
}

/** The division of the period's usage: teams, their repositories, project folders and flows, each with its share. */
function Division({ usage, term, onWork }: { usage: ComputeUsage; term: string; onWork: (filter: DivisionFilter) => void }) {
  const division = useMemo(() => divisionOf(usage.shares, term), [usage.shares, term]);
  const [opened, setOpened] = useState<ReadonlySet<string>>(new Set());
  // A search opens every branch that holds a find; without one the tree is as the reader left it.
  const expanded = useMemo(() => (term.trim() === "" ? opened : new Set(branchIds(division))), [term, opened, division]);
  const state: TreeState = {
    expanded,
    toggle: (id) => setOpened((current) => {
      const next = new Set(current);
      if (!next.delete(id)) {
        next.add(id);
      }

      return next;
    }),
    setOpen: (id, open) => setOpened((current) => {
      const next = new Set(current);
      if (open) {
        next.add(id);
      } else {
        next.delete(id);
      }

      return next;
    }),
    selectedId: null,
    select: () => undefined,
  };
  const total = usage.totals.computeUnits;
  const unassigned = unassignedUnits(usage.shares);

  if (division.length === 0) {
    return (
      <EmptyState
        icon={<Gauge />}
        title={`Nothing in this period is found by "${term.trim()}"`}
        description="The search reads teams, repositories, project folders and flows, by how a word of the name starts."
        data-testid="compute-usage-no-match"
      />
    );
  }

  return (
    <div className="flex flex-col gap-2">
      {unassigned > 0 && (
        <p className="flex items-center gap-1 text-[13px]" data-testid="compute-usage-unassigned">
          <span>
            <span className="font-medium">{formatShare(shareOf(unassigned, total))}</span> of the period&apos;s compute units
            ({formatUnits(unassigned)}) is charged to no team.
          </span>
          <ExplainMark title="How to name a team" text={HOW_TO_NAME_A_TEAM} testId="compute-usage-team-howto" />
        </p>
      )}
      <Card className="gap-0 overflow-hidden rounded-lg p-0">
        <div className="flex h-8 items-center gap-1.5 border-b pl-3 pr-2 text-xs font-medium text-muted-foreground">
          <span className="min-w-0 flex-1">Team, repository, project folder, flow</span>
          <span className="w-24 shrink-0 whitespace-nowrap text-right">Compute units</span>
          <span className="w-28 shrink-0 text-right">Share</span>
          <span className="hidden w-24 shrink-0 text-right md:inline">Wall clock</span>
          <span className="hidden w-16 shrink-0 text-right lg:inline">Work</span>
          <span className="w-6 shrink-0" />
        </div>
        <TreeRoot label="Compute usage by team" state={state} className="max-h-[calc(100vh-22rem)] min-h-40 overflow-y-auto p-1" testId="compute-usage-division">
          <DivisionRows nodes={division} total={total} onWork={onWork} />
        </TreeRoot>
      </Card>
    </div>
  );
}

const WORK_COLUMNS = ["Ended", "Flow", "Charged to", "Node", "Started by", "Wall clock", "Cores held", "Compute units", "Outcome"];

/** One piece of work, with every value its compute units were worked out from. */
function WorkRow({ work }: { work: ComputeWork }) {
  const cores = chargedCores(work.cores, work.slots, work.nodeSlots);
  return (
    <TableRow data-testid="compute-usage-work-row">
      <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]"><RelativeTime value={work.endedUtc} /></TableCell>
      <TableCell className="max-w-64 px-3 py-1.5 text-[13px]">
        <span className="flex min-w-0 flex-col">
          {work.kind === "run"
            ? (
              <Link to={`/runs/${work.workId}`} className="truncate font-mono text-[12.5px] hover:underline" title={`${work.flow}, attempt ${work.attempt}`}>
                {work.flow}
              </Link>
            )
            : <span className="truncate font-mono text-[12.5px]" title={work.flow}>{work.flow}</span>}
          <span className="text-[11.5px] text-muted-foreground">
            {work.kind === "run" ? (work.attempt > 1 ? `run, attempt ${work.attempt}` : "run") : "compute task"}
          </span>
        </span>
      </TableCell>
      <TableCell className="max-w-56 px-3 py-1.5 text-[13px]">
        <span className="flex min-w-0 flex-col">
          <span className={cn("truncate", work.team === "" && "italic text-muted-foreground")}>{labelOf("team", work.team)}</span>
          <span className="truncate text-[11.5px] text-muted-foreground">
            {[labelOf("repo", work.repo), ...(work.repo === "" ? [] : [labelOf("project", work.project)])].join(" / ")}
          </span>
        </span>
      </TableCell>
      <TableCell className="max-w-40 truncate px-3 py-1.5 font-mono text-[12px]" title={work.node}>{work.node}</TableCell>
      <TableCell className="max-w-44 truncate px-3 py-1.5 font-mono text-[12px]" title={work.requestedBy ?? undefined}>
        {work.requestedBy ?? <span className="font-sans text-muted-foreground">not said</span>}
      </TableCell>
      <TableCell className="whitespace-nowrap px-3 py-1.5 text-right font-mono text-[12.5px] tabular-nums">{formatWallClock(work.wallClockMs)}</TableCell>
      <TableCell className="whitespace-nowrap px-3 py-1.5 text-right text-[13px]">
        <Tooltip>
          <TooltipTrigger asChild>
            <span className="cursor-default font-mono text-[12.5px] tabular-nums underline decoration-dotted underline-offset-2">
              {Number.isInteger(cores) ? cores : cores.toFixed(2)}
            </span>
          </TooltipTrigger>
          <TooltipContent>
            {work.slots.toLocaleString()} of the node&apos;s {work.nodeSlots.toLocaleString()} {work.nodeSlots === 1 ? "slot" : "slots"}, on{" "}
            {work.cores.toLocaleString()} {work.cores === 1 ? "core" : "cores"}
          </TooltipContent>
        </Tooltip>
      </TableCell>
      <TableCell className="whitespace-nowrap px-3 py-1.5 text-right font-mono text-[12.5px] tabular-nums">{formatUnits(work.computeUnits)}</TableCell>
      <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">{work.outcome}</TableCell>
    </TableRow>
  );
}

/** The pieces of work behind the period, or behind the part of the division the address names, newest first. */
function WorkList({ range, filter, usage, onClear }: {
  range: { from: string; to: string };
  filter: DivisionFilter | null;
  /** The period's usage once it is read: it says how far back the list reaches. */
  usage: ComputeUsage | undefined;
  onClear: () => void;
}) {
  const asked: ComputeWorkFilter = { ...range, ...(filter ?? {}) };
  const work = useInfiniteQuery({
    queryKey: [...COMPUTE_USAGE_KEY, "work", asked],
    queryFn: ({ pageParam }) => computeUsageApi.work(asked, pageParam, WORK_PAGE),
    initialPageParam: null as string | null,
    getNextPageParam: (page) => page.next,
  });
  const rows = work.data?.pages.flatMap((page) => page.rows) ?? [];
  // Work older than the oldest piece still listed was folded into its day's total: it counts, and is not listed. That is
  // worth saying only where it bites: the period has work, and the list starts after the period does.
  const keptFromUtc = usage?.workKeptFromUtc ?? null;
  const folded = usage !== undefined && usage.totals.work > 0 && (keptFromUtc === null || keptFromUtc.slice(0, 10) > range.from);

  return (
    <div className="flex flex-col gap-2">
      <div className="flex min-h-7 flex-wrap items-center gap-x-2 gap-y-1 text-[13px]">
        {filter === null
          ? <span className="text-muted-foreground">Every piece of work of the period, newest first.</span>
          : (
            <>
              <span data-testid="compute-usage-work-filter">
                The work of <span className="font-medium">{filterParts(filter).join(" / ")}</span>, newest first.
              </span>
              <Button variant="ghost" size="xs" onClick={onClear} data-testid="compute-usage-work-clear">
                <X />
                Show all work
              </Button>
            </>
          )}
        {folded && (
          <span className="flex items-center gap-1 text-muted-foreground" data-testid="compute-usage-work-folded">
            {keptFromUtc === null ? "The period's work is in its days' totals, not listed piece by piece." : <>Listed from <RelativeTime value={keptFromUtc} /></>}
            <ExplainMark
              title="Why older work is not listed"
              text={"An admin set how long the nodes' work stays listed piece by piece (Maintenance, Compute usage). A cleanup adds each older piece to the total of its day, team and flow before it removes it, so the totals, the chart and the division still count it; only the piece-by-piece list starts later."}
              testId="compute-usage-folded-why"
            />
          </span>
        )}
      </div>
      {work.isError && <p className="text-[13px] text-destructive">{problemText(work.error)}</p>}
      <Card className="gap-0 overflow-hidden rounded-lg p-0">
        <div className="max-h-[calc(100vh-22rem)] min-h-40 overflow-auto">
          <Table>
            <TableHeader className="sticky top-0 z-10 bg-card">
              <TableRow className="hover:bg-transparent">
                {WORK_COLUMNS.map((column) => (
                  <TableHead
                    key={column}
                    className={cn(
                      "h-8 whitespace-nowrap px-3 text-xs font-medium text-muted-foreground",
                      ["Wall clock", "Cores held", "Compute units"].includes(column) && "text-right",
                    )}
                  >
                    {column}
                  </TableHead>
                ))}
              </TableRow>
            </TableHeader>
            <TableBody>
              {work.isPending && Array.from({ length: 4 }, (_, i) => (
                <TableRow key={`skeleton-${i}`}>
                  {WORK_COLUMNS.map((column) => <TableCell key={column} className="px-3 py-2"><Skeleton className="h-4 w-full" /></TableCell>)}
                </TableRow>
              ))}
              {work.isSuccess && rows.length === 0 && (
                <TableRow className="hover:bg-transparent">
                  <TableCell colSpan={WORK_COLUMNS.length} className="whitespace-normal border-0 p-0">
                    <EmptyState
                      icon={<ListTree />}
                      title="No work is listed here"
                      description={folded
                        ? "The work of this part of the period was folded into its days' totals, or none ran."
                        : "No node did work for this part of the period."}
                      data-testid="compute-usage-work-empty"
                    />
                  </TableCell>
                </TableRow>
              )}
              {rows.map((row) => <WorkRow key={row.usageId} work={row} />)}
            </TableBody>
          </Table>
        </div>
      </Card>
      {work.hasNextPage && (
        <div>
          <Button variant="outline" size="sm" disabled={work.isFetchingNextPage} onClick={() => void work.fetchNextPage()} data-testid="compute-usage-work-more">
            {work.isFetchingNextPage && <Loader2 className="animate-spin" />}
            Show {WORK_PAGE} older
          </Button>
        </div>
      )}
    </div>
  );
}

/** Which folder of which repository names which team, as each repository's last sync recorded it. */
function TeamTags() {
  const teams = useQuery({ queryKey: [...COMPUTE_USAGE_KEY, "teams"], queryFn: computeUsageApi.teams });
  const columns = ["Repository", "Folder", "Team", "Recorded"];
  return (
    <div className="flex flex-col gap-2">
      <p className="flex items-center gap-1 text-[13px] text-muted-foreground">
        The team each repository document names, as its repository&apos;s last sync read it. A flow takes the nearest one above it.
        <ExplainMark title="How to name a team" text={HOW_TO_NAME_A_TEAM} testId="compute-usage-tags-howto" />
      </p>
      {teams.isError && <p className="text-[13px] text-destructive">{problemText(teams.error)}</p>}
      <Card className="gap-0 overflow-hidden rounded-lg p-0">
        <div className="max-h-[calc(100vh-22rem)] min-h-40 overflow-auto">
          <Table>
            <TableHeader className="sticky top-0 z-10 bg-card">
              <TableRow className="hover:bg-transparent">
                {columns.map((column) => (
                  <TableHead key={column} className="h-8 whitespace-nowrap px-3 text-xs font-medium text-muted-foreground">{column}</TableHead>
                ))}
              </TableRow>
            </TableHeader>
            <TableBody>
              {teams.isPending && Array.from({ length: 3 }, (_, i) => (
                <TableRow key={`skeleton-${i}`}>
                  {columns.map((column) => <TableCell key={column} className="px-3 py-2"><Skeleton className="h-4 w-full" /></TableCell>)}
                </TableRow>
              ))}
              {teams.isSuccess && teams.data.length === 0 && (
                <TableRow className="hover:bg-transparent">
                  <TableCell colSpan={columns.length} className="whitespace-normal border-0 p-0">
                    <EmptyState
                      icon={<Tags />}
                      title="No repository names a team yet"
                      description={(
                        <>
                          Add <code className="font-mono text-[12px]">team: &lt;name&gt;</code> to a repository&apos;s{" "}
                          <code className="font-mono text-[12px]">osdu-delivery.yaml</code> and sync it. Until then every piece of work is
                          charged to no team.
                        </>
                      )}
                      data-testid="compute-usage-tags-empty"
                    />
                  </TableCell>
                </TableRow>
              )}
              {teams.data?.map((tag) => (
                <TableRow key={`${tag.repoId}/${tag.folder}`} data-testid="compute-usage-tag-row">
                  <TableCell className="max-w-64 truncate px-3 py-1.5 text-[13px]">
                    {tag.repo ?? <span className="text-muted-foreground" title={tag.repoId}>no longer registered</span>}
                  </TableCell>
                  <TableCell className="max-w-80 truncate px-3 py-1.5 font-mono text-[12.5px]" title={tag.folder}>
                    {tag.folder === "" ? <span className="font-sans text-[13px] text-muted-foreground">the repository&apos;s root</span> : tag.folder}
                  </TableCell>
                  <TableCell className="max-w-64 truncate px-3 py-1.5 text-[13px] font-medium">{tag.team}</TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]"><RelativeTime value={tag.updatedUtc} /></TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      </Card>
    </div>
  );
}

/**
 * Compute usage (osdu/docs/reference/concepts/compute-usage.md): how much compute the nodes spent in a period, in
 * compute units, a day at a time, and how it divides over the teams the repositories name, their repositories, project
 * folders and flows. Any row of the division opens the work behind it, piece by piece, with every value its units were
 * worked out from; the division downloads as a file to divide a cost by. An admin's page: the control plane refuses its
 * reads to anyone else.
 */
export default function ComputeUsagePage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const period = periodOf(searchParams.get("period"));
  const asked = searchParams.get("tab");
  const tab: UsageTab = TABS.find((each) => each === asked) ?? "division";
  const filter = filterOf(searchParams);
  const [term, setTerm] = useState("");
  // The days are settled when the period is picked, so a page left open over midnight keeps the period it shows.
  const range = useMemo(() => periodRange(period, new Date()), [period]);
  const usage = useQuery({
    queryKey: [...COMPUTE_USAGE_KEY, "summary", range.from, range.to],
    queryFn: () => computeUsageApi.summary(range.from, range.to),
  });

  // One edit of the address per action, made from the address as it is then, so two quick clicks never undo each other.
  const edit = (change: (next: URLSearchParams) => void) => setSearchParams((current) => {
    const next = new URLSearchParams(current);
    change(next);
    return next;
  });
  const clearFilter = (next: URLSearchParams) => {
    for (const name of ["team", "repo", "project", "flow"]) {
      next.delete(name);
    }
  };
  const openWork = (of: DivisionFilter) => edit((next) => {
    clearFilter(next);
    next.set("tab", "work");
    for (const [name, value] of Object.entries(of)) {
      if (value !== undefined) {
        next.set(name, value);
      }
    }
  });

  const data = usage.data;
  const days = useMemo(() => (data === undefined ? [] : daysOf(data.from, data.to, data.days)), [data]);
  const unassigned = data === undefined ? 0 : unassignedUnits(data.shares);
  const assigned = data === undefined ? null : shareOf(data.totals.computeUnits - unassigned, data.totals.computeUnits);
  const teams = data === undefined ? 0 : new Set(data.shares.filter((share) => share.team !== "").map((share) => share.team)).size;

  return (
    <Page data-testid="page-delivery-compute-usage">
      <PageHeader
        title="Compute usage"
        subtitle="What the nodes' work came to, and which team it is charged to. One compute unit is one core held for one hour."
        actions={(
          <>
            {/* The search narrows the division, so it is there only while a division is. */}
            {tab === "division" && data !== undefined && data.shares.length > 0 && (
              <SearchInput value={term} onChange={setTerm} placeholder="Team, repository, flow" label="Search the division" testId="compute-usage-search" />
            )}
            <Select value={period} onValueChange={(value) => edit((next) => next.set("period", value as PeriodId))}>
              <SelectTrigger size="sm" className="h-8 w-36" aria-label="Period" data-testid="compute-usage-period"><SelectValue /></SelectTrigger>
              <SelectContent>
                {PERIODS.map((each) => <SelectItem key={each.id} value={each.id}>{each.label}</SelectItem>)}
              </SelectContent>
            </Select>
            <Tooltip>
              <TooltipTrigger asChild>
                <span className="inline-flex">
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={data === undefined || data.shares.length === 0}
                    onClick={() => { if (data !== undefined) { download(`compute-usage-${data.from}-to-${data.to}.csv`, divisionCsv(data.shares)); } }}
                    data-testid="compute-usage-download"
                  >
                    <Download />
                    Download CSV
                  </Button>
                </span>
              </TooltipTrigger>
              <TooltipContent className="max-w-xs">
                The period&apos;s division as a file: one line per flow and team with its work, wall-clock time, compute units and share.
              </TooltipContent>
            </Tooltip>
          </>
        )}
      />

      {usage.isError && <p className="text-[13px] text-destructive" data-testid="compute-usage-error">{problemText(usage.error)}</p>}

      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        {data === undefined
          ? Array.from({ length: 4 }, (_, i) => <Skeleton key={i} className="h-[88px] rounded-lg" />)
          : (
            <>
              <KpiCard label="Compute units" value={formatUnits(data.totals.computeUnits)} caption={`${data.from} to ${data.to}`} testId="compute-usage-kpi-units" />
              <KpiCard label="Wall clock" value={formatWallClock(data.totals.wallClockMs)} caption="the time nodes held work" testId="compute-usage-kpi-time" />
              <KpiCard label="Pieces of work" value={data.totals.work.toLocaleString()} caption="run attempts and compute tasks" testId="compute-usage-kpi-work" />
              <KpiCard
                label="Charged to a team"
                value={formatShare(assigned)}
                caption={data.totals.work === 0 ? "nothing ran" : `${teams.toLocaleString()} ${teams === 1 ? "team" : "teams"} in the period`}
                testId="compute-usage-kpi-assigned"
              />
            </>
          )}
      </div>

      {data !== undefined && data.totals.work > 0 && (
        <Card className="gap-2 rounded-lg p-4">
          <div className="flex items-center gap-1">
            <h2 className="text-[13px] font-medium">Compute units a day</h2>
            <ExplainMark title="How it is counted" text={HOW_IT_IS_COUNTED} testId="compute-usage-how" />
          </div>
          <DayChart days={days} />
        </Card>
      )}

      <Tabs value={tab} onValueChange={(value) => edit((next) => next.set("tab", value))}>
        <TabsList data-testid="compute-usage-tabs">
          <TabsTrigger value="division" data-testid="compute-usage-tab-division">Division</TabsTrigger>
          <TabsTrigger value="work" data-testid="compute-usage-tab-work">Work</TabsTrigger>
          <TabsTrigger value="teams" data-testid="compute-usage-tab-teams">Team tags</TabsTrigger>
        </TabsList>

        <TabsContent value="division" className="mt-3">
          {data === undefined && !usage.isError && <Skeleton className="h-40 rounded-lg" />}
          {data !== undefined && data.totals.work === 0 && (
            <EmptyState
              icon={<Gauge />}
              title="No node did work in this period"
              description="Every run and compute task a node finishes is measured from the moment the node is handed it. Pick a longer period, or run a flow."
              data-testid="compute-usage-empty"
            />
          )}
          {data !== undefined && data.totals.work > 0 && <Division usage={data} term={term} onWork={openWork} />}
        </TabsContent>

        <TabsContent value="work" className="mt-3">
          <WorkList range={range} filter={filter} usage={data} onClear={() => edit(clearFilter)} />
        </TabsContent>

        <TabsContent value="teams" className="mt-3">
          <TeamTags />
        </TabsContent>
      </Tabs>
    </Page>
  );
}
