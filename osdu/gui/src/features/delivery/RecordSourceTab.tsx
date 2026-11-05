import { Database, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { RecordContentView } from "./RecordContentView";
import { CopyButton } from "@/components/CopyButton";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import type { DeliveryRecord, DeliverySourceRows } from "../../api/delivery";
import { Fact, FactGrid, NoFact } from "./Facts";
import { ProblemView } from "./TemplateSheet";

function SectionHeading({ title, description }: { title: string; description?: string }) {
  return (
    <div className="flex flex-wrap items-baseline gap-2">
      <h3 className="text-[13px] font-medium">{title}</h3>
      {description !== undefined && <span className="text-[12px] text-muted-foreground">{description}</span>}
    </div>
  );
}

/** An ingestion file and the row in it, on one line, the file clipped at its cell and copyable whole. */
function FileRow({ file, row, copyTestId, testId }: { file: string | null; row: number | null; copyTestId?: string; testId?: string }) {
  if (file === null) {
    return <NoFact>not recorded</NoFact>;
  }

  // Two tracks, the file's sized by the cell rather than by its content, so the file clips there and the row number
  // keeps its place after it: a content-sized wrapper would size to the whole file name and run out of the cell.
  return (
    <span className="grid max-w-full items-baseline gap-x-1 [grid-template-columns:minmax(0,max-content)_auto]" data-testid={testId}>
      <TruncatedText text={file} mono maxWidth={360} copy copyTestId={copyTestId} title="Ingestion file" />
      {row !== null && <span className="text-muted-foreground">row <span className="font-mono">{row}</span></span>}
    </span>
  );
}

/**
 * The parts of a record's key tuple: the ledger keeps the tuple as a JSON array of the key columns' values, in the order
 * the flow's `source.record.key` names the columns. Null when the stored text is not such an array.
 */
function keyParts(json: string): string[] | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(json);
  } catch {
    return null;
  }

  if (!Array.isArray(parsed) || parsed.length === 0) {
    return null;
  }

  const parts: string[] = [];
  for (const part of parsed) {
    if (part !== null && typeof part === "object") {
      return null;
    }

    parts.push(part === null ? "" : String(part));
  }

  return parts;
}

/**
 * The key columns that find the record's row in the ingestion tables, each value beside the column it is a value of,
 * with a copy of the tuple as the ledger holds it (what a preview or a key-scoped read is given). The columns are the
 * flow's as it declares them now, so a tuple keyed before the flow's key changed shows its values alone, with the
 * columns the flow keys on now; a tuple that does not read as one is shown as the ledger stored it.
 */
function KeyColumns({ json, columns }: { json: string; columns: string[] | null }) {
  const parts = keyParts(json);
  if (parts === null) {
    return <TruncatedText text={json} mono maxWidth={720} copy copyTestId="copy-record-key-columns" title="Key columns" />;
  }

  const named = columns !== null && columns.length === parts.length ? columns : null;
  return (
    <span className="flex flex-wrap items-center gap-1.5">
      {parts.map((value, i) => (
        <span
          // The tuple's parts are positional, so the position is what tells two equal values apart.
          key={i}
          className="inline-flex max-w-full items-baseline gap-1.5 rounded border border-border bg-muted/40 px-1.5 font-mono text-[12px] leading-5"
          data-testid="record-key-part"
        >
          {named !== null && <span className="shrink-0 text-muted-foreground">{named[i]}</span>}
          {value === ""
            ? <span className="text-muted-foreground">empty</span>
            : <TruncatedText text={value} mono maxWidth={360} />}
        </span>
      ))}
      <CopyButton iconOnly label="Copy the key as the ledger holds it" text={json} testId="copy-record-key-columns" />
      {named === null && columns !== null && columns.length > 0 && (
        <span className="text-muted-foreground">
          {"keyed before the flow's key changed; it keys on "}
          <span className="font-mono">{columns.join(", ")}</span>
          {" now"}
        </span>
      )}
    </span>
  );
}

/**
 * Where the record came from: the ingestion file and row the delivered document was built from (and the newer row a
 * waiting document is built from), the source key that names it, the key columns that find it in the ingestion tables,
 * and a read of its rows as those tables hold them now, with the flow's own connection.
 */
export function RecordSourceTab({ record, keyColumns, canRead, reading, onRead, answer }: {
  record: DeliveryRecord;
  /** The columns the flow's `source.record.key` names, in the order of the record's key tuple; null when unknown. */
  keyColumns: string[] | null;
  /** Whether the read can be asked for: the record's flow is known and no other request of the page is in flight. */
  canRead: boolean;
  reading: boolean;
  onRead: () => void;
  /** What the last read answered: the rows, or why there are none; null before any read has answered. */
  answer: { rows: DeliverySourceRows | null; error?: unknown } | null;
}) {
  // A record that has not been delivered has only the pending row, which is then simply where it came from; a newer
  // row is a fact only once there is a delivered row for it to be newer than.
  const newerRow = record.sourceUpdatedUtc !== null && record.pendingSourceUpdatedUtc !== null && record.pendingSourceUpdatedUtc !== record.sourceUpdatedUtc;
  const file = record.sourceFileName ?? record.pendingSourceFileName;
  const row = record.sourceFileName !== null ? record.sourceRowNumber : record.pendingSourceRowNumber;

  return (
    <div className="flex flex-col gap-3">
      <Card className="gap-2 rounded-lg p-3">
        <SectionHeading title="Where the row came from" description="the file and row the ledger records against every delivered version" />
        {/* Three to a row where the width allows: where the row came from, then how the ledger names the record, then
            the key columns the name is made of. */}
        <FactGrid>
          <Fact label="Ingestion file"><FileRow file={file} row={row} copyTestId="copy-record-origin" testId="record-origin" /></Fact>
          <Fact label="Row received"><RelativeTime value={record.sourceUpdatedUtc ?? record.pendingSourceUpdatedUtc} /></Fact>
          <Fact label="Source modified"><RelativeTime value={record.sourceModifiedUtc} /></Fact>
          <Fact label="Source key"><TruncatedText text={record.sourceKey} mono maxWidth={360} copy copyTestId="copy-record-source-key" title="Source key" /></Fact>
          <Fact label="Delivery key"><TruncatedText text={record.deliveryKey} mono maxWidth={360} copy copyTestId="copy-record-key" title="Delivery key" /></Fact>
          <Fact label="Mapping">{record.mappingName}</Fact>
          <Fact label="Key columns" wide testId="record-key-columns">
            {record.sourceKeyJson !== null ? <KeyColumns json={record.sourceKeyJson} columns={keyColumns} /> : <NoFact>not recorded</NoFact>}
          </Fact>
        </FactGrid>
        {newerRow && (
          <div className="flex flex-col gap-1.5 rounded-md border border-info/40 px-3 py-2" data-testid="record-newer-row">
            <SectionHeading title="A newer row is waiting" description="the version the waiting document is built from" />
            <FactGrid>
              <Fact label="Ingestion file"><FileRow file={record.pendingSourceFileName} row={record.pendingSourceRowNumber} /></Fact>
              <Fact label="Row received"><RelativeTime value={record.pendingSourceUpdatedUtc} /></Fact>
            </FactGrid>
          </div>
        )}
      </Card>

      <Card className="gap-2 rounded-lg p-3">
        <SectionHeading title="As the ingestion tables hold it now" description="read with the flow's own connection; nothing is written" />
        <div>
          <Button variant="outline" size="sm" onClick={onRead} disabled={!canRead || reading} data-testid="record-read-source">
            {reading ? <Loader2 className="animate-spin" /> : <Database />}
            {answer === null ? "Read the source row" : "Read again"}
          </Button>
        </div>
        {answer?.error !== undefined && <ProblemView error={answer.error} testId="record-source-error" />}
        {answer?.rows && <RecordContentView value={JSON.stringify(answer.rows, null, 2)} language="json" height={360} data-testid="record-source-task-json" />}
      </Card>
    </div>
  );
}
