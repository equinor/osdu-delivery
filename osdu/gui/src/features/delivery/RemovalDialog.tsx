import { Fragment, useMemo, useState, type ReactNode } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { CircleAlert, Eraser, Info, Loader2, Lock, LockOpen, RotateCcw, Trash2, TriangleAlert, Undo2 } from "lucide-react";
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
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { isApiError } from "@/api/client";
import {
  deliveryApi,
  type DeliveryFlowScope,
  type DeliveryLedgerPurgeResult,
  type DeliveryRecordFilter,
  type DeliveryRemovalAccepted,
  type DeliveryRemovalRequest,
  type DeliveryTarget,
  type RemovalScope,
} from "../../api/delivery";
import { GONE_FINDING, inventoryApi, type Inventory, type InventoryRemovalPolicy } from "../../api/inventories";
import { ACCESS_RESTRICTIONS_KEY, accessRestrictionsApi, type AccessRestriction } from "../../api/accessRestrictions";
import { aclText } from "./accessRestrictionText";
import { isPartitionId, useActivePartition } from "./activePartition";
import { CorrelationError } from "@/components/CorrelationError";
import { counted, findingVisual } from "./inventories/inventoryFormat";

/**
 * Which records the removal acts on: the exact ones an operator ticked, or the listing they were looking at with
 * the count it showed. The filter form is what "select all 3,481 matching" means; the count travels with it so the
 * API can refuse the removal if the set has changed since it was shown.
 */
export type RemovalSelection =
  | { kind: "keys"; keys: string[] }
  | { kind: "filter"; filter: DeliveryRecordFilter; expected: number };

/**
 * What an inventory's removal acts on (osdu/docs/reference/flow/inventory-removals.md, What a removal does): the ids of one finding,
 * those picked or every one, as many as the operator was shown, through the inventory flow's own source as its policy says;
 * or, gone ids, from the inventory alone, which no policy governs, or brought back in OSDU (Restoring gone ids), which takes the
 * flow's removal policy as a removal does.
 */
export interface InventoryRemovalTarget {
  inventory: Inventory;
  finding: string;
  /** The ids picked; null removes every id of the finding (of the selection, when one is named). */
  ids: readonly string[] | null;
  count: number;
  /** What the flow lets an operator remove from OSDU, and restore there; absent where the flow declares no removal. */
  policy?: InventoryRemovalPolicy;
  /**
   * The selection whose ids of the finding are removed, every one of them: those that held what the operator's conditions
   * asked in OSDU, with the conditions as a sentence.
   */
  selection?: { id: number; sentence: string };
  /** For gone ids, what the dialog opens on: bringing them back in OSDU, or removing them from the inventory (the default). */
  opensOn?: "restore" | "inventory";
  /** The soft-delete removal whose removed ids are brought back in OSDU, every one: only a restore, of `count` ids. */
  restoresRemoval?: number;
}

/** A removal or a restore of an inventory's ids, queued as a run of its flow. */
export interface InventoryChangeQueued {
  runId: string;
  inventory: string;
  expected: number;
  /** True for a restore in OSDU; false for a removal. */
  restore: boolean;
}

/** The records of a flow's ledger the dialog removes. */
interface LedgerRemovalProps {
  inventory?: undefined;
  pipelineId: string;
  /**
   * The ledger whose records these are: the interface of a source (null for the single form) and the partition of a flow
   * that works in partitions (null for one whose partition is its header's).
   */
  flowScope?: DeliveryFlowScope;
  flowName: string;
  selection: RemovalSelection;
  /** What the records are called in the dialog's title when there is exactly one of them. */
  singleLabel?: string;
  /** Every selected record is removed from OSDU already: the dialog opens on deleting it from the ledger. */
  alreadyRemoved?: boolean;
  /** The dialog opens on lifting the records' access restriction, as a restricted record's page asks. */
  opensOnLift?: boolean;
  /** A removal that reaches OSDU was queued on a node. */
  onQueued: (accepted: DeliveryRemovalAccepted) => void;
  /** Records already removed from OSDU were deleted from the ledger, here and now. */
  onPurged: (result: DeliveryLedgerPurgeResult) => void;
}

/** The ids of an inventory the dialog removes, or restores. */
interface InventoryRemovalProps {
  inventory: InventoryRemovalTarget;
  /** The removal (or the restore) was queued as a run of the inventory flow. */
  onQueued: (queued: InventoryChangeQueued) => void;
}

type RemovalDialogProps = { open: boolean; onClose: () => void } & (LedgerRemovalProps | InventoryRemovalProps);

/**
 * What to do in OSDU: one of the removal scopes, nothing, for records already removed from it, or, for an inventory's gone ids,
 * bring them back.
 */
type OsduChoice = RemovalScope | "none" | "restore";

interface ScopeChoice {
  scope: OsduChoice;
  title: string;
  /** What happens, in one line of the operator's terms. */
  effect: string;
  /** What the ledger does about it, which is not the same question as what OSDU does: shown on hover, with the call. */
  ledger: string;
  /** Whether it can be undone; null for a choice that changes nothing in OSDU. */
  reversible: boolean | null;
  /** The call the scope makes against the target, as the flow resolves it; null for none. */
  call: (target: DeliveryTarget) => string | null;
  /** Why the flow cannot make the call, or null when it can. */
  refusal: (target: DeliveryTarget) => string | null;
  confirmLabel: string;
}

/**
 * A removal endpoint the flow cannot resolve, or that its DDMS refuses, reads as a bracketed reason. The bracketed
 * placeholders for a collection that is only known at run time are not refusals: the node resolves them.
 */
function refusalOf(path: string): string | null {
  return /^\((not configured|not routable|refused)/.test(path) ? path.replace(/^\(|\)$/g, "") : null;
}

const SCOPES: ScopeChoice[] = [
  {
    scope: "record",
    title: "Remove the record",
    effect: "Stops resolving. Every version is kept.",
    ledger: "Marked removed here. The flow's next run reads its row again and delivers it.",
    reversible: true,
    // The node says which call the record scope makes: a DDMS's own DELETE, or a POST to storage's :delete path or the
    // dataset service's reversible removal.
    call: (target) => `${target.recordMethod} ${target.recordPath}`,
    refusal: (target) => refusalOf(target.recordPath),
    confirmLabel: "Remove",
  },
  {
    scope: "previous",
    title: "Restore the previous version",
    effect: "The version before the latest becomes current again.",
    ledger: "OSDU cannot delete only the latest version, so the previous one is written back as a new version and the "
      + "replaced one stays in the history. Marked reverted here, and blocked from redelivery until its source changes "
      + "or it is released. Asked again, it brings the replaced version back.",
    reversible: true,
    call: (target) => target.previousPath,
    refusal: (target) => target.previousRefusal ?? null,
    confirmLabel: "Restore previous version",
  },
  {
    scope: "restrict",
    title: "Restrict access",
    effect: "Readable only by a registered restriction's groups. Nothing is deleted.",
    ledger: "Each record's ACL in OSDU is replaced in place with the restriction's groups: no new version, every version and the "
      + "search index follow. The ledger keeps the access each had, and keeps the record out of every run, release and deletion "
      + "from the ledger until the restriction is lifted. Records restricted already, or never delivered, are passed over.",
    reversible: true,
    call: (target) => target.restrictPath ?? null,
    refusal: (target) => target.restrictRefusal ?? (target.restrictPath === undefined ? "The control plane does not restrict access yet." : null),
    confirmLabel: "Restrict access",
  },
  {
    scope: "unrestrict",
    title: "Lift the restriction",
    effect: "Each record gets back the access it had.",
    ledger: "The ACL each record had before its restriction is written back in place, with no new version; a record OSDU no longer "
      + "holds is sent again. Lifted, a record follows its source again: the flow's next run plans it.",
    reversible: true,
    call: (target) => target.restrictPath ?? null,
    refusal: (target) => target.restrictRefusal ?? (target.restrictPath === undefined ? "The control plane does not restrict access yet." : null),
    confirmLabel: "Lift the restriction",
  },
  {
    scope: "history",
    title: "Purge earlier versions",
    effect: "Only the latest version is kept.",
    ledger: "The record stays delivered here; the purge is written to its history.",
    reversible: false,
    call: (target) => `DELETE ${target.historyPath}`,
    refusal: (target) => refusalOf(target.historyPath),
    confirmLabel: "Purge history",
  },
  {
    scope: "everything",
    title: "Purge everything",
    effect: "The record and every version are destroyed.",
    ledger: "Marked removed here. The flow's next run reads its row again and delivers it as a new record.",
    reversible: false,
    call: (target) => `DELETE ${target.everythingPath}`,
    refusal: (target) => refusalOf(target.everythingPath),
    confirmLabel: "Purge everything",
  },
  {
    scope: "none",
    title: "Leave as it is",
    effect: "For records already removed.",
    ledger: "Nothing is asked of OSDU. Deleting from the ledger reaches only the records already removed from it.",
    reversible: null,
    call: () => null,
    refusal: () => null,
    confirmLabel: "Delete from ledger",
  },
  {
    // Offered only for an inventory's gone ids, which no ledger holds: a record a ledger holds comes back through its flow.
    scope: "restore",
    title: "Restore the record",
    effect: "Served again at the version it had. No new version.",
    ledger: "",
    reversible: true,
    call: () => null,
    refusal: () => null,
    confirmLabel: "Restore in OSDU",
  },
];

/** The choices that leave a record out of OSDU, after which the ledger may delete it. */
function leavesItOut(scope: OsduChoice): boolean {
  return scope === "record" || scope === "everything" || scope === "none";
}

/** The call a scope makes for an inventory's removal or restore, through the inventory flow's source; null for one it does not offer. */
function inventoryCall(scope: OsduChoice, policy: InventoryRemovalPolicy | undefined): string | null {
  if (policy === undefined) {
    return null;
  }

  return scope === "record"
    ? `POST ${policy.bulkDeletePath}`
    : scope === "everything"
      ? `DELETE ${policy.purgePath}`
      : scope === "restore" ? `PATCH ${policy.restorePath}  {"deleted": false}` : null;
}

/** Why an inventory's removal cannot take a scope: a purge its flow does not allow, or a restore by a flow that only reads OSDU. */
function inventoryRefusal(scope: OsduChoice, policy: InventoryRemovalPolicy | undefined): string | null {
  if (scope === "restore") {
    return policy === undefined ? "The flow declares no removal, so it only reads OSDU: a removal in its YAML lets it remove and restore." : null;
  }

  return scope === "everything" && policy?.purge !== true ? "The flow allows soft deletes only: removal.purge: true in its YAML lets it purge." : null;
}

/** What an inventory's removal keeps of each id, where the ledger dialog says what the ledger does. */
const INVENTORY_KEPT = "Each id's outcome is kept with the removal, with why, and the ids removed are gone from the inventory at once. "
  + "A stale record's own ledger records the removal on the record.";

/** Why gone ids ask nothing of OSDU, where the ledger dialog says what leaving a record as it is does. */
const GONE_LEFT = "Nothing is asked of OSDU: the inventory's last build did not list these ids, so there is nothing of them there to remove.";

/** What a restore does with each id, where the ledger dialog says what the ledger does. */
const RESTORED = "Storage keeps a soft-deleted record and every version, and serves it again at the version it had, one id a request. "
  + "The ids brought back are served again in the inventory at once, and each id's outcome is kept with the restore, under Removals. "
  + "A purged record cannot come back.";

/** What is checked again of each id just before it goes, so a removal never takes what moved since the inventory found it. */
function rechecks(finding: string, restoring: boolean, byRemoval: boolean): string[] {
  if (restoring) {
    return [
      byRemoval
        ? "The inventory still finds it gone, or no longer holds it: one a build listed again is served already."
        : "The inventory still finds it gone: one a build listed again is served already.",
      "No ledger knows it. A record a ledger holds comes back through its own flow, which records it.",
      ...(byRemoval ? [] : ["An identity this estate writes as created it: a restore brings back only what this estate wrote."]),
      "Storage still holds it soft deleted. One it holds no record of was purged, and is listed as gone for good.",
    ];
  }

  if (finding === GONE_FINDING) {
    return [
      "The inventory still finds it gone, and OSDU does not serve it again: its last build did not list it.",
      "No ledger expects it. One a ledger expects stays, for a later build to ask storage whether it is missing.",
    ];
  }

  return [
    `The inventory still finds it ${findingVisual(finding).label.toLowerCase()}, and the ledgers of the partition give it that finding now.`,
    "OSDU still serves it at the version the inventory listed.",
    ...(finding === "orphan" ? ["An identity this estate writes as still shows as its creator."] : []),
  ];
}

/** The word an operator types to confirm a permanent removal: the partition it is aimed at, or the flow. */
function confirmationWord(target: DeliveryTarget | undefined, flowName: string): string {
  return target?.dataPartition ?? flowName;
}

function requestFor(selection: RemovalSelection, scope: RemovalScope, purgeLedger = false, restriction: string | null = null): DeliveryRemovalRequest {
  const purge = purgeLedger && leavesItOut(scope) ? { purgeLedger: true } : {};
  const restricting = scope === "restrict" && restriction !== null ? { restriction } : {};
  return selection.kind === "keys"
    ? { scope, keys: selection.keys, ...purge, ...restricting }
    : { scope, filter: selection.filter, expected: selection.expected, ...purge, ...restricting };
}

/**
 * The registered access restriction a restriction sets: the one the partition has when it has one, else the one picked
 * among several, with each one's groups. No picker is drawn for one, and none for none, which says where one is registered.
 */
function RestrictionPicker({ restrictions, picked, onPick, partition, loading, failed, disabled }: {
  restrictions: AccessRestriction[];
  picked: AccessRestriction | null;
  onPick: (name: string) => void;
  partition: string | null;
  loading: boolean;
  failed: string | null;
  disabled: boolean;
}) {
  if (loading) {
    return <Skeleton className="h-8 w-full rounded-md" />;
  }

  if (failed !== null) {
    return <p className="text-[12px] text-destructive" data-testid="removal-restriction-error">{failed}</p>;
  }

  if (restrictions.length === 0) {
    return null;
  }

  return (
    <div className="flex flex-col gap-1.5 rounded-md border border-border bg-muted/30 px-3 py-2" data-testid="removal-restriction">
      {restrictions.length === 1
        ? (
          <span className="text-[12.5px]">
            <span className="text-muted-foreground">Under </span>
            <span className="font-medium" data-testid="removal-restriction-name">{restrictions[0].name}</span>
            {restrictions[0].description !== null && <span className="text-muted-foreground">{`: ${restrictions[0].description}`}</span>}
          </span>
        )
        : (
          <div className="flex items-center gap-2">
            <Label htmlFor="removal-restriction-pick" className="shrink-0 text-[12px] font-normal text-muted-foreground">Under</Label>
            <Select value={picked?.name ?? ""} onValueChange={onPick} disabled={disabled}>
              <SelectTrigger id="removal-restriction-pick" size="sm" className="h-8 min-w-0 flex-1 [&>span]:truncate" data-testid="removal-restriction-pick">
                <SelectValue placeholder={`Pick one of ${partition ?? "the partition"}'s ${restrictions.length} restrictions`} />
              </SelectTrigger>
              <SelectContent>
                {restrictions.map((restriction) => (
                  <SelectItem key={restriction.restrictionId} value={restriction.name}>{restriction.name}</SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        )}
      {picked !== null && restrictions.length > 1 && picked.description !== null && (
        <span className="text-[12px] text-muted-foreground" data-testid="removal-restriction-purpose">{picked.description}</span>
      )}
      {picked !== null && (
        <span className="break-all font-mono text-[11px] text-muted-foreground" data-testid="removal-restriction-groups">
          {aclText(picked.viewers, picked.owners)}
        </span>
      )}
    </div>
  );
}

/** A hover panel of facts or an explanation, behind a small info mark beside what it explains. */
export function InfoTip({ label, children, testId }: { label: string; children: ReactNode; testId?: string }) {
  return (
    <Tooltip delayDuration={200}>
      <TooltipTrigger asChild>
        <button
          type="button"
          aria-label={label}
          className="inline-flex size-6 shrink-0 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-foreground focus-visible:outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50"
          data-testid={testId}
        >
          <Info className="size-3.5" />
        </button>
      </TooltipTrigger>
      <TooltipContent variant="panel" side="right" align="start" className="max-w-sm">
        {children}
      </TooltipContent>
    </Tooltip>
  );
}

/** Whether a choice can be undone: a quiet word led by a glyph in its tone, the same on every row of both parts. */
export function Permanence({ reversible }: { reversible: boolean }) {
  const Icon = reversible ? RotateCcw : TriangleAlert;
  return (
    <span className="inline-flex shrink-0 items-center gap-1 whitespace-nowrap text-[11px] text-muted-foreground">
      <Icon className={cn("size-3", reversible ? "text-success" : "text-destructive")} aria-hidden />
      {reversible ? "reversible" : "permanent"}
    </span>
  );
}

/** One fact of the target panel: a quiet label over its value. */
function TargetFact({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="flex flex-col">
      <span className="text-[11px] text-muted-foreground">{label}</span>
      <span className={cn("break-all", mono && "font-mono text-[11.5px]")}>{value}</span>
    </div>
  );
}

/** The heading of one part of the dialog: where its choice acts. */
export function PartHeading({ children }: { children: ReactNode }) {
  return <div className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">{children}</div>;
}

/** The frame of one choice: a bordered row, marked when chosen, quiet when the flow cannot take it. */
export function ChoiceRow({ active, permanent, unavailable, children }: { active: boolean; permanent: boolean; unavailable: boolean; children: ReactNode }) {
  return (
    <div
      className={cn(
        "flex items-start gap-1 rounded-md border pl-3 pr-1 transition-colors",
        active
          ? permanent ? "border-destructive/70 bg-destructive/5" : "border-primary bg-primary/5"
          : "border-border hover:bg-accent/40",
        unavailable && "opacity-60 hover:bg-transparent",
      )}
    >
      {children}
    </div>
  );
}

/**
 * The one removal surface, for what goes in OSDU and what goes in the ledger: which records, how much of each goes, and
 * whether it can be undone. Its two parts say where each choice acts. In OSDU, the scopes are genuinely different calls
 * with different promises, each named with one line of what it does; "Leave as it is" is offered for records already
 * removed. In the ledger, deleting the records' history is a step of its own, offered only with a choice that leaves them
 * out of OSDU. Every row carries the same mark of whether it can be undone, and anything permanent asks for the partition
 * to be typed back. What the ledger does and the call each choice makes are on hover, beside it; the endpoint and route
 * are beside the partition, which stays in sight.
 *
 * It removes an inventory's ids the same way (`inventory`): the ids of one finding, picked or every one, through the
 * inventory flow's own source, removed or (where the flow allows it) purged; there is no ledger part, since no ledger holds
 * them live, what is checked again of each id before it goes is said under the choices, and the partition is typed back
 * whatever the choice, since one removal may take millions of ids. Gone ids, which OSDU no longer serves, are either brought
 * back in OSDU (a restore, where the flow declares a removal) or left as they are there and removed from the inventory, a
 * part of its own in the ledger part's place, as records already removed from OSDU are deleted from the ledger; the dialog
 * opens on the one the operator asked for. What one removal removed is brought back the same way, the restore alone.
 */
export function RemovalDialog(props: RemovalDialogProps) {
  const { open, onClose } = props;
  // A ledger's records, or an inventory's ids.
  const ledger = props.inventory === undefined ? props : null;
  const ids = props.inventory === undefined ? null : props;
  const of = ids?.inventory ?? null;
  const alreadyRemoved = ledger?.alreadyRemoved ?? false;
  const opensOnLift = ledger?.opensOnLift ?? false;
  // Gone ids are out of OSDU already: they are brought back there, or left as they are there and removed from the inventory.
  // What one removal removed is only brought back.
  const byRemoval = of?.restoresRemoval !== undefined;
  const fromInventory = of !== null && of.finding === GONE_FINDING;
  const startsOut = alreadyRemoved || fromInventory;
  const opensOn: OsduChoice = byRemoval || of?.opensOn === "restore" ? "restore" : opensOnLift ? "unrestrict" : startsOut ? "none" : "record";
  const [scope, setScope] = useState<OsduChoice>(opensOn);
  // Deleting here: the records from the ledger, or the gone ids from the inventory.
  const [deleteHere, setDeleteHere] = useState(startsOut && opensOn !== "restore");
  const [typed, setTyped] = useState("");
  // The access restriction picked among several a partition registers; null until one is.
  const [picked, setPicked] = useState<string | null>(null);
  const [wasOpen, setWasOpen] = useState(open);

  // Every opening starts afresh: on the reversible removal with the ledger left alone, or, for records already removed (and
  // gone ids), on deleting them here, which still waits for the partition to be typed; or, gone ids the operator asked to
  // bring back, on the restore. A purge is never one click away from the last thing the operator did.
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setScope(opensOn);
      setDeleteHere(startsOut && opensOn !== "restore");
      setTyped("");
      setPicked(null);
    }
  }

  const restoring = scope === "restore";
  const purging = ledger !== null && deleteHere && leavesItOut(scope);
  const dropping = fromInventory && deleteHere && !restoring;
  const removalScope: RemovalScope = scope === "none" || scope === "restore" ? "record" : scope;
  const selection = ledger?.selection ?? null;
  const preview = useQuery({
    queryKey: [
      "delivery", "removal-preview", ledger?.pipelineId ?? null, ledger?.flowScope?.interfaceName ?? null, ledger?.flowScope?.partition ?? null,
      selection === null ? null : JSON.stringify(requestFor(selection, "record")),
    ],
    queryFn: () => deliveryApi.previewRemoval(ledger!.pipelineId, requestFor(ledger!.selection, "record"), ledger!.flowScope),
    enabled: open && ledger !== null,
    staleTime: 0,
    gcTime: 0,
  });

  // The access restrictions a restriction picks from: the registered ones of the partition the records' ledger is kept in,
  // read as soon as the target names it, so the choice says at once whether there is one to pick.
  const [activeOne] = useActivePartition();
  const named = preview.data?.target.dataPartition ?? null;
  const restrictionPartition = ledger?.flowScope?.partition ?? (named !== null && isPartitionId(named) ? named : null) ?? activeOne;
  const restrictions = useQuery({
    queryKey: [...ACCESS_RESTRICTIONS_KEY, restrictionPartition],
    queryFn: () => accessRestrictionsApi.list(restrictionPartition),
    enabled: open && ledger !== null && preview.data !== undefined && preview.data.target.restrictRefusal == null,
  });
  const registered = (restrictions.data ?? []).filter((restriction) => restrictionPartition === null || restriction.partition === restrictionPartition);
  const restriction = registered.length === 1 ? registered[0] : registered.find((one) => one.name === picked) ?? null;
  const request = useMemo(
    () => (selection === null ? null : requestFor(selection, removalScope, purging, restriction?.name ?? null)),
    [selection, removalScope, purging, restriction],
  );

  const remove = useMutation({
    mutationFn: async () => {
      if (ledger !== null) {
        ledger.onQueued(await deliveryApi.removeRecords(ledger.pipelineId, request!, ledger.flowScope));
        return;
      }

      const asked = ids!.inventory;
      if (restoring) {
        const accepted = await inventoryApi.restore(asked.inventory.partition, asked.inventory.inventoryId, {
          expected: asked.count,
          confirm: typed.trim(),
          ...(asked.ids === null ? {} : { ids: [...asked.ids] }),
          ...(asked.restoresRemoval === undefined ? {} : { removal: asked.restoresRemoval }),
        });
        ids!.onQueued({ runId: accepted.runId, inventory: accepted.inventory, expected: accepted.expected, restore: true });
        return;
      }

      const accepted = await inventoryApi.remove(asked.inventory.partition, asked.inventory.inventoryId, {
        finding: asked.finding,
        scope: fromInventory ? "inventory" : removalScope === "everything" ? "everything" : "record",
        expected: asked.count,
        confirm: typed.trim(),
        ...(asked.ids === null ? {} : { ids: [...asked.ids] }),
        ...(asked.ids === null && asked.selection !== undefined ? { selection: asked.selection.id } : {}),
      });
      ids!.onQueued({ runId: accepted.runId, inventory: accepted.inventory, expected: accepted.expected, restore: false });
    },
    onSuccess: () => onClose(),
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });

  // Deleting from the ledger alone asks nothing of OSDU: the ledger answers here and now, through the same deletion the
  // removal's extra step makes.
  const purge = useMutation({
    mutationFn: () => deliveryApi.purgeRecords(
      ledger!.pipelineId,
      ledger!.selection.kind === "keys" ? { keys: ledger!.selection.keys } : { filter: ledger!.selection.filter, expected: ledger!.selection.expected },
      ledger!.flowScope),
    onSuccess: (result) => {
      ledger!.onPurged(result);
      onClose();
    },
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });

  const choice = SCOPES.find((s) => s.scope === scope)!;
  const target = preview.data?.target;
  const flowName = ledger?.flowName ?? of!.inventory.flowName;
  const partition = of === null ? target?.dataPartition ?? null : of.inventory.partition;
  const word = of === null ? confirmationWord(target, flowName) : of.inventory.partition;
  const records = of !== null ? of.count : preview.data?.records ?? (selection!.kind === "keys" ? selection!.keys.length : selection!.expected);
  const removed = of !== null ? 0 : preview.data?.removed ?? (alreadyRemoved ? records : 0);
  // Of the selection, the records whose access is restricted: what a lift reaches, and what a restriction passes over.
  const restricted = of !== null ? 0 : preview.data?.restricted ?? (opensOnLift ? records : 0);
  // More than one removal takes: the API would refuse it, so the dialog says so and does not offer it.
  const capped = of === null && preview.data?.capped === true;
  const callOf = (option: ScopeChoice) => (of !== null ? inventoryCall(option.scope, of.policy) : target === undefined ? null : option.call(target));
  const refusalFor = (option: ScopeChoice) => (of !== null ? inventoryRefusal(option.scope, of.policy) : target === undefined ? null : option.refusal(target));
  const refused = refusalFor(choice);
  const busy = remove.isPending || purge.isPending;
  // One inventory removal may take millions of ids, so it is typed back whatever it takes.
  const needsTyping = of !== null || choice.reversible === false || purging;
  const confirmed = !needsTyping || typed.trim() === word;
  // Nothing in OSDU and nothing in the ledger (or the inventory) is no request at all.
  const nothingAsked = scope === "none" && !purging && !dropping;
  const acting = fromInventory || byRemoval ? records : scope === "none" ? removed : scope === "unrestrict" ? restricted : records;
  // Every record of the selection is out of OSDU already: only purging what OSDU keeps of it, or leaving it, still acts on it.
  const allRemoved = records > 0 && removed >= records;
  // Gone ids are brought back or left as they are in OSDU; what one removal removed is only brought back; served ids are
  // removed or purged. The ledger's records never see the restore, which only an inventory's gone ids take.
  const offered = of !== null
    ? SCOPES.filter((option) => (byRemoval
      ? option.scope === "restore"
      : fromInventory ? option.scope === "none" || option.scope === "restore" : option.scope === "record" || option.scope === "everything"))
    : SCOPES.filter((option) => option.scope !== "restore"
      && (option.scope !== "none" || removed > 0 || scope === "none")
      && (option.scope !== "unrestrict" || restricted > 0 || scope === "unrestrict"));
  const known = of !== null || target !== undefined;

  const label = of === null ? "" : findingVisual(of.finding).label.toLowerCase();
  const bySelection = of !== null && of.ids === null && of.selection !== undefined ? of.selection : null;
  const subject = of !== null
    ? byRemoval
      ? `${counted(of.count, "id")} of ${of.inventory.name} that removal ${of.restoresRemoval} soft deleted`
      : of.ids === null
      ? bySelection !== null
        ? `${counted(of.count, `${label} id`)} of ${of.inventory.name} matching the conditions`
        : `${counted(of.count, `${label} id`)} of ${of.inventory.name}, every one`
      : `${counted(of.count, `picked ${label} id`)} of ${of.inventory.name}`
    : capped
      ? `More than one removal takes: ${records.toLocaleString()}${selection!.kind === "filter" ? "+" : ""} records. Narrow the selection and remove them in parts.`
      : records === 1
        ? ledger!.singleLabel ?? "One record"
        : selection!.kind === "filter"
          ? `${records.toLocaleString()} records matching the filter`
          : `${records.toLocaleString()} records`;
  const removedNote = removed === 0
    ? null
    : records === 1 ? "removed from OSDU" : `${removed.toLocaleString()} of them removed from OSDU`;

  const confirmLabel = restoring
    ? choice.confirmLabel
    : fromInventory
      ? "Remove from inventory"
      : scope === "none"
        ? choice.confirmLabel
        : purging ? `${choice.confirmLabel} and delete from ledger` : choice.confirmLabel;

  // What the run is held to before it starts: as many ids as the operator was shown, of the inventory, the match or the removal.
  const holdsNote = of === null || of.ids !== null
    ? ""
    : byRemoval
      ? ` The run starts only while removal ${of.restoresRemoval} holds exactly ${counted(of.count, "removed id")}.`
      : ` The run starts only while the ${of.selection === undefined ? "inventory" : "match"} holds exactly ${of.count.toLocaleString("en-US")} ${label} id${of.count === 1 ? "" : "s"}.`;
  // What is checked again of each id, said under the part where the removal acts: in OSDU, or, for gone ids, in the inventory.
  const rechecksNote = of === null ? null : (
    <p className="flex items-start gap-2 text-[12px] leading-snug text-muted-foreground" data-testid="removal-rechecks">
      <CircleAlert className="mt-0.5 size-3.5 shrink-0 text-warning" />
      <span className="flex-1">
        {restoring
          ? `Each id is checked again just before it is brought back; one that moved since, or that a ledger knows, is left as it is and kept with why.${holdsNote}`
          : `Each id is checked again just before it goes; one that moved since the inventory found it is left ${fromInventory ? "in the inventory" : "in OSDU"} and kept with why.${holdsNote}`}
      </span>
      <InfoTip label="What is checked again" testId="removal-rechecks-info">
        <ul className="flex list-disc flex-col gap-0.5 pl-4">
          {rechecks(of.finding, restoring, byRemoval).map((line) => <li key={line}>{line}</li>)}
        </ul>
      </InfoTip>
    </p>
  );

  return (
    <AlertDialog
      open={open}
      onOpenChange={(next) => {
        if (!next && !busy) {
          onClose();
        }
      }}
    >
      {/* Five choices, the ledger step and a typed confirmation can outgrow a short window: the dialog scrolls rather than
          putting its buttons past the bottom of the viewport. */}
      <AlertDialogContent data-testid="removal-dialog" className="max-h-[calc(100dvh-2rem)] max-w-lg gap-4 overflow-y-auto">
        <AlertDialogHeader className="gap-1 text-left">
          <div className="flex items-center gap-2">
            <AlertDialogTitle className="flex min-w-0 flex-1 items-center gap-2" data-testid="removal-title">
              {restoring
                ? <RotateCcw className="size-4 shrink-0 text-primary" />
                : <Trash2 className="size-4 shrink-0 text-destructive" />}
              {restoring ? "Restore" : fromInventory ? "Remove from inventory" : "Remove"}
            </AlertDialogTitle>
            {!known
              ? !preview.isError && <Skeleton className="h-5 w-16 rounded-md" />
              : (
                <span className="flex items-center gap-0.5" data-testid="removal-target">
                  {partition !== null && (
                    <Badge variant="secondary" className="font-mono" data-testid="removal-partition">
                      {partition}
                    </Badge>
                  )}
                  <InfoTip label="Where the records live" testId="removal-target-info">
                    {of !== null
                      ? (
                        <div className="flex flex-col gap-1.5">
                          <TargetFact label="Flow" value={flowName} />
                          {of.policy !== undefined && (!fromInventory || restoring) && <TargetFact label="Endpoint" value={of.policy.endpoint} mono />}
                          <TargetFact
                            label="Route"
                            value={fromInventory && !restoring ? "the inventory in the module's database; nothing is asked of OSDU" : "storage, through the inventory flow's own source and credentials"}
                          />
                        </div>
                      )
                      : target !== undefined && (
                        <div className="flex flex-col gap-1.5">
                          <TargetFact label="Flow" value={flowName} />
                          <TargetFact label="Endpoint" value={target.endpoint} mono />
                          <TargetFact label="Route" value={`${target.protocol} · ${target.authType}`} />
                          {target.ddms !== null && <span className="text-muted-foreground" data-testid="removal-ddms">{target.ddms}</span>}
                        </div>
                      )}
                  </InfoTip>
                </span>
              )}
          </div>
          <AlertDialogDescription className="break-words" data-testid="removal-scope-line">
            {subject}
            {removedNote !== null && !capped && <span className="text-muted-foreground/80">{` · ${removedNote}`}</span>}
          </AlertDialogDescription>
          {bySelection !== null && (
            <p className="flex items-start gap-1.5 rounded-md border border-border bg-muted/40 px-2.5 py-1.5 text-[12.5px]" data-testid="removal-selected-by">
              <span className="shrink-0 text-muted-foreground">Selected by</span>
              <span className="min-w-0 break-words font-medium">{bySelection.sentence}</span>
            </p>
          )}
        </AlertDialogHeader>

        {preview.isError && (isApiError(preview.error)
          ? <CorrelationError error={preview.error} />
          : <p className="text-[13px] text-destructive">{String(preview.error)}</p>)}

        <div className="flex flex-col gap-1.5">
          <PartHeading>In OSDU</PartHeading>
          <RadioGroup
            value={scope}
            onValueChange={(next) => {
              setScope(next as OsduChoice);
              setTyped("");
            }}
            disabled={busy}
            aria-label="What to do in OSDU"
            className="gap-1.5"
          >
            {offered.map((option) => {
              const active = option.scope === scope;
              // A scope whose call the flow cannot resolve, or whose route refuses it, is not offered: the node would refuse
              // it anyway, and offering it invites an operator to ask for a removal that never happens. Nor is one that has
              // nothing left to act on: a record out of OSDU has no latest version to step back from or earlier ones to purge.
              const refusal = !known || option.scope === "none" ? null : refusalFor(option);
              const unavailable: { line: string; why: string } | null = option.scope === "none"
                ? removed === 0 && !fromInventory ? { line: "None of these is removed yet.", why: "None of these is removed from OSDU yet." } : null
                : allRemoved && option.scope !== "everything" && option.scope !== "restore" && option.scope !== "unrestrict"
                  ? { line: "Removed from OSDU already.", why: "Every record here is removed from OSDU already, so there is nothing of it for this to act on." }
                  : refusal !== null
                    ? { line: "Not available for this flow.", why: refusal }
                    : option.scope === "restrict" && restrictions.isSuccess && registered.length === 0
                      ? {
                        line: `No access restriction is registered${restrictionPartition === null ? "" : ` for ${restrictionPartition}`}.`,
                        why: "A restriction replaces the records' ACL with a registered restriction's groups. An admin registers one under Access restrictions (the OSDU menu).",
                      }
                      : null;
              const call = known ? callOf(option) : null;
              const id = `removal-scope-${option.scope}`;
              return (
                <Fragment key={option.scope}>
                  <ChoiceRow active={active} permanent={option.reversible === false} unavailable={unavailable !== null}>
                    <label
                      htmlFor={id}
                      className={cn("flex min-w-0 flex-1 items-start gap-2.5 py-2", unavailable === null ? "cursor-pointer" : "cursor-not-allowed")}
                    >
                      <RadioGroupItem
                        id={id}
                        value={option.scope}
                        disabled={unavailable !== null}
                        className={cn("mt-0.5", option.reversible === false && "text-destructive [&_svg]:fill-destructive")}
                        data-testid={id}
                      />
                      <span className="flex min-w-0 flex-1 flex-col gap-0.5">
                        <span className="text-[13px] font-medium leading-tight">{option.title}</span>
                        <span className="text-[12px] leading-snug text-muted-foreground">
                          {unavailable !== null
                            ? unavailable.line
                            : option.scope === "restore"
                              ? option.effect
                              : fromInventory
                                ? "OSDU no longer serves them."
                                : option.scope === "none" && records > 1 ? `For the ${removed.toLocaleString()} already removed.` : option.effect}
                        </span>
                      </span>
                    </label>
                    <span className="flex items-center gap-1 pt-1.5">
                      {option.reversible !== null && <Permanence reversible={option.reversible} />}
                      {known && (
                        <InfoTip label={`About ${option.title.toLowerCase()}`} testId={`${id}-info`}>
                          {unavailable !== null
                            ? <span>{unavailable.why}</span>
                            : (
                              <div className="flex flex-col gap-1.5">
                                <span>{option.scope === "restore" ? RESTORED : fromInventory ? GONE_LEFT : of !== null ? INVENTORY_KEPT : option.ledger}</span>
                                {call !== null && <span className="break-all font-mono text-[11px] text-muted-foreground">{call}</span>}
                              </div>
                            )}
                        </InfoTip>
                      )}
                    </span>
                  </ChoiceRow>
                  {/* The restriction to set is picked under its choice, where the choice acts. */}
                  {option.scope === "restrict" && active && ledger !== null && (
                    <RestrictionPicker
                      restrictions={registered}
                      picked={restriction}
                      onPick={setPicked}
                      partition={restrictionPartition}
                      loading={restrictions.isLoading}
                      failed={restrictions.isError ? (isApiError(restrictions.error) ? restrictions.error.detail ?? restrictions.error.title : String(restrictions.error)) : null}
                      disabled={busy}
                    />
                  )}
                </Fragment>
              );
            })}
          </RadioGroup>
        </div>

        {(!fromInventory || restoring) && rechecksNote}

        {ledger !== null && (
          <div className="flex flex-col gap-1.5">
            <PartHeading>In the ledger</PartHeading>
            <ChoiceRow active={purging} permanent unavailable={!leavesItOut(scope)}>
              <label
                htmlFor="removal-purge-ledger"
                className={cn("flex min-w-0 flex-1 items-start gap-2.5 py-2", leavesItOut(scope) ? "cursor-pointer" : "cursor-not-allowed")}
              >
                <Checkbox
                  id="removal-purge-ledger"
                  checked={purging}
                  onCheckedChange={(checked) => {
                    setDeleteHere(checked === true);
                    setTyped("");
                  }}
                  disabled={busy || !leavesItOut(scope)}
                  className="mt-0.5"
                  data-testid="removal-purge-ledger"
                />
                <span className="flex min-w-0 flex-1 flex-col gap-0.5">
                  <span className="text-[13px] font-medium leading-tight">Delete from the ledger</span>
                  <span className="text-[12px] leading-snug text-muted-foreground">
                    {leavesItOut(scope) ? "Its history here goes; one line of each is kept. The next run delivers it again." : "Only with a choice that leaves it out of OSDU."}
                  </span>
                </span>
              </label>
              <span className="flex items-center gap-1 pt-1.5">
                <Permanence reversible={false} />
                {known && (
                  <InfoTip label="About deleting from the ledger" testId="removal-purge-ledger-info">
                    <div className="flex flex-col gap-1.5">
                      <span>
                        Each record out of OSDU is deleted from the ledger: its attempts and search entries go. One line is kept of
                        it: what it was, its OSDU id and last version, who deleted it and when.
                      </span>
                      <span>
                        Only a record OSDU no longer holds goes: one whose removal failed, or that OSDU still holds, stays as it was.
                        If its row is still in the source, the flow&apos;s next run reads it again and delivers it as a new record. To
                        keep a record out of OSDU, mark its row deleted in the source.
                      </span>
                    </div>
                  </InfoTip>
                )}
              </span>
            </ChoiceRow>
          </div>
        )}

        {fromInventory && !byRemoval && (
          <div className="flex flex-col gap-1.5">
            <PartHeading>In the inventory</PartHeading>
            <ChoiceRow active={dropping} permanent unavailable={restoring}>
              <label
                htmlFor="removal-drop-inventory"
                className={cn("flex min-w-0 flex-1 items-start gap-2.5 py-2", restoring ? "cursor-not-allowed" : "cursor-pointer")}
              >
                <Checkbox
                  id="removal-drop-inventory"
                  checked={dropping}
                  onCheckedChange={(checked) => {
                    setDeleteHere(checked === true);
                    setTyped("");
                  }}
                  disabled={busy || restoring}
                  className="mt-0.5"
                  data-testid="removal-drop-inventory"
                />
                <span className="flex min-w-0 flex-1 flex-col gap-0.5">
                  <span className="text-[13px] font-medium leading-tight">Remove from the inventory</span>
                  <span className="text-[12px] leading-snug text-muted-foreground">
                    {restoring ? "Only while they are left as they are in OSDU." : "Their rows here go, with the versions kept of them. Nothing changes in OSDU."}
                  </span>
                </span>
              </label>
              <span className="flex items-center gap-1 pt-1.5">
                <Permanence reversible={false} />
                <InfoTip label="About removing from the inventory" testId="removal-drop-inventory-info">
                  <div className="flex flex-col gap-1.5">
                    <span>
                      The removal keeps how many ids it removed, who asked and when, not the ids themselves: a lookup by OSDU id no longer
                      finds them in this inventory.
                    </span>
                    <span>
                      No ledger changes. A later build lists again, as a new id, any id OSDU serves again. What a removal of this
                      inventory soft deleted can still be restored from that removal, under Removals.
                    </span>
                  </div>
                </InfoTip>
              </span>
            </ChoiceRow>
          </div>
        )}

        {fromInventory && !restoring && rechecksNote}

        {scope !== "none" && scope !== "unrestrict" && preview.data !== undefined && preview.data.neverDelivered > 0 && (
          <p className="flex items-start gap-2 text-[12px] text-muted-foreground" data-testid="removal-never-delivered">
            <CircleAlert className="mt-0.5 size-3.5 shrink-0 text-warning" />
            {scope === "restrict"
              ? `${preview.data.neverDelivered.toLocaleString()} of these were never delivered: OSDU holds no access of theirs to restrict, and they are passed over.`
              : `${preview.data.neverDelivered.toLocaleString()} of these were never delivered: they are asked for, and come back as already gone.`}
          </p>
        )}

        {scope === "restrict" && restricted > 0 && (
          <p className="flex items-start gap-2 text-[12px] text-muted-foreground" data-testid="removal-restricted-already">
            <Lock className="mt-0.5 size-3.5 shrink-0" />
            {records === 1
              ? "Its access is restricted already: it is passed over. Lift the restriction first to restrict it under another."
              : `${restricted.toLocaleString()} of these are restricted already: they are passed over. Lift a restriction first to restrict a record under another.`}
          </p>
        )}

        {needsTyping && (
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="removal-confirm" className="text-[12px] font-normal text-muted-foreground">
              <span>
                {choice.reversible === false || purging || dropping
                  ? "This cannot be undone."
                  : restoring ? "A removal can take them out of OSDU again." : "OSDU can bring a removed record back."}
                {" Type "}
                <span className="font-mono font-medium text-foreground">{word}</span> to confirm.
              </span>
            </Label>
            <Input
              id="removal-confirm"
              value={typed}
              onChange={(event) => setTyped(event.target.value)}
              disabled={busy}
              autoComplete="off"
              spellCheck={false}
              className="h-8 font-mono"
              data-testid="removal-confirm-input"
            />
          </div>
        )}

        <AlertDialogFooter>
          <Button variant="ghost" size="sm" onClick={onClose} disabled={busy} data-testid="removal-cancel">
            Cancel
          </Button>
          <Button
            variant={scope === "previous" || scope === "restrict" || scope === "unrestrict" || restoring ? "default" : "destructive"}
            size="sm"
            onClick={() => (scope === "none" && !fromInventory ? purge.mutate() : remove.mutate())}
            disabled={busy || nothingAsked || !confirmed || acting === 0 || capped || (ledger !== null && preview.isError) || refused !== null
              || (scope === "restrict" && restriction === null)}
            data-testid="removal-confirm"
          >
            {busy
              ? <Loader2 className="animate-spin" />
              : scope === "previous"
                ? <Undo2 />
                : scope === "restrict" ? <Lock /> : scope === "unrestrict" ? <LockOpen /> : restoring ? <RotateCcw /> : scope === "none" ? <Eraser /> : null}
            {confirmLabel}
            {acting > 1 ? ` (${acting.toLocaleString()})` : ""}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
