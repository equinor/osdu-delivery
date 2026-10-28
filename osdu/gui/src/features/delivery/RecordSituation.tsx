import { useCallback, useState, useSyncExternalStore, type ReactNode } from "react";
import { Link as RouterLink } from "react-router-dom";
import { Ban, CircleAlert, Hourglass, KeyRound, Loader2, RotateCcw, Send, ShieldAlert, Trash2, Undo2, type LucideIcon } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { ExplainMark } from "@/components/ExplainTip";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import type { DeliveryRecord, DeliveryRecordLink } from "../../api/delivery";
import type { RemovedKey } from "../../api/removals";
import { removalWhy } from "./removals/removalFormat";
import { shortId } from "./idTail";
import { stranded } from "./strandedRecord";
import { hasPassed } from "./utcInstant";

type Tone = "destructive" | "warning" | "info" | "muted";

const TONE: Record<Tone, string> = {
  destructive: "",
  warning: "border-warning/40 text-warning *:data-[slot=alert-description]:text-warning/90",
  info: "border-info/40 *:data-[slot=alert-description]:text-foreground [&>svg]:text-info",
  muted: "*:data-[slot=alert-description]:text-muted-foreground [&>svg]:text-muted-foreground",
};

function Line({ tone, icon: Icon, spin = false, testId, children }: { tone: Tone; icon: LucideIcon; spin?: boolean; testId: string; children: ReactNode }) {
  return (
    <Alert variant={tone === "destructive" ? "destructive" : "default"} className={cn("py-2", TONE[tone])} data-testid={testId}>
      <Icon className={spin ? "animate-spin" : undefined} />
      {/* One block for the whole line: the description lays its children out as rows, which would put every
          clipped value and time of the sentence on a row of its own. */}
      <AlertDescription className="text-[13px]"><div className="min-w-0 break-words">{children}</div></AlertDescription>
    </Alert>
  );
}

/** A link to another record's page, named as the ledger names the record, with its flow on hover. */
export function RecordLink({ link, testId }: { link: DeliveryRecordLink; testId?: string }) {
  const flow = link.flowName === null ? undefined : link.interface ? `${link.flowName} / ${link.interface}` : link.flowName;
  return (
    <RouterLink to={`/delivery/records/${link.flowId}/${link.deliveryKey}`} className="text-primary hover:underline" title={flow} data-testid={testId}>
      {link.label ?? link.sourceKey}
    </RouterLink>
  );
}

/**
 * Whether an instant has passed, kept current while the page stays open: a lease that runs out while the operator is
 * looking at the record turns into the "ran out" line without a reload. The clock is read in an effect, never in
 * render, so a render is the same whenever it happens.
 */
function useHasPassed(instantUtc: string | null): boolean {
  const subscribe = useCallback((onChange: () => void) => {
    const timer = window.setInterval(onChange, 15_000);
    return () => window.clearInterval(timer);
  }, []);
  return useSyncExternalStore(subscribe, () => hasPassed(instantUtc, Date.now()));
}

/**
 * The ledger's deletion of the record's key, when a removal flow found its source no longer holds it
 * (osdu/docs/reference/flow/removal.md): taken out, about to be, or waiting for an operator's approval, with who found it and
 * as of when (the whole of why on hover), and the way to lift it: a record taken out is delivered again under its id.
 */
function RemovalLine({ removedKey, onLift, lifting }: { removedKey: RemovedKey; onLift?: () => void; lifting: boolean }) {
  const who = `removal flow ${removedKey.removalFlow} (${removedKey.entry})`;
  const why = removedKey.reason === "listed" ? "its source listed the key as removed" : "its source's keys no longer held it";
  const done = removedKey.state === "removed";
  const held = removedKey.state === "awaiting-approval";
  const action = onLift === undefined
    ? null
    : (
      <Button
        variant="outline"
        size="xs"
        className="ml-2 align-middle"
        onClick={onLift}
        disabled={lifting}
        title={done
          ? "Stop keeping the key out and deliver the record again under its id now. While the source still lacks the key, the removal flow's next run removes it again."
          : "Stop keeping the key out: the record stays in OSDU. While the source still lacks the key, the removal flow's next run finds it removed again."}
        data-testid="record-removal-lift"
      >
        {lifting ? <Loader2 className="animate-spin" /> : <RotateCcw />}
        {done ? "Deliver again" : "Lift removal"}
      </Button>
    );
  return (
    <Line key="removal" tone={done ? "muted" : "warning"} icon={done ? Trash2 : held ? ShieldAlert : Hourglass} testId="record-removal">
      <span>
        {done
          ? <>{`Removed by ${who}: ${why} `}<RelativeTime value={removedKey.removedUtc} />{". A row of the key newer than that is delivered again."}</>
          : held
            ? <>{`Found removed by ${who}: ${why} `}<RelativeTime value={removedKey.removedUtc} />{", with more keys than its guard allows, so nothing is done until an operator approves them on its Removals tab."}</>
            : <>{`Found removed by ${who}: ${why} `}<RelativeTime value={removedKey.removedUtc} />{". Its next run takes the record out of OSDU; no row of the key older than that is delivered."}</>}
      </span>
      <ExplainMark title="Why removed" text={removalWhy(removedKey)} testId="record-removal-why" />
      {action}
    </Line>
  );
}

/** When the record was held or failed, by the tries made for its pending work: after them, or before any. */
function afterTries(count: number): string {
  return count === 0 ? "before any try" : `after ${count} ${count === 1 ? "try" : "tries"}`;
}

/** What is waiting to go: the metadata, the payload, or both. */
function pendingWhat(record: DeliveryRecord): string {
  return record.pendingMetadata && record.pendingPayload ? "metadata and payload" : record.pendingPayload ? "payload" : "metadata";
}

function NextTry({ record }: { record: DeliveryRecord }) {
  return record.nextAttemptUtc === null ? null : <span>{" Next try "}<RelativeTime value={record.nextAttemptUtc} />.</span>;
}

/**
 * The steps of the waiting delivery an earlier try completed (a file uploaded, a dataset registered) before it stopped,
 * which the next try resumes after rather than doing again; what each returned is on hover.
 */
function StepsDone({ record }: { record: DeliveryRecord }) {
  const steps = record.pendingSteps === null ? [] : Object.keys(record.pendingSteps);
  if (steps.length === 0) {
    return null;
  }

  return (
    <span title={JSON.stringify(record.pendingSteps, null, 2)} data-testid="record-steps-done">
      {" An earlier try completed "}
      <span className="font-mono">{steps.join(", ")}</span>
      {"; the next try resumes after them."}
    </span>
  );
}

/** The most records waiting for this one the record's page is given (the control plane's `MaxWaitersShown`). */
const MAX_WAITERS_SHOWN = 50;

/** How many of the records waiting for this one the line names before it offers the rest. */
const WAITERS_NAMED = 5;

/**
 * The records whose waiting document refers to this one: what this record's state costs beyond itself. A record that
 * failed, is held or is blocked keeps them waiting, so the line warns then. The first few are named, the rest one click
 * away, each a link to its own page with its flow on hover.
 */
function Waiters({ record, waiters }: { record: DeliveryRecord; waiters: DeliveryRecordLink[] }) {
  const [all, setAll] = useState(false);
  const named = all ? waiters : waiters.slice(0, WAITERS_NAMED);
  const stuck = record.blocked || record.status === "failed" || record.status === "held";
  const count = waiters.length >= MAX_WAITERS_SHOWN
    ? `${MAX_WAITERS_SHOWN} or more records wait`
    : waiters.length === 1 ? "1 record waits" : `${waiters.length} records wait`;
  return (
    <Line tone={stuck ? "warning" : "info"} icon={Hourglass} testId="record-waited-on-by">
      {record.status === "delivered" ? `${count} for this one still: ` : `${count} for this one to be delivered: `}
      {named.map((waiter, i) => (
        <span key={`${waiter.flowId}:${waiter.deliveryKey}`}>
          {i > 0 && ", "}
          <RecordLink link={waiter} testId="record-waiter-link" />
        </span>
      ))}
      {waiters.length > named.length
        ? (
          // Kept on one line: the button is a box of its own, and the full stop after it wrapped alone.
          <span className="whitespace-nowrap">
            {" and "}
            <Button variant="link" className="h-auto p-0 text-[13px]" onClick={() => setAll(true)} data-testid="record-waiters-more">
              {`${waiters.length - named.length} more`}
            </Button>
            .
          </span>
        )
        : "."}
    </Line>
  );
}

/**
 * Where the record stands right now and why, said once, in the lines its state calls for and in no others: blocked,
 * held or failed with its error, waiting for another record, mid-delivery under a lease (or a lease that ran out), a
 * rendered document waiting to go (with the steps an earlier try of it completed), removed, and the records waiting for
 * this one. A delivered record with nothing wrong has no situation and gets no lines: the status pill and the milestones
 * say everything there is.
 */
export function RecordSituation({ record, waitsOn, waitedOnBy, removedKey, onLift, lifting = false }: {
  record: DeliveryRecord;
  waitsOn: DeliveryRecordLink | null | undefined;
  /** The records whose waiting document refers to this one, the first `MAX_WAITERS_SHOWN`. */
  waitedOnBy: DeliveryRecordLink[];
  /** The ledger's deletion of the record's key, when a removal flow found its source no longer holds it. */
  removedKey?: RemovedKey;
  /** Lifts that deletion; absent where the reader cannot. */
  onLift?: () => void;
  lifting?: boolean;
}) {
  const leaseExpired = useHasPassed(record.leaseExpiresUtc);
  const lines: ReactNode[] = [];
  if (removedKey !== undefined) {
    lines.push(<RemovalLine key="removal" removedKey={removedKey} onLift={onLift} lifting={lifting} />);
  }

  if (record.blocked) {
    lines.push(
      <Line key="blocked" tone="warning" icon={Ban} testId="record-blocked-note">
        Blocked: nothing is sent until its row changes or the record is released.
      </Line>,
    );
  }

  // Released and asked to be planned again from its row: nothing is wrong to show, only what comes next. The run that
  // plans it holds it again, sends it, or finds OSDU already holding it and settles it as delivered.
  const released = (record.status === "held" || record.status === "failed") && !record.blocked && !record.hasPendingDocument
    && record.planRequestedUtc !== null;

  switch (record.status) {
    case "held":
    case "failed":
      if (released) {
        lines.push(
          <Line key="released" tone="info" icon={Hourglass} testId="record-released">
            {"Asked to be planned again "}
            <RelativeTime value={record.planRequestedUtc} />
            {": the flow's next deliver run plans it from its ingestion row."}
            {record.lastError !== null && <div className="mt-1 break-words text-muted-foreground">{record.lastError}</div>}
          </Line>,
        );
        break;
      }

      if (stranded(record)) {
        lines.push(
          <Line key="stranded" tone="warning" icon={CircleAlert} testId="record-stranded">
            Nothing will move this record: it is not blocked, no run is asked to plan it, and it keeps no document to send.
            Release it, and the flow&apos;s next deliver run plans it from its ingestion row.
            {record.lastError !== null && <div className="mt-1 break-words">{record.lastError}</div>}
          </Line>,
        );
        break;
      }

      lines.push(
        <Line key="error" tone="destructive" icon={CircleAlert} testId="record-error">
          <span className="font-medium">{record.status === "held" ? "Held back" : "Failed"}</span>
          {` ${afterTries(record.attemptCount)}.`}
          <NextTry record={record} />
          {record.hasPendingDocument && (
            <span>
              {` The rendered ${pendingWhat(record)} waits`}
              {record.workBatch !== null && ` in work batch ${record.workBatch}`}
              {record.blocked ? " until the record is released." : " for the next try."}
              <StepsDone record={record} />
            </span>
          )}
          {record.lastError !== null && <div className="mt-1 break-words">{record.lastError}</div>}
        </Line>,
      );
      break;
    case "waiting":
      lines.push(
        <Line key="waiting" tone="info" icon={Hourglass} testId="record-waiting">
          <span>{record.lastError ?? `Waits for ${record.waitingFor ?? "a record it refers to"}.`}</span>
          {waitsOn && (
            <span>
              {" It goes out on its own once "}
              <RecordLink link={waitsOn} testId="record-waits-on-link" />
              {" is delivered."}
            </span>
          )}
          {record.planRequestedUtc !== null && <span>{" A plan last asked for it "}<RelativeTime value={record.planRequestedUtc} />.</span>}
        </Line>,
      );
      break;
    case "delivering":
      lines.push(
        leaseExpired
          ? (
            <Line key="lease" tone="warning" icon={KeyRound} testId="record-lease">
              {"The lease "}
              <TruncatedText text={record.leaseOwner ?? "a worker"} mono maxWidth={220} />
              {" held ran out "}
              <RelativeTime value={record.leaseExpiresUtc} />
              {": the worker stopped mid-delivery. The flow's next deliver run recovers the record and sends it."}
            </Line>
          )
          : (
            <Line key="lease" tone="info" icon={Loader2} spin testId="record-lease">
              {"Being delivered by "}
              <TruncatedText text={record.leaseOwner ?? "a worker"} mono maxWidth={220} />
              {record.leaseExpiresUtc !== null && <span>{"; the lease runs until "}<RelativeTime value={record.leaseExpiresUtc} /></span>}
              .
            </Line>
          ),
      );
      break;
    case "pending":
      lines.push(
        <Line key="pending" tone="info" icon={Send} testId="record-pending">
          {record.hasPendingDocument
            ? (
              <span>
                {`A rendered document (${pendingWhat(record)}) waits to be dispatched`}
                {record.workBatch !== null && ` in work batch ${record.workBatch}`}
                {record.lastSubmissionId !== null && <span>{" of submission "}<span className="font-mono" title={record.lastSubmissionId}>{shortId(record.lastSubmissionId)}</span></span>}
                .
              </span>
            )
            : "Pending: the flow's next deliver run renders the record from its source row and sends it."}
          <NextTry record={record} />
          {record.hasPendingDocument && <StepsDone record={record} />}
        </Line>,
      );
      break;
    case "delivered":
      // Asked to be planned again with nothing queued: a released reverted record, a redelivery, a sync that found its row
      // moved, a cache change. The next run plans it from its row.
      if (!record.hasPendingDocument && record.planRequestedUtc !== null) {
        lines.push(
          <Line key="asked" tone="info" icon={Hourglass} testId="record-asked">
            {"Asked to be planned again "}
            <RelativeTime value={record.planRequestedUtc} />
            {": the flow's next deliver run plans it from its ingestion row and sends what differs from what OSDU holds."}
          </Line>,
        );
      }

      if (record.hasPendingDocument) {
        lines.push(
          <Line key="newer" tone="info" icon={Send} testId="record-pending">
            {`A newer document (${pendingWhat(record)}) is rendered and waits to go`}
            {record.workBatch !== null && ` in work batch ${record.workBatch}`}
            .
            <NextTry record={record} />
            <StepsDone record={record} />
          </Line>,
        );
      }
      break;
    case "deleted":
      // A record a removal flow took out says so in its own line, with the way to deliver it again.
      if (removedKey?.state === "removed") {
        break;
      }

      lines.push(
        <Line key="deleted" tone="muted" icon={Trash2} testId="record-deleted">
          {record.blocked
            ? "Removed from OSDU by a reversal: nothing of this flow's is there under its id. It stays removed until its row changes or the record is released."
            : record.planRequestedUtc !== null
              ? "Removed from OSDU: nothing of this flow's is there under its id. The flow's next run reads its row again and sends it; Redeliver sends it now."
              : "Removed from OSDU: nothing of this flow's is there under its id. The next run that reads its row sends it again; Redeliver sends it now."}
        </Line>,
      );
      break;
    case "reverted":
      lines.push(
        <Line key="reverted" tone="muted" icon={Undo2} testId="record-reverted">
          {`Reverted: an earlier version was written back${record.targetVersion !== null ? ` as version ${record.targetVersion}` : ""}, by a reversal or by restoring the previous version. `}
          A release plans it again from the current source; a changed source plans it on its own.
        </Line>,
      );
      break;
  }

  if (waitedOnBy.length > 0) {
    lines.push(<Waiters key="waiters" record={record} waiters={waitedOnBy} />);
  }

  if (record.status !== "delivering" && record.leaseOwner !== null) {
    lines.push(
      <Line key="stale-lease" tone="muted" icon={KeyRound} testId="record-lease">
        {"A lease is still recorded for "}
        <TruncatedText text={record.leaseOwner} mono maxWidth={220} />
        {record.leaseExpiresUtc !== null && <span>{leaseExpired ? ", which ran out " : ", which runs until "}<RelativeTime value={record.leaseExpiresUtc} /></span>}
        .
      </Line>,
    );
  }

  if (lines.length === 0) {
    return null;
  }

  return <div className="flex flex-col gap-2" data-testid="record-situation">{lines}</div>;
}
