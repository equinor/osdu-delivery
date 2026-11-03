import { useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { ChevronRight, Code2, Loader2, Play } from "lucide-react";
import { Button } from "@/components/ui/button";
import { CodeView } from "@/components/CodeView";
import { CopyButton } from "@/components/CopyButton";
import { DataTable, type Column } from "@/components/DataTable";
import { ExplainMark } from "@/components/ExplainTip";
import { TruncatedText } from "@/components/TruncatedText";
import { cn } from "@/lib/utils";
import {
  deliveryApi,
  type DeliveryProofRows,
  type DeliveryValueCheckFinding,
  type DeliveryValueProofStep,
} from "../../api/delivery";
import { ProblemView, TaskProgress } from "./TemplateSheet";
import { flowScope, type ValueCheckSession, type VariableCheck } from "./useValueCheck";
import { formatCount } from "./valueCheck";
import { codeParts, proofRequest } from "./valueProof";

/** The rows a step run lists on the page; the statement copied lists them all. */
const RUN_LIMIT = 200;

/** Text with what the server set in backticks drawn as code: a column, a type, a value, an id. */
export function CodeText({ text, className }: { text: string; className?: string }) {
  return (
    <span className={className}>
      {codeParts(text).map((part, i) => part.code
        // A long id breaks where it must rather than pushing the panel wider; a short one stays whole.
        ? <code key={i} className="rounded-sm bg-muted px-1 py-px font-mono text-[0.92em] [overflow-wrap:anywhere]">{part.text}</code>
        : <span key={i}>{part.text}</span>)}
    </span>
  );
}

/**
 * What a finding does to its records, where it was looked for, and what to do about it, in the words the workbook and the
 * command line use; the reason as the render gave it sits behind a mark, for the ledger and the run's log say it so.
 */
export function FindingExplanation({ finding, testId }: { finding: DeliveryValueCheckFinding; testId: string }) {
  const said = finding.explanation;
  if (!said) {
    return null;
  }

  return (
    <div className="flex flex-col gap-2" data-testid={testId}>
      <p className="flex items-start gap-1 text-[12.5px]" data-testid={`${testId}-effect`}>
        <CodeText text={said.effect} className="min-w-0" />
        <ExplainMark title="Reason as rendered" text={finding.message} testId={`${testId}-reason`} />
      </p>
      {said.detail && <p className="text-[12px] text-muted-foreground" data-testid={`${testId}-detail`}><CodeText text={said.detail} /></p>}
      <div className="flex flex-col gap-1">
        <h5 className="text-[12px] font-medium">What to do</h5>
        <ol className="flex list-decimal flex-col gap-1 pl-5 text-[12.5px] marker:text-muted-foreground" data-testid={`${testId}-fixes`}>
          {said.fixes.map((fix) => <li key={fix}><CodeText text={fix} /></li>)}
        </ol>
      </div>
    </div>
  );
}

const RUNS_ON: Record<DeliveryValueProofStep["runsOn"], string> = {
  source: "Runs on the flow's source",
  catalog: "Runs on the catalog's cache",
  platform: "Send to the platform's search",
};

const ABOUT_THE_PROOF = "Queries that show this finding from the data itself: where the source holds the values behind it, the records to fix by their keys, and what the cache (or the platform's search) holds of what was looked for. Run them here, or copy them into SSMS: the text copied is the text run. Each only reads; nothing is written.";

/**
 * The proof of a finding, opened on request: each step a read-only statement the control plane writes from the flow and the
 * mapping, with what it shows, where it runs, a Run that lists its first rows here, and the statement to copy, which lists
 * them all. Nothing is read until it is opened, and a step reads nothing until it is run.
 */
export function FindingProof({
  target, finding, check, session, testId,
}: {
  /** The attribute checked, which the finding is of. */
  target: string;
  finding: DeliveryValueCheckFinding;
  /** The check that found it, whose flow, scope and cache the proof reads. */
  check: VariableCheck;
  session: ValueCheckSession;
  testId: string;
}) {
  const [open, setOpen] = useState(false);
  const [results, setResults] = useState<Partial<Record<DeliveryValueProofStep["step"], DeliveryProofRows>>>({});
  const { flow, values, cacheVersion } = check.meta;
  const ask = proofRequest(finding, target, { values, cacheVersion }, session.reference);
  const written = useQuery({
    queryKey: ["delivery", "value-proof", flow.pipelineId, flow.interface, flow.partition, ask?.request ?? null],
    queryFn: () => deliveryApi.valueProof(flow.pipelineId, ask!.request, flowScope(flow)),
    enabled: open && ask !== null,
    staleTime: 60000,
    retry: false,
  });
  const run = useMutation({
    mutationFn: (step: "values" | "records" | "cache") => deliveryApi.valueProof(flow.pipelineId, { ...ask!.request, run: step, limit: RUN_LIMIT }, flowScope(flow)),
    onSuccess: (proof, step) => {
      const result = proof.steps.find((s) => s.step === step)?.result;
      if (result) {
        setResults((current) => ({ ...current, [step]: result }));
      }
    },
  });

  if (ask === null) {
    return null;
  }

  const cut = [
    ask.sourceValues.sent < ask.sourceValues.listed ? `the first ${formatCount(ask.sourceValues.sent)} of the ${formatCount(ask.sourceValues.listed)} source values listed` : null,
    ask.looked.sent < ask.looked.listed ? `the first ${formatCount(ask.looked.sent)} of the ${formatCount(ask.looked.listed)} values looked for` : null,
  ].filter((part): part is string => part !== null);
  const others = (finding.otherSourceValues ?? 0) > 0 || finding.otherValues > 0;
  return (
    <div className="flex flex-col gap-2 border-t px-2.5 py-2" data-testid={testId}>
      <span className="inline-flex items-center gap-1">
        <button
          type="button"
          onClick={() => setOpen((was) => !was)}
          aria-expanded={open}
          className="inline-flex items-center gap-1 text-[12.5px] font-medium hover:text-foreground"
          data-testid={`${testId}-toggle`}
        >
          <ChevronRight className={cn("size-3.5 text-muted-foreground transition-transform", open && "rotate-90")} />
          Prove it from the data
        </button>
        <ExplainMark title="Prove it from the data" text={ABOUT_THE_PROOF} testId={`${testId}-about`} />
      </span>
      {open && (
        <>
          {written.isPending && <TaskProgress label="Writing the queries" testId={`${testId}-progress`} />}
          {written.isError && <ProblemView error={written.error} testId={`${testId}-error`} />}
          {(cut.length > 0 || others) && (
            <p className="text-[12px] text-muted-foreground" data-testid={`${testId}-covers`}>
              {cut.length > 0
                ? `The queries look for ${cut.join(" and ")}.`
                : "The queries look for the values the check listed; it met others too, which a check of fewer rows lists."}
            </p>
          )}
          {written.data?.notes.map((note) => <p key={note} className="text-[12px] text-muted-foreground">{note}</p>)}
          {written.data?.steps.map((step, i) => (
            <ProofStep
              key={step.step}
              index={i + 1}
              step={step}
              result={results[step.step]}
              running={run.isPending && run.variables === step.step}
              onRun={step.step === "search" ? undefined : () => run.mutate(step.step as "values" | "records" | "cache")}
              testId={`${testId}-step-${step.step}`}
            />
          ))}
          {run.isError && <ProblemView error={run.error} testId={`${testId}-run-error`} />}
        </>
      )}
    </div>
  );
}

/** One step of a proof: what it shows and where it runs, Run and Copy, the statement on request, and what a run listed. */
function ProofStep({
  index, step, result, running, onRun, testId,
}: {
  index: number;
  step: DeliveryValueProofStep;
  result: DeliveryProofRows | undefined;
  running: boolean;
  onRun: (() => void) | undefined;
  testId: string;
}) {
  const [showCode, setShowCode] = useState(false);
  return (
    <div className="flex flex-col rounded-md border bg-card" data-testid={testId}>
      <div className="flex items-start gap-2 px-2.5 py-2">
        <span className="mt-px font-mono text-[11px] text-muted-foreground tabular-nums">{index}</span>
        <div className="flex min-w-0 flex-1 flex-col gap-0.5">
          <span className="inline-flex items-center gap-1 text-[12.5px] font-medium">
            {step.title}
            <ExplainMark title={step.title} text={step.proves} testId={`${testId}-about`} />
          </span>
          {/* Where it runs wraps rather than being cut: a three-part table name is what a reader looks for. */}
          <span className="min-w-0 text-[11.5px] text-muted-foreground" data-testid={`${testId}-where`}>
            {RUNS_ON[step.runsOn]}: <span className="font-mono [overflow-wrap:anywhere]">{step.where}</span>
          </span>
        </div>
        <span className="inline-flex shrink-0 items-center gap-0.5">
          {step.runnable && onRun !== undefined && (
            <Button type="button" size="xs" variant={result ? "outline" : "default"} disabled={running} onClick={onRun} data-testid={`${testId}-run`}>
              {running ? <Loader2 className="animate-spin" /> : <Play />}
              {running ? "Running" : result ? "Run again" : "Run"}
            </Button>
          )}
          <Button
            type="button"
            size="xs"
            variant="ghost"
            onClick={() => setShowCode((was) => !was)}
            aria-expanded={showCode}
            data-testid={`${testId}-code-toggle`}
          >
            <Code2 />
            {step.language === "sql" ? "SQL" : "Request"}
          </Button>
          <CopyButton iconOnly label={step.language === "sql" ? "Copy the SQL" : "Copy the request"} text={step.statement} testId={`${testId}-copy`} />
        </span>
      </div>
      {showCode && <CodeView value={step.statement} language={step.language} height={240} data-testid={`${testId}-code`} />}
      {result !== undefined && <ProofResult result={result} testId={`${testId}-result`} />}
    </div>
  );
}

interface ProofRow {
  index: number;
  cells: (string | null)[];
}

/** What a step run listed: its columns and rows as the statement answers them, a null said as such, and whether there are more. */
function ProofResult({ result, testId }: { result: DeliveryProofRows; testId: string }) {
  const rows: ProofRow[] = result.rows.map((cells, index) => ({ index, cells }));
  const columns: Column<ProofRow>[] = result.columns.map((name, i) => ({
    id: `${i}`,
    header: name,
    render: ({ cells }) => cells[i] === null
      ? <span className="font-mono text-[11px] text-muted-foreground">NULL</span>
      : <TruncatedText text={cells[i]} mono maxWidth={170} />,
  }));
  return (
    <div className="flex flex-col gap-1 border-t px-2.5 py-2" data-testid={testId}>
      <div className="max-h-72 overflow-auto">
        <DataTable columns={columns} rows={rows} rowKey={(row) => String(row.index)} emptyMessage="No rows." data-testid={`${testId}-table`} />
      </div>
      <span className="text-[11.5px] text-muted-foreground tabular-nums" data-testid={`${testId}-count`}>
        {result.more
          ? `The first ${formatCount(rows.length)} rows; copy the statement to read them all.`
          : `${formatCount(rows.length)} ${rows.length === 1 ? "row" : "rows"}.`}
      </span>
    </div>
  );
}
