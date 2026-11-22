import type { DeliveryAttempt } from "../../api/delivery";
import { TruncatedText } from "@/components/TruncatedText";
import { NOT_RECORDED, recordedReason } from "./deliveryReason";

/**
 * An attempt's error, or the note an attempt that did not fail carries, with the correlation id its OSDU requests
 * carried underneath: the id to hand to whoever reads the services' logs.
 */
export function AttemptDetail({ attempt }: { attempt: DeliveryAttempt }) {
  const text = attempt.error ?? attempt.result?.detail ?? null;
  const correlationId = attempt.result?.correlationId;
  // A delivered try says what sent it first, and a render that sent nothing what moved to render it
  // (osdu/docs/reference/concepts/delivery-reasons.md).
  const why = recordedReason(attempt.reason)?.summary ?? (attempt.outcome === "delivered" ? NOT_RECORDED : null);
  return (
    <div className="flex flex-col gap-0.5">
      {why !== null && <TruncatedText text={why} maxWidth={360} className={recordedReason(attempt.reason) === null ? "text-muted-foreground" : undefined} />}
      {(why === null || text !== null) && <TruncatedText text={text} maxWidth={360} className={attempt.error !== null ? "text-destructive" : undefined} />}
      {correlationId !== undefined && (
        <TruncatedText text={correlationId} mono copy maxWidth={360} title="Correlation id" className="text-[11px] text-muted-foreground" />
      )}
    </div>
  );
}
