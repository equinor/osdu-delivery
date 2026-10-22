import { useState, type ReactNode } from "react";
import { Loader2 } from "lucide-react";
import {
  AlertDialog, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Label } from "@/components/ui/label";

/** How many records a deliver run plans again in one pass by key; it makes as many passes as there are (DeliveryExecutor.RequestedPerPass). */
export const PLANNED_PER_PASS = 5000;

/**
 * The confirmation of a release or an adoption of records, with the choice of a deliver run straight after: the one
 * dialog for a flow's blocked records, an issue's, and a held record's adoption. While it runs it cannot be dismissed.
 */
export function ReleaseDialog({ open, title, message, confirmLabel, busy, confirmDisabled = false, children, testId, onConfirm, onClose }: {
  open: boolean;
  title: string;
  message: string;
  confirmLabel: string;
  busy: boolean;
  /** Keeps the confirm button off until what `children` asks for is given (a partition typed back). */
  confirmDisabled?: boolean;
  /** What else the dialog shows above the run choice (the issue's error, a warning, a partition to type back). */
  children?: ReactNode;
  testId: string;
  onConfirm: (run: boolean) => void;
  onClose: () => void;
}) {
  const [run, setRun] = useState(true);
  return (
    <AlertDialog
      open={open}
      onOpenChange={(next) => {
        if (!next && !busy) {
          onClose();
        }
      }}
    >
      <AlertDialogContent data-testid={testId} className="max-w-lg">
        <AlertDialogHeader>
          <AlertDialogTitle>{title}</AlertDialogTitle>
          <AlertDialogDescription>{message}</AlertDialogDescription>
        </AlertDialogHeader>
        {children}
        <Label className="flex items-center gap-2 text-[13px] font-normal" title="The run plans every record released, a pass of 5,000 at a time, and sends what each release queued.">
          <Checkbox checked={run} onCheckedChange={(next) => setRun(next === true)} disabled={busy} data-testid={`${testId}-run`} />
          Queue a deliver run now
        </Label>
        <AlertDialogFooter>
          <Button variant="ghost" size="sm" onClick={onClose} disabled={busy} data-testid={`${testId}-cancel`}>
            Cancel
          </Button>
          <Button size="sm" onClick={() => onConfirm(run)} disabled={busy || confirmDisabled} data-testid={`${testId}-confirm`}>
            {busy && <Loader2 className="animate-spin" />}
            {confirmLabel}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
