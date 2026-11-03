import { useState } from "react";
import { ChevronRight, CircleAlert } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { cn } from "@/lib/utils";
import type { DeliveryTestResult } from "../../../api/delivery";
import { KindText } from "../KindText";
import { TestOutcomeIcon } from "./AssertionBadges";
import { CheckList } from "./AssertionChecks";
import { STANDING_TEXT, checkCounts, checksVerdict, counted, duration } from "./assertionFormat";

/**
 * One test's result on one line (where it stands, its verdict, what it matched, how long it took), opening in place on
 * what it read as it ran and the checks that ask for a look.
 */
export function ResultRow({ result, initiallyOpen }: { result: DeliveryTestResult; initiallyOpen: boolean }) {
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
          {result.description && <p className="text-[12.5px] text-muted-foreground">{result.description}</p>}
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
          <details className="text-[12px] text-muted-foreground" data-testid="report-result-read">
            <summary className="w-fit cursor-pointer hover:text-foreground">How it was checked</summary>
            <p className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1">
              <span className="min-w-0 max-w-[26rem]"><KindText kind={result.kind} /></span>
              {result.query && <code className="break-all font-mono text-[11.5px] text-foreground">{result.query}</code>}
              {(result.ids ?? 0) > 0 && <span>{counted(result.ids ?? 0, "record")} by id</span>}
              {result.sampled && <span>read {result.evaluated?.toLocaleString("en-US") ?? "-"} as a sample</span>}
              {result.template && <span>schema <span className="font-mono">{result.template}</span></span>}
            </p>
          </details>
        </div>
      )}
    </div>
  );
}
