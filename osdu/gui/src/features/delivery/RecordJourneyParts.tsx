// The pieces the entries of a record's timeline (RecordJourney) are drawn with: a value in the mono face or clipped
// to its line, a file and row, a flow and its run, an entry's fact line, and a try's steps, undo and ACL.

import type { ReactNode } from "react";
import { Link as RouterLink } from "react-router-dom";
import { TruncatedText } from "@/components/TruncatedText";
import type { DeliveryAttemptAcl, DeliveryAttemptStep, DeliveryChainRun, DeliveryUndoArtifact } from "../../api/delivery";
import { UndoOutcomeBadge } from "./ArtifactMarks";
import { RunRef } from "./DeliveryRefs";
import { NoteMark } from "./RecordArtifacts";
import { runDuration } from "./recordStory";

export function Mono({ children }: { children: ReactNode }) {
  return <span className="font-mono text-[12px]">{children}</span>;
}

/**
 * A value with no length of its own (an OSDU id, a file name, a lease owner, a worker): clipped to the line it sits
 * on, revealed in full on hover, and handed over by its copy button, so a timeline entry stays one line whatever the
 * estate names things.
 */
function LongValue({ value, width = 320, copy = true, title }: { value: string; width?: number; copy?: boolean; title?: string }) {
  // The wrapper carries the cap as a definite width: the clipped span's own cap is relative to its container, which
  // counts for nothing while the line is being measured, so without the wrapper the line would be laid out as if
  // the whole value were showing and leave a blank stretch after the ellipsis. The line itself bounds the cap, so a
  // value in a cell narrower than the cap clips at the cell instead of running under its neighbour.
  return (
    <span className="inline-flex min-w-0 max-w-full" style={{ maxWidth: `min(100%, ${width}px)` }}>
      <TruncatedText text={value} mono maxWidth={width} copy={copy} title={title} />
    </span>
  );
}

export function Origin({ file, row, inCell = false }: { file: string | null; row: number | null; inCell?: boolean }) {
  if (file === null) {
    return <span>the ingestion table (the file is not recorded)</span>;
  }

  if (inCell) {
    // Two tracks, the file's sized by the cell rather than by its content, so the file clips there and the row number
    // keeps its place after it: a content-sized wrapper would size to the whole file name and run out of the cell.
    return (
      <span className="grid max-w-full items-baseline gap-x-1 [grid-template-columns:minmax(0,max-content)_auto]">
        <TruncatedText text={file} mono maxWidth={360} copy title="File" />
        {row !== null && <span>{"row "}<Mono>{row}</Mono></span>}
      </span>
    );
  }

  return (
    <span className="inline-flex min-w-0 max-w-full items-baseline gap-1">
      <LongValue value={file} width={280} title="File" />
      {row !== null && <span className="shrink-0">{"row "}<Mono>{row}</Mono></span>}
    </span>
  );
}

export function FlowRef({ run }: { run: DeliveryChainRun }) {
  return <RouterLink to={`/pipelines/${run.pipelineId}`} className="text-primary hover:underline" onClick={(event) => event.stopPropagation()}>{run.flowName}</RouterLink>;
}

/** The pieces of an entry's fact line, separated so a missing one leaves no dangling separator. */
export function FactLine({ parts }: { parts: ReactNode[] }) {
  const shown = parts.filter((part) => part !== null && part !== undefined && part !== false && part !== "");
  if (shown.length === 0) {
    return null;
  }

  return (
    <span className="flex flex-wrap items-center gap-x-3.5 gap-y-0.5 text-[12px] text-muted-foreground">
      {/* Each part is a flex row of its own, so a clipped value inside it counts at its clipped width: as a plain
          inline the part would size to the whole unclipped string and leave a blank stretch after the ellipsis. */}
      {shown.map((part, index) => <span key={index} className="inline-flex max-w-full flex-wrap items-baseline gap-x-1">{part}</span>)}
    </span>
  );
}

/** The steps of one try, one line each: whether it completed, its name, what the target answered and how long it took. */
export function StepList({ steps }: { steps: DeliveryAttemptStep[] }) {
  return (
    <ul className="flex flex-col gap-1 text-[12px]" data-testid="journey-steps">
      {steps.map((step, index) => {
        const failed = step.error !== undefined;
        const version = step.returned?.version;
        const meta = [
          step.status !== undefined ? `HTTP ${step.status}` : null,
          step.ms !== undefined ? `${step.ms.toLocaleString()} ms` : null,
          version !== undefined ? `version ${version}` : null,
          step.resumed === true ? "completed by an earlier try" : null,
        ].filter((part) => part !== null);
        return (
          <li
            key={`${step.name}-${index}`}
            className="grid grid-cols-[14px_minmax(0,auto)_minmax(0,1fr)] items-baseline gap-x-2"
            title={step.returned !== undefined ? JSON.stringify(step.returned) : undefined}
          >
            <span className={failed ? "text-destructive" : "text-success"}>{failed ? "✕" : "✓"}</span>
            <Mono>{step.name}</Mono>
            <span className="min-w-0 truncate font-mono text-[11.5px] text-muted-foreground">
              {meta.join(" · ")}
              {failed && <span className="text-destructive">{meta.length > 0 ? " · " : ""}{step.error}</span>}
            </span>
          </li>
        );
      })}
    </ul>
  );
}

export function RunFacts({ run, rowsLabel }: { run: DeliveryChainRun; rowsLabel: string }) {
  const duration = runDuration(run.durationSeconds);
  return (
    <span className="flex min-w-0 flex-wrap items-baseline gap-x-2">
      <FlowRef run={run} />
      <RunRef runId={run.runId} />
      <span className="text-muted-foreground">
        <Mono>{run.status}</Mono>
        {run.rows > 0 && `, ${run.rows.toLocaleString()} ${rowsLabel}`}
        {duration !== null && `, took ${duration}`}
      </span>
    </span>
  );
}

/**
 * What an undo did to each artifact of an unfinished delivery, one line each: how the undo settled it, what it is, its OSDU
 * id or what finds it, the versions it names, and what the route answered, behind a note mark with its copy.
 */
export function UndoList({ artifacts, keptRecord }: { artifacts: DeliveryUndoArtifact[]; keptRecord: boolean }) {
  return (
    <div className="flex flex-col gap-1" data-testid="journey-undo">
      {keptRecord && <span className="text-[12px] text-muted-foreground">The record itself was left to the newer work, which writes it again.</span>}
      <ul className="flex flex-col gap-1 text-[12px]">
        {artifacts.map((artifact) => (
          <li
            key={artifact.artifactId}
            className="grid grid-cols-[minmax(0,auto)_minmax(0,auto)_minmax(0,1fr)_auto] items-center gap-x-2"
            data-testid="journey-undo-artifact"
          >
            <UndoOutcomeBadge outcome={artifact.outcome} />
            <span className="whitespace-nowrap">{artifact.role}</span>
            <span className="inline-flex min-w-0 items-baseline gap-1.5">
              <TruncatedText text={artifact.targetId ?? artifact.locator ?? artifact.slot} mono maxWidth={420} copy title={artifact.targetId !== undefined ? "OSDU id" : "Found by"} />
              {artifact.version !== undefined && <Mono>{`v${artifact.version}`}</Mono>}
              {artifact.priorVersion !== undefined && <span className="text-muted-foreground"><Mono>{`replaced v${artifact.priorVersion}`}</Mono></span>}
            </span>
            <NoteMark note={artifact.note} testId={`journey-undo-note-${artifact.artifactId}`} />
          </li>
        ))}
      </ul>
    </div>
  );
}

/** An ACL in an attempt's detail: its viewers and its owners, each group in full. */
export function AclFact({ acl }: { acl: DeliveryAttemptAcl }) {
  return (
    <span className="flex flex-col gap-0.5" data-testid="journey-acl">
      <span><span className="text-muted-foreground">viewers </span><Mono>{acl.viewers.join(", ")}</Mono></span>
      <span><span className="text-muted-foreground">owners </span><Mono>{acl.owners.join(", ")}</Mono></span>
      {(acl.beside ?? []).map((group) => (
        <span key={group.ids.join(",")} data-testid="journey-acl-beside">
          <span className="text-muted-foreground">{group.viewers !== undefined && group.owners !== undefined ? "beside it, with its own " : "beside it, the same "}</span>
          <Mono>{group.ids.join(", ")}</Mono>
          {group.viewers !== undefined && group.owners !== undefined && (
            <span className="text-muted-foreground">{` (viewers ${group.viewers.join(", ")}; owners ${group.owners.join(", ")})`}</span>
          )}
        </span>
      ))}
    </span>
  );
}
