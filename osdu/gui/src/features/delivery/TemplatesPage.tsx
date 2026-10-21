import { useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Download, Loader2, TriangleAlert } from "lucide-react";
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
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import { KindText } from "./KindText";
import { TemplatesBrowseTab } from "./TemplatesBrowseTab";
import { problemText } from "./problemText";
import { fileName, templateSchemaQuery } from "./templateFormat";
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

/** The kind a file holds, or, for a file that holds none, why, beside a warning glyph. */
function KindCell({ row }: { row: DeliveryTemplate }) {
  if (row.kind !== null) {
    return <KindText kind={row.kind} />;
  }

  return (
    <span className="inline-flex min-w-0 items-center gap-1.5 text-muted-foreground" data-testid={`templates-problem-${row.path}`}>
      <TriangleAlert className="size-3.5 shrink-0 text-warning" aria-hidden />
      <TruncatedText text={row.problem ?? "Holds no template."} maxWidth={360} title="Holds no template" />
    </span>
  );
}

const columns: Column<DeliveryTemplate>[] = [
  { id: "kind", header: "Kind", fill: true, floor: 150, render: (row) => <KindCell row={row} /> },
  {
    id: "version",
    header: "Version",
    // In a narrow table eight characters of the content hash tell versions apart; the rest stays in the row's text.
    render: (row) => row.version === null
      ? <span className="text-muted-foreground">-</span>
      : (
        <span className="font-mono text-[12px]">
          {row.version.slice(0, 8)}
          <span className="@max-3xl/table:sr-only">{row.version.slice(8)}</span>
        </span>
      ),
  },
  { id: "repository", header: "Repository", render: (row) => <TruncatedText text={row.repository} maxWidth={180} title="Repository" /> },
  {
    id: "file",
    header: "File",
    render: (row) => <span className="font-mono text-[12px]"><TruncatedText text={row.path} maxWidth={340} title="File" /></span>,
  },
  {
    id: "seen",
    header: "Synced",
    // When the repository's sync last saw the file gives way first in a narrow table.
    render: (row) => <span className="@max-3xl/table:sr-only"><RelativeTime value={row.seenUtc} /></span>,
  },
];

/**
 * Templates (osdu/docs/reference/concepts/templates.md): the OSDU record schemas the synced repositories hold in their
 * template folders, folders named templates beside the documents that fill them. A repository adds and removes its
 * templates, and its sync records which file holds which template; each is read from its file, in the folder the
 * repository was synced into, and nothing of it is kept in the database. Browse OSDU reads the OSDU data definitions (the
 * Open Group's public repository of OSDU schemas) and downloads a kind as the file a template folder keeps.
 */
export default function TemplatesPage() {
  const queryClient = useQueryClient();
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");
  const [searchParams, setSearchParams] = useSearchParams();
  const [storedTab, setTab] = useLocalStorageState<string>("sqlflow.templates.tab", "repositories");
  const tab: TemplatesTab = TABS.includes(storedTab) ? (storedTab as TemplatesTab) : "repositories";
  const [search, setSearch] = useState("");
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

  const term = search.trim().toLowerCase();
  const rows = templates.data === undefined
    ? undefined
    : templates.data.filter((template) => term === ""
      || (template.kind ?? "").toLowerCase().includes(term)
      || (template.version ?? "").toLowerCase().includes(term)
      || template.repository.toLowerCase().includes(term)
      || template.path.toLowerCase().includes(term));

  return (
    <Page data-testid="page-delivery-templates">
      <PageHeader
        title="Templates"
        subtitle="The OSDU record schemas the synced repositories hold in their template folders, each read from its file. A repository adds and removes its templates, and its sync records them; Browse OSDU downloads a kind as the file a template folder keeps."
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
              placeholder="Kind, version, repository or file"
              label="Filter the repositories' templates"
              testId="templates-search"
            />
          </FilterBar>
          {unheldLink !== null && (
            <p className="flex items-center gap-1.5 text-[13px] text-muted-foreground" data-testid="templates-link-unheld">
              <TriangleAlert className="size-3.5 shrink-0 text-warning" aria-hidden />
              {unheldLink}
            </p>
          )}
          {templates.isError && <ProblemView error={templates.error} testId="templates-error" />}
          {!templates.isError && (
            <DataTable
              columns={columns}
              rows={rows}
              rowKey={(row) => `${row.repoId} ${row.path}`}
              onRowClick={(row) => {
                download.reset();
                setOpened({ repoId: row.repoId, path: row.path, kind: row.kind });
              }}
              emptyMessage={templates.data !== undefined && templates.data.length === 0
                ? "No synced repository holds a template yet. A template is a file in a folder named templates: download one from Browse OSDU, commit it to the repository beside the documents that fill it, and sync the repository."
                : "No template file matches the filter."}
              data-testid="templates-table"
            />
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
