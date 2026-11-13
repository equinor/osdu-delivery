import { useDeferredValue, useMemo, useState, type ReactNode } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Archive, Download, FlaskConical, Info, Loader2, RefreshCw } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { downloadText } from "@/lib/download";
import { DataTable, type Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { IconAction } from "@/components/IconAction";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { StatePill } from "@/components/StatusBadge";
import { deliveryApi } from "../../api/delivery";
import { explorerApi, type ExplorerSchemaKind } from "../../api/explorer";
import { DimensionGrid } from "./dimensions/DimensionGrid";
import { KindText } from "./KindText";
import { kindStem, templateFileName } from "./templateFormat";
import { problemText } from "./problemText";
import { ProblemView, TaskProgress, TemplateSheet } from "./TemplateSheet";

/** The most rows the table renders at once; the search narrows the rest. */
const MAX_ROWS = 200;

/** The authority of the platform's own schemas; a kind of any other is the partition's own. */
const OSDU_AUTHORITY = "osdu";

/** Where Browse OSDU reads schemas from: a release of the OSDU data definitions, or the partition's Schema service. */
export type BrowseSource = "definitions" | "partition";

/** The choice of where Browse OSDU reads schemas from, offered when the partition in the title bar can be read. */
export function BrowseSourcePicker({ value, onChange, partition }: { value: BrowseSource; onChange: (source: BrowseSource) => void; partition: string }) {
  return (
    <ToggleGroup
      type="single"
      variant="outline"
      size="sm"
      value={value}
      onValueChange={(next) => {
        if (next !== "") {
          onChange(next as BrowseSource);
        }
      }}
      aria-label="Where the schemas are read from"
      data-testid="templates-browse-source"
    >
      <ToggleGroupItem
        value="definitions"
        className="h-8 text-[13px]"
        title="The OSDU standard schemas: the OSDU data definitions, the Open Group's public repository, release by release. A partition's custom schemas are not here."
        data-testid="templates-browse-source-definitions"
      >
        OSDU standard
      </ToggleGroupItem>
      <ToggleGroupItem
        value="partition"
        className="h-8 gap-1.5 text-[13px]"
        title={`Every schema registered in partition ${partition}'s Schema service: the OSDU standard ones it holds, and its custom ones, which only a partition holds`}
        data-testid="templates-browse-source-partition"
      >
        Registered in <span className="font-mono">{partition}</span>
      </ToggleGroupItem>
    </ToggleGroup>
  );
}

/**
 * A schema's kind, and its status after it when that is not the usual one: a published schema says nothing, one in
 * development or obsolete stands out beside its kind.
 */
export function KindStatusCell({ kind, status }: { kind: string; status: string | null | undefined }) {
  const usual = status === null || status === undefined || status === "PUBLISHED";
  const obsolete = status === "OBSOLETE";
  return (
    <span className="flex min-w-0 items-center gap-2">
      <span className="min-w-0 shrink"><KindText kind={kind} /></span>
      {!usual && (
        <span className="shrink-0">
          <StatePill
            tone={obsolete ? "muted" : "warning"}
            label={obsolete ? "Obsolete" : status === "DEVELOPMENT" ? "In development" : status ?? ""}
            icon={obsolete ? Archive : FlaskConical}
            testId={`templates-browse-status-${kind}`}
          />
        </span>
      )}
    </span>
  );
}

/** A version as numbers, for the newest first: 1.10.0 after 1.9.0. */
function versionOrder(a: string, b: string): number {
  const left = a.split(".").map(Number);
  const right = b.split(".").map(Number);
  for (let i = 0; i < Math.max(left.length, right.length); i++) {
    const difference = (left[i] ?? 0) - (right[i] ?? 0);
    if (difference !== 0) {
      return difference;
    }
  }

  return 0;
}

/** A kind of the partition in the table, and how many versions of it (by authority, source and entity type) the partition holds. */
interface PartitionRow {
  schema: ExplorerSchemaKind;
  versions: number;
}

/**
 * Browse OSDU over the partition picked in the title bar: every schema its Schema service holds, the platform's and the
 * partition's own custom ones (any authority but osdu), whatever their status, whether or not any record is of them. A
 * kind opens laid out as a template, read from the Schema service bundled with every schema it refers to, and downloads as
 * the file a repository's template folder keeps, as a release's does. The control plane keeps the listing of the
 * partition's schemas for ten minutes; List again reads it anew.
 */
export function PartitionSchemaBrowse({ partition, sourcePicker, customOnlyAtFirst = false }: {
  partition: string;
  sourcePicker: ReactNode;
  /** Opens on the custom schemas alone: asked for from the standard schemas' summary. */
  customOnlyAtFirst?: boolean;
}) {
  const queryClient = useQueryClient();
  const [search, setSearch] = useState("");
  const [allVersions, setAllVersions] = useState(false);
  const [customOnly, setCustomOnly] = useState(customOnlyAtFirst);
  const [viewing, setViewing] = useState<string | null>(null);
  const term = useDeferredValue(search);

  const listingKey = ["explorer", "schemas", partition] as const;
  const listing = useQuery({
    queryKey: listingKey,
    queryFn: () => explorerApi.schemas(partition),
    staleTime: 5 * 60_000,
    retry: false,
    // While the first listing is made the answer holds none; it is asked again until it does.
    refetchInterval: (query) => (query.state.data?.answer.state === "reading" ? 2_000 : false),
  });
  const relist = useMutation({
    mutationFn: () => explorerApi.schemas(partition, true),
    onSuccess: (answered) => queryClient.setQueryData(listingKey, answered),
    onError: (error) => toast.error(`The partition's schemas could not be listed: ${problemText(error)}`),
  });

  const schemaQuery = (kind: string) => ({
    queryKey: ["explorer", "schema", partition, kind] as const,
    queryFn: () => explorerApi.schema(partition, kind),
    staleTime: 60_000,
    retry: false,
  });
  const file = useQuery({ ...schemaQuery(viewing ?? ""), enabled: viewing !== null });
  const parsed = useMemo((): Record<string, unknown> | null => {
    if (file.data === undefined) {
      return null;
    }

    try {
      return JSON.parse(file.data.answer.file) as Record<string, unknown>;
    } catch {
      return null;
    }
  }, [file.data]);
  const preview = useQuery({
    queryKey: ["delivery", "templates", "preview", "partition", partition, viewing, file.data?.answeredUtc ?? null],
    queryFn: () => deliveryApi.previewTemplate(viewing!, parsed!),
    enabled: viewing !== null && parsed !== null,
    retry: false,
  });

  // A kind as the file a template folder keeps, named after its kind: bundled by the control plane, as the Schema service holds it.
  const download = useMutation({
    mutationFn: async (kind: string) => {
      const answered = await queryClient.fetchQuery(schemaQuery(kind));
      downloadText(templateFileName(kind), answered.answer.file, "application/json");
    },
    onSuccess: (_result, kind) => toast.success(
      `Downloaded ${templateFileName(kind)}. Put it in a folder named templates beside the documents that fill it, commit it, and sync the repository.`),
    onError: (error) => toast.error(`The schema could not be downloaded: ${problemText(error)}`),
  });

  const answer = listing.data?.answer;
  // Each kind's versions together, the newest first, so the first of a kind is its newest.
  const ordered = useMemo(() => [...(answer?.kinds ?? [])].sort((a, b) => {
    const stem = kindStem(a.kind).localeCompare(kindStem(b.kind), "en", { sensitivity: "base" });
    return stem !== 0 ? stem : versionOrder(b.version, a.version);
  }), [answer]);
  const counts = useMemo(() => {
    const byStem = new Map<string, number>();
    for (const schema of ordered) {
      byStem.set(kindStem(schema.kind), (byStem.get(kindStem(schema.kind)) ?? 0) + 1);
    }

    return byStem;
  }, [ordered]);
  const customCount = useMemo(() => ordered.filter((schema) => schema.authority !== OSDU_AUTHORITY).length, [ordered]);

  const rows = useMemo((): PartitionRow[] | undefined => {
    if (answer === undefined || answer.state === "reading") {
      return undefined;
    }

    const terms = term.trim().toLowerCase().split(/\s+/).filter((part) => part !== "");
    const shown = new Set<string>();
    const result: PartitionRow[] = [];
    for (const schema of ordered) {
      if (customOnly && schema.authority === OSDU_AUTHORITY) {
        continue;
      }

      const stem = kindStem(schema.kind);
      if (!allVersions) {
        if (shown.has(stem)) {
          continue;
        }

        shown.add(stem);
      }

      const kind = schema.kind.toLowerCase();
      if (terms.every((part) => kind.includes(part))) {
        result.push({ schema, versions: counts.get(stem) ?? 1 });
      }
    }

    return result;
  }, [answer, ordered, term, allVersions, customOnly, counts]);

  const columns: Column<PartitionRow>[] = [
    { id: "kind", header: "Kind", fill: true, floor: 200, render: (row) => <KindStatusCell kind={row.schema.kind} status={row.schema.status} /> },
    ...(allVersions ? [] : [{
      id: "versions",
      header: "Versions",
      align: "right" as const,
      render: (row: PartitionRow) => (
        <span className="tabular-nums text-muted-foreground">{row.versions} version{row.versions === 1 ? "" : "s"}</span>
      ),
    }]),
    {
      id: "actions",
      header: "",
      align: "right",
      render: (row) => (
        <span className="inline-flex items-center gap-0.5 text-muted-foreground">
          <IconAction
            label={`Download ${templateFileName(row.schema.kind)} for a repository's template folder`}
            icon={download.isPending && download.variables === row.schema.kind ? <Loader2 className="animate-spin" /> : <Download />}
            className="text-muted-foreground"
            onClick={(event) => { event.stopPropagation(); download.mutate(row.schema.kind); }}
            disabled={download.isPending}
            data-testid={`templates-partition-download-${row.schema.kind}`}
          />
        </span>
      ),
    },
  ];

  // What the sheet shows while the schema is on its way, and what went wrong when it does not arrive.
  let progress: ReactNode = null;
  let problem: ReactNode = null;
  if (viewing !== null) {
    if (file.isError) {
      problem = <ProblemView error={file.error} testId="templates-partition-fetch-error" />;
    } else if (file.data === undefined) {
      progress = <TaskProgress label={`Reading ${viewing} and every schema it refers to from partition ${partition}'s Schema service`} testId="templates-partition-fetch-progress" />;
    } else if (parsed === null) {
      problem = <ProblemView error={new Error("The Schema service answered with a schema that is not JSON.")} testId="templates-partition-fetch-error" />;
    } else if (preview.isError) {
      problem = <ProblemView error={preview.error} testId="templates-partition-preview-error" />;
    } else if (preview.data === undefined) {
      progress = <TaskProgress label="Laying the schema out as a template" testId="templates-partition-preview-progress" />;
    }
  }

  const shownRows = rows?.slice(0, MAX_ROWS);
  const read = file.data?.answer;
  // What the file says other than the service's text: the kind it names in place of the one the schema named.
  if (problem === null && read !== undefined && read.notes.length > 0) {
    problem = (
      <div className="flex items-start gap-2 rounded-md border px-3 py-2 text-[12px] text-muted-foreground" data-testid="templates-partition-notes">
        <Info className="mt-0.5 size-3.5 shrink-0" aria-hidden />
        <div className="flex flex-col gap-1">{read.notes.map((note) => <p key={note}>{note}</p>)}</div>
      </div>
    );
  }
  const reading = answer?.state === "reading" || (listing.data === undefined && listing.isFetching);

  return (
    <div className="flex flex-col gap-3" data-testid="templates-browse">
      <Card className="gap-2 rounded-lg p-3" data-testid="templates-partition-form">
        <FilterBar>
          <SearchInput
            value={search}
            onChange={setSearch}
            placeholder="Completion, Wellbore, reference-data..."
            label="Search the kinds registered in the partition"
            className="sm:w-80"
            testId="templates-browse-search"
          />
          {sourcePicker}
          <Label className="flex items-center gap-2 text-[13px] font-normal" title={`Only the schemas of an authority other than osdu: those registered for ${partition} alone, published in no OSDU release`}>
            <Switch checked={customOnly} onCheckedChange={setCustomOnly} data-testid="templates-partition-custom" />
            Custom schemas only
          </Label>
          <Label className="flex items-center gap-2 text-[13px] font-normal">
            <Switch checked={allVersions} onCheckedChange={setAllVersions} data-testid="templates-browse-all-versions" />
            Every version
          </Label>
          <div className="flex items-center gap-2 sm:ml-auto">
            {answer?.readUtc != null && (
              <span className="text-xs text-muted-foreground" data-testid="templates-partition-listed">
                Listed <RelativeTime value={answer.readUtc} />
              </span>
            )}
            <Button
              variant="outline"
              size="sm"
              onClick={() => relist.mutate()}
              disabled={relist.isPending || reading}
              title={`List partition ${partition}'s schemas again, for those registered since`}
              data-testid="templates-partition-relist"
            >
              {relist.isPending ? <Loader2 className="animate-spin" /> : <RefreshCw />}
              List again
            </Button>
          </div>
        </FilterBar>
        <p className="flex flex-wrap items-center gap-x-1.5 text-xs text-muted-foreground" data-testid="templates-partition-summary">
          {answer === undefined || answer.state !== "ready"
            ? `The schemas registered in partition ${partition}'s Schema service: the OSDU standard ones it holds, and its custom ones.`
            : (
              <span>
                <span className="font-mono tabular-nums text-foreground">{ordered.length.toLocaleString()}</span>
                {` schema${ordered.length === 1 ? "" : "s"} (`}
                <span className="font-mono tabular-nums text-foreground">{counts.size.toLocaleString()}</span>
                {` record type${counts.size === 1 ? "" : "s"}) registered in partition `}
                <span className="font-mono text-foreground">{partition}</span>
                {": "}
                <span className="font-mono tabular-nums text-foreground">{customCount.toLocaleString()}</span>
                {` custom (an authority other than ${OSDU_AUTHORITY}, in no OSDU release) and `}
                <span className="font-mono tabular-nums text-foreground">{(ordered.length - customCount).toLocaleString()}</span>
                {" OSDU standard."}
              </span>
            )}
        </p>
      </Card>

      {listing.isError && <ProblemView error={listing.error} testId="templates-partition-error" />}
      {answer?.state === "failed" && (
        <ProblemView error={new Error(answer.problem ?? "The Schema service could not be listed.")} testId="templates-partition-error" />
      )}
      {!listing.isError && reading && (
        <TaskProgress label={`Listing the schemas partition ${partition}'s Schema service holds, every status and scope`} testId="templates-partition-listing" />
      )}
      {!listing.isError && listing.data === undefined && !listing.isFetching && <Skeleton className="h-64 w-full rounded-lg" />}

      {shownRows !== undefined && (
        <DimensionGrid testId="templates-partition-grid">
          <DataTable
            columns={columns}
            rows={shownRows}
            rowKey={(row) => row.schema.kind}
            onRowClick={(row) => setViewing(row.schema.kind)}
            emptyMessage={customOnly && customCount === 0
              ? `Partition ${partition} has no custom schema registered: every kind it lists is of the osdu authority.`
              : `No kind registered in partition ${partition} matches the search.`}
            footer={rows !== undefined && rows.length > MAX_ROWS ? (
              <div className="flex h-8 items-center border-t border-border px-3 text-xs text-muted-foreground" data-testid="templates-partition-truncated">
                {`Showing ${MAX_ROWS.toLocaleString()} of ${rows.length.toLocaleString()}. Narrow the search to see the rest.`}
              </div>
            ) : undefined}
            data-testid="templates-partition-results"
          />
        </DimensionGrid>
      )}

      <TemplateSheet
        open={viewing !== null}
        onClose={() => setViewing(null)}
        title={viewing ?? "Schema"}
        source={read !== undefined ? (
          <span className="inline-flex min-w-0 flex-wrap items-center gap-x-1.5" data-testid="templates-partition-source">
            <span className="min-w-0 break-all">{`Partition ${partition}'s Schema service`}</span>
            <span className="text-muted-foreground">
              {`${read.read.length} schema${read.read.length === 1 ? "" : "s"} read`}
              {read.unresolved.length > 0 && `, ${read.unresolved.length} reference${read.unresolved.length === 1 ? "" : "s"} it could not follow`}
            </span>
          </span>
        ) : null}
        progress={progress}
        problem={problem}
        detail={read !== undefined && parsed !== null ? preview.data : undefined}
        previewSchema={parsed ?? undefined}
        actions={read !== undefined && preview.data !== undefined ? (
          <Button
            variant="outline"
            size="xs"
            onClick={() => { if (viewing !== null) { download.mutate(viewing); } }}
            disabled={download.isPending}
            title={`The bundled schema as ${templateFileName(read.kind)}, for a folder named templates beside the documents that fill it`}
            data-testid="templates-partition-sheet-download"
          >
            {download.isPending ? <Loader2 className="animate-spin" /> : <Download />}
            Download
          </Button>
        ) : undefined}
        busy={download.isPending}
        testId="templates-partition-sheet"
      />
    </div>
  );
}
