import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { Link as RouterLink, useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { FileCode, OctagonAlert, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { deliveryApi, type DeliveryMapping } from "../../api/delivery";
import { repoApi } from "@/api/endpoints";
import { DataTable, type Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { GlyphRef } from "@/components/GlyphRef";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import { DimensionGrid } from "./dimensions/DimensionGrid";
import { KindText } from "./KindText";
import { MappingLineage } from "./MappingLineage";
import { mappingRoute, matchingMappings, summaryText } from "./mappingSearch";
import { ProblemView } from "./TemplateSheet";

const ALL = "all";

/** A row's source: the system, then the dataset key that identifies its records. */
function sourceText(mapping: DeliveryMapping): string {
  return [summaryText(mapping.summary, "system"), summaryText(mapping.summary, "key")].filter((part) => part !== "").join(": ");
}

const mappingColumns: Column<DeliveryMapping>[] = [
  {
    id: "reference",
    header: "Mapping",
    // A mapping that is valid says nothing: the rows that need a look are the ones that stand out.
    render: (row) => (
      <span className="inline-flex items-center gap-1.5 whitespace-nowrap font-mono text-[12px] font-medium">
        {row.status !== "valid" && <OctagonAlert className="size-3.5 shrink-0 text-destructive" aria-label={row.status} />}
        <span title={row.message ?? undefined}>{row.reference}</span>
      </span>
    ),
  },
  { id: "kind", header: "Kind", fill: true, floor: 130, render: (row) => <KindText kind={row.kind} /> },
  { id: "source", header: "Source", render: (row) => <TruncatedText text={sourceText(row)} mono maxWidth={220} title="Source system and dataset key" className="text-[12px]" /> },
  {
    id: "template",
    header: "Template",
    render: (row) => <span className="font-mono text-[11px] text-muted-foreground">{row.templateVersion === "" ? "-" : row.templateVersion}</span>,
  },
  {
    id: "entries",
    header: "Entries",
    align: "right",
    render: (row) => <span className="font-mono tabular-nums">{summaryText(row.summary, "entries") || "-"}</span>,
  },
  { id: "seen", header: "Last seen", render: (row) => <RelativeTime value={row.lastSeenUtc} /> },
  {
    id: "open",
    header: "",
    align: "right",
    width: 72,
    render: (row) => (
      <span className="inline-flex items-center gap-1">
        <GlyphRef icon={FileCode} title="Path" body={row.relativePath} mono />
        <MappingLineage mapping={row} />
      </span>
    ),
  },
];

/**
 * The mapping documents the synced repositories hold, what every delivery flow renders with: find one by anything its row
 * shows, and it opens on a page of its own. The field has the cursor when the page opens, and Enter opens the first row listed.
 * The caches mappings read are on the Cache page.
 */
export default function DeliveryDocumentsPage() {
  const navigate = useNavigate();
  const [repoFilter, setRepoFilter] = useLocalStorageState("sqlflow.filters.delivery-documents.repo", ALL);
  const [search, setSearch] = useState("");

  const repos = useQuery({ queryKey: ["repos", "all-for-delivery-documents"], queryFn: () => repoApi.list({ page: 1, pageSize: 200 }) });
  const repoId = repoFilter === ALL ? undefined : repoFilter;
  const mappings = useQuery({ queryKey: ["delivery", "mappings", repoId], queryFn: () => deliveryApi.mappings(repoId) });
  const rows = useMemo(() => (mappings.data === undefined ? undefined : matchingMappings(mappings.data, search)), [mappings.data, search]);

  const searchBox = useRef<HTMLDivElement>(null);
  useEffect(() => searchBox.current?.querySelector("input")?.focus(), []);
  const openFirst = (event: KeyboardEvent<HTMLDivElement>) => {
    const first = rows?.[0];
    if (event.key === "Enter" && first !== undefined) {
      navigate(mappingRoute(first.id));
    }
  };

  return (
    <Page data-testid="page-delivery-documents">
      <PageHeader
        title="Mappings"
        subtitle="The mapping documents the repositories hold, as the last sync found them."
        actions={(
          <Button asChild size="sm" data-testid="delivery-documents-new-mapping">
            <RouterLink to="/delivery/mappings/build">
              <Plus />
              New mapping
            </RouterLink>
          </Button>
        )}
      />
      <FilterBar>
        <div ref={searchBox} className="contents" onKeyDown={openFirst}>
          <SearchInput
            value={search}
            onChange={setSearch}
            placeholder="Search by name, kind, source or path"
            label="Search the mappings"
            className="sm:w-96"
            testId="delivery-documents-search"
          />
        </div>
        <Select value={repoFilter} onValueChange={setRepoFilter}>
          <SelectTrigger size="sm" className="h-8 w-56" data-testid="delivery-documents-repo"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>All repos</SelectItem>
            {(repos.data?.items ?? []).map((repo) => <SelectItem key={repo.id} value={repo.id}>{repo.name}</SelectItem>)}
          </SelectContent>
        </Select>
      </FilterBar>
      {mappings.isError && <ProblemView error={mappings.error} testId="delivery-mappings-error" />}
      <DimensionGrid testId="delivery-mappings-grid">
        <DataTable
          columns={mappingColumns}
          rows={rows}
          rowKey={(row) => row.id}
          onRowClick={(row) => navigate(mappingRoute(row.id))}
          emptyMessage={search.trim() !== ""
            ? `No mapping matches '${search.trim()}'. The search reads the name, kind, source, template and path.`
            : "No mapping documents synced yet. A mapping is a YAML file with documentType: mapping under a repository's mappings directory, and the Mapping builder writes one."}
          data-testid="delivery-mappings-table"
        />
      </DimensionGrid>
    </Page>
  );
}
