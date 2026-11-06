import {
  Ban, CircleDashed, CircleHelp, Equal, FileQuestion, GitBranch, Hourglass, Lock, PenLine, Trash2, Undo2, XCircle, type LucideIcon,
} from "lucide-react";
import type { DeliveryReversal } from "../../api/delivery";

/**
 * What came of a record of a reversal, as a page draws it: its glyph in the tone of what it means, a short word, and what it
 * means in a sentence, for the tooltip. The words are the API's outcomes, so a page links them straight to the records.
 */
export const REVERSAL_OUTCOMES: Record<string, { label: string; icon: LucideIcon; tone: string; meaning: string }> = {
  restored: {
    label: "restored",
    icon: Undo2,
    tone: "text-success",
    meaning: "OSDU holds again the version it held before, written back as a new version. The record is blocked until its source changes or it is released.",
  },
  removed: {
    label: "removed",
    icon: Trash2,
    tone: "text-success",
    meaning: "The reversed run created the record, so it was removed again, reversibly. It is blocked until its source changes or it is released.",
  },
  "already-gone": {
    label: "already gone",
    icon: Trash2,
    tone: "text-muted-foreground",
    meaning: "The reversed run created the record and OSDU had already lost it; the ledger now says so.",
  },
  superseded: {
    label: "superseded",
    icon: GitBranch,
    tone: "text-warning",
    meaning: "Something came after the reversed run: a later run delivered the record again, or it was removed or reverted since. Reverse the later run first.",
  },
  unchanged: {
    label: "unchanged",
    icon: Equal,
    tone: "text-muted-foreground",
    meaning: "The reversed run left OSDU at the version it held before: there was nothing to put back.",
  },
  "changed-in-osdu": {
    label: "changed in OSDU",
    icon: PenLine,
    tone: "text-warning",
    meaning: "Something outside this flow wrote the record in OSDU after the reversed run, so it is left as it is.",
  },
  "missing-in-osdu": {
    label: "missing in OSDU",
    icon: FileQuestion,
    tone: "text-warning",
    meaning: "OSDU no longer holds a record the reversed run updated: it was removed outside this flow.",
  },
  "version-missing": {
    label: "version gone",
    icon: FileQuestion,
    tone: "text-warning",
    meaning: "OSDU no longer holds the version to put back: the record's earlier versions were purged.",
  },
  busy: {
    label: "busy",
    icon: Hourglass,
    tone: "text-info",
    meaning: "Work was queued or in flight for the record. It is taken again when the reversal is asked again, once that work has settled.",
  },
  restricted: {
    label: "restricted",
    icon: Lock,
    tone: "text-info",
    meaning: "The record's access in OSDU is restricted, and nothing is written to it while it is. It is taken again when the reversal is asked again, once the restriction is lifted.",
  },
  "not-claimed": {
    label: "not claimed",
    icon: Ban,
    tone: "text-muted-foreground",
    meaning: "The record never claimed its OSDU id, so this flow wrote nothing to OSDU under it.",
  },
  "not-in-ledger": {
    label: "not in the ledger",
    icon: CircleHelp,
    tone: "text-muted-foreground",
    meaning: "The ledger holds no record of the flow under the key.",
  },
  "not-reversible": {
    label: "not reversible",
    icon: Ban,
    tone: "text-warning",
    meaning: "The flow's route cannot do what the record needs; each record says why.",
  },
  failed: {
    label: "failed",
    icon: XCircle,
    tone: "text-destructive",
    meaning: "OSDU or the route refused or failed. The record is taken again when the reversal is asked again.",
  },
  pending: {
    label: "not done yet",
    icon: CircleDashed,
    tone: "text-info",
    meaning: "Not taken yet, or left mid-write by a run that stopped; asking for the reversal again takes it.",
  },
};

/** The order outcomes are shown in: what was put back first, then what was passed over, then what failed or waits. */
export const REVERSAL_OUTCOME_ORDER = [
  "restored", "removed", "already-gone", "superseded", "unchanged", "changed-in-osdu", "missing-in-osdu", "version-missing", "busy",
  "restricted", "not-claimed", "not-in-ledger", "not-reversible", "failed", "pending",
];

/** Whether a reversal is still being worked on by a run. */
export function reversalActive(reversal: DeliveryReversal | undefined | null): boolean {
  return reversal?.status === "capturing" || reversal?.status === "reversing";
}
