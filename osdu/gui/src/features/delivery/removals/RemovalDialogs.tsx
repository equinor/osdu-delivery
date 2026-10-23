import { useState, type ReactNode } from "react";
import { useMutation } from "@tanstack/react-query";
import { Loader2, RotateCcw, ShieldCheck } from "lucide-react";
import { toast } from "sonner";
import {
  AlertDialog, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { isApiError } from "@/api/client";
import type { DeliveryFlowScope } from "../../../api/delivery";
import {
  MAX_LIFT, removalApi, type RemovalApprovalAccepted, type RemovalEntry, type RemovalLiftResult, type RemovalRun,
} from "../../../api/removals";
import { ChoiceRow, PartHeading, Permanence } from "../RemovalDialog";
import { heldText, ledgerName } from "./removalFormat";

/** One step of what a dialog's action does, in the frame the removal dialog gives each of its choices. */
function Step({ title, effect, reversible, testId }: { title: string; effect: ReactNode; reversible: boolean; testId: string }) {
  return (
    <ChoiceRow active permanent={!reversible} unavailable={false}>
      <span className="flex min-w-0 flex-1 flex-col gap-0.5 py-2" data-testid={testId}>
        <span className="text-[13px] font-medium leading-tight">{title}</span>
        <span className="text-[12px] leading-snug text-muted-foreground">{effect}</span>
      </span>
      <span className="flex items-center pt-2 pr-2">
        <Permanence reversible={reversible} />
      </span>
    </ChoiceRow>
  );
}

function failure(error: unknown): string {
  return isApiError(error) ? error.detail ?? error.title : String(error);
}

/**
 * Delivering records again whose removal a removal flow found (osdu/docs/reference/flow/removal.md, Delivering a record
 * again): the ledger stops keeping their keys out, and a record the removal took out is planned again, so it is delivered
 * under its id, at once when the operator leaves "Deliver now" on. It says plainly that the removal flow's next run finds a
 * key removed again while its source still does not hold it. Nothing is permanent, so nothing is typed back.
 */
export function LiftDialog({ open, onClose, pipelineId, scope, flowName, keys, onLifted }: {
  open: boolean;
  onClose: () => void;
  /** The delivery flow's pipeline, whose ledger keeps the deletions. */
  pipelineId: string;
  scope?: DeliveryFlowScope;
  /** The ledger as the page names it. */
  flowName: string;
  /** The delivery keys picked, at most {@link MAX_LIFT}. */
  keys: string[];
  onLifted: (result: RemovalLiftResult) => void;
}) {
  const [now, setNow] = useState(true);
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setNow(true);
    }
  }

  const lift = useMutation({
    mutationFn: () => removalApi.lift(pipelineId, keys, scope, now),
    onSuccess: (result) => {
      onLifted(result);
      onClose();
    },
    onError: (error) => toast.error(failure(error)),
  });
  const busy = lift.isPending;
  const count = keys.length;
  const tooMany = count > MAX_LIFT;

  return (
    <AlertDialog open={open} onOpenChange={(next) => !next && !busy && onClose()}>
      <AlertDialogContent data-testid="lift-dialog" className="max-h-[calc(100dvh-2rem)] max-w-lg gap-4 overflow-y-auto">
        <AlertDialogHeader className="gap-1 text-left">
          <div className="flex items-center gap-2">
            <AlertDialogTitle className="flex min-w-0 flex-1 items-center gap-2">
              <RotateCcw className="size-4 shrink-0 text-primary" />
              Deliver again
            </AlertDialogTitle>
            <Badge variant="secondary" className="font-mono tabular-nums" data-testid="lift-count">{count.toLocaleString("en-US")}</Badge>
          </div>
          <AlertDialogDescription className="break-words" data-testid="lift-subject">
            {`${count.toLocaleString("en-US")} record${count === 1 ? "" : "s"} of ${flowName} a removal flow found its source no longer holds`}
          </AlertDialogDescription>
        </AlertDialogHeader>

        <div className="flex flex-col gap-1.5">
          <PartHeading>In the ledger</PartHeading>
          <Step
            title="Stop keeping their keys out"
            effect="The deletions are lifted. A record the removal took out is planned again; one it had not reached yet stays as it is."
            reversible
            testId="lift-ledger"
          />
        </div>
        <div className="flex flex-col gap-1.5">
          <PartHeading>In OSDU</PartHeading>
          <Step
            title="Deliver them again under their ids"
            effect={now ? "A deliver run queued now reads their rows and delivers them." : "The flow's next run reads their rows and delivers them."}
            reversible
            testId="lift-osdu"
          />
        </div>
        <p className="text-[12px] text-muted-foreground" data-testid="lift-note">
          While the source still does not hold a key, the removal flow&apos;s next run finds it removed again. Bring the key back in the source to keep the record.
        </p>
        <Label className="flex items-center gap-2 text-[13px] font-normal">
          <Switch checked={now} onCheckedChange={setNow} disabled={busy} data-testid="lift-now" />
          Deliver now
        </Label>
        {tooMany && (
          <p className="text-[12px] text-destructive" data-testid="lift-too-many">
            {`A lift names at most ${MAX_LIFT.toLocaleString("en-US")} records; pick fewer.`}
          </p>
        )}

        <AlertDialogFooter>
          <Button variant="ghost" size="sm" onClick={onClose} disabled={busy} data-testid="lift-cancel">Cancel</Button>
          <Button size="sm" onClick={() => lift.mutate()} disabled={busy || count === 0 || tooMany} data-testid="lift-confirm">
            {busy ? <Loader2 className="animate-spin" /> : <RotateCcw />}
            Deliver again
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}

/**
 * Approving the deletions a removal's guard held (osdu/docs/reference/flow/removal.md, The guard): why the run held them,
 * what the approval does in the ledger and in OSDU, and the count it is given for. Nothing is queued until the partition is
 * typed back; the run it queues checks the count again on the node, and approves nothing when another number waits.
 */
export function ApproveDialog({ open, onClose, pipelineId, partition, entry, run, onQueued, onReview }: {
  open: boolean;
  onClose: () => void;
  /** The removal flow's pipeline. */
  pipelineId: string;
  partition: string;
  entry: RemovalEntry;
  /** The run that held them, when the page knows it. */
  run?: RemovalRun;
  onQueued: (accepted: RemovalApprovalAccepted) => void;
  /** Shows the held deletions instead, closing the dialog. */
  onReview: () => void;
}) {
  const [typed, setTyped] = useState("");
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setTyped("");
    }
  }

  const count = entry.awaitingApproval;
  const approve = useMutation({
    mutationFn: () => removalApi.approve(pipelineId, { entry: entry.name, expected: count, confirm: typed.trim() }),
    onSuccess: (accepted) => {
      onQueued(accepted);
      onClose();
    },
    onError: (error) => toast.error(failure(error)),
  });
  const busy = approve.isPending;
  const confirmed = typed.trim() === partition;

  return (
    <AlertDialog open={open} onOpenChange={(next) => !next && !busy && onClose()}>
      <AlertDialogContent data-testid="approve-dialog" className="max-h-[calc(100dvh-2rem)] max-w-lg gap-4 overflow-y-auto">
        <AlertDialogHeader className="gap-1 text-left">
          <div className="flex items-center gap-2">
            <AlertDialogTitle className="flex min-w-0 flex-1 items-center gap-2">
              <ShieldCheck className="size-4 shrink-0 text-warning" />
              Approve held removals
            </AlertDialogTitle>
            <Badge variant="secondary" className="font-mono" data-testid="approve-partition">{partition}</Badge>
          </div>
          <AlertDialogDescription className="break-words" data-testid="approve-subject">
            {`${entry.name}: ${count.toLocaleString("en-US")} removed key${count === 1 ? "" : "s"} of ${ledgerName(entry)}`}
          </AlertDialogDescription>
        </AlertDialogHeader>

        {run !== undefined && (
          <p className="text-[12.5px] leading-snug" data-testid="approve-why">
            {`Removal run ${run.removalRunId} held them: ${heldText(run)}. A source that suddenly holds far fewer keys is more often a broken read than a real mass removal.`}
          </p>
        )}
        <Button variant="link" size="sm" className="h-6 self-start px-0 text-[13px]" onClick={() => { onClose(); onReview(); }} data-testid="approve-review">
          Review the held keys first
        </Button>

        <div className="flex flex-col gap-1.5">
          <PartHeading>In the ledger</PartHeading>
          <Step
            title={`Make the ${count.toLocaleString("en-US")} deletions pending`}
            effect="Approved under your name. From then on no row of these keys older than their removal is delivered."
            reversible
            testId="approve-ledger"
          />
        </div>
        <div className="flex flex-col gap-1.5">
          <PartHeading>In OSDU</PartHeading>
          <Step
            title="Remove their records"
            effect={entry.keepReferenced
              ? "The run this queues takes each out with a soft delete, which OSDU can undo. A record other records still name stays until they are gone."
              : "The run this queues takes each out with a soft delete, which OSDU can undo, whether other records name it or not."}
            reversible
            testId="approve-osdu"
          />
        </div>

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="approve-confirm" className="text-[12px] font-normal text-muted-foreground">
            <span>
              {"The run approves only if exactly "}
              <span className="font-medium text-foreground">{count.toLocaleString("en-US")}</span>
              {" still wait. Type "}
              <span className="font-mono font-medium text-foreground">{partition}</span>
              {" to confirm."}
            </span>
          </Label>
          <Input
            id="approve-confirm"
            value={typed}
            onChange={(event) => setTyped(event.target.value)}
            disabled={busy}
            autoComplete="off"
            spellCheck={false}
            className="h-8 font-mono"
            data-testid="approve-confirm-input"
          />
        </div>

        <AlertDialogFooter>
          <Button variant="ghost" size="sm" onClick={onClose} disabled={busy} data-testid="approve-cancel">Cancel</Button>
          <Button variant="destructive" size="sm" onClick={() => approve.mutate()} disabled={busy || !confirmed || count === 0} data-testid="approve-confirm">
            {busy ? <Loader2 className="animate-spin" /> : <ShieldCheck />}
            Approve and remove
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
