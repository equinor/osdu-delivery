import type { ReactNode } from "react";
import { Link as RouterLink } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { CircleAlert, CirclePlay, FileDiff, Inbox, PackageCheck, ShieldAlert, X, type LucideIcon } from "lucide-react";
import { runApi } from "@/api/endpoints";
import { RelativeTime } from "@/components/RelativeTime";
import { RunStatusBadge } from "@/components/StatusBadge";
import { runScope } from "./runOutcome";
import { shortId } from "./idTail";
import { triggerLabel } from "./deliveryReason";

/** The address parameters a link narrows a flow's Records tab by, in the order the strip says them. */
const NARROWING_KEYS = ["run", "submission", "delivered", "trigger", "issue", "missing"] as const;

export type NarrowingKey = (typeof NARROWING_KEYS)[number];

/** Where a run ran, when it names it: the interface of a source, the partition of a flow that names its partitions. */
export interface LedgerScope {
  interfaceName: string | null;
  partition: string | null;
}

interface RecordsNarrowingProps {
  /** The narrowings the address names, each by its id. */
  narrowed: Partial<Record<NarrowingKey, string>>;
  /** The ledger in view: the interface of a source of several, and the partition of a flow that names several. */
  view: { many: boolean; interfaceName: string | null; partitions: readonly string[]; partition: string | null };
  /** The address of the Issues tab with one issue open, keeping the rest of the address. */
  issueLink: (issue: string) => string;
  onDrop: (keys: readonly NarrowingKey[]) => void;
  /** Narrows to another run: the whole run a part was split from. */
  onRun: (runId: string) => void;
  /** Shows the ledger a run ran in, keeping the run. */
  onShowLedger: (scope: LedgerScope) => void;
}

/**
 * What a link narrowed a flow's records to, one chip each, read as a sentence (`Touched by run …1a2b3c`): its id opens what
 * it names (the run, the submission, the issue), the cross drops it, and Clear all drops them all. They hold together, with
 * the filters above the list. The run's chip says what the run was and when it ended, leads from a part to the whole run,
 * and says when the run worked in another interface or partition than the one in view, with the way there.
 */
export function RecordsNarrowing({ narrowed, view, issueLink, onDrop, onRun, onShowLedger }: RecordsNarrowingProps) {
  const keys = NARROWING_KEYS.filter((key) => narrowed[key] !== undefined);
  if (keys.length === 0) {
    return null;
  }

  return (
    <div className="flex flex-wrap items-center gap-1.5 border-b px-3 py-1.5" data-testid="delivery-records-narrowing">
      {narrowed.run !== undefined && (
        <RunNarrowing runId={narrowed.run} view={view} onDrop={() => onDrop(["run"])} onRun={onRun} onShowLedger={onShowLedger} />
      )}
      {narrowed.submission !== undefined && (
        <Chip icon={Inbox} kind="submission" dropLabel="Stop narrowing to what this submission last planned" onDrop={() => onDrop(["submission"])}
          explain="The records this submission planned last: a later submission that plans one moves it on.">
          <Caption>Last planned by submission</Caption>
          <IdLink id={narrowed.submission} to={`/delivery/submissions/${narrowed.submission}`} />
        </Chip>
      )}
      {narrowed.delivered !== undefined && (
        <Chip icon={PackageCheck} kind="delivered" dropLabel="Stop narrowing to what this submission delivered" onDrop={() => onDrop(["delivered"])}
          explain="The records this submission delivered, which stay its own however many submissions plan them afterwards.">
          <Caption>Delivered by submission</Caption>
          <IdLink id={narrowed.delivered} to={`/delivery/submissions/${narrowed.delivered}`} />
        </Chip>
      )}
      {narrowed.trigger !== undefined && (
        <Chip icon={FileDiff} kind="trigger" dropLabel="Stop narrowing to this cause" onDrop={() => onDrop(["trigger"])}
          explain="Of the records the run or submission delivered, those its reason names this cause for: a record counts under every cause its reason names.">
          <Caption>Delivered for</Caption>
          <span className="text-[12px]" data-testid="delivery-records-trigger">{triggerLabel(narrowed.trigger).toLowerCase()}</span>
        </Chip>
      )}
      {narrowed.issue !== undefined && (
        <Chip icon={CircleAlert} kind="issue" dropLabel="Stop narrowing to this issue's records" onDrop={() => onDrop(["issue"])}
          explain="The blocked records this issue keeps blocked: held or failed for the same reason.">
          <Caption>Kept blocked by issue</Caption>
          <IdLink id={narrowed.issue} to={issueLink(narrowed.issue)} text={narrowed.issue} />
        </Chip>
      )}
      {narrowed.missing !== undefined && (
        <Chip icon={ShieldAlert} kind="missing" dropLabel="Stop narrowing to these missing values" onDrop={() => onDrop(["missing"])}
          explain="The records OSDU holds restricted until complete for the same missing values.">
          <Caption>Restricted until complete, missing values</Caption>
          <span className="font-mono text-[11px]">{narrowed.missing}</span>
        </Chip>
      )}
      {keys.length > 1 && (
        <button
          type="button"
          className="px-1 text-[12px] text-muted-foreground hover:text-foreground hover:underline"
          onClick={() => onDrop(keys)}
          data-testid="delivery-records-clear-narrowing"
        >
          Clear all
        </button>
      )}
    </div>
  );
}

/**
 * The run's chip, with what the run was once it is read: its operation, how it ended and when, which part of a split
 * delivery it was (and the way to the whole run), and the interface or partition it worked in when that is not the one in
 * view. A run the platform no longer holds keeps its id alone.
 */
function RunNarrowing({ runId, view, onDrop, onRun, onShowLedger }: {
  runId: string;
  view: RecordsNarrowingProps["view"];
  onDrop: () => void;
  onRun: (runId: string) => void;
  onShowLedger: (scope: LedgerScope) => void;
}) {
  const run = useQuery({ queryKey: ["runs", runId], queryFn: () => runApi.getById(runId), staleTime: 10000, retry: false });
  const detail = run.data;
  const ran = detail === undefined ? null : runScope(detail);
  // Where the run worked when the view shows another ledger: an interface of the source, or a partition the flow serves.
  const elsewhere: LedgerScope = {
    interfaceName: ran?.interfaceName != null && view.many && ran.interfaceName !== view.interfaceName ? ran.interfaceName : null,
    partition: ran?.partition != null && view.partitions.length > 1 && view.partitions.includes(ran.partition) && ran.partition !== view.partition
      ? ran.partition
      : null,
  };
  const part = detail?.fanOutRoot != null ? detail : null;
  return (
    <>
      <Chip icon={CirclePlay} kind="run" dropLabel="Stop narrowing to this run's records" onDrop={onDrop}
        explain="The records an attempt of this run names: delivered, held, failed, passed over, removed or restored by it. A run that split its delivery over nodes counts the records its parts touched too.">
        <Caption>Touched by run</Caption>
        <IdLink id={runId} to={`/runs/${runId}`} />
        {detail !== undefined && (
          <>
            {detail.operation !== null && <span className="font-mono text-[11px] text-muted-foreground" data-testid="delivery-records-run-operation">{detail.operation}</span>}
            <span className="inline-flex scale-90"><RunStatusBadge status={detail.status} testId="delivery-records-run-status" /></span>
            {detail.endUtc !== null && (
              <span className="text-muted-foreground">
                ended <RelativeTime value={detail.endUtc} />
              </span>
            )}
          </>
        )}
        {part !== null && (
          <span className="text-muted-foreground" data-testid="delivery-records-run-part">
            {`part ${part.fanOutSlot ?? "?"} of ${part.fanOutCount ?? "?"}`}
          </span>
        )}
      </Chip>
      {part?.fanOutRoot != null && (
        <QuietAction onClick={() => onRun(part.fanOutRoot!)} testId="delivery-records-run-whole"
          title="Every record the delivery this part belongs to touched: the run that split it and each of its parts">
          Show the whole run
        </QuietAction>
      )}
      {(elsewhere.interfaceName !== null || elsewhere.partition !== null) && (
        <QuietAction onClick={() => onShowLedger(elsewhere)} testId="delivery-records-run-ledger"
          title="The run touched the records of another ledger than the one in view: each interface and partition keeps its own">
          {`It ran in ${[
            elsewhere.interfaceName === null ? null : `interface ${elsewhere.interfaceName}`,
            elsewhere.partition === null ? null : `partition ${elsewhere.partition}`,
          ].filter(Boolean).join(", ")}: show its records`}
        </QuietAction>
      )}
    </>
  );
}

/** One narrowing as a chip: its glyph, its words, and the cross that drops it, with what it means on hover. */
function Chip({ icon: Icon, kind, explain, dropLabel, onDrop, children }: {
  icon: LucideIcon;
  kind: NarrowingKey;
  explain: string;
  dropLabel: string;
  onDrop: () => void;
  children: ReactNode;
}) {
  return (
    <span
      className="inline-flex min-h-6 max-w-full min-w-0 items-center gap-1.5 rounded-md border bg-muted/60 py-0.5 pr-0.5 pl-2 text-[12px]"
      data-testid={`delivery-records-narrowed-${kind}`}
    >
      <Icon className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
      <span className="flex min-w-0 flex-wrap items-center gap-x-1.5" title={explain}>{children}</span>
      <button
        type="button"
        className="rounded-sm p-0.5 text-muted-foreground hover:bg-accent hover:text-foreground"
        onClick={onDrop}
        aria-label={dropLabel}
        title={dropLabel}
        data-testid={`delivery-records-clear-${kind}`}
      >
        <X className="size-3" />
      </button>
    </span>
  );
}

function Caption({ children }: { children: ReactNode }) {
  return <span className="text-muted-foreground">{children}</span>;
}

/** The id a chip names, by its tail unless told otherwise, opening what it names; the whole id is on hover. */
function IdLink({ id, to, text }: { id: string; to: string; text?: string }) {
  return (
    <RouterLink to={to} className="font-mono text-[11px] text-primary hover:underline" title={id.toLowerCase()}>
      {text ?? shortId(id)}
    </RouterLink>
  );
}

/** A way out of what a chip says, as quiet words beside it rather than another bordered control. */
function QuietAction({ onClick, title, testId, children }: { onClick: () => void; title: string; testId: string; children: ReactNode }) {
  return (
    <button
      type="button"
      className="px-1 text-[12px] text-primary hover:underline"
      onClick={onClick}
      title={title}
      data-testid={testId}
    >
      {children}
    </button>
  );
}
