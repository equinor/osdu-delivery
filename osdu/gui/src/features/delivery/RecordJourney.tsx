import { useLayoutEffect, useMemo, useState, type ReactNode } from "react";
import { Link as RouterLink } from "react-router-dom";
import { format, formatDistanceToNow } from "date-fns";
import {
  ArchiveRestore, ArrowRightLeft, CheckCircle2, ChevronRight, CircleDashed, CircleDot, Database, DatabaseZap, Eraser, FileInput, FilePen,
  FileQuestion, FileX2, Hourglass, Layers, Link2, PauseCircle, RefreshCw, ScanSearch, ShieldCheck, Trash2, Undo2, XCircle,
  type LucideIcon,
} from "lucide-react";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { parseUtc } from "@/lib/time";
import { CodeView } from "@/components/CodeView";
import { EmptyState } from "@/components/EmptyState";
import { RelativeTime } from "@/components/RelativeTime";
import { SummaryStrip, type SummaryCell } from "@/components/SummaryStrip";
import { TruncatedText } from "@/components/TruncatedText";
import type {
  DeliveryActivity, DeliveryAttempt, DeliveryAttemptStep, DeliveryChainLanding, DeliveryChainRun, DeliveryRecord,
  DeliveryRecordChain, DeliverySourceChange, DeliverySourceChangeKind, DeliveryUndoArtifact,
} from "../../api/delivery";
import { verdictIn, type ValidationVerdict } from "../../api/validation";
import { UndoOutcomeBadge } from "./ArtifactMarks";
import { RunRef, SubmissionRef } from "./DeliveryRefs";
import { Fact, FactGrid, NoFact } from "./Facts";
import { prettyJson } from "./prettyJson";
import { RecordName } from "./RecordName";
import { ValidationVerdictView } from "./ValidationVerdictView";
import { outcomeWord } from "./validationModel";
import { NoteMark } from "./RecordArtifacts";

type Tone = "success" | "destructive" | "warning" | "info" | "muted";

/**
 * Where a step happened: in the record's row of its ingestion table, in the ledger, or in OSDU. The rail draws each on
 * a lane of its own, so the path between the lanes shows how the record moved.
 */
type Lane = "source" | "ledger" | "osdu";

/** What the timeline narrows to: one lane, or the steps someone asked for. */
type Filter = "all" | Lane | "people";

const LANES: readonly Lane[] = ["source", "ledger", "osdu"];
const LANE_X: Record<Lane, number> = { source: 22, ledger: 66, osdu: 110 };
const LANE_LABEL: Record<Lane, string> = { source: "Source", ledger: "Ledger", osdu: "OSDU" };

/** How far below the top of its row a node's centre sits, which is where the path meets it. */
const NODE_CENTRE = 14;

/** The time, the rail and the entry: one grid every row of the timeline shares, so times and nodes line up. */
const ROW = "grid grid-cols-[72px_132px_minmax(0,1fr)]";

const TONE_RING: Record<Tone, string> = {
  success: "border-success/50 bg-success/10 text-success",
  destructive: "border-destructive/50 bg-destructive/10 text-destructive",
  warning: "border-warning/50 bg-warning/10 text-warning",
  info: "border-info/50 bg-info/10 text-info",
  muted: "border-border bg-muted text-muted-foreground",
};

const TONE_TEXT: Record<Tone, string> = {
  success: "text-success",
  destructive: "text-destructive",
  warning: "text-warning",
  info: "text-info",
  muted: "text-muted-foreground",
};

interface Outcome {
  text: string;
  tone: Tone;
}

/** One thing that happened to the record: when, where, what, the facts that place it, and the rest. */
interface Entry {
  id: string;
  at: string;
  lane: Lane;
  title: string;
  tone: Tone;
  /** The node's icon; null draws the initial of who asked instead. */
  icon: LucideIcon | null;
  /** Who asked, for a step someone asked for. */
  actor: string | null;
  /** How many records the request named, when that is known: one for this record alone. */
  scope: number | null;
  /** The facts that place the entry, on one line: the file and row, the run and submission, how long it took. */
  facts: ReactNode[];
  /** What the step left the record in, when that is not its title (held until released, tried again). */
  then: Outcome | null;
  /** What went wrong, when something did. */
  error: string | null;
  /** Everything else known about the step, revealed when the entry is opened. */
  more: ReactNode | null;
  /** Entries dated alike (a landing and the load it fed, a request and the try it started) keep their order by this. */
  order: number;
  /** A step that sets the record moving opens a chapter, named by this: its row arrived or changed, someone asked. */
  trigger: string | null;
  /** What this step settles its chapter as, for a step that settles something. */
  outcome: Outcome | null;
  /** A verify, whose match after a delivery settles the chapter as delivered and verified. */
  verifies: boolean;
  /** Consecutive tries that ended alike share this key and fold into one entry. */
  foldKey: string | null;
  testId: string;
}

/** A stretch of the record's story: what set it moving, what happened, and how it ended. */
interface Chapter {
  number: number;
  trigger: string;
  entries: Entry[];
  outcome: Outcome;
}

function Mono({ children }: { children: ReactNode }) {
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

function Origin({ file, row, inCell = false }: { file: string | null; row: number | null; inCell?: boolean }) {
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

function FlowRef({ run }: { run: DeliveryChainRun }) {
  return <RouterLink to={`/pipelines/${run.pipelineId}`} className="text-primary hover:underline" onClick={(event) => event.stopPropagation()}>{run.flowName}</RouterLink>;
}

/** The pieces of an entry's fact line, separated so a missing one leaves no dangling separator. */
function FactLine({ parts }: { parts: ReactNode[] }) {
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
function StepList({ steps }: { steps: DeliveryAttemptStep[] }) {
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

/** How long something took, from two moments the ledger or the catalog recorded. */
function between(from: string | null, to: string | null): string | null {
  if (from === null || to === null) {
    return null;
  }

  const ms = parseUtc(to).getTime() - parseUtc(from).getTime();
  if (!Number.isFinite(ms) || ms < 0) {
    return null;
  }

  return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(1)} s`;
}

/** How long a run took, for a run's line. */
function runDuration(seconds: number | null): string | null {
  if (seconds === null || !Number.isFinite(seconds)) {
    return null;
  }

  return seconds < 1 ? `${Math.round(seconds * 1000)} ms` : seconds < 90 ? `${seconds.toFixed(1)} s` : `${(seconds / 60).toFixed(1)} min`;
}

function fileSize(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  if (bytes < 1024 * 1024) {
    return `${(bytes / 1024).toFixed(1)} KB`;
  }

  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/** JSON the ledger stored as text, pretty-printed when it parses and shown as it is when it does not. */
function storedJson(text: string): string {
  try {
    return prettyJson(text);
  } catch {
    return text;
  }
}

/** The ingestion table as an operator names it: its schema and table, without brackets or the database. */
function ingestionTableName(sourceTable: string | null): string {
  if (sourceTable === null) {
    return "the ingestion table";
  }

  const parts = sourceTable.split(".").map((part) => part.trim().replace(/^\[/, "").replace(/\]$/, "")).filter((part) => part !== "");
  return parts.length === 0 ? sourceTable : parts.slice(-2).join(".");
}

/** Whether a change is the row's arrival as far as the ledger knows it: its insert, a reinsert, or its earliest version. */
function isArrival(change: DeliverySourceChange): boolean {
  return change.kind === "loaded" || change.kind === "reloaded" || change.kind === "earliest";
}

/** The local day a moment fell on, as the timeline names days: "Fri 25 Sep". */
function dayOf(at: string): string {
  return format(parseUtc(at), "EEE d MMM");
}

function clockOf(at: string): string {
  return format(parseUtc(at), "HH:mm");
}

/** Who asked, as the ledger names them without the prefix of a signed-in user. */
function actorName(actor: string): string {
  return actor.startsWith("user:") ? actor.slice("user:".length) : actor;
}

/** The letter a person's node carries: the first of their name, past a schedule's or a workstation's prefix. */
function actorInitial(actor: string): string {
  const name = actorName(actor).replace(/^(cli|schedule):/, "");
  return name === "" || name === "unknown" ? "?" : name.charAt(0).toUpperCase();
}

const CHANGE_SHAPES: Record<DeliverySourceChangeKind, {
  title: (table: string) => string;
  trigger: (table: string) => string;
  what: (table: string) => string;
  icon: LucideIcon;
  tone: Tone;
}> = {
  loaded: {
    title: (table) => `Row loaded into ${table}`,
    trigger: (table) => `Row arrived in ${table}`,
    what: (table) => `The ingestion flow inserted the row into ${table} (its InsertedDate_DW): the record's arrival. A later run that loads the row unchanged leaves it as it is, and is no part of this history.`,
    icon: DatabaseZap,
    tone: "info",
  },
  reloaded: {
    title: (table) => `Row loaded into ${table} again`,
    trigger: (table) => `Row arrived in ${table} again`,
    what: (table) => `The ingestion flow inserted the row into ${table} again after earlier versions of it: the row was deleted from the table and loaded anew.`,
    icon: ArchiveRestore,
    tone: "info",
  },
  earliest: {
    title: (table) => `Row in ${table}: the earliest version the ledger holds`,
    trigger: (table) => `Row in ${table}`,
    what: () => "The ledger does not know when the row first reached the table: the table carries no InsertedDate_DW, or no plan has read the row since the ledger began keeping it. This is the earliest version of the row it recorded; whether it was the row's insert or a later change cannot be told.",
    icon: Database,
    tone: "muted",
  },
  changed: {
    title: (table) => `Row changed in ${table}`,
    trigger: (table) => `Row changed in ${table}`,
    what: () => "The ingestion flow restamped the row (its UpdatedDate_DW): the row's data changed. A run that loads the row unchanged does not restamp it, so every entry like this one is a change.",
    icon: FilePen,
    tone: "info",
  },
  deleted: {
    title: (table) => `Row deleted from ${table}`,
    trigger: (table) => `Row deleted from ${table}`,
    what: () => "The ingestion flow's key match found the row gone from the source and marked it deleted (its DeletedDate_DW). A deleted row is never delivered: what OSDU holds is removed only by a removal someone asks for.",
    icon: FileX2,
    tone: "destructive",
  },
};

function RunFacts({ run, rowsLabel }: { run: DeliveryChainRun; rowsLabel: string }) {
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

function entryDefaults(): Pick<Entry, "actor" | "scope" | "then" | "error" | "more" | "trigger" | "outcome" | "verifies" | "foldKey"> {
  return { actor: null, scope: null, then: null, error: null, more: null, trigger: null, outcome: null, verifies: false, foldKey: null };
}

/**
 * One change of the record's row, as the ledger recorded it: its arrival, a change, or its deletion. The runs are the
 * evidence of the change, not entries of their own: the ingestion run that wrote it and, for a change from a file, the
 * landing that brought the file in. A run that reloaded the row unchanged made no change, and so is not here.
 */
function changeEntry(change: DeliverySourceChange, table: string): Entry {
  const shape = CHANGE_SHAPES[change.kind];
  const arrival = isArrival(change);
  const deleted = change.kind === "deleted";
  return {
    ...entryDefaults(),
    id: `change-${change.kind}-${change.atUtc}`,
    at: change.atUtc,
    lane: "source",
    title: shape.title(table),
    tone: shape.tone,
    icon: shape.icon,
    facts: [
      change.fileName !== null
        ? <Origin key="o" file={change.fileName} row={change.rowNumber} />
        : arrival && change.kind !== "earliest" && "from a file the ledger never saw: the row changed before a plan read it",
      change.loading !== null
        ? <span key="l">{deleted ? "marked by" : "written by"} <RunRef runId={change.loading.runId} /></span>
        : "the ingestion run that wrote it is not recorded",
      !arrival && change.landing !== null && <span key="f">landed by <RunRef runId={change.landing.run.runId} /></span>,
    ],
    error: change.loading !== null && !change.loading.success ? `The ingestion run ended ${change.loading.status}.` : null,
    more: (
      <FactGrid>
        <Fact label="What happened" wide>{shape.what(table)}</Fact>
        <Fact label={deleted ? "Marked by" : "Written by"} wide>
          {change.loading !== null
            ? <RunFacts run={change.loading} rowsLabel="rows loaded" />
            : <span className="text-muted-foreground">{`No recorded run of a flow that writes ${table} was executing at this moment: the run was pruned, ran before this estate recorded runs, or the lineage does not name the table's writers.`}</span>}
        </Fact>
        {!deleted && (
          <Fact label="File landed by" wide>
            {change.landing !== null
              ? <RunFacts run={change.landing.run} rowsLabel="rows in the file" />
              : <span className="text-muted-foreground">{change.fileName === null ? "The ledger holds no file for this version." : "No successful run recorded processing a file of this name before the row was written, so the landing is not named."}</span>}
          </Fact>
        )}
        {change.landing?.filePath != null && (
          <Fact label="Path" wide>
            <TruncatedText text={change.landing.filePath} mono maxWidth={720} copy title="Path" />
            {change.landing.sizeBytes > 0 && <span className="ml-2 text-muted-foreground"><Mono>{fileSize(change.landing.sizeBytes)}</Mono></span>}
          </Fact>
        )}
      </FactGrid>
    ),
    order: 1,
    trigger: shape.trigger(table),
    testId: `journey-source-${change.kind}`,
  };
}

/**
 * The first time the record came in from a file: the landing that brought in the file its arrival was loaded from. A
 * landing of the same file later is no change of the record, so only the arrival's landing is an entry of its own; a
 * later change names its landing as its evidence.
 */
function landingEntry(change: DeliverySourceChange, landing: DeliveryChainLanding, table: string): Entry {
  const duration = runDuration(landing.run.durationSeconds);
  return {
    ...entryDefaults(),
    id: `landing-${landing.run.runId}`,
    at: landing.run.ranUtc,
    lane: "source",
    title: `File landed by ${landing.run.flowName}`,
    tone: landing.run.success ? "info" : "destructive",
    icon: FileInput,
    facts: [
      <Origin key="o" file={landing.fileName} row={change.rowNumber} />,
      landing.rows > 0 && `${landing.rows.toLocaleString()} row${landing.rows === 1 ? "" : "s"} in the file`,
      duration !== null && `took ${duration}`,
      <RunRef key="r" runId={landing.run.runId} />,
    ],
    error: landing.run.success ? null : landing.run.error,
    more: (
      <FactGrid>
        <Fact label="Flow"><FlowRef run={landing.run} /></Fact>
        <Fact label="Run status"><Mono>{landing.run.status}</Mono>{" "}<span className="text-muted-foreground">wave</span>{" "}<Mono>{landing.run.wave}</Mono></Fact>
        <Fact label="File"><TruncatedText text={landing.fileName} mono maxWidth={360} copy title="File" /></Fact>
        {landing.filePath !== null && <Fact label="Path"><TruncatedText text={landing.filePath} mono maxWidth={360} copy title="Path" /></Fact>}
        {landing.sizeBytes > 0 && <Fact label="Size"><Mono>{fileSize(landing.sizeBytes)}</Mono></Fact>}
        {landing.fileModifiedUtc !== null && <Fact label="File modified"><RelativeTime value={landing.fileModifiedUtc} /></Fact>}
      </FactGrid>
    ),
    order: 0,
    trigger: CHANGE_SHAPES[change.kind].trigger(table),
    testId: "journey-source-landing",
  };
}

/** The record's row through its ingestion table: each change of it, and the landing its arrival came in by. */
function sourceEntries(chain: DeliveryRecordChain | undefined): Entry[] {
  if (chain === undefined) {
    return [];
  }

  const table = ingestionTableName(chain.sourceTable);
  return chain.changes.flatMap((change) =>
    isArrival(change) && change.landing !== null
      ? [changeEntry(change, table), landingEntry(change, change.landing, table)]
      : [changeEntry(change, table)]);
}

/** When the ledger first took the record in: the run read its row, rendered it, queued it and reserved its OSDU id. */
function pickedUpEntry(record: DeliveryRecord): Entry {
  return {
    ...entryDefaults(),
    id: "planned",
    at: record.createdUtc,
    lane: "ledger",
    title: "Picked up for delivery",
    tone: "info",
    icon: ScanSearch,
    facts: [
      <span key="m">rendered with mapping <Mono>{record.mappingName}</Mono></span>,
      record.targetId !== null
        ? <span key="t">OSDU id reserved for this flow: <RecordName id={record.targetId} copy className="max-w-[320px]" /></span>
        : "no OSDU id reserved yet",
    ],
    more: (
      <FactGrid>
        <Fact label="What happened" wide>
          A deliver run read the row, rendered the document with the mapping and the cache, and queued it to be sent. It
          reserved the record's OSDU id for this flow, so no other flow can write to that record. The id is computed from
          the record key: nothing was sent at this point.
        </Fact>
        <Fact label="OSDU id">{record.targetId !== null ? <TruncatedText text={record.targetId} mono maxWidth={420} copy title="OSDU id" /> : <NoFact />}</Fact>
        <Fact label="Mapping"><Mono>{record.mappingName}</Mono></Fact>
        <Fact label="Rendered with">{record.renderContext !== null ? <TruncatedText text={record.renderContext} mono maxWidth={420} copy title="Render context" /> : <NoFact />}</Fact>
      </FactGrid>
    ),
    order: 3,
    testId: "journey-planned",
  };
}

/** What OSDU answered a refused request with, read from the error the worker stored. */
interface Refusal {
  status: number;
  reason: string;
  method: string | null;
  url: string | null;
  answer: string | null;
}

function refusalOf(error: string | null): Refusal | null {
  if (error === null) {
    return null;
  }

  const head = /HTTP (\d{3}) ([A-Za-z]+) from (GET|POST|PUT|PATCH|DELETE|HEAD) (\S+)/.exec(error);
  if (head === null) {
    const bare = /^HTTP (\d{3})\b/.exec(error);
    return bare === null ? null : { status: Number(bare[1]), reason: "", method: null, url: null, answer: null };
  }

  const answer = /\):\s*(.+)$/s.exec(error)?.[1]?.trim();
  return {
    status: Number(head[1]),
    reason: head[2].replace(/([a-z])([A-Z])/g, "$1 $2"),
    method: head[3],
    url: head[4],
    answer: answer === undefined || answer === "" ? null : answer,
  };
}

function urlPath(url: string): string {
  try {
    return new URL(url).pathname;
  } catch {
    return url;
  }
}

/**
 * Whether a try reached OSDU: a delivery, a removal, a purge, a try that failed, one that took a step, or one OSDU
 * refused. What the intake decided (a hold of a row it could not build, a change that rendered what was delivered, an
 * older version) and the final hash check that found nothing to send are the ledger's decisions. A refused request
 * completes no step, so a refusal is known by the answer the worker stored with its hold.
 */
function reachedTarget(attempt: DeliveryAttempt): boolean {
  return attempt.outcome === "delivered"
    || attempt.outcome === "failed"
    || attempt.outcome === "deleted"
    || attempt.outcome === "historypurged"
    || attempt.outcome === "restored"
    || attempt.outcome === "undone"
    || (attempt.result?.steps?.length ?? 0) > 0
    || (attempt.outcome === "held" && attempt.worker !== "intake" && attempt.phase !== "undo-wait" && refusalOf(attempt.error) !== null);
}

const PHASE_TEXT: Record<string, string> = {
  metadata: "record only",
  payload: "payload only",
  "metadata+payload": "record and payload",
  "source-removed": "a removal flow found its source no longer holds the key",
};

/** Why an undo ran, as its attempt's result names the reason. */
const UNDO_REASON: Record<string, string> = {
  held: "after a try that held",
  failed: "after the delivery used its tries",
  abandoned: "newer work replaced the delivery",
  removed: "before the record was removed",
};

/**
 * What an undo did to each artifact of an unfinished delivery, one line each: how the undo settled it, what it is, its OSDU
 * id or what finds it, the versions it names, and what the route answered, behind a note mark with its copy.
 */
function UndoList({ artifacts, keptRecord }: { artifacts: DeliveryUndoArtifact[]; keptRecord: boolean }) {
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

function statusText(refusal: Refusal): string {
  return `HTTP ${refusal.status}${refusal.reason !== "" ? ` ${refusal.reason}` : ""}`;
}

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
      return { title: "Delivered", tone: "success", icon: CheckCircle2, then: null, outcome: { text: "Delivered", tone: "success" } };
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
          return { title: "Nothing to send: the row renders what was delivered", tone: "muted", icon: CircleDashed, then: null, outcome: { text: "Nothing to send", tone: "muted" } };
        case "unchanged":
          return { title: "Nothing to send: already delivered", tone: "muted", icon: CircleDashed, then: null, outcome: { text: "Nothing to send", tone: "muted" } };
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

function attemptEntry(attempt: DeliveryAttempt): Entry {
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
        {steps.length > 0 && <StepList steps={steps} />}
        {undo?.artifacts !== undefined && undo.artifacts.length > 0 && <UndoList artifacts={undo.artifacts} keptRecord={undo.keptRecord === true} />}
        {verdict !== null && <ValidationVerdictView verdict={verdict} />}
        <FactGrid>
          {refusal?.url != null && <Fact label="Request" wide><Mono>{`${refusal.method ?? ""} ${refusal.url}`}</Mono></Fact>}
          {attempt.error !== null && <Fact label="Error" wide><span className="text-destructive">{attempt.error}</span></Fact>}
          {attempt.result?.detail !== undefined && <Fact label="Note" wide>{attempt.result.detail}</Fact>}
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

function foldRepeats(entries: Entry[]): Entry[] {
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

/** The activity's parameters, as the ledger stored them; null when they are absent or not an object. */
function parametersOf(activity: DeliveryActivity): Record<string, unknown> | null {
  if (activity.parametersJson === null) {
    return null;
  }

  try {
    const parsed: unknown = JSON.parse(activity.parametersJson);
    return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed as Record<string, unknown> : null;
  } catch {
    return null;
  }
}

/** How many records a request named: the keys it listed, or this record alone when the ledger tied it to one. */
function scopeOf(activity: DeliveryActivity, parameters: Record<string, unknown> | null): number | null {
  const keys = parameters?.keys;
  if (Array.isArray(keys) && keys.length > 0) {
    return keys.length;
  }

  // An intervention on many records names how many rather than each one; the ledger names every record it reached.
  const count = parameters?.count;
  if (typeof count === "number" && Number.isFinite(count) && count > 0) {
    return count;
  }

  return activity.deliveryKey !== null ? 1 : null;
}

/**
 * A release that named no record and reached this one among others: a release of every blocked record, or of every record
 * one issue kept blocked. The ledger names each record such a release changed, which is how it is on this record's
 * timeline at all; its summary says how many it released once it is done, and an issue's release names the issue's
 * pattern.
 */
interface Reach {
  title: string;
  count: number | null;
  pattern: string | null;
}

function reachOf(activity: DeliveryActivity, parameters: Record<string, unknown> | null): Reach | null {
  const keys = parameters?.keys;
  if ((activity.kind !== "release" && activity.kind !== "adopt" && activity.kind !== "redeliver" && activity.kind !== "rerender")
    || activity.deliveryKey !== null || (Array.isArray(keys) && keys.length > 0) || typeof parameters?.count === "number") {
    return null;
  }

  // "released 1,204 record(s) ...", "asked 1204 record(s) ... to adopt ...", or "marked every delivered record, 1204 in all, for
  // redelivery of ...". An adoption made before adoption was asked of a run said "adopted 1204 record(s) ...".
  const reached = /^(?:released|adopted|asked) (\d+) record|, (\d+) in all,/.exec(activity.summary ?? "");
  const said = reached?.[1] ?? reached?.[2];
  const count = said === undefined ? null : Number(said);
  if (activity.kind === "rerender") {
    return {
      title: count === null
        ? "Bringing up to date asked for every delivered record, this one among them"
        : `Bringing up to date asked for every delivered record, ${count.toLocaleString()} in all, this one among them`,
      count,
      pattern: null,
    };
  }

  if (activity.kind === "redeliver") {
    return {
      title: count === null
        ? "Redelivery asked for every delivered record, this one among them"
        : `Redelivery asked for every delivered record, ${count.toLocaleString()} in all, this one among them`,
      count,
      pattern: null,
    };
  }

  const records = count === null ? "the records" : `${count.toLocaleString()} record${count === 1 ? "" : "s"}`;
  // An adoption that names no record is asked of every record held because OSDU holds a record at the id its key makes,
  // or of the records one issue kept held for that.
  if (activity.kind === "adopt") {
    if (parameters?.every !== undefined) {
      return {
        title: count === null
          ? "Adoption asked for every record held for an id OSDU holds, this one among them"
          : `Adoption asked for every record held for an id OSDU holds, ${records} in all, this one among them`,
        count,
        pattern: null,
      };
    }

    return {
      title: `Adoption asked for ${records} one issue kept held, this one among them`,
      count,
      pattern: typeof parameters?.pattern === "string" ? parameters.pattern : null,
    };
  }

  // A release of one issue's records names the issue (under "problem" in what the first releases wrote).
  if (typeof (parameters?.issue ?? parameters?.problem) === "string") {
    return {
      title: `Release asked for ${records} one issue kept blocked, this one among them`,
      count,
      pattern: typeof parameters?.pattern === "string" ? parameters.pattern : null,
    };
  }

  return {
    title: count === null
      ? "Release asked for every blocked record, this one among them"
      : `Release asked for every blocked record, ${records} in all, this one among them`,
    count,
    pattern: null,
  };
}

/** What a redelivery re-sends, from the scope and parts it was asked with. */
function redeliveryOf(parameters: Record<string, unknown> | null): string | null {
  const parts = parameters?.parts;
  if (Array.isArray(parts) && parts.length > 0) {
    return `the ${parts.map(String).join(" and ")}`;
  }

  switch (parameters?.scope) {
    case "All":
      return "everything: record and payload";
    case "Metadata":
      return "the record";
    case "Payload":
      return "the payload";
    default:
      return null;
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

/** Whether a verify was the one whose outcome the record holds: the record was verified while the verify ran. */
function covers(activity: DeliveryActivity, at: string): boolean {
  const moment = parseUtc(at).getTime();
  const from = parseUtc(activity.startedUtc).getTime() - 2000;
  const to = activity.completedUtc === null ? Number.POSITIVE_INFINITY : parseUtc(activity.completedUtc).getTime() + 2000;
  return moment >= from && moment <= to;
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
function activityEntry(activity: DeliveryActivity, record: DeliveryRecord, result: boolean): Entry {
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

  // The record's ledger moved to the pipeline that replaced the one that kept it (a renamed pipeline): the record changed
  // pipelines, not state, so this opens no chapter, and its summary names both pipelines.
  if (activity.kind === "ledger-move") {
    return {
      ...entryDefaults(),
      id: `activity-${activity.activityId}`,
      at: activity.startedUtc,
      lane: "ledger",
      title: "Moved with its ledger to the pipeline that replaced its own",
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
function verifiedEntry(record: DeliveryRecord, at: string): Entry {
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

/**
 * Where the record stands, in the words of a chapter's outcome, and whether work on it is in flight (a document queued
 * or being sent, a wait on a record it refers to): what the latest chapter says when nothing in it has settled yet, or
 * when something since has set the record going again. `adopted` says its newest attempt is an adoption made before
 * adoption was asked of a run, so a record asked to be planned with no document kept claimed the record OSDU already held
 * and waits for its first write there.
 */
function standingOf(record: DeliveryRecord, adopted: boolean): { outcome: Outcome; inFlight: boolean } {
  switch (record.status) {
    case "delivered":
      return { outcome: { text: "Delivered", tone: "success" }, inFlight: false };
    case "pending":
      return record.hasPendingDocument
        ? { outcome: { text: "Queued to send", tone: "info" }, inFlight: true }
        : { outcome: { text: record.planRequestedUtc !== null ? "Waiting for the next run" : "Pending", tone: "info" }, inFlight: true };
    case "delivering":
      return { outcome: { text: "Being delivered", tone: "info" }, inFlight: true };
    case "waiting":
      return { outcome: { text: "Waiting for a record it refers to", tone: "info" }, inFlight: true };
    case "held":
    case "failed":
      // Released, asked to adopt the record OSDU holds at the id its key makes, or asked to be planned again, with no document
      // kept: the next run plans it from its row. A request to adopt that a run held the record over since waits for a release.
      if (!record.blocked && !record.hasPendingDocument && record.planRequestedUtc !== null) {
        const waiting = record.adoptRequestedUtc != null ? "Adoption asked" : adopted ? "Adopted" : "Released";
        return { outcome: { text: `${waiting}: waiting for the next run`, tone: "info" }, inFlight: true };
      }

      return record.status === "held"
        ? { outcome: { text: "Held", tone: "warning" }, inFlight: false }
        : { outcome: { text: "Failed", tone: "destructive" }, inFlight: false };
    case "deleted":
      return { outcome: { text: "Removed", tone: "muted" }, inFlight: false };
    case "reverted":
      return { outcome: { text: "Reverted", tone: "muted" }, inFlight: false };
    default:
      return { outcome: { text: record.status, tone: "muted" }, inFlight: false };
  }
}

/**
 * How a chapter ended: its last settled step, a verified delivery as both. The latest chapter says where the record
 * stands instead while work on it is in flight, or when nothing in it has settled yet.
 */
function settle(entries: Entry[], latest: boolean, standing: { outcome: Outcome; inFlight: boolean }): Outcome {
  const settled = entries.reduce<Outcome | null>((outcome, entry) => {
    if (entry.outcome === null) {
      return outcome;
    }

    return entry.verifies && entry.outcome.tone === "success" && outcome !== null && outcome.text === "Delivered"
      ? { text: "Delivered and verified", tone: "success" }
      : entry.outcome;
  }, null);

  if (latest && (standing.inFlight || settled === null)) {
    return standing.outcome;
  }

  return settled ?? { text: "Nothing sent", tone: "muted" };
}

interface Story {
  chapters: Chapter[];
  syncs: DeliveryActivity[];
  table: string;
}

/**
 * The record's story, in chapters. Each step is placed on the lane where it happened; the steps are read in the order
 * they happened and cut into chapters where something set the record moving (its row arrived, changed or was deleted,
 * or someone asked for a redelivery, a release or a removal), so a chapter is what started it and what came of it.
 * Steps that set the record moving one after another, before anything came of them (a file landing and the load of its
 * row, a request asked twice), open one chapter together. A sync of the timeline itself is kept apart: it is how the
 * ledger learned the row's history, not a step of it.
 */
function buildStory(
  record: DeliveryRecord, attempts: DeliveryAttempt[], activities: DeliveryActivity[], chain: DeliveryRecordChain | undefined,
): Story {
  const lastVerified = record.lastVerifiedUtc;
  const verifyWithResult = lastVerified === null
    ? undefined
    : activities.filter((a) => a.kind === "verify" && covers(a, lastVerified)).at(-1);

  const entries: Entry[] = [...sourceEntries(chain), pickedUpEntry(record)];
  for (const activity of activities) {
    if (activity.kind !== "sync") {
      entries.push(activityEntry(activity, record, activity === verifyWithResult));
    }
  }

  if (lastVerified !== null && verifyWithResult === undefined) {
    entries.push(verifiedEntry(record, lastVerified));
  }

  const attemptEntries = attempts.map(attemptEntry);
  entries.push(...attemptEntries);

  // In the order it happened. The API writes some moments without their zone; every moment is UTC by contract.
  entries.sort((a, b) => {
    const byTime = parseUtc(a.at).getTime() - parseUtc(b.at).getTime();
    return byTime !== 0 && Number.isFinite(byTime) ? byTime : a.order - b.order;
  });

  const chapters: Chapter[] = [];
  // An adoption made at an operator's click as the record's newest attempt is what it waits on.
  const newest = attempts.reduce<DeliveryAttempt | undefined>(
    (latest, attempt) => latest === undefined || parseUtc(attempt.startedUtc).getTime() > parseUtc(latest.startedUtc).getTime() ? attempt : latest,
    undefined);
  const standing = standingOf(record, newest?.phase === "adopt" && newest.worker !== "intake");
  for (const entry of foldRepeats(entries)) {
    const current = chapters.at(-1);
    const onlyTriggers = current !== undefined && current.entries.every((e) => e.trigger !== null);
    if (current === undefined || (entry.trigger !== null && !onlyTriggers)) {
      chapters.push({ number: chapters.length + 1, trigger: entry.trigger ?? entry.title, entries: [entry], outcome: standing.outcome });
    } else {
      current.entries.push(entry);
    }
  }

  chapters.forEach((chapter, index) => {
    chapter.outcome = settle(chapter.entries, index === chapters.length - 1, standing);
  });

  const syncs = activities
    .filter((a) => a.kind === "sync")
    .sort((a, b) => parseUtc(b.startedUtc).getTime() - parseUtc(a.startedUtc).getTime());
  return { chapters, syncs, table: ingestionTableName(chain?.sourceTable ?? null) };
}

/**
 * The answers an operator came for, in one strip, in the order a record moves: the file it came in from, when its row
 * reached the ingestion table, when the row last changed, when it was delivered, when it was last verified, and whether
 * it was removed.
 */
function milestones(record: DeliveryRecord, attempts: DeliveryAttempt[], chain: DeliveryRecordChain | undefined): SummaryCell[] {
  const dispatched = attempts.filter((a) => a.outcome === "delivered" || a.outcome === "failed" || (a.outcome === "held" && reachedTarget(a)));
  const failed = dispatched.filter((a) => a.outcome === "failed").length;
  const removed = attempts.find((a) => a.outcome === "deleted");
  const changes = chain?.changes ?? [];
  const arrival = [...changes].reverse().find(isArrival);
  const since = arrival === undefined ? changes : changes.filter((c) => parseUtc(c.atUtc).getTime() > parseUtc(arrival.atUtc).getTime());
  const latest = since.at(0);
  const unknown = chain === undefined ? "-" : "not recorded";

  const cells: SummaryCell[] = [
    {
      label: "From file",
      value: arrival?.landing != null ? <RelativeTime value={arrival.landing.run.ranUtc} /> : unknown,
      caption: arrival === undefined
        ? undefined
        : arrival.fileName ?? "the file is not recorded",
      tone: arrival?.landing != null && !arrival.landing.run.success ? "destructive" : undefined,
      testId: "milestone-file",
    },
    {
      label: "Loaded",
      value: arrival !== undefined ? <RelativeTime value={arrival.atUtc} /> : unknown,
      caption: arrival === undefined
        ? undefined
        : arrival.kind === "earliest"
          ? `earliest version held, ${ingestionTableName(chain?.sourceTable ?? null)}`
          : ingestionTableName(chain?.sourceTable ?? null),
      testId: "milestone-loaded",
    },
    {
      label: "Last change",
      value: latest !== undefined ? <RelativeTime value={latest.atUtc} /> : arrival !== undefined ? "none" : unknown,
      caption: latest === undefined
        ? (arrival !== undefined ? "unchanged since it was loaded" : undefined)
        : latest.kind === "deleted"
          ? "the row was deleted"
          : `${since.length} change${since.length === 1 ? "" : "s"} since it was loaded`,
      tone: latest?.kind === "deleted" ? "destructive" : undefined,
      testId: "milestone-changed",
    },
    {
      label: "Delivered",
      value: record.lastDeliveredUtc === null ? "not yet" : <RelativeTime value={record.lastDeliveredUtc} />,
      caption: record.lastDeliveredUtc === null
        ? (dispatched.length === 0
          ? (record.status === "pending" || record.status === "delivering" ? "queued" : record.status === "waiting" ? "waiting" : undefined)
          : `${dispatched.length} ${dispatched.length === 1 ? "try" : "tries"}${failed > 0 ? `, ${failed} failed` : ""}`)
        : record.targetVersion !== null ? `version ${record.targetVersion}` : undefined,
      tone: record.lastDeliveredUtc === null ? undefined : "success",
      testId: "milestone-landed",
    },
    {
      label: "Verified",
      value: record.lastVerifiedUtc === null ? "never" : <RelativeTime value={record.lastVerifiedUtc} />,
      caption: record.lastVerifyOutcome ?? undefined,
      tone: record.lastVerifyOutcome === "drifted" || record.lastVerifyOutcome === "missing" ? "warning" : record.lastVerifyOutcome === "match" ? "success" : undefined,
      testId: "milestone-verified",
    },
  ];
  if (record.status === "deleted" || removed !== undefined) {
    cells.push({
      label: "Removed",
      value: removed === undefined ? "yes" : <RelativeTime value={removed.startedUtc} />,
      caption: record.status !== "deleted"
        ? "delivered again since"
        : removed?.phase === "source-removed"
          ? "its source no longer holds the key"
          : record.blocked ? "blocked until its row changes or it is released" : "sent again when its row is read",
      tone: "destructive",
      testId: "milestone-removed",
    });
  }

  return cells;
}

/**
 * How far the record has come, as one strip in the order a record moves: the file it came in from, when its row
 * reached the ingestion table and last changed there, when it was delivered and as which version, when it was last
 * verified, and whether it was removed. The page's spine: the timeline under it is the evidence.
 */
export function RecordMilestones({ record, attempts, chain }: {
  record: DeliveryRecord;
  attempts: DeliveryAttempt[] | undefined;
  /** The changes of the record's row in its ingestion table; undefined while they are being read. */
  chain: DeliveryRecordChain | undefined;
}) {
  if (attempts === undefined) {
    return <Skeleton className="h-16 w-full rounded-lg" />;
  }

  // Five cells (six for a removed record) at the strip's default floor need more width than the workbench's content
  // measure gives them and strand the last on a line of its own; a time and a short caption fit in 160px.
  return <SummaryStrip cells={milestones(record, attempts, chain)} minCellWidth={160} data-testid="record-milestones" />;
}

/** The lowest the timeline's list goes, so a small window still shows a useful stretch of it and the page scrolls. */
const MIN_LIST_HEIGHT = 320;

/** The nearest ancestor that scrolls vertically (the workbench's content pane), or null when the window scrolls. */
function scrollingAncestor(node: HTMLElement): HTMLElement | null {
  for (let parent = node.parentElement; parent !== null; parent = parent.parentElement) {
    const overflow = getComputedStyle(parent).overflowY;
    if (overflow === "auto" || overflow === "scroll") {
      return parent;
    }
  }

  return null;
}

/**
 * The most a list may grow to so that it, and what its card holds under it, ends at the bottom of the pane the page
 * scrolls in: the rest of the page, whatever the header above it and the window hold. It is measured before paint,
 * from the list's place in the pane's content rather than on screen, so it holds while the page scrolls, and again
 * whenever the pane or anything in it changes size. A short list stays short; a long one fills the page and scrolls.
 */
function useFillHeight(): { cardRef: (node: HTMLDivElement | null) => void; listRef: (node: HTMLDivElement | null) => void; maxHeight: number | null } {
  const [card, setCard] = useState<HTMLDivElement | null>(null);
  const [list, setList] = useState<HTMLDivElement | null>(null);
  const [maxHeight, setMaxHeight] = useState<number | null>(null);
  useLayoutEffect(() => {
    if (card === null || list === null) {
      return;
    }

    const pane = scrollingAncestor(card);
    const measure = () => {
      const paneTop = pane === null ? 0 : pane.getBoundingClientRect().top;
      const paneHeight = pane === null ? window.innerHeight : pane.clientHeight;
      const scrolled = pane === null ? window.scrollY : pane.scrollTop;
      const padding = pane === null ? 0 : Number.parseFloat(getComputedStyle(pane).paddingBottom) || 0;
      const listBox = list.getBoundingClientRect();
      const cardBottom = card.getBoundingClientRect().bottom;
      const top = listBox.top - paneTop + scrolled;
      // What the card holds under the list, and what the page keeps under the card (its own padding, anything after it).
      const below = cardBottom - listBox.bottom;
      const contentBottom = Math.max(cardBottom, ...Array.from((pane ?? document.body).children, (child) => child.getBoundingClientRect().bottom));
      const after = contentBottom - cardBottom;
      setMaxHeight(Math.max(MIN_LIST_HEIGHT, Math.floor(paneHeight - top - below - after - padding)));
    };
    measure();

    // The pane resizes with the window; what the pane holds resizes when the header above the list grows or shrinks.
    const observer = new ResizeObserver(measure);
    observer.observe(pane ?? document.documentElement);
    for (const child of Array.from((pane ?? document.body).children)) {
      observer.observe(child);
    }

    return () => observer.disconnect();
  }, [card, list]);

  return { cardRef: setCard, listRef: setList, maxHeight };
}

function matches(entry: Entry, filter: Filter): boolean {
  return filter === "all" || (filter === "people" ? entry.actor !== null : entry.lane === filter);
}

const EMPTY: Record<Exclude<Filter, "all">, { title: string; description: string }> = {
  source: { title: "No change recorded", description: "The ledger holds no version of this record's row in its ingestion table." },
  ledger: { title: "Nothing decided in the ledger", description: "The ledger has taken no step of its own on this record." },
  osdu: { title: "Nothing delivered yet", description: "The record has not been delivered, removed or verified." },
  people: { title: "Nobody asked for anything", description: "Nobody has redelivered, released, verified or removed this record." },
};

/** A moment in the time column: the local clock, with the day under it when the row's chapter began on another day. */
function TimeCell({ at, day }: { at: string; day: string | null }) {
  const date = parseUtc(at);
  const utc = date.toISOString().replace("T", " ").replace(/\.\d+Z$/, " UTC");
  return (
    <div className="flex flex-col items-end pr-2.5 pt-[5px]">
      <Tooltip>
        <TooltipTrigger asChild>
          <span className="font-mono text-[12px] tabular-nums">
            {format(date, "HH:mm")}<span className="text-muted-foreground">{format(date, ":ss")}</span>
          </span>
        </TooltipTrigger>
        <TooltipContent className="font-mono text-[11px]">{`${formatDistanceToNow(date, { addSuffix: true })} · ${utc}`}</TooltipContent>
      </Tooltip>
      {day !== null && <span className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">{day}</span>}
    </div>
  );
}

const PATH = "absolute bg-muted-foreground/40";

/**
 * The rail beside one row: the three lanes, the path from the row above (across to this row's lane at the top of the
 * row, then down to its node) and on to the row below, and the node.
 */
function RailCell({ lane, from, last, children }: { lane: Lane; from: Lane | null; last: boolean; children?: ReactNode }) {
  const x = LANE_X[lane];
  const left = from === null ? x : Math.min(LANE_X[from], x);
  const right = from === null ? x : Math.max(LANE_X[from], x);
  return (
    <div className="relative" aria-hidden="true">
      {LANES.map((l) => <span key={l} className="absolute inset-y-0 w-px bg-border/70" style={{ left: LANE_X[l] }} />)}
      {from !== null && right > left && <span className={cn(PATH, "top-0 h-0.5")} style={{ left: left - 1, width: right - left + 2 }} />}
      {from !== null && <span className={cn(PATH, "top-0 w-0.5")} style={{ left: x - 1, height: NODE_CENTRE }} />}
      {!last && <span className={cn(PATH, "bottom-0 w-0.5")} style={{ left: x - 1, top: NODE_CENTRE }} />}
      {children}
    </div>
  );
}

/** A node on the rail: a ring in the colour of how the step went, around its icon or the initial of who asked. */
function RailNode({ lane, tone, size = 22, className, children }: { lane: Lane; tone: Tone; size?: number; className?: string; children: ReactNode }) {
  return (
    <span
      className={cn("absolute z-[1] rounded-full bg-card", className)}
      style={{ left: LANE_X[lane] - size / 2, top: NODE_CENTRE - size / 2, width: size, height: size }}
    >
      <span className={cn("flex size-full items-center justify-center rounded-full border", TONE_RING[tone])}>{children}</span>
    </span>
  );
}

function ScopeChip({ scope }: { scope: number }) {
  return scope === 1
    ? (
      <span className="inline-flex items-center gap-1 whitespace-nowrap rounded-full border border-primary/45 bg-primary/10 px-1.5 text-[11px] font-medium text-primary" title="The request named this record alone" data-testid="journey-scope">
        <CircleDot className="size-3" />this record only
      </span>
    )
    : (
      <span className="inline-flex items-center gap-1 whitespace-nowrap rounded-full border border-warning/45 bg-warning/10 px-1.5 text-[11px] font-medium text-warning" title={`The request named ${scope} records at once`} data-testid="journey-scope">
        <Layers className="size-3" />{`${scope} records`}
      </span>
    );
}

function EntryRow({ entry, from, last, day, open, onToggle }: {
  entry: Entry; from: Lane | null; last: boolean; day: string | null; open: boolean; onToggle: () => void;
}) {
  const Icon = entry.icon;
  const title = <span className="text-[13.5px] font-semibold">{entry.title}</span>;
  return (
    <li className={ROW} data-testid={entry.testId} data-state={open ? "open" : "closed"}>
      <TimeCell at={entry.at} day={day} />
      <RailCell lane={entry.lane} from={from} last={last}>
        <RailNode lane={entry.lane} tone={entry.tone}>
          {Icon !== null
            ? <Icon className="size-3" />
            : <span className="text-[10px] font-semibold" title={entry.actor ?? undefined}>{actorInitial(entry.actor ?? "")}</span>}
        </RailNode>
      </RailCell>
      <div className={cn("flex min-w-0 flex-col gap-0.5 pl-1 pt-0.5", last ? "pb-1" : "pb-3.5")}>
        <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
          {entry.more !== null
            ? (
              <button
                type="button"
                className="flex min-w-0 cursor-pointer items-center gap-1.5 text-left hover:underline hover:decoration-border hover:underline-offset-4"
                onClick={onToggle}
                aria-expanded={open}
                data-testid="journey-event-toggle"
              >
                <ChevronRight className={cn("size-3.5 shrink-0 text-muted-foreground transition-transform motion-reduce:transition-none", open && "rotate-90")} />
                {title}
              </button>
            )
            : <span className="flex min-w-0 items-center gap-1.5"><span className="size-3.5 shrink-0" aria-hidden="true" />{title}</span>}
          {entry.actor !== null && (
            <span className="whitespace-nowrap rounded-full border px-1.5 text-[11px] font-medium text-muted-foreground" title={entry.actor} data-testid="journey-actor">
              {actorName(entry.actor)}
            </span>
          )}
          {entry.scope !== null && <ScopeChip scope={entry.scope} />}
        </div>
        <FactLine parts={entry.facts} />
        {entry.then !== null && (
          <span className={cn("flex items-baseline gap-1.5 text-[12.5px]", TONE_TEXT[entry.then.tone])}>
            <span className="text-muted-foreground" aria-hidden="true">↳</span>{entry.then.text}
          </span>
        )}
        {entry.error !== null && <span className="text-[12px] text-destructive" data-testid="journey-attempt-error">{entry.error}</span>}
        {open && entry.more !== null && (
          <div className="mt-1.5 rounded-md border bg-muted/30 px-3 py-2" data-testid="journey-event-detail">
            {entry.more}
          </div>
        )}
      </div>
    </li>
  );
}

/** When a chapter ran: the day and the span of clock it covered, or both days when it ran past midnight. */
function chapterWhen(entries: Entry[]): string {
  const first = entries[0].at;
  const last = entries[entries.length - 1].at;
  if (dayOf(first) !== dayOf(last)) {
    return `${dayOf(first)} ${clockOf(first)} to ${dayOf(last)} ${clockOf(last)}`;
  }

  return clockOf(first) === clockOf(last) ? `${dayOf(first)} · ${clockOf(first)}` : `${dayOf(first)} · ${clockOf(first)} to ${clockOf(last)}`;
}

function ChapterRow({ chapter, count, from }: { chapter: Chapter; count: number; from: Lane | null }) {
  const newest = chapter.entries[chapter.entries.length - 1].at;
  return (
    <li className={ROW} data-testid="journey-chapter" aria-label={`Chapter ${chapter.number}: ${chapter.trigger}, ${chapter.outcome.text}`}>
      <div className="flex justify-end pr-2.5 pt-[15px]">
        <span className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">{dayOf(newest)}</span>
      </div>
      <div className="relative" aria-hidden="true">
        {LANES.map((l) => <span key={l} className="absolute inset-y-0 w-px bg-border/70" style={{ left: LANE_X[l] }} />)}
        {from !== null && <span className={cn(PATH, "inset-y-0 w-0.5")} style={{ left: LANE_X[from] - 1 }} />}
      </div>
      <div className="min-w-0 py-1.5 pl-1">
        <div className="flex flex-wrap items-center gap-x-2.5 gap-y-1 rounded-md border bg-muted/40 px-2.5 py-1.5">
          <span className="grid size-5 place-items-center rounded-md border bg-card text-[11px] font-semibold tabular-nums" title={`Chapter ${chapter.number} of ${count}`}>
            {chapter.number}
          </span>
          <span className="text-[13px] font-semibold" data-testid="journey-chapter-trigger">{chapter.trigger}</span>
          <span className="text-muted-foreground" aria-hidden="true">→</span>
          <span className={cn("rounded-full border px-2 text-[11.5px] font-semibold", TONE_RING[chapter.outcome.tone])} data-testid="journey-chapter-outcome">
            {chapter.outcome.text}
          </span>
          <span className="ml-auto text-[12px] tabular-nums text-muted-foreground">{chapterWhen(chapter.entries)}</span>
        </div>
      </div>
    </li>
  );
}

/** The syncs that read the row's history from the ingestion table into the ledger: bookkeeping of the timeline itself. */
function SyncLine({ syncs, table }: { syncs: DeliveryActivity[]; table: string }) {
  if (syncs.length === 0) {
    return null;
  }

  const shown = syncs.slice(0, 3);
  return (
    <div className="grid grid-cols-[auto_minmax(0,1fr)] items-baseline gap-x-2 border-t pt-2 text-[12px] text-muted-foreground" data-testid="journey-syncs">
      <span className="inline-flex items-center gap-1.5 self-start">
        <RefreshCw className="size-3.5" />
        <span>Timeline synced from <Mono>{table}</Mono>:</span>
      </span>
      <span className="flex min-w-0 flex-col gap-0.5">
        {shown.map((sync) => (
          <span key={sync.activityId} className="inline-flex flex-wrap items-baseline gap-x-1.5">
            <RelativeTime value={sync.startedUtc} />
            <span>{`by ${actorName(sync.actor)}${sync.summary !== null ? `, ${sync.summary}` : ""}`}</span>
            <RunRef runId={sync.runId} />
          </span>
        ))}
        {syncs.length > shown.length && <span>{`and ${syncs.length - shown.length} earlier`}</span>}
      </span>
    </div>
  );
}

type Row =
  | { kind: "chapter"; chapter: Chapter }
  | { kind: "entry"; entry: Entry; day: string | null };

/**
 * What happened to the record, newest first, in chapters: each opens with what set the record moving (its row arrived,
 * changed or was deleted, or someone asked for a redelivery, a release or a removal) and says how it ended, and its
 * steps sit on the rail's three lanes, the row's ingestion table, the ledger and OSDU, with a path between them. Times
 * share one column. A step someone asked for names who asked and how many records the request named. Every entry opens
 * to the rest known about it (the runs a change came from, the steps a try took and what they answered, a request's
 * parameters). The last thing that happened heads the list; where the record stands is the page header's and the
 * milestones' to say, and the latest chapter's band says it while work on the record is in flight. The syncs that read
 * the row's history into the ledger sit under the list. The list takes the rest of the page and scrolls inside it, so
 * its filter stays in view, and narrows to one lane, or to the steps someone asked for.
 */
export function RecordJourney({ record, attempts, activities, chain }: {
  record: DeliveryRecord;
  attempts: DeliveryAttempt[] | undefined;
  activities: DeliveryActivity[] | undefined;
  /** The changes of the record's row in its ingestion table; undefined while they are being read. */
  chain: DeliveryRecordChain | undefined;
}) {
  const [filter, setFilter] = useState<Filter>("all");
  const [opened, setOpened] = useState<ReadonlySet<string>>(() => new Set());
  const { cardRef, listRef, maxHeight } = useFillHeight();
  const loaded = attempts !== undefined && activities !== undefined;
  const story = useMemo(
    () => (loaded ? buildStory(record, attempts, activities, chain) : null),
    [loaded, record, attempts, activities, chain]);

  const all = useMemo(() => story?.chapters.flatMap((chapter) => chapter.entries) ?? [], [story]);
  const count = (f: Exclude<Filter, "all">) => all.filter((entry) => matches(entry, f)).length;

  const rows = useMemo(() => {
    if (story === null) {
      return [];
    }

    const list: Row[] = [];
    for (const chapter of [...story.chapters].reverse()) {
      const shown = chapter.entries.filter((entry) => matches(entry, filter)).reverse();
      if (shown.length === 0) {
        continue;
      }

      list.push({ kind: "chapter", chapter });
      const chapterDay = dayOf(chapter.entries[chapter.entries.length - 1].at);
      for (const entry of shown) {
        const day = dayOf(entry.at);
        list.push({ kind: "entry", entry, day: day === chapterDay ? null : day });
      }
    }

    return list;
  }, [story, filter]);

  const toggle = (id: string) => setOpened((current) => {
    const next = new Set(current);
    if (next.has(id)) {
      next.delete(id);
    } else {
      next.add(id);
    }

    return next;
  });

  // The path runs through the rows in the order they are listed; the last row with a node ends it.
  let lastNode = -1;
  rows.forEach((row, index) => {
    if (row.kind !== "chapter") {
      lastNode = index;
    }
  });

  const listed: ReactNode[] = [];
  let from: Lane | null = null;
  if (story !== null) {
    rows.forEach((row, index) => {
      switch (row.kind) {
        case "chapter":
          listed.push(<ChapterRow key={`chapter-${row.chapter.number}`} chapter={row.chapter} count={story.chapters.length} from={from} />);
          break;
        case "entry":
          listed.push(
            <EntryRow
              key={row.entry.id}
              entry={row.entry}
              from={from}
              last={index === lastNode}
              day={row.day}
              open={opened.has(row.entry.id)}
              onToggle={() => toggle(row.entry.id)}
            />,
          );
          from = row.entry.lane;
          break;
      }
    });
  }

  return (
    <Card ref={cardRef} className="gap-2 rounded-lg p-3" data-testid="record-journey">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <ToggleGroup
          type="single"
          value={filter}
          onValueChange={(value) => { if (value !== "") { setFilter(value as Filter); } }}
          variant="outline"
          size="sm"
          data-testid="record-journey-filter"
        >
          <ToggleGroupItem value="all" className="px-2.5 text-xs">{`All (${all.length})`}</ToggleGroupItem>
          <ToggleGroupItem value="source" className="px-2.5 text-xs">{`Source (${count("source")})`}</ToggleGroupItem>
          <ToggleGroupItem value="ledger" className="px-2.5 text-xs">{`Ledger (${count("ledger")})`}</ToggleGroupItem>
          <ToggleGroupItem value="osdu" className="px-2.5 text-xs">{`OSDU (${count("osdu")})`}</ToggleGroupItem>
          <ToggleGroupItem value="people" className="px-2.5 text-xs">{`Asked for (${count("people")})`}</ToggleGroupItem>
        </ToggleGroup>
        <span className="ml-auto text-[12px] text-muted-foreground">Newest first, in local time</span>
      </div>
      {story === null
        ? <Skeleton className="h-24 w-full rounded-md" />
        : rows.length === 0
          ? (
            <EmptyState
              title={filter === "all" ? "Nothing recorded yet" : EMPTY[filter].title}
              description={filter === "all" ? undefined : EMPTY[filter].description}
            />
          )
          : (
            <div ref={listRef} className="overflow-y-auto pr-1" style={{ maxHeight: maxHeight ?? MIN_LIST_HEIGHT }}>
              <div className={cn(ROW, "sticky top-0 z-[2] bg-card pb-0.5 pt-1")} aria-hidden="true">
                <span />
                <div className="relative h-5">
                  {LANES.map((lane) => (
                    <span key={lane} className="absolute top-0.5 -translate-x-1/2 whitespace-nowrap text-[9.5px] font-semibold uppercase tracking-wide text-muted-foreground" style={{ left: LANE_X[lane] }}>
                      {LANE_LABEL[lane]}
                    </span>
                  ))}
                </div>
                <span className="pl-1 pt-0.5 text-[11px] text-muted-foreground">where each step happened</span>
              </div>
              <ol className="flex flex-col" data-testid="record-journey-events">
                {listed}
              </ol>
            </div>
          )}
      {story !== null && <SyncLine syncs={story.syncs} table={story.table} />}
      {loaded && chain !== undefined && (chain.note !== null || chain.truncated) && (
        <p className="text-[12px] text-muted-foreground" data-testid="record-chain-note">
          {chain.truncated && "The row changed more often than the timeline lists: its newest changes and its arrival are shown. "}
          {chain.note}
        </p>
      )}
    </Card>
  );
}
