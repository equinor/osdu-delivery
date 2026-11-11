import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { Link as RouterLink, useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { FileCode, OctagonAlert, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
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
import { DimensionGrid, GridFooter } from "./dimensions/DimensionGrid";
import { KindText } from "./KindText";
import { MappingLineage } from "./MappingLineage";
import { mappingRoute, matchingMappings, summaryText } from "./mappingSearch";
import { ProblemsLine, RepositoryPicker } from "./RepositoryGrid";
import { repositoryGrouping } from "./repositoryGrouping";
import { ALL_REPOSITORIES, countOf, inRepositoryOrder, latestOf, pickedRepository, repositoriesOf } from "./repositoryRows";
import { ProblemView } from "./TemplateSheet";

/** The line over the grid when mappings do not load: how many, or, while only they are listed, that only they are. */
function failingText(count: number, only: boolean): string {
  const mappings = countOf(count, "mapping");
  const verb = count === 1 ? "does" : "do";
  return only ? `Listing only the ${mappings} that ${verb} not load` : `${mappings} ${verb} not load; ${count === 1 ? "its" : "each"} row says why`;
}

/** A mapping's name, its version after it in a quieter voice; one that does not load is marked before it. */
function MappingName({ mapping }: { mapping: DeliveryMapping }) {
  const at = mapping.reference.lastIndexOf("@");
  return (
    <span className="inline-flex items-center gap-1.5 whitespace-nowrap">
      {mapping.status !== "valid" && <OctagonAlert className="size-3.5 shrink-0 text-destructive" aria-label="Does not load" />}
      <span>
        <span className="font-medium">{at > 0 ? mapping.reference.slice(0, at) : mapping.reference}</span>
        {at > 0 && <span className="text-muted-foreground">{mapping.reference.slice(at)}</span>}
      </span>
    </span>
  );
}

/** What the mapping delivers, in its own words; for one that does not load, why, where the description would be. */
function MappingAbout({ mapping }: { mapping: DeliveryMapping }) {
  if (mapping.status !== "valid") {
    return (
      <TruncatedText
        text={mapping.message ?? "The mapping does not load; its page says why."}
        maxWidth={4000}
        title="Why it does not load"
        className="text-destructive"
      />
    );
  }

  return <TruncatedText text={summaryText(mapping.summary, "description")} maxWidth={4000} className="text-muted-foreground" />;
}

/**
 * Where the rows come from: the source system, then the dataset key that tells its records apart. The column is as wide
 * as a share of the grid, so a narrow grid keeps room for the description; the key gives way first.
 */
function MappingSource({ mapping }: { mapping: DeliveryMapping }) {
  const system = summaryText(mapping.summary, "system");
  const key = summaryText(mapping.summary, "key");
  return (
    <span className="flex w-[clamp(96px,12cqw,240px)] min-w-0 items-baseline gap-1.5 overflow-hidden">
      {system === "" && key === "" && <span className="text-muted-foreground">-</span>}
      {system !== "" && <span className="max-w-full shrink-0 truncate" title={system}>{system}</span>}
      {key !== "" && <span className="min-w-0 truncate font-mono text-[12px] text-muted-foreground" title={`Dataset key: ${key}`}>{key}</span>}
    </span>
  );
}

/**
 * The template the mapping fills: its kind, and the version of it, which opens the template. The version's first eight
 * characters tell versions apart; the whole of it stays in the row's text. A mapping whose template no folder on its path
 * holds names the version unlinked. The column is as wide as a share of the grid; the kind's authority and group give
 * way first.
 */
function MappingTemplate({ mapping }: { mapping: DeliveryMapping }) {
  const version = mapping.templateVersion;
  const text = (
    <span className="font-mono text-[12px] tabular-nums">
      {version.slice(0, 8)}
      <span className="sr-only">{version.slice(8)}</span>
    </span>
  );

  return (
    <span className="flex w-[clamp(190px,25cqw,440px)] min-w-0 items-baseline gap-2">
      <span className="min-w-0 flex-1"><KindText kind={mapping.kind} /></span>
      {version !== "" && mapping.templatePath === "" && (
        <span className="shrink-0 text-muted-foreground" title={`Template version ${version}: no template folder on the mapping's path holds it.`}>
          {text}
        </span>
      )}
      {version !== "" && mapping.templatePath !== "" && (
        <Tooltip>
          <TooltipTrigger asChild>
            <RouterLink
              to={`/delivery/templates?${new URLSearchParams({ repoId: mapping.repoId, path: mapping.templatePath }).toString()}`}
              onClick={(event) => event.stopPropagation()}
              className="shrink-0 rounded-sm text-muted-foreground underline-offset-4 outline-none hover:text-primary hover:underline focus-visible:ring-2 focus-visible:ring-ring"
              data-testid="delivery-mappings-template"
            >
              {text}
            </RouterLink>
          </TooltipTrigger>
          <TooltipContent className="max-w-sm">{`Open the template it fills: version ${version}, read from ${mapping.templatePath}.`}</TooltipContent>
        </Tooltip>
      )}
    </span>
  );
}

const mappingColumns: Column<DeliveryMapping>[] = [
  { id: "reference", header: "Mapping", render: (row) => <MappingName mapping={row} /> },
  { id: "description", header: "Description", fill: true, floor: 120, render: (row) => <MappingAbout mapping={row} /> },
  { id: "template", header: "Template", render: (row) => <MappingTemplate mapping={row} /> },
  { id: "source", header: "Source", render: (row) => <MappingSource mapping={row} /> },
  {
    id: "entries",
    header: "Entries",
    align: "right",
    render: (row) => <span className="tabular-nums text-muted-foreground">{summaryText(row.summary, "entries") || "-"}</span>,
  },
  {
    id: "open",
    header: "",
    align: "right",
    width: 64,
    render: (row) => (
      <span className="inline-flex items-center gap-1">
        <GlyphRef icon={FileCode} title="Path" body={row.relativePath} mono />
        <MappingLineage mapping={row} />
      </span>
    ),
  },
];

/**
 * The mapping documents the synced repositories hold, what every delivery flow renders with, under a heading for each
 * repository when there are several: find one by anything the grid shows of it, and it opens on a page of its own. The
 * field has the cursor when the page opens, and Enter opens the first row listed. A mapping that does not load says why
 * in its row, and a line over the grid counts them. The caches mappings read are on the Cache page.
 */
export default function DeliveryDocumentsPage() {
  const navigate = useNavigate();
  const [repoFilter, setRepoFilter] = useLocalStorageState("sqlflow.filters.delivery-documents.repo", ALL_REPOSITORIES);
  const [search, setSearch] = useState("");
  const [problemsOnly, setProblemsOnly] = useState(false);

  const repos = useQuery({ queryKey: ["repos", "all-for-delivery-documents"], queryFn: () => repoApi.list({ page: 1, pageSize: 200 }) });
  const mappings = useQuery({ queryKey: ["delivery", "mappings", undefined], queryFn: () => deliveryApi.mappings() });

  // The repositories that hold a mapping, by name; one the repository list does not name is shown by its id.
  const names = useMemo(() => new Map((repos.data?.items ?? []).map((repo) => [repo.id, repo.name])), [repos.data]);
  const repositories = useMemo(() => repositoriesOf(mappings.data ?? [], (mapping) => mapping.repoId, names), [mappings.data, names]);
  const repoId = pickedRepository(repoFilter, repositories);

  const inRepository = useMemo(() => (mappings.data === undefined
    ? undefined
    : inRepositoryOrder(mappings.data.filter((mapping) => repoId === ALL_REPOSITORIES || mapping.repoId === repoId), (mapping) => mapping.repoId, names)),
  [mappings.data, repoId, names]);
  const failing = inRepository?.filter((mapping) => mapping.status !== "valid").length ?? 0;
  const onlyFailing = problemsOnly && failing > 0;
  const rows = useMemo(() => {
    if (inRepository === undefined) {
      return undefined;
    }

    const found = matchingMappings(inRepository, search, (mapping) => names.get(mapping.repoId) ?? "");
    return onlyFailing ? found.filter((mapping) => mapping.status !== "valid") : found;
  }, [inRepository, search, names, onlyFailing]);

  const grouping = repositoryGrouping(
    rows,
    { repoId: (mapping) => mapping.repoId, seenUtc: (mapping) => mapping.lastSeenUtc, failing: (mapping) => mapping.status !== "valid" },
    names,
    { one: "mapping", many: "mappings", failing: (count) => `${count} ${count === 1 ? "does" : "do"} not load` },
  );

  // Rows of one repository have no heading to say when its sync last saw them, so the foot of the grid says it.
  const lastSeen = latestOf((rows ?? []).map((mapping) => mapping.lastSeenUtc));
  const footNote = grouping !== undefined
    ? `in ${countOf(new Set(rows?.map((mapping) => mapping.repoId)).size, "repository", "repositories")}`
    : lastSeen !== null ? <>{"synced "}<RelativeTime value={lastSeen} absolute={false} /></> : undefined;

  const searchBox = useRef<HTMLDivElement>(null);
  useEffect(() => searchBox.current?.querySelector("input")?.focus(), []);
  const openFirst = (event: KeyboardEvent<HTMLDivElement>) => {
    const first = rows?.[0];
    if (event.key === "Enter" && first !== undefined) {
      navigate(mappingRoute(first.id));
    }
  };

  const searched = search.trim();
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
            placeholder="Search by name, description, kind, source or path"
            label="Search the mappings"
            className="sm:w-96"
            testId="delivery-documents-search"
          />
        </div>
        <RepositoryPicker value={repoId} onChange={setRepoFilter} repositories={repositories} testId="delivery-documents-repo" />
      </FilterBar>
      {mappings.isError && <ProblemView error={mappings.error} testId="delivery-mappings-error" />}
      {!mappings.isError && (
        <DimensionGrid testId="delivery-mappings-grid">
          <DataTable
            columns={mappingColumns}
            rows={rows}
            rowKey={(row) => row.id}
            onRowClick={(row) => navigate(mappingRoute(row.id))}
            grouping={grouping}
            toolbar={failing > 0 ? (
              <ProblemsLine
                problem={failingText(failing, onlyFailing)}
                only={onlyFailing}
                onOnly={setProblemsOnly}
                icon={<OctagonAlert className="size-3.5 shrink-0 text-destructive" aria-hidden />}
                testId="delivery-mappings-problems"
              />
            ) : undefined}
            footer={rows !== undefined ? (
              <GridFooter
                shown={rows.length}
                total={inRepository !== undefined && inRepository.length > rows.length ? inRepository.length : null}
                noun="mapping"
                hasMore={false}
                loading={false}
                onMore={() => undefined}
                note={footNote}
                testId="delivery-mappings-foot"
              />
            ) : undefined}
            emptyMessage={searched !== ""
              ? `No mapping matches '${searched}'. The search reads the name, description, kind, source, template, path and repository.`
              : "No mapping documents synced yet. A mapping is a YAML file with documentType: mapping under a repository's mappings directory, and the Mapping builder writes one."}
            data-testid="delivery-mappings-table"
          />
        </DimensionGrid>
      )}
    </Page>
  );
}
