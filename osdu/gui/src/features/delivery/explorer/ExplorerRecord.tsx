import { useCallback, useEffect, useState, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft, Loader2, RefreshCw, SearchCheck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { IconAction } from "@/components/IconAction";
import { explorerApi, type ExplorerHit, type ExplorerPage, type ExplorerRead, type ExplorerValidation } from "../../../api/explorer";
import { idParts, recordNameOf } from "../osduRecordModel";
import type { InspectorField } from "../OsduRecordInspector";
import { OsduRecordPanel, type RecordFollow } from "../OsduRecordView";
import { ExplorerProblem, ExplorerErrorText } from "./ExplorerProblem";
import { ExplorerValidationView, ValidationFieldMark } from "./ExplorerValidation";
import { ScopeCrumbs } from "./ExplorerResults";
import { counted, kindParts, rememberRecord, typeScopeOf, useExplorerRead, type ExplorerScope } from "./explorerModel";

/** A list of records as the record view lists them (the ones that mention it, the ones near an id): name, type and id. */
function HitList({ hits, onOpen, testId }: { hits: ExplorerHit[]; onOpen: (id: string) => void; testId: string }) {
  // One row per record, however the service answered.
  const unique = [...new Map(hits.map((hit) => [hit.id, hit])).values()];
  return (
    <div className="flex flex-col" data-testid={testId}>
      {unique.map((hit) => {
        const parts = kindParts(hit.kind ?? "");
        return (
          <button
            key={hit.id}
            type="button"
            onClick={() => onOpen(hit.id)}
            className="grid min-w-0 cursor-pointer grid-cols-[minmax(0,2fr)_minmax(0,1fr)_minmax(0,1.4fr)] items-center gap-3 border-b px-3 py-1.5 text-left text-[12px] last:border-b-0 hover:bg-accent/50"
            title={hit.id}
            data-testid={`${testId}-item`}
          >
            <span className="min-w-0 truncate text-primary">{hit.name ?? idParts(hit.id).unique}</span>
            <span className="min-w-0 truncate text-muted-foreground">{parts.type}</span>
            <span dir="rtl" className="min-w-0 truncate text-left font-mono text-[11px] text-muted-foreground"><bdi dir="ltr">{idParts(hit.id).unique}</bdi></span>
          </button>
        );
      })}
    </div>
  );
}

/**
 * The records whose values name this one, found by the search (a phrase of its id in any property, the record itself left
 * out): the other way along the links the record's own references go. The first hundred are listed, each opened on the
 * trail after this record; the count says how many there are, by type, and all of them open as a search of their own.
 */
function Mentions({ partition, id, onOpen, onBrowse }: {
  partition: string | null;
  id: string;
  onOpen: (id: string) => void;
  onBrowse: (query: string) => void;
}) {
  const read = useExplorerRead<ExplorerPage>(
    ["mentions", partition, id],
    () => explorerApi.search(partition, { mentions: id, limit: 100, facet: { path: "kind", index: "keyword" } }),
  );
  const page = read.data?.answer;
  if (read.isPending) {
    return (
      <div className="flex items-center gap-2 p-3 text-[12px] text-muted-foreground" data-testid="explorer-mentions-loading">
        <Loader2 className="size-3.5 animate-spin" />
        Finding the records that mention this one
      </div>
    );
  }

  if (read.isError) {
    return <div className="p-3"><ExplorerProblem error={read.error} /></div>;
  }

  if (page?.refusal) {
    return <p className="p-3 text-[12px] text-destructive">{`The search service would not look for the records that mention this one: ${page.refusal}`}</p>;
  }

  if (page === undefined || page.total === 0) {
    return <p className="p-3 text-[12px] text-muted-foreground" data-testid="explorer-mentions-none">No other record names this one.</p>;
  }

  return (
    <div className="flex flex-col" data-testid="explorer-mentions">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b px-3 py-2 text-[12px]">
        <span className="font-medium">{counted(page.total, "record")}</span>
        {(page.facet ?? []).map((bucket) => (
          <span key={bucket.value ?? ""} className="text-muted-foreground" title={bucket.value ?? undefined}>
            {`${kindParts(bucket.value ?? "").type} ${bucket.count.toLocaleString("en-US")}`}
          </span>
        ))}
        {page.total > page.hits.length && page.query !== null && (
          <Button variant="link" size="sm" className="ml-auto h-auto p-0 text-[12px]" onClick={() => onBrowse(page.query!)} data-testid="explorer-mentions-browse">
            {`Browse all ${page.total.toLocaleString("en-US")}`}
          </Button>
        )}
      </div>
      <HitList hits={page.hits} onOpen={onOpen} testId="explorer-mention" />
    </div>
  );
}

/**
 * What OSDU holds near an id it holds no record under: the ids that go on from it (a paste cut short) and the same unique
 * part under another type, as the search reads a whole id. A near record opens in place of the one asked for.
 */
function NearIds({ partition, id, onOpen }: { partition: string | null; id: string; onOpen: (id: string) => void }) {
  const read = useExplorerRead<ExplorerPage>(["near", partition, id], () => explorerApi.search(partition, { text: id, limit: 20 }));
  const near = (read.data?.answer.hits ?? []).filter((hit) => hit.id !== id);
  return (
    <div className="mx-3 mb-3 rounded-md border" data-testid="explorer-near">
      <div className="flex items-center gap-2 border-b px-3 py-1.5 text-[12px] font-medium">
        <SearchCheck className="size-3.5 text-muted-foreground" />
        Records with a near id
        {read.isPending && <Loader2 className="size-3.5 animate-spin text-muted-foreground" />}
      </div>
      {read.isError && <ExplorerErrorText error={read.error} className="px-3 py-2" />}
      {read.data !== undefined && near.length === 0 && (
        <p className="px-3 py-2 text-[12px] text-muted-foreground" data-testid="explorer-near-none">
          No id starts with it, and no other type holds its unique part.
        </p>
      )}
      {near.length > 0 && <HitList hits={near} onOpen={onOpen} testId="explorer-near" />}
    </div>
  );
}

/**
 * One record of the explorer, read from OSDU's storage service through the partition's connection, in the record
 * inspector the record pages use: its place leads the location (the partition, the group, the type, each a step back to
 * the records there), its links open records after it on the trail, the records that mention it are a view of its own,
 * any two of its versions compare side by side, and an id OSDU holds nothing under offers the records whose ids are near.
 * Every record opened is remembered in this browser, so the reader finds it again. A page that shows it in a panel of its
 * own, which its own control closes, gives no way back.
 */
export function ExplorerRecord({ partition, id, version, onBack, onScope, onOpenId, onBrowseQuery, onSwitchPartition, fieldActions, follow = null, onFollowed }: {
  partition: string | null;
  id: string;
  /** The version the record is opened at; null for its latest. */
  version: number | null;
  /** Leaves the record for the records it was opened from, as they were left; none where the record is in a panel of its own. */
  onBack?: () => void;
  /** Leaves the record for the records of a place. */
  onScope: (scope: ExplorerScope) => void;
  /** Opens another record in this one's place. */
  onOpenId: (id: string) => void;
  /** Leaves the record for the records a Lucene query finds. */
  onBrowseQuery: (query: string) => void;
  onSwitchPartition: (partition: string) => void;
  /** What shows beside each value of the records on the trail: a dimension's picks, while one is built. */
  fieldActions?: (field: InspectorField) => ReactNode;
  /** A record to open on the trail after this one, from the value of it naming the record: the dimension builder's ask. */
  follow?: RecordFollow | null;
  onFollowed?: () => void;
}) {
  const answered = useQuery({
    queryKey: ["explorer", "read", partition, id, version],
    queryFn: () => explorerApi.read(partition, id, version ?? undefined),
    // A record is read once per visit; reading again is the reader's call.
    staleTime: Number.POSITIVE_INFINITY,
    gcTime: 60_000,
    retry: false,
    refetchOnWindowFocus: false,
  });
  const read: ExplorerRead | null = answered.data ?? null;

  // The last check of the record, kept so its fields carry the marks of the problems it found while the record is in view.
  const [validated, setValidated] = useState<ExplorerValidation | null>(null);
  const keepValidated = useCallback((result: ExplorerValidation | null) => setValidated(result), []);

  useEffect(() => {
    if (read?.found && read.record) {
      rememberRecord({ id: read.targetId, name: recordNameOf(read.record), kind: typeof read.record.kind === "string" ? read.record.kind : null, partition }, read.readUtc);
    }
  }, [read, partition]);

  const parts = idParts(id);
  const entityType = parts.group === "" ? parts.type : `${parts.group}--${parts.type}`;
  const elsewhere = parts.partition !== "" && partition !== null && parts.partition !== partition ? parts.partition : null;
  const reading = answered.isFetching;
  const place = (
    <span className="inline-flex min-w-0 max-w-full flex-wrap items-center gap-x-1" data-testid="explorer-record-place">
      {onBack !== undefined && (
        <button
          type="button"
          onClick={onBack}
          className="mr-1 inline-flex size-6 shrink-0 items-center justify-center rounded-md text-muted-foreground hover:bg-accent hover:text-foreground"
          aria-label="Back to the records"
          title="Back to the records, as they were left"
          data-testid="explorer-record-back"
        >
          <ArrowLeft className="size-4" />
        </button>
      )}
      <ScopeCrumbs
        partition={partition}
        // The record's own kind places it, a type naming no group within its namespace; until it is read, its id does.
        scope={typeof read?.record?.kind === "string" ? typeScopeOf(read.record.kind) : entityType === "" ? { level: "all" } : { level: "type", entityType }}
        onScope={onScope}
        last={false}
      />
    </span>
  );
  const actions: ReactNode = (
    <IconAction
      label="Read again from OSDU"
      icon={reading ? <Loader2 className="animate-spin" /> : <RefreshCw />}
      variant="ghost"
      className="size-7"
      disabled={reading}
      onClick={() => void answered.refetch()}
      data-testid="explorer-record-refresh"
    />
  );

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-2" data-testid="explorer-record">
      {elsewhere !== null && (
        <div className="flex flex-wrap items-center gap-2 rounded-md border border-warning/40 bg-warning/10 px-3 py-1.5 text-[12px]" data-testid="explorer-record-elsewhere">
          {`This id is of partition ${elsewhere}, and the explorer reads ${partition}.`}
          <Button variant="outline" size="sm" className="h-6 px-2 text-[12px]" onClick={() => onSwitchPartition(elsewhere)}>{`Read it in ${elsewhere}`}</Button>
        </div>
      )}
      {/* A read that fails says so under the record's place, as the inspector says why a record could not be read. */}
      <div className="flex min-h-0 flex-1 flex-col">
        <OsduRecordPanel
          key={answered.dataUpdatedAt}
          readLinked={(linked, linkedVersion) => explorerApi.read(partition, linked, linkedVersion)}
          root={{ read: answered.data, error: answered.error ?? undefined, pending: answered.isPending }}
          targetId={id}
          readRootVersion={(picked) => explorerApi.read(partition, id, picked)}
          ledgerVersion={null}
          actions={actions}
          extras={{
            place,
            mentions: (mentioned, open) => <Mentions partition={partition} id={mentioned} onOpen={open} onBrowse={onBrowseQuery} />,
            notFound: (missing) => <NearIds partition={partition} id={missing} onOpen={onOpenId} />,
            validation: (checked, shownVersion, openPath) => (
              <ExplorerValidationView
                pickMapping
                source={{ key: ["partition", partition], validate: (asked) => explorerApi.validate(partition, asked) }}
                id={checked}
                version={shownVersion}
                onOpenPath={openPath}
                onResult={checked === id ? keepValidated : undefined}
              />
            ),
            fieldActions: (field) => (
              <span className="inline-flex items-center gap-1">
                <ValidationFieldMark result={validated} id={id} field={field} />
                {fieldActions?.(field)}
              </span>
            ),
          }}
          follow={follow}
          onFollowed={onFollowed}
          fill
        />
      </div>
    </div>
  );
}
