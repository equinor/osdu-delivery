// The entries of a record's timeline (RecordJourney) for what someone asked: a redelivery, a release, a removal or a
// verify, with who asked, how many records it named and what came of it, and a verify of the whole flow by its outcome.

import type { ReactNode } from "react";
import { ArrowRightLeft, ShieldCheck } from "lucide-react";
import { parseUtc } from "@/lib/time";
import { CodeView } from "@/components/CodeView";
import { RelativeTime } from "@/components/RelativeTime";
import type { DeliveryActivity, DeliveryRecord } from "../../api/delivery";
import { RunRef, SubmissionRef } from "./DeliveryRefs";
import { Fact, FactGrid } from "./Facts";
import { prettyJson } from "./prettyJson";
import { actorName, parametersOf, reachOf, redeliveryOf, scopeOf } from "./recordStory";
import { between, type Entry, entryDefaults, type Outcome, type Tone } from "./recordJourneyEntry";
import { Mono } from "./RecordJourneyParts";

/** JSON the ledger stored as text, pretty-printed when it parses and shown as it is when it does not. */
function storedJson(text: string): string {
  try {
    return prettyJson(text);
  } catch {
    return text;
  }
}

/** The request each kind of intervention makes, and the button on a record's page that makes it for that record alone. */
const REQUESTS: Partial<Record<string, { one: string; many: (count: number) => string; button: string }>> = {
  redeliver: { one: "Redelivery of this record asked", many: (count) => `Redelivery asked for ${count} records, this one among them`, button: "Redeliver" },
  rerender: {
    one: "Bringing this record up to date asked",
    many: (count) => `Bringing ${count} records up to date asked, this one among them`,
    button: "Bring up to date",
  },
  release: { one: "Release of this record asked", many: (count) => `Release asked for ${count} records, this one among them`, button: "Release" },
  adopt: { one: "Adoption of this record asked", many: (count) => `Adoption asked for ${count} records, this one among them`, button: "Adopt" },
  delete: { one: "Removal of this record asked", many: (count) => `Removal asked for ${count} records, this one among them`, button: "Remove" },
  "restore-previous": {
    one: "Restoring the previous version of this record asked",
    many: (count) => `Restoring the previous version asked for ${count} records, this one among them`,
    button: "Remove",
  },
  reverse: { one: "Reversal of a run asked", many: (count) => `Reversal of a run that reached ${count} records, this one among them`, button: "Reverse" },
  restrict: {
    one: "Restricting this record's access asked",
    many: (count) => `Restricting the access of ${count} records asked, this one among them`,
    button: "Remove",
  },
  "lift-restriction": {
    one: "Lifting this record's restriction asked",
    many: (count) => `Lifting the restriction of ${count} records asked, this one among them`,
    button: "Remove",
  },
  verify: { one: "Verify of this record asked", many: (count) => `Verify asked for ${count} records, this one among them`, button: "Verify" },
  "source-remove": {
    one: "Taken out by a removal flow: its source no longer holds the key",
    many: (count) => `Taken out by a removal flow with ${count} records their source no longer holds, this one among them`,
    button: "Deliver again",
  },
  "lift-removal": {
    one: "Lifting this record's removal asked",
    many: (count) => `Lifting the removal of ${count} records asked, this one among them`,
    button: "Deliver again",
  },
};

function activityDetail(activity: DeliveryActivity): ReactNode {
  return (
    <FactGrid>
      <Fact label="Asked by"><Mono>{activity.actor}</Mono></Fact>
      <Fact label="Ended">{activity.completedUtc !== null ? <RelativeTime value={activity.completedUtc} /> : "still running"}</Fact>
      <Fact label="Outcome"><Mono>{activity.outcome}</Mono></Fact>
      {activity.summary !== null && <Fact label="Summary" wide>{activity.summary}</Fact>}
      {activity.parametersJson !== null && (
        <Fact label="Parameters" wide>
          <CodeView value={storedJson(activity.parametersJson)} language="json" height={120} />
        </Fact>
      )}
    </FactGrid>
  );
}

function verifyShape(record: DeliveryRecord): { title: string; tone: Tone; outcome: Outcome } {
  switch (record.lastVerifyOutcome) {
    case "match":
      return { title: "Verified: matches the ledger", tone: "success", outcome: { text: "Verified", tone: "success" } };
    case "drifted":
      return { title: "Verified: drifted from what was delivered", tone: "warning", outcome: { text: "Drifted", tone: "warning" } };
    case "missing":
      return { title: "Verified: no longer in OSDU", tone: "warning", outcome: { text: "Missing from OSDU", tone: "warning" } };
    default:
      return { title: "Verify could not read the record", tone: "muted", outcome: { text: "Verify could not read it", tone: "muted" } };
  }
}

/** The version a verify compared, when no delivery since has moved it. */
function verifiedVersion(record: DeliveryRecord): number | null {
  if (record.targetVersion === null || record.lastVerifiedUtc === null) {
    return null;
  }

  return record.lastDeliveredUtc === null || parseUtc(record.lastVerifiedUtc).getTime() >= parseUtc(record.lastDeliveredUtc).getTime()
    ? record.targetVersion
    : null;
}

/**
 * What someone asked for on this record: a redelivery, a release, a removal or a verify, with who asked, how many records
 * the request named, and what came of it. A verify carries its result, when it is the one whose outcome the record holds,
 * so a request and its answer are one entry. A request that is not a verify sets the record moving, and so opens a
 * chapter.
 */
export function activityEntry(activity: DeliveryActivity, record: DeliveryRecord, result: boolean): Entry {
  const parameters = parametersOf(activity);
  const reach = reachOf(activity, parameters);
  const scope = scopeOf(activity, parameters) ?? reach?.count ?? null;
  const request = REQUESTS[activity.kind];
  const who = actorName(activity.actor);
  const failed = activity.outcome === "failed";
  const many = scope !== null && scope > 1 && request !== undefined
    ? `asked for ${scope} records at once; this page's ${request.button} acts on this record only`
    : null;
  const then: Outcome | null = activity.outcome === "running"
    ? { text: "Still running.", tone: "info" }
    : activity.outcome === "cancelled" ? { text: "Cancelled.", tone: "muted" } : null;

  if (activity.kind === "verify") {
    const shape = result ? verifyShape(record) : null;
    const version = result ? verifiedVersion(record) : null;
    const took = between(activity.startedUtc, activity.completedUtc);
    return {
      ...entryDefaults(),
      id: `activity-${activity.activityId}`,
      at: activity.startedUtc,
      lane: "osdu",
      title: shape?.title ?? "Verified",
      tone: failed ? "destructive" : shape?.tone ?? "muted",
      icon: ShieldCheck,
      actor: activity.actor,
      scope,
      facts: [
        version !== null && <Mono key="v">{`version ${version}`}</Mono>,
        shape === null && !failed && activity.summary,
        many,
        took !== null && `took ${took}`,
        <RunRef key="r" runId={activity.runId} />,
      ],
      then,
      error: failed ? activity.summary : null,
      more: activityDetail(activity),
      order: 5,
      outcome: shape?.outcome ?? null,
      verifies: shape !== null,
      testId: "journey-verified",
    };
  }

  // The record's ledger moved to another pipeline: the one that replaced its own (a renamed pipeline the repository sync
  // paired with it), or the one a person had take over its pipeline's orphaned ledger. The record changed pipelines, not
  // state, so this opens no chapter, and its summary names both pipelines.
  if (activity.kind === "ledger-move") {
    return {
      ...entryDefaults(),
      id: `activity-${activity.activityId}`,
      at: activity.startedUtc,
      lane: "ledger",
      title: activity.actor === "service:repository-sync"
        ? "Moved with its ledger to the pipeline that replaced its own"
        : "Taken over with its ledger by another pipeline",
      tone: failed ? "destructive" : "muted",
      icon: ArrowRightLeft,
      actor: activity.actor,
      facts: [!failed && activity.summary],
      then,
      error: failed ? activity.summary : null,
      more: activityDetail(activity),
      order: 2,
      testId: "journey-ledger-move",
    };
  }

  const title = reach !== null
    ? reach.title
    : request === undefined ? activity.kind : scope !== null && scope > 1 ? request.many(scope) : request.one;
  return {
    ...entryDefaults(),
    id: `activity-${activity.activityId}`,
    at: activity.startedUtc,
    lane: "ledger",
    title,
    tone: failed ? "destructive" : activity.outcome === "running" ? "info" : "muted",
    icon: null,
    actor: activity.actor,
    scope,
    facts: [
      reach?.pattern != null
        ? <span key="p" className="break-words">{`kept blocked by: ${reach.pattern}`}</span>
        : activity.kind === "redeliver" ? redeliveryOf(parameters)
          : activity.kind === "rerender" ? "rendered again, and sent only where it renders differently"
            : reach === null && !failed && activity.summary,
      many,
      <RunRef key="r" runId={activity.runId} />,
      <SubmissionRef key="s" submissionId={activity.submissionId} />,
    ],
    then,
    error: failed ? activity.summary : null,
    more: activityDetail(activity),
    order: 2,
    trigger: request === undefined ? null : `${title.replace(/, this one among them$/, "")} by ${who}`,
    outcome: failed ? { text: `${request?.button ?? activity.kind} failed`, tone: "destructive" } : null,
    testId: `journey-intervention-${activity.kind}`,
  };
}

/** A verify whose request the ledger did not tie to this record (a whole flow's, or a scheduled one): its outcome alone. */
export function verifiedEntry(record: DeliveryRecord, at: string): Entry {
  const shape = verifyShape(record);
  const version = verifiedVersion(record);
  return {
    ...entryDefaults(),
    id: "verified",
    at,
    lane: "osdu",
    title: shape.title,
    tone: shape.tone,
    icon: ShieldCheck,
    facts: [version !== null && <Mono key="v">{`version ${version}`}</Mono>, "in a verify of the flow"],
    order: 5,
    outcome: shape.outcome,
    verifies: true,
    testId: "journey-verified",
  };
}
