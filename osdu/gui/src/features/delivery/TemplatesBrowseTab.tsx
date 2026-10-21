import { useDeferredValue, useMemo, useState, type ReactNode } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Archive, Download, ExternalLink, Eye, FlaskConical, GitCompareArrows, Loader2, RefreshCw } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { downloadText } from "@/lib/download";
import { deliveryApi, type DeliveryOsduSchema } from "../../api/delivery";
import { DataTable, type Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { IconAction } from "@/components/IconAction";
import { LinkRef } from "@/components/LinkRef";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { StatePill } from "@/components/StatusBadge";
import { KindText } from "./KindText";
import { TemplateCompareSheet, type CompareStart } from "./TemplateCompareSheet";
import { kindStem, osduTemplateFileQuery, templateFileName } from "./templateFormat";
import { problemText } from "./problemText";
import { ProblemView, TaskProgress, TemplateSheet } from "./TemplateSheet";

/** The most rows the table renders at once; the search narrows the rest. */
const MAX_ROWS = 200;

/** A schema in the table, and how many versions of its entity type the release publishes. */
interface BrowseRow {
  schema: DeliveryOsduSchema;
  versions: number;
}

/** A schema being looked at: the release it is read from, and its kind. */
interface Viewing {
  release: string;
  kind: string;
}

/** A published schema, the usual case, reads plainly; one in development or obsolete stands out. */
function StatusCell({ schema }: { schema: DeliveryOsduSchema }) {
  if (schema.status === null || schema.status === "PUBLISHED") {
    return <span className="text-muted-foreground">{schema.status === null ? "-" : "Published"}</span>;
  }

  const obsolete = schema.status === "OBSOLETE";
  return (
    <StatePill
      tone={obsolete ? "muted" : "warning"}
      label={obsolete ? "Obsolete" : schema.status === "DEVELOPMENT" ? "In development" : schema.status}
      icon={obsolete ? Archive : FlaskConical}
      testId={`templates-browse-status-${schema.kind}`}
    />
  );
}

/** A link out to the data definitions repository, opening in a new tab. */
function RepositoryLink({ href, children, testId }: { href: string; children: ReactNode; testId: string }) {
  return (
    <Button asChild variant="link" size="sm" className="h-auto px-0">
      <a href={href} target="_blank" rel="noreferrer" data-testid={testId}>
        {children}
        <ExternalLink className="size-3.5" />
      </a>
    </Button>
  );
}

/**
 * Browse OSDU: the OSDU data definitions, the Open Group's public repository of OSDU schemas and the canonical source of
 * every OSDU kind. Pick a release (the newest by default), find a kind with one search, look at it laid out as a template,
 * and download it as the file a repository's template folder keeps: committed beside the documents that fill it, the
 * repository's next sync records it. The control plane reads the repository; the schemas are public, so no flow,
 * credential or node is involved, and nothing is kept.
 */
export function TemplatesBrowseTab({ canOperate }: { canOperate: boolean }) {
  const queryClient = useQueryClient();
  const [pickedRelease, setPickedRelease] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [allVersions, setAllVersions] = useState(false);
  const [viewing, setViewing] = useState<Viewing | null>(null);
  const [comparing, setComparing] = useState<CompareStart | null>(null);
  const term = useDeferredValue(search);

  const releases = useQuery({
    queryKey: ["delivery", "osdu-definitions", "releases"],
    queryFn: deliveryApi.osduReleases,
    staleTime: 10 * 60_000,
  });
  const release = pickedRelease ?? releases.data?.releases[0]?.name ?? null;
  const chosenRelease = releases.data?.releases.find((candidate) => candidate.name === release);

  // Syncing reads the release list again and brings the release in view into the local copy.
  const sync = useMutation({
    mutationFn: () => deliveryApi.osduSync(release),
    onSuccess: (result) => {
      toast.success(result.downloaded.length === 0
        ? `Synced with the OSDU data definitions: ${result.releases.length} releases, and the release in view is already on disk.`
        : `Synced with the OSDU data definitions: ${result.releases.length} releases; downloaded ${result.downloaded.join(", ")}.`);
      void queryClient.invalidateQueries({ queryKey: ["delivery", "osdu-definitions"] });
    },
    onError: (error) => toast.error(problemText(error)),
  });

  // A release is a tag at a fixed commit, so what it publishes never changes while the page is open.
  const index = useQuery({
    queryKey: ["delivery", "osdu-definitions", "schemas", release],
    queryFn: () => deliveryApi.osduSchemas(release!),
    enabled: release !== null,
    staleTime: Infinity,
  });

  const file = useQuery({
    queryKey: ["delivery", "osdu-definitions", "schema", viewing?.release ?? null, viewing?.kind ?? null],
    queryFn: () => deliveryApi.osduSchema(viewing!.release, viewing!.kind),
    enabled: viewing !== null,
    staleTime: Infinity,
  });

  const preview = useQuery({
    queryKey: ["delivery", "templates", "preview", "osdu", file.data?.release.commit ?? null, file.data?.kind ?? null],
    queryFn: () => deliveryApi.previewTemplate(file.data!.kind, file.data!.schema),
    enabled: viewing !== null && file.data !== undefined,
  });

  // A kind of a release as the file a template folder keeps, named after its kind: bundled, and naming the kind it holds.
  const download = useMutation({
    mutationFn: async (target: Viewing) => {
      const text = await queryClient.fetchQuery(osduTemplateFileQuery(target.release, target.kind));
      downloadText(templateFileName(target.kind), text, "application/json");
    },
    onSuccess: (_result, target) => toast.success(
      `Downloaded ${templateFileName(target.kind)}. Put it in a folder named templates beside the documents that fill it, commit it, and sync the repository.`),
    onError: (error) => toast.error(`The template could not be downloaded: ${problemText(error)}`),
  });

  const counts = useMemo(() => {
    const byType = new Map<string, number>();
    for (const schema of index.data?.schemas ?? []) {
      byType.set(schema.entityType, (byType.get(schema.entityType) ?? 0) + 1);
    }

    return byType;
  }, [index.data]);

  // The index lists each entity type newest version first, so the first of a type is its newest.
  const rows = useMemo((): BrowseRow[] | undefined => {
    if (index.data === undefined) {
      return undefined;
    }

    const terms = term.trim().toLowerCase().split(/\s+/).filter((part) => part !== "");
    const shown = new Set<string>();
    const result: BrowseRow[] = [];
    for (const schema of index.data.schemas) {
      if (!allVersions) {
        if (shown.has(schema.entityType)) {
          continue;
        }

        shown.add(schema.entityType);
      }

      const kind = schema.kind.toLowerCase();
      if (terms.every((part) => kind.includes(part))) {
        result.push({ schema, versions: counts.get(schema.entityType) ?? 1 });
      }
    }

    return result;
  }, [index.data, term, allVersions, counts]);

  const view = (kind: string) => {
    if (index.data === undefined) {
      return;
    }

    setViewing({ release: index.data.release.name, kind });
  };

  // A comparison opens on the version before this one in the release, or on the same version in the release before it.
  const compare = (kind: string) => {
    if (index.data === undefined) {
      return;
    }

    const current = index.data.release.name;
    const versions = index.data.schemas.filter((schema) => kindStem(schema.kind) === kindStem(kind));
    const older = versions[versions.findIndex((schema) => schema.kind === kind) + 1];
    if (older !== undefined && versions.some((schema) => schema.kind === kind)) {
      setComparing({ from: { release: current, kind: older.kind }, to: { release: current, kind } });
      return;
    }

    const names = (releases.data?.releases ?? []).map((candidate) => candidate.name);
    const previous = names[names.indexOf(current) + 1] ?? current;
    setComparing({ from: { release: previous, kind }, to: { release: current, kind } });
  };

  const columns: Column<BrowseRow>[] = [
    { id: "kind", header: "Kind", fill: true, render: (row) => <KindText kind={row.schema.kind} /> },
    { id: "status", header: "Status", render: (row) => <StatusCell schema={row.schema} /> },
    ...(allVersions ? [] : [{
      id: "versions",
      header: "Versions",
      align: "right" as const,
      render: (row: BrowseRow) => (
        <span className="font-mono tabular-nums text-muted-foreground">{row.versions} version{row.versions === 1 ? "" : "s"}</span>
      ),
    }]),
    {
      id: "actions",
      header: "",
      align: "right",
      render: (row) => (
        <span className="inline-flex items-center gap-1">
          <LinkRef
            url={row.schema.webUrl}
            title="In the OSDU data definitions"
            testId={`templates-browse-link-${row.schema.kind}`}
            copyTestId={`templates-browse-copy-${row.schema.kind}`}
          />
          <IconAction
            label="Compare versions"
            icon={<GitCompareArrows />}
            onClick={(event) => { event.stopPropagation(); compare(row.schema.kind); }}
            data-testid={`templates-browse-compare-${row.schema.kind}`}
          />
          <IconAction
            label={`Download ${templateFileName(row.schema.kind)} for a repository's template folder`}
            icon={download.isPending && download.variables?.kind === row.schema.kind ? <Loader2 className="animate-spin" /> : <Download />}
            onClick={(event) => {
              event.stopPropagation();
              if (index.data !== undefined) {
                download.mutate({ release: index.data.release.name, kind: row.schema.kind });
              }
            }}
            disabled={download.isPending}
            data-testid={`templates-browse-download-${row.schema.kind}`}
          />
          <Button
            variant="outline"
            size="xs"
            onClick={(event) => { event.stopPropagation(); view(row.schema.kind); }}
            data-testid={`templates-browse-view-${row.schema.kind}`}
          >
            <Eye />
            View
          </Button>
        </span>
      ),
    },
  ];

  // What the sheet shows while the schema is on its way, and what went wrong when it does not arrive.
  let progress: ReactNode = null;
  let problem: ReactNode = null;
  if (viewing !== null) {
    if (file.isError) {
      problem = <ProblemView error={file.error} testId="templates-browse-fetch-error" />;
    } else if (file.data === undefined) {
      progress = (
        <TaskProgress
          label={`Reading ${viewing.kind} and every schema it refers to from release ${viewing.release}`}
          testId="templates-browse-fetch-progress"
        />
      );
    } else if (preview.isError) {
      problem = <ProblemView error={preview.error} testId="templates-browse-preview-error" />;
    } else if (preview.data === undefined) {
      progress = <TaskProgress label="Laying the schema out as a template" testId="templates-browse-preview-progress" />;
    }
  }

  const shownRows = rows?.slice(0, MAX_ROWS);
  const typeCount = counts.size;
  const schemaCount = index.data?.schemas.length ?? 0;
  const listed = index.data?.release;

  return (
    <div className="flex flex-col gap-3" data-testid="templates-browse">
      <Card className="gap-2 rounded-lg p-3" data-testid="templates-browse-form">
        <FilterBar>
          <div className="flex items-center gap-2">
            <Label htmlFor="templates-browse-release" className="text-[13px] font-normal text-muted-foreground">Release</Label>
            <Select value={release ?? ""} onValueChange={setPickedRelease} disabled={releases.data === undefined}>
              <SelectTrigger id="templates-browse-release" size="sm" className="h-8 w-40" data-testid="templates-browse-release">
                <SelectValue placeholder={releases.isError ? "Unavailable" : "Loading releases"} />
              </SelectTrigger>
              <SelectContent>
                {(releases.data?.releases ?? []).map((candidate, i) => (
                  <SelectItem key={candidate.name} value={candidate.name}>
                    <span className="font-mono">{candidate.name}</span>
                    {i === 0 && <span className="text-[11px] text-muted-foreground">newest</span>}
                    {candidate.local && <span className="text-[11px] text-success" data-testid={`templates-browse-local-${candidate.name}`}>on disk</span>}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <SearchInput
            value={search}
            onChange={setSearch}
            placeholder="Wellbore, WellLog, reference-data..."
            label="Search the record kinds of the release"
            className="sm:w-80"
            testId="templates-browse-search"
          />
          <Label className="flex items-center gap-2 text-[13px] font-normal">
            <Switch checked={allVersions} onCheckedChange={setAllVersions} data-testid="templates-browse-all-versions" />
            Every version
          </Label>
          <div className="flex items-center gap-2 sm:ml-auto">
            {releases.data?.syncedUtc != null && (
              <span className="text-xs text-muted-foreground" data-testid="templates-browse-synced">
                Synced <RelativeTime value={releases.data.syncedUtc} />
              </span>
            )}
            <Button
              variant="outline"
              size="sm"
              onClick={() => sync.mutate()}
              disabled={!canOperate || sync.isPending || releases.data === undefined}
              title={canOperate ? "Read the release list again, and download the release in view when it is not on disk" : "Syncing takes a signed-in session."}
              data-testid="templates-browse-sync"
            >
              {sync.isPending ? <Loader2 className="animate-spin" /> : <RefreshCw />}
              Sync with the repository
            </Button>
          </div>
        </FilterBar>
        <p className="flex flex-wrap items-center gap-x-1.5 text-xs text-muted-foreground" data-testid="templates-browse-summary">
          {listed === undefined
            ? "The OSDU data definitions: the Open Group's public repository of OSDU schemas."
            : (
              <>
                <span>
                  <span className="font-mono tabular-nums text-foreground">{typeCount.toLocaleString()}</span>
                  {` record type${typeCount === 1 ? "" : "s"} and `}
                  <span className="font-mono tabular-nums text-foreground">{schemaCount.toLocaleString()}</span>
                  {` schema${schemaCount === 1 ? "" : "s"} in the OSDU data definitions `}
                  <span className="font-mono text-foreground">{listed.name}</span>
                  {listed.publishedUtc !== null && <>, released <RelativeTime value={listed.publishedUtc} /></>}
                  .
                </span>
                <RepositoryLink href={listed.webUrl} testId="templates-browse-repository-link">Open in the repository</RepositoryLink>
              </>
            )}
        </p>
      </Card>

      {releases.isError && <ProblemView error={releases.error} testId="templates-browse-error" />}
      {index.isError && <ProblemView error={index.error} testId="templates-browse-error" />}
      {!releases.isError && !index.isError && rows === undefined && (chosenRelease !== undefined && !chosenRelease.local
        ? (
          <TaskProgress
            label={`Downloading release ${chosenRelease.name} into the local copy; it is read from disk from then on`}
            testId="templates-browse-downloading"
          />
        )
        : <Skeleton className="h-64 w-full rounded-lg" />)}

      {shownRows !== undefined && (
        <DataTable
          columns={columns}
          rows={shownRows}
          rowKey={(row) => row.schema.kind}
          onRowClick={(row) => view(row.schema.kind)}
          emptyMessage={`No record kind in ${listed?.name ?? "the release"} matches the search.`}
          footer={rows !== undefined && rows.length > MAX_ROWS ? (
            <div className="border-t border-border px-3 py-1.5 text-xs text-muted-foreground" data-testid="templates-browse-truncated">
              {`Showing ${MAX_ROWS.toLocaleString()} of ${rows.length.toLocaleString()}. Narrow the search to see the rest.`}
            </div>
          ) : undefined}
          data-testid="templates-browse-results"
        />
      )}

      {comparing !== null && (
        <TemplateCompareSheet
          key={`${comparing.from.release} ${comparing.from.kind} ${comparing.to.release} ${comparing.to.kind}`}
          start={comparing}
          onClose={() => setComparing(null)}
        />
      )}

      <TemplateSheet
        open={viewing !== null}
        onClose={() => setViewing(null)}
        title={viewing?.kind ?? "Schema"}
        source={file.data !== undefined ? (
          <span className="inline-flex min-w-0 flex-wrap items-center gap-x-1.5">
            <a
              href={file.data.webUrl}
              target="_blank"
              rel="noreferrer"
              className="inline-flex min-w-0 items-center gap-1 text-primary underline-offset-4 hover:underline"
              data-testid="templates-browse-source-link"
            >
              <span className="min-w-0 break-all">{`OSDU data definitions ${file.data.release.name}, Generated/${file.data.path}`}</span>
              <ExternalLink className="size-3.5 shrink-0" aria-hidden />
            </a>
            <span className="font-mono text-muted-foreground">{file.data.release.commit.slice(0, 12)}</span>
          </span>
        ) : null}
        progress={progress}
        problem={problem}
        detail={file.data !== undefined ? preview.data : undefined}
        previewSchema={file.data?.schema}
        actions={file.data !== undefined && preview.data !== undefined ? (
          <span className="inline-flex items-center gap-2">
            <Button
              variant="outline"
              size="xs"
              onClick={() => {
                const kind = viewing?.kind;
                setViewing(null);
                if (kind !== undefined) {
                  compare(kind);
                }
              }}
              data-testid="templates-browse-sheet-compare"
            >
              <GitCompareArrows />
              Compare versions
            </Button>
            <Button
              variant="outline"
              size="xs"
              onClick={() => { if (viewing !== null) { download.mutate(viewing); } }}
              disabled={download.isPending}
              title={`The bundled schema as ${templateFileName(file.data.kind)}, for a folder named templates beside the documents that fill it`}
              data-testid="templates-browse-download"
            >
              {download.isPending ? <Loader2 className="animate-spin" /> : <Download />}
              Download
            </Button>
          </span>
        ) : undefined}
        busy={download.isPending}
        testId="templates-browse-sheet"
      />
    </div>
  );
}
