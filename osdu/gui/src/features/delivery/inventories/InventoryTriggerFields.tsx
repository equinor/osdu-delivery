import { useEffect, useId, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Checkbox } from "@/components/ui/checkbox";
import { Skeleton } from "@/components/ui/skeleton";
import { SearchInput } from "@/components/SearchInput";
import type { TriggerBodyContribution, TriggerFieldsProps } from "@/modules/registry";
import { inventoryApi, type InventoryFlow } from "../../../api/inventories";
import { useActivePartition } from "../activePartition";
import type { RunValue } from "@/api/types";
import { kindEntity } from "../assertions/assertionFormat";
import { inventoriesPayload } from "./inventoryFormat";

/**
 * The run value naming the partition a run reads (osdu/docs/reference/concepts/partitions.md, The partition a run
 * targets).
 */
const PARTITION = "partition";

/** The payload key these fields own; anything else the run being repeated carried goes with it unchanged. */
const OWNED_KEY = "inventories";

/** An inventory's name as the flow document allows it. */
const INVENTORY_NAME = /^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$/;

/** The list gets a filter once it is longer than this. */
const FILTER_FROM = 8;

function stringsOf(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string" && item.trim() !== "") : [];
}

/** The partition a run of the flow reads, and why it cannot run while the title bar is where it is. */
function partitionOf(flow: InventoryFlow | undefined, active: string | null): { partition: string | null; error: string | null } {
  if (flow === undefined) {
    return { partition: null, error: "Reading the flow's inventories." };
  }

  if (flow.partitions.length > 0) {
    if (active === null) {
      return { partition: null, error: "Pick a partition in the title bar: a run reads the partition picked there." };
    }

    return flow.partitions.includes(active)
      ? { partition: active, error: null }
      : {
        partition: null,
        error: `This flow does not read ${active}, the partition picked in the title bar; it reads ${flow.partitions.join(", ")}. Pick ${flow.partitions.length > 1 ? "one of them" : "it"} in the title bar to run it.`,
      };
  }

  if (flow.problem !== undefined) {
    return { partition: null, error: flow.problem };
  }

  return flow.partition !== undefined && active !== null && flow.partition !== active
    ? {
      partition: null,
      error: `This flow reads ${flow.partition}, the partition its data-partition-id header names, and the title bar is on ${active}. Pick ${flow.partition} in the title bar to run it.`,
    }
    : { partition: null, error: null };
}

/**
 * The trigger dialog's fields for an inventory flow: the partition it reads (the title bar's), and which of its
 * inventories to build or reconcile; its parameters are the dialog's Flow parameters, read from the flow's document.
 * Nothing picked takes every one. A run being repeated opens with the inventories it was given, and keeps any other part
 * of its payload as it was.
 */
export function InventoryTriggerFields({ pipelineId, initialPayload, onChange }: TriggerFieldsProps) {
  const idPrefix = useId();
  const [active] = useActivePartition();
  const board = useQuery({
    queryKey: ["delivery", "inventories", "flow", pipelineId, active],
    queryFn: () => inventoryApi.flow(pipelineId!),
    enabled: pipelineId !== null,
    staleTime: 15000,
  });
  const flow = board.data;
  const [picked, setPicked] = useState<ReadonlySet<string>>(() => new Set(stringsOf(initialPayload?.[OWNED_KEY])));
  const [filter, setFilter] = useState("");
  const [carried] = useState<Record<string, unknown>>(
    () => Object.fromEntries(Object.entries(initialPayload ?? {}).filter(([key]) => key !== OWNED_KEY)),
  );

  const declared = useMemo(() => (flow?.inventories ?? []).filter((inventory) => inventory.declared), [flow]);
  const { partition, error: partitionError } = partitionOf(flow, active);
  const shown = useMemo(() => {
    const term = filter.trim().toLowerCase();
    return term === ""
      ? declared
      : declared.filter((inventory) => inventory.name.toLowerCase().includes(term) || inventory.kind.toLowerCase().includes(term));
  }, [declared, filter]);

  const body = useMemo<TriggerBodyContribution>(() => {
    const known = new Set(declared.map((inventory) => inventory.name.toLowerCase()));
    const missing = flow === undefined ? [] : [...picked].filter((name) => !known.has(name.toLowerCase()));
    const error = partitionError
      ?? ([...picked].some((name) => !INVENTORY_NAME.test(name)) ? "An inventory's name is a letter or digit, then letters, digits, '.', '_' and '-'." : null)
      ?? (missing.length > 0 ? `The flow declares no inventory named ${missing.join(", ")}; clear ${missing.length === 1 ? "it" : "them"} to run.` : null);
    const values: Record<string, RunValue> = {};
    if (partition !== null) {
      values[PARTITION] = partition;
    }

    const payload = { ...carried, ...inventoriesPayload([...picked]) };
    return {
      values: Object.keys(values).length > 0 ? values : undefined,
      payload: Object.keys(payload).length > 0 ? payload : undefined,
      error,
    };
  }, [carried, declared, flow, partition, partitionError, picked]);

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
        <span className="font-mono text-[13px]" data-testid="trigger-partition-value">
          {flow !== undefined && flow.partitions.length === 0
            ? flow.partition ?? "the one its data-partition-id header names"
            : partition ?? active ?? "none picked"}
        </span>
        <p className="text-xs text-muted-foreground">
          The partition picked in the title bar: the run reads its OSDU and compares what it lists with the ledgers kept under it. Nothing is written to OSDU.
        </p>
      </div>


      <div className="flex flex-col gap-2" data-testid="trigger-inventories">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <span className="text-[13px] font-medium">Inventories</span>
          <span className="text-xs text-muted-foreground" data-testid="trigger-inventories-summary">
            {picked.size === 0
              ? `Every inventory runs${declared.length > 0 ? ` (${declared.length})` : ""}.`
              : `${picked.size} ${picked.size === 1 ? "inventory" : "inventories"} of ${declared.length} run.`}
          </span>
        </div>
        {board.isPending && pipelineId !== null && <Skeleton className="h-24 w-full" />}
        {declared.length > FILTER_FROM && (
          <SearchInput value={filter} onChange={setFilter} placeholder="Filter inventories by name or kind" label="Filter inventories" testId="trigger-inventories-filter" />
        )}
        {declared.length > 0 && (
          <div className="max-h-64 overflow-y-auto rounded-md border" data-testid="trigger-inventories-list">
            {shown.map((inventory) => {
              const id = `${idPrefix}-inventory-${inventory.name}`;
              return (
                <label key={inventory.name} htmlFor={id} className="flex cursor-pointer items-center gap-2.5 border-b px-2.5 py-1.5 last:border-b-0 hover:bg-muted/50">
                  <Checkbox
                    id={id}
                    checked={picked.has(inventory.name)}
                    onCheckedChange={() => toggle(inventory.name)}
                    data-testid={`trigger-inventory-${inventory.name}`}
                  />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate font-mono text-[12px]">{inventory.name}</span>
                    <span className="block truncate text-[11px] text-muted-foreground">
                      {kindEntity(inventory.kind)}{inventory.query === undefined ? "" : `, where ${inventory.query}`}
                    </span>
                  </span>
                  <span className="hidden font-mono text-[10.5px] tabular-nums text-muted-foreground sm:inline">
                    {inventory.inventory?.lastBuiltUtc === undefined ? "not built" : `${(inventory.inventory.raised ?? 0).toLocaleString("en-US")} raised`}
                  </span>
                </label>
              );
            })}
            {shown.length === 0 && <p className="px-2.5 py-3 text-xs text-muted-foreground">No inventory matches the filter.</p>}
          </div>
        )}
        <p className="text-xs text-muted-foreground">
          Pick inventories to run only those; the others keep what their last build found. A reconcile compares each with the ledgers as they stand now without listing the kind again; a plan counts what a build would read.
        </p>
      </div>
    </div>
  );
}
