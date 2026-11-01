import { Badge } from "@/components/ui/badge";
import { RelativeTime } from "@/components/RelativeTime";
import type { DeliveryPreviewLedgerRecord, DeliveryRecordStatus } from "../../api/delivery";
import { RecordStatusBadge } from "./DeliveryBadges";

const STAGED_MEANS = "The row is in the record table, and no run has taken it into the ledger yet.";

/** A row no run has taken into the ledger: the word that stands where a record's status would. */
export function StagedBadge({ testId }: { testId: string }) {
  return <Badge variant="outline" className="text-[10.5px] font-normal" title={STAGED_MEANS} data-testid={testId}>staged</Badge>;
}

/** Where one row stands, in a list: staged, or the status of its record. Nothing when the ledger was not read, rather than a guess. */
export function RowStanding({ status }: { status: DeliveryRecordStatus | null | undefined }) {
  if (status === undefined) {
    return null;
  }

  return status === null ? <StagedBadge testId="staged-line-ledger" /> : <RecordStatusBadge status={status} testId="staged-line-ledger" />;
}

/**
 * Where a previewed record stands: only staged, or in the ledger with its status, when it was delivered and whether that is the
 * document rendered here. `compact` keeps to the status where room is short, with the rest on hover.
 */
export function LedgerStanding({ ledger, compact = false }: { ledger: DeliveryPreviewLedgerRecord | null; compact?: boolean }) {
  if (ledger === null) {
    return (
      <span className="inline-flex items-center gap-1.5 text-[12px] text-muted-foreground" data-testid="preview-ledger">
        <StagedBadge testId="preview-ledger-status" />
        {!compact && "not in the ledger yet"}
      </span>
    );
  }

  const { lastDeliveredUtc, sameDocument } = ledger;
  const known = sameDocument !== null && sameDocument !== undefined;
  const hover = known ? (sameDocument ? "In the ledger, the same as this render." : "In the ledger, and it differs from this render.") : "In the ledger.";
  return (
    <span className="inline-flex items-center gap-1.5 text-[12px] text-muted-foreground" title={hover} data-testid="preview-ledger">
      <RecordStatusBadge status={ledger.status} testId="preview-ledger-status" />
      {/* Its version in OSDU is on the record's page; here, when it was delivered and whether it is this document. */}
      {!compact && (lastDeliveredUtc || known) && (
        <span>
          {lastDeliveredUtc && <RelativeTime value={lastDeliveredUtc} />}
          {lastDeliveredUtc && known && ", "}
          {sameDocument === true && <span data-testid="preview-same">same as this render</span>}
          {sameDocument === false && <span data-testid="preview-differs">differs from this render</span>}
        </span>
      )}
    </span>
  );
}
