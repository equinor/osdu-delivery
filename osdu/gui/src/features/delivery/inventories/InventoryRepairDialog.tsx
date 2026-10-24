import { useState } from "react";
import { Link } from "react-router-dom";
import { useMutation } from "@tanstack/react-query";
import { CircleAlert, Loader2, RotateCcw } from "lucide-react";
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
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group";
import { isApiError } from "@/api/client";
import { cn } from "@/lib/utils";
import { inventoryApi, type Inventory, type InventoryRepairResult } from "../../../api/inventories";
import { ChoiceRow, InfoTip, PartHeading, Permanence } from "../RemovalDialog";
import { counted } from "./inventoryFormat";

/** Under which OSDU id the records are delivered again: the one each claimed, or a new one. */
type IdChoice = "own" | "new";

interface IdOption {
  choice: IdChoice;
  title: string;
  effect: string;
  /** What it does, on hover. */
  detail: string;
}

const OPTIONS: IdOption[] = [
  {
    choice: "own",
    title: "Under their own ids",
    effect: "A soft-deleted record comes back with every field it had; a purged one is written again.",
    detail: "Each record is marked as OSDU having lost it, and its flow's deliver run is queued. Its route brings a soft-deleted "
      + "record back under its id before writing it, so the keys it keeps of the record are kept; a purged one is created again "
      + "under the same id. Records that name these ids are whole again once it lands.",
  },
  {
    choice: "new",
    title: "Under new OSDU ids",
    effect: "Each record is written under a new id from now on.",
    detail: "The new id is made from the record's key and the id it replaces, in the same partition and entity type. Records that "
      + "name the old id keep naming it, so take this only when the old id must not be used again. Both ids stay in the record's "
      + "history, and it is found by its new one.",
  },
];

/**
 * Delivering again the records behind an inventory's missing ids (osdu/docs/reference/flow/inventory-removals.md, Repairing missing
 * ids): each through the flow that delivered it, under its own id (a soft-deleted record brought back first, a purged one
 * written again) or, typed back with the partition, under a new one. What was asked is answered in place: the records
 * marked, the run each flow was given, and the ids left as they were, with why.
 */
export function InventoryRepairDialog({ open, onClose, inventory, ids, count, onRepaired }: {
  open: boolean;
  onClose: () => void;
  inventory: Inventory;
  /** The ids picked; null repairs every missing id. */
  ids: readonly string[] | null;
  count: number;
  /** The repair was asked for. */
  onRepaired: (result: InventoryRepairResult) => void;
}) {
  const [choice, setChoice] = useState<IdChoice>("own");
  const [typed, setTyped] = useState("");
  const [result, setResult] = useState<InventoryRepairResult | null>(null);
  const [wasOpen, setWasOpen] = useState(open);

  // Every opening starts afresh, under the records' own ids: a new id is never one click away from the last repair.
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setChoice("own");
      setTyped("");
      setResult(null);
    }
  }

  const repair = useMutation({
    mutationFn: () => inventoryApi.repair(inventory.partition, inventory.inventoryId, {
      expected: count,
      ...(ids === null ? {} : { ids: [...ids] }),
      ...(choice === "new" ? { newIds: true, confirm: typed.trim() } : {}),
    }),
    onSuccess: (answer) => {
      setResult(answer);
      onRepaired(answer);
    },
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });

  const fresh = choice === "new";
  const confirmed = !fresh || typed.trim() === inventory.partition;
  // Once asked, the grid lets go of what was picked: what the repair was asked for is said from its answer.
  const subject = result !== null
    ? `${counted(result.asked, "missing id")} of ${result.inventory}`
    : ids === null
      ? `${counted(count, "missing id")} of ${inventory.name}, every one`
      : `${counted(count, "picked missing id")} of ${inventory.name}`;

  return (
    <AlertDialog
      open={open}
      onOpenChange={(next) => {
        if (!next && !repair.isPending) {
          onClose();
        }
      }}
    >
      <AlertDialogContent data-testid="repair-dialog" className="max-h-[calc(100dvh-2rem)] max-w-lg gap-4 overflow-y-auto">
        <AlertDialogHeader className="gap-1 text-left">
          <div className="flex items-center gap-2">
            <AlertDialogTitle className="flex min-w-0 flex-1 items-center gap-2">
              <RotateCcw className="size-4 shrink-0 text-primary" />
              Deliver again
            </AlertDialogTitle>
            <Badge variant="secondary" className="font-mono" data-testid="repair-partition">{inventory.partition}</Badge>
          </div>
          <AlertDialogDescription className="break-words" data-testid="repair-subject">{subject}</AlertDialogDescription>
        </AlertDialogHeader>

        {result === null
          ? (
            <>
              <div className="flex flex-col gap-1.5">
                <PartHeading>Under which OSDU id</PartHeading>
                <RadioGroup
                  value={choice}
                  onValueChange={(next) => {
                    setChoice(next as IdChoice);
                    setTyped("");
                  }}
                  disabled={repair.isPending}
                  aria-label="Under which OSDU id"
                  className="gap-1.5"
                >
                  {OPTIONS.map((option) => {
                    const id = `repair-ids-${option.choice}`;
                    return (
                      <ChoiceRow key={option.choice} active={option.choice === choice} permanent={option.choice === "new"} unavailable={false}>
                        <label htmlFor={id} className="flex min-w-0 flex-1 cursor-pointer items-start gap-2.5 py-2">
                          <RadioGroupItem
                            id={id}
                            value={option.choice}
                            className={cn("mt-0.5", option.choice === "new" && "text-destructive [&_svg]:fill-destructive")}
                            data-testid={id}
                          />
                          <span className="flex min-w-0 flex-1 flex-col gap-0.5">
                            <span className="text-[13px] font-medium leading-tight">{option.title}</span>
                            <span className="text-[12px] leading-snug text-muted-foreground">{option.effect}</span>
                          </span>
                        </label>
                        <span className="flex items-center gap-1 pt-1.5">
                          {option.choice === "new" && <Permanence reversible={false} />}
                          <InfoTip label={`About ${option.title.toLowerCase()}`} testId={`${id}-info`}>
                            <span>{option.detail}</span>
                          </InfoTip>
                        </span>
                      </ChoiceRow>
                    );
                  })}
                </RadioGroup>
              </div>

              <p className="flex items-start gap-2 text-[12px] leading-snug text-muted-foreground" data-testid="repair-reach">
                <CircleAlert className="mt-0.5 size-3.5 shrink-0 text-warning" />
                <span className="flex-1">
                  {`Only a record its ledger holds as delivered is reached; a dataset a delivery minted, or a record held or removed since the inventory was reconciled, is left and listed with why.${ids === null ? ` The repair is asked only while the inventory holds exactly ${count.toLocaleString("en-US")} missing id${count === 1 ? "" : "s"}.` : ""}`}
                </span>
              </p>

              {fresh && (
                <div className="flex flex-col gap-1.5">
                  <Label htmlFor="repair-confirm" className="text-[12px] font-normal text-muted-foreground">
                    <span>
                      {"This cannot be undone. Type "}
                      <span className="font-mono font-medium text-foreground">{inventory.partition}</span> to confirm.
                    </span>
                  </Label>
                  <Input
                    id="repair-confirm"
                    value={typed}
                    onChange={(event) => setTyped(event.target.value)}
                    disabled={repair.isPending}
                    autoComplete="off"
                    spellCheck={false}
                    className="h-8 font-mono"
                    data-testid="repair-confirm-input"
                  />
                </div>
              )}
            </>
          )
          : <RepairOutcome result={result} />}

        <AlertDialogFooter>
          {result === null
            ? (
              <>
                <Button variant="ghost" size="sm" onClick={onClose} disabled={repair.isPending} data-testid="repair-cancel">
                  Cancel
                </Button>
                <Button
                  variant={fresh ? "destructive" : "default"}
                  size="sm"
                  onClick={() => repair.mutate()}
                  disabled={repair.isPending || !confirmed || count === 0}
                  data-testid="repair-confirm"
                >
                  {repair.isPending ? <Loader2 className="animate-spin" /> : <RotateCcw />}
                  {fresh ? "Deliver under new ids" : "Deliver again"}
                  {count > 1 ? ` (${count.toLocaleString("en-US")})` : ""}
                </Button>
              </>
            )
            : (
              <Button size="sm" onClick={onClose} data-testid="repair-close">
                Close
              </Button>
            )}
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}

/** What a repair did: the records it marked and how, the run each flow was given, and what it left with why. */
function RepairOutcome({ result }: { result: InventoryRepairResult }) {
  const left = result.skipped.reduce((sum, skip) => sum + skip.count, 0);
  return (
    <div className="flex flex-col gap-3 text-[13px]" data-testid="repair-outcome">
      <p data-testid="repair-outcome-line">
        {result.marked === 0
          ? "No record could be delivered again."
          : `${counted(result.marked, "record")} will be delivered again ${result.newIds ? "under new OSDU ids" : "under their own ids"}.`}
        {left > 0 ? ` ${left.toLocaleString("en-US")} left as ${left === 1 ? "it was" : "they were"}.` : ""}
      </p>
      {result.flows.length > 0 && (
        <div className="flex flex-col gap-1">
          <PartHeading>Delivered again by</PartHeading>
          <ul className="flex flex-col gap-1">
            {result.flows.map((flow) => (
              <li key={flow.ledgerId} className="flex min-w-0 items-center gap-2" data-testid="repair-outcome-flow">
                <span className="min-w-0 flex-1 truncate font-mono text-[12px]">{flow.flow}</span>
                <span className="shrink-0 text-[12px] tabular-nums text-muted-foreground">{`${flow.marked.toLocaleString("en-US")} of ${flow.asked.toLocaleString("en-US")}`}</span>
                {flow.runId === undefined
                  ? <span className="shrink-0 text-[12px] text-muted-foreground">no run</span>
                  : <Link to={`/runs/${flow.runId}`} className="shrink-0 text-[12px] text-primary hover:underline" data-testid="repair-outcome-run">Open run</Link>}
              </li>
            ))}
          </ul>
        </div>
      )}
      {result.skipped.length > 0 && (
        <div className="flex flex-col gap-1">
          <PartHeading>Left as they were</PartHeading>
          <ul className="flex flex-col gap-1.5">
            {result.skipped.map((skip) => (
              <li key={skip.reason} className="flex flex-col gap-0.5" data-testid="repair-outcome-skipped">
                <span className="text-[12.5px]"><span className="font-mono tabular-nums">{skip.count.toLocaleString("en-US")}</span>{`: ${skip.reason}`}</span>
                {skip.ids.length > 0 && (
                  <span className="break-all font-mono text-[11px] text-muted-foreground">{skip.ids.join(", ")}{skip.count > skip.ids.length ? ", ..." : ""}</span>
                )}
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}
