import { useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { ArrowRight, ArrowRightLeft, CheckCircle2, CircleSlash, Loader2, PackageSearch, ScrollText, Sparkles, XCircle } from "lucide-react";
import { toast } from "sonner";
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
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { PathRef } from "@/components/PathRef";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { cn } from "@/lib/utils";
import { isApiError } from "@/api/client";
import { deliveryApi, type DeliveryTakeoverAnswer, type LedgerMoveResult, type OrphanedDocument, type TakeoverCandidate } from "../../api/delivery";
import { ChoiceRow, PartHeading } from "./RemovalDialog";
import { destinationOf, heldCount, heldText, ledgerLabel, recordsHref, SHARES, suggestedOf, trailHref } from "./orphanedLedgers";

/** How many flows the list shows before it offers a search. */
const SEARCH_FROM = 6;

/** How one ledger's move ended: its glyph, colour and word. */
const OUTCOME: Record<LedgerMoveResult["outcome"], { icon: typeof CheckCircle2; className: string; word: string }> = {
  completed: { icon: CheckCircle2, className: "text-success", word: "Taken over" },
  running: { icon: Loader2, className: "text-info", word: "Waiting" },
  failed: { icon: XCircle, className: "text-destructive", word: "Not taken over" },
  skipped: { icon: CircleSlash, className: "text-muted-foreground", word: "Stays orphaned" },
};

/** A small link out of the dialog to a page about one ledger, with what it opens on hover. */
function LedgerLink({ to, label, icon: Icon, onLeave, testId }: { to: string; label: string; icon: typeof ScrollText; onLeave: () => void; testId: string }) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Button variant="ghost" size="icon" className="size-6 text-muted-foreground" asChild>
          <Link to={to} onClick={onLeave} aria-label={label} data-testid={testId}>
            <Icon className="size-3.5" />
          </Link>
        </Button>
      </TooltipTrigger>
      <TooltipContent>{label}</TooltipContent>
    </Tooltip>
  );
}

/** One flow the missing document can be taken over by, as a choice: its name, file, what it shares and how much it takes. */
function Candidate({ candidate, ledgers, active, disabled }: { candidate: TakeoverCandidate; ledgers: number; active: boolean; disabled: boolean }) {
  const id = `takeover-${candidate.repoId}-${candidate.flowName}`;
  return (
    <ChoiceRow active={active} permanent={false} unavailable={false}>
      <label htmlFor={id} className="flex min-w-0 flex-1 cursor-pointer items-start gap-2.5 py-2">
        <RadioGroupItem id={id} value={candidate.flowName} className="mt-0.5" disabled={disabled} data-testid={`takeover-candidate-${candidate.flowName}`} />
        <span className="flex min-w-0 flex-1 flex-col gap-1">
          <span className="flex min-w-0 items-center gap-1.5">
            <span className="truncate font-mono text-[13px] font-medium leading-tight" title={candidate.flowName}>{candidate.flowName}</span>
            {candidate.suggested && (
              <span className="inline-flex shrink-0 items-center gap-1 text-[11px] text-muted-foreground" title="The one flow that shares the most with the missing one">
                <Sparkles className="size-3 text-primary" aria-hidden />
                suggested
              </span>
            )}
          </span>
          <span className="flex min-w-0 flex-wrap items-center gap-1 text-[12px] text-muted-foreground">
            <PathRef path={candidate.relativePath} maxWidth={220} />
            {candidate.shares.map((share) => (
              <Badge key={share} variant="secondary" className="h-4 px-1 text-[10px] font-normal" title={SHARES[share]?.hint}>
                {SHARES[share]?.label ?? share}
              </Badge>
            ))}
            {candidate.takes < ledgers && (
              <span title="A ledger it keeps no counterpart of stays orphaned">{`takes ${candidate.takes} of ${ledgers} ledgers`}</span>
            )}
          </span>
        </span>
      </label>
    </ChoiceRow>
  );
}

/** Where each ledger of the missing document would go under the flow picked: the plan the takeover follows. */
function Plan({ document, candidate }: { document: OrphanedDocument; candidate: TakeoverCandidate }) {
  return (
    <ul className="grid grid-cols-[minmax(0,max-content)_auto_auto_minmax(0,1fr)] items-center gap-x-2 gap-y-1 rounded-md border border-border bg-muted/30 px-3 py-2 text-[12px]" data-testid="takeover-plan">
      {document.ledgers.map((ledger) => {
        const to = destinationOf(document.kind, candidate, ledger);
        return (
          <li key={ledger.ledger} className="contents" data-testid={to === null ? "takeover-plan-stays" : "takeover-plan-moves"}>
            <span className="min-w-0 truncate font-mono" title={ledger.ledgerName}>{ledgerLabel(ledger)}</span>
            <span className="text-right tabular-nums text-muted-foreground">{heldCount(document.kind, ledger.rows)}</span>
            <ArrowRight className="size-3.5 text-muted-foreground" aria-hidden />
            {to === null
              ? (
                <span className="min-w-0 truncate text-muted-foreground" title={`'${candidate.flowName}' keeps no counterpart of this ledger${ledger.interface === "" ? "" : `: it declares no interface '${ledger.interface}'`}`}>
                  stays orphaned
                </span>
              )
              : <span className="min-w-0 truncate font-mono font-medium" title={to}>{to}</span>}
          </li>
        );
      })}
    </ul>
  );
}

/** What one ledger's move came to, in a line: what moved and what gave way, or why nothing did. */
function outcomeText(kind: string, move: LedgerMoveResult): string {
  switch (move.outcome) {
    case "completed":
      return `${heldCount(kind, move.records)} moved`
        + (move.superseded > 0 ? `; ${move.superseded.toLocaleString("en-US")} the flow had held gave way to them.` : ".");
    case "running":
      return "It waits for a run working on either ledger to end; the control plane finishes it then.";
    default:
      return move.summary;
  }
}

/** What the takeover did to each ledger, once it is asked. */
function Outcomes({ answer, document, onLeave }: { answer: DeliveryTakeoverAnswer; document: OrphanedDocument; onLeave: () => void }) {
  return (
    <div className="flex flex-col gap-2" data-testid="takeover-outcomes">
      <ul className="flex flex-col gap-1.5">
        {answer.ledgers.map((move) => {
          const ledger = document.ledgers.find((l) => l.ledger === move.from);
          const outcome = OUTCOME[move.outcome] ?? OUTCOME.failed;
          const Icon = outcome.icon;
          return (
            <li key={`${move.partition}-${move.from}`} className="flex items-start gap-2 text-[12px] leading-snug" data-testid={`takeover-outcome-${move.outcome}`}>
              <Icon className={cn("mt-0.5 size-3.5 shrink-0", outcome.className, move.outcome === "running" && "animate-spin")} aria-hidden />
              <span className="min-w-0 flex-1">
                <span className="font-medium">{`${outcome.word}: ${ledger === undefined ? move.partition : ledgerLabel(ledger)}. `}</span>
                <span className={cn("break-words text-muted-foreground", move.outcome === "failed" && "text-destructive")} title={move.summary}>
                  {outcomeText(document.kind, move)}
                </span>
              </span>
              {move.outcome === "completed" && (
                <LedgerLink to={trailHref(move.to, move.partition)} label="Open the audit trail it moved to" icon={ScrollText} onLeave={onLeave} testId="takeover-outcome-trail" />
              )}
            </li>
          );
        })}
      </ul>
    </div>
  );
}

/**
 * Handing a missing document's ledgers to a flow a repository declares (osdu/docs/reference/concepts/renaming-a-pipeline.md,
 * Missing documents). It says what the missing flow left behind, each ledger with links to its records and its audit trail;
 * offers the declared flows of its kind that can take it over, those sharing its mapping document, record table, interface
 * or folder first, the suggested one picked to start with; shows where each ledger would go under the flow picked; and says
 * what the takeover does. Once asked, each ledger moves at once, and the dialog says how each one ended.
 */
export function TakeoverDialog({ document, partition, onClose }: { document: OrphanedDocument | null; partition: string | null; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [shown, setShown] = useState<OrphanedDocument | null>(document);
  const [picked, setPicked] = useState("");
  const [search, setSearch] = useState("");
  const [answer, setAnswer] = useState<DeliveryTakeoverAnswer | null>(null);

  // Every opening starts from the suggestion, with nothing searched and no answer from the last one.
  if (document !== null && document !== shown) {
    setShown(document);
    setPicked(suggestedOf(document)?.flowName ?? "");
    setSearch("");
    setAnswer(null);
  }

  const takeOver = useMutation({
    mutationFn: (to: string) => deliveryApi.takeOverLedgers({ kind: shown!.kind, from: shown!.flowName, to, partition }),
    onSuccess: (result) => {
      setAnswer(result);
      // The records, statistics and trails of both flows changed, and so did the list of missing documents.
      void queryClient.invalidateQueries({ queryKey: ["delivery"] });
    },
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });

  const candidates = useMemo(() => {
    const all = shown?.candidates ?? [];
    const term = search.trim().toLowerCase();
    return term === "" ? all : all.filter((c) => c.flowName.toLowerCase().includes(term) || c.relativePath.toLowerCase().includes(term));
  }, [shown, search]);

  if (shown === null) {
    return null;
  }

  const busy = takeOver.isPending;
  const partitions = [...new Set(shown.ledgers.map((l) => l.partition))];
  const chosen = shown.candidates.find((c) => c.flowName === picked);
  const moving = chosen === undefined ? 0 : shown.ledgers.filter((l) => destinationOf(shown.kind, chosen, l) !== null).reduce((sum, l) => sum + l.rows, 0);
  const close = () => {
    if (!busy) {
      onClose();
    }
  };

  return (
    <AlertDialog open={document !== null} onOpenChange={(next) => (next ? undefined : close())}>
      <AlertDialogContent data-testid="takeover-dialog" className="max-h-[calc(100dvh-2rem)] grid-cols-[minmax(0,1fr)] gap-4 overflow-y-auto sm:max-w-xl">
        <AlertDialogHeader className="gap-1 text-left">
          <div className="flex items-center gap-2">
            <AlertDialogTitle className="flex min-w-0 flex-1 items-center gap-2">
              <ArrowRightLeft className="size-4 shrink-0 text-primary" />
              <span className="truncate" title={shown.flowName}>Take over <span className="font-mono">{shown.flowName}</span></span>
            </AlertDialogTitle>
            {partitions.map((p) => <Badge key={p} variant="secondary" className="font-mono">{p}</Badge>)}
          </div>
          <AlertDialogDescription className="flex flex-col gap-0.5" data-testid="takeover-subject">
            <span>
              {`No repository declares it any more. It left ${heldText(shown.kind, shown.rows, shown.claimed)} behind`}
              {shown.ledgers.length > 1 ? `, in ${shown.ledgers.length} ledgers.` : "."}
            </span>
            {shown.relativePath && (
              <span className="flex min-w-0 items-center gap-1 text-[12px]">
                <span className="shrink-0">Last declared in</span>
                <span className="min-w-0 truncate font-mono text-foreground" title={shown.relativePath}>{shown.relativePath}</span>
                {shown.leftUtc && <span className="shrink-0">· left <RelativeTime value={shown.leftUtc} absolute={false} /></span>}
              </span>
            )}
          </AlertDialogDescription>
        </AlertDialogHeader>

        {answer === null
          ? (
            <>
              <div className="flex flex-col gap-1.5">
                <PartHeading>What it left behind</PartHeading>
                <ul className="flex flex-col gap-0.5 text-[12px]" data-testid="takeover-ledgers">
                  {shown.ledgers.map((l) => (
                    <li key={l.ledger} className="flex min-w-0 items-center gap-2">
                      <span className="min-w-0 flex-1 truncate font-mono" title={l.ledgerName}>{ledgerLabel(l)}</span>
                      <span className="shrink-0 tabular-nums text-muted-foreground">{heldText(shown.kind, l.rows, l.claimed)}</span>
                      {shown.kind === "delivery" && (
                        <LedgerLink to={recordsHref(l)} label="Open its records" icon={PackageSearch} onLeave={onClose} testId="takeover-ledger-records" />
                      )}
                      <LedgerLink to={trailHref(l.ledger, l.partition)} label="Open its audit trail" icon={ScrollText} onLeave={onClose} testId="takeover-ledger-trail" />
                    </li>
                  ))}
                </ul>
              </div>

              <div className="flex flex-col gap-1.5">
                <PartHeading>Taken over by</PartHeading>
                {shown.candidates.length > SEARCH_FROM && (
                  <SearchInput value={search} onChange={setSearch} placeholder="Search flows or files" label="Search the flows" testId="takeover-search" className="sm:w-full" />
                )}
                {shown.candidates.length === 0
                  ? (
                    <p className="text-[12px] text-muted-foreground" data-testid="takeover-none">
                      {`No repository declares a ${shown.kind} flow that keeps a ledger in ${partitions.join(", ")}. Add the flow that replaces it, sync its repository, and take it over then.`}
                    </p>
                  )
                  : (
                    <RadioGroup value={picked} onValueChange={setPicked} disabled={busy} aria-label="Taken over by" className="max-h-64 gap-1.5 overflow-y-auto pr-1">
                      {candidates.map((c) => (
                        <Candidate key={`${c.repoId}-${c.flowName}`} candidate={c} ledgers={shown.ledgers.length} active={c.flowName === picked} disabled={busy} />
                      ))}
                      {candidates.length === 0 && <p className="text-[12px] text-muted-foreground">No flow matches.</p>}
                    </RadioGroup>
                  )}
              </div>

              {chosen !== undefined && (
                <div className="flex flex-col gap-1.5">
                  <PartHeading>What goes where</PartHeading>
                  <Plan document={shown} candidate={chosen} />
                </div>
              )}

              {shown.candidates.length > 0 && (
              <p className="text-[12px] leading-snug text-muted-foreground" data-testid="takeover-effect">
                {shown.kind === "delivery"
                  ? "Each record moves with its history and the OSDU id it claimed, and the flow delivers it from then on as an update of that id; a record the flow held because that id was claimed gives way. "
                  : "Its history moves to the flow, which carries on from it. "}
                The audit trail records the takeover as yours.
              </p>
              )}
            </>
          )
          : <Outcomes answer={answer} document={shown} onLeave={onClose} />}

        <AlertDialogFooter>
          {answer === null && shown.candidates.length === 0
            ? <Button size="sm" onClick={onClose} data-testid="takeover-cancel">Close</Button>
            : answer === null
            ? (
              <>
                <Button variant="ghost" size="sm" onClick={close} disabled={busy} data-testid="takeover-cancel">Cancel</Button>
                <Button size="sm" onClick={() => takeOver.mutate(picked)} disabled={busy || chosen === undefined || chosen.takes === 0} data-testid="takeover-confirm">
                  {busy ? <Loader2 className="animate-spin" /> : <ArrowRightLeft />}
                  {chosen === undefined || moving === 0 ? "Take over" : `Take over ${heldCount(shown.kind, moving)}`}
                </Button>
              </>
            )
            : <Button size="sm" onClick={onClose} data-testid="takeover-close">Close</Button>}
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
