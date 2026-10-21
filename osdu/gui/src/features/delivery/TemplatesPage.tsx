import { useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ClipboardPaste, Cloud, Download, FileJson, Loader2, Trash2, type LucideIcon } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { downloadText } from "@/lib/download";
import { deliveryApi, type DeliveryTemplate } from "../../api/delivery";
import { useAuth } from "@/auth/AuthContext";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { DataTable, type Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { GlyphRef } from "@/components/GlyphRef";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import { KindText } from "./KindText";
import { TemplatesBrowseTab } from "./TemplatesBrowseTab";
import { TemplatesImportTab } from "./TemplatesImportTab";
import { problemText } from "./problemText";
import { templateFileName, templateSchemaQuery } from "./templateFormat";
import { ProblemView, TemplateSheet } from "./TemplateSheet";

type TemplatesTab = "saved" | "browse" | "import";

const TABS: readonly string[] = ["saved", "browse", "import"];

/**
 * Where a saved version came from, as the import and browse tabs word it, reduced to a glyph and a word so the column
 * stays narrow; the full origin is on hover. Null for an origin worded some other way, which then shows as text.
 */
function originVisual(origin: string): { icon: LucideIcon; label: string } | null {
  if (origin.startsWith("file ")) {
    return { icon: FileJson, label: "File" };
  }

  if (origin.startsWith("OSDU ")) {
    return { icon: Cloud, label: "OSDU" };
  }

  return origin === "pasted schema" ? { icon: ClipboardPaste, label: "Pasted" } : null;
}

function OriginCell({ origin }: { origin: string }) {
  const visual = originVisual(origin);
  return visual === null
    ? <TruncatedText text={origin} maxWidth={180} title="Origin" />
    : <GlyphRef icon={visual.icon} title="Origin" body={origin} label={visual.label} collapse />;
}

const savedColumns: Column<DeliveryTemplate>[] = [
  { id: "kind", header: "Kind", fill: true, floor: 150, render: (row) => <KindText kind={row.kind} /> },
  {
    id: "version",
    header: "Version",
    // In a narrow table eight characters of the content hash tell versions apart; the rest stays in the row's text.
    render: (row) => (
      <span className="font-mono text-[12px]">
        {row.version.slice(0, 8)}
        <span className="@max-3xl/table:sr-only">{row.version.slice(8)}</span>
      </span>
    ),
  },
  {
    id: "saved",
    header: "Saved",
    render: (row) => (
      <span className="inline-flex items-baseline gap-1.5">
        <RelativeTime value={row.capturedUtc} />
        {/* Who saved it gives way first in a narrow table; the template's sheet names them as well. */}
        <span className="inline-flex items-baseline gap-1.5 @max-3xl/table:sr-only">
          <span className="text-muted-foreground">by</span>
          <TruncatedText text={row.capturedBy} maxWidth={160} />
        </span>
      </span>
    ),
  },
  { id: "origin", header: "Origin", render: (row) => <OriginCell origin={row.origin} /> },
];

/**
 * Templates: the OSDU record schemas saved as templates, the library, for what reads templates where no repository is at
 * hand (the explorer, the mapping builder, the assistant). It is not linked to the repositories, whose documents read
 * their templates from their template folders: it holds what was saved here, from the OSDU data definitions (the Open
 * Group's public repository of OSDU schemas) or, for a schema of one's own, from a bundled schema file, each looked at
 * variable by variable before it is saved. A saved version is its content and never changes, and it downloads as the
 * bundled file a template folder keeps.
 */
export default function TemplatesPage() {
  const queryClient = useQueryClient();
  const { hasScope } = useAuth();
  const canAuthor = hasScope("author");
  const canOperate = hasScope("operate");
  const [searchParams, setSearchParams] = useSearchParams();
  const [storedTab, setTab] = useLocalStorageState<string>("sqlflow.templates.tab", "saved");
  const tab: TemplatesTab = TABS.includes(storedTab) ? (storedTab as TemplatesTab) : "saved";
  const [search, setSearch] = useState("");
  const [selected, setSelected] = useState<{ kind: string; version: string } | null>(null);
  const [deleteOpen, setDeleteOpen] = useState(false);

  // A link to one saved version (/delivery/templates?kind=...&version=...) opens it, once per link.
  const linkedKind = searchParams.get("kind");
  const linkedVersion = searchParams.get("version");
  const link = linkedKind !== null && linkedVersion !== null ? `${linkedKind} ${linkedVersion}` : null;
  const [openedLink, setOpenedLink] = useState<string | null>(null);
  if (link !== openedLink) {
    setOpenedLink(link);
    if (linkedKind !== null && linkedVersion !== null) {
      setSelected({ kind: linkedKind, version: linkedVersion });
      setTab("saved");
    }
  }

  const templates = useQuery({ queryKey: ["delivery", "templates", "list"], queryFn: deliveryApi.templates });
  const detail = useQuery({
    queryKey: ["delivery", "templates", "detail", selected?.kind ?? null, selected?.version ?? null],
    queryFn: () => deliveryApi.templateDetail(selected!.kind, selected!.version),
    enabled: selected !== null,
  });

  const closeSheet = () => {
    setSelected(null);
    if (searchParams.has("kind") || searchParams.has("version")) {
      setSearchParams((current) => {
        const next = new URLSearchParams(current);
        next.delete("kind");
        next.delete("version");
        return next;
      }, { replace: true });
    }
  };

  const remove = useMutation({
    mutationFn: (template: { kind: string; version: string }) => deliveryApi.deleteTemplate(template.kind, template.version),
    onSuccess: (_result, template) => {
      setDeleteOpen(false);
      closeSheet();
      toast.success(`Deleted template ${template.kind} version ${template.version}.`);
      void queryClient.invalidateQueries({ queryKey: ["delivery", "templates"] });
    },
    onError: (error) => {
      // A 409 names the template folder files or the synced mappings that took up the version since the sheet was read;
      // the sheet keeps the problem in view, and reads the version again so Delete shows what now holds it.
      setDeleteOpen(false);
      toast.error(problemText(error));
      void queryClient.invalidateQueries({ queryKey: ["delivery", "templates"] });
    },
  });

  // The saved version as a file: the text the Schema JSON tab shows, read once for both, named as the samples name their
  // schemas. A repository can keep it beside the mappings that pin it, and importing it saves this same version.
  const download = useMutation({
    mutationFn: async (template: { kind: string; version: string }) => {
      const text = await queryClient.fetchQuery(templateSchemaQuery(template.kind, template.version));
      downloadText(templateFileName(template.kind), text, "application/json");
    },
    onSuccess: (_result, template) => toast.success(`Downloaded ${templateFileName(template.kind)}.`),
    onError: (error) => toast.error(`The schema could not be downloaded: ${problemText(error)}`),
  });
  const downloadButton = selected === null ? null : (
    <Tooltip>
      <TooltipTrigger asChild>
        <Button
          variant="outline"
          size="xs"
          onClick={() => download.mutate(selected)}
          disabled={download.isPending}
          data-testid="templates-download"
        >
          {download.isPending ? <Loader2 className="animate-spin" /> : <Download />}
          Download
        </Button>
      </TooltipTrigger>
      <TooltipContent className="max-w-xs">
        {`The bundled schema as ${templateFileName(selected.kind)}, for a repository's template folder, where its documents read it. Importing it saves this same version.`}
      </TooltipContent>
    </Tooltip>
  );

  // Nothing a repository holds depends on a saved version, so any can be deleted.
  const deleteButton = (
    <Button
      variant="destructive-outline"
      size="xs"
      onClick={() => setDeleteOpen(true)}
      disabled={remove.isPending}
      data-testid="templates-delete"
    >
      <Trash2 />
      Delete
    </Button>
  );

  const term = search.trim().toLowerCase();
  const rows = templates.data === undefined
    ? undefined
    : templates.data
      .filter((template) => term === ""
        || template.kind.toLowerCase().includes(term)
        || template.version.toLowerCase().includes(term)
        || template.origin.toLowerCase().includes(term)
        || template.capturedBy.toLowerCase().includes(term))
      .sort((a, b) => a.kind.localeCompare(b.kind) || b.capturedUtc.localeCompare(a.capturedUtc));

  return (
    <Page data-testid="page-delivery-templates">
      <PageHeader
        title="Templates"
        subtitle="OSDU record schemas saved as templates, for the explorer, the mapping builder and the assistant; a repository's documents read theirs from its template folders. Every property becomes a variable a mapping can fill, and a saved version never changes."
      />
      <Tabs value={tab} onValueChange={setTab}>
        <TabsList data-testid="templates-tabs">
          <TabsTrigger value="saved" data-testid="templates-tab-saved">Saved</TabsTrigger>
          <TabsTrigger value="browse" data-testid="templates-tab-browse">Browse OSDU</TabsTrigger>
          <TabsTrigger value="import" data-testid="templates-tab-import">Import file</TabsTrigger>
        </TabsList>
        {/* The tabs stay mounted so a search, its results and a pasted schema survive switching between them. */}
        <TabsContent value="saved" forceMount className="flex flex-col gap-3 data-[state=inactive]:hidden">
          <FilterBar>
            <SearchInput
              value={search}
              onChange={setSearch}
              placeholder="Kind, version, origin or who saved it"
              label="Filter the saved templates"
              testId="templates-saved-search"
            />
          </FilterBar>
          {templates.isError && <ProblemView error={templates.error} testId="templates-saved-error" />}
          {!templates.isError && (
            <DataTable
              columns={savedColumns}
              rows={rows}
              rowKey={(row) => `${row.kind} ${row.version}`}
              onRowClick={(row) => {
                remove.reset();
                setSelected({ kind: row.kind, version: row.version });
              }}
              emptyMessage={templates.data !== undefined && templates.data.length === 0
                ? "No template is saved yet. Browse OSDU or import a schema file to save one."
                : "No saved template matches the filter."}
              data-testid="templates-saved-table"
            />
          )}
        </TabsContent>
        <TabsContent value="browse" forceMount className="data-[state=inactive]:hidden">
          <TemplatesBrowseTab canAuthor={canAuthor} canOperate={canOperate} />
        </TabsContent>
        <TabsContent value="import" forceMount className="data-[state=inactive]:hidden">
          <TemplatesImportTab canAuthor={canAuthor} />
        </TabsContent>
      </Tabs>

      <TemplateSheet
        open={selected !== null}
        onClose={closeSheet}
        title={selected?.kind ?? "Template"}
        source={null}
        progress={null}
        problem={detail.isError
          ? <ProblemView error={detail.error} testId="templates-detail-error" />
          : remove.isError ? <ProblemView error={remove.error} testId="templates-delete-error" /> : null}
        detail={detail.data}
        actions={detail.data !== undefined ? (
          <>
            {/* Anyone who can read a version can download it; deleting it is an author's action. */}
            {downloadButton}
            {canAuthor && deleteButton}
          </>
        ) : undefined}
        busy={remove.isPending}
        testId="templates-saved-sheet"
      />
      <ConfirmDialog
        open={deleteOpen && selected !== null}
        title="Delete this template version"
        message={selected === null
          ? ""
          : `Delete ${selected.kind} version ${selected.version}? Repositories read their templates from their template folders, so none of their documents is affected. The builder and the explorer no longer offer it until the same schema is saved again.`}
        confirmLabel="Delete"
        danger
        busy={remove.isPending}
        onConfirm={() => {
          if (selected !== null) {
            remove.mutate(selected);
          }
        }}
        onClose={() => setDeleteOpen(false)}
      />
    </Page>
  );
}
