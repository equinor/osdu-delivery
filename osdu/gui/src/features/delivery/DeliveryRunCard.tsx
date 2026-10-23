import { Info, TriangleAlert } from "lucide-react";
import { useQuery } from "@tanstack/react-query";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Card } from "@/components/ui/card";
import type { RunDetail } from "@/api/types";
import { runValueAssignment } from "@/lib/runValues";
import { CodeView } from "@/components/CodeView";
import { deliveryApi, type ReversalSource } from "../../api/delivery";
import { prettyJson } from "./prettyJson";
import { ReversalCard } from "./ReversalCard";
import { runCacheSteps, runRequest, runResult, runScope } from "./runOutcome";
import { undoneFirst, undoRunCounts } from "../../api/artifacts";
import { UndoneFirst } from "./ArtifactMarks";
import { DimensionRunViews } from "./dimensions/DimensionRunViews";

const OUTCOME_COPY: Record<string, string> = {
  cache: "What the refresh reported when it finished: the version it wrote (or that nothing changed), each type's record count and changes, and the delivered records those changes reach.",
  retrieval: "What the retrieval reported when it finished: the window it covered, the records and files it wrote, and where they went.",
  assertion: "How the assertions came out: the report it kept, each outcome's count, and the assertions that did not pass. Every result, whole, is in the report.",
};

const DEFAULT_OUTCOME_COPY = "What the run reported when it finished: its counts, the submission it worked on, and how far it fanned out.";

const REDELIVER_LABELS: Record<string, string> = {
  all: "metadata and payload",
  metadata: "metadata only",
  payload: "payload only",
  record: "the record only",
  files: "files only",
  bulk: "bulk data only",
  workflow: "the workflow run only",
};

/** What a remove run takes of each record, as the removal dialog names its choices. */
const REMOVAL_LABELS: Record<string, string> = {
  record: "remove from OSDU (reversible)",
  previous: "restore the previous version",
  history: "purge earlier versions",
  everything: "purge everything",
};

/** The parts of a listing filter, by the names the records list gives them, in the order it shows them. */
const FILTER_PARTS: [field: string, label: string][] = [
  ["status", "status"],
  ["issue", "issue"],
  ["submissionId", "submission"],
  ["deliveredBySubmissionId", "delivered by"],
  ["runId", "run"],
];

/** The listing a remove run empties, as one line: every part it names, the search with how it matches. */
function filterText(filter: Record<string, unknown>): string {
  const parts = FILTER_PARTS
    .filter(([field]) => typeof filter[field] === "string" && filter[field] !== "")
    .map(([field, label]) => `${label} ${String(filter[field])}`);
  if (typeof filter.search === "string" && filter.search !== "") {
    parts.push(`${filter.mode === "contains" ? "contains" : "starts with"} ${filter.search}`);
  }

  if (filter.drifted === true) {
    parts.push("drifted");
  }

  return parts.length === 0 ? "every record of the ledger" : `every record matching ${parts.join(", ")}`;
}

/**
 * What a delivery, retrieval, cache, assertion or dimension run was asked to do (its operation, the payload its trigger sent,
 * the flow parameter values it was given) and, once it finished, what it reported; for a remove run, what became of the
 * records it took; for a dimension run, the views it wrote.
 */
export default function DeliveryRunCard({ run }: { run: RunDetail }) {
  const request = runRequest(run);
  const values = Object.entries(run.values ?? {});
  const asked = run.operation !== null || values.length > 0 || Object.keys(run.payload ?? {}).length > 0;

  // A reverse run shows the reversal it works on, read from the ledger as it goes; it opens the reversal when it starts.
  const reversed: ReversalSource | null = run.operation !== "reverse"
    ? null
    : request.reversesRun !== null
      ? { kind: "run", id: request.reversesRun }
      : request.submissionId !== null ? { kind: "submission", id: request.submissionId } : null;
  const scope = runScope(run);
  const reversal = useQuery({
    queryKey: ["delivery", "reversals", run.pipelineId, scope.interfaceName, scope.partition, reversed?.kind ?? null, reversed?.id ?? null],
    queryFn: () => deliveryApi.reversals(run.pipelineId!, scope, reversed!),
    enabled: reversed !== null && run.pipelineId !== null,
    refetchInterval: (q) => ((q.state.data?.length ?? 0) === 0 && (run.status === "queued" || run.status === "running") ? 3000 : false),
  });
  const reversalId = reversal.data?.[0]?.reversalId ?? null;
  const result = runResult(run);
  const undone = run.operation === "undo" ? undoRunCounts(result) : null;
  // Deleting the ledger and a removal each undo what unfinished deliveries left before they remove anything.
  const ledgerUndone = run.operation === "delete-ledger" || run.operation === "remove" ? undoneFirst(result) : null;
  const removalSummary = run.operation === "remove" && typeof result?.summary === "string" ? result.summary : null;
  // A deliver, replan or drain run ends with the sweep that undoes what unfinished deliveries left.
  const sweptUndone = run.operation === null || run.operation === "deliver" || run.operation === "replan" || run.operation === "drain" ? undoneFirst(result) : null;
  // A pipeline's cache steps: what it brought up to date before it rendered, and what it put into the cache after.
  const cacheSteps = run.flowKind === "delivery" ? runCacheSteps(result) : [];

  return (
    <>
      {asked && (
        <Alert data-testid="run-parameters">
          <Info />
          <AlertDescription>
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-medium text-foreground">Parameters:</span>
              {run.operation !== null && (
                <Badge variant="secondary" className="bg-info/12 text-info" data-testid="run-operation">{run.operation}</Badge>
              )}
              {request.force && <Badge variant="secondary" className="bg-warning/15 text-warning">forced</Badge>}
              {request.submissionId !== null && (
                <Badge variant="secondary" className="font-mono">
                  {run.operation === "reverse" ? `reverses submission ${request.submissionId}` : `submission ${request.submissionId}`}
                </Badge>
              )}
              {request.reversesRun !== null && (
                <Badge variant="secondary" className="font-mono" data-testid="run-reverses">{`reverses run ${request.reversesRun}`}</Badge>
              )}
              {request.recordKeys.length > 0 && (
                <Badge variant="secondary">
                  {request.recordKeys.length} {run.operation === "remove" ? "" : "scoped "}{request.recordKeys.length === 1 ? "record" : "records"}
                </Badge>
              )}
              {request.removalScope !== null && (
                <Badge variant="secondary" data-testid="run-removal-scope">
                  {REMOVAL_LABELS[request.removalScope] ?? request.removalScope}
                </Badge>
              )}
              {request.purgeLedger && <Badge variant="secondary" data-testid="run-purge-ledger">then delete from the ledger</Badge>}
              {request.filter !== null && (
                <Badge variant="secondary" className="font-mono" data-testid="run-removal-filter">{filterText(request.filter)}</Badge>
              )}
              {request.redeliver !== null && (
                <Badge variant="secondary" data-testid="run-redeliver">
                  {`redeliver ${REDELIVER_LABELS[request.redeliver] ?? request.redeliver}${request.recordKeys.length === 0 ? " of every delivered record" : ""}`}
                </Badge>
              )}
              {request.rerender && (
                <Badge variant="secondary" data-testid="run-rerender">
                  {run.operation === "plan"
                    ? "what bringing them up to date would send"
                    : `bring up to date${request.recordKeys.length === 0 ? " every delivered record" : ""}`}
                </Badge>
              )}
              {request.slices.length > 0 && (
                <Badge variant="secondary" className="font-mono" data-testid="run-slices">
                  {`${request.slices.length === 1 ? "slice" : "slices"} ${request.slices.join(", ")}`}
                </Badge>
              )}
              {request.tests.length > 0 && (
                <Badge variant="secondary" className="font-mono" data-testid="run-tests-picked">
                  {`${request.tests.length === 1 ? "assertion" : "assertions"} ${request.tests.join(", ")}`}
                </Badge>
              )}
              {request.tags.map((tag) => (
                <Badge key={`tag-${tag}`} variant="secondary" className="font-mono" data-testid="run-tags-picked">#{tag}</Badge>
              ))}
              {values.map(([name, value]) => (
                <Badge key={name} variant="secondary" className="font-mono">{runValueAssignment(name, value)}</Badge>
              ))}
            </div>
          </AlertDescription>
        </Alert>
      )}

      {reversalId !== null && <ReversalCard reversalId={reversalId} />}

      {removalSummary !== null && (
        <Card className="gap-1 rounded-lg p-3" data-testid="run-removal">
          <h2 className="text-[13px] font-medium">Removal</h2>
          <p className="text-[12.5px] text-muted-foreground" data-testid="run-removal-summary">{removalSummary}</p>
        </Card>
      )}

      {/* What an undo run took back, what deleting the ledger undid before it removed anything, and what a delivery run's sweep undid. */}
      {run.operation === "undo" && undone !== null && (
        <Card className="gap-1 rounded-lg p-3" data-testid="run-undo">
          {undone.records === 0
            ? <p className="text-[13px] text-muted-foreground">Nothing unfinished deliveries left was due an undo.</p>
            : <UndoneFirst undone={undone} heading="Undone" testId="run-undo-counts" />}
        </Card>
      )}
      {ledgerUndone !== null && ledgerUndone.records > 0 && (
        <Card className="gap-1 rounded-lg p-3" data-testid="run-undone-first">
          <UndoneFirst undone={ledgerUndone} testId="run-undone-first-counts" />
        </Card>
      )}
      {sweptUndone !== null && sweptUndone.records > 0 && (
        <Card className="gap-1 rounded-lg p-3" data-testid="run-undone-after">
          <UndoneFirst undone={sweptUndone} heading="Undone after the run" testId="run-undone-after-counts" />
        </Card>
      )}

      {cacheSteps.length > 0 && (
        <Card className="gap-1 rounded-lg p-3" data-testid="run-cache-steps">
          <h2 className="text-[13px] font-medium">Cache</h2>
          {cacheSteps.map((said, index) => (
            <p key={`${said.step}-${said.scope}-${index}`} className="flex items-start gap-1.5 text-[12.5px] text-muted-foreground" data-testid={`run-cache-${said.step}`}>
              {said.skipped && <TriangleAlert className="mt-0.5 size-3.5 shrink-0 text-warning" aria-label="Not brought up to date" />}
              <span>{said.summary}</span>
            </p>
          ))}
        </Card>
      )}

      {/* A dimension run's views: what it wrote, checked and dropped, each a way to its page. */}
      {run.flowKind === "dimension" && <DimensionRunViews run={run} />}

      {run.resultJson !== null && (
        <Card className="gap-2 rounded-lg p-3" data-testid="run-result">
          <h2 className="text-[13px] font-medium">Outcome</h2>
          <p className="text-[13px] text-muted-foreground">{OUTCOME_COPY[run.flowKind] ?? DEFAULT_OUTCOME_COPY}</p>
          <CodeView value={prettyJson(run.resultJson)} language="json" height={220} data-testid="run-result-json" />
        </Card>
      )}
    </>
  );
}
