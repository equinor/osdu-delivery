import { useState } from "react";
import { Link as RouterLink } from "react-router-dom";
import { useMutation, useQuery } from "@tanstack/react-query";
import { ChevronDown, ChevronRight, Info, Loader2, RefreshCcw, Send } from "lucide-react";
import { toast } from "sonner";
import {
  AlertDialog, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { isApiError } from "@/api/client";
import { runApi } from "@/api/endpoints";
import type { RunDetail } from "@/api/types";
import { CorrelationError } from "@/components/CorrelationError";
import { RichTooltip } from "@/components/RichTooltip";
import {
  deliveryApi, deliveryRecordRoute, type DeliveryFlowScope, type DeliveryManyRecords, type DeliveryRecordFilter,
} from "../../api/delivery";
import { shortId } from "./idTail";
import { planPreview, type PlanPreview } from "./runOutcome";

/**
 * Which records a redelivery acts on: the ones an operator ticked, the listing they were looking at with the count it
 * showed (what "select all 3,481 matching" means, refused if the set changed since), or every record the flow delivered.
 */
export type RedeliverSelection =
  | { kind: "keys"; keys: string[] }
  | { kind: "filter"; filter: DeliveryRecordFilter; expected: number }
  | { kind: "all"; delivered: number };

type Mode = "uptodate" | "again";

/** What a part of a record is called where an operator chooses what to send again; the older names are not offered beside the newer. */
const PART_LABELS: Record<string, { title: string; effect: string }> = {
  all: { title: "Everything", effect: "The record and whatever payload its route sends with it." },
  record: { title: "The record only", effect: "The document. Files and bulk data stay as OSDU holds them." },
  files: { title: "Files only", effect: "Uploaded and registered again; the record is rewritten with the new dataset ids." },
  bulk: { title: "Bulk data only", effect: "Written again as a new version of the record's bulk data." },
  workflow: { title: "The workflow run only", effect: "Triggered again, without sending anything else again." },
  payload: { title: "Every part of the payload", effect: "Each part the route sends beside the record." },
};

/** The parts offered: the route's own, without the older names (metadata for the record, payload where one part is all of it). */
function offeredParts(parts: readonly string[]): string[] {
  const own = parts.filter((part) => part !== "metadata" && part !== "payload");
  const payloadParts = own.filter((part) => part !== "all" && part !== "record");
  return payloadParts.length > 1 && parts.includes("payload") ? [...own, "payload"] : own;
}

function requestFor(selection: RedeliverSelection, run: boolean): DeliveryManyRecords {
  switch (selection.kind) {
    case "keys":
      return { keys: selection.keys, run };
    case "filter":
      return { filter: selection.filter, expected: selection.expected, run };
    case "all":
      return { all: true, run };
  }
}

function selectionLine(selection: RedeliverSelection, flowName: string, singleLabel: string | undefined): string {
  switch (selection.kind) {
    case "keys":
      return selection.keys.length === 1
        ? `One record${singleLabel ? ` (${singleLabel})` : ""} of ${flowName}.`
        : `${selection.keys.length.toLocaleString()} records of ${flowName}.`;
    case "filter":
      return `Every record of ${flowName} the current filter matches, ${selection.expected.toLocaleString()} now, resolved when you confirm.`;
    case "all":
      return `Every record ${flowName} has delivered, ${selection.delivered.toLocaleString()} now.`;
  }
}

const TERMINAL = new Set(["succeeded", "failed", "cancelled", "skipped"]);

const plural = (count: number, one: string, many = `${one}s`) => `${count.toLocaleString()} ${count === 1 ? one : many}`;

/** How many records a selection names, as far as the page knows before the API resolves it. */
function selectionCount(selection: RedeliverSelection): number {
  switch (selection.kind) {
    case "keys":
      return selection.keys.length;
    case "filter":
      return selection.expected;
    case "all":
      return selection.delivered;
  }
}

/** How many records a deliver run reads by key in one pass (DeliveryExecutor.RequestedPerPass). */
const PER_PASS = 5000;

/**
 * The one redelivery surface, for a record, a selection, a filtered listing or a whole flow. Its two ways are side by side
 * because they make different promises. Bringing records up to date renders them again under the rules of now and sends
 * only what comes out different, which is what a change to a mapping, a cache or the engine asks for; the dialog first asks
 * a plan run what it would send and shows the answer. Sending again sends the chosen part of every record whatever its
 * hashes say, which OSDU keeps as a new version of each: for a record OSDU lost or someone changed there.
 */
export function RedeliverDialog({ open, onClose, pipelineId, flowId, flowScope, flowName, selection, singleLabel, startMode = "uptodate", onDone }: {
  open: boolean;
  onClose: () => void;
  pipelineId: string;
  /** The ledger the records are in, for links to the records the preview names. */
  flowId: string | null;
  flowScope?: DeliveryFlowScope;
  flowName: string;
  selection: RedeliverSelection;
  /** What the record is called in the dialog when there is exactly one. */
  singleLabel?: string;
  /**
   * The way the dialog opens on: bringing up to date by default, sending again for a record that is not delivered, which only
   * a record OSDU holds as delivered can be brought up to date from.
   */
  startMode?: Mode;
  onDone: (outcome: { marked: number; runId: string | null; mode: Mode }) => void;
}) {
  const [mode, setMode] = useState<Mode>(startMode);
  const [part, setPart] = useState("record");
  const [run, setRun] = useState(true);
  const [wasOpen, setWasOpen] = useState(open);

  // Every opening starts from the way it was given (bringing records up to date, the way that sends least), with a run queued.
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setMode(startMode);
      setPart("record");
      setRun(true);
    }
  }

  const selectionKey = JSON.stringify(requestFor(selection, true));
  const parts = useQuery({
    queryKey: ["delivery", "redeliver-parts", pipelineId, flowScope?.interfaceName ?? null, flowScope?.partition ?? null],
    queryFn: () => deliveryApi.stats(pipelineId, flowScope),
    enabled: open,
    staleTime: 60_000,
  });
  const offered = offeredParts(parts.data?.redeliverParts ?? ["all", "record"]);

  const preview = useQuery({
    queryKey: ["delivery", "rerender-preview", pipelineId, flowScope?.interfaceName ?? null, flowScope?.partition ?? null, selectionKey],
    queryFn: () => deliveryApi.previewRerender(pipelineId, requestFor(selection, true), flowScope),
    enabled: open,
    staleTime: Infinity,
    gcTime: 0,
    retry: false,
  });
  const runId = preview.data?.runId ?? null;
  const planRun = useQuery({
    queryKey: ["runs", runId],
    queryFn: () => runApi.getById(runId!),
    enabled: open && runId !== null,
    refetchInterval: (query) => (query.state.data !== undefined && TERMINAL.has(query.state.data.status) ? false : 1000),
  });

  const act = useMutation({
    mutationFn: async () => {
      const request = requestFor(selection, run);
      if (mode === "uptodate") {
        const result = await deliveryApi.rerenderFlow(pipelineId, request, flowScope);
        return { marked: result.marked, runId: result.runId ?? null, mode };
      }

      const result = await deliveryApi.redeliverFlow(pipelineId, { ...request, scope: part }, flowScope);
      return { marked: result.marked, runId: result.runId ?? null, mode };
    },
    onSuccess: (outcome) => {
      onDone(outcome);
      onClose();
    },
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });

  const busy = act.isPending;
  const chosenPart = offered.includes(part) ? part : offered[0] ?? "all";

  const count = selectionCount(selection);
  return (
    <AlertDialog
      open={open}
      onOpenChange={(next) => {
        if (!next && !busy) {
          onClose();
        }
      }}
    >
      <AlertDialogContent data-testid="redeliver-dialog" className="max-h-[calc(100dvh-2rem)] gap-3 overflow-y-auto sm:max-w-2xl">
        <AlertDialogHeader>
          <AlertDialogTitle className="flex items-center gap-2">
            <RefreshCcw className="size-4" />
            Redeliver
          </AlertDialogTitle>
          <AlertDialogDescription data-testid="redeliver-selection">{selectionLine(selection, flowName, singleLabel)}</AlertDialogDescription>
        </AlertDialogHeader>

        {/* The two ways are choices side by side; what the chosen one does in detail is laid out under them. */}
        <div role="radiogroup" aria-label="How to redeliver" className="grid grid-cols-1 gap-2 sm:grid-cols-2">
          <ModeChoice
            checked={mode === "uptodate"}
            disabled={busy}
            onSelect={() => setMode("uptodate")}
            title="Bring up to date"
            note="recommended"
            effect="Renders each record again with the mapping, template, cache and engine of now, and sends only what comes out different."
            testId="redeliver-mode-uptodate"
          />
          <ModeChoice
            checked={mode === "again"}
            disabled={busy}
            onSelect={() => setMode("again")}
            title="Send again"
            effect="Sends the part you choose of every record, changed or not. OSDU keeps a new version of each."
            testId="redeliver-mode-again"
          />
        </div>

        {mode === "uptodate"
          ? (
            <section className="rounded-md border border-border p-3" aria-label="What bringing them up to date would send">
              <PreviewPanel
                loading={preview.isPending}
                error={preview.error}
                accepted={preview.data}
                run={planRun.data}
                flowId={flowId}
              />
            </section>
          )
          : (
            <section className="flex flex-col gap-1.5 rounded-md border border-border p-3" aria-label="What to send again">
              <span className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">What to send again</span>
              <div className="flex flex-col gap-1.5" role="radiogroup" aria-label="What to send again">
                {parts.isPending && <Skeleton className="h-10 w-full rounded-md" />}
                {!parts.isPending && offered.map((name) => {
                  const label = PART_LABELS[name] ?? { title: name, effect: "" };
                  const active = name === chosenPart;
                  return (
                    <button
                      key={name}
                      type="button"
                      role="radio"
                      aria-checked={active}
                      disabled={busy}
                      onClick={() => setPart(name)}
                      className={cn(
                        "flex w-full items-start gap-2.5 rounded-md border px-2.5 py-1.5 text-left transition-colors",
                        active ? "border-primary bg-primary/5" : "border-border hover:bg-accent/50",
                      )}
                      data-testid={`redeliver-part-${name}`}
                    >
                      <RadioMark checked={active} />
                      <span className="flex flex-col">
                        <span className="text-[13px] font-medium">{label.title}</span>
                        {label.effect !== "" && <span className="text-[12px] text-muted-foreground">{label.effect}</span>}
                      </span>
                    </button>
                  );
                })}
              </div>
              <p className="text-[12px] text-muted-foreground" data-testid="redeliver-again-warning">
                {count === 1
                  ? "The record is written again whether it changed or not."
                  : `Every one of the ${count.toLocaleString()} records is written again whether it changed or not.`}
              </p>
            </section>
          )}

        <div className="flex flex-col gap-1">
          <Label className="flex items-center gap-2 text-[13px] font-normal">
            <Checkbox checked={run} onCheckedChange={(next) => setRun(next === true)} disabled={busy} data-testid="redeliver-run" />
            Queue a deliver run now
          </Label>
          <p className="pl-6 text-[12px] text-muted-foreground">
            {run
              ? `The run takes the records ${PER_PASS.toLocaleString()} to a pass${mode === "uptodate" ? ", renders each and sends only what changed" : ""}; its page shows how far it is.`
              : "Without a run, the flow's next run takes them, a scheduled one included."}
          </p>
        </div>

        <AlertDialogFooter>
          <Button variant="ghost" size="sm" onClick={onClose} disabled={busy} data-testid="redeliver-cancel">
            Cancel
          </Button>
          <Button
            size="sm"
            onClick={() => { setPart(chosenPart); act.mutate(); }}
            disabled={busy || (mode === "again" && parts.isPending)}
            data-testid="redeliver-confirm"
          >
            {busy ? <Loader2 className="animate-spin" /> : <Send />}
            {mode === "uptodate" ? "Bring up to date" : "Send again"}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}

/** The mark of a choice: a ring, filled while it is the one chosen. */
function RadioMark({ checked }: { checked: boolean }) {
  return (
    <span
      aria-hidden
      className={cn(
        "mt-0.5 flex size-4 shrink-0 items-center justify-center rounded-full border",
        checked ? "border-primary" : "border-muted-foreground/50",
      )}
    >
      {checked && <span className="size-2 rounded-full bg-primary" />}
    </span>
  );
}

function ModeChoice({ checked, disabled, onSelect, title, note, effect, testId }: {
  checked: boolean;
  disabled: boolean;
  onSelect: () => void;
  title: string;
  note?: string;
  effect: string;
  testId: string;
}) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={checked}
      disabled={disabled}
      onClick={onSelect}
      className={cn(
        "flex w-full items-start gap-2.5 rounded-md border p-2.5 text-left transition-colors",
        checked ? "border-primary bg-primary/5" : "border-border hover:bg-accent/50",
      )}
      data-testid={testId}
    >
      <RadioMark checked={checked} />
      <span className="flex flex-col gap-1">
        <span className="flex items-center gap-2">
          <span className="text-[13px] font-medium">{title}</span>
          {note !== undefined && <span className="text-[11px] text-muted-foreground">{note}</span>}
        </span>
        <span className="text-[12px] text-muted-foreground">{effect}</span>
      </span>
    </button>
  );
}

/** A share of what was checked, as a whole percentage; less than one but more than none reads as under 1%. */
function share(part: number, whole: number): string {
  if (whole <= 0 || part <= 0) {
    return "";
  }

  const percent = (part / whole) * 100;
  return percent < 1 ? "under 1%" : `${Math.round(percent)}%`;
}

/** What the plan's count of the records failing an assertion of the mapping counts, for its tooltip. */
const ASSERTIONS_HINT = [
  "Records that would be sent whose document fails an assertion the mapping states ($assert).",
  "They are counted among those that would be sent, as the plan renders them as a delivery would. An assertion whose",
  "failure holds keeps the record at the check before sending, its document kept, until its source or the mapping",
  "changes or a release accepts it; one that reports sends the record and records the failure, and one that omits",
  "sends it without the value.",
].join(" ");

/**
 * What the plan run found, laid out for a selection of any size: of the records it checked (all of a small selection, a
 * sample of a large one), how many would be sent and with which part, how many of those fail an assertion of the mapping
 * and would be held before they are sent, how many render the same, how many would be held, and, for a sample, what that
 * comes to for the whole selection. Records are named only as a few examples, on request.
 */
function PreviewPanel({ loading, error, accepted, run, flowId }: {
  loading: boolean;
  error: unknown;
  accepted: { runId: string; checked: number; selected: number; selectedCapped: boolean } | undefined;
  run: RunDetail | undefined;
  flowId: string | null;
}) {
  const [examples, setExamples] = useState(false);

  if (error !== null && error !== undefined) {
    return (
      <div className="w-full" data-testid="redeliver-preview-error">
        {isApiError(error) ? <CorrelationError error={error} /> : <p className="text-[13px] text-destructive">{String(error)}</p>}
      </div>
    );
  }

  if (loading || accepted === undefined) {
    return <Skeleton className="h-12 w-full rounded-md" />;
  }

  const runLink = (
    <RouterLink to={`/runs/${accepted.runId}`} className="font-mono text-primary underline-offset-2 hover:underline">
      {shortId(accepted.runId)}
    </RouterLink>
  );
  const sampled = accepted.checked < accepted.selected;
  const selected = `${accepted.selectedCapped ? "more than " : ""}${accepted.selected.toLocaleString()}`;
  const what = sampled
    ? `a sample of ${accepted.checked.toLocaleString()} of the ${selected} selected records`
    : plural(accepted.checked, "delivered record");

  if (run === undefined || !TERMINAL.has(run.status)) {
    return (
      <p className="flex items-center gap-2 text-[13px] text-muted-foreground" data-testid="redeliver-preview-running">
        <Loader2 className="size-3.5 shrink-0 animate-spin" />
        <span>Rendering {what} again to see which would change. Nothing is sent until you confirm (plan run {runLink}).</span>
      </p>
    );
  }

  const outcome: PlanPreview | null = run.status === "succeeded" ? planPreview(run) : null;
  if (outcome === null) {
    return (
      <p className="text-[13px] text-destructive" data-testid="redeliver-preview-failed">
        The check did not finish{run.error ? `: ${run.error}` : "."} See plan run {runLink}. Bringing the records up to date still works; it only cannot say beforehand what it sends.
      </p>
    );
  }

  const read = Math.max(outcome.records, 1);
  const held = outcome.held + outcome.blocked;
  const both = Math.max(0, outcome.metadata + outcome.payload - outcome.deliveries);
  // A row under another is a part of it (level 1), or a part of that part (level 2); only a whole row takes a share.
  const rows: { label: string; value: number; level?: 1 | 2; hint?: string; testId: string }[] = [
    { label: "Would be sent", value: outcome.deliveries, testId: "redeliver-preview-sends" },
    ...(outcome.deliveries > 0 && outcome.metadata - both > 0 ? [{ label: "with a new document", value: outcome.metadata - both, level: 1 as const, testId: "redeliver-preview-document" }] : []),
    ...(outcome.deliveries > 0 && outcome.payload - both > 0 ? [{ label: "with a new payload", value: outcome.payload - both, level: 1 as const, testId: "redeliver-preview-payload" }] : []),
    ...(both > 0 ? [{ label: "with a new document and payload", value: both, level: 1 as const, testId: "redeliver-preview-both" }] : []),
    ...(outcome.failingAssertions > 0
      ? [{ label: "failing an assertion of the mapping", value: outcome.failingAssertions, level: 1 as const, hint: ASSERTIONS_HINT, testId: "redeliver-preview-asserted" }]
      : []),
    ...(outcome.heldByAssertions > 0
      ? [{ label: "to be held before they are sent", value: outcome.heldByAssertions, level: 2 as const, testId: "redeliver-preview-asserted-held" }]
      : []),
    { label: "Render the same, left as they are", value: outcome.unchanged, testId: "redeliver-preview-unchanged" },
    ...(held > 0 ? [{ label: "Would be held", value: held, testId: "redeliver-preview-held" }] : []),
    ...(outcome.other > 0 ? [{ label: "Waiting for approval, or older than OSDU holds", value: outcome.other, testId: "redeliver-preview-other" }] : []),
  ];
  const estimate = sampled && outcome.records > 0 ? Math.round((outcome.deliveries / outcome.records) * accepted.selected) : null;

  return (
    <div className="flex w-full flex-col gap-2 text-[13px]" data-testid="redeliver-preview">
      <p className="text-muted-foreground">
        Rendered {what} again, sending nothing (plan run {runLink}):
      </p>
      <dl className="grid grid-cols-[1fr_auto_auto] items-baseline gap-x-4 gap-y-1">
        {rows.map((row) => (
          <div key={row.testId} className="contents" data-testid={row.testId}>
            <dt className={cn(row.level === undefined ? "font-medium" : "text-muted-foreground", row.level === 1 && "pl-4", row.level === 2 && "pl-8")}>
              {row.label}
              {row.hint !== undefined && (
                <RichTooltip title={row.label} body={row.hint}>
                  <Info className="ml-1 inline size-3.5 align-[-2px] text-muted-foreground" aria-label="What this counts" data-testid={`${row.testId}-hint`} />
                </RichTooltip>
              )}
            </dt>
            <dd className={cn("text-right tabular-nums", row.value === 0 && "text-muted-foreground")}>{row.value.toLocaleString()}</dd>
            <dd className="w-16 text-right tabular-nums text-muted-foreground">{row.level === undefined ? share(row.value, read) : ""}</dd>
          </div>
        ))}
      </dl>
      {estimate !== null && (
        <p data-testid="redeliver-preview-estimate">
          {outcome.deliveries === 0
            ? `None of the sample renders differently, so few if any of the ${selected} would be sent.`
            : `For all ${selected} selected, that is about ${estimate.toLocaleString()} to send.`}
          <span className="text-muted-foreground"> Bringing them up to date renders every one and decides each on its own.</span>
        </p>
      )}
      {!sampled && outcome.deliveries === 0 && (
        <p className="text-muted-foreground">None renders differently, so bringing them up to date would send nothing.</p>
      )}
      {outcome.sample.length > 0 && (
        <div className="flex flex-col gap-1">
          <button
            type="button"
            onClick={() => setExamples((shown) => !shown)}
            className="flex items-center gap-1 self-start text-[12px] text-muted-foreground hover:text-foreground"
            aria-expanded={examples}
            data-testid="redeliver-preview-examples-toggle"
          >
            {examples ? <ChevronDown className="size-3.5" /> : <ChevronRight className="size-3.5" />}
            {examples ? "Hide examples" : `Show ${Math.min(outcome.sample.length, 5)} of the records it would send`}
          </button>
          {examples && (
            <ul className="flex flex-col gap-0.5 pl-4 text-[12px]" data-testid="redeliver-preview-sample">
              {outcome.sample.slice(0, 5).map((entry) => (
                <li key={entry.key} className="flex min-w-0 items-baseline gap-2">
                  {flowId === null
                    ? <span className="min-w-0 truncate">{entry.label ?? entry.sourceKey}</span>
                    : (
                      <RouterLink to={deliveryRecordRoute({ flowId, deliveryKey: entry.key })} className="min-w-0 truncate underline-offset-2 hover:underline">
                        {entry.label ?? entry.sourceKey}
                      </RouterLink>
                    )}
                  <span className="shrink-0 text-muted-foreground">
                    {entry.metadata && entry.payload ? "document and payload" : entry.metadata ? "document" : "payload"}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  );
}
