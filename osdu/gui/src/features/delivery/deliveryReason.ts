// Why a record was delivered, as the record's timeline and a submission's page say it
// (osdu/docs/reference/concepts/delivery-reasons.md). Pure: no React, no SQLFlow import, so the
// unit tier reads it. The shapes are the API's (`DeliveryAttemptReason`, `DeliveryReasonTrigger`), restated structurally.

/** The kinds a delivered attempt's reason names, in the order a reason lists them. */
export const TRIGGER_KINDS = [
  "first-delivery",
  "requested",
  "drift",
  "forced",
  "source-changed",
  "child-or-lookup-changed",
  "mapping-changed",
  "template-changed",
  "parameters-changed",
  "enrichment-changed",
  "cache-changed",
  "payload-changed",
  "other-input-changed",
] as const;

export type TriggerKind = (typeof TRIGGER_KINDS)[number];

/** One input that moved, as a reason lists it. */
export interface ReasonTrigger {
  kind: string;
  from?: string;
  to?: string;
  fromFingerprint?: string;
  toFingerprint?: string;
  fromCommit?: string;
  toCommit?: string;
  names?: string[];
  more?: number;
  detail?: string;
  at?: string;
  activityId?: number;
  activity?: string;
  actor?: string;
}

export interface ReasonPaths {
  compared: boolean;
  why?: string;
  added?: number;
  removed?: number;
  changed?: number;
  listed?: { path: string; change: string }[];
  more?: number;
}

export type AttemptReason =
  | { recorded: true; action: string; summary: string; triggers: ReasonTrigger[]; paths?: ReasonPaths }
  | { recorded: false; summary: string };

const LABELS: Record<TriggerKind, string> = {
  "first-delivery": "First delivery",
  requested: "Asked to be planned again",
  drift: "OSDU differed from the ledger",
  forced: "Forced by the flow",
  "source-changed": "Source row changed",
  "child-or-lookup-changed": "Child or lookup row changed",
  "mapping-changed": "Mapping changed",
  "template-changed": "Template changed",
  "parameters-changed": "Parameters changed",
  "enrichment-changed": "Enrichment changed",
  "cache-changed": "Cache changed",
  "payload-changed": "Payload changed",
  "other-input-changed": "Rendered differently, no compared input moved",
};

/** What the words of a timeline say a reason was not recorded. */
export const NOT_RECORDED = "reason not recorded";

/** A trigger kind as a label; a kind this page does not know, as the ledger names it. */
export function triggerLabel(kind: string): string {
  return (LABELS as Record<string, string>)[kind] ?? kind;
}

/** A reason the attempt carries and recorded, or null for one delivered before the ledger kept reasons. */
export function recordedReason(reason: AttemptReason | null | undefined): Extract<AttemptReason, { recorded: true }> | null {
  return reason != null && reason.recorded ? reason : null;
}

/** The title of a delivered try on the timeline: what sent it, when it was recorded. */
export function deliveredTitle(reason: AttemptReason | null | undefined): string {
  const recorded = recordedReason(reason);
  return recorded !== null ? `Delivered: ${recorded.summary}` : "Delivered";
}

/** A fingerprint, hash or commit as a fact shows it: its first twelve characters, or seven for a commit. */
export function shortened(value: string, length = 12): string {
  return value.length > length ? value.slice(0, length) : value;
}

/**
 * The two sides of a trigger, as one line each when it has them: what it was and what it is now. A mapping names its
 * reference, the fingerprint of its document and the estate commit of each; a request names who asked and as what.
 */
export function triggerSides(trigger: ReasonTrigger): { was: string | null; now: string | null } {
  if (trigger.kind === "mapping-changed") {
    const side = (reference?: string, fingerprint?: string, commit?: string): string | null => {
      const parts = [reference, fingerprint !== undefined ? shortened(fingerprint) : undefined, commit !== undefined ? `commit ${shortened(commit, 7)}` : undefined]
        .filter((part): part is string => part !== undefined && part !== "");
      return parts.length > 0 ? parts.join(" · ") : null;
    };
    return { was: side(trigger.from, trigger.fromFingerprint, trigger.fromCommit), now: side(trigger.to, trigger.toFingerprint, trigger.toCommit) };
  }

  if (trigger.kind === "payload-changed") {
    return { was: trigger.from !== undefined ? shortened(trigger.from) : "none", now: trigger.to !== undefined ? shortened(trigger.to) : null };
  }

  return { was: trigger.from ?? null, now: trigger.to ?? null };
}

/** What a trigger names beside its sides (the parameters, enrichments or cache changes, who asked), in one line. */
export function triggerNames(trigger: ReasonTrigger): string | null {
  if (trigger.kind === "requested" && (trigger.activity !== undefined || trigger.actor !== undefined)) {
    return [trigger.activity, trigger.actor !== undefined ? `by ${trigger.actor}` : undefined].filter((part) => part !== undefined).join(" ");
  }

  const names = trigger.names ?? [];
  if (names.length === 0) {
    return null;
  }

  const more = trigger.more ?? 0;
  return names.join(", ") + (more > 0 ? ` and ${more.toLocaleString("en-US")} more` : "");
}

/** The paths of a reason in a heading: how many changed and of what kind, or why they were not compared. */
export function pathsHeading(paths: ReasonPaths): string {
  if (!paths.compared) {
    return `Paths not compared: ${paths.why ?? "no earlier render"}`;
  }

  const counts = [
    [paths.changed ?? 0, "changed"],
    [paths.added ?? 0, "added"],
    [paths.removed ?? 0, "removed"],
  ] as const;
  const named = counts.filter(([count]) => count > 0).map(([count, word]) => `${count.toLocaleString("en-US")} ${word}`);
  return named.length > 0 ? `Paths: ${named.join(", ")}` : "No path changed";
}

/** A submission's deliveries by what sent them, in one line: `11,768 delivered: mapping changed 11,768; cache changed 2,104`. */
export function deliveredByText(deliveredBy: { delivered: number; notRecorded: number; triggers: { kind: string; records: number }[] }): string {
  const n = (count: number) => count.toLocaleString("en-US");
  const parts = deliveredBy.triggers.map((t) => `${triggerLabel(t.kind).toLowerCase()} ${n(t.records)}`);
  if (deliveredBy.notRecorded > 0) {
    parts.push(`${NOT_RECORDED} ${n(deliveredBy.notRecorded)}`);
  }

  return parts.length > 0 ? `${n(deliveredBy.delivered)} delivered: ${parts.join("; ")}` : `${n(deliveredBy.delivered)} delivered`;
}
