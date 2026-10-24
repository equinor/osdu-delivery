import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { ArrowRight, ArrowRightLeft, CheckCircle2, CircleDashed, CircleSlash, Loader2, XCircle } from "lucide-react";
import {
  AlertDialog,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { isApiError } from "@/api/client";
import { deliveryApi, type DeliveryTakeoverAnswer, type OrphanedDocument } from "../../api/delivery";
import { heldCount, heldText, suggestedOf } from "./orphanedLedgers";

/** Where one document of a bulk takeover stands. */
type Step =
  | { state: "waiting" }
  | { state: "running" }
  | { state: "done"; answer: DeliveryTakeoverAnswer }
  | { state: "refused"; message: string };

const keyOf = (document: OrphanedDocument) => `${document.kind}:${document.flowName}`;

/** How a document's takeover ended, as a glyph and a line. */
function StepMark({ step }: { step: Step }) {
  if (step.state === "waiting") {
    return <CircleDashed className="size-3.5 shrink-0 text-muted-foreground" aria-label="Waiting its turn" />;
  }

  if (step.state === "running") {
    return <Loader2 className="size-3.5 shrink-0 animate-spin text-info" aria-label="Taking over" />;
  }

  if (step.state === "refused") {
    return <XCircle className="size-3.5 shrink-0 text-destructive" aria-label="Not taken over" />;
  }

  const { answer } = step;
  return answer.failed > 0
    ? <XCircle className="size-3.5 shrink-0 text-destructive" aria-label="Some ledgers not taken over" />
    : answer.skipped > 0
      ? <CircleSlash className="size-3.5 shrink-0 text-warning" aria-label="Some ledgers stay orphaned" />
      : <CheckCircle2 className="size-3.5 shrink-0 text-success" aria-label="Taken over" />;
}

/** What a document's takeover came to, in a few words. */
function stepText(step: Step): string | null {
  if (step.state === "refused") {
    return step.message;
  }

  if (step.state !== "done") {
    return null;
  }

  const { answer } = step;
  const parts = [
    answer.completed > 0 ? `${answer.completed} taken over` : null,
    answer.waiting > 0 ? `${answer.waiting} waiting for a run to end` : null,
    answer.skipped > 0 ? `${answer.skipped} ${answer.skipped === 1 ? "stays" : "stay"} orphaned` : null,
    answer.failed > 0 ? `${answer.failed} refused: ${answer.ledgers.find((l) => l.outcome === "failed")?.summary ?? ""}` : null,
  ].filter((p): p is string => p !== null);
  return parts.join("; ");
}

/**
 * Taking several missing documents over at once, each by its suggested flow (osdu/docs/reference/concepts/renaming-a-pipeline.md,
 * Missing documents): the case of a batch of renames. It lists each pair and what moves before anything does; once asked,
 * it takes the documents over one after another, as the takeover dialog does one, and says how each ended. A document
 * whose takeover is refused is left as it was, and the rest go on.
 */
export function BulkTakeoverDialog({ documents, partition, onClose, onDone }: {
  documents: OrphanedDocument[] | null;
  partition: string | null;
  onClose: () => void;
  /** Called once every document has had its turn, with the documents taken over whole. */
  onDone: (taken: string[]) => void;
}) {
  const queryClient = useQueryClient();
  const [steps, setSteps] = useState<Record<string, Step>>({});
  const [shown, setShown] = useState<OrphanedDocument[] | null>(documents);
  const [running, setRunning] = useState(false);

  // Every opening starts with nothing asked.
  if (documents !== null && documents !== shown) {
    setShown(documents);
    setSteps({});
  }

  if (shown === null) {
    return null;
  }

  const pairs = shown.map((d) => ({ document: d, to: suggestedOf(d) })).filter((p) => p.to !== undefined);
  const finished = pairs.length > 0 && pairs.every((p) => {
    const step = steps[keyOf(p.document)];
    return step !== undefined && (step.state === "done" || step.state === "refused");
  });
  const records = pairs.filter((p) => p.document.kind === "delivery").reduce((sum, p) => sum + p.document.rows, 0);

  const run = async () => {
    setRunning(true);
    const taken: string[] = [];
    for (const { document, to } of pairs) {
      const key = keyOf(document);
      setSteps((current) => ({ ...current, [key]: { state: "running" } }));
      try {
        const answer = await deliveryApi.takeOverLedgers({ kind: document.kind, from: document.flowName, to: to!.flowName, partition });
        setSteps((current) => ({ ...current, [key]: { state: "done", answer } }));
        if (answer.completed === answer.ledgers.length) {
          taken.push(key);
        }
      } catch (error) {
        const message = isApiError(error) ? error.detail ?? error.title : String(error);
        setSteps((current) => ({ ...current, [key]: { state: "refused", message } }));
      }
    }

    setRunning(false);
    // The records, statistics and trails of every flow involved changed, and so did the list of missing documents.
    void queryClient.invalidateQueries({ queryKey: ["delivery"] });
    onDone(taken);
  };

  return (
    <AlertDialog open={documents !== null} onOpenChange={(next) => (next || running ? undefined : onClose())}>
      <AlertDialogContent data-testid="bulk-takeover-dialog" className="max-h-[calc(100dvh-2rem)] grid-cols-[minmax(0,1fr)] gap-4 overflow-y-auto sm:max-w-2xl">
        <AlertDialogHeader className="gap-1 text-left">
          <AlertDialogTitle className="flex items-center gap-2">
            <ArrowRightLeft className="size-4 shrink-0 text-primary" />
            {`Take over ${pairs.length} missing ${pairs.length === 1 ? "document" : "documents"}`}
            {partition !== null && <Badge variant="secondary" className="ml-auto font-mono">{partition}</Badge>}
          </AlertDialogTitle>
          <AlertDialogDescription>
            {"Each is taken over by its suggested flow, one after another"}
            {records > 0 ? `, ${heldCount("delivery", records)} in all` : ""}
            {". A document whose takeover is refused is left as it was and says why; the others go on."}
          </AlertDialogDescription>
        </AlertDialogHeader>

        <ul className="flex flex-col divide-y divide-border rounded-md border border-border" data-testid="bulk-takeover-pairs">
          {pairs.map(({ document, to }) => {
            const step = steps[keyOf(document)] ?? { state: "waiting" };
            const text = stepText(step);
            return (
              <li key={keyOf(document)} className="flex flex-col gap-0.5 px-3 py-2 text-[12px]" data-testid="bulk-takeover-pair">
                <span className="flex min-w-0 items-center gap-2">
                  {Object.keys(steps).length > 0 && <StepMark step={step} />}
                  <span className="min-w-0 flex-1 truncate font-mono font-medium" title={document.flowName}>{document.flowName}</span>
                  <span className="shrink-0 tabular-nums text-muted-foreground">{heldText(document.kind, document.rows, document.claimed)}</span>
                </span>
                <span className="flex min-w-0 items-center gap-1.5 pl-0.5 text-muted-foreground">
                  <ArrowRight className="size-3.5 shrink-0" aria-hidden />
                  <span className="min-w-0 truncate font-mono text-foreground" title={to!.flowName}>{to!.flowName}</span>
                </span>
                {to!.takes < document.ledgers.length && step.state === "waiting" && (
                  <span className="pl-0.5 text-muted-foreground">{`takes ${to!.takes} of ${document.ledgers.length} ledgers; the others stay orphaned`}</span>
                )}
                {text !== null && (
                  <span className={cn("break-words text-muted-foreground", step.state === "refused" && "text-destructive")} data-testid="bulk-takeover-outcome">{text}</span>
                )}
              </li>
            );
          })}
        </ul>

        <p className="text-[12px] leading-snug text-muted-foreground">
          A delivery flow&apos;s records move with their history and the OSDU ids they claimed, and the flow taking them over
          delivers them from then on as updates of those ids; another kind&apos;s history moves to its flow. The audit trail
          records each takeover as yours.
        </p>

        <AlertDialogFooter>
          {finished
            ? <Button size="sm" onClick={onClose} data-testid="bulk-takeover-close">Close</Button>
            : (
              <>
                <Button variant="ghost" size="sm" onClick={onClose} disabled={running} data-testid="bulk-takeover-cancel">Cancel</Button>
                <Button size="sm" onClick={() => void run()} disabled={running || pairs.length === 0} data-testid="bulk-takeover-confirm">
                  {running ? <Loader2 className="animate-spin" /> : <ArrowRightLeft />}
                  {`Take over ${pairs.length} ${pairs.length === 1 ? "document" : "documents"}`}
                </Button>
              </>
            )}
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
