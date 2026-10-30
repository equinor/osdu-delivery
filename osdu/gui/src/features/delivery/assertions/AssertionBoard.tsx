import { useCallback, useMemo, useState, type ReactNode } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Boxes, CircleAlert, ListChecks, Play, Tags, X, type LucideIcon } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { repoApi } from "@/api/endpoints";
import { EmptyState } from "@/components/EmptyState";
import { FilterBar, activeFilterClass } from "@/components/FilterBar";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { cn } from "@/lib/utils";
import type { DeliveryAssertionBoard, DeliveryAssertionFlow, DeliveryAssertionTest } from "../../../api/delivery";
import { KindText } from "../KindText";
import { StatusStrip, type StripCell } from "./AssertionBadges";
import { AssertionBoardGrid, KindName } from "./AssertionBoardGrid";
import { AssertionRunDialog, ReportDownloads, type AssertionLaunch } from "./AssertionRunDialog";
import { AssertionTestSheet } from "./AssertionTestSheet";
import {
  boardEntries, boardFilterOf, boardSortOf, facetCounts, narrowing, type BoardCriteria, type BoardEntry, type BoardFilter, type BoardSort,
} from "./assertionBoardModel";
import { STANDING_TEXT, counted, kindEntity, runStatusVisual, type TestStanding } from "./assertionFormat";

/** The search parameters the test sheet owns, cleared with it. */
const SHEET_PARAMS = ["test", "flow", "testTab", "check"];

/**
 * The search parameters the board's criteria live in, so a board narrowed to what needs a look is still narrowed after
 * a report opened from it is left with Back, and a link can name it: the term, the strip filter, the types and tags
 * picked, and the order.
 */
const CRITERIA_PARAMS = ["q", "status", "type", "tag"];

/** Several values of one search parameter as one string, so a set read from them keeps its identity across renders. */
const SEPARATOR = "\n";

/**
 * Values the tests carry (their types, their tags) as a picker: every test carrying one of the values picked is shown.
 * The list scrolls, so a board of a hundred flows' types stays one popover.
 */
function FacetFilter({ icon: Icon, label, values, picked, onChange, render, summary, testId }: {
  icon: LucideIcon;
  label: string;
  values: readonly (readonly [string, number])[];
  picked: ReadonlySet<string>;
  onChange: (next: ReadonlySet<string>) => void;
  render: (value: string) => ReactNode;
  summary: (picked: readonly string[]) => string;
  testId: string;
}) {
  if (values.length === 0) {
    return null;
  }

  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button variant="outline" size="sm" className={cn("max-w-64", picked.size > 0 && activeFilterClass)} data-testid={testId}>
          <Icon />
          <span className="truncate">{picked.size === 0 ? label : summary([...picked])}</span>
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-80 p-1">
        <div className="flex max-h-80 flex-col overflow-y-auto">
          {values.map(([value, count]) => (
            <label key={value} className="flex cursor-pointer items-center gap-2 rounded-sm px-2 py-1.5 text-[12.5px] hover:bg-accent/50">
              <Checkbox
                checked={picked.has(value)}
                onCheckedChange={(on) => {
                  const next = new Set(picked);
                  if (on === true) {
                    next.add(value);
                  } else {
                    next.delete(value);
                  }

                  onChange(next);
                }}
                data-testid={`${testId}-${value}`}
              />
              <span className="min-w-0 flex-1">{render(value)}</span>
              <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{count}</span>
            </label>
          ))}
        </div>
      </PopoverContent>
    </Popover>
  );
}

/** The test a link opens the sheet on (`?test=`, and on the board of every flow `?flow=`), when the board shows it. */
function openedTest(flows: readonly DeliveryAssertionFlow[], flowId: string | null, name: string | null) {
  if (name === null) {
    return null;
  }

  for (const flow of flows) {
    const test = flowId !== null && flow.pipelineId !== flowId ? undefined : flow.tests.find((t) => t.name === name);
    if (test !== undefined) {
      return { flow, test };
    }
  }

  return null;
}

/** Which flows the reader opened or closed against what the criteria open, and under which criteria they did. */
interface Expansion {
  criteria: string;
  flipped: ReadonlySet<string>;
}

const NOTHING_FLIPPED: ReadonlySet<string> = new Set();

/**
 * A board of one flow names it once above its tests: the type it reads, its last run with its report, and its run
 * buttons, with what keeps it from running here.
 */
function FlowHeading({ flow, picked, onLaunch }: {
  flow: DeliveryAssertionFlow;
  picked: readonly string[];
  onLaunch: (flow: DeliveryAssertionFlow, tests: readonly string[]) => void;
}) {
  const kinds = new Set(flow.tests.map((t) => t.kind));
  const last = flow.lastRun;
  const lastVisual = last === null ? null : runStatusVisual(last.status);
  const runnable = flow.testsPartition && flow.problem === null;
  return (
    <>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1.5 border-b bg-muted/30 px-3 py-2">
        {kinds.size === 1 && <span className="min-w-0 max-w-[26rem] text-muted-foreground"><KindText kind={[...kinds][0]!} /></span>}
        {last !== null && lastVisual !== null && (
          <span className="flex items-center gap-1.5 text-[12px] text-muted-foreground" data-testid="board-flow-last-run">
            <lastVisual.icon className={cn("size-3.5", STANDING_TEXT[last.status as TestStanding] ?? "")} aria-label={lastVisual.label} />
            <Link to={`/delivery/assertions/runs/${last.assertionRunId}`} className="font-mono hover:underline">#{last.assertionRunId}</Link>
            <RelativeTime value={last.completedUtc ?? last.startedUtc} absolute={false} />
          </span>
        )}
        <div className="ml-auto flex items-center gap-1.5">
          {last !== null && <ReportDownloads assertionRunId={last.assertionRunId} flowName={flow.name} partition={last.partition} testId="board-flow-report" />}
          <Button
            size="sm"
            onClick={() => onLaunch(flow, picked)}
            disabled={!runnable}
            data-testid={picked.length > 0 ? "board-flow-run-picked" : "board-flow-run-all"}
          >
            <Play />
            {picked.length > 0 ? `Run ${picked.length} picked` : "Run all"}
          </Button>
        </div>
      </div>
      {flow.problem !== null && (
        <Alert variant="destructive" className="rounded-none border-0 border-b" data-testid="board-flow-problem">
          <CircleAlert />
          <AlertDescription>{flow.problem}</AlertDescription>
        </Alert>
      )}
    </>
  );
}

/**
 * The board of assertion flows in the workbench's partition (osdu/docs/reference/concepts/gui.md, OSDU): how the data
 * stands now. Every flow is one line that opens on its tests, what needs a look first, in a grid that stays as tall as the
 * window leaves, so a hundred flows read as a hundred lines and the page never grows with them. The status strip's counts,
 * the search and the type and tag pickers narrow the board to the tests they find and open their flows on them; a search
 * that names flows lists them closed. A test opens in a sheet on what it found, and runs again from its line, with others
 * picked, or with every test of its flow. How tests came out over earlier runs is the flow's History and Reports tabs' to
 * show. The board of one flow, on its Tests tab, lists its tests directly under its heading.
 */
export function AssertionBoard({ board, scope }: { board: DeliveryAssertionBoard; scope: "all" | "flow" }) {
  const [params, setParams] = useSearchParams();
  const [picks, setPicks] = useState<ReadonlyMap<string, ReadonlySet<string>>>(new Map());
  const [expansion, setExpansion] = useState<Expansion>({ criteria: "", flipped: NOTHING_FLIPPED });
  const [launch, setLaunch] = useState<AssertionLaunch | null>(null);

  // The repositories' names, which the board's flows carry only the ids of: a flow's lineage jump names its graph by it.
  const repos = useQuery({
    queryKey: ["repos", "names-for-assertion-board"],
    queryFn: () => repoApi.list({ page: 1, pageSize: 200 }),
    enabled: scope === "all",
  });
  const repoNames = useMemo(() => new Map((repos.data?.items ?? []).map((repo) => [repo.id, repo.name])), [repos.data]);

  const term = params.get("q") ?? "";
  const filter = boardFilterOf(params.get("status"));
  const sort = boardSortOf(params.get("sort"));
  const typeKey = params.getAll("type").join(SEPARATOR);
  const tagKey = params.getAll("tag").join(SEPARATOR);
  const lowered = term.trim().toLowerCase();
  const criteria = useMemo<BoardCriteria>(() => ({
    term: lowered,
    filter,
    types: new Set(typeKey === "" ? [] : typeKey.split(SEPARATOR)),
    tags: new Set(tagKey === "" ? [] : tagKey.split(SEPARATOR)),
  }), [filter, lowered, tagKey, typeKey]);
  const criteriaKey = [lowered, filter ?? "", typeKey, tagKey].join("\u0000");
  const narrowed = narrowing(criteria);

  const entries = useMemo(() => boardEntries(board.flows, criteria, sort), [board.flows, criteria, sort]);
  const allTypes = useMemo(() => facetCounts(board.flows, (test) => [test.kind]), [board.flows]);
  const allTags = useMemo(() => facetCounts(board.flows, (test) => test.tags), [board.flows]);
  const opened = openedTest(board.flows, scope === "all" ? params.get("flow") : null, params.get("test"));

  // A flow opens when the criteria narrowed its tests, and a reader's own opening or closing of it reverses that until the
  // criteria change, when every flow takes what the new criteria say again.
  const flipped = expansion.criteria === criteriaKey ? expansion.flipped : NOTHING_FLIPPED;
  const isOpen = useCallback((entry: BoardEntry) => entry.narrowed !== flipped.has(entry.flow.pipelineId), [flipped]);
  const allOpen = entries.length > 0 && entries.every(isOpen);

  const update = useCallback((change: (next: URLSearchParams) => void) => setParams((was) => {
    const next = new URLSearchParams(was);
    change(next);
    return next;
  }, { replace: true }), [setParams]);

  const setMany = (key: string, values: ReadonlySet<string>) => update((next) => {
    next.delete(key);
    for (const value of values) {
      next.append(key, value);
    }
  });

  const onToggle = useCallback((flowId: string) => setExpansion((was) => {
    const next = new Set(was.criteria === criteriaKey ? was.flipped : NOTHING_FLIPPED);
    if (next.has(flowId)) {
      next.delete(flowId);
    } else {
      next.add(flowId);
    }

    return { criteria: criteriaKey, flipped: next };
  }), [criteriaKey]);

  const onToggleAll = () => setExpansion({
    criteria: criteriaKey,
    flipped: new Set(entries.filter((entry) => entry.narrowed === allOpen).map((entry) => entry.flow.pipelineId)),
  });

  const onSort = useCallback((next: BoardSort) => update((was) => {
    if (next === "status") {
      was.delete("sort");
    } else {
      was.set("sort", next);
    }
  }), [update]);

  const onOpen = useCallback((flow: DeliveryAssertionFlow, test: DeliveryAssertionTest | null) => update((next) => {
    for (const key of SHEET_PARAMS) {
      next.delete(key);
    }

    if (test !== null) {
      next.set("test", test.name);
      if (scope === "all") {
        next.set("flow", flow.pipelineId);
      }
    }
  }), [scope, update]);

  const onPick = useCallback((flowId: string, name: string, on: boolean) => setPicks((was) => {
    const next = new Map(was);
    const set = new Set(next.get(flowId) ?? []);
    if (on) {
      set.add(name);
    } else {
      set.delete(name);
    }

    if (set.size === 0) {
      next.delete(flowId);
    } else {
      next.set(flowId, set);
    }

    return next;
  }), []);

  const onLaunch = useCallback((flow: DeliveryAssertionFlow, tests: readonly string[]) => setLaunch({
    pipelineId: flow.pipelineId, repoId: flow.repoId, flowName: flow.name, tests, tags: [],
  }), []);

  // What is picked counts the tests each flow still declares and runs here, as its run button does.
  const picked = board.flows
    .map((flow) => ({ flow, names: flow.tests.filter((t) => t.runsHere && (picks.get(flow.pipelineId)?.has(t.name) ?? false)).map((t) => t.name) }))
    .filter((entry) => entry.names.length > 0);
  const pickedTests = picked.reduce((sum, entry) => sum + entry.names.length, 0);

  const totals = board.totals;
  const cells: StripCell[] = [
    { key: "failed", label: "Failing", count: totals.failed, standing: "failed", hint: "A check of error severity failed." },
    { key: "errored", label: "Errored", count: totals.errored, standing: "errored", hint: "The assertion could not find out: a query OSDU refused, a service that did not answer." },
    { key: "warned", label: "Warned", count: totals.warned, standing: "warned", hint: "Only checks of warning severity failed." },
    { key: "notRun", label: "Not run", count: totals.notRun, standing: null, hint: "No result yet in this partition." },
    { key: "passed", label: "Passing", count: totals.passed, standing: "passed", hint: "Every check of error or warning severity passed in the latest run." },
    ...(totals.problems > 0 ? [{ key: "problems", label: "Unfit", count: totals.problems, standing: "errored" as const, hint: "Assertions that do not fit the schema of their type, and so are not evaluated." }] : []),
    ...(totals.changed > 0 ? [{ key: "changed", label: "Changed", count: totals.changed, standing: "warned" as const, hint: "Assertions changed since their latest result." }] : []),
  ];

  if (board.flows.length === 0) {
    return (
      <EmptyState
        icon={<ListChecks />}
        title="No assertion flows synced yet"
        description="Register a repository holding flowType: assertion documents; each flow's assertions appear here with how they came out."
        data-testid="assertion-board-empty"
      />
    );
  }

  const clear = () => update((next) => {
    for (const key of CRITERIA_PARAMS) {
      next.delete(key);
    }
  });

  const shownTests = entries.reduce((sum, entry) => sum + entry.tests.filter((test) => test.runsHere).length, 0);
  const single = scope === "flow" ? board.flows[0] : undefined;
  const none = narrowed
    ? (
      <EmptyState
        title={scope === "all" ? "No flow or assertion matches" : "No assertion of this flow matches"}
        description="Clear the filters to see every assertion."
        data-testid="assertion-board-none"
      />
    )
    : single !== undefined && single.problem === null && (
      <p className="px-3 py-3 text-[12.5px] text-muted-foreground" data-testid="board-flow-none">The flow declares no assertion.</p>
    );

  const footer = single !== undefined && single.problem !== null && single.tests.length === 0 ? undefined : (
    <div className="flex h-8 shrink-0 items-center justify-between gap-3 border-t px-3 text-xs text-muted-foreground" data-testid="assertion-board-footer">
      <span className="truncate font-mono tabular-nums">
        {scope === "all"
          ? narrowed
            ? `${entries.length.toLocaleString("en-US")} of ${counted(board.flows.length, "flow")}, ${shownTests.toLocaleString("en-US")} of ${counted(totals.tests, "assertion")}`
            : `${counted(board.flows.length, "flow")}, ${counted(totals.tests, "assertion")}`
          : narrowed
            ? `${shownTests.toLocaleString("en-US")} of ${counted(totals.tests, "assertion")}`
            : counted(totals.tests, "assertion")}
      </span>
      {pickedTests > 0 && (
        <span className="flex shrink-0 items-center gap-2" data-testid="assertion-board-picked">
          {scope === "all" && picked.length > 1
            ? `${counted(pickedTests, "assertion")} picked in ${picked.length} flows, each run from its flow's line`
            : `${counted(pickedTests, "assertion")} picked`}
          <Button variant="ghost" size="xs" onClick={() => setPicks(new Map())} data-testid="assertion-board-unpick">Clear</Button>
        </span>
      )}
    </div>
  );

  return (
    <div className="flex flex-col gap-3" data-testid="assertion-board">
      <StatusStrip
        passRate={totals.passRate}
        evaluated={totals.passed + totals.failed + totals.warned + totals.errored}
        bar={{ passed: totals.passed, warned: totals.warned, failed: totals.failed, errored: totals.errored, skipped: totals.notRun }}
        cells={cells}
        selected={filter}
        onSelect={(key) => update((next) => {
          if (key === null) {
            next.delete("status");
          } else {
            next.set("status", key as BoardFilter);
          }
        })}
        testId="assertion-scoreboard"
      />

      <FilterBar>
        <SearchInput
          value={term}
          onChange={(value) => update((next) => {
            if (value === "") {
              next.delete("q");
            } else {
              next.set("q", value);
            }
          })}
          placeholder={scope === "all" ? "Find a flow, or an assertion by its name, type, tag, query or check" : "Find an assertion by its name, type, tag, query or check"}
          label={scope === "all" ? "Find a flow or an assertion" : "Find an assertion"}
          testId="assertion-board-search"
          className="sm:w-[30rem]"
        />
        {allTypes.length > 1 && (
          <FacetFilter
            icon={Boxes}
            label="Types"
            values={allTypes}
            picked={criteria.types}
            onChange={(next) => setMany("type", next)}
            render={(kind) => <KindName kind={kind} />}
            summary={(kinds) => kinds.length === 1 ? kindEntity(kinds[0]!) : `${kinds.length} types`}
            testId="assertion-board-types"
          />
        )}
        <FacetFilter
          icon={Tags}
          label="Tags"
          values={allTags}
          picked={criteria.tags}
          onChange={(next) => setMany("tag", next)}
          render={(tag) => <span className="font-mono">#{tag}</span>}
          summary={(tags) => tags.map((tag) => `#${tag}`).join(", ")}
          testId="assertion-board-tags"
        />
        {narrowed && (
          <Button variant="ghost" size="sm" onClick={clear} data-testid="assertion-board-clear">
            <X />
            Clear
          </Button>
        )}
      </FilterBar>

      <AssertionBoardGrid
        scope={scope}
        entries={entries}
        isOpen={isOpen}
        allOpen={allOpen}
        onToggleAll={onToggleAll}
        sort={sort}
        onSort={onSort}
        listKey={`${criteriaKey} ${sort}`}
        repoNames={repoNames}
        picks={picks}
        onToggle={onToggle}
        onPick={onPick}
        onOpen={onOpen}
        onLaunch={onLaunch}
        none={none}
        footer={footer}
        heading={single !== undefined
          ? <FlowHeading flow={single} picked={picked.find((entry) => entry.flow === single)?.names ?? []} onLaunch={onLaunch} />
          : undefined}
      />

      {opened !== null && (
        <AssertionTestSheet
          flow={opened.flow}
          test={opened.test}
          onClose={() => onOpen(opened.flow, null)}
          onRun={(test) => onLaunch(opened.flow, [test.name])}
        />
      )}
      <AssertionRunDialog launch={launch} onClose={() => setLaunch(null)} />
    </div>
  );
}
