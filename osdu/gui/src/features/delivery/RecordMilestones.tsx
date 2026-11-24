// How far a record has come, as the strip over its timeline shows it. RecordJourney.tsx re-exports it, so the record
// page imports both from there.

import type { ReactNode } from "react";
import { Skeleton } from "@/components/ui/skeleton";
import { parseUtc } from "@/lib/time";
import { RelativeTime } from "@/components/RelativeTime";
import { SummaryStrip, type SummaryCell } from "@/components/SummaryStrip";
import type { DeliveryAttempt, DeliveryRecord, DeliveryRecordChain } from "../../api/delivery";
import { verifyOutcomeAgrees, verifyOutcomeLabel } from "./verifyOutcome";
import { recordedReason } from "./deliveryReason";
import { ingestionTableName, isArrival, reachedTarget } from "./recordStory";

/**
 * The answers an operator came for, in one strip, in the order a record moves: the file it came in from, when its row
 * reached the ingestion table, when the row last changed, when it was delivered, when it was last verified, and whether
 * it was removed.
 */
/**
 * Under a delivered record's Delivered milestone: the version OSDU holds and why its last delivery was sent, the reason's one
 * line clipped to the cell and whole on hover, so why it was delivered is read without opening the timeline. A delivery made
 * before the ledger kept reasons leaves the version alone.
 */
function deliveredCaption(record: DeliveryRecord, attempts: DeliveryAttempt[]): ReactNode {
  const version = record.targetVersion !== null ? `version ${record.targetVersion}` : null;
  const why = recordedReason(attempts.find((a) => a.outcome === "delivered")?.reason)?.summary ?? null;
  if (why === null) {
    return version ?? undefined;
  }

  const line = version === null ? why : `${version} · ${why}`;
  return <span className="block max-w-[360px] truncate" title={line} data-testid="milestone-landed-why">{line}</span>;
}

function milestones(record: DeliveryRecord, attempts: DeliveryAttempt[], chain: DeliveryRecordChain | undefined): SummaryCell[] {
  const dispatched = attempts.filter((a) => a.outcome === "delivered" || a.outcome === "failed" || (a.outcome === "held" && reachedTarget(a)));
  const failed = dispatched.filter((a) => a.outcome === "failed").length;
  const removed = attempts.find((a) => a.outcome === "deleted");
  const changes = chain?.changes ?? [];
  const arrival = [...changes].reverse().find(isArrival);
  const since = arrival === undefined ? changes : changes.filter((c) => parseUtc(c.atUtc).getTime() > parseUtc(arrival.atUtc).getTime());
  const latest = since.at(0);
  const unknown = chain === undefined ? "-" : "not recorded";

  const cells: SummaryCell[] = [
    {
      label: "From file",
      value: arrival?.landing != null ? <RelativeTime value={arrival.landing.run.ranUtc} /> : unknown,
      caption: arrival === undefined
        ? undefined
        : arrival.fileName ?? "the file is not recorded",
      tone: arrival?.landing != null && !arrival.landing.run.success ? "destructive" : undefined,
      testId: "milestone-file",
    },
    {
      label: "Loaded",
      value: arrival !== undefined ? <RelativeTime value={arrival.atUtc} /> : unknown,
      caption: arrival === undefined
        ? undefined
        : arrival.kind === "earliest"
          ? `earliest version held, ${ingestionTableName(chain?.sourceTable ?? null)}`
          : ingestionTableName(chain?.sourceTable ?? null),
      testId: "milestone-loaded",
    },
    {
      label: "Last change",
      value: latest !== undefined ? <RelativeTime value={latest.atUtc} /> : arrival !== undefined ? "none" : unknown,
      caption: latest === undefined
        ? (arrival !== undefined ? "unchanged since it was loaded" : undefined)
        : latest.kind === "deleted"
          ? "the row was deleted"
          : `${since.length} change${since.length === 1 ? "" : "s"} since it was loaded`,
      tone: latest?.kind === "deleted" ? "destructive" : undefined,
      testId: "milestone-changed",
    },
    {
      label: "Delivered",
      value: record.lastDeliveredUtc === null ? "not yet" : <RelativeTime value={record.lastDeliveredUtc} />,
      caption: record.lastDeliveredUtc === null
        ? (dispatched.length === 0
          ? (record.status === "pending" || record.status === "delivering" ? "queued" : record.status === "waiting" ? "waiting" : undefined)
          : `${dispatched.length} ${dispatched.length === 1 ? "try" : "tries"}${failed > 0 ? `, ${failed} failed` : ""}`)
        : deliveredCaption(record, attempts),
      tone: record.lastDeliveredUtc === null ? undefined : "success",
      testId: "milestone-landed",
    },
    {
      label: "Verified",
      value: record.lastVerifiedUtc === null ? "never" : <RelativeTime value={record.lastVerifiedUtc} />,
      caption: record.lastVerifyOutcome === null ? undefined : verifyOutcomeLabel(record.lastVerifyOutcome),
      tone: record.lastVerifyOutcome === null || record.lastVerifyOutcome === "error"
        ? undefined
        : verifyOutcomeAgrees(record.lastVerifyOutcome) ? "success" : "warning",
      testId: "milestone-verified",
    },
  ];
  if (record.status === "deleted" || removed !== undefined) {
    cells.push({
      label: "Removed",
      value: removed === undefined ? "yes" : <RelativeTime value={removed.startedUtc} />,
      caption: record.status !== "deleted"
        ? "delivered again since"
        : removed?.phase === "source-removed"
          ? "its source no longer holds the key"
          : record.blocked ? "blocked until its row changes or it is released" : "sent again when its row is read",
      tone: "destructive",
      testId: "milestone-removed",
    });
  }

  return cells;
}

/**
 * How far the record has come, as one strip in the order a record moves: the file it came in from, when its row
 * reached the ingestion table and last changed there, when it was delivered and as which version, when it was last
 * verified, and whether it was removed. The page's spine: the timeline under it is the evidence.
 */
export function RecordMilestones({ record, attempts, chain }: {
  record: DeliveryRecord;
  attempts: DeliveryAttempt[] | undefined;
  /** The changes of the record's row in its ingestion table; undefined while they are being read. */
  chain: DeliveryRecordChain | undefined;
}) {
  if (attempts === undefined) {
    return <Skeleton className="h-16 w-full rounded-lg" />;
  }

  // Five cells (six for a removed record) at the strip's default floor need more width than the workbench's content
  // measure gives them and strand the last on a line of its own; a time and a short caption fit in 160px.
  return <SummaryStrip cells={milestones(record, attempts, chain)} minCellWidth={160} data-testid="record-milestones" />;
}
