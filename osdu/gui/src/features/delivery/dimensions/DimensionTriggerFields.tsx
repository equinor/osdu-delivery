import { useEffect, useId, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Checkbox } from "@/components/ui/checkbox";
import { Skeleton } from "@/components/ui/skeleton";
import { SearchInput } from "@/components/SearchInput";
import type { TriggerBodyContribution, TriggerFieldsProps } from "@/modules/registry";
import { deliveryApi, type DeliveryDimensionFlow } from "../../../api/delivery";
import { useActivePartition } from "../activePartition";
import type { RunValue } from "@/api/types";
import { counted, kindEntity } from "../assertions/assertionFormat";
import { dimensionsPayload } from "./dimensionFormat";

/** The run value naming the partition a run builds in (osdu/docs/reference/flow/dimension.md, Partitions). */
const PARTITION = "partition";

/** The payload key these fields own; anything else the run being repeated carried goes with it unchanged. */
const OWNED_KEY = "dimensions";

/** A dimension's name as the flow document allows it. */
const DIMENSION_NAME = /^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$/;

/** The list gets a filter once it is longer than this. */
const FILTER_FROM = 8;

function stringsOf(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string" && item.trim() !== "") : [];
}

/** The partition a run of the flow builds in, and why it cannot run while the title bar is where it is. */
function partitionOf(flow: DeliveryDimensionFlow | undefined, active: string | null): { partition: string | null; error: string | null } {
  if (flow === undefined) {
    return { partition: null, error: "Reading the flow's dimensions." };
  }

  if (flow.partitions.length > 0) {
    if (active === null) {
      return { partition: null, error: "Pick a partition in the title bar: a run builds in the partition picked there." };
    }

    return flow.partitions.includes(active)
      ? { partition: active, error: null }
      : {
        partition: null,
        error: `This flow does not build in ${active}, the partition picked in the title bar; it builds in ${flow.partitions.join(", ")}. Pick ${flow.partitions.length > 1 ? "one of them" : "it"} in the title bar to build it.`,
      };
  }

  if (flow.problem !== null) {
    return { partition: null, error: flow.problem };
  }

  return flow.partition !== null && active !== null && flow.partition !== active
    ? {
      partition: null,
      error: `This flow builds in ${flow.partition}, the partition its data-partition-id header names, and the title bar is on ${active}. Pick ${flow.partition} in the title bar to build it.`,
    }
    : { partition: null, error: null };
}

/**
 * The trigger dialog's fields for a dimension flow: the partition it builds in (the title bar's), and which of its
 * dimensions to build; its parameters are the dialog's Flow parameters, read from the flow's document. Nothing picked
 * builds every one. A run being repeated opens with the dimensions it was given, and keeps any other part of its payload
 * as it was.
 */
export function DimensionTriggerFields({ pipelineId, initialPayload, onChange }: TriggerFieldsProps) {
  const idPrefix = useId();
  const [active] = useActivePartition();
  const board = useQuery({
    queryKey: ["delivery", "dimensions", "flow", pipelineId, active],
    queryFn: () => deliveryApi.dimensionFlowBoard(pipelineId!),
    enabled: pipelineId !== null,
    staleTime: 15000,
  });
  const flow = board.data?.flows[0];
  const [picked, setPicked] = useState<ReadonlySet<string>>(() => new Set(stringsOf(initialPayload?.[OWNED_KEY])));
  const [filter, setFilter] = useState("");
  const [carried] = useState<Record<string, unknown>>(
    () => Object.fromEntries(Object.entries(initialPayload ?? {}).filter(([key]) => key !== OWNED_KEY)),
  );

  const declared = useMemo(() => (flow?.dimensions ?? []).filter((dimension) => dimension.declared), [flow]);
  const { partition, error: partitionError } = partitionOf(flow, active);
  const shown = useMemo(() => {
    const term = filter.trim().toLowerCase();
    return term === ""
      ? declared
      : declared.filter((dimension) => dimension.name.toLowerCase().includes(term)
        || dimension.kind.toLowerCase().includes(term)
        || dimension.path.toLowerCase().includes(term));
  }, [declared, filter]);

  const body = useMemo<TriggerBodyContribution>(() => {
    const known = new Set(declared.map((dimension) => dimension.name.toLowerCase()));
    const missing = flow === undefined ? [] : [...picked].filter((name) => !known.has(name.toLowerCase()));
    const error = partitionError
      ?? ([...picked].some((name) => !DIMENSION_NAME.test(name)) ? "A dimension's name is a letter or digit, then letters, digits, '.', '_' and '-'." : null)
      ?? (missing.length > 0 ? `The flow declares no dimension named ${missing.join(", ")}; clear ${missing.length === 1 ? "it" : "them"} to build.` : null);
    const values: Record<string, RunValue> = {};
    if (partition !== null) {
      values[PARTITION] = partition;
    }

    const payload = { ...carried, ...dimensionsPayload([...picked]) };
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
          The partition picked in the title bar: the build reads its OSDU search, and keeps the dimensions under it. Nothing is written to OSDU.
        </p>
      </div>


      <div className="flex flex-col gap-2" data-testid="trigger-dimensions">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <span className="text-[13px] font-medium">Dimensions</span>
          <span className="text-xs text-muted-foreground" data-testid="trigger-dimensions-summary">
            {picked.size === 0
              ? `Every dimension builds${declared.length > 0 ? ` (${declared.length})` : ""}.`
              : `${counted(picked.size, "dimension")} of ${declared.length} build.`}
          </span>
        </div>
        {board.isPending && pipelineId !== null && <Skeleton className="h-24 w-full" />}
        {declared.length > FILTER_FROM && (
          <SearchInput value={filter} onChange={setFilter} placeholder="Filter dimensions by name, kind or path" label="Filter dimensions" testId="trigger-dimensions-filter" />
        )}
        {declared.length > 0 && (
          <div className="max-h-64 overflow-y-auto rounded-md border" data-testid="trigger-dimensions-list">
            {shown.map((dimension) => {
              const id = `${idPrefix}-dimension-${dimension.name}`;
              return (
                <label key={dimension.name} htmlFor={id} className="flex cursor-pointer items-center gap-2.5 border-b px-2.5 py-1.5 last:border-b-0 hover:bg-muted/50">
                  <Checkbox
                    id={id}
                    checked={picked.has(dimension.name)}
                    onCheckedChange={() => toggle(dimension.name)}
                    data-testid={`trigger-dimension-${dimension.name}`}
                  />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate font-mono text-[12px]">{dimension.name}</span>
                    <span className="block truncate text-[11px] text-muted-foreground">
                      {kindEntity(dimension.kind)} {dimension.path}
                      {dimension.buildsHere ? "" : ", skipped: it is not built in this partition"}
                    </span>
                  </span>
                  <span className="hidden font-mono text-[10.5px] tabular-nums text-muted-foreground sm:inline">
                    {dimension.lastBuiltUtc === null ? "not built" : counted(dimension.values, "value")}
                  </span>
                </label>
              );
            })}
            {shown.length === 0 && <p className="px-2.5 py-3 text-xs text-muted-foreground">No dimension matches the filter.</p>}
          </div>
        )}
        <p className="text-xs text-muted-foreground">
          Pick dimensions to build only those; the others keep what their last build wrote. A plan settles each field and counts the records without reading a value.
        </p>
      </div>
    </div>
  );
}
