import { useCallback, useEffect, useState } from "react";
import { Link as RouterLink } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowRight, Check, ShieldCheck, X } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { cn } from "@/lib/utils";
import { deliveryApi, type DeliveryUpdateTag } from "../../api/delivery";
import { CopyButton } from "@/components/CopyButton";
import { DetailPair } from "@/components/DetailPair";
import { EmptyState } from "@/components/EmptyState";
import { IconAction } from "@/components/IconAction";
import { PagedTable, type Column } from "@/components/PagedTable";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import { useOwnedPanel } from "@/layout/workbench/useOwnedPanel";
import { ChangeBadge } from "./ChangeMark";
import { DeliveryCacheGaps } from "./DeliveryCacheGaps";
import { compositeLookedUp } from "./findByLines";
import { RecordId } from "./DeliveryCacheRecords";

/** The bottom panel content ids this surface owns. */
const PANEL = "cache-tag:";

const ALL = "all";

/** Not a state of a change: the records built without a value the cache did not hold, which no refresh has changed yet. */
const MISSING = "missing";

const statuses: { value: string; label: string }[] = [
  { value: MISSING, label: "Missing from cache" },
  { value: "pending", label: "Waiting for approval" },
  { value: "approved", label: "Approved" },
  { value: "rolling", label: "Rolling out" },
  { value: "applied", label: "Rolled out" },
  { value: "rejected", label: "Rejected" },
  { value: ALL, label: "All changes" },
];

const statusLabel = { ...Object.fromEntries(statuses.map((s) => [s.value, s.label])), delivering: "Rolling out" } as Record<string, string>;

/** What each kind of change means for the records built from the cached value, as the detail panel says it. */
const changeMeaning: Record<string, string> = {
  changed: "A value delivered records were built from reads differently in the new version of the cache.",
  removed: "The cached record delivered records were built from is no longer in the cache.",
  unmatched: "The value delivered records found this cached record by no longer matches it.",
  listed: "The cache now holds a row under a value delivered records looked up and found none under (a key a lookup table did not list, or a record such as a wellbore loaded after them), so they were built without what it gives.",
  relisted: "The rows a key finds are no longer the ones delivered records read every one of ($findAll): a row was listed under the key, such as an access group a data office adds for a field, or one no longer is.",
  found: "The cache now holds a record delivered records reference as an unverified id: they were built without finding it, and are built again against it.",
};

/**
 * The one decision this surface makes, as the row buttons, the toolbar and the detail panel all make it: approving hands
 * the change to the batched rollout, rejecting leaves OSDU with what it holds. Every cache query refreshes afterwards,
 * since the change leaves the pending list and the counts move with it.
 */
function useDecideTags(onDecided?: () => void) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ ids, approve }: { ids: number[]; approve: boolean }) => deliveryApi.decideTags(ids, approve),
    onSuccess: (result) => {
      toast.success(result.approved
        ? `${result.decided} change${result.decided === 1 ? "" : "s"} approved; the rollout carries the records in batches.`
        : `${result.decided} change${result.decided === 1 ? "" : "s"} rejected; the delivered records stay as they are.`);
      void queryClient.invalidateQueries({ queryKey: ["delivery", "cache"] });
      onDecided?.();
    },
    onError: (error) => toast.error(`The decision could not be recorded: ${error instanceof Error ? error.message : String(error)}`),
  });
}

/**
 * What a change moved away from, as its row shows it: for values a findBy line of several comparisons looked up together and
 * a row now meets, each field with its value (`Well = A-12 and Mnemonic = GR`); otherwise the value as kept.
 */
function lookedUp(tag: DeliveryUpdateTag): string {
  return (tag.change === "listed" ? compositeLookedUp(tag.path, tag.oldValue) : null) ?? tag.oldValue ?? "";
}

/**
 * The value before and after, as one line: what OSDU holds now struck through, what the cache holds now beside it. For a
 * key a lookup table now lists, the key the records looked up, and what its row gives them.
 */
function ValueChange({ row, maxWidth }: { row: DeliveryUpdateTag; maxWidth: number }) {
  // A newly listed key and a newly found record did not replace a value: what they had is kept, not struck through.
  const listed = row.change === "listed" || row.change === "found";
  return (
    <span className="inline-flex items-center gap-1.5 font-mono text-[12px]">
      {row.oldValue === null
        ? <span className="font-sans text-muted-foreground">no value</span>
        : <TruncatedText text={lookedUp(row)} mono maxWidth={maxWidth} className={cn("text-muted-foreground", !listed && "line-through")} />}
      <ArrowRight className="size-3.5 shrink-0 text-muted-foreground" />
      <NewValue tag={row} maxWidth={maxWidth} />
    </span>
  );
}

/** What the cache holds now: a removed record or a lost match is gone; a newly listed row may give no value. */
function NewValue({ tag, maxWidth }: { tag: DeliveryUpdateTag; maxWidth?: number }) {
  if (tag.newValue !== null) {
    return maxWidth === undefined
      ? <span className="break-all font-mono text-[12px]">{tag.newValue}</span>
      : <TruncatedText text={tag.newValue} mono maxWidth={maxWidth} />;
  }

  return tag.change === "listed"
    ? <span className="font-sans text-[12px] text-muted-foreground">no value</span>
    : <span className="font-mono text-[12px] text-destructive">gone</span>;
}

/** How far the rollout has carried the change: the records rendered again by their flow, and the flows still to run. */
function Rollout({ row }: { row: DeliveryUpdateTag }) {
  if (row.status === "pending") {
    return <span className="text-[12px] text-warning">waiting for a decision</span>;
  }

  if (row.status === "rejected") {
    return <span className="text-[12px] text-muted-foreground">not sent</span>;
  }

  const delivered = Math.max(0, row.affectedRecords - row.waiting);
  const done = row.affectedRecords === 0 ? 1 : delivered / row.affectedRecords;
  const flows = row.waitingFlows.length;
  return (
    <span
      className="inline-flex items-center gap-2"
      title={flows === 0 ? undefined : `Waiting for ${row.waitingFlows.map((f) => `${f.flowName ?? f.flowId} (${f.records.toLocaleString()})`).join(", ")}`}
      data-testid="delivery-cache-tag-rollout"
    >
      <span className="h-1.5 w-20 overflow-hidden rounded-full bg-muted">
        <span
          className={cn("block h-full", row.status === "applied" ? "bg-success" : "bg-primary")}
          style={{ width: `${Math.round(done * 100)}%` }}
        />
      </span>
      <span className="font-mono text-[11px] tabular-nums text-muted-foreground">
        {delivered.toLocaleString()} / {row.affectedRecords.toLocaleString()}
      </span>
      {flows > 0 && (
        <span className="text-[11px] text-muted-foreground">
          {flows} flow{flows === 1 ? "" : "s"} to run
        </span>
      )}
    </span>
  );
}

/** The flows a change still waits for, each with its records and a link to the pipeline that runs it. */
function WaitingFlows({ tag }: { tag: DeliveryUpdateTag }) {
  if (tag.waitingFlows.length === 0) {
    return <span className="text-[12px] text-muted-foreground">{tag.status === "applied" ? "none: every flow has run" : "none"}</span>;
  }

  return (
    <ul className="flex flex-col gap-0.5 text-[12px]" data-testid="delivery-cache-tag-waiting-flows">
      {tag.waitingFlows.map((flow) => (
        <li key={flow.flowId} className="flex items-baseline gap-2">
          {flow.pipelineId === null
            ? <span className="font-mono text-muted-foreground">{flow.flowId}</span>
            : <RouterLink to={`/pipelines/${flow.pipelineId}`} className="text-primary hover:underline">{flow.flowName}</RouterLink>}
          <span className="font-mono tabular-nums text-muted-foreground">{flow.records.toLocaleString()}</span>
        </li>
      ))}
    </ul>
  );
}

/** Approve and reject for one waiting change, right in its row, so a decision never needs the row opened first. */
function RowDecision({ tag }: { tag: DeliveryUpdateTag }) {
  const decide = useDecideTags();
  if (tag.status !== "pending") {
    return null;
  }

  return (
    <span className="inline-flex items-center gap-0.5">
      <IconAction
        label="Approve: the next runs carry the update"
        icon={<Check />}
        className="text-success hover:text-success"
        disabled={decide.isPending}
        onClick={(event) => {
          event.stopPropagation();
          decide.mutate({ ids: [tag.tagId], approve: true });
        }}
        data-testid="delivery-cache-row-approve"
      />
      <IconAction
        label="Reject: the delivered records stay as they are"
        icon={<X />}
        className="text-destructive hover:text-destructive"
        disabled={decide.isPending}
        onClick={(event) => {
          event.stopPropagation();
          decide.mutate({ ids: [tag.tagId], approve: false });
        }}
        data-testid="delivery-cache-row-reject"
      />
    </span>
  );
}

const columns: Column<DeliveryUpdateTag>[] = [
  {
    id: "what",
    header: "Cached value",
    render: (row) => (
      <span className="flex flex-col gap-0.5">
        <span className="inline-flex items-center gap-1.5 font-mono text-[12px]">
          <ChangeBadge change={row.change} className="font-sans" />
          {row.typeName}<span className="-mx-1 text-muted-foreground">.</span>{row.path}
        </span>
        <RecordId id={row.itemId} maxWidth={300} />
      </span>
    ),
  },
  { id: "values", header: "Was / is now", fill: true, floor: 180, render: (row) => <ValueChange row={row} maxWidth={180} /> },
  {
    id: "records",
    header: "Delivered records",
    align: "right",
    render: (row) => <span className="font-mono tabular-nums">{row.affectedRecords.toLocaleString()}</span>,
  },
  { id: "progress", header: "Rollout", render: (row) => <Rollout row={row} /> },
  { id: "detected", header: "Found", render: (row) => <RelativeTime value={row.detectedUtc} absolute={false} /> },
  { id: "decide", header: "", align: "right", render: (row) => <RowDecision tag={row} /> },
];

/**
 * One change in full, in the bottom panel: both values unclipped, what it reaches, how the rollout stands, who
 * decided it, and the decision itself while it is still open.
 */
function TagDetail({ tag, onDecided }: { tag: DeliveryUpdateTag; onDecided: () => void }) {
  const decide = useDecideTags(onDecided);
  const pending = tag.status === "pending";

  return (
    <div className="flex flex-col gap-4 p-3" data-testid="delivery-cache-tag-detail">
      <div className="flex flex-wrap items-center gap-2">
        <ChangeBadge change={tag.change} />
        <span className="font-mono text-[13px] font-medium">
          {tag.typeName}<span className="text-muted-foreground">.</span>{tag.path}
        </span>
        <span className="inline-flex items-center gap-0.5">
          <RecordId id={tag.itemId} maxWidth={420} />
          <CopyButton iconOnly label="Copy the cached record id" text={tag.itemId} testId="delivery-cache-tag-copy-id" />
        </span>
        <div className="grow" />
        {pending
          ? (
            <>
              <Button
                size="sm"
                disabled={decide.isPending}
                onClick={() => decide.mutate({ ids: [tag.tagId], approve: true })}
                data-testid="delivery-cache-tag-approve"
              >
                <Check /> Approve
              </Button>
              <Button
                size="sm"
                variant="outline"
                disabled={decide.isPending}
                onClick={() => decide.mutate({ ids: [tag.tagId], approve: false })}
                data-testid="delivery-cache-tag-reject"
              >
                <X /> Reject
              </Button>
            </>
          )
          : <span className="text-[12px] text-muted-foreground">{statusLabel[tag.status] ?? tag.status}</span>}
      </div>

      {changeMeaning[tag.change] !== undefined && (
        <p className="text-[12.5px] text-muted-foreground" data-testid="delivery-cache-tag-meaning">{changeMeaning[tag.change]}</p>
      )}

      <div className="grid gap-4 [grid-template-columns:repeat(auto-fill,minmax(200px,1fr))]">
        <DetailPair label={tag.change === "listed" ? "Looked up" : "Was"}>
          {tag.oldValue === null
            ? <span className="text-[12px] text-muted-foreground">no value</span>
            : (
              <span className={cn("break-all font-mono text-[12px] text-muted-foreground", tag.change !== "listed" && "line-through")}>
                {lookedUp(tag)}
              </span>
            )}
        </DetailPair>
        <DetailPair label={tag.change === "listed" ? "Now gives" : "Is now"}>
          <NewValue tag={tag} />
        </DetailPair>
        <DetailPair label="Versions">
          <span className="inline-flex items-center gap-1.5 font-mono text-[12px]">
            <span className="text-muted-foreground">{tag.fromVersion ?? "-"}</span>
            <ArrowRight className="size-3.5 shrink-0 text-muted-foreground" />
            <span>{tag.toVersion}</span>
          </span>
        </DetailPair>
        <DetailPair label="Delivered records reached">
          <span className="font-mono tabular-nums">{tag.affectedRecords.toLocaleString()}</span>
          <span className="text-[12px] text-muted-foreground">
            {pending
              ? " held back until decided"
              : tag.status === "rejected"
              ? " · left as they are"
              : ` · ${Math.max(0, tag.affectedRecords - tag.waiting).toLocaleString()} rendered again, ${tag.waiting.toLocaleString()} waiting`}
          </span>
        </DetailPair>
        <DetailPair label="Waiting for">
          <WaitingFlows tag={tag} />
        </DetailPair>
        <DetailPair label="Rollout"><Rollout row={tag} /></DetailPair>
        <DetailPair label="Mode">{tag.mode === "auto" ? "automatic: approved as found" : "needs approval"}</DetailPair>
        <DetailPair label="Found"><RelativeTime value={tag.detectedUtc} /></DetailPair>
        <DetailPair label="Decided">
          {tag.decidedUtc === null
            ? <span className="text-muted-foreground">not yet</span>
            : (
              <span className="inline-flex flex-wrap items-center gap-1.5">
                <RelativeTime value={tag.decidedUtc} />
                {tag.decidedBy !== null && <span className="text-[12px] text-muted-foreground">by {tag.decidedBy}</span>}
              </span>
            )}
        </DetailPair>
        {tag.startedUtc !== null && (
          <DetailPair label="Rollout started"><RelativeTime value={tag.startedUtc} /></DetailPair>
        )}
        {tag.completedUtc !== null && (
          <DetailPair label="Rollout completed"><RelativeTime value={tag.completedUtc} /></DetailPair>
        )}
      </div>

      {tag.summary !== "" && <p className="text-[12px] text-muted-foreground">{tag.summary}</p>}
    </div>
  );
}

/**
 * What the cache does to the delivered records of one partition, in one list: the records built without a value the cache
 * did not hold (Missing from cache), and the changes refreshes found in values delivered records were built from, with
 * what became of each. By default a change goes out on the next run on its own and shows here as a rollout, which lasts
 * until every flow reading the value has rendered its records again; a type whose
 * cache flow asks for approval holds its changes here until someone decides, with Approve and Reject in the row, in bulk
 * from a selection, and in the panel a row opens. The list opens on the changes waiting for a decision when there can be
 * any, else on what is missing when anything is, and on every change otherwise.
 */
export function DeliveryCacheApprovals({ scope, approvalTypes, pendingTotal, openMissing = false }: {
  /** The partition whose cache the changes were found in. */
  scope: string;
  /** The types of the cache whose changes wait for approval; empty when every change goes out on its own. */
  approvalTypes: string[];
  /** How many of the cache's changes wait for a decision; undefined while it loads. */
  pendingTotal: number | undefined;
  /** Whether a link opened the list on what is missing from the cache. */
  openMissing?: boolean;
}) {
  const [chosen, setChosen] = useState<string | null>(openMissing ? MISSING : null);
  // The count alone: a one-row page carries the total of what is missing.
  const gaps = useQuery({
    queryKey: ["delivery", "cache", "gaps", "count", scope],
    queryFn: () => deliveryApi.cacheGaps({ scope, page: 1, pageSize: 1 }),
  });
  const missingTotal = gaps.data?.total ?? 0;
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [openTag, setOpenTag] = useState<DeliveryUpdateTag | null>(null);
  // The affected counts of every row seen so far, so a selection that spans pages still adds up.
  const [affected, setAffected] = useState<ReadonlyMap<string, number>>(new Map());
  const { ownedId, show, close } = useOwnedPanel(PANEL);

  const approval = approvalTypes.length > 0 || (pendingTotal ?? 0) > 0;
  const status = chosen ?? (approval ? "pending" : missingTotal > 0 ? MISSING : ALL);
  const pending = status === "pending";

  const raise = useCallback((tag: DeliveryUpdateTag) => show(
    String(tag.tagId),
    `Change · ${tag.typeName}.${tag.path}`,
    <TagDetail tag={tag} onDecided={close} />,
  ), [show, close]);

  const onPageLoaded = useCallback((rows: DeliveryUpdateTag[]) => {
    setAffected((known) => {
      const next = new Map(known);
      for (const row of rows) {
        next.set(String(row.tagId), row.affectedRecords);
      }

      return next;
    });

    // A page that arrives with the open change in it carries its current state (a rollout that moved on).
    setOpenTag((current) => {
      const fresh = current === null ? undefined : rows.find((row) => row.tagId === current.tagId);
      return fresh ?? current;
    });
  }, []);

  const open = ownedId !== null;
  useEffect(() => {
    if (open && openTag !== null) {
      raise(openTag);
    }
  }, [open, openTag, raise]);

  const decide = useDecideTags(() => setSelected(new Set()));
  const decideSelected = (approve: boolean) => decide.mutate({ ids: [...selected].map(Number), approve });
  const reach = [...selected].reduce((sum, id) => sum + (affected.get(id) ?? 0), 0);
  const highlighted = open && openTag !== null ? String(openTag.tagId) : null;

  const toolbar = pending && selected.size > 0
    ? (
      <div className="flex flex-wrap items-center gap-2 border-b border-border bg-muted/40 px-3 py-1.5 text-[12px]" data-testid="delivery-cache-selection">
        <span className="font-medium">{selected.size} selected</span>
        <span className="text-muted-foreground">
          {reach.toLocaleString()} delivered record{reach === 1 ? "" : "s"} would be redelivered
        </span>
        <div className="grow" />
        <Button size="xs" disabled={decide.isPending} onClick={() => decideSelected(true)} data-testid="delivery-cache-approve">
          <Check /> Approve {selected.size}
        </Button>
        <Button size="xs" variant="outline" disabled={decide.isPending} onClick={() => decideSelected(false)} data-testid="delivery-cache-reject">
          <X /> Reject
        </Button>
        <Button size="xs" variant="ghost" disabled={decide.isPending} onClick={() => setSelected(new Set())}>
          Clear
        </Button>
      </div>
    )
    : undefined;

  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <ToggleGroup
          type="single"
          variant="outline"
          size="sm"
          value={status}
          onValueChange={(value) => {
            if (value !== "") {
              setChosen(value);
              setSelected(new Set());
            }
          }}
          aria-label="Change state"
          data-testid="delivery-cache-tag-status"
        >
          {statuses.map((option) => (
            <ToggleGroupItem
              key={option.value}
              value={option.value}
              className="h-8 gap-1.5 px-2.5 text-[13px]"
              data-testid={`delivery-cache-tag-status-${option.value}`}
            >
              {option.label}
              {option.value === "pending" && (pendingTotal ?? 0) > 0 && (
                <span className="rounded-full bg-warning/15 px-1.5 font-mono text-[11px] tabular-nums text-warning">{pendingTotal}</span>
              )}
              {option.value === MISSING && missingTotal > 0 && (
                <span className="rounded-full bg-muted px-1.5 font-mono text-[11px] tabular-nums text-muted-foreground">{missingTotal}</span>
              )}
            </ToggleGroupItem>
          ))}
        </ToggleGroup>
        <span className="text-[12px] text-muted-foreground" data-testid="delivery-cache-approval-rule">
          {status === MISSING
            ? "Records built without a value the cache did not hold. The refresh that brings it lists them as a change, and they are updated on their next delivery."
            : status === "rolling"
            ? "Each flow reading a change renders its records again on its next run; a change stays here until the last of them has."
            : status === "applied"
            ? "Every flow that read these values has rendered its records again with the new ones."
            : approvalTypes.length === 0
            ? "Every type updates automatically: a change reaches its records on their next delivery."
            : `Approval is on for ${approvalTypes.join(", ")}; every other type updates automatically on the next delivery.`}
        </span>
      </div>

      {status === MISSING
        ? <DeliveryCacheGaps scope={scope} type={null} />
        : pending && pendingTotal === 0
        ? (
          <Card className="gap-0 rounded-lg p-0">
            <EmptyState
              icon={<ShieldCheck />}
              title="Nothing needs approval"
              description={approvalTypes.length === 0
                ? "No type of this cache asks for approval, so a changed value reaches the delivered records on the next run without waiting here. To look at a type's changes first, set onChange: approve on it in a cache flow file of the partition."
                : "A change waits here only when a record already delivered was built from a value that moved in a type that asks for approval."}
              data-testid="delivery-cache-approvals-empty"
            />
          </Card>
        )
        : (
          <PagedTable
            queryKey={["delivery", "cache", "tags", scope, status]}
            fetchPage={(page, pageSize) => deliveryApi.updateTags({ page, pageSize, scope, status: status === ALL ? undefined : status })}
            columns={columns}
            rowKey={(row) => String(row.tagId)}
            onRowClick={(row) => {
              setOpenTag(row);
              raise(row);
            }}
            rowSx={(row) => (String(row.tagId) === highlighted ? { backgroundColor: "var(--accent)" } : undefined)}
            selection={pending ? { selected, onChange: setSelected } : undefined}
            toolbar={toolbar}
            onPageLoaded={onPageLoaded}
            emptyMessage={status === ALL
              ? "No refresh of this cache has changed a value a delivered record was built from."
              : "No changes in this state."}
            data-testid="delivery-cache-tags-table"
          />
        )}
    </div>
  );
}
