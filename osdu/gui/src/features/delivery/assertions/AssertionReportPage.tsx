import { useMemo, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { format } from "date-fns";
import { ChevronRight, CircleAlert, Play, RotateCcw } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { pipelineApi } from "@/api/endpoints";
import { IdChip } from "@/components/IdChip";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { useTabTitle } from "@/layout/workbench/TabsContext";
import { parseUtc } from "@/lib/time";
import { cn } from "@/lib/utils";
import { deliveryApi, type DeliveryAssertionRun, type DeliveryAssertionRunDetail, type DeliveryTestResult, type TestOutcome } from "../../../api/delivery";
import { KindText } from "../KindText";
import { ProblemView } from "../TemplateSheet";
import { AssertionRunStatusBadge, OutcomeBar, StatusStrip, TestOutcomeIcon, type StripCell } from "./AssertionBadges";
import { CheckList } from "./AssertionChecks";
import { AssertionRunDialog, ReportDownloads, type AssertionLaunch } from "./AssertionRunDialog";
import { OUTCOME_ORDER, STANDING_TEXT, checkCounts, checksVerdict, counted, duration, selectionText } from "./assertionFormat";
import { shortId } from "../idTail";

function tally(results: readonly DeliveryTestResult[]) {
  const count = (outcome: TestOutcome) => results.filter((r) => r.outcome === outcome).length;
  return { passed: count("passed"), failed: count("failed"), warned: count("warned"), errored: count("errored"), skipped: count("skipped") };
}

/**
 * Whether the run stopped before it recorded a result for every test it selected: cancelled, or failing part way. A run
 * that completed records one for each, a skipped test's included, so one whose tests errored is told from one that stopped
 * by its counts; its reason (how many tests failed or errored, and which) is what the strip and the results below show.
 */
function runStopped(run: Pick<DeliveryAssertionRun, "status" | "tests" | "passed" | "failed" | "warned" | "errored" | "skipped">) {
  const recorded = run.passed + run.failed + run.warned + run.errored + run.skipped;
  return run.status === "cancelled" || (run.status === "errored" && recorded < run.tests);
}

/**
 * One test's result on one line (where it stands, its verdict, what it matched, how long it took), opening in place on
 * what it read as it ran and the checks that ask for a look.
 */
function ResultRow({ result, initiallyOpen }: { result: DeliveryTestResult; initiallyOpen: boolean }) {
  const [open, setOpen] = useState(initiallyOpen);
  const verdict = result.outcome === "skipped"
    ? { text: result.error ?? "skipped: it does not run in this partition", standing: "skipped" as const }
    : result.outcome === "errored" && (result.assertions ?? []).length === 0
      ? { text: "could not be evaluated", standing: "errored" as const }
      : checksVerdict(checkCounts(result.assertions ?? []));
  return (
    <div data-testid={`report-result-${result.test}`} data-outcome={result.outcome}>
      <button
        type="button"
        onClick={() => setOpen((was) => !was)}
        aria-expanded={open}
        className="grid w-full grid-cols-[auto_auto_minmax(0,1fr)_auto] items-center gap-x-2.5 px-3 py-1.5 text-left outline-none transition-colors hover:bg-accent/50 focus-visible:bg-accent/50 md:grid-cols-[auto_auto_minmax(0,16rem)_minmax(0,1fr)_7rem_4.5rem]"
      >
        <ChevronRight className={cn("size-4 text-muted-foreground transition-transform", open && "rotate-90")} />
        <TestOutcomeIcon outcome={result.outcome} />
        <span className="truncate font-mono text-[12.5px]" title={result.description ?? undefined}>{result.test}</span>
        <span className={cn("hidden truncate text-[12px] md:block", STANDING_TEXT[result.outcome] ?? (verdict.standing === "passed" ? "text-muted-foreground" : STANDING_TEXT[verdict.standing]))}>
          {verdict.text}
        </span>
        <span className="hidden text-right font-mono text-[11.5px] tabular-nums text-muted-foreground md:block">
          {result.matched !== null && result.matched !== undefined ? `${result.matched.toLocaleString("en-US")} matched` : ""}
        </span>
        <span className="text-right font-mono text-[11.5px] tabular-nums text-muted-foreground">{duration(result.durationMs)}</span>
      </button>
      {open && (
        <div className="flex flex-col gap-3 border-t bg-muted/10 px-3 py-3 md:pl-12">
          <p className="flex flex-wrap items-center gap-x-3 gap-y-1 text-[12px] text-muted-foreground" data-testid="report-result-read">
            <span className="min-w-0 max-w-[26rem]"><KindText kind={result.kind} /></span>
            {result.query && <code className="break-all font-mono text-[11.5px] text-foreground">{result.query}</code>}
            {(result.ids ?? 0) > 0 && <span>{counted(result.ids ?? 0, "record")} by id</span>}
            {result.sampled && <span>read {result.evaluated?.toLocaleString("en-US") ?? "-"} as a sample</span>}
            {result.template && <span>schema <span className="font-mono">{result.template}</span></span>}
          </p>
          {result.error && result.outcome !== "skipped" && (
            <Alert variant="destructive">
              <CircleAlert />
              <AlertTitle>The assertion could not be evaluated</AlertTitle>
              <AlertDescription>{result.error}</AlertDescription>
            </Alert>
          )}
          {(result.problems ?? []).length > 0 && (
            <Alert variant="destructive">
              <CircleAlert />
              <AlertTitle>It does not fit the schema of its type</AlertTitle>
              <AlertDescription>
                <ul className="list-disc pl-4">{(result.problems ?? []).map((p) => <li key={p}>{p}</li>)}</ul>
              </AlertDescription>
            </Alert>
          )}
          {(result.notes ?? []).length > 0 && (
            <ul className="list-disc pl-5 text-[12.5px] text-muted-foreground">{(result.notes ?? []).map((note) => <li key={note}>{note}</li>)}</ul>
          )}
          {(result.assertions ?? []).length > 0 && <CheckList outcomes={result.assertions ?? []} testId={`report-checks-${result.test}`} />}
        </div>
      )}
    </div>
  );
}

/** The tests of the report by the type they read, each type's outcomes as a bar: shown only when there is more than one. */
function ByKind({ results }: { results: readonly DeliveryTestResult[] }) {
  const kinds = useMemo(() => {
    const groups = new Map<string, DeliveryTestResult[]>();
    for (const result of results) {
      groups.set(result.kind, [...(groups.get(result.kind) ?? []), result]);
    }

    return [...groups.entries()].sort((a, b) => a[0].localeCompare(b[0]));
  }, [results]);
  if (kinds.length < 2) {
    return null;
  }

  return (
    <Card className="gap-2 rounded-lg p-3" data-testid="report-by-kind">
      <h2 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">By type</h2>
      <div className="grid gap-x-4 gap-y-2 md:grid-cols-[minmax(0,22rem)_minmax(8rem,1fr)_auto]">
        {kinds.map(([kind, group]) => {
          const counts = tally(group);
          return (
            <div key={kind} className="contents">
              <span className="min-w-0"><KindText kind={kind} /></span>
              <span className="flex items-center"><OutcomeBar counts={counts} /></span>
              <span className="font-mono text-[11.5px] tabular-nums text-muted-foreground">{counts.passed}/{group.length} passed</span>
            </div>
          );
        })}
      </div>
    </Card>
  );
}

/** The report's facts line: which flow, where, when, how long, by whom, and what it was asked to run. */
function Subtitle({ detail }: { detail: DeliveryAssertionRunDetail }) {
  const run = detail.run;
  return (
    <span className="flex flex-wrap items-center gap-x-2 gap-y-1">
      {detail.pipelineId !== null
        ? <Link to={`/pipelines/${detail.pipelineId}?tab=tests`} className="font-mono text-foreground hover:underline">{run.flowName}</Link>
        : <span className="font-mono">{run.flowName}</span>}
      {run.partition !== null && <span>in <span className="font-mono">{run.partition}</span></span>}
      <span>{format(parseUtc(run.startedUtc), "yyyy-MM-dd HH:mm:ss")}</span>
      {run.completedUtc !== null && <span>took {duration(parseUtc(run.completedUtc).getTime() - parseUtc(run.startedUtc).getTime())}</span>}
      <span>by {run.actor}</span>
      <span>ran {selectionText(run.selection)}</span>
    </span>
  );
}

/**
 * One run of an assertion flow's tests, as the report of how OSDU stood when it ran: the share that passed, and every test
 * on a line of its own that opens on the checks that ask for a look. It downloads as HTML, Markdown, JSON and JUnit XML,
 * and the tests that did not pass run again from here.
 */
export default function AssertionReportPage() {
  const { assertionRunId: raw } = useParams();
  const assertionRunId = Number(raw);
  const valid = Number.isSafeInteger(assertionRunId) && assertionRunId > 0;
  const [shown, setShown] = useState<string | null>(null);
  const [launch, setLaunch] = useState<AssertionLaunch | null>(null);
  const detail = useQuery({
    queryKey: ["delivery", "assertions", "run", assertionRunId],
    queryFn: () => deliveryApi.assertionRun(assertionRunId),
    enabled: valid,
    refetchInterval: (query) => (query.state.data?.run.status === "running" ? 5000 : false),
  });
  const pipeline = useQuery({
    queryKey: ["pipelines", "detail", detail.data?.pipelineId ?? null],
    queryFn: () => pipelineApi.getById(detail.data!.pipelineId!),
    enabled: detail.data?.pipelineId !== null && detail.data?.pipelineId !== undefined,
    staleTime: 60000,
  });
  useTabTitle(valid ? `Report #${assertionRunId}` : undefined);

  const results = useMemo(() => {
    const all = detail.data?.results ?? [];
    return [...all].sort((a, b) => OUTCOME_ORDER.indexOf(a.outcome) - OUTCOME_ORDER.indexOf(b.outcome) || a.test.localeCompare(b.test));
  }, [detail.data]);

  if (!valid) {
    return <Page data-testid="page-assertion-report"><p className="text-[13px] text-destructive">'{raw}' is not a report number.</p></Page>;
  }

  if (detail.isError) {
    return <Page data-testid="page-assertion-report"><ProblemView error={detail.error} testId="assertion-report-error" /></Page>;
  }

  if (detail.data === undefined) {
    return <Page data-testid="page-assertion-report"><Skeleton className="h-72 w-full rounded-lg" /></Page>;
  }

  const { run } = detail.data;
  const counts = tally(results);
  const evaluated = counts.passed + counts.failed + counts.warned + counts.errored;
  const notPassing = results.filter((r) => r.outcome === "failed" || r.outcome === "errored" || r.outcome === "warned").map((r) => r.test);
  const shownResults = shown === null ? results : results.filter((r) => r.outcome === shown);
  const launchable = pipeline.data !== undefined;
  const again = (tests: readonly string[], tags: readonly string[]) => {
    if (pipeline.data !== undefined) {
      setLaunch({ pipelineId: pipeline.data.id, repoId: pipeline.data.repoId, flowName: pipeline.data.name, tests, tags });
    }
  };
  const selection = (() => {
    try {
      const parsed = run.selection === null ? {} : JSON.parse(run.selection) as { tests?: string[]; tags?: string[] };
      return { tests: parsed.tests ?? [], tags: parsed.tags ?? [] };
    } catch {
      return { tests: [], tags: [] };
    }
  })();
  const cells: StripCell[] = [
    { key: "failed", label: "Failed", count: counts.failed, standing: "failed", hint: "A check of error severity failed." },
    { key: "errored", label: "Errored", count: counts.errored, standing: "errored", hint: "The assertion could not find out." },
    { key: "warned", label: "Warned", count: counts.warned, standing: "warned", hint: "Only checks of warning severity failed." },
    { key: "passed", label: "Passed", count: counts.passed, standing: "passed", hint: "Every check of error or warning severity passed." },
    { key: "skipped", label: "Skipped", count: counts.skipped, standing: null, hint: "Not run in this partition." },
  ];

  return (
    <Page data-testid="page-assertion-report">
      <PageHeader
        title={(
          <span className="flex flex-wrap items-center gap-2">
            Report #{run.assertionRunId}
            <AssertionRunStatusBadge status={run.status} />
          </span>
        )}
        subtitle={<Subtitle detail={detail.data} />}
        actions={(
          <>
            {run.runId !== null && <IdChip label="run" value={run.runId} display={shortId(run.runId)} to={`/runs/${run.runId}`} testId="assertion-report-run" />}
            {notPassing.length > 0 && (
              <Button size="sm" onClick={() => again(notPassing, [])} disabled={!launchable} data-testid="assertion-report-rerun-failing">
                <RotateCcw />
                Run the {counted(notPassing.length, "assertion")} not passing again
              </Button>
            )}
            <Button size="sm" variant="outline" onClick={() => again(selection.tests, selection.tags)} disabled={!launchable} data-testid="assertion-report-rerun">
              <Play />
              Run again
            </Button>
            <ReportDownloads assertionRunId={run.assertionRunId} flowName={run.flowName} partition={run.partition} testId="assertion-report-downloads" />
          </>
        )}
      />

      {run.error && runStopped(run) && (
        <Alert variant="destructive" data-testid="assertion-report-run-error">
          <CircleAlert />
          <AlertTitle>The run stopped</AlertTitle>
          <AlertDescription>{run.error}</AlertDescription>
        </Alert>
      )}

      <StatusStrip
        passRate={evaluated === 0 ? null : (100 * counts.passed) / evaluated}
        evaluated={evaluated}
        bar={counts}
        cells={cells}
        selected={shown}
        onSelect={setShown}
        testId="assertion-report-score"
      />

      <ByKind results={results} />

      {shownResults.length === 0
        ? <p className="text-[13px] text-muted-foreground">{results.length === 0 ? "The run recorded no result yet." : "No assertion came out this way."}</p>
        : (
          <Card className="gap-0 divide-y overflow-hidden rounded-lg p-0" data-testid="assertion-report-results">
            {shownResults.map((result) => (
              <ResultRow key={result.test} result={result} initiallyOpen={result.outcome === "failed" || result.outcome === "errored"} />
            ))}
          </Card>
        )}

      <AssertionRunDialog launch={launch} onClose={() => setLaunch(null)} />
    </Page>
  );
}
