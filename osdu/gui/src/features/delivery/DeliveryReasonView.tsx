import { RelativeTime } from "@/components/RelativeTime";
import type { DeliveryAttemptReason } from "../../api/delivery";
import { NoteMark } from "./RecordArtifacts";
import { NOT_RECORDED, pathsHeading, recordedReason, triggerLabel, triggerNames, triggerSides } from "./deliveryReason";

/**
 * Why a delivered try was sent, in its opened timeline entry (osdu/docs/reference/concepts/delivery-reasons.md): every
 * input that moved, one line each with what it was and is now, its explanation behind a note mark, and the paths of the
 * document that changed. A try delivered before the ledger kept reasons says so, and nothing more.
 */
export function DeliveryReasonView({ reason }: { reason: DeliveryAttemptReason | null | undefined }) {
  const recorded = recordedReason(reason);
  if (recorded === null) {
    return <span className="text-[12px] text-muted-foreground" data-testid="journey-reason-not-recorded">{`Why it was sent: ${NOT_RECORDED}`}</span>;
  }

  const paths = recorded.paths;
  const listed = paths?.listed ?? [];
  return (
    <div className="flex flex-col gap-1.5" data-testid="journey-reason">
      <span className="text-[12px] font-medium">Why it was sent</span>
      <ul className="flex flex-col gap-1 text-[12px]">
        {recorded.triggers.map((trigger, index) => {
          const { was, now } = triggerSides(trigger);
          const names = triggerNames(trigger);
          return (
            <li
              key={`${trigger.kind}-${index}`}
              className="flex min-w-0 flex-wrap items-center gap-x-2"
              data-testid="journey-reason-trigger"
              data-kind={trigger.kind}
            >
              <span className="whitespace-nowrap">{triggerLabel(trigger.kind)}</span>
              {(was !== null || now !== null) && (
                <span className="inline-flex min-w-0 flex-wrap items-baseline gap-x-1.5 text-muted-foreground">
                  {was !== null && <span className="font-mono text-[11.5px]" data-testid="journey-reason-was">{was}</span>}
                  {was !== null && now !== null && <span aria-label="to">→</span>}
                  {now !== null && <span className="font-mono text-[11.5px] text-foreground" data-testid="journey-reason-now">{now}</span>}
                </span>
              )}
              {names !== null && <span className="text-muted-foreground" data-testid="journey-reason-names">{names}</span>}
              {trigger.at !== undefined && <span className="text-muted-foreground"><RelativeTime value={trigger.at} /></span>}
              {trigger.detail !== undefined && <NoteMark note={trigger.detail} title="What moved" testId={`journey-reason-note-${index}`} />}
            </li>
          );
        })}
      </ul>
      {paths !== undefined && (
        <div className="flex flex-col gap-0.5 text-[12px]" data-testid="journey-reason-paths">
          <span className={paths.compared ? undefined : "text-muted-foreground"}>{pathsHeading(paths)}</span>
          {listed.length > 0 && (
            <ul className="flex flex-col gap-0.5">
              {listed.map((change) => (
                <li key={change.path} className="flex min-w-0 items-baseline gap-2" data-testid="journey-reason-path" data-change={change.change}>
                  <span className="min-w-0 truncate font-mono text-[11.5px]" title={change.path}>{change.path}</span>
                  <span className="text-muted-foreground">{change.change}</span>
                </li>
              ))}
            </ul>
          )}
          {(paths.more ?? 0) > 0 && <span className="text-muted-foreground">{`and ${(paths.more ?? 0).toLocaleString("en-US")} more`}</span>}
        </div>
      )}
    </div>
  );
}
