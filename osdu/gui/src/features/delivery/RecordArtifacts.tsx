import { StickyNote } from "lucide-react";
import type { UseQueryResult } from "@tanstack/react-query";
import { isApiError } from "@/api/client";
import { CopyButton } from "@/components/CopyButton";
import { CorrelationError } from "@/components/CorrelationError";
import { DataTable, type Column } from "@/components/DataTable";
import { RichTooltip } from "@/components/RichTooltip";
import { TruncatedText } from "@/components/TruncatedText";
import { ARTIFACT_STATES, type DeliveryArtifact } from "../../api/artifacts";
import { ArtifactStateBadge, ArtifactStateCount } from "./ArtifactMarks";
import { DimensionGrid } from "./dimensions/DimensionGrid";
import { CompactTime } from "./RecordCells";
import { shortId } from "./idTail";

/** How many artifacts the record page reads; a record with more says so. */
export const RECORD_ARTIFACTS_SHOWN = 500;

/** Who settled an artifact, as the ledger names them without the prefix of a signed-in user. */
function actorName(actor: string): string {
  return actor.startsWith("user:") ? actor.slice("user:".length) : actor;
}

/** A remark reduced to a glyph, the whole of it in a hover panel, and a copy button beside it; a dash for none. */
export function NoteMark({ note, title = "Note", testId }: { note: string | null | undefined; title?: string; testId: string }) {
  if (note === null || note === undefined || note === "") {
    return <span className="text-muted-foreground">-</span>;
  }

  return (
    <span className="inline-flex items-center gap-0.5" data-testid={testId}>
      <RichTooltip body={note} title={title}>
        <span className="inline-flex size-6 items-center justify-center text-muted-foreground">
          <StickyNote className="size-4" aria-label={title} />
        </span>
      </RichTooltip>
      <CopyButton label={`Copy the ${title.toLowerCase()}`} text={note} testId={`${testId}-copy`} iconOnly />
    </span>
  );
}

/**
 * What else names an artifact, under its id: what finds it when its id is not known (a landing-zone path), the version its
 * delivery wrote, and the version that write replaced, which an undo writes back.
 */
function Beside({ artifact }: { artifact: DeliveryArtifact }) {
  const versions = [
    artifact.version !== null ? `wrote ${artifact.version}` : null,
    artifact.priorVersion !== null ? `replaced ${artifact.priorVersion}` : null,
  ].filter((part) => part !== null).join(" · ");
  const text = [artifact.targetId !== null ? artifact.locator : null, versions === "" ? null : versions].filter((part) => part !== null && part !== "").join(" · ");
  return text === "" ? null : <TruncatedText text={text} mono maxWidth={560} className="text-[11.5px] text-muted-foreground" title="Found by, and the versions" />;
}

/** Where an open artifact's undo stands, or when and by whom it was settled. */
function Settlement({ artifact }: { artifact: DeliveryArtifact }) {
  if (artifact.settledUtc !== null) {
    return (
      <span className="inline-flex min-w-0 flex-col leading-tight">
        <CompactTime value={artifact.settledUtc} caption="Settled" className="text-[12px]" />
        {artifact.settledBy !== null && <TruncatedText text={actorName(artifact.settledBy)} maxWidth={160} className="text-[11.5px] text-muted-foreground" title="Settled by" />}
      </span>
    );
  }

  if (artifact.open && artifact.undoAttempts > 0) {
    return (
      <span className="inline-flex flex-col text-[12px] leading-tight">
        <span>{`undo tried ${artifact.undoAttempts}×`}</span>
        {artifact.exhausted
          ? <span className="text-muted-foreground">no tries left</span>
          : artifact.nextUndoUtc !== null && <span className="inline-flex gap-1 text-muted-foreground">next <CompactTime value={artifact.nextUndoUtc} caption="Next try" /></span>}
      </span>
    );
  }

  return <span className="text-muted-foreground">-</span>;
}

const columns: Column<DeliveryArtifact>[] = [
  {
    id: "state",
    header: "State",
    render: (row) => <ArtifactStateBadge state={row.state} exhausted={row.exhausted} testId="record-artifact-state" />,
  },
  {
    id: "what",
    header: "What",
    render: (row) => (
      <span className="inline-flex min-w-0 flex-col leading-tight">
        <span className="text-[13px]">{row.role}</span>
        {row.slot !== row.role && <TruncatedText text={row.slot} mono maxWidth={180} className="text-[11.5px] text-muted-foreground" title="Slot" />}
      </span>
    ),
  },
  {
    // The OSDU id, or while the call has not answered, what finds the object if its answer was lost.
    id: "target",
    header: "OSDU id",
    fill: true,
    floor: 200,
    render: (row) => (
      <span className="inline-flex min-w-0 max-w-full flex-col leading-tight">
        {row.targetId !== null
          ? <TruncatedText text={row.targetId} mono maxWidth={560} copy title="OSDU id" />
          : row.locator !== null
            ? <TruncatedText text={row.locator} mono maxWidth={560} copy title="No id yet: found by" />
            : <span className="text-[12px] text-muted-foreground">no id yet</span>}
        <Beside artifact={row} />
      </span>
    ),
  },
  {
    id: "unit",
    header: "Delivery",
    render: (row) => (
      <span className="inline-flex flex-col leading-tight" title={`Unit of work ${row.unitId}`}>
        <span className="font-mono text-[12px]">{shortId(row.unitId)}</span>
        <CompactTime value={row.unitStartedUtc} caption="The delivery began" className="text-[11.5px] text-muted-foreground" />
      </span>
    ),
  },
  { id: "settled", header: "Settled", render: (row) => <Settlement artifact={row} /> },
  { id: "note", header: "Note", align: "center", render: (row) => <NoteMark note={row.note} testId={`record-artifact-note-${row.artifactId}`} /> },
];

/**
 * The header's line when an unfinished delivery of the record left something in OSDU that an undo still has to take: how
 * many, how many the sweep has stopped trying, and the way to the artifacts. Nothing while nothing is due or failed: a
 * delivery under way has intents and pending artifacts of its own.
 */
export function RecordUndoNotice({ artifacts, onOpen }: { artifacts: DeliveryArtifact[] | undefined; onOpen: () => void }) {
  const due = (artifacts ?? []).filter((row) => row.state === "due").length;
  const failed = (artifacts ?? []).filter((row) => row.state === "failed" && !row.exhausted).length;
  const exhausted = (artifacts ?? []).filter((row) => row.exhausted).length;
  if (due + failed + exhausted === 0) {
    return null;
  }

  const left = due + failed + exhausted;
  const explanation = exhausted > 0
    ? "The undo of an unfinished delivery failed as often as the sweep tries, so it is no longer tried by itself. Newer work for the record waits until it is undone. Once what stops it is fixed (the note on each artifact says what OSDU answered), run the flow's Undo unfinished with force, or release the record, which tries the undo before the newer work."
    : "A delivery of this record did not complete. What it created is undone by the worker before newer work is sent, and by the sweep at the end of every deliver and drain run; an undo that OSDU refused is tried again with backoff.";
  return (
    <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-[12.5px]" data-testid="record-undo-notice">
      <ArtifactStateCount
        state={exhausted > 0 ? "failed" : due > 0 ? "due" : "failed"}
        exhausted={exhausted > 0}
        count={left}
        label={exhausted === left
          ? `item${left === 1 ? "" : "s"} an unfinished delivery left in OSDU, whose undo has no tries left`
          : `item${left === 1 ? "" : "s"} an unfinished delivery left in OSDU, still to undo`}
      />
      {exhausted > 0 && exhausted < left && <span className="text-muted-foreground">{`${exhausted} with no tries left`}</span>}
      <NoteMark note={explanation} title="What this means" testId="record-undo-notice-why" />
      <button type="button" className="text-[12px] text-primary hover:underline" onClick={onOpen} data-testid="record-undo-notice-open">
        Artifacts
      </button>
    </div>
  );
}

/**
 * What deliveries of the record created in OSDU (osdu/docs/reference/concepts/gui-ledger.md, A record's page), newest first: one
 * line of counts by state, the ones an undo still has to take called out, then the grid, which fits what the window leaves
 * and scrolls in place. A delivery that completed keeps only the ids that stay in OSDU (datasets, content, outputs,
 * dataspaces), so a record delivered through a DDMS session, or in one call, has none, and the empty grid says why.
 */
export function RecordArtifacts({ query }: { query: UseQueryResult<DeliveryArtifact[]> }) {
  if (query.isError) {
    return isApiError(query.error)
      ? <CorrelationError error={query.error} />
      : <p className="text-[13px] text-destructive">{String(query.error)}</p>;
  }

  const rows = query.data;
  // A failed artifact the sweep still tries is counted apart from one that has used every try.
  const exhausted = (rows ?? []).filter((row) => row.exhausted).length;
  const states = ARTIFACT_STATES
    .map((state) => ({ state, count: (rows ?? []).filter((row) => row.state === state && !row.exhausted).length }))
    .filter((entry) => entry.count > 0);
  return (
    <div className="flex flex-col gap-2" data-testid="record-artifacts">
      {rows !== undefined && rows.length > 0 && (
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-[12px]" data-testid="record-artifact-counts">
          <span className="text-muted-foreground">
            {rows.length >= RECORD_ARTIFACTS_SHOWN ? `The newest ${rows.length.toLocaleString()}` : `${rows.length.toLocaleString()} in all`}
          </span>
          {states.map((entry) => (
            <ArtifactStateCount
              key={entry.state}
              state={entry.state}
              count={entry.count}
              label={entry.state === "failed" ? "failed, tried again" : undefined}
            />
          ))}
          {exhausted > 0 && <ArtifactStateCount state="failed" exhausted count={exhausted} label="failed, no tries left" />}
        </div>
      )}
      <DimensionGrid testId="record-artifacts-grid">
        <DataTable
          columns={columns}
          rows={rows}
          rowKey={(row) => row.artifactId}
          emptyMessage="Nothing kept beside the record. While a delivery runs, it lists here what it creates; once it completes, only datasets, content, outputs and dataspaces stay, and the record's own write, a version or a session leaves the list. A route that writes the record in one call lists nothing."
          data-testid="record-artifacts-table"
        />
      </DimensionGrid>
    </div>
  );
}
