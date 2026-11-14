import { useState } from "react";
import { Link } from "react-router-dom";
import { useQueries, useQueryClient, type UseQueryResult } from "@tanstack/react-query";
import { CheckCircle2, CircleDashed, CircleSlash, Loader2, ScrollText, Trash2, TriangleAlert, XCircle } from "lucide-react";
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
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { FlowKindBadge } from "@/components/FlowKindBadge";
import { ExplainTip } from "@/components/ExplainTip";
import { RelativeTime } from "@/components/RelativeTime";
import { parseUtc } from "@/lib/time";
import { cn } from "@/lib/utils";
import { isApiError } from "@/api/client";
import { deliveryApi, type OrphanDeleteAnswer, type OrphanDeletePreview, type OrphanedDocument } from "../../api/delivery";
import {
  deletedText, deleteEffect, deleteLabel, deletePartition, deleteTitle, deletionTotals, documentKey, heldText, ledgerLabel, leftRecently, trailHref,
} from "./orphanedLedgers";

/** The missing documents a deletion was opened for, and when: what says which of them left only just now. */
export interface OrphanDeletion {
  documents: OrphanedDocument[];
  atMs: number;
}

/** Where one document of a deletion stands once it is asked. */
type Step =
  | { state: "running" }
  | { state: "done"; answer: OrphanDeleteAnswer }
  | { state: "refused"; message: string };

const message = (error: unknown) => (isApiError(error) ? error.detail ?? error.title : String(error));

/** How a document's deletion ended, or that it has not had its turn, as a glyph. */
function StepMark({ step }: { step: Step | undefined }) {
  if (step === undefined) {
    return <CircleDashed className="size-3.5 shrink-0 text-muted-foreground" aria-label="Not deleted" />;
  }

  if (step.state === "running") {
    return <Loader2 className="size-3.5 shrink-0 animate-spin text-info" aria-label="Deleting" />;
  }

  if (step.state === "refused" || step.answer.failed > 0) {
    return <XCircle className="size-3.5 shrink-0 text-destructive" aria-label="Not deleted" />;
  }

  return step.answer.waiting > 0
    ? <Loader2 className="size-3.5 shrink-0 text-info" aria-label="Waiting" />
    : <CheckCircle2 className="size-3.5 shrink-0 text-success" aria-label="Deleted" />;
}

/**
 * One missing document of a deletion: what it left and when, then what its preview says of each ledger that stays, and
 * once the deletion is asked, how each of its ledgers ended.
 */
function DocumentRow({ document, preview, step, ran, onLeave }: {
  document: OrphanedDocument;
  preview: UseQueryResult<OrphanDeletePreview>;
  step: Step | undefined;
  ran: boolean;
  onLeave: () => void;
}) {
  const several = document.ledgers.length > 1;
  const staying = preview.data?.ledgers.filter((l) => l.refusal) ?? [];
  const nothingGoes = preview.isSuccess && preview.data.deletable === 0;
  return (
    <li className="flex flex-col gap-0.5 px-3 py-2 text-[12px]" data-testid="orphan-delete-row">
      <span className="flex min-w-0 items-center gap-2">
        {ran && <StepMark step={step} />}
        {document.kind !== "delivery" && <FlowKindBadge kind={document.kind} />}
        <span className={cn("min-w-0 flex-1 truncate font-mono font-medium", nothingGoes && "text-muted-foreground")} title={document.flowName}>{document.flowName}</span>
        <span className="shrink-0 tabular-nums text-muted-foreground">{heldText(document.kind, document.rows, document.claimed)}</span>
      </span>
      <span className="flex min-w-0 items-center gap-1 text-muted-foreground">
        {document.leftUtc && <span className="shrink-0">left <RelativeTime value={document.leftUtc} absolute={false} /></span>}
        {document.leftUtc && document.relativePath && <span aria-hidden>·</span>}
        {document.relativePath && <span className="min-w-0 truncate font-mono" title={document.relativePath}>{document.relativePath}</span>}
      </span>

      {preview.isPending && !ran && (
        <span className="flex items-center gap-1.5 text-muted-foreground"><Loader2 className="size-3 animate-spin" aria-hidden />Checking what it holds</span>
      )}
      {preview.isError && !ran && (
        <span className="break-words text-destructive" data-testid="orphan-delete-preview-error">{message(preview.error)}</span>
      )}

      {step === undefined && staying.map((ledger) => (
        <ExplainTip key={ledger.ledger} title="Why it stays" text={ledger.refusal ?? ""} testId="orphan-delete-refusal" side="right">
          <span className="flex w-fit min-w-0 cursor-help items-start gap-1.5 leading-snug" tabIndex={0} data-testid="orphan-delete-refusal">
            <CircleSlash className="mt-0.5 size-3.5 shrink-0 text-warning" aria-hidden />
            <span className="min-w-0 break-words">
              <span className="font-medium">{several ? `${ledgerLabel(ledger)} stays: ` : "Stays: "}</span>
              <span className="text-muted-foreground underline decoration-dotted underline-offset-2">{ledger.refusedFor ?? ledger.refusal}</span>
            </span>
          </span>
        </ExplainTip>
      ))}

      {step?.state === "refused" && <span className="break-words text-destructive" data-testid="orphan-delete-outcome">{step.message}</span>}
      {step?.state === "done" && step.answer.ledgers.map((result) => (
        <span key={result.ledger} className="flex items-start gap-1.5 leading-snug" data-testid={`orphan-delete-outcome-${result.outcome}`}>
          {result.outcome === "failed"
            ? (
              <ExplainTip title="Why it was not deleted" text={result.summary} testId="orphan-delete-failed" side="right">
                <span className="min-w-0 flex-1 cursor-help break-words text-destructive" tabIndex={0}>
                  {several && <span className="font-medium text-foreground">{`${ledgerLabel(result)}: `}</span>}
                  <span className="underline decoration-dotted underline-offset-2">
                    {`Not deleted: ${preview.data?.ledgers.find((l) => l.ledger === result.ledger)?.refusedFor ?? "see why"}`}
                  </span>
                </span>
              </ExplainTip>
            )
            : (
              <span className="min-w-0 flex-1 break-words text-muted-foreground" title={result.summary}>
                {several && <span className="font-medium text-foreground">{`${ledgerLabel(result)}: `}</span>}
                {deletedText(document.kind, result)}
              </span>
            )}
          {result.activityId > 0 && (
            <Tooltip>
              <TooltipTrigger asChild>
                <Button variant="ghost" size="icon" className="size-6 shrink-0 text-muted-foreground" asChild>
                  <Link to={trailHref(result.ledger, result.partition)} onClick={onLeave} aria-label="Open its audit trail" data-testid="orphan-delete-trail">
                    <ScrollText className="size-3.5" />
                  </Link>
                </Button>
              </TooltipTrigger>
              <TooltipContent>Open its audit trail, which keeps this deletion</TooltipContent>
            </Tooltip>
          )}
        </span>
      ))}
    </li>
  );
}

/**
 * Deleting what missing documents left behind (osdu/docs/reference/concepts/renaming-a-pipeline.md, Deleting what a missing
 * document left behind), one document or a selection: for the flows no flow is to take over. Before anything goes it asks
 * the control plane what each ledger holds and whether it can go, lists every document with what stays and why, says when
 * one left only just now, and asks for the partition to be typed back; the button says what goes. Once asked, the
 * documents are deleted one after another, and each says how it ended, with a link to the audit trail that keeps the
 * deletion. A ledger that knows something of OSDU nothing else does is never deleted, and nothing is asked of OSDU.
 */
export function DeleteOrphanedDialog({ deletion, partition, onClose, onDone }: {
  deletion: OrphanDeletion | null;
  partition: string | null;
  onClose: () => void;
  /** Called once every document has had its turn, with the documents deleted whole. */
  onDone: (deleted: string[]) => void;
}) {
  const queryClient = useQueryClient();
  const [shown, setShown] = useState<OrphanDeletion | null>(deletion);
  const [steps, setSteps] = useState<Record<string, Step>>({});
  const [typed, setTyped] = useState("");
  const [running, setRunning] = useState(false);

  // Every opening starts with nothing typed and nothing asked.
  if (deletion !== null && deletion !== shown) {
    setShown(deletion);
    setSteps({});
    setTyped("");
  }

  const documents = shown?.documents ?? [];
  const where = deletePartition(documents, partition);
  const ran = Object.keys(steps).length > 0;
  const previews = useQueries({
    queries: documents.map((document) => ({
      queryKey: ["orphaned-delete-preview", where, document.kind, document.flowName],
      queryFn: () => deliveryApi.previewOrphanedDelete({ kind: document.kind, flow: document.flowName, partition: where ?? "" }),
      // Asked afresh at every opening, and never again once the deletion is asked: what it shows is what was confirmed.
      enabled: deletion !== null && where !== null && !ran,
      staleTime: 0,
      gcTime: 0,
      retry: false,
    })),
  });

  if (shown === null) {
    return null;
  }

  const one = documents.length === 1 ? documents[0] : null;
  // A preview kept from an earlier opening is shown only until its fresh answer comes: nothing is asked on it.
  const settled = previews.every((p) => !p.isFetching && (p.isSuccess || p.isError));
  const totals = deletionTotals(documents.flatMap((d, i) => (previews[i]?.data ? [{ kind: d.kind, ledgers: previews[i].data.ledgers }] : [])));
  const recent = documents.filter((d) => leftRecently(d.leftUtc ? parseUtc(d.leftUtc).getTime() : null, shown.atMs)).length;
  const finished = ran && !running;
  const confirmed = where !== null && typed.trim() === where;
  const partitions = [...new Set(documents.flatMap((d) => d.ledgers.map((l) => l.partition)))];

  const run = async () => {
    if (where === null) {
      return;
    }

    setRunning(true);
    const deleted: string[] = [];
    for (const [index, document] of documents.entries()) {
      const preview = previews[index]?.data;
      if (preview === undefined || preview.deletable === 0) {
        continue;
      }

      const key = documentKey(document);
      setSteps((current) => ({ ...current, [key]: { state: "running" } }));
      try {
        const answer = await deliveryApi.deleteOrphanedLedgers({ kind: document.kind, flow: document.flowName, partition: where, confirm: typed.trim() });
        setSteps((current) => ({ ...current, [key]: { state: "done", answer } }));
        if (answer.completed === answer.ledgers.length) {
          deleted.push(key);
        }
      } catch (error) {
        setSteps((current) => ({ ...current, [key]: { state: "refused", message: message(error) } }));
      }
    }

    setRunning(false);
    // The list of missing documents changed, and so did the audit trails of the ledgers deleted.
    void queryClient.invalidateQueries({ queryKey: ["delivery"] });
    onDone(deleted);
  };

  const close = () => {
    if (!running) {
      onClose();
    }
  };

  return (
    <AlertDialog open={deletion !== null} onOpenChange={(next) => (next ? undefined : close())}>
      <AlertDialogContent data-testid="orphan-delete-dialog" className="max-h-[calc(100dvh-2rem)] grid-cols-[minmax(0,1fr)] gap-4 overflow-y-auto sm:max-w-2xl">
        <AlertDialogHeader className="gap-1 text-left">
          <div className="flex items-center gap-2">
            <AlertDialogTitle className="flex min-w-0 flex-1 items-center gap-2">
              <Trash2 className="size-4 shrink-0 text-destructive" />
              <span className="truncate">{deleteTitle(documents)}</span>
            </AlertDialogTitle>
            {where !== null && <Badge variant="secondary" className="font-mono">{where}</Badge>}
          </div>
          <AlertDialogDescription>
            {one === null ? "For flows no other flow will take over. Their history goes for good" : "For a flow no other flow will take over. Its history goes for good"}
            ; nothing is removed from OSDU, and a ledger that is the only record of something in OSDU stays and says why.
          </AlertDialogDescription>
        </AlertDialogHeader>

        {where === null
          ? (
            <p className="text-[13px]" data-testid="orphan-delete-partitions">
              {`These ledgers are kept in ${partitions.join(", ")}. Pick one partition in the workbench first: a deletion is made, and confirmed, one partition at a time.`}
            </p>
          )
          : (
            <>
              <ul className="flex max-h-[45dvh] flex-col divide-y divide-border overflow-y-auto rounded-md border border-border" data-testid="orphan-delete-rows">
                {documents.map((document, index) => (
                  <DocumentRow
                    key={documentKey(document)}
                    document={document}
                    preview={previews[index]}
                    step={steps[documentKey(document)]}
                    ran={ran}
                    onLeave={onClose}
                  />
                ))}
              </ul>

              {!ran && recent > 0 && (
                <p className="flex items-start gap-2 text-[12px] leading-snug" data-testid="orphan-delete-recent">
                  <TriangleAlert className="mt-0.5 size-3.5 shrink-0 text-warning" aria-hidden />
                  <span>
                    <span className="font-medium">{one !== null ? "It left less than a day ago. " : `${recent} of these left less than a day ago. `}</span>
                    <span className="text-muted-foreground">
                      If a repository synced the wrong branch, or a file is on its way back, sync it again instead: a flow
                      declared again keeps its ledger.
                    </span>
                  </span>
                </p>
              )}

              {!ran && (
                <p className="text-[12px] leading-snug text-muted-foreground" data-testid="orphan-delete-effect">
                  {deleteEffect(documents.map((d) => d.kind))}
                </p>
              )}

              {!ran && settled && totals.ledgers === 0 && (
                <p className="text-[13px]" data-testid="orphan-delete-none">
                  {documents.length === 1 ? "It cannot be deleted now; it says why above." : "None of these can be deleted now; each says why above."}
                </p>
              )}

              {!ran && totals.ledgers > 0 && (
                <div className="flex flex-col gap-1.5">
                  <Label htmlFor="orphan-delete-confirm" className="text-[12px] font-normal text-muted-foreground">
                    <span>
                      {totals.staying > 0 ? `${totals.staying.toLocaleString("en-US")} ${totals.staying === 1 ? "ledger stays" : "ledgers stay"}; the rest cannot be brought back. ` : "This cannot be undone. "}
                      {"Type "}
                      <span className="font-mono font-medium text-foreground">{where}</span> to confirm.
                    </span>
                  </Label>
                  <Input
                    id="orphan-delete-confirm"
                    value={typed}
                    onChange={(event) => setTyped(event.target.value)}
                    disabled={running}
                    autoComplete="off"
                    spellCheck={false}
                    className="h-8 font-mono"
                    data-testid="orphan-delete-confirm-input"
                  />
                </div>
              )}
            </>
          )}

        <AlertDialogFooter>
          {finished || where === null || (settled && totals.ledgers === 0)
            ? <Button size="sm" onClick={onClose} data-testid="orphan-delete-close">Close</Button>
            : (
              <>
                <Button variant="ghost" size="sm" onClick={close} disabled={running} data-testid="orphan-delete-cancel">Cancel</Button>
                <Button variant="destructive" size="sm" onClick={() => void run()} disabled={running || !settled || totals.ledgers === 0 || !confirmed} data-testid="orphan-delete-confirm">
                  {running || !settled ? <Loader2 className="animate-spin" /> : <Trash2 />}
                  {deleteLabel(totals)}
                </Button>
              </>
            )}
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
