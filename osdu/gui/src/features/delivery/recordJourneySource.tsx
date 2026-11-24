// The entries of a record's timeline (RecordJourney) for its row: each change of the row in its ingestion table, the
// landing its arrival came in by, and the ledger picking the record up.

import { ArchiveRestore, Database, DatabaseZap, FileInput, FilePen, FileX2, ScanSearch, type LucideIcon } from "lucide-react";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import type {
  DeliveryChainLanding, DeliveryRecord, DeliveryRecordChain, DeliverySourceChange, DeliverySourceChangeKind,
} from "../../api/delivery";
import { RunRef } from "./DeliveryRefs";
import { Fact, FactGrid, NoFact } from "./Facts";
import { RecordName } from "./RecordName";
import { fileSize, ingestionTableName, isArrival, runDuration } from "./recordStory";
import { type Entry, entryDefaults, type Tone } from "./recordJourneyEntry";
import { FlowRef, Mono, Origin, RunFacts } from "./RecordJourneyParts";

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
export function sourceEntries(chain: DeliveryRecordChain | undefined): Entry[] {
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
export function pickedUpEntry(record: DeliveryRecord): Entry {
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
