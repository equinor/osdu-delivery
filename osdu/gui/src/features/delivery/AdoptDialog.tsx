import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { toast } from "sonner";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { isApiError } from "@/api/client";
import { deliveryApi, type DeliveryAdoptResult, type DeliveryFlowScope, type DeliveryManyRecords, type DeliveryRecordFilter } from "../../api/delivery";
import { ReleaseDialog } from "./ReleaseDialog";

/**
 * What an adoption is asked of: every record held because OSDU already holds a record at the id its key makes (of every
 * interface when the scope names none), the records ticked, or every record the listing's filter matches with the count it
 * showed.
 */
export type AdoptSelection =
  | { kind: "every"; count: number }
  | { kind: "keys"; keys: string[] }
  | { kind: "filter"; filter: DeliveryRecordFilter; expected: number };

function requestOf(selection: AdoptSelection): DeliveryManyRecords {
  switch (selection.kind) {
    case "keys":
      return { keys: selection.keys };
    case "filter":
      return { filter: selection.filter, expected: selection.expected };
    default:
      return {};
  }
}

function countOf(selection: AdoptSelection): string {
  const count = selection.kind === "every" ? selection.count : selection.kind === "keys" ? selection.keys.length : selection.expected;
  return `${count.toLocaleString()} record${count === 1 ? "" : "s"}`;
}

/**
 * The confirmation of an adoption of many records (osdu/docs/reference/concepts/record-lifecycle.md, Adopting a record OSDU
 * already holds), with the partition the ledgers are kept in typed back and the choice of a deliver run straight after: the
 * batch an operator runs once a run held records whose ids OSDU already holds, so the next run takes them all over.
 */
export function AdoptDialog({ pipelineId, scope, partition, ledgerName, selection, onClose, onDone }: {
  pipelineId: string;
  /** The ledger the adoption acts on; an interface of null, for every record, is every interface of the source. */
  scope: DeliveryFlowScope;
  /** The partition the ledgers are kept in, which the operator types back. */
  partition: string;
  ledgerName: string;
  selection: AdoptSelection;
  onClose: () => void;
  onDone: (result: DeliveryAdoptResult) => void;
}) {
  const [typed, setTyped] = useState("");
  const adopt = useMutation({
    mutationFn: (run: boolean) => deliveryApi.adoptFlow(pipelineId, { ...requestOf(selection), confirm: typed.trim(), run }, scope),
    onSuccess: onDone,
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });
  const what = selection.kind === "every"
    ? `Every record of ${ledgerName} held because OSDU already holds a record at the id its key makes, ${countOf(selection)}, is asked to adopt it.`
    : `Of the ${countOf(selection)} selected, those held because OSDU already holds a record at the id their key makes are asked to adopt it; the others are left as they are.`;
  return (
    <ReleaseDialog
      open
      title={selection.kind === "every" ? `Adopt ${countOf(selection)}` : "Adopt the selected records"}
      message={`${what} Another system, or another instance of the delivery whose ledger this database does not hold, wrote those OSDU records. The flow's next run takes each one over at the version OSDU holds then and writes the row there, so this ledger maintains it from then on. A record whose id OSDU no longer holds is delivered as a new one.`}
      confirmLabel="Adopt"
      busy={adopt.isPending}
      confirmDisabled={typed.trim().toLowerCase() !== partition.toLowerCase()}
      testId="flow-adopt-dialog"
      onConfirm={(run) => adopt.mutate(run)}
      onClose={onClose}
    >
      <div className="flex flex-col gap-1.5">
        <Label htmlFor="flow-adopt-confirm" className="text-[12px] font-normal text-muted-foreground">
          <span>
            {"Type "}
            <span className="font-mono font-medium text-foreground">{partition}</span>
            {" to confirm."}
          </span>
        </Label>
        <Input
          id="flow-adopt-confirm"
          value={typed}
          onChange={(event) => setTyped(event.target.value)}
          disabled={adopt.isPending}
          autoComplete="off"
          spellCheck={false}
          className="h-8 font-mono"
          data-testid="flow-adopt-confirm-input"
        />
      </div>
    </ReleaseDialog>
  );
}
