import { useEffect, useId, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Checkbox } from "@/components/ui/checkbox";
import { Skeleton } from "@/components/ui/skeleton";
import type { TriggerBodyContribution, TriggerFieldsProps } from "@/modules/registry";
import { removalApi, type RemovalFlow } from "../../../api/removals";
import { useActivePartition } from "../activePartition";
import type { RunValue } from "@/api/types";
import { ledgerName } from "./removalFormat";

/** The run value naming the partition a run removes in (osdu/docs/reference/concepts/partitions.md, The partition a run targets). */
const PARTITION = "partition";

/** The payload key these fields own. */
const OWNED_KEY = "entries";

/**
 * The payload keys a repeated run never carries over: an approval is given on the Removals tab, for the count shown there,
 * and repeating one would approve whatever waits now, unseen.
 */
const NEVER_CARRIED = new Set([OWNED_KEY, "approve", "confirm"]);

function stringsOf(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string" && item.trim() !== "") : [];
}

/** The partition a run of the flow removes in, and why it cannot run while the title bar is where it is. */
function partitionOf(flow: RemovalFlow | undefined, active: string | null): { partition: string | null; error: string | null } {
  if (flow === undefined) {
    return { partition: null, error: "Reading the flow's removals." };
  }

  if (flow.partition === undefined) {
    return { partition: null, error: flow.problem ?? "Pick a partition in the title bar: a run removes in the partition picked there." };
  }

  return active !== null && flow.partition.toLowerCase() !== active.toLowerCase()
    ? { partition: null, error: `The flow removes in ${flow.partition}, and the title bar is on ${active}. Pick ${flow.partition} in the title bar to run it.` }
    : { partition: flow.partition, error: null };
}

/**
 * The trigger dialog's fields for a removal flow: the partition it removes in (the title bar's), and which of its removals
 * to run; nothing picked runs every one. Its parameters are the dialog's Flow parameters, read from the flow's document. A
 * run being repeated opens with the removals it was given, never with an approval, which is given on the flow's Removals
 * tab for the count shown there.
 */
export function RemovalTriggerFields({ pipelineId, operation, initialPayload, onChange }: TriggerFieldsProps) {
  const idPrefix = useId();
  const [active] = useActivePartition();
  const board = useQuery({
    queryKey: ["delivery", "removals", "flow", pipelineId, active],
    queryFn: () => removalApi.flow(pipelineId!),
    enabled: pipelineId !== null,
    staleTime: 15000,
  });
  const flow = board.data;
  const [picked, setPicked] = useState<ReadonlySet<string>>(() => new Set(stringsOf(initialPayload?.[OWNED_KEY])));
  const [carried] = useState<Record<string, unknown>>(
    () => Object.fromEntries(Object.entries(initialPayload ?? {}).filter(([key]) => !NEVER_CARRIED.has(key))),
  );

  const removals = useMemo(() => flow?.removals ?? [], [flow]);
  const { partition, error: partitionError } = partitionOf(flow, active);
  const body = useMemo<TriggerBodyContribution>(() => {
    const known = new Set(removals.map((removal) => removal.name.toLowerCase()));
    const missing = flow === undefined ? [] : [...picked].filter((name) => !known.has(name.toLowerCase()));
    const error = partitionError
      ?? (missing.length > 0 ? `The flow declares no removal named ${missing.join(", ")}; clear ${missing.length === 1 ? "it" : "them"} to run.` : null);
    const values: Record<string, RunValue> = {};
    if (partition !== null) {
      values[PARTITION] = partition;
    }

    const payload: Record<string, unknown> = { ...carried };
    if (picked.size > 0) {
      payload[OWNED_KEY] = [...picked];
    }

    return {
      values: Object.keys(values).length > 0 ? values : undefined,
      payload: Object.keys(payload).length > 0 ? payload : undefined,
      error,
    };
  }, [carried, flow, partition, partitionError, picked, removals]);

  useEffect(() => {
    onChange(body);
  }, [body, onChange]);

  const toggle = (name: string) => setPicked((was) => {
    const next = new Set(was);
    if (!next.delete(name)) {
      next.add(name);
    }

    return next;
  });

  return (
    <div className="flex flex-col gap-3" data-testid="trigger-kind-fields">
      <div className="flex flex-col gap-1" data-testid="trigger-partition">
        <span className="text-[13px] font-medium">Partition</span>
        <span className="font-mono text-[13px]" data-testid="trigger-partition-value">{partition ?? active ?? "none picked"}</span>
        <p className="text-xs text-muted-foreground">
          {operation === "plan"
            ? "The partition picked in the title bar: a plan reads the sources and compares them with the ledgers kept under it, and writes nothing."
            : "The partition picked in the title bar: the run compares the sources with the ledgers kept under it and takes out of its OSDU what the sources no longer hold."}
        </p>
      </div>


      <div className="flex flex-col gap-2" data-testid="trigger-removals">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <span className="text-[13px] font-medium">Removals</span>
          <span className="text-xs text-muted-foreground" data-testid="trigger-removals-summary">
            {picked.size === 0
              ? `Every removal runs${removals.length > 0 ? ` (${removals.length})` : ""}.`
              : `${picked.size} ${picked.size === 1 ? "removal" : "removals"} of ${removals.length} run.`}
          </span>
        </div>
        {board.isPending && pipelineId !== null && <Skeleton className="h-24 w-full" />}
        {removals.length > 0 && (
          <div className="max-h-64 overflow-y-auto rounded-md border" data-testid="trigger-removals-list">
            {removals.map((removal) => {
              const id = `${idPrefix}-removal-${removal.name}`;
              return (
                <label key={removal.name} htmlFor={id} className="flex cursor-pointer items-center gap-2.5 border-b px-2.5 py-1.5 last:border-b-0 hover:bg-muted/50">
                  <Checkbox id={id} checked={picked.has(removal.name)} onCheckedChange={() => toggle(removal.name)} data-testid={`trigger-removal-${removal.name}`} />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate font-mono text-[12px]">{removal.name}</span>
                    <span className="block truncate text-[11px] text-muted-foreground">{ledgerName(removal)}</span>
                  </span>
                  {removal.awaitingApproval > 0 && (
                    <span className="hidden font-mono text-[10.5px] tabular-nums text-warning sm:inline">
                      {`${removal.awaitingApproval.toLocaleString("en-US")} held`}
                    </span>
                  )}
                </label>
              );
            })}
          </div>
        )}
        <p className="text-xs text-muted-foreground">
          Pick removals to run only those. Each reads its source&apos;s keys whole, compares them with the delivery flow&apos;s ledger, and takes out of OSDU only what it has not taken out before; a run that finds more than the guard allows waits for approval on the Removals tab.
        </p>
      </div>
    </div>
  );
}
