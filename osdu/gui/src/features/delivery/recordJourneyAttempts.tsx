// The entries of a record's timeline (RecordJourney) for its delivery tries: what each try, undo, wait or reversal did
// and left the record in, with its steps, and tries that ended alike folded into one entry.

import {
  CheckCircle2, CircleDashed, Eraser, FileQuestion, Hourglass, Link2, Lock, LockOpen, PauseCircle, RefreshCw, Trash2, Undo2,
  XCircle, type LucideIcon,
} from "lucide-react";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import type { DeliveryAttempt } from "../../api/delivery";
import { verdictIn, type ValidationVerdict } from "../../api/validation";
import { RunRef, SubmissionRef } from "./DeliveryRefs";
import { Fact, FactGrid, NoFact } from "./Facts";
import { RecordName } from "./RecordName";
import { ValidationVerdictView } from "./ValidationVerdictView";
import { outcomeWord } from "./validationModel";
import { DeliveryReasonView } from "./DeliveryReasonView";
import { deliveredTitle, NOT_RECORDED, recordedReason, unchangedTitle } from "./deliveryReason";
import { reachedTarget, refusalOf, setAside, statusText, urlPath, type Refusal } from "./recordStory";
import { between, clockOf, type Entry, entryDefaults, type Lane, type Outcome, type Tone } from "./recordJourneyEntry";
import { AclFact, FactLine, Mono, Origin, StepList, UndoList } from "./RecordJourneyParts";

const PHASE_TEXT: Record<string, string> = {
  metadata: "record only",
  payload: "payload only",
  "metadata+payload": "record and payload",
  "source-removed": "a removal flow found its source no longer holds the key",
  restrict: "access restricted in OSDU, no new version",
  "lift-restriction": "access given back in OSDU, no new version",
};

/** Why an undo ran, as its attempt's result names the reason. */
const UNDO_REASON: Record<string, string> = {
  held: "after a try that held",
  failed: "after the delivery used its tries",
  abandoned: "newer work replaced the delivery",
  removed: "before the record was removed",
};

function attemptShape(attempt: DeliveryAttempt, refusal: Refusal | null, reached: boolean): {
  title: string; tone: Tone; icon: LucideIcon; then: Outcome | null; outcome: Outcome | null;
} {
  const waits = "the record waits for someone to release or redeliver it";

  // What a reversal did to the record: put back as OSDU held it before the run it reversed, passed over, or not yet.
  if (attempt.phase === "reverse") {
    switch (attempt.outcome) {
      case "restored":
        return {
          title: "Reverted to the version OSDU held before the reversed run",
          tone: "muted",
          icon: Undo2,
          then: { text: "Blocked until its source changes or someone releases it.", tone: "muted" },
          outcome: { text: "Reverted", tone: "muted" },
        };
      case "deleted":
        return {
          title: "Removed by a reversal: the reversed run created it",
          tone: "muted",
          icon: Undo2,
          then: { text: "Blocked until its source changes or someone releases it.", tone: "muted" },
          outcome: { text: "Removed by a reversal", tone: "muted" },
        };
      case "failed":
        return {
          title: "A reversal could not put it back",
          tone: "destructive",
          icon: XCircle,
          then: { text: "Taken again when the reversal is asked again.", tone: "muted" },
          outcome: null,
        };
      default:
        return { title: "A reversal passed it over", tone: "muted", icon: Undo2, then: null, outcome: null };
    }
  }

  // What an unfinished delivery left in OSDU, undone: an undo decides nothing about the record, so it settles no chapter.
  if (attempt.outcome === "undone") {
    const left = (attempt.result?.undo?.artifacts ?? []).filter((artifact) => artifact.outcome === "failed").length;
    const summary = attempt.result?.undo?.summary;
    return {
      title: left > 0
        ? `Undo of an unfinished delivery: ${summary ?? `${left} not undone yet`}`
        : `Unfinished delivery undone${summary !== undefined ? `: ${summary}` : ""}`,
      tone: left > 0 ? "warning" : "muted",
      icon: Eraser,
      then: left > 0
        ? { text: "What could not be undone yet is tried again by the sweep; newer work for the record waits for it.", tone: "muted" }
        : null,
      outcome: null,
    };
  }

  // A record whose queued document was not there to read when its turn came: nothing about its data is wrong, it holds no
  // problem, and it is planned again from its row without anyone releasing it.
  if (setAside(attempt)) {
    return {
      title: "Set aside: its queued document could not be read where it was sent from",
      tone: "muted",
      icon: RefreshCw,
      then: { text: "Not blocked: it is planned again from its row, by the run that found it or by the flow's next run.", tone: "muted" },
      outcome: { text: "Set aside to be planned again", tone: "muted" },
    };
  }

  // Newer work that was not sent, or a record not delivered again, because what an earlier delivery left is not undone yet.
  if (attempt.phase === "undo-wait") {
    return attempt.outcome === "held"
      ? {
        title: "Held: what an earlier delivery left in OSDU is not undone yet",
        tone: "warning",
        icon: PauseCircle,
        then: { text: "Once what stops the undo is fixed, a release tries the undo again and then goes on.", tone: "warning" },
        outcome: { text: "Held: waits for an undo", tone: "warning" },
      }
      : {
        title: "Waited for an earlier delivery's undo",
        tone: "muted",
        icon: Hourglass,
        then: { text: "Nothing was sent and the try was not charged; the newer work goes once the undo lands.", tone: "muted" },
        outcome: null,
      };
  }

  // The record took over the record OSDU already held at the id its key makes, written by another system or another
  // instance of the delivery, once an operator asked: a run's intake adopts it and goes on to write the row there, while
  // an adoption made at the operator's click, before adoption was asked of a run, left the write to the next run.
  // Nothing was sent.
  if (attempt.phase === "adopt") {
    return {
      title: "Adopted the record OSDU already held",
      tone: "info",
      icon: Link2,
      then: { text: attempt.worker === "intake" ? "This run writes the row there." : "The next deliver run writes this row there.", tone: "muted" },
      outcome: null,
    };
  }

  // An operator replaced the record's ACL in OSDU with a registered restriction's groups, or gave it back the ACL it had.
  if (attempt.outcome === "restricted") {
    const name = attempt.result?.restriction?.name;
    return {
      title: name !== undefined ? `Access restricted under ${name}` : "Access restricted",
      tone: "muted",
      icon: Lock,
      then: { text: "No run delivers it, and no release sends it, until the restriction is lifted.", tone: "muted" },
      outcome: { text: "Access restricted", tone: "muted" },
    };
  }

  if (attempt.outcome === "unrestricted") {
    const name = attempt.result?.restriction?.name;
    return {
      title: name !== undefined ? `Restriction ${name} lifted` : "Restriction lifted",
      tone: "muted",
      icon: LockOpen,
      then: { text: "It follows its source again: the next run plans it.", tone: "muted" },
      outcome: null,
    };
  }

  // An operator took the latest version out of being current: the version before it written back as a new version.
  if (attempt.phase === "restore-previous" && attempt.outcome === "restored") {
    return {
      title: "Previous version restored",
      tone: "muted",
      icon: Undo2,
      then: { text: "Blocked until its source changes or someone releases it.", tone: "muted" },
      outcome: { text: "Reverted", tone: "muted" },
    };
  }

  switch (attempt.outcome) {
    case "delivered":
      // What sent it, in the words its reason keeps; a try delivered before the ledger kept reasons is titled as it was.
      return { title: deliveredTitle(attempt.reason), tone: "success", icon: CheckCircle2, then: null, outcome: { text: "Delivered", tone: "success" } };
    case "failed": {
      const final = attempt.error?.startsWith("failed after") === true;
      return {
        title: refusal !== null ? `Failed: ${statusText(refusal)}` : "Failed",
        tone: "destructive",
        icon: XCircle,
        then: final ? { text: `No tries left: ${waits}.`, tone: "destructive" } : { text: "It will be tried again.", tone: "muted" },
        outcome: final ? { text: "Failed", tone: "destructive" } : { text: "Failed, to be tried again", tone: "warning" },
      };
    }
    case "held":
      if (attempt.phase === "source-deleted") {
        return { title: "Held: the row was deleted, so it is not delivered", tone: "warning", icon: PauseCircle, then: null, outcome: { text: "Held: the row was deleted", tone: "warning" } };
      }

      return refusal !== null && reached
        ? {
          title: `Refused: ${statusText(refusal)}`,
          tone: "destructive",
          icon: XCircle,
          then: { text: `Held: a ${refusal.status} is not retried, so ${waits}.`, tone: "warning" },
          outcome: { text: `Held: refused with ${refusal.status}`, tone: "warning" },
        }
        : { title: "Held", tone: "warning", icon: PauseCircle, then: { text: "Not tried again until someone releases it or its row changes.", tone: "warning" }, outcome: { text: "Held", tone: "warning" } };
    case "skipped":
      // A held, failed or reverted record a plan found OSDU already holding the version of: delivered again, nothing sent.
      if (attempt.result?.settled === true) {
        return attempt.phase === "stale"
          ? { title: "Delivered again: OSDU holds a newer version than the row", tone: "success", icon: CheckCircle2, then: null, outcome: { text: "Delivered again", tone: "success" } }
          : { title: "Delivered again: OSDU already holds what the row renders", tone: "success", icon: CheckCircle2, then: null, outcome: { text: "Delivered again", tone: "success" } };
      }

      switch (attempt.phase) {
        case "source-removed":
          return {
            title: "Settled: its source no longer holds the key, and OSDU did not hold the record",
            tone: "muted",
            icon: Trash2,
            then: { text: "Kept out: a row of the key newer than its removal is delivered again.", tone: "muted" },
            outcome: { text: "Removed by its source", tone: "muted" },
          };
        case "source-missing":
          return { title: "Not found in the ingestion table", tone: "warning", icon: FileQuestion, then: { text: "The row is gone; the record keeps its status.", tone: "muted" }, outcome: null };
        case "identical":
          // What moved to render it, when its attempt says (a mapping, a cache, a request): the record saw the change.
          return { title: unchangedTitle(attempt.reason, "Nothing to send: the row renders what was delivered"), tone: "muted", icon: CircleDashed, then: null, outcome: { text: "Nothing to send", tone: "muted" } };
        case "unchanged":
          return { title: "Nothing to send: already delivered", tone: "muted", icon: CircleDashed, then: { text: "OSDU already held this version when it was its turn to be sent.", tone: "muted" }, outcome: { text: "Nothing to send", tone: "muted" } };
        case "stale":
          return { title: "Skipped: an older version of the row", tone: "muted", icon: CircleDashed, then: null, outcome: { text: "Skipped: older version", tone: "muted" } };
        default:
          return { title: "Skipped", tone: "muted", icon: CircleDashed, then: null, outcome: { text: "Skipped", tone: "muted" } };
      }
    case "deleted":
      return attempt.phase === "source-removed"
        ? {
          title: "Removed from OSDU: its source no longer holds the key",
          tone: "muted",
          icon: Trash2,
          then: { text: "Kept out: a row of the key newer than its removal is delivered again.", tone: "muted" },
          outcome: { text: "Removed by its source", tone: "muted" },
        }
        : { title: "Removed", tone: "muted", icon: Trash2, then: null, outcome: { text: "Removed", tone: "muted" } };
    case "historypurged":
      return { title: "Earlier versions purged", tone: "warning", icon: Eraser, then: null, outcome: { text: "Earlier versions purged", tone: "warning" } };
    case "restored":
      return { title: "An earlier version written back", tone: "muted", icon: Undo2, then: null, outcome: { text: "Reverted", tone: "muted" } };
    default:
      return { title: attempt.outcome, tone: "muted", icon: CircleDashed, then: null, outcome: null };
  }
}

/** The error a try stored, without what differs from one try to the next, so tries that failed alike compare equal. */
function errorShape(error: string | null): string {
  return (error ?? "").replace(/\(correlation-id [^)]*\)/g, "").replace(/\s+/g, " ").trim();
}

/**
 * What the check before sending found, in a few words: the outcome, and how many judgements of the mapping's assertions
 * failed when any did.
 */
function verdictFact(verdict: ValidationVerdict): string {
  const failed = verdict.assertions?.failed ?? 0;
  return failed > 0
    ? `checked ${outcomeWord(verdict.outcome)}, ${failed.toLocaleString("en-US")} assertion failure${failed === 1 ? "" : "s"}`
    : `checked ${outcomeWord(verdict.outcome)}`;
}

export function attemptEntry(attempt: DeliveryAttempt): Entry {
  const steps = attempt.result?.steps ?? [];
  const verdict = verdictIn(attempt.result?.validation);
  const correlationId = attempt.result?.correlationId;
  const undo = attempt.outcome === "undone" ? attempt.result?.undo : undefined;
  // An undo's error lists what it could not take back, and a wait's names what it waits for; neither is a request OSDU refused.
  const refusal = attempt.outcome === "undone" || attempt.phase === "undo-wait" ? null : refusalOf(attempt.error);
  const reached = reachedTarget(attempt);
  const shape = attemptShape(attempt, refusal, reached);
  const took = between(attempt.startedUtc, attempt.completedUtc);
  const lane: Lane = attempt.phase === "source-missing" ? "source" : reached ? "osdu" : "ledger";
  const request = refusal?.method != null && refusal.url !== null ? `${refusal.method} ${urlPath(refusal.url)}` : null;
  const version = attempt.targetVersion !== null && attempt.outcome === "delivered" ? attempt.targetVersion : null;
  const adopted = attempt.phase === "adopt" ? attempt.result?.adopted : undefined;
  const restriction = attempt.result?.restriction;
  return {
    ...entryDefaults(),
    id: `attempt-${attempt.attemptId}`,
    at: attempt.startedUtc,
    lane,
    title: shape.title,
    tone: shape.tone,
    icon: shape.icon,
    facts: reached
      ? [
        // A delivery made before the ledger kept reasons says so rather than leaving its title to be read as complete.
        attempt.outcome === "delivered" && recordedReason(attempt.reason) === null && <span key="w" data-testid="journey-reason-missing">{NOT_RECORDED}</span>,
        undo !== undefined ? UNDO_REASON[undo.reason ?? ""] : PHASE_TEXT[attempt.phase],
        request !== null && <Mono key="q">{request}</Mono>,
        took !== null && `took ${took}`,
        version !== null && <Mono key="v">{`version ${version}`}</Mono>,
        <RunRef key="r" runId={attempt.runId} />,
        <SubmissionRef key="s" submissionId={attempt.submissionId} />,
      ]
      : [
        // What an adoption took over: the OSDU id and the version OSDU held there.
        adopted !== undefined && <RecordName key="a" id={adopted.targetId} className="max-w-[320px]" />,
        adopted?.version != null && <Mono key="av">{`version ${adopted.version}`}</Mono>,
        attempt.sourceFileName !== null && <Origin key="o" file={attempt.sourceFileName} row={attempt.sourceRowNumber} />,
        verdict !== null && verdictFact(verdict),
        <RunRef key="r" runId={attempt.runId} />,
        <SubmissionRef key="s" submissionId={attempt.submissionId} />,
      ],
    then: shape.then,
    // A refusal's facts already say what was asked and what came back; what OSDU said about it is the error to read.
    error: refusal?.answer ?? attempt.error,
    more: (
      <div className="flex flex-col gap-2.5">
        {attempt.outcome === "delivered" && <DeliveryReasonView reason={attempt.reason} />}
        {attempt.outcome !== "delivered" && recordedReason(attempt.reason) !== null && (
          <DeliveryReasonView reason={attempt.reason} heading={attempt.phase === "unchanged" ? "Why it was planned" : "What moved"} />
        )}
        {steps.length > 0 && <StepList steps={steps} />}
        {undo?.artifacts !== undefined && undo.artifacts.length > 0 && <UndoList artifacts={undo.artifacts} keptRecord={undo.keptRecord === true} />}
        {verdict !== null && <ValidationVerdictView verdict={verdict} />}
        <FactGrid>
          {refusal?.url != null && <Fact label="Request" wide><Mono>{`${refusal.method ?? ""} ${refusal.url}`}</Mono></Fact>}
          {attempt.error !== null && <Fact label="Error" wide><span className="text-destructive">{attempt.error}</span></Fact>}
          {attempt.result?.detail !== undefined && <Fact label="Note" wide>{attempt.result.detail}</Fact>}
          {restriction !== undefined && <Fact label="Restriction" wide>{restriction.name}</Fact>}
          {restriction?.acl != null && (
            <Fact label={attempt.outcome === "restricted" ? "Access set" : "Access given back"} wide><AclFact acl={restriction.acl} /></Fact>
          )}
          {restriction?.replaced != null && (
            <Fact label={attempt.outcome === "restricted" ? "Access replaced" : "Restriction's access"} wide><AclFact acl={restriction.replaced} /></Fact>
          )}
          <Fact label="Correlation id">{correlationId !== undefined ? <TruncatedText text={correlationId} mono maxWidth={360} copy title="Correlation id" /> : <NoFact />}</Fact>
          <Fact label="Built from"><Origin file={attempt.sourceFileName} row={attempt.sourceRowNumber} inCell /></Fact>
          <Fact label="Row stamped"><RelativeTime value={attempt.sourceUpdatedUtc} /></Fact>
          {attempt.sourceDeletedUtc != null && <Fact label="Row deleted"><RelativeTime value={attempt.sourceDeletedUtc} /></Fact>}
          <Fact label="OSDU version">{attempt.targetVersion !== null ? <Mono>{attempt.targetVersion}</Mono> : <NoFact />}</Fact>
          {/* A record of a linked template: the version of its parent the document this try sent was rendered from. */}
          {attempt.parentVersion != null && <Fact label="Parent version"><Mono>{attempt.parentVersion}</Mono></Fact>}
          <Fact label="Work batch">{attempt.workBatch !== null ? <Mono>{attempt.workBatch}</Mono> : <NoFact />}</Fact>
          <Fact label="Worker"><TruncatedText text={attempt.worker} mono maxWidth={360} title="Worker" /></Fact>
          <Fact label="Completed"><RelativeTime value={attempt.completedUtc} /></Fact>
          <Fact label="Run">{attempt.runId !== null ? <TruncatedText text={attempt.runId.toLowerCase()} mono maxWidth={360} copy title="Run" /> : <NoFact />}</Fact>
          <Fact label="Submission">{attempt.submissionId !== null ? <TruncatedText text={attempt.submissionId.toLowerCase()} mono maxWidth={360} copy title="Submission" /> : <NoFact />}</Fact>
          <Fact label="Metadata hash">{attempt.metadataHash !== null ? <TruncatedText text={attempt.metadataHash} mono maxWidth={360} copy title="Metadata hash" /> : <NoFact />}</Fact>
          <Fact label="Payload hash">{attempt.payloadHash !== null ? <TruncatedText text={attempt.payloadHash} mono maxWidth={360} copy title="Payload hash" /> : <NoFact />}</Fact>
        </FactGrid>
      </div>
    ),
    order: 4,
    outcome: shape.outcome,
    foldKey: attempt.outcome === "failed" || attempt.outcome === "held" || attempt.outcome === "skipped" || attempt.outcome === "undone"
      ? `${attempt.outcome}|${attempt.phase}|${lane}|${errorShape(attempt.error)}`
      : null,
    testId: `journey-attempt-${attempt.outcome}`,
  };
}

/** Tries of this length or more that ended alike, one after another, read as one entry that opens to each of them. */
const FOLD_AT = 3;

export function foldRepeats(entries: Entry[]): Entry[] {
  const folded: Entry[] = [];
  let index = 0;
  while (index < entries.length) {
    const first = entries[index];
    let end = index + 1;
    while (first.foldKey !== null && end < entries.length && entries[end].foldKey === first.foldKey) {
      end += 1;
    }

    const run = entries.slice(index, end);
    if (run.length < FOLD_AT) {
      folded.push(...run);
    } else {
      const latest = run[run.length - 1];
      folded.push({
        ...latest,
        id: `fold-${first.id}`,
        title: `${latest.title}, ${run.length} times`,
        facts: [`from ${clockOf(first.at)} to ${clockOf(latest.at)}`, ...latest.facts.slice(-2)],
        more: (
          <div className="flex flex-col gap-2.5">
            <ul className="flex flex-col gap-0.5 text-[12px] text-muted-foreground" data-testid="journey-folded-tries">
              {run.map((entry) => (
                <li key={entry.id} className="flex flex-wrap items-baseline gap-x-3">
                  <RelativeTime value={entry.at} />
                  <FactLine parts={entry.facts} />
                </li>
              ))}
            </ul>
            <span className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">The latest try</span>
            {latest.more}
          </div>
        ),
      });
    }

    index = end;
  }

  return folded;
}
