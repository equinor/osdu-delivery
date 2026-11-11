import { useMemo, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Download, Link2, Loader2, TriangleAlert } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { downloadText } from "@/lib/download";
import { deliveryApi, type DeliveryTemplate } from "../../api/delivery";
import { useAuth } from "@/auth/AuthContext";
import { DataTable, type Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { IconAction } from "@/components/IconAction";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import { DimensionGrid, GridFooter } from "./dimensions/DimensionGrid";
import { KindText } from "./KindText";
import { ProblemsLine, RepositoryPicker } from "./RepositoryGrid";
import { repositoryGrouping } from "./repositoryGrouping";
import { ALL_REPOSITORIES, countOf, inRepositoryOrder, latestOf, pickedRepository, repositoriesOf } from "./repositoryRows";
import { TemplatesBrowseTab } from "./TemplatesBrowseTab";
import { problemText } from "./problemText";
import { entityName, fileName, templateSchemaQuery } from "./templateFormat";
import { ProblemView, TemplateSheet } from "./TemplateSheet";

type TemplatesTab = "repositories" | "browse";

const TABS: readonly string[] = ["repositories", "browse"];

/** A repository's template file the sheet is open on. */
interface Opened {
  repoId: string;
  path: string;
  /** The kind the file holds, for the sheet's title before the template is read. */
  kind: string | null;
}

/** The line over the grid when files hold no template: how many, or, while only they are listed, that only they are. */
function emptyFilesText(count: number, only: boolean): string {
  const files = countOf(count, "file");
  const verb = count === 1 ? "holds" : "hold";
  return only ? `Listing only the ${files} that ${verb} no template` : `${files} ${verb} no template; ${count === 1 ? "its" : "each"} row says why`;
}

/** Everything the grid shows of a template file, as the text its search reads: its row, and the repository heading it. */
function templateSearchText(template: DeliveryTemplate): string {
  return [template.kind, template.parent, template.version, template.repository, template.path, template.problem]
    .filter((part) => part !== null && part !== undefined)
    .join(" ")
    .toLowerCase();
}

/**
 * The kind a template is linked to as its parent, beside its own: a neutral chip naming the parent's entity and version,
 * with what the link does on hover.
 */
function ParentChip({ parent, testId }: { parent: string; testId?: string }) {
  const version = parent.slice(parent.lastIndexOf(":") + 1);
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span
          className="inline-flex min-w-[1.375rem] shrink-[100] items-center gap-1 overflow-hidden rounded-md border border-border/60 bg-muted/40 px-1.5 py-px text-[11px] text-muted-foreground"
          data-testid={testId}
        >
          <Link2 className="size-3 shrink-0" aria-hidden />
          {/* The kind the row holds keeps the room: in a narrow cell the parent gives way down to its glyph and its hover. */}
          <span className="min-w-0 truncate font-mono">{`${entityName(parent)} ${version}`}</span>
        </span>
      </TooltipTrigger>
      <TooltipContent className="max-w-sm">
        {`Linked to ${parent}. Each record of this template has a parent: the ${entityName(parent)} record with the same minted id, whose sections its mapping can read with $parent.`}
      </TooltipContent>
    </Tooltip>
  );
}

/** The kind a file holds, with the kind it is linked to; or, for a file that holds none, why, beside a warning glyph. */
function KindCell({ row }: { row: DeliveryTemplate }) {
  if (row.kind !== null) {
    return (
      <span className="flex min-w-0 items-center gap-2">
        <span className="min-w-0 shrink"><KindText kind={row.kind} /></span>
        {row.parent && <ParentChip parent={row.parent} testId={`templates-parent-${row.path}`} />}
      </span>
    );
  }

  return (
    <span className="flex min-w-0 items-center gap-1.5" data-testid={`templates-problem-${row.path}`}>
      <TriangleAlert className="size-3.5 shrink-0 text-warning" aria-label="Holds no template" />
      <TruncatedText text={row.problem ?? "Holds no template."} maxWidth={4000} title="Holds no template" />
    </span>
  );
}

/**
 * Templates (osdu/docs/reference/concepts/templates.md): the OSDU record schemas the synced repositories hold in their
 * template folders, folders named templates beside the documents that fill them, under a heading for each repository when
 * there are several. A repository adds and removes its templates, and its sync records which file holds which template;
 * each is read from its file, in the folder the repository was synced into, and nothing of it is kept in the database.
 * A file that holds no template says why in its row, and a line over the grid counts them. Browse OSDU reads the OSDU data
 * definitions (the Open Group's public repository of OSDU schemas) and downloads a kind as the file a template folder keeps.
 */
export default function TemplatesPage() {
  const queryClient = useQueryClient();
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");
  const [searchParams, setSearchParams] = useSearchParams();
  const [storedTab, setTab] = useLocalStorageState<string>("sqlflow.templates.tab", "repositories");
  const tab: TemplatesTab = TABS.includes(storedTab) ? (storedTab as TemplatesTab) : "repositories";
  const [repoFilter, setRepoFilter] = useLocalStorageState("sqlflow.filters.delivery-templates.repo", ALL_REPOSITORIES);
  const [search, setSearch] = useState("");
  const [problemsOnly, setProblemsOnly] = useState(false);
  const [opened, setOpened] = useState<Opened | null>(null);

  const templates = useQuery({ queryKey: ["delivery", "templates", "list"], queryFn: deliveryApi.templates });

  // A link to one file (/delivery/templates?repoId=...&path=...) opens it, once per link. A link naming a kind and version
  // opens the file holding that version the repository synced last holds, once the list is read.
  const linkedRepo = searchParams.get("repoId");
  const linkedPath = searchParams.get("path");
  const linkedKind = searchParams.get("kind");
  const linkedVersion = searchParams.get("version");
  const byVersion = linkedKind !== null && linkedVersion !== null && templates.data !== undefined
    ? templates.data
      .filter((row) => row.kind === linkedKind && row.version === linkedVersion)
      .sort((a, b) => b.seenUtc.localeCompare(a.seenUtc))[0] ?? null
    : null;
  const link = linkedRepo !== null && linkedPath !== null
    ? `${linkedRepo} ${linkedPath}`
    : byVersion !== null ? `${byVersion.repoId} ${byVersion.path}` : null;
  const [openedLink, setOpenedLink] = useState<string | null>(null);
  if (link !== openedLink) {
    setOpenedLink(link);
    if (linkedRepo !== null && linkedPath !== null) {
      setOpened({ repoId: linkedRepo, path: linkedPath, kind: null });
      setTab("repositories");
    } else if (byVersion !== null) {
      setOpened({ repoId: byVersion.repoId, path: byVersion.path, kind: byVersion.kind });
      setTab("repositories");
    }
  }

  // A link naming a version no repository's folders hold says so where the list is, rather than opening nothing silently.
  const unheldLink = linkedRepo === null && linkedKind !== null && linkedVersion !== null && templates.data !== undefined && byVersion === null
    ? `No synced repository's template folders hold ${linkedKind} version ${linkedVersion}.`
    : null;

  const detail = useQuery({
    queryKey: ["delivery", "templates", "detail", opened?.repoId ?? null, opened?.path ?? null],
    queryFn: () => deliveryApi.templateDetail(opened!.repoId, opened!.path),
    enabled: opened !== null,
  });

  const closeSheet = () => {
    setOpened(null);
    if (["repoId", "path", "kind", "version"].some((name) => searchParams.has(name))) {
      setSearchParams((current) => {
        const next = new URLSearchParams(current);
        for (const name of ["repoId", "path", "kind", "version"]) {
          next.delete(name);
        }

        return next;
      }, { replace: true });
    }
  };

  // The file as the repository holds it: the text the Schema JSON tab shows, read once for both, under the file's own name.
  const download = useMutation({
    mutationFn: async (file: Opened) => {
      const text = await queryClient.fetchQuery(templateSchemaQuery(file.repoId, file.path));
      downloadText(fileName(file.path), text, "application/json");
    },
    onSuccess: (_result, file) => toast.success(`Downloaded ${fileName(file.path)}.`),
    onError: (error) => toast.error(`The template could not be downloaded: ${problemText(error)}`),
  });

  // The repositories that hold a template file, by name, and the files of the one picked in the order the grid lists them.
  const names = useMemo(() => new Map((templates.data ?? []).map((row) => [row.repoId, row.repository])), [templates.data]);
  const repositories = useMemo(() => repositoriesOf(templates.data ?? [], (row) => row.repoId, names), [templates.data, names]);
  const repoId = pickedRepository(repoFilter, repositories);
  const inRepository = useMemo(() => (templates.data === undefined
    ? undefined
    : inRepositoryOrder(templates.data.filter((row) => repoId === ALL_REPOSITORIES || row.repoId === repoId), (row) => row.repoId, names)),
  [templates.data, repoId, names]);
  const empty = inRepository?.filter((row) => row.kind === null).length ?? 0;
  const onlyEmpty = problemsOnly && empty > 0;

  // Every word typed is found in what the grid shows of a file, in any order and ignoring case.
  const words = search.toLowerCase().split(/\s+/).filter((word) => word !== "");
  const rows = inRepository?.filter((row) => (!onlyEmpty || row.kind === null)
    && (words.length === 0 || words.every((word) => templateSearchText(row).includes(word))));

  const grouping = repositoryGrouping(
    rows,
    { repoId: (row) => row.repoId, seenUtc: (row) => row.seenUtc, failing: (row) => row.kind === null },
    names,
    { one: "template file", many: "template files", failing: (count) => `${count} ${count === 1 ? "holds" : "hold"} no template` },
  );
  const lastSeen = latestOf((rows ?? []).map((row) => row.seenUtc));
  const footNote = grouping !== undefined
    ? `in ${countOf(new Set(rows?.map((row) => row.repoId)).size, "repository", "repositories")}`
    : lastSeen !== null ? <>{"synced "}<RelativeTime value={lastSeen} absolute={false} /></> : undefined;

  const open = (row: DeliveryTemplate) => {
    download.reset();
    setOpened({ repoId: row.repoId, path: row.path, kind: row.kind });
  };

  const columns: Column<DeliveryTemplate>[] = [
    { id: "kind", header: "Kind", fill: true, floor: 180, render: (row) => <KindCell row={row} /> },
    {
      id: "version",
      header: "Version",
      // In a narrow grid eight characters of the content hash tell versions apart; the rest stays in the row's text.
      render: (row) => row.version === null
        ? <span className="text-muted-foreground">-</span>
        : (
          <span className="font-mono text-[12px] tabular-nums text-muted-foreground">
            {row.version.slice(0, 8)}
            <span className="@max-3xl/table:sr-only">{row.version.slice(8)}</span>
          </span>
        ),
    },
    {
      id: "file",
      header: "File",
      // As wide as a share of the grid, its folder in view: the file's name repeats the kind the row already shows.
      render: (row) => (
        <span className="block w-[clamp(160px,32cqw,560px)]">
          <TruncatedText text={row.path} mono maxWidth={4000} title="File" className="text-muted-foreground" />
        </span>
      ),
    },
    {
      id: "actions",
      header: "",
      align: "right",
      width: 40,
      render: (row) => row.kind === null ? null : (
        <IconAction
          label={`Download ${fileName(row.path)} as the repository holds it`}
          icon={download.isPending && download.variables?.path === row.path && download.variables.repoId === row.repoId
            ? <Loader2 className="animate-spin" />
            : <Download />}
          className="text-muted-foreground"
          onClick={(event) => {
            event.stopPropagation();
            download.mutate({ repoId: row.repoId, path: row.path, kind: row.kind });
          }}
          disabled={download.isPending}
          data-testid={`templates-row-download-${row.path}`}
        />
      ),
    },
  ];

  const searched = search.trim();
  return (
    <Page data-testid="page-delivery-templates">
      <PageHeader
        title="Templates"
        subtitle="The record schemas the repositories keep in their template folders, which the mappings fill. Browse OSDU downloads one as such a file."
      />
      <Tabs value={tab} onValueChange={setTab}>
        <TabsList data-testid="templates-tabs">
          <TabsTrigger value="repositories" data-testid="templates-tab-repositories">Repositories</TabsTrigger>
          <TabsTrigger value="browse" data-testid="templates-tab-browse">Browse OSDU</TabsTrigger>
        </TabsList>
        {/* The tabs stay mounted so a search and its results survive switching between them. */}
        <TabsContent value="repositories" forceMount className="flex flex-col gap-3 data-[state=inactive]:hidden">
          <FilterBar>
            <SearchInput
              value={search}
              onChange={setSearch}
              placeholder="Search by kind, version, repository or file"
              label="Search the repositories' templates"
              className="sm:w-96"
              testId="templates-search"
            />
            <RepositoryPicker value={repoId} onChange={setRepoFilter} repositories={repositories} testId="templates-repo" />
          </FilterBar>
          {unheldLink !== null && (
            <p className="flex items-center gap-1.5 text-[13px] text-muted-foreground" data-testid="templates-link-unheld">
              <TriangleAlert className="size-3.5 shrink-0 text-warning" aria-hidden />
              {unheldLink}
            </p>
          )}
          {templates.isError && <ProblemView error={templates.error} testId="templates-error" />}
          {!templates.isError && (
            <DimensionGrid testId="templates-grid">
              <DataTable
                columns={columns}
                rows={rows}
                rowKey={(row) => `${row.repoId} ${row.path}`}
                onRowClick={open}
                grouping={grouping}
                toolbar={empty > 0 ? (
                  <ProblemsLine
                    problem={emptyFilesText(empty, onlyEmpty)}
                    only={onlyEmpty}
                    onOnly={setProblemsOnly}
                    icon={<TriangleAlert className="size-3.5 shrink-0 text-warning" aria-hidden />}
                    testId="templates-problems"
                  />
                ) : undefined}
                footer={rows !== undefined ? (
                  <GridFooter
                    shown={rows.length}
                    total={inRepository !== undefined && inRepository.length > rows.length ? inRepository.length : null}
                    noun="template file"
                    hasMore={false}
                    loading={false}
                    onMore={() => undefined}
                    note={footNote}
                    testId="templates-foot"
                  />
                ) : undefined}
                emptyMessage={templates.data !== undefined && templates.data.length === 0
                  ? "No synced repository holds a template yet. A template is a file in a folder named templates: download one from Browse OSDU, commit it to the repository beside the documents that fill it, and sync the repository."
                  : `No template file matches '${searched}'. The search reads the kind, its parent, the version, the repository and the file.`}
                data-testid="templates-table"
              />
            </DimensionGrid>
          )}
        </TabsContent>
        <TabsContent value="browse" forceMount className="data-[state=inactive]:hidden">
          <TemplatesBrowseTab canOperate={canOperate} />
        </TabsContent>
      </Tabs>

      <TemplateSheet
        open={opened !== null}
        onClose={closeSheet}
        title={opened?.kind ?? detail.data?.kind ?? (opened !== null ? fileName(opened.path) : "Template")}
        source={null}
        progress={null}
        problem={detail.isError ? <ProblemView error={detail.error} testId="templates-detail-error" /> : null}
        detail={detail.data}
        actions={detail.data !== undefined && opened !== null ? (
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                variant="outline"
                size="xs"
                onClick={() => download.mutate(opened)}
                disabled={download.isPending}
                data-testid="templates-download"
              >
                {download.isPending ? <Loader2 className="animate-spin" /> : <Download />}
                Download
              </Button>
            </TooltipTrigger>
            <TooltipContent className="max-w-xs">
              {`The file ${opened.path} of the repository, as ${fileName(opened.path)}: to keep in another repository's template folder.`}
            </TooltipContent>
          </Tooltip>
        ) : undefined}
        busy={download.isPending}
        testId="templates-sheet"
      />
    </Page>
  );
}
