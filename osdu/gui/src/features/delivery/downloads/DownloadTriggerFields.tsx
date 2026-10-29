import { useEffect, useId, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Checkbox } from "@/components/ui/checkbox";
import { Skeleton } from "@/components/ui/skeleton";
import { SearchInput } from "@/components/SearchInput";
import type { TriggerBodyContribution, TriggerFieldsProps } from "@/modules/registry";
import type { RunValue } from "@/api/types";
import { downloadApi, type DownloadFlow } from "../../../api/downloads";
import { useActivePartition } from "../activePartition";
import { kindEntity } from "../assertions/assertionFormat";
import { downloadsPayload } from "./downloadFormat";

/** The run value naming the partition a run reads (osdu/docs/reference/concepts/partitions.md, The partition a run targets). */
const PARTITION = "partition";

/** The payload keys these fields own; anything else the run being repeated carried goes with it unchanged. */
const OWNED_KEYS = ["downloads", "force"];

/** A download's name as the flow document allows it. */
const DOWNLOAD_NAME = /^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$/;

/** The list gets a filter once it is longer than this. */
const FILTER_FROM = 8;

function stringsOf(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string" && item.trim() !== "") : [];
}

/** The partition a run of the flow reads, and why it cannot run while the title bar is where it is. */
function partitionOf(flow: DownloadFlow | undefined, active: string | null): { partition: string | null; error: string | null } {
  if (flow === undefined) {
    return { partition: null, error: "Reading the flow's downloads." };
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
 * The trigger dialog's fields for a download flow: the partition it reads (the title bar's), which of its downloads to run, and
 * whether a download run writes every record OSDU serves again rather than what is new or changed; its parameters are the
 * dialog's Flow parameters, read from the flow's document. Nothing picked takes every download. A run being repeated opens with
 * the downloads it was given and its force, and keeps any other part of its payload as it was.
 */
export function DownloadTriggerFields({ pipelineId, operation, initialPayload, onChange }: TriggerFieldsProps) {
  const idPrefix = useId();
  const [active] = useActivePartition();
  const board = useQuery({
    queryKey: ["delivery", "downloads", "flow", pipelineId, active],
    queryFn: () => downloadApi.flow(pipelineId!),
    enabled: pipelineId !== null,
    staleTime: 15000,
  });
  const flow = board.data;
  const [picked, setPicked] = useState<ReadonlySet<string>>(() => new Set(stringsOf(initialPayload?.downloads)));
  const [force, setForce] = useState(initialPayload?.force === true);
  const [filter, setFilter] = useState("");
  const [carried] = useState<Record<string, unknown>>(
    () => Object.fromEntries(Object.entries(initialPayload ?? {}).filter(([key]) => !OWNED_KEYS.includes(key))),
  );

  // A plan writes no file, so it never writes every record again.
  const planning = operation === "plan";
  const declared = useMemo(() => (flow?.downloads ?? []).filter((download) => download.declared), [flow]);
  const { partition, error: partitionError } = partitionOf(flow, active);
  const shown = useMemo(() => {
    const term = filter.trim().toLowerCase();
    return term === ""
      ? declared
      : declared.filter((download) => download.name.toLowerCase().includes(term) || download.kind.toLowerCase().includes(term));
  }, [declared, filter]);

  const body = useMemo<TriggerBodyContribution>(() => {
    const known = new Set(declared.map((download) => download.name.toLowerCase()));
    const missing = flow === undefined ? [] : [...picked].filter((name) => !known.has(name.toLowerCase()));
    const error = partitionError
      ?? ([...picked].some((name) => !DOWNLOAD_NAME.test(name)) ? "A download's name is a letter or digit, then letters, digits, '.', '_' and '-'." : null)
      ?? (missing.length > 0 ? `The flow declares no download named ${missing.join(", ")}; clear ${missing.length === 1 ? "it" : "them"} to run.` : null);
    const values: Record<string, RunValue> = {};
    if (partition !== null) {
      values[PARTITION] = partition;
    }

    const payload = { ...carried, ...downloadsPayload([...picked], force && !planning) };
    return {
      values: Object.keys(values).length > 0 ? values : undefined,
      payload: Object.keys(payload).length > 0 ? payload : undefined,
      error,
    };
  }, [carried, declared, flow, force, partition, partitionError, picked, planning]);

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
          The partition picked in the title bar: the run lists its records and writes them to the flow&apos;s folder. Nothing is written to OSDU.
        </p>
      </div>

      <div className="flex flex-col gap-2" data-testid="trigger-downloads">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <span className="text-[13px] font-medium">Downloads</span>
          <span className="text-xs text-muted-foreground" data-testid="trigger-downloads-summary">
            {picked.size === 0
              ? `Every download runs${declared.length > 0 ? ` (${declared.length})` : ""}.`
              : `${picked.size} ${picked.size === 1 ? "download" : "downloads"} of ${declared.length} run.`}
          </span>
        </div>
        {board.isPending && pipelineId !== null && <Skeleton className="h-24 w-full" />}
        {declared.length > FILTER_FROM && (
          <SearchInput value={filter} onChange={setFilter} placeholder="Filter downloads by name or kind" label="Filter downloads" testId="trigger-downloads-filter" />
        )}
        {declared.length > 0 && (
          <div className="max-h-64 overflow-y-auto rounded-md border" data-testid="trigger-downloads-list">
            {shown.map((download) => {
              const id = `${idPrefix}-download-${download.name}`;
              return (
                <label key={download.name} htmlFor={id} className="flex cursor-pointer items-center gap-2.5 border-b px-2.5 py-1.5 last:border-b-0 hover:bg-muted/50">
                  <Checkbox
                    id={id}
                    checked={picked.has(download.name)}
                    onCheckedChange={() => toggle(download.name)}
                    data-testid={`trigger-download-${download.name}`}
                  />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate font-mono text-[12px]">{download.name}</span>
                    <span className="block truncate text-[11px] text-muted-foreground">
                      {kindEntity(download.kind)}{download.query === undefined ? "" : `, where ${download.query}`}
                    </span>
                  </span>
                  <span className="hidden font-mono text-[10.5px] tabular-nums text-muted-foreground sm:inline">
                    {download.download === undefined ? "no run yet" : `${download.download.counts.due.toLocaleString("en-US")} due`}
                  </span>
                </label>
              );
            })}
            {shown.length === 0 && <p className="px-2.5 py-3 text-xs text-muted-foreground">No download matches the filter.</p>}
          </div>
        )}
        <p className="text-xs text-muted-foreground">
          Pick downloads to run only those; the others keep the files they wrote. A plan counts what a run would list and names the folder it would write to.
        </p>
      </div>

      {!planning && (
        <label htmlFor={`${idPrefix}-force`} className="flex cursor-pointer items-start gap-2.5" data-testid="trigger-download-force">
          <Checkbox id={`${idPrefix}-force`} checked={force} onCheckedChange={(checked) => setForce(checked === true)} data-testid="trigger-download-force-box" />
          <span className="flex flex-col gap-0.5">
            <span className="text-[13px] font-medium">Write every record again</span>
            <span className="text-xs text-muted-foreground">
              Rewrite the file of every record OSDU serves, not only those new or changed since the last run: for a folder whose files were changed or removed by hand.
            </span>
          </span>
        </label>
      )}
    </div>
  );
}
