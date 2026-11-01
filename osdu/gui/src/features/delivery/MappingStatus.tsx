import type { ReactNode } from "react";
import { CircleAlert, Info, Loader2, type LucideIcon } from "lucide-react";
import { cn } from "@/lib/utils";
import { verdictSentence, verdictVisual } from "./dryRun";
import { ProblemView } from "./TemplateSheet";
import type { ValueCheckSession } from "./useValueCheck";
import { flowLabel, formatCount } from "./valueCheck";

/** One line of what the page is doing or came to: a glyph in its tone and what it says. */
function Line({ icon: Icon, tone, spin = false, testId, children }: { icon: LucideIcon; tone?: string; spin?: boolean; testId: string; children: ReactNode }) {
  return (
    <p className="flex min-w-0 items-start gap-1.5 text-[12px]" data-testid={testId}>
      <Icon className={cn("mt-0.5 size-3.5 shrink-0", tone, spin && "animate-spin")} aria-hidden />
      {children}
    </p>
  );
}

/**
 * The one line under a mapping's toolbar, over both its views, and only when there is something to say: the dry run under way,
 * why it failed, why the rows cannot be read, that the scope moved since the last run, or whether the rows can be delivered.
 */
export function MappingStatus({ session }: { session: ValueCheckSession }) {
  const { flow, dryRun } = session;
  if (session.flowsError !== null) {
    return <ProblemView error={session.flowsError} testId="value-check-flows-error" />;
  }

  if (session.running > 0) {
    const reading = session.maxRows === 0 ? "every row of the scope" : `up to ${formatCount(session.maxRows)} rows`;
    return (
      <Line icon={Loader2} tone="text-muted-foreground" spin testId="dry-run-progress">
        <span className="text-muted-foreground">{`Rendering ${reading} of ${flow === null ? "the flow" : flowLabel(flow)} on a node. Nothing is sent.`}</span>
      </Line>
    );
  }

  if (session.failure !== null) {
    return (
      <Line icon={CircleAlert} tone="text-destructive" testId="dry-run-failed">
        <span className="whitespace-pre-wrap text-destructive">{`The dry run could not be made: ${session.failure}`}</span>
      </Line>
    );
  }

  // With no flow the records' own side says why, in full; here it would be said twice.
  if (flow !== null && session.blocked !== null) {
    return (
      <Line icon={Info} tone="text-muted-foreground" testId="staged-blocked">
        <span className="text-muted-foreground">{session.blocked}</span>
      </Line>
    );
  }

  if (dryRun === null) {
    return null;
  }

  if (session.dryRunStale) {
    return (
      <Line icon={Info} tone="text-muted-foreground" testId="dry-run-stale">
        <span className="text-muted-foreground">The scope's values changed since the last dry run, so its findings are not shown. Run it again for these rows.</span>
      </Line>
    );
  }

  const visual = verdictVisual(dryRun.run);
  const { rows } = dryRun.meta;
  return (
    <div data-testid="dry-run-summary">
      <Line icon={visual.icon} tone={visual.tone} testId="dry-run-verdict">
        <span>
          <span className="font-medium">{verdictSentence(dryRun.run)}</span>
          <span className="ml-2 text-muted-foreground">
            {rows.complete ? "The whole scope. " : `The first ${formatCount(rows.read)} of about ${formatCount(rows.scopeRecords ?? rows.read)} rows. `}
            Nothing was sent.
          </span>
        </span>
      </Line>
    </div>
  );
}
