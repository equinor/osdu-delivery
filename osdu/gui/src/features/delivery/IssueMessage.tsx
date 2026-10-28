import { cn } from "@/lib/utils";
import { issueHeadline, issueHolds, issueParts, issueSteps } from "./issueText";

/**
 * Words of an issue as its records share them, each placeholder a quiet chip naming what each record says there of its
 * own (a value, a number, a moment), so where the records differ reads at a glance.
 */
function IssueWords({ text }: { text: string }) {
  return (
    <>
      {issueParts(text).map((part, index) => (part.kind === "text"
        ? <span key={index}>{part.text}</span>
        : (
          <span
            key={index}
            className="mx-px rounded border border-border bg-muted px-1 font-mono text-[11px] text-muted-foreground"
            title={`Here each record names ${part.meaning} of its own`}
            data-testid="issue-placeholder"
          >
            {part.name}
          </span>
        )))}
    </>
  );
}

/**
 * An issue in a grid's row: the property its error names first, as the record's own path, and how far through a list it
 * reaches, over what it says of it on one line; an error that names no property first takes both lines. How many more
 * reasons the error joins is said beside the property; the whole of it is in the panel.
 */
export function IssueSummary({ text }: { text: string }) {
  const holds = issueHolds(text);
  const { property, scope, reason } = issueHeadline(holds[0]);
  const more = holds.length > 1 ? <span className="shrink-0 text-[11.5px] text-muted-foreground">{`and ${holds.length - 1} more`}</span> : null;
  if (property === null) {
    return (
      <span className="flex min-w-0 flex-col gap-0.5" data-testid="issue-summary">
        <span className="line-clamp-2 whitespace-normal break-words text-[13px]"><IssueWords text={reason} /></span>
        {more}
      </span>
    );
  }

  return (
    <span className="flex min-w-0 flex-col" data-testid="issue-summary">
      <span className="flex min-w-0 items-baseline gap-2">
        <span className="min-w-0 truncate font-mono text-[12.5px] font-medium text-foreground" data-testid="issue-property">{property}</span>
        {scope !== null && <span className="shrink-0 text-[11.5px] text-muted-foreground"><IssueWords text={scope} /></span>}
        {more}
      </span>
      <span className="truncate text-[12.5px] text-muted-foreground" data-testid="issue-reason"><IssueWords text={reason} /></span>
    </span>
  );
}

/**
 * The whole of an issue's error: each reason it joins under the property it names, and a reason ending in what each of
 * several alternatives said (a `$coalesce`'s) as a numbered list. Compact, it takes the size of the facts beside it.
 */
export function IssueMessage({ text, compact = false }: { text: string; compact?: boolean }) {
  const body = compact ? "text-[12px] leading-5" : "text-[13px] leading-relaxed";
  return (
    <div className={cn("flex min-w-0 flex-col", compact ? "gap-1.5" : "gap-2.5")} data-testid="issue-message">
      {issueHolds(text).map((hold, index) => {
        const { property, scope, reason } = issueHeadline(hold);
        const steps = issueSteps(reason);
        return (
          <div key={index} className={cn("flex min-w-0 flex-col", !compact && "gap-1")} data-testid="issue-hold">
            {property !== null && (
              <span className="flex min-w-0 flex-wrap items-baseline gap-x-2">
                <span className={cn("break-all font-mono font-medium text-foreground", compact ? "text-[12px]" : "text-[13px]")}>{property}</span>
                {scope !== null && <span className={cn("text-muted-foreground", compact ? "text-[11.5px]" : "text-[12px]")}><IssueWords text={scope} /></span>}
              </span>
            )}
            <span className={cn("whitespace-pre-wrap break-words", body)}>
              <IssueWords text={steps === null ? reason : `${steps.lead}:`} />
            </span>
            {steps !== null && (
              <ol className={cn("flex flex-col pl-1", compact ? "gap-0" : "gap-0.5", body)} data-testid="issue-steps">
                {steps.steps.map((step, at) => (
                  <li key={at} className="flex min-w-0 gap-2">
                    <span className={cn("w-4 shrink-0 text-right font-mono text-[11.5px] text-muted-foreground", compact ? "leading-5" : "leading-[1.6rem]")}>{at + 1}</span>
                    <span className="min-w-0 break-words"><IssueWords text={step} /></span>
                  </li>
                ))}
              </ol>
            )}
          </div>
        );
      })}
    </div>
  );
}
