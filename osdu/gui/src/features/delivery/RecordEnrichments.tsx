import type { UseQueryResult } from "@tanstack/react-query";
import { CircleCheck, CirclePause, History } from "lucide-react";
import { isApiError } from "@/api/client";
import { CopyButton } from "@/components/CopyButton";
import { CorrelationError } from "@/components/CorrelationError";
import { DataTable, type Column } from "@/components/DataTable";
import { RichTooltip } from "@/components/RichTooltip";
import { TruncatedText } from "@/components/TruncatedText";
import { Badge } from "@/components/ui/badge";
import type { DeliveryEnrichmentRecord, DeliveryRecordEnrichment } from "../../api/artifacts";
import { ArtifactStateBadge } from "./ArtifactMarks";
import { DimensionGrid } from "./dimensions/DimensionGrid";
import { addedKeyText, addsDetail, addsText, rowsText, writesText } from "./enrichmentText";
import { idParts } from "./osduRecordModel";

/** A neutral chip led by a glyph in its tone, as every state on the record's page is drawn: the tone is the glyph's alone. */
function StateChip({ icon: Icon, tone, word, title, testId }: { icon: typeof CircleCheck; tone: string; word: string; title: string; testId: string }) {
  return (
    <Badge variant="secondary" className="rounded-md border-border bg-muted/60 font-normal text-foreground" title={title} data-testid={testId}>
      <Icon className={`size-3.5 shrink-0 ${tone}`} aria-hidden />
      {word}
    </Badge>
  );
}

const enrichmentColumns: Column<DeliveryRecordEnrichment>[] = [
  {
    // An enrichment set aside on the record's last render says so under its name: the one state an enrichment has that a
    // reader acts on, and nothing for the rest.
    id: "name",
    header: "Enrichment",
    render: (row) => (
      <span className="inline-flex flex-col items-start gap-1">
        <span className="font-mono text-[12.5px]">{row.name}</span>
        {row.setAside && (
          <StateChip
            icon={CirclePause}
            tone="text-warning"
            word="set aside"
            title="It held on the record's last render and was set aside: the record went ahead with what it added to its last delivery. The Render tab says why it held."
            testId={`record-enrichment-aside-${row.name}`}
          />
        )}
      </span>
    ),
  },
  {
    id: "adds",
    header: "Adds",
    render: (row) => {
      const detail = addsDetail(row);
      return (
        <span className="inline-flex min-w-0 max-w-full flex-col leading-tight">
          <span className="text-[12.5px]">{addsText(row)}</span>
          {detail !== null && <TruncatedText text={detail} maxWidth={300} className="text-[11.5px] text-muted-foreground" title="What it adds" />}
        </span>
      );
    },
  },
  {
    id: "mapping",
    header: "Mapping",
    render: (row) => (row.mapping !== null
      ? <TruncatedText text={row.mapping} mono maxWidth={210} copy title={row.mappingFingerprint !== null ? `Enrichment mapping, fingerprint ${row.mappingFingerprint}` : "Enrichment mapping"} />
      : <span className="text-muted-foreground">-</span>),
  },
  {
    id: "rows",
    header: "Rows it read",
    fill: true,
    floor: 180,
    render: (row) => {
      const text = rowsText(row.rows);
      return text === null
        ? <span className="text-[12px] text-muted-foreground" title="It reads no rows, or the record has not been delivered with it yet">-</span>
        : <TruncatedText text={text} mono maxWidth={520} className="text-[12px]" title="Where the rows of the delivered record came from: the ingestion file and row" />;
    },
  },
];

/** One record an enrichment added, with the enrichment's name beside it, as the records grid lists them. */
interface AddedRow {
  enrichment: string;
  record: DeliveryEnrichmentRecord;
}

const recordColumns: Column<AddedRow>[] = [
  {
    id: "linked",
    header: "State",
    render: (row) => (row.record.linked
      ? (
        <StateChip
          icon={CircleCheck}
          tone="text-success"
          word="linked"
          title="The record links it now ($link in its mapping)."
          testId="record-enrichment-linked"
        />
      )
      : (
        <ArtifactStateBadge state="superseded" testId="record-enrichment-superseded">
          no longer linked
        </ArtifactStateBadge>
      )),
  },
  { id: "enrichment", header: "Enrichment", render: (row) => <span className="font-mono text-[12px]">{row.enrichment}</span> },
  {
    // The id every record an enrichment adds shares the enriched record's unique part, so the record is named by its type and
    // the row key that tells it from the others; the whole id is a hover and a copy away.
    id: "id",
    header: "Record",
    fill: true,
    floor: 200,
    render: (row) => <AddedRecord enrichment={row.enrichment} id={row.record.id} />,
  },
  {
    id: "version",
    header: "Version",
    align: "right",
    render: (row) => (row.record.version !== null
      ? <span className="font-mono text-[12px] tabular-nums">{row.record.version}</span>
      : <span className="text-[12px] text-muted-foreground" title="No version has landed yet">-</span>),
  },
  {
    id: "source",
    header: "From row",
    render: (row) => (row.record.source !== null
      ? <TruncatedText text={row.record.source} mono maxWidth={300} className="text-[12px]" title="The ingestion file and row it was rendered from" />
      : <span className="text-[12px] text-muted-foreground">-</span>),
  },
  {
    id: "writes",
    header: "Writes",
    align: "right",
    render: (row) => (row.record.writes.length === 0
      ? <span className="font-mono text-[12px] tabular-nums text-muted-foreground">0</span>
      : (
        <RichTooltip body={writesText(row.record.writes)} title="Every write deliveries made of it, newest first">
          <span className="inline-flex items-center gap-1 font-mono text-[12px] tabular-nums" data-testid="record-enrichment-writes">
            <History className="size-3.5 text-muted-foreground" aria-hidden />
            {row.record.writes.length.toLocaleString()}
          </span>
        </RichTooltip>
      )),
  },
];

/**
 * What the mapping's enrichments added to the record (osdu/docs/reference/flow/enrich.md, What the ledger keeps), read from its
 * ledger row: a line of counts, each enrichment in the order it renders with what it adds, the mapping that rendered it and
 * where its rows came from, then every record they added, those the record links now first within each enrichment, with the
 * version OSDU holds, the row it came from and every write made of it. Both grids fit what the window leaves and scroll in
 * place.
 */
export function RecordEnrichments({ query }: { query: UseQueryResult<DeliveryRecordEnrichment[]> }) {
  if (query.isError) {
    return isApiError(query.error)
      ? <CorrelationError error={query.error} />
      : <p className="text-[13px] text-destructive">{String(query.error)}</p>;
  }

  const enrichments = query.data;
  const added: AddedRow[] = (enrichments ?? []).flatMap((enrichment) => enrichment.records.map((record) => ({ enrichment: enrichment.name, record })));
  const linked = added.filter((row) => row.record.linked).length;
  const aside = (enrichments ?? []).filter((enrichment) => enrichment.setAside).length;
  return (
    <div className="flex flex-col gap-3" data-testid="record-enrichments">
      {enrichments !== undefined && enrichments.length > 0 && (
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-[12px]" data-testid="record-enrichment-counts">
          <Count value={enrichments.length} label={enrichments.length === 1 ? "enrichment" : "enrichments"} />
          <Count value={added.length} label={added.length === 1 ? "record added" : "records added"} />
          {added.length > 0 && <Count value={linked} label="linked" />}
          {added.length > linked && <Count value={added.length - linked} label="no longer linked, kept in OSDU" />}
          {aside > 0 && <Count value={aside} label="set aside on the last render" />}
        </div>
      )}
      <DimensionGrid testId="record-enrichments-grid">
        <DataTable
          columns={enrichmentColumns}
          rows={enrichments}
          rowKey={(row) => row.name}
          emptyMessage="Nothing was added to this record: its mapping declares no enrichment (enrich:)."
          data-testid="record-enrichments-table"
        />
      </DimensionGrid>
      {added.length > 0 && (
        <div className="flex flex-col gap-1.5">
          <h3 className="text-[13px] font-medium">Records the enrichments added</h3>
          <DimensionGrid testId="record-enrichment-records-grid">
            <DataTable
              columns={recordColumns}
              rows={added}
              rowKey={(row) => row.record.id}
              emptyMessage="No record was added."
              data-testid="record-enrichment-records-table"
            />
          </DimensionGrid>
        </div>
      )}
    </div>
  );
}

/**
 * A record an enrichment added, named by what tells it apart: its type in a whisper, then the key of the row it came from
 * (or that it is the one record the enrichment adds), the whole OSDU id on hover and on the copy beside it.
 */
function AddedRecord({ enrichment, id }: { enrichment: string; id: string }) {
  const key = addedKeyText(id, enrichment);
  return (
    <span className="inline-flex min-w-0 max-w-full items-center gap-0.5" data-testid="record-enrichment-record" data-value={id}>
      <RichTooltip body={id} title="OSDU id" mono>
        <span className="inline-flex min-w-0 items-baseline gap-1.5">
          <span className="min-w-0 truncate text-[12px]">{idParts(id).type || id}</span>
          {key !== null
            ? <span className="shrink-0 font-mono text-[11.5px] text-muted-foreground">{`row key ${key}`}</span>
            : <span className="shrink-0 text-[11.5px] text-muted-foreground">one for the record</span>}
        </span>
      </RichTooltip>
      <CopyButton label="Copy the OSDU id" text={id} testId="copy-record-enrichment-id" iconOnly />
    </span>
  );
}

/** A count as data and what it counts quietly after it; a zero takes no weight. */
function Count({ value, label }: { value: number; label: string }) {
  return (
    <span className="inline-flex items-baseline gap-1 whitespace-nowrap">
      <span className={`font-mono tabular-nums ${value === 0 ? "text-muted-foreground" : ""}`}>{value.toLocaleString()}</span>
      <span className="text-muted-foreground">{label}</span>
    </span>
  );
}
