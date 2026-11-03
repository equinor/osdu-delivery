import { memo, useEffect, useRef, useState, type KeyboardEvent, type MouseEvent, type ReactNode } from "react";
import { Link } from "react-router-dom";
import {
  ArrowDown, ArrowUpRight, ChevronRight, ChevronsDownUp, ChevronsUpDown, CircleAlert, FileText, FileWarning, Play, SkipForward, type LucideIcon,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { CopyButton } from "@/components/CopyButton";
import { LineageJumpButton } from "@/components/LineageJumpButton";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import type { DeliveryAssertionFlow, DeliveryAssertionRun, DeliveryAssertionTest } from "../../../api/delivery";
import { splitKind } from "../templateFormat";
import { useWindowFit } from "../useWindowFit";
import { OutcomeBar, TestOutcomeIcon } from "./AssertionBadges";
import { OUTCOME_VISUALS, STANDING_TEXT, counted, setRoute, standingOf, testVerdict, type OutcomeTone, type TestStanding } from "./assertionFormat";
import { tallyText, type BoardEntry, type BoardSort, type FlowTally, type SetEntry } from "./assertionBoardModel";

/**
 * The grid's columns, one template for every row so a set's, a flow's and a test's facts line up: the pick or the opener,
 * the name, the type, how the tests stand, when they were last checked, and the actions. The grid drops the type, then the
 * time, as it narrows. A board of one flow that reads one type names it once above the grid, and leaves the column out.
 */
const WITH_TYPE = "grid grid-cols-[2rem_minmax(0,1.2fr)_minmax(0,1fr)_7.5rem] @2xl/board:grid-cols-[2rem_minmax(0,1.4fr)_minmax(0,1.2fr)_8rem_7.5rem] @4xl/board:grid-cols-[2rem_minmax(0,1.7fr)_minmax(0,0.7fr)_minmax(0,1.2fr)_8rem_7.5rem]";
const WITHOUT_TYPE = "grid grid-cols-[2rem_minmax(0,1.2fr)_minmax(0,1fr)_7.5rem] @2xl/board:grid-cols-[2rem_minmax(0,1.4fr)_minmax(0,1.2fr)_8rem_7.5rem]";

const TYPE_CELL = "hidden min-w-0 items-center @4xl/board:flex";
const RUN_CELL = "hidden min-w-0 items-center @2xl/board:flex";

/**
 * The rows' heights, in pixels and borders included, which the grid lays its sets and flows out by: it renders the rows in
 * and near view and stands in for the rest with their height, so the scroll bar is exact however many are open.
 */
const HEADING_PX = 32;
const SET_PX = 40;
const FLOW_PX = 36;
const TEST_PX = 32;
const NOTE_PX = 36;

/** How far beyond the visible rows the grid renders, in pixels, so a scroll or a step of the arrow keys finds rows there. */
const OVERSCAN_PX = 480;

/** What stays under the grid's rows: its footer line, the page's padding and the workbench's status bar. */
const BELOW_GRID = 84;

/** The least height the grid keeps, so a short window still shows a few rows. */
const MIN_GRID_HEIGHT = 260;

/**
 * A set's and a flow's rows are opaque, so the rows they stick above while those scroll do not show through them: the set
 * wears the stronger wash, so the three levels read apart.
 */
const SET_SURFACE = "bg-[color-mix(in_oklab,var(--muted)_85%,var(--card))]";
const FLOW_SURFACE = "bg-[color-mix(in_oklab,var(--muted)_45%,var(--card))]";

const TONE_TEXT: Record<OutcomeTone, string> = {
  success: "text-success",
  destructive: "text-destructive",
  warning: "text-warning",
  info: "text-info",
  muted: "text-muted-foreground",
};

const SORT_HINTS: Record<BoardSort, string> = {
  status: "What needs a look first: failed assertions, then errored, warned, unfit and not run, then passing; the set or flow with more of them first",
  name: "By name",
  run: "The most recently checked first",
};

/** A secondary action of a row: quiet until it is pointed at. */
const QUIET_ACTION = "[&_button]:text-muted-foreground [&_button:hover]:text-foreground [&_a]:text-muted-foreground [&_a:hover]:text-foreground";

/** A click inside a row's control cell acts on the control alone, never on the row. */
const stop = (event: MouseEvent) => event.stopPropagation();

/** The flow's own facts, read on hover of its name: what it is for and what fails a run of it. */
function flowAbout(flow: DeliveryAssertionFlow): string {
  const fails = flow.failRunOn === "never" ? "A run of it never fails on its assertions; it only reports." : flow.failRunOn === "warning" ? "A run fails on a warning or worse." : "A run fails on a failed or errored assertion.";
  return `${flow.description ?? "Assertions about what OSDU holds."}\n${fails}`;
}

/** The kinds some flows' tests read, in the order their documents first name them. */
function kindsOf(flows: readonly DeliveryAssertionFlow[]): string[] {
  return [...new Set(flows.flatMap((flow) => flow.tests.map((test) => test.kind)))];
}

/**
 * A kind as a column of a hundred of them reads best: the entity and its version, which tell two kinds apart; the whole
 * kind, with its authority, source and group, on hover.
 */
export function KindName({ kind }: { kind: string }) {
  const { entity, version } = splitKind(kind);
  return (
    <RichTooltip body={kind} title="Kind" mono>
      <span className="min-w-0 truncate text-[12px]">
        {entity}
        {version !== "" && <span className="text-muted-foreground"> {version.replace(/^:/, "")}</span>}
      </span>
    </RichTooltip>
  );
}

/** The types some flows read: the one there is, or how many, each named on hover. */
function KindsCell({ kinds }: { kinds: readonly string[] }) {
  if (kinds.length === 1) {
    return <KindName kind={kinds[0]!} />;
  }

  return kinds.length > 1
    ? (
      <RichTooltip body={kinds.join("\n")} title="Types" mono>
        <span className="text-[12px]">{kinds.length} types</span>
      </RichTooltip>
    )
    : null;
}

/** One count of a tally: the standing's glyph in its tone, the count, and the word after it. */
function TallyPart({ standing, count }: { standing: TestStanding; count: number }) {
  const visual = OUTCOME_VISUALS[standing];
  const Icon = visual.icon;
  return (
    <span className="inline-flex h-5 items-center gap-1 whitespace-nowrap text-[12px]" data-testid={`board-tally-${standing}`}>
      <Icon className={cn("size-3.5 shrink-0", TONE_TEXT[visual.tone])} aria-hidden />
      <span className="font-mono tabular-nums">{count.toLocaleString("en-US")}</span>
      <span className="text-muted-foreground">{visual.label}</span>
    </span>
  );
}

/**
 * How tests stand: the shares as a bar, then each standing there is, worst first. The counts wrap onto a line the cell
 * hides, so a narrow cell drops whole counts from the end rather than cutting one; every count is on hover.
 */
function TallyCell({ tally, testId }: { tally: FlowTally; testId: string }) {
  const parts: [TestStanding, number][] = [
    ["failed", tally.failed], ["errored", tally.errored], ["warned", tally.warned], ["notRun", tally.notRun], ["passed", tally.passed], ["disabled", tally.disabled],
  ];
  return (
    <RichTooltip body={tallyText(tally)} title="Assertions">
      <div className="flex min-w-0 items-center gap-3" data-testid={testId}>
        <OutcomeBar
          counts={{ passed: tally.passed, warned: tally.warned, failed: tally.failed, errored: tally.errored, skipped: tally.notRun, disabled: tally.disabled }}
          className="h-1.5 w-12 shrink-0"
        />
        <span className="flex h-5 min-w-0 flex-wrap items-center gap-x-3 overflow-hidden">
          {parts.filter(([, count]) => count > 0).map(([standing, count]) => <TallyPart key={standing} standing={standing} count={count} />)}
          {tally.tests === 0 && tally.elsewhere > 0 && (
            <span className="h-5 whitespace-nowrap text-[12px] leading-5 text-muted-foreground">{counted(tally.elsewhere, "assertion")} of other partitions</span>
          )}
        </span>
      </div>
    </RichTooltip>
  );
}

/** A mark beside a name: what keeps some of its tests from being trusted, counted when it is a set's or a flow's. */
function Mark({ icon: Icon, tone, hint, count, testId }: { icon: LucideIcon; tone: string; hint: string; count?: number; testId: string }) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className={cn("inline-flex shrink-0 items-center gap-0.5 font-mono text-[11px] tabular-nums", tone)} aria-label={hint} data-testid={testId}>
          <Icon className="size-3.5" aria-hidden />
          {count !== undefined && count}
        </span>
      </TooltipTrigger>
      <TooltipContent className="max-w-md">{hint}</TooltipContent>
    </Tooltip>
  );
}

/** The unfit mark of a set or a flow: how many of its tests do not fit the schema of their type. */
function UnfitMark({ count, testId }: { count: number; testId: string }) {
  return count > 0
    ? (
      <Mark
        icon={FileWarning}
        tone="text-destructive"
        count={count}
        hint={`${counted(count, "assertion")} ${count === 1 ? "does" : "do"} not fit the schema of ${count === 1 ? "its" : "their"} type, and so ${count === 1 ? "is" : "are"} not evaluated`}
        testId={testId}
      />
    )
    : null;
}

/** A run button that says why it cannot run: a disabled button takes no hover, so its tooltip hangs off a wrapper. */
function RunControl({ label, disabledWhy, onRun, children, testId }: {
  label: string;
  disabledWhy: string | null;
  onRun: () => void;
  children: ReactNode;
  testId: string;
}) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="inline-flex">
          <Button variant="ghost" size="icon-xs" onClick={onRun} disabled={disabledWhy !== null} aria-label={label} data-testid={testId}>
            {children}
          </Button>
        </span>
      </TooltipTrigger>
      <TooltipContent>{disabledWhy ?? label}</TooltipContent>
    </Tooltip>
  );
}

/** When a flow's last run checked it, with that run's report a click away; nothing when it has not run. */
function CheckedCell({ run, testId }: { run: DeliveryAssertionRun | null; testId: string }) {
  return (
    <div role="gridcell" className={cn(RUN_CELL, "gap-1.5 text-[12px] text-muted-foreground")} data-testid={testId}>
      {run !== null && (
        <>
          <Link to={`/delivery/assertions/runs/${run.assertionRunId}`} onClick={stop} className="font-mono hover:text-foreground hover:underline">#{run.assertionRunId}</Link>
          <span className="truncate"><RelativeTime value={run.completedUtc ?? run.startedUtc} absolute={false} /></span>
        </>
      )}
    </div>
  );
}

/** Moves focus to the row before or after the focused one, within the grid. */
function stepFocus(event: KeyboardEvent<HTMLElement>) {
  if (event.key !== "ArrowDown" && event.key !== "ArrowUp") {
    return;
  }

  const rows = [...event.currentTarget.querySelectorAll<HTMLElement>("[data-row]")];
  const at = rows.indexOf(document.activeElement as HTMLElement);
  if (at < 0) {
    return;
  }

  event.preventDefault();
  rows[Math.max(0, Math.min(rows.length - 1, at + (event.key === "ArrowDown" ? 1 : -1)))]?.focus();
}

/** The indent of a row's name by how deep it sits: a flow under its set, a test under its flow. */
const INDENT = ["", "pl-4", "pl-8"];

/**
 * One test on one line: where it stands now, its verdict (why it is disabled, in its own words, when it is), the type it
 * reads when its flow reads several, when it was last checked, and a way to run it again for how the data stands now.
 */
const TestRow = memo(function TestRow({ flow, test, columns, showKind, depth, picked, onPick, onOpen, onLaunch }: {
  flow: DeliveryAssertionFlow;
  test: DeliveryAssertionTest;
  columns: string;
  /** Whether the type cell names the test's kind; a grid with the column keeps the cell, empty, so the cells line up. */
  showKind: boolean;
  /** How deep the row sits: 0 on a flow's own board, 1 under a flow, 2 under a flow under its set. */
  depth: number;
  picked: boolean;
  onPick: (flowId: string, test: string, on: boolean) => void;
  onOpen: (flow: DeliveryAssertionFlow, test: DeliveryAssertionTest) => void;
  onLaunch: (flow: DeliveryAssertionFlow, tests: readonly string[]) => void;
}) {
  const standing = standingOf(test);
  const verdict = testVerdict(test);
  const checked = test.latest !== null && !test.changed && standing !== "disabled" ? test.latest : null;
  const runnable = test.runsHere && flow.problem === null;

  const keys = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.target !== event.currentTarget) {
      return;
    }

    if (event.key === "Enter") {
      event.preventDefault();
      onOpen(flow, test);
    } else if (event.key === "ArrowLeft" && depth > 0) {
      event.preventDefault();
      event.currentTarget.closest("[data-flow-group]")?.querySelector<HTMLElement>("[data-row=flow]")?.focus();
    }
  };

  return (
    <div
      role="row"
      aria-level={depth + 1}
      tabIndex={0}
      data-row="test"
      onClick={() => onOpen(flow, test)}
      onKeyDown={keys}
      className={cn(
        columns,
        "group h-[32px] cursor-pointer items-center gap-x-3 border-b border-border/60 px-2 outline-none transition-colors hover:bg-accent/50 focus-visible:bg-accent/50",
        standing === "elsewhere" && "opacity-55",
      )}
      data-testid={`board-test-${test.name}`}
      data-standing={standing}
    >
      <div role="gridcell" className="flex justify-center" onClick={stop}>
        <Checkbox
          checked={picked}
          onCheckedChange={(next) => onPick(flow.pipelineId, test.name, next === true)}
          disabled={!runnable}
          aria-label={`Pick ${test.name} to run`}
          className={cn(!picked && "opacity-40 group-hover:opacity-100 group-focus-visible:opacity-100")}
          data-testid={`board-test-${test.name}-pick`}
        />
      </div>
      <div role="gridcell" className={cn("flex min-w-0 items-center gap-1.5", INDENT[depth])}>
        <TestOutcomeIcon outcome={standing} />
        {test.description !== null
          ? <RichTooltip body={test.description}><span className="truncate font-mono text-[12.5px]">{test.name}</span></RichTooltip>
          : <span className="truncate font-mono text-[12.5px]">{test.name}</span>}
        {test.problems.length > 0 && (
          <Mark icon={FileWarning} tone="text-destructive" hint={`It does not fit the schema of its type: ${test.problems.join(" ")}`} testId="board-test-problems" />
        )}
      </div>
      {columns === WITH_TYPE && <div role="gridcell" className={TYPE_CELL}>{showKind && <KindName kind={test.kind} />}</div>}
      <div role="gridcell" className="min-w-0" data-testid="board-test-verdict">
        <RichTooltip body={verdict.text}>
          <span className={cn("block truncate text-[12px]", STANDING_TEXT[verdict.standing] ?? "text-muted-foreground")}>{verdict.text}</span>
        </RichTooltip>
      </div>
      <div role="gridcell" className={cn(RUN_CELL, "text-[11.5px] text-muted-foreground")} data-testid="board-test-checked">
        {checked !== null && <span className="truncate"><RelativeTime value={checked.completedUtc} absolute={false} /></span>}
      </div>
      <div role="gridcell" className="flex justify-end" onClick={stop}>
        <RunControl
          label={`Run ${test.name}`}
          disabledWhy={runnable ? null : test.runsHere ? "Its flow cannot run in this partition" : "It does not check this partition"}
          onRun={() => onLaunch(flow, [test.name])}
          testId={`board-test-${test.name}-run`}
        >
          <Play />
        </RunControl>
      </div>
    </div>
  );
});

/** Why a flow cannot run its tests here, or null when it can. */
function whyNotRunnable(entry: BoardEntry): string | null {
  switch (entry.state) {
    case "broken":
      return "Its document does not parse";
    case "elsewhere":
      return "It does not check this partition";
    case "empty":
      return "It declares no assertion";
    default:
      return entry.tally.tests === 0 ? "None of its assertions checks this partition" : null;
  }
}

/** The line an opened flow with no test to list shows: why, on one line, whole on hover and with a way to copy it. */
function FlowNote({ entry, depth }: { entry: BoardEntry; depth: number }) {
  const { flow, state } = entry;
  const text = flow.problem ?? "The flow declares no assertion.";
  return (
    <div role="row" aria-level={depth + 2} className={cn("flex h-[36px] items-center gap-2 border-b border-border/60 pr-3 text-[12.5px]", depth > 0 ? "pl-16" : "pl-12")} data-testid="board-flow-problem">
      {state === "broken"
        ? <CircleAlert className="size-4 shrink-0 text-destructive" aria-hidden />
        : <SkipForward className="size-4 shrink-0 text-muted-foreground" aria-hidden />}
      <RichTooltip body={text}>
        <span className={cn("min-w-0 flex-1 truncate", state === "broken" ? "text-destructive" : "text-muted-foreground")}>{text}</span>
      </RichTooltip>
      {flow.problem !== null && <CopyButton label="Copy why" text={flow.problem} testId="board-flow-problem-copy" iconOnly />}
    </div>
  );
}

/** How tall a flow lays out: its row, and while it is open, its tests or the line saying why it lists none. */
function groupHeight(entry: BoardEntry, open: boolean): number {
  if (!open) {
    return FLOW_PX;
  }

  return FLOW_PX + (entry.state === "tested" ? entry.tests.length * TEST_PX : NOTE_PX);
}

/**
 * One flow and, while it is open, its tests. Its row says what the flow reads, how its tests stand now, its last run, and
 * how to run them; it opens and closes on a click, Enter or the arrow keys, and sticks under its set's row while its tests
 * scroll by. A flow with no test to list opens on why.
 */
const FlowGroup = memo(function FlowGroup({ entry, open, repoName, columns, picked, onToggle, onPick, onOpen, onLaunch }: {
  entry: BoardEntry;
  open: boolean;
  /** The name of the repository the flow is synced from, as the lineage jump names its graph. */
  repoName: string;
  columns: string;
  picked: ReadonlySet<string>;
  onToggle: (flowId: string) => void;
  onPick: (flowId: string, test: string, on: boolean) => void;
  onOpen: (flow: DeliveryAssertionFlow, test: DeliveryAssertionTest) => void;
  onLaunch: (flow: DeliveryAssertionFlow, tests: readonly string[]) => void;
}) {
  const { flow, state, tally } = entry;
  const kinds = kindsOf([flow]);
  const why = whyNotRunnable(entry);
  const hidden = flow.tests.length - entry.tests.length;
  // A pick of a test the flow no longer declares, after its document changed, is not run.
  const pickedNames = flow.tests.filter((test) => test.runsHere && picked.has(test.name)).map((test) => test.name);

  const keys = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.target !== event.currentTarget) {
      return;
    }

    if (event.key === "Enter" || event.key === " " || (event.key === "ArrowRight" && !open) || (event.key === "ArrowLeft" && open)) {
      event.preventDefault();
      onToggle(flow.pipelineId);
    } else if (event.key === "ArrowLeft") {
      event.preventDefault();
      event.currentTarget.closest("[data-set-group]")?.querySelector<HTMLElement>("[data-row=set]")?.focus();
    }
  };

  return (
    <div role="rowgroup" data-flow-group={flow.pipelineId}>
      <div
        role="row"
        aria-level={2}
        aria-expanded={open}
        tabIndex={0}
        data-row="flow"
        data-flow={flow.pipelineId}
        onClick={() => onToggle(flow.pipelineId)}
        onKeyDown={keys}
        className={cn(
          columns,
          FLOW_SURFACE,
          "h-[36px] cursor-pointer items-center gap-x-3 border-b px-2 outline-none transition-colors hover:bg-accent focus-visible:bg-accent",
          open && "sticky top-[72px] z-[2]",
        )}
        data-testid={`board-flow-${flow.name}`}
        data-state={state}
      >
        <div role="gridcell" aria-hidden />
        <div role="gridcell" className={cn("flex min-w-0 items-center gap-2", INDENT[1])}>
          <ChevronRight className={cn("size-4 shrink-0 text-muted-foreground transition-transform duration-150", open && "rotate-90")} aria-hidden />
          <RichTooltip body={flowAbout(flow)} title={flow.name}>
            <span className={cn("truncate font-mono text-[12.5px] font-medium", state === "elsewhere" && "text-muted-foreground")}>{flow.name}</span>
          </RichTooltip>
          <UnfitMark count={tally.unfit} testId="board-flow-unfit" />
          {hidden > 0 && entry.tests.length > 0 && (
            <span className="shrink-0 font-mono text-[11px] tabular-nums text-muted-foreground" data-testid="board-flow-shown">
              {entry.tests.length} of {flow.tests.length}
            </span>
          )}
        </div>
        <div role="gridcell" className={cn(TYPE_CELL, "text-muted-foreground")}>
          <KindsCell kinds={kinds} />
        </div>
        <div role="gridcell" className="flex min-w-0 items-center">
          {state === "tested" && <TallyCell tally={tally} testId="board-flow-tally" />}
          {state === "broken" && (
            <span className="flex min-w-0 items-center gap-1.5 text-[12px] text-destructive">
              <CircleAlert className="size-3.5 shrink-0" aria-hidden />
              <span className="truncate">its document does not parse</span>
            </span>
          )}
          {state === "elsewhere" && (
            <span className="flex min-w-0 items-center gap-1.5 text-[12px] text-muted-foreground">
              <SkipForward className="size-3.5 shrink-0" aria-hidden />
              <span className="truncate">{flow.partitions.length > 0 ? `checks ${flow.partitions.join(", ")}` : "not this partition"}</span>
            </span>
          )}
          {state === "empty" && <span className="text-[12px] text-muted-foreground">declares no assertion</span>}
        </div>
        <CheckedCell run={flow.lastRun} testId="board-flow-last-run" />
        <div role="gridcell" className="flex items-center justify-end gap-0.5" onClick={stop}>
          <span className={cn("flex items-center gap-0.5", QUIET_ACTION)}>
            <LineageJumpButton
              target={{ kind: "node", repoId: flow.repoId, repoName, focusId: flow.pipelineId, label: flow.name, sublabel: flow.description ?? undefined }}
              iconOnly
            />
            <Tooltip>
              <TooltipTrigger asChild>
                <Button asChild variant="ghost" size="icon-xs">
                  <Link to={`/pipelines/${flow.pipelineId}?tab=tests`} aria-label={`Open ${flow.name}`} data-testid="board-flow-open">
                    <ArrowUpRight />
                  </Link>
                </Button>
              </TooltipTrigger>
              <TooltipContent>Open the flow: its assertions and its reports</TooltipContent>
            </Tooltip>
          </span>
          {pickedNames.length > 0
            ? (
              <Button size="xs" onClick={() => onLaunch(flow, pickedNames)} disabled={why !== null} data-testid="board-flow-run-picked">
                <Play />
                Run {pickedNames.length}
              </Button>
            )
            : (
              <RunControl label="Run every assertion of the flow" disabledWhy={why} onRun={() => onLaunch(flow, [])} testId="board-flow-run-all">
                <Play />
              </RunControl>
            )}
        </div>
      </div>
      {open && (state === "tested"
        ? entry.tests.map((test) => (
          <TestRow
            key={test.name}
            flow={flow}
            test={test}
            columns={columns}
            showKind={kinds.length > 1}
            depth={2}
            picked={picked.has(test.name)}
            onPick={onPick}
            onOpen={onOpen}
            onLaunch={onLaunch}
          />
        ))
        : <FlowNote entry={entry} depth={1} />)}
    </div>
  );
});

/** Where the rows are scrolled to and how much of them shows, which decides the rows the grid renders. */
interface Viewport {
  top: number;
  height: number;
}

/** The flows of a set to render between two heights, and the height of those above and below, which stand in for them. */
function windowOf(entries: readonly BoardEntry[], isOpen: (entry: BoardEntry) => boolean, from: number, to: number, start: number) {
  const shown: { entry: BoardEntry; open: boolean }[] = [];
  let above = 0;
  let below = 0;
  let y = start;
  for (const entry of entries) {
    const open = isOpen(entry);
    const height = groupHeight(entry, open);
    if (y + height < from) {
      above += height;
    } else if (y > to) {
      below += height;
    } else {
      shown.push({ entry, open });
    }

    y += height;
  }

  return { shown, above, below };
}

/**
 * The sets to render for what is in view, each open one with the flows of it to render, and the height of what is above
 * and below, which stands in for it: a board of a hundred flows with every one open is a few thousand rows, and only the
 * few dozen near the view are ever on the page.
 */
function laySets(sets: readonly SetEntry[], isSetOpen: (set: SetEntry) => boolean, isOpen: (entry: BoardEntry) => boolean, view: Viewport) {
  const from = view.top - OVERSCAN_PX;
  const to = view.top + view.height + OVERSCAN_PX;
  const shown: { set: SetEntry; open: boolean; flows: ReturnType<typeof windowOf> | null }[] = [];
  let above = 0;
  let below = 0;
  let y = HEADING_PX;
  for (const set of sets) {
    const open = isSetOpen(set);
    const height = SET_PX + (open ? set.entries.reduce((sum, entry) => sum + groupHeight(entry, isOpen(entry)), 0) : 0);
    if (y + height < from) {
      above += height;
    } else if (y > to) {
      below += height;
    } else {
      shown.push({ set, open, flows: open ? windowOf(set.entries, isOpen, from, to, y + SET_PX) : null });
    }

    y += height;
  }

  return { shown, above, below };
}

/**
 * One set's row: its name and how many flows it holds, the types they read, how its assertions stand now, when it was last
 * checked, its report, and how to run every flow of it. It opens on its flows, and sticks under the column headings while
 * they scroll by. The flows that name no set are a group of their own, with no report and no run of their own.
 */
function SetRow({ set, open, columns, onToggle, onLaunchSet }: {
  set: SetEntry;
  open: boolean;
  columns: string;
  onToggle: (key: string) => void;
  onLaunchSet: (set: SetEntry) => void;
}) {
  const runnable = set.flows.some((flow) => flow.problem === null && flow.tests.some((test) => test.runsHere));
  const keys = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.target !== event.currentTarget) {
      return;
    }

    if (event.key === "Enter" || event.key === " " || (event.key === "ArrowRight" && !open) || (event.key === "ArrowLeft" && open)) {
      event.preventDefault();
      onToggle(set.key);
    }
  };

  return (
    <div
      role="row"
      aria-level={1}
      aria-expanded={open}
      tabIndex={0}
      data-row="set"
      onClick={() => onToggle(set.key)}
      onKeyDown={keys}
      className={cn(
        columns,
        SET_SURFACE,
        "h-[40px] cursor-pointer items-center gap-x-3 border-b px-2 outline-none transition-colors hover:bg-accent focus-visible:bg-accent",
        open && "sticky top-[32px] z-[3]",
      )}
      data-testid={set.set === null ? "board-set-none" : `board-set-${set.set}`}
    >
      <div role="gridcell" className="flex justify-center">
        <ChevronRight className={cn("size-4 text-muted-foreground transition-transform duration-150", open && "rotate-90")} aria-hidden />
      </div>
      <div role="gridcell" className="flex min-w-0 items-baseline gap-2">
        {set.set === null
          ? <span className="truncate text-[13px] font-medium text-muted-foreground">Flows in no set</span>
          : (
            <Link to={setRoute(set.set)} onClick={stop} className="truncate text-[13px] font-semibold hover:underline" data-testid="board-set-name">
              {set.set}
            </Link>
          )}
        <span className="shrink-0 text-[11.5px] text-muted-foreground">{counted(set.flows.length, "flow")}</span>
        <UnfitMark count={set.tally.unfit} testId="board-set-unfit" />
      </div>
      <div role="gridcell" className={cn(TYPE_CELL, "text-muted-foreground")}>
        <KindsCell kinds={kindsOf(set.flows)} />
      </div>
      <div role="gridcell" className="flex min-w-0 items-center">
        <TallyCell tally={set.tally} testId="board-set-tally" />
      </div>
      <div role="gridcell" className={cn(RUN_CELL, "text-[12px] text-muted-foreground")} data-testid="board-set-checked">
        {set.lastRun !== null && <span className="truncate"><RelativeTime value={set.lastRun.completedUtc ?? set.lastRun.startedUtc} absolute={false} /></span>}
      </div>
      <div role="gridcell" className="flex items-center justify-end gap-0.5" onClick={stop}>
        {set.set !== null && (
          <>
            <Tooltip>
              <TooltipTrigger asChild>
                <Button asChild variant="ghost" size="icon-xs">
                  <Link to={setRoute(set.set)} aria-label={`The report of set ${set.set}`} data-testid="board-set-report">
                    <FileText />
                  </Link>
                </Button>
              </TooltipTrigger>
              <TooltipContent>The set's report: where every assertion stands now, what fails and why</TooltipContent>
            </Tooltip>
            <RunControl
              label="Run every flow of the set"
              disabledWhy={runnable ? null : "None of its flows checks this partition"}
              onRun={() => onLaunchSet(set)}
              testId="board-set-run"
            >
              <Play />
            </RunControl>
          </>
        )}
      </div>
    </div>
  );
}

/** A column heading that orders the rows by it; pressed again it stays, since each order has one direction. */
function SortHeading({ sort, current, onSort, className, children }: {
  sort: BoardSort;
  current: BoardSort;
  onSort: (sort: BoardSort) => void;
  className?: string;
  children: ReactNode;
}) {
  const active = current === sort;
  return (
    <div role="columnheader" aria-sort={active ? (sort === "name" ? "ascending" : "descending") : "none"} className={cn("flex min-w-0 items-center", className)}>
      <Tooltip>
        <TooltipTrigger asChild>
          <button
            type="button"
            onClick={() => onSort(sort)}
            className={cn("inline-flex items-center gap-1 rounded-sm outline-none hover:text-foreground focus-visible:text-foreground", active && "text-foreground")}
            data-testid={`board-sort-${sort}`}
          >
            {children}
            <ArrowDown className={cn("size-3", !active && "invisible")} aria-hidden />
          </button>
        </TooltipTrigger>
        <TooltipContent className="max-w-sm">{SORT_HINTS[sort]}</TooltipContent>
      </Tooltip>
    </div>
  );
}

/** The grid's state and what its rows do, as the board holds them. */
export interface BoardGridProps {
  scope: "all" | "flow";
  /** The board of every flow: its sets, each with the flows the criteria leave in view. */
  sets: readonly SetEntry[];
  /** The board of one flow: the flow, with the tests the criteria leave in view. */
  entries: readonly BoardEntry[];
  isSetOpen: (set: SetEntry) => boolean;
  isOpen: (entry: BoardEntry) => boolean;
  /** Whether every set listed is open, which the heading's toggle reverses. */
  allOpen: boolean;
  onToggleAll: () => void;
  sort: BoardSort;
  onSort: (sort: BoardSort) => void;
  /** Changes when what the grid lists changes (the criteria, the order), which scrolls it back to its top. */
  listKey: string;
  repoNames: ReadonlyMap<string, string>;
  picks: ReadonlyMap<string, ReadonlySet<string>>;
  onToggleSet: (key: string) => void;
  onToggle: (flowId: string) => void;
  onPick: (flowId: string, test: string, on: boolean) => void;
  onOpen: (flow: DeliveryAssertionFlow, test: DeliveryAssertionTest) => void;
  onLaunch: (flow: DeliveryAssertionFlow, tests: readonly string[]) => void;
  onLaunchSet: (set: SetEntry) => void;
  /** What the grid shows when nothing is left to list, if anything: a flow that cannot run here says why above it. */
  none: ReactNode;
  /** The line under the rows: how many are shown, and what is picked. */
  footer?: ReactNode;
  /** A board of one flow names it, its last run and its run buttons above the columns. */
  heading?: ReactNode;
}

const NO_PICKS: ReadonlySet<string> = new Set();

/**
 * The board's grid: a column heading that stays in place, then the sets, each one row that opens on its flows, each of
 * those a row that opens on its tests, in a list that is as tall as the window leaves and scrolls inside itself, so a
 * hundred flows and their thousand tests never make the page itself long. A board of one flow lists its tests directly.
 */
export function AssertionBoardGrid(props: BoardGridProps) {
  const {
    scope, sets, entries, isSetOpen, isOpen, allOpen, onToggleAll, sort, onSort, listKey, repoNames, picks, onToggleSet, onToggle, onPick, onOpen,
    onLaunch, onLaunchSet, none, footer, heading,
  } = props;
  const scroller = useRef<HTMLDivElement>(null);
  const [view, setView] = useState<Viewport>(() => ({ top: 0, height: window.innerHeight }));
  useWindowFit(scroller, BELOW_GRID, MIN_GRID_HEIGHT);

  // What is in view follows the scroll and the grid's own height, read once a frame.
  useEffect(() => {
    const element = scroller.current;
    if (element === null) {
      return undefined;
    }

    let frame = 0;
    const read = () => {
      window.cancelAnimationFrame(frame);
      frame = window.requestAnimationFrame(() => {
        setView((was) => was.top === element.scrollTop && was.height === element.clientHeight ? was : { top: element.scrollTop, height: element.clientHeight });
      });
    };

    read();
    element.addEventListener("scroll", read, { passive: true });
    const observer = new ResizeObserver(read);
    observer.observe(element);
    return () => {
      window.cancelAnimationFrame(frame);
      element.removeEventListener("scroll", read);
      observer.disconnect();
    };
  }, []);

  // A new list starts at its top: rows scrolled to under other criteria say nothing of the new ones.
  useEffect(() => {
    if (scroller.current !== null) {
      scroller.current.scrollTop = 0;
    }
  }, [listKey]);

  const single = scope === "flow" ? entries[0] : undefined;
  const singleKinds = single === undefined ? [] : kindsOf([single.flow]);
  const columns = scope === "all" || singleKinds.length > 1 ? WITH_TYPE : WITHOUT_TYPE;
  const empty = single === undefined ? sets.length === 0 : single.tests.length === 0;
  // A flow that cannot run here says why above the grid, and lists nothing under it: no headings over no rows.
  const bare = empty && (none === null || none === undefined || none === false);
  const laid = scope === "all" ? laySets(sets, isSetOpen, isOpen, view) : null;

  return (
    <div className="@container/board flex min-w-0 flex-col overflow-hidden rounded-lg border bg-card" data-testid="assertion-grid">
      {heading}
      <div
        ref={scroller}
        role="treegrid"
        aria-label={scope === "all" ? "Assertion sets, their flows and their assertions" : "The flow's assertions"}
        aria-rowcount={-1}
        onKeyDown={stepFocus}
        className="min-h-0 overflow-y-auto overscroll-contain"
        data-testid="assertion-grid-rows"
      >
        {!bare && (
          <div
            role="row"
            className={cn(columns, "sticky top-0 z-[4] h-[32px] items-center gap-x-3 border-b bg-card px-2 text-xs font-medium text-muted-foreground")}
          >
            <div role="columnheader" className="flex justify-center">
              {scope === "all" && sets.length > 0 && (
                <Tooltip>
                  <TooltipTrigger asChild>
                    <Button variant="ghost" size="icon-xs" onClick={onToggleAll} aria-label={allOpen ? "Close every set" : "Open every set"} data-testid="assertion-board-expand">
                      {allOpen ? <ChevronsDownUp /> : <ChevronsUpDown />}
                    </Button>
                  </TooltipTrigger>
                  <TooltipContent>{allOpen ? "Close every set" : "Open every set"}</TooltipContent>
                </Tooltip>
              )}
            </div>
            {scope === "all"
              ? <SortHeading sort="name" current={sort} onSort={onSort}>Set and flow</SortHeading>
              : <div role="columnheader">Assertion</div>}
            {columns === WITH_TYPE && <div role="columnheader" className={TYPE_CELL}>Type</div>}
            {scope === "all"
              ? <SortHeading sort="status" current={sort} onSort={onSort}>Results</SortHeading>
              : <div role="columnheader">Result</div>}
            {scope === "all"
              ? <SortHeading sort="run" current={sort} onSort={onSort} className={RUN_CELL}>Last checked</SortHeading>
              : <div role="columnheader" className={RUN_CELL}>Checked</div>}
            <div role="columnheader" className="sr-only">Actions</div>
          </div>
        )}
        {empty
          ? none
          : laid !== null
            ? (
              <>
                {laid.above > 0 && <div style={{ height: laid.above }} aria-hidden />}
                {laid.shown.map(({ set, open, flows }) => (
                  <div key={set.key} role="rowgroup" data-set-group={set.key}>
                    <SetRow set={set} open={open} columns={columns} onToggle={onToggleSet} onLaunchSet={onLaunchSet} />
                    {flows !== null && (
                      <>
                        {flows.above > 0 && <div style={{ height: flows.above }} aria-hidden />}
                        {flows.shown.map(({ entry, open: flowOpen }) => (
                          <FlowGroup
                            key={entry.flow.pipelineId}
                            entry={entry}
                            open={flowOpen}
                            repoName={repoNames.get(entry.flow.repoId) ?? entry.flow.repoId}
                            columns={columns}
                            picked={picks.get(entry.flow.pipelineId) ?? NO_PICKS}
                            onToggle={onToggle}
                            onPick={onPick}
                            onOpen={onOpen}
                            onLaunch={onLaunch}
                          />
                        ))}
                        {flows.below > 0 && <div style={{ height: flows.below }} aria-hidden />}
                      </>
                    )}
                  </div>
                ))}
                {laid.below > 0 && <div style={{ height: laid.below }} aria-hidden />}
              </>
            )
            : single !== undefined && single.tests.map((test) => (
              <TestRow
                key={test.name}
                flow={single.flow}
                test={test}
                columns={columns}
                showKind={singleKinds.length > 1}
                depth={0}
                picked={(picks.get(single.flow.pipelineId) ?? NO_PICKS).has(test.name)}
                onPick={onPick}
                onOpen={onOpen}
                onLaunch={onLaunch}
              />
            ))}
      </div>
      {footer}
    </div>
  );
}
