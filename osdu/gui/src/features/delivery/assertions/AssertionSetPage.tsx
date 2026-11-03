import { useMemo, useRef, useState } from "react";
import { Link, useParams, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ArrowUpRight, Play, RotateCcw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { EmptyState } from "@/components/EmptyState";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { useTabTitle } from "@/layout/workbench/TabsContext";
import { cn } from "@/lib/utils";
import { deliveryApi, type DeliveryAssertionFlow, type DeliveryAssertionSet, type DeliveryAssertionTest, type DeliveryTestResult } from "../../../api/delivery";
import { useActivePartition } from "../activePartition";
import { ProblemView } from "../TemplateSheet";
import { useWindowFit } from "../useWindowFit";
import { OutcomeBar, StatusStrip, TestOutcomeBadge, TestOutcomeIcon, type StripCell } from "./AssertionBadges";
import { ResultRow } from "./AssertionResultRow";
import { SetReportDownloads } from "./AssertionRunDialog";
import { AssertionSetRunDialog, type AssertionSetLaunch } from "./AssertionSetRunDialog";
import { OUTCOME_ORDER, OUTCOME_VISUALS, STANDING_TEXT, assertionStanding, checkReading, counted, disabledWhy, sharedPrefix, standingOf, type TestStanding } from "./assertionFormat";

/** How often the page reads the set again, so a run under way shows its results as they land. */
const REFRESH_MS = 15000;

/** What stays under the two panes: the page's padding and the workbench's status bar. */
const BELOW_PANES = 28;

/** The least height the panes keep, so a short window still shows a few flows. */
const MIN_PANES = 320;

/** The standings a set's tests are counted in, in the order its figures list them. */
const COUNTED: readonly TestStanding[] = ["failed", "errored", "warned", "notRun", "passed", "disabled"];

const NEEDS_LOOK: ReadonlySet<TestStanding> = new Set(["failed", "errored", "warned"]);

const STRIP_LABELS: Partial<Record<TestStanding, string>> = { notRun: "Not yet checked", errored: "Could not be checked" };

const STRIP_HINTS: Partial<Record<TestStanding, string>> = {
  failed: "A check of error severity does not hold: OSDU holds something other than what the flow states.",
  errored: "The assertion could not find out: a query OSDU refused, a service that did not answer, or an assertion that does not fit the schema of its type.",
  warned: "Only checks of warning severity do not hold, or the values a check expects need restating.",
  notRun: "No run has checked the assertion as its flow states it now.",
  passed: "Every check of error or warning severity holds in the assertion's latest run.",
  disabled: "Switched off in the flow's document, with the reason given there.",
};

/** One test of the set: where it stands now, and its latest result whole when that is what it stands at. */
interface SetTest {
  flow: DeliveryAssertionFlow;
  test: DeliveryAssertionTest;
  standing: TestStanding;
  result: DeliveryTestResult | null;
}

/** One flow of the set: its tests worst first, where it stands as a whole, and its first issue. */
interface SetFlow {
  flow: DeliveryAssertionFlow;
  tests: SetTest[];
  standing: TestStanding;
  issue: string | null;
}

function rankOf(standing: TestStanding): number {
  const at = OUTCOME_ORDER.indexOf(standing);
  return at < 0 ? OUTCOME_ORDER.length : at;
}

/**
 * The first thing a flow's reader should know is wrong, in one line: its worst test and that test's first check that did
 * not hold, with what it expected and what it found, or why the test could not be checked; null when nothing is.
 */
function firstIssue(tests: readonly SetTest[]): string | null {
  const worst = tests.find((entry) => NEEDS_LOOK.has(entry.standing));
  if (worst === undefined) {
    return null;
  }

  const result = worst.result;
  const checks = result?.assertions ?? [];
  const check = checks.find((c) => assertionStanding(c) === "failed" || assertionStanding(c) === "errored")
    ?? checks.find((c) => assertionStanding(c) === "warned");
  if (check === undefined) {
    return `${worst.test.name}: ${result?.error ?? result?.problems?.[0] ?? OUTCOME_VISUALS[worst.standing].label}`;
  }

  const reading = checkReading(check);
  const found = check.actual ? `, found ${reading.found}` : check.message ? `: ${check.message}` : "";
  return `${worst.test.name}: ${check.label}: expected ${reading.expected}${found}`;
}

/** The set's flows worst first, each test by where it stands now and with its latest result whole. */
function setFlows(data: DeliveryAssertionSet): SetFlow[] {
  const results = new Map(data.results.map((entry) => [`${entry.pipelineId}\n${entry.result.test}`, entry.result]));
  return data.flows
    .map((flow) => {
      const tests = flow.tests
        .filter((test) => test.runsHere)
        .map((test, order) => {
          const standing = standingOf(test);
          const judged = standing === "passed" || standing === "failed" || standing === "warned" || standing === "errored";
          return { entry: { flow, test, standing, result: judged ? results.get(`${flow.pipelineId}\n${test.name}`) ?? null : null }, order };
        })
        .sort((a, b) => rankOf(a.entry.standing) - rankOf(b.entry.standing) || a.order - b.order)
        .map((x) => x.entry);
      const standing: TestStanding = tests.length === 0 ? (flow.problem !== null ? "errored" : "notRun") : tests[0]!.standing;
      return { flow, tests, standing, issue: firstIssue(tests) };
    })
    .sort((a, b) => rankOf(a.standing) - rankOf(b.standing) || a.flow.name.localeCompare(b.flow.name));
}

/** How many of a flow's tests a run checked, and how many of those pass. */
function passShare(tests: readonly SetTest[]): { passed: number; judged: number } {
  const judged = tests.filter((t) => t.standing === "passed" || t.standing === "failed" || t.standing === "warned" || t.standing === "errored");
  return { passed: judged.filter((t) => t.standing === "passed").length, judged: judged.length };
}

/** One flow's line in the list: where it stands, its name, its share of passing tests, and its first issue or what it is for. */
function FlowLine({ entry, prefix, selected, onSelect }: { entry: SetFlow; prefix: string; selected: boolean; onSelect: () => void }) {
  const { flow, tests, standing, issue } = entry;
  const count = (s: TestStanding) => tests.filter((t) => t.standing === s).length;
  const share = passShare(tests);
  return (
    <button
      type="button"
      onClick={onSelect}
      aria-current={selected ? "true" : undefined}
      className={cn(
        "flex w-full flex-col gap-1 border-b border-l-2 border-l-transparent px-3 py-2 text-left outline-none transition-colors hover:bg-accent/50 focus-visible:bg-accent/50",
        selected && "border-l-primary bg-accent/60",
      )}
      data-testid={`set-flow-${flow.name}`}
      data-standing={standing}
    >
      <span className="flex min-w-0 items-center gap-2">
        <TestOutcomeIcon outcome={standing} />
        <span className="min-w-0 flex-1 truncate font-mono text-[12.5px] font-medium" title={flow.name}>{flow.name.slice(prefix.length)}</span>
        <OutcomeBar
          counts={{ passed: count("passed"), warned: count("warned"), failed: count("failed"), errored: count("errored"), skipped: count("notRun"), disabled: count("disabled") }}
          className="h-1.5 w-14 shrink-0"
        />
        <span className="w-12 shrink-0 text-right font-mono text-[11px] tabular-nums text-muted-foreground">{share.judged === 0 ? "-" : `${share.passed}/${share.judged}`}</span>
      </span>
      <span className={cn("truncate pl-6 text-[12px]", issue !== null ? (STANDING_TEXT[standing] ?? "text-muted-foreground") : "text-muted-foreground")}>
        {issue ?? (tests.length === 0 && flow.problem !== null ? flow.problem : flow.description ?? counted(tests.length, "assertion"))}
      </span>
    </button>
  );
}

/** A test with no result to open, on one line: its glyph, its name, and why (the reason it is disabled, or why it is not checked). */
function QuietLine({ entry, why }: { entry: SetTest; why: string }) {
  return (
    <div className="grid grid-cols-[auto_auto_minmax(0,16rem)_minmax(0,1fr)] items-start gap-x-2.5 px-3 py-1.5 text-[12.5px]" data-testid={`set-test-${entry.test.name}`} data-standing={entry.standing}>
      <span className="size-4" aria-hidden />
      <TestOutcomeIcon outcome={entry.standing} />
      <span className="truncate font-mono" title={entry.test.description ?? undefined}>{entry.test.name}</span>
      <span className="text-muted-foreground">{why}</span>
    </div>
  );
}

/**
 * Several tests that share why they have no result to open, on one line: their glyph, how many, why, and their names, so
 * twenty-five assertions changed together, or disabled for one reason, read as one fact.
 */
function QuietGroup({ entries, why }: { entries: readonly SetTest[]; why: string }) {
  return (
    <div className="grid grid-cols-[auto_auto_minmax(0,1fr)] items-start gap-x-2.5 gap-y-1.5 px-3 py-2 text-[12.5px]" data-testid="set-test-group" data-standing={entries[0]!.standing}>
      <span className="size-4" aria-hidden />
      <TestOutcomeIcon outcome={entries[0]!.standing} />
      <div className="flex min-w-0 flex-col gap-1.5">
        <span><span className="font-medium">{counted(entries.length, "assertion")}</span> <span className="text-muted-foreground">{why}</span></span>
        <span className="flex flex-wrap gap-1">
          {entries.map((entry) => (
            <span key={entry.test.name} className="rounded-sm bg-muted px-1.5 py-px font-mono text-[11.5px]" title={entry.test.description ?? undefined}>{entry.test.name}</span>
          ))}
        </span>
      </div>
    </div>
  );
}

/** A line of a flow's detail: a test with a result to open, or the tests that share why they have none. */
type DetailLine = { kind: "result"; entry: SetTest } | { kind: "quiet"; reason: string; entries: SetTest[] };

/**
 * A flow's tests as its detail lists them, in their order: each with a result on a line of its own, and the disabled and
 * not-yet-checked ones gathered by why (the reason a document gives, a change since the last result), each reason once.
 */
function lines(tests: readonly SetTest[]): DetailLine[] {
  const out: DetailLine[] = [];
  const quiet = new Map<string, Extract<DetailLine, { kind: "quiet" }>>();
  for (const entry of tests) {
    if (entry.standing !== "disabled" && entry.standing !== "notRun") {
      out.push({ kind: "result", entry });
      continue;
    }

    const reason = reasonOf(entry);
    const line = quiet.get(reason);
    if (line === undefined) {
      const created: Extract<DetailLine, { kind: "quiet" }> = { kind: "quiet", reason, entries: [entry] };
      quiet.set(reason, created);
      out.push(created);
    } else {
      line.entries.push(entry);
    }
  }

  return out;
}

/** Why a test has no result to open, as a code its line is gathered by: its reason for being disabled, or for not being checked. */
function reasonOf(entry: SetTest): string {
  if (entry.standing === "disabled") {
    return `disabled\n${disabledWhy(entry.test) ?? ""}`;
  }

  if (entry.test.changed) {
    return "changed";
  }

  return entry.test.problems.length > 0 ? `unfit\n${entry.test.problems.join(" ")}` : "never";
}

/** A reason in words, for one test or for several. */
function reasonText(reason: string, count: number): string {
  const [code, detail = ""] = reason.split("\n");
  const one = count === 1;
  switch (code) {
    case "disabled":
      return `disabled: ${detail}`;
    case "changed":
      return one ? "changed since its last result, which checked an earlier version of it" : "changed since their last result, which checked an earlier version of them";
    case "unfit":
      return `${one ? "does not fit the schema of its type" : "do not fit the schema of their type"}: ${detail}`;
    default:
      return one ? "no run has checked it yet" : "no run has checked them yet";
  }
}

/**
 * One flow's assertions, what the dashboard opens on: what the flow is for and when it was checked, then its assertions
 * worst first. One that failed, could not be checked or warned opens on what each check expected and found and the
 * records that fail it; a disabled one says why on its line, and so does one not yet checked.
 */
function FlowDetail({ entry, filter, onRun }: { entry: SetFlow; filter: TestStanding | null; onRun: (tests: readonly string[]) => void }) {
  const { flow } = entry;
  const tests = entry.tests.filter((t) => filter === null || t.standing === filter);
  const notPassing = entry.tests.filter((t) => NEEDS_LOOK.has(t.standing)).map((t) => t.test.name);
  return (
    <div className="flex min-h-0 flex-col" data-testid="set-flow-detail">
      <div className="flex flex-wrap items-start gap-x-3 gap-y-1 border-b px-3 py-2.5">
        <div className="min-w-0 flex-1">
          <span className="flex items-center gap-2">
            <TestOutcomeIcon outcome={entry.standing} />
            <span className="truncate font-mono text-[13px] font-semibold">{flow.name}</span>
          </span>
          {flow.description !== null && <p className="mt-0.5 text-[12px] leading-snug text-muted-foreground">{flow.description}</p>}
          <p className="mt-0.5 text-[11.5px] text-muted-foreground">
            {counted(entry.tests.length, "assertion")}
            {flow.lastRun !== null && <>, last checked <RelativeTime value={flow.lastRun.completedUtc ?? flow.lastRun.startedUtc} absolute={false} /></>}
          </p>
        </div>
        <div className="flex shrink-0 items-center gap-1.5">
          {notPassing.length > 0 && (
            <Button size="xs" onClick={() => onRun(notPassing)} data-testid="set-flow-rerun-failing">
              <RotateCcw />
              Run {notPassing.length} not passing
            </Button>
          )}
          <Button size="xs" variant="outline" onClick={() => onRun([])} disabled={flow.problem !== null} data-testid="set-flow-run">
            <Play />
            Run flow
          </Button>
          <Button asChild size="icon-xs" variant="ghost">
            <Link to={`/pipelines/${flow.pipelineId}?tab=tests`} aria-label={`Open ${flow.name}`} data-testid="set-flow-open">
              <ArrowUpRight />
            </Link>
          </Button>
        </div>
      </div>
      <div className="min-h-0 flex-1 divide-y overflow-y-auto overscroll-contain">
        {flow.problem !== null && entry.tests.length === 0 && <p className="px-3 py-3 text-[12.5px] text-destructive">{flow.problem}</p>}
        {tests.length === 0 && entry.tests.length > 0 && (
          <p className="px-3 py-3 text-[12.5px] text-muted-foreground">No assertion of this flow stands this way now.</p>
        )}
        {lines(tests).map((line) => {
          if (line.kind === "quiet") {
            return line.entries.length === 1
              ? <QuietLine key={line.entries[0]!.test.name} entry={line.entries[0]!} why={reasonText(line.reason, 1)} />
              : <QuietGroup key={line.reason} entries={line.entries} why={reasonText(line.reason, line.entries.length)} />;
          }

          const t = line.entry;
          return t.result !== null
            ? <ResultRow key={t.test.name} result={t.result} initiallyOpen={NEEDS_LOOK.has(t.standing)} />
            : <QuietLine key={t.test.name} entry={t} why="its result could not be read" />;
        })}
      </div>
    </div>
  );
}

/**
 * An assertion set in the workbench's partition (osdu/docs/reference/flow/assertion-sets.md), as a dashboard that fits the
 * window: the set's verdict, its figures (each the filter to what it counts), its flows worst first with each one's first
 * issue, and the flow picked opening beside them on its assertions and what each check expected and found. Every assertion
 * is reported by its latest result of the assertion as its document states it now; an earlier result is not shown. The
 * report opens as a page of its own and downloads as HTML, Markdown, JSON and JUnit XML; the set, a flow, and the assertions
 * that need a look run from here. The flow picked and the filter live in the address, so a link names them.
 */
export default function AssertionSetPage() {
  const { set = "" } = useParams();
  const [params, setParams] = useSearchParams();
  const [active] = useActivePartition();
  const [term, setTerm] = useState("");
  const [launch, setLaunch] = useState<AssertionSetLaunch | null>(null);
  const panes = useRef<HTMLDivElement>(null);
  useWindowFit(panes, BELOW_PANES, MIN_PANES, undefined, "height");
  const query = useQuery({
    queryKey: ["delivery", "assertions", "set", set, active],
    queryFn: () => deliveryApi.assertionSet(set),
    enabled: set !== "",
    refetchInterval: REFRESH_MS,
  });
  useTabTitle(set === "" ? undefined : `Set ${set}`);
  const flows = useMemo(() => (query.data === undefined ? [] : setFlows(query.data)), [query.data]);

  const filterParam = params.get("status");
  const filter = COUNTED.includes(filterParam as TestStanding) ? filterParam as TestStanding : null;
  const lowered = term.trim().toLowerCase();
  const listed = flows.filter((entry) => (filter === null || entry.tests.some((t) => t.standing === filter))
    && (lowered === "" || [entry.flow.name, entry.flow.description ?? "", ...entry.tests.map((t) => t.test.name)].some((text) => text.toLowerCase().includes(lowered))));
  const picked = listed.find((entry) => entry.flow.pipelineId === params.get("flow")) ?? listed[0];
  const prefix = sharedPrefix(flows.map((entry) => entry.flow.name));

  const setParam = (key: string, value: string | null) => setParams((was) => {
    const next = new URLSearchParams(was);
    if (value === null) {
      next.delete(key);
    } else {
      next.set(key, value);
    }

    return next;
  }, { replace: true });

  if (query.isError) {
    return <Page data-testid="page-assertion-set"><ProblemView error={query.error} testId="assertion-set-error" /></Page>;
  }

  if (query.data === undefined) {
    return <Page data-testid="page-assertion-set"><Skeleton className="h-72 w-full rounded-lg" /></Page>;
  }

  const data = query.data;
  const totals = data.totals;
  const tests = flows.flatMap((entry) => entry.tests);
  const of = (standing: TestStanding) => tests.filter((entry) => entry.standing === standing);
  const verdictStanding: TestStanding = data.status;
  const verdict = OUTCOME_VISUALS[verdictStanding];
  const cells: StripCell[] = COUNTED.map((standing) => ({
    key: standing,
    label: STRIP_LABELS[standing] ?? OUTCOME_VISUALS[standing].label,
    count: of(standing).length,
    standing: standing === "notRun" || standing === "disabled" ? null : standing,
    hint: STRIP_HINTS[standing] ?? "",
  }));
  const runFlow = (flow: DeliveryAssertionFlow, names: readonly string[]) => setLaunch({
    set: data.set, flows: [flow], ...(names.length > 0 ? { tests: new Map([[flow.pipelineId, names]]) } : {}),
  });

  return (
    <Page data-testid="page-assertion-set">
      <PageHeader
        title={(
          <span className="flex flex-wrap items-center gap-2">
            {data.set}
            <TestOutcomeBadge outcome={verdictStanding} testId="assertion-set-status" />
          </span>
        )}
        subtitle={`Assertion set: ${counted(data.flows.length, "flow")}, ${counted(totals.tests, "assertion")}${data.partition === null ? "" : ` in ${data.partition}`}, each by its latest result.`}
        actions={(
          <>
            <Button size="sm" variant="outline" onClick={() => setLaunch({ set: data.set, flows: data.flows })} data-testid="assertion-set-run">
              <Play />
              Run set
            </Button>
            <SetReportDownloads set={data.set} partition={data.partition} testId="assertion-set-report" />
          </>
        )}
      />

      <div
        className={cn(
          "flex items-center gap-2.5 rounded-lg border border-l-4 bg-card px-3 py-2",
          verdictStanding === "failed" || verdictStanding === "errored" ? "border-l-destructive" : verdictStanding === "warned" ? "border-l-warning" : verdictStanding === "passed" ? "border-l-success" : "border-l-border",
        )}
        data-testid="assertion-set-verdict"
      >
        <verdict.icon className={cn("size-5 shrink-0", STANDING_TEXT[verdictStanding] ?? (verdictStanding === "passed" ? "text-success" : "text-muted-foreground"))} aria-hidden />
        <p className="text-[14px] font-semibold leading-snug">{data.verdict}</p>
      </div>

      <StatusStrip
        passRate={totals.passRate}
        evaluated={totals.passed + totals.failed + totals.warned + totals.errored}
        bar={{ passed: totals.passed, warned: totals.warned, failed: totals.failed, errored: totals.errored, skipped: totals.notRun, disabled: totals.disabled }}
        cells={cells}
        selected={filter}
        onSelect={(key) => setParam("status", key)}
        testId="assertion-set-score"
      />

      <div ref={panes} className="grid min-h-0 grid-cols-1 gap-3 lg:grid-cols-[minmax(0,2fr)_minmax(0,3fr)]" data-testid="assertion-set-panes">
        <div className="flex min-h-0 flex-col overflow-hidden rounded-lg border bg-card" data-testid="assertion-set-flows">
          <div className="flex items-center gap-2 border-b px-2 py-1.5">
            <SearchInput value={term} onChange={setTerm} placeholder="Find a flow or an assertion" label="Find a flow or an assertion" testId="assertion-set-search" className="flex-1" />
            <span className="shrink-0 px-1 font-mono text-[11px] tabular-nums text-muted-foreground" title={prefix === "" ? undefined : `Each flow's name starts with ${prefix}`}>
              {listed.length === flows.length ? counted(flows.length, "flow") : `${listed.length} of ${flows.length}`}
              {prefix !== "" && <span className="hidden xl:inline"> {prefix}…</span>}
            </span>
          </div>
          <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain">
            {listed.length === 0
              ? <p className="px-3 py-3 text-[12.5px] text-muted-foreground">No flow of the set matches.</p>
              : listed.map((entry) => (
                <FlowLine key={entry.flow.pipelineId} entry={entry} prefix={prefix} selected={entry === picked} onSelect={() => setParam("flow", entry.flow.pipelineId)} />
              ))}
          </div>
        </div>
        <div className="flex min-h-0 flex-col overflow-hidden rounded-lg border bg-card">
          {picked === undefined
            ? <EmptyState title="Pick a flow" description="Its assertions open here, with what each check expected and found." data-testid="assertion-set-no-flow" />
            : <FlowDetail entry={picked} filter={filter} onRun={(names) => runFlow(picked.flow, names)} />}
        </div>
      </div>

      <AssertionSetRunDialog launch={launch} onClose={() => setLaunch(null)} />
    </Page>
  );
}
