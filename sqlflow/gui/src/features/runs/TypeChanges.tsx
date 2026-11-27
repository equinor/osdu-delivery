import { useState } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ChevronRight, CircleAlert, CircleCheck, CircleMinus, CircleX, type LucideIcon } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Card } from "@/components/ui/card";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import { pipelineApi, runApi } from "../../api/endpoints";
import type { ColumnTypeChange } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { Mono } from "../../components/Mono";
import { RelativeTime } from "../../components/RelativeTime";
import { OutcomePill } from "../../components/StatusBadge";
import { fetchEveryPage } from "../../lib/fetchEveryPage";
import { needingAttention, typeChangeOutcome, typeChangeTierLabel, type TypeChangeTone } from "./typeChangeOutcome";

/** The most type changes a page reads for one run: far more columns than a table changes the type of at once. */
const MOST_CHANGES = 1000;

const TONE_ICON: Record<TypeChangeTone, LucideIcon> = {
  success: CircleCheck,
  warning: CircleAlert,
  destructive: CircleX,
  muted: CircleMinus,
};

/** A run's type changes, every page of them, under the key the run page refreshes when the run ends. */
function useRunTypeChanges(runId: string | null) {
  return useQuery({
    queryKey: ["runs", runId ?? "", "type-changes"],
    queryFn: () => fetchEveryPage((page, pageSize) => runApi.typeChanges(runId!, { page, pageSize }), MOST_CHANGES),
    enabled: runId !== null,
  });
}

/** One change on one line, its explanation, refused values and manual steps once opened. */
function TypeChangeRow({ change, showTable }: { change: ColumnTypeChange; showTable: boolean }) {
  const [open, setOpen] = useState(false);
  const outcome = typeChangeOutcome(change.status);
  return (
    <Collapsible open={open} onOpenChange={setOpen} className="border-b border-border last:border-b-0" data-testid="type-change">
      <CollapsibleTrigger
        className="flex w-full min-w-0 flex-wrap items-center gap-x-3 gap-y-1 px-3 py-2 text-left hover:bg-accent/50"
        data-testid="type-change-toggle"
      >
        <ChevronRight className={cn("size-4 shrink-0 text-muted-foreground transition-transform duration-120", open && "rotate-90")} />
        <span className="font-mono text-[12px] font-medium" data-testid="type-change-column">{change.column}</span>
        {showTable && <Mono className="text-muted-foreground">{change.table}</Mono>}
        <span className="inline-flex items-center gap-1.5 font-mono text-[12px]" data-testid="type-change-types">
          <span>{change.fromType}</span>
          <span aria-label="to" className="text-muted-foreground">→</span>
          <span>{change.toType}</span>
        </span>
        <Badge variant="outline" data-testid="type-change-tier">{typeChangeTierLabel(change.tier)}</Badge>
        <OutcomePill tone={outcome.tone} label={outcome.label} icon={TONE_ICON[outcome.tone]} testId="type-change-outcome" />
      </CollapsibleTrigger>
      <CollapsibleContent className="flex flex-col gap-3 border-t border-border bg-background/60 px-3 py-3 pl-10" data-testid="type-change-detail">
        <p className="text-[13px] leading-5 whitespace-pre-wrap" data-testid="type-change-message">{change.message}</p>
        {(change.requiresOptIn !== null || change.rewritesTable || change.isKey || change.rows !== null) && (
          <div className="flex flex-wrap items-center gap-2 text-[12px] text-muted-foreground">
            {change.requiresOptIn !== null && (
              <span>needs <Mono>{change.requiresOptIn}</Mono></span>
            )}
            {change.rewritesTable && <Badge variant="outline">rewrites every row</Badge>}
            {change.isKey && <Badge variant="outline">key column</Badge>}
            {change.rows !== null && <span>{`${change.rows.toLocaleString("en-US")} row(s) in the table`}</span>}
          </div>
        )}
        {change.failingValues !== null && (
          <div className="flex flex-col gap-1.5" data-testid="type-change-sample">
            <h3 className="text-[12px] font-medium">
              {`${change.failingValues.toLocaleString("en-US")} stored value${change.failingValues === 1 ? "" : "s"} do not convert`}
            </h3>
            {change.sample.length > 0 && (
              <table className="w-fit text-[12px]">
                <thead className="text-muted-foreground">
                  <tr>
                    <th className="pr-6 text-left font-normal">Value</th>
                    <th className="text-right font-normal">Rows</th>
                  </tr>
                </thead>
                <tbody>
                  {change.sample.map((value, index) => (
                    <tr key={`${index}-${value.value}`}>
                      <td className="pr-6"><Mono>{value.value}</Mono></td>
                      <td className="text-right font-mono tabular-nums">{value.rows.toLocaleString("en-US")}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        )}
        {change.manualSteps.length > 0 && (
          <div className="flex flex-col gap-1.5" data-testid="type-change-steps">
            <h3 className="text-[12px] font-medium">To make the change by hand</h3>
            <ol className="ml-5 list-decimal text-[13px] leading-5">
              {change.manualSteps.map((step, index) => <li key={index} className="whitespace-pre-wrap">{step}</li>)}
            </ol>
          </div>
        )}
      </CollapsibleContent>
    </Collapsible>
  );
}

/** The list of changes in a card's body. */
function TypeChangeList({ changes, showTable }: { changes: readonly ColumnTypeChange[]; showTable: boolean }) {
  return (
    <div className="overflow-hidden rounded-md border border-border">
      {changes.map((change) => <TypeChangeRow key={change.id} change={change} showTable={showTable} />)}
    </div>
  );
}

/** Whether a set of changes names more than one table, so each row says which. */
function manyTables(changes: readonly ColumnTypeChange[]): boolean {
  return new Set(changes.map((change) => change.table)).size > 1;
}

/**
 * What a run did about each change to an existing column's type it considered: the column, from which type to which, the
 * tier and the outcome, and on opening a row the explanation, the values that refused it and the steps to make it by
 * hand. A run that considered none shows nothing.
 */
export function RunTypeChangesCard({ runId }: { runId: string }) {
  const query = useRunTypeChanges(runId);
  if (query.isError) {
    return isApiError(query.error)
      ? <CorrelationError error={query.error} />
      : <p className="text-[13px] text-destructive">{String(query.error)}</p>;
  }

  const sweep = query.data;
  if (sweep === undefined || sweep.items.length === 0) {
    return null;
  }

  const attention = needingAttention(sweep.items).length;
  const table = manyTables(sweep.items) ? null : sweep.items[0].table;
  return (
    <Card className={cn("gap-2 rounded-lg p-3", attention > 0 && "border-warning/50")} data-testid="run-type-changes">
      <div className="flex flex-wrap items-center gap-2">
        <h2 className="text-[13px] font-medium">Type changes</h2>
        {table !== null && <Mono className="text-muted-foreground">{table}</Mono>}
        {attention > 0 && (
          <span className="inline-flex items-center gap-1 text-[12px] text-warning" data-testid="run-type-changes-attention">
            <CircleAlert className="size-3.5" />
            {`${attention} need${attention === 1 ? "s" : ""} attention`}
          </span>
        )}
        {sweep.capped && (
          <span className="text-[12px] text-muted-foreground">{`the first ${sweep.items.length} of ${sweep.total}`}</span>
        )}
      </div>
      <TypeChangeList changes={sweep.items} showTable={table === null} />
    </Card>
  );
}

/**
 * The type changes a pipeline's latest finished run left for an operator (pending an opt-in, refused, manual, rolled
 * back), near the top of its page. They stay while the latest run reports them, and go once a run no longer does. A
 * pipeline whose latest run left none shows nothing.
 */
export function PipelineTypeChangeAttention({ pipelineId }: { pipelineId: string }) {
  // The pipeline header's run times read the same profile and keep it fresh; this reads it from the shared cache.
  const durations = useQuery({
    queryKey: ["pipelines", "run-durations", pipelineId],
    queryFn: () => pipelineApi.runDurations(pipelineId),
    enabled: pipelineId !== "",
  });
  const last = durations.data?.last ?? null;
  const changes = useRunTypeChanges(last?.runId ?? null);

  if (changes.isError) {
    return isApiError(changes.error)
      ? <CorrelationError error={changes.error} />
      : <p className="text-[13px] text-destructive">{String(changes.error)}</p>;
  }

  const attention = changes.data === undefined ? [] : needingAttention(changes.data.items);
  if (last === null || attention.length === 0) {
    return null;
  }

  return (
    <Card className="gap-2 rounded-lg border-warning/50 p-3" data-testid="pipeline-type-change-attention">
      <div className="flex flex-wrap items-center gap-2">
        <CircleAlert className="size-4 shrink-0 text-warning" />
        <h2 className="text-[13px] font-medium">
          {`${attention.length} column type change${attention.length === 1 ? "" : "s"} need${attention.length === 1 ? "s" : ""} attention`}
        </h2>
        <span className="text-[12px] text-muted-foreground">
          reported by the{" "}
          <Link to={`/runs/${last.runId}`} className="text-primary hover:underline" data-testid="pipeline-type-change-run">
            latest run
          </Link>
          {last.endUtc !== null && <>, <RelativeTime value={last.endUtc} /></>}
        </span>
      </div>
      <TypeChangeList changes={attention} showTable={manyTables(attention)} />
    </Card>
  );
}
