import {
  AlertTriangle, Ban, CheckCircle2, CircleDashed, Hourglass, Loader2, PauseCircle, Trash2, Undo2, XCircle, type LucideIcon,
} from "lucide-react";
import { Badge } from "@/components/ui/badge";
import type { DeliveryRecordStatus, DeliverySubmissionStatus, DeliveryVerifyOutcome } from "../../api/delivery";
import { StatePill } from "@/components/StatusBadge";
import { verifyOutcomeAgrees, verifyOutcomeLabel } from "./verifyOutcome";

type Tone = "success" | "destructive" | "info" | "warning" | "muted";

const recordTones: Record<DeliveryRecordStatus, { tone: Tone; icon: LucideIcon }> = {
  pending: { tone: "info", icon: CircleDashed },
  delivering: { tone: "info", icon: Loader2 },
  delivered: { tone: "success", icon: CheckCircle2 },
  held: { tone: "warning", icon: PauseCircle },
  failed: { tone: "destructive", icon: XCircle },
  deleted: { tone: "muted", icon: Trash2 },
  reverted: { tone: "muted", icon: Undo2 },
  waiting: { tone: "info", icon: Hourglass },
};

/** A record's custody state, in the same pill family the run board uses. */
export function RecordStatusBadge({ status, testId = "record-status" }: { status: DeliveryRecordStatus; testId?: string }) {
  const { tone, icon } = recordTones[status] ?? { tone: "muted" as Tone, icon: CircleDashed };
  return <StatePill tone={tone} label={status} icon={icon} testId={testId} />;
}

const submissionTones: Record<DeliverySubmissionStatus, Tone> = {
  received: "info",
  planned: "info",
  running: "info",
  completed: "success",
  failed: "destructive",
};

export function SubmissionStatusBadge({ status, testId = "submission-status" }: { status: DeliverySubmissionStatus; testId?: string }) {
  const icon: LucideIcon = status === "completed" ? CheckCircle2 : status === "failed" ? XCircle : status === "running" ? Loader2 : CircleDashed;
  return <StatePill tone={submissionTones[status] ?? "muted"} label={status} icon={icon} testId={testId} />;
}

/** What the last drift check found, when one ran. */
/** What each verify outcome means, on the hover of its badge. */
const VERIFY_OUTCOME_TITLES: Record<DeliveryVerifyOutcome, string> = {
  match: "OSDU holds the record at the version the ledger holds.",
  drifted: "OSDU holds the record at another version, or with other content.",
  missing: "OSDU holds no record at this id, though the ledger says it was delivered.",
  error: "OSDU could not be read.",
  gone: "OSDU holds no record at this id: the removal stands.",
  served: "OSDU still holds a record at this id, though the ledger says it was removed.",
};

export function VerifyOutcomeBadge({ outcome }: { outcome: DeliveryVerifyOutcome | null }) {
  if (outcome === null) {
    return <span className="text-muted-foreground">-</span>;
  }

  const className = verifyOutcomeAgrees(outcome)
    ? "bg-success/15 text-success"
    : outcome === "error"
      ? "bg-muted text-muted-foreground"
      : "bg-warning/15 text-warning";
  return (
    <Badge variant="secondary" className={className} title={VERIFY_OUTCOME_TITLES[outcome]} data-testid="verify-outcome" data-outcome={outcome}>
      {!verifyOutcomeAgrees(outcome) && outcome !== "error" && <AlertTriangle className="size-3" />}
      {verifyOutcomeLabel(outcome)}
    </Badge>
  );
}

export function BlockedBadge() {
  return (
    <Badge variant="secondary" className="bg-warning/15 text-warning" data-testid="record-blocked">
      <Ban className="size-3" />
      blocked
    </Badge>
  );
}
