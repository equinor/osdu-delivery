import {
  AlertTriangle, Ban, CheckCircle2, CircleDashed, Hourglass, Loader2, Lock, PauseCircle, Trash2, Undo2, XCircle, type LucideIcon,
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

/**
 * A record whose access in OSDU is restricted under a registered access restriction: a neutral chip with its glyph, since a
 * restriction is something an operator chose, not a problem. `asked` marks one whose answer from OSDU is not known yet.
 */
export function RestrictedBadge({ asked = false }: { asked?: boolean }) {
  return (
    <Badge
      variant="outline"
      title={asked
        ? "Restricting its access was asked and OSDU's answer is not known: no run delivers it, and asking again or lifting the restriction settles it"
        : "Its access in OSDU is restricted: readable only by the restriction's groups, and delivered by no run until the restriction is lifted"}
      data-testid="record-restricted"
    >
      <Lock className="size-3 text-muted-foreground" aria-hidden />
      {asked ? "restriction asked" : "restricted"}
    </Badge>
  );
}

/**
 * A record OSDU holds restricted until complete (osdu/docs/reference/flow/mapping-restricted-access.md): delivered under its
 * mapping's restricted access because values it marks `$restrictWhenMissing` were missing. A state the data office acts on,
 * so it takes the warning tone, with what is missing on hover; it lifts itself once a delivery finds every value.
 */
export function RestrictedUntilCompleteBadge({ missing }: { missing?: string[] | null }) {
  const values = missing ?? [];
  const title = values.length === 0
    ? "Restricted until complete: OSDU holds it under its mapping's restricted access while values its mapping marks $restrictWhenMissing are missing. It gets its own access back once a delivery finds them all."
    : `Restricted until complete: OSDU holds it under its mapping's restricted access while ${values.length === 1 ? "this value is" : `these ${values.length} values are`} missing, and gives it its own access back once a delivery finds ${values.length === 1 ? "it" : "them all"}:\n${values.join("\n")}`;
  return (
    <span className="inline-flex whitespace-nowrap" title={title}>
      <StatePill tone="warning" label="restricted until complete" icon={Lock} testId="record-restricted-until-complete" />
    </span>
  );
}

/**
 * The access a record's OSDU copy is held under when it is not its mapping's: an operator's restriction (`asked` while OSDU's
 * answer is not known), then its mapping's until complete, with what it goes without. Nothing for a record with its own access.
 */
export function AccessBadges({ restricted, asked = false, restrictedUntilComplete, missing }: {
  restricted: boolean;
  asked?: boolean;
  restrictedUntilComplete: boolean;
  missing?: string[] | null;
}) {
  if (!restricted && !restrictedUntilComplete) {
    return null;
  }

  return (
    <span className="inline-flex gap-1" data-testid="record-access">
      {restricted && <RestrictedBadge asked={asked} />}
      {restrictedUntilComplete && <RestrictedUntilCompleteBadge missing={missing} />}
    </span>
  );
}
