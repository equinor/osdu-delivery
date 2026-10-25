import { useEffect, useId, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import type { RunValue } from "@/api/types";
import type { TriggerBodyContribution, TriggerFieldsProps } from "@/modules/registry";
import { deliveryApi } from "../../api/delivery";
import { useActivePartition } from "./activePartition";
import { lines, singleValue } from "./runValues";
import { PLANNED_PER_PASS } from "./ReleaseDialog";
import { counted } from "./assertions/assertionFormat";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
/** An interface's name: a letter, then letters, digits, '_' and '-' (SourceDefinition.IsInterfaceName). */
const INTERFACE_NAME = /^[A-Za-z][A-Za-z0-9_-]{0,63}$/;

/** The operation a kind's run performs when the dialog could not learn the registered ones. */
const DEFAULT_OPERATION: Record<string, string> = {
  delivery: "deliver",
  retrieval: "retrieve",
  cache: "refresh",
};

type RedeliverScope = "all" | "metadata" | "payload" | "record" | "files" | "bulk" | "workflow";

/**
 * What a deliver run sends: what changed; what renders differently once its records are rendered again (bringing them up
 * to date); or a part of its records again whatever their hashes say.
 */
type SendAgain = "changed" | "uptodate" | RedeliverScope;

const REDELIVER_SCOPES: readonly { value: RedeliverScope; label: string }[] = [
  { value: "all", label: "Metadata and payload" },
  { value: "metadata", label: "Metadata only" },
  { value: "payload", label: "Payload only" },
];

/** The parts a route sends by name, which a run started from the CLI or the API may have named and a repeat keeps. */
const PART_LABELS: Partial<Record<RedeliverScope, string>> = {
  record: "The record only",
  files: "Files only",
  bulk: "Bulk data only",
  workflow: "The workflow run only",
};

const SEND_CHANGED: { value: SendAgain; label: string } = { value: "changed", label: "Nothing: send only what changed" };

const SEND_UPTODATE: { value: SendAgain; label: string } = { value: "uptodate", label: "What renders differently (bring up to date)" };


/**
 * The run value naming the partition a run of a flow that works in partitions targets
 * (osdu/docs/reference/concepts/partitions.md, The partition a run targets).
 */
const PARTITION = "partition";

/**
 * The run value a scheduled cache refresh names to build every partition the flow serves, one after another. A run started
 * here never names it: it acts in the partition picked in the title bar.
 */
const EVERY_PARTITION = "*";

/**
 * The partitions a flow serves (the ones it names under `partitions:`, in its order, or every registered one), read from
 * what the control plane already serves about it: a delivery flow's interface listing, which names a row's partition, and
 * the cache listing, which names a cache flow's. Empty for a flow whose partition is its header's, and for every other
 * kind; for such a flow `headerPartition` is the partition it delivers to or fills, once the control plane knows it (a
 * delivery flow's ledger, once it has run; the cache the flow is listed under).
 */
function useFlowPartitions(flowKind: string, pipelineId: string | null) {
  const delivery = useQuery({
    queryKey: ["delivery", "interfaces", pipelineId],
    queryFn: () => deliveryApi.interfaces(pipelineId!),
    enabled: flowKind === "delivery" && pipelineId !== null,
    staleTime: 30000,
  });
  const caches = useQuery({
    queryKey: ["delivery", "caches"],
    queryFn: () => deliveryApi.caches(),
    enabled: flowKind === "cache" && pipelineId !== null,
    staleTime: 30000,
  });
  const partitions = useMemo(() => {
    if (flowKind === "delivery") {
      return [...new Set((delivery.data ?? []).map((row) => row.partition ?? null).filter((name): name is string => name !== null))];
    }

    if (flowKind === "cache") {
      const flow = (caches.data ?? []).flatMap((cache) => cache.flows).find((f) => f.pipelineId === pipelineId);
      return flow?.partitions ?? [];
    }

    return [];
  }, [flowKind, delivery.data, caches.data, pipelineId]);
  const headerPartition = useMemo(() => {
    if (flowKind === "delivery") {
      return (delivery.data ?? []).map((row) => row.stats.headerPartition ?? null).find((name): name is string => name !== null) ?? null;
    }

    if (flowKind === "cache") {
      const holding = (caches.data ?? []).find((cache) => cache.flows.some((f) => f.pipelineId === pipelineId && (f.partitions ?? []).length === 0));
      return holding?.scope ?? null;
    }

    return null;
  }, [flowKind, delivery.data, caches.data, pipelineId]);
  // The types a cache flow declares, for each partition it fills: a type may be built for some of its partitions only.
  const cacheTypes = useMemo(() => {
    if (flowKind !== "cache") {
      return [];
    }

    return (caches.data ?? []).flatMap((cache) => cache.flows
      .filter((f) => f.pipelineId === pipelineId)
      .map((f) => ({ scope: cache.scope, types: f.types })));
  }, [flowKind, caches.data, pipelineId]);
  const loading = pipelineId !== null
    && ((flowKind === "delivery" && delivery.isPending) || (flowKind === "cache" && caches.isPending));
  return { partitions, headerPartition, loading, interfaces: flowKind === "delivery" ? delivery.data : undefined, cacheTypes };
}

/** The types of a cache flow a run in `scope` may name: those it declares there, or every one it declares when the partition is not known yet. */
function typesIn(listed: readonly { scope: string; types: string[] }[], scope: string | null): string[] {
  const here = scope === null ? undefined : listed.find((entry) => entry.scope === scope);
  const names = here?.types ?? listed.flatMap((entry) => entry.types);
  return [...new Set(names)].sort((a, b) => a.localeCompare(b));
}

/** The partitions of a list as a sentence names them: "dev", "dev and test", "dev, test and prod". */
function listed(names: readonly string[]): string {
  return names.length <= 1 ? names.join("") : `${names.slice(0, -1).join(", ")} and ${names[names.length - 1]}`;
}

/**
 * The payload keys these fields own; anything else the run being repeated carried goes with it unchanged. maxRecords is a
 * run setting the dialog's Run settings own, opened with what the run being repeated gave it, so it is not carried here.
 */
const OWNED_KEYS = new Set(["force", "submissionId", "recordKeys", "maxRecords", "redeliver", "rerender", "interface", "interfaces", "types"]);

const FORCE_HINTS: Record<string, string> = {
  delivery: "Look at every record even when no source row changed, re-plan a completed submission, verify records verified recently. A record that renders and hashes as it was delivered is still not sent: Send again does that.",
  retrieval: "Start again at the declared start instead of continuing from the last run's watermark.",
};

/**
 * What force does to an undo run (osdu/docs/reference/concepts/removal-and-reversal.md, Unfinished deliveries and their
 * undo).
 */
const UNDO_FORCE_HINT = "Also retry the undos that failed as often as the sweep tries (10 times), which no deliver or drain run retries any more: run it once what OSDU refused them for is fixed.";

function isRedeliverScope(value: unknown): value is RedeliverScope {
  return value === "all" || value === "metadata" || value === "payload" || (typeof value === "string" && value in PART_LABELS);
}

/**
 * The trigger dialog's fields for delivery, retrieval and cache flows: the partition, force, and for a delivery flow the
 * submission to work on, the records to scope the run to, the part of them to send again and the interfaces; for a cache
 * flow its types. The flow's parameters and its run settings (a delivery's Records at most) are the dialog's own Flow
 * parameters and Run settings, read from the flow's document. Which fields apply follows the operation picked; reading
 * every row of the scope again is the flow kind's own `replan` operation rather than a field here. A run being repeated
 * opens with what it was given, and keeps any other part of its payload (the key slices a fan-out member took) as it was.
 */
export function DeliveryTriggerFields({ flowKind, pipelineId, operation, initialValues, initialPayload, onChange }: TriggerFieldsProps) {
  const idPrefix = useId();
  const [force, setForce] = useState(() => initialPayload?.force === true);
  // The partition is never picked here: a run acts in the partition picked in the title bar, which every page follows
  // (osdu/docs/reference/concepts/partitions.md, In the GUI and the API), so the partition a run writes to is the one the
  // operator was looking at. A run being repeated ran in a partition of its own; it runs in the title bar's now, and the
  // fields say so.
  const [active] = useActivePartition();
  const [ranIn] = useState<string | null>(() => singleValue(initialValues, PARTITION));
  const [submissionId, setSubmissionId] = useState(
    () => (typeof initialPayload?.submissionId === "string" ? initialPayload.submissionId : ""),
  );
  const [recordKeysText, setRecordKeysText] = useState(() => (
    Array.isArray(initialPayload?.recordKeys)
      ? initialPayload.recordKeys.filter((key): key is string => typeof key === "string").join("\n")
      : ""
  ));
  const [redeliver, setRedeliver] = useState<SendAgain>(
    () => (initialPayload?.rerender === true ? "uptodate" : isRedeliverScope(initialPayload?.redeliver) ? initialPayload.redeliver : "changed"),
  );
  const [repeatedPart] = useState<RedeliverScope | null>(
    () => (isRedeliverScope(initialPayload?.redeliver) && PART_LABELS[initialPayload.redeliver] !== undefined ? initialPayload.redeliver : null),
  );
  // A source delivers several interfaces; a run can take some of them, and the ones it leaves out are not run, their
  // records read from the ledger as they stand. Either payload key the kind accepts opens the field.
  const [interfacesText, setInterfacesText] = useState(() => (
    Array.isArray(initialPayload?.interfaces)
      ? initialPayload.interfaces.filter((name): name is string => typeof name === "string").join("\n")
      : typeof initialPayload?.interface === "string" ? initialPayload.interface : ""
  ));
  const [carried] = useState<Record<string, unknown>>(
    () => Object.fromEntries(Object.entries(initialPayload ?? {}).filter(([key]) => !OWNED_KEYS.has(key))),
  );
  // A cache run may refresh or plan some of the flow's types; a run being repeated opens with the ones it named.
  const [pickedTypes, setPickedTypes] = useState<ReadonlySet<string>>(() => new Set(
    Array.isArray(initialPayload?.types) ? initialPayload.types.filter((name): name is string => typeof name === "string") : [],
  ));

  const effectiveOperation = operation ?? DEFAULT_OPERATION[flowKind] ?? null;
  const deliveryKind = flowKind === "delivery";
  const takesSubmission = deliveryKind
    && (effectiveOperation === "deliver" || effectiveOperation === "intake" || effectiveOperation === "drain");
  const takesRecordScope = deliveryKind && (effectiveOperation === "deliver" || effectiveOperation === "verify" || effectiveOperation === "sync");
  // A drain delivers what a submission planned and a sync reads every row it is given: neither has a gate to force. A
  // refresh captures every type it is given in full, so a cache run has none either.
  const takesForce = flowKind !== "cache" && !(deliveryKind && (effectiveOperation === "drain" || effectiveOperation === "sync"));
  const takesTypes = flowKind === "cache";
  const recordKeys = useMemo(() => lines(recordKeysText), [recordKeysText]);
  const takesRedeliver = deliveryKind && effectiveOperation === "deliver";
  // Records named by key are always sent again or brought up to date, so naming them makes "only what changed" everything.
  const sendAgain: SendAgain = recordKeys.length > 0 && redeliver === "changed" ? "all" : redeliver;
  const sendAgainOptions = useMemo(() => {
    const part = repeatedPart === null ? undefined : PART_LABELS[repeatedPart];
    const scopes = repeatedPart !== null && part !== undefined ? [...REDELIVER_SCOPES, { value: repeatedPart, label: part }] : REDELIVER_SCOPES;
    return recordKeys.length > 0 ? [SEND_UPTODATE, ...scopes] : [SEND_CHANGED, SEND_UPTODATE, ...scopes];
  }, [recordKeys.length, repeatedPart]);
  const takesInterfaces = deliveryKind && effectiveOperation !== null;
  const interfaceNames = useMemo(() => lines(interfacesText), [interfacesText]);
  const { partitions, headerPartition, loading: partitionsLoading, cacheTypes } = useFlowPartitions(flowKind, pipelineId);
  const takesPartition = partitions.length > 0;
  // A flow that works in partitions runs in the title bar's partition when it serves it, and not at all otherwise; a flow
  // whose partition is its header's takes none, and runs only while the title bar is on the partition its header names.
  const partition = takesPartition && active !== null && partitions.includes(active) ? active : null;
  const typeNames = useMemo(() => typesIn(cacheTypes, partition ?? headerPartition), [cacheTypes, partition, headerPartition]);
  const verb = deliveryKind ? "deliver to" : "build a cache for";
  const partitionError = partitionsLoading
    ? "Reading the partitions the flow serves."
    : takesPartition && active === null
      ? "Pick a partition in the title bar: a run acts in the partition picked there."
      : takesPartition && partition === null
        ? `This flow does not ${verb} ${active}, the partition picked in the title bar; it serves ${listed(partitions)}. Pick ${partitions.length > 1 ? "one of them" : "it"} in the title bar to run it.`
        : !takesPartition && headerPartition !== null && active !== null && headerPartition !== active
          ? `This flow ${deliveryKind ? "delivers to" : "fills the cache of"} ${headerPartition}, the partition its data-partition-id header names, and the title bar is on ${active}. Pick ${headerPartition} in the title bar to run it.`
          : null;
  // A repeated run that worked on one partition's submission or records cannot be taken to another's.
  const movedFrom = ranIn !== null && ranIn !== EVERY_PARTITION && partition !== null && ranIn !== partition ? ranIn : null;
  const carriesLedgerWork = (takesSubmission && submissionId.trim() !== "") || (takesRecordScope && recordKeys.length > 0);

  const body = useMemo<TriggerBodyContribution>(() => {
    const trimmedSubmission = submissionId.trim();
    // Client-side mirror of the kind's own validation, so obvious mistakes are caught before the round trip; the control
    // plane validates authoritatively and its problem details still render if anything slips through. The flow's
    // parameters and its run settings are the dialog's, which the control plane explains as they are filled in.
    const error = partitionError
      ?? (movedFrom !== null && carriesLedgerWork
        ? `The submission and records this run names are ${movedFrom}'s, where the run being repeated ran. Pick ${movedFrom} in the title bar to run it again there, or clear them.`
        : null)
      ?? (takesSubmission && trimmedSubmission !== "" && !UUID.test(trimmedSubmission)
        ? "The submission id must be a UUID."
        : takesRedeliver && sendAgain !== "changed" && takesSubmission && trimmedSubmission !== ""
          ? "A run on a submission delivers what that submission planned; clear the submission, or set Send again to nothing."
          : takesRecordScope && recordKeys.some((key) => !UUID.test(key))
          ? "Every record key must be a UUID (one per line)."
          : takesInterfaces && interfaceNames.some((name) => !INTERFACE_NAME.test(name))
            ? "An interface's name is a letter, then letters, digits, '_' and '-' (one per line)."
            : takesInterfaces && new Set(interfaceNames).size !== interfaceNames.length
              ? "The same interface is named twice."
              : takesTypes && typeNames.length > 0 && [...pickedTypes].some((name) => !typeNames.includes(name))
                ? `The flow declares no type named ${[...pickedTypes].filter((name) => !typeNames.includes(name)).join(", ")} here; clear it to run.`
                : null);

    const payload: Record<string, unknown> = { ...carried };
    if (takesForce && force) {
      payload.force = true;
    }

    if (takesSubmission && trimmedSubmission !== "") {
      payload.submissionId = trimmedSubmission;
    }

    if (takesRecordScope && recordKeys.length > 0) {
      payload.recordKeys = recordKeys;
    }

    if (takesRedeliver && sendAgain === "uptodate") {
      payload.rerender = true;
    } else if (takesRedeliver && sendAgain !== "changed") {
      payload.redeliver = sendAgain;
    }

    if (takesInterfaces && interfaceNames.length > 0) {
      payload.interfaces = interfaceNames;
    }

    if (takesTypes && pickedTypes.size > 0) {
      payload.types = [...pickedTypes].sort((a, b) => a.localeCompare(b));
    }

    const values: Record<string, RunValue> = {};
    if (partition !== null) {
      values[PARTITION] = partition;
    }

    return {
      values: Object.keys(values).length > 0 ? values : undefined,
      payload: Object.keys(payload).length > 0 ? payload : undefined,
      error,
    };
  }, [
    carried, carriesLedgerWork, force, interfaceNames, movedFrom, partition, partitionError, recordKeys, sendAgain, submissionId, takesForce,
    takesInterfaces, takesRecordScope, takesRedeliver, takesSubmission, takesTypes, typeNames, pickedTypes,
  ]);

  const toggleType = (name: string) => setPickedTypes((was) => {
    const next = new Set(was);
    if (!next.delete(name)) {
      next.add(name);
    }

    return next;
  });

  useEffect(() => {
    onChange(body);
  }, [body, onChange]);

  return (
    <div className="flex flex-col gap-3" data-testid="trigger-kind-fields">
      {(flowKind === "delivery" || flowKind === "cache") && (
        <div className="flex flex-col gap-1" data-testid="trigger-partition">
          <span className="text-[13px] font-medium">Partition</span>
          <span className="font-mono text-[13px]" data-testid="trigger-partition-value">
            {takesPartition ? partition ?? active ?? "none picked" : headerPartition ?? "the one its data-partition-id header names"}
          </span>
          <p className="text-xs text-muted-foreground">
            {takesPartition
              ? deliveryKind
                ? "The partition picked in the title bar: this run's ids, cache, ledger and configuration are its. Switch it there to run in another."
                : "The partition picked in the title bar: this refresh builds its cache. Switch it there to refresh another."
              : deliveryKind
                ? "This flow delivers to the partition its data-partition-id header names."
                : "This flow fills the cache of the partition its data-partition-id header names."}
            {movedFrom !== null && !carriesLedgerWork && ` The run being repeated ran in ${movedFrom}; this one runs in ${partition}.`}
          </p>
        </div>
      )}
      {takesForce && (
        <div className="flex flex-col gap-1">
          <Label className="flex items-center gap-2 text-[13px] font-normal">
            <Switch checked={force} onCheckedChange={setForce} data-testid="trigger-force" />
            Force
          </Label>
          <p className="pl-10 text-xs text-muted-foreground">
            {deliveryKind && effectiveOperation === "undo"
              ? UNDO_FORCE_HINT
              : FORCE_HINTS[flowKind] ?? "Run past the change gates that would otherwise skip work."}
          </p>
        </div>
      )}
      {takesRedeliver && (
        <div className="flex flex-col gap-1.5">
          <Label htmlFor={`${idPrefix}-redeliver`}>Send again</Label>
          <Select value={sendAgain} onValueChange={(value) => { if (value === "changed" || value === "uptodate" || isRedeliverScope(value)) { setRedeliver(value); } }}>
            <SelectTrigger id={`${idPrefix}-redeliver`} size="sm" className="h-8 w-full" data-testid="trigger-redeliver">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {sendAgainOptions.map((scope) => (
                <SelectItem key={scope.value} value={scope.value}>{scope.label}</SelectItem>
              ))}
            </SelectContent>
          </Select>
          <p className="text-xs text-muted-foreground" data-testid="trigger-redeliver-hint">
            {sendAgain === "uptodate"
              ? recordKeys.length > 0
                ? "The records named below are rendered again with the mapping, template, cache and engine of now, and sent only where they come out different. The rest stay as OSDU holds them."
                : `Every record this flow has delivered${partition !== null ? ` in ${partition}` : ""} is rendered again with the mapping, template, cache and engine of now, and only what comes out different is sent; the rest stay as OSDU holds them. The run renders them all, ${PLANNED_PER_PASS.toLocaleString()} to a pass. A record's Redeliver, or the Records tab's, says beforehand what would be sent.`
              : recordKeys.length > 0
                ? "What of the records named below is sent again, changed or not: the record and its payload files, or one of them."
                : sendAgain === "changed"
                  ? "Only records whose rendering or payload changed are sent."
                  : `Every record this flow has delivered${partition !== null ? ` in ${partition}` : ""} is sent again, changed or not: a new version of each in OSDU. The run plans them all, ${PLANNED_PER_PASS.toLocaleString()} to a pass.`}
          </p>
        </div>
      )}
      {takesSubmission && (
        <div className="flex flex-col gap-1.5">
          <Label htmlFor={`${idPrefix}-submission`}>
            {effectiveOperation === "drain" ? "Submission to drain" : effectiveOperation === "intake" ? "Submission" : "Re-run submission"}
          </Label>
          <Input
            id={`${idPrefix}-submission`}
            className="h-8 font-mono"
            value={submissionId}
            onChange={(event) => setSubmissionId(event.target.value)}
            data-testid="trigger-submission"
          />
          <p className="text-xs text-muted-foreground">
            {effectiveOperation === "drain"
              ? "Deliver the pending batches of this submission; empty drains every pending record of the flow."
              : "Work on one submission, with the parameters it was received with."}
          </p>
        </div>
      )}
      {takesRecordScope && (
        <div className="flex flex-col gap-1.5">
          <Label htmlFor={`${idPrefix}-records`}>
            {effectiveOperation === "verify" ? "Verify only these records" : effectiveOperation === "sync" ? "Sync only these records" : "Redeliver these records"}
          </Label>
          <Textarea
            id={`${idPrefix}-records`}
            className="min-h-16 font-mono text-[12px]"
            placeholder="one delivery key per line"
            value={recordKeysText}
            onChange={(event) => setRecordKeysText(event.target.value)}
            data-testid="trigger-record-keys"
          />
          <p className="text-xs text-muted-foreground">
            {effectiveOperation === "verify"
              ? "Delivery keys to check; empty verifies the flow's delivered records."
              : effectiveOperation === "sync"
                ? "Delivery keys whose rows to read from the ingestion tables (at most 1,000); empty syncs every record."
                : sendAgain === "uptodate"
                  ? "Delivery keys to bring up to date; empty brings every record the flow has delivered up to date."
                  : "Delivery keys to send again, changed or not; empty leaves it to Send again, over every record the flow has delivered."}
          </p>
        </div>
      )}
      {takesTypes && typeNames.length > 0 && (
        <div className="flex flex-col gap-2" data-testid="trigger-cache-types">
          <div className="flex flex-wrap items-baseline justify-between gap-2">
            <span className="text-[13px] font-medium">Types</span>
            <span className="text-xs text-muted-foreground" data-testid="trigger-cache-types-summary">
              {pickedTypes.size === 0
                ? `Every type (${typeNames.length}).`
                : `${counted(pickedTypes.size, "type")} of ${typeNames.length}.`}
            </span>
          </div>
          <div className="max-h-48 overflow-y-auto rounded-md border" data-testid="trigger-cache-types-list">
            {typeNames.map((name) => {
              const id = `${idPrefix}-type-${name}`;
              return (
                <label key={name} htmlFor={id} className="flex cursor-pointer items-center gap-2.5 border-b px-2.5 py-1.5 last:border-b-0 hover:bg-muted/50">
                  <Checkbox id={id} checked={pickedTypes.has(name)} onCheckedChange={() => toggleType(name)} data-testid={`trigger-cache-type-${name}`} />
                  <span className="min-w-0 flex-1 truncate font-mono text-[12px]">{name}</span>
                </label>
              );
            })}
          </div>
          <p className="text-xs text-muted-foreground">
            Pick types to {effectiveOperation === "plan" ? "count" : "refresh"} only those; the others keep what the cache holds of them.
          </p>
        </div>
      )}
      {takesInterfaces && (
        <div className="flex flex-col gap-1.5">
          <Label htmlFor={`${idPrefix}-interfaces`}>Interfaces</Label>
          <Textarea
            id={`${idPrefix}-interfaces`}
            className="min-h-16 font-mono text-[12px]"
            placeholder={"wellbores\nwelllogs"}
            value={interfacesText}
            onChange={(event) => setInterfacesText(event.target.value)}
            data-testid="trigger-interfaces"
          />
          <p className="text-xs text-muted-foreground">
            Which interfaces of the source this run takes, one per line; empty runs them all. The ones left out are not
            run, and the records they deliver are read from the ledger as they stand.
          </p>
        </div>
      )}
    </div>
  );
}
