import { Link as RouterLink, useParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft, FileCode, LayoutTemplate, OctagonAlert, PencilRuler } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { CodeView } from "@/components/CodeView";
import { GlyphRef } from "@/components/GlyphRef";
import { useTabTitle } from "@/layout/workbench/TabsContext";
import { deliveryApi, type DeliveryMappingDetail } from "../../api/delivery";
import { KindText } from "./KindText";
import { MappingCoverageView } from "./MappingCoverageView";
import { MappingLineage } from "./MappingLineage";
import { MappingPicker } from "./MappingPicker";
import { MappingShapeView } from "./MappingShapeView";
import { MappingStatus } from "./MappingStatus";
import { StagedRecordsTab } from "./StagedRecordsTab";
import { ProblemView } from "./TemplateSheet";
import { useStagedView } from "./useStagedView";
import { ValueCheckContext, useValueCheckSession } from "./useValueCheck";
import { DryRunButton, ValueCheckControls } from "./ValueCheckBar";
import { WindowFrame } from "./WindowFrame";

/** The way back to the list of mappings. */
function BackToMappings() {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Button asChild variant="ghost" size="icon-sm" aria-label="All mappings" data-testid="delivery-mapping-back">
          <RouterLink to="/delivery/documents"><ArrowLeft /></RouterLink>
        </Button>
      </TooltipTrigger>
      <TooltipContent>All mappings</TooltipContent>
    </Tooltip>
  );
}

/** The page's one heading: the mapping, a click from any other, what it renders and where it is read from, and what leads away from it. */
function MappingHeader({ detail, id, loading }: { detail: DeliveryMappingDetail | undefined; id: string; loading: boolean }) {
  const mapping = detail?.mapping;
  const version = mapping === undefined || mapping.templateVersion === "" ? null : mapping.templateVersion;
  return (
    <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
      <div className="flex min-w-0 flex-[1_1_20rem] items-center gap-1">
        <BackToMappings />
        {loading && <Skeleton className="h-6 w-56" />}
        {mapping !== undefined && (
          <>
            <h1 className="min-w-0"><MappingPicker current={id} name={mapping.reference} /></h1>
            <span className="min-w-0 flex-1 pl-1 text-muted-foreground" data-testid="delivery-mapping-kind"><KindText kind={mapping.kind} /></span>
          </>
        )}
      </div>
      {mapping !== undefined && (
        <div className="ml-auto flex shrink-0 items-center gap-1">
          <GlyphRef icon={FileCode} title="Path" body={mapping.relativePath} mono />
          <MappingLineage mapping={mapping} />
          {version !== null && mapping.templatePath !== "" && (
            <Button
              asChild
              size="sm"
              variant="ghost"
              title={`The template the mapping fills, read from ${mapping.templatePath}`}
              data-testid="delivery-mapping-template"
            >
              <RouterLink to={`/delivery/templates?${new URLSearchParams({ repoId: mapping.repoId, path: mapping.templatePath }).toString()}`}>
                <LayoutTemplate />
                <span className="font-mono text-[12px]">{version}</span>
              </RouterLink>
            </Button>
          )}
          {version !== null && mapping.templatePath === "" && (
            <span className="px-2 font-mono text-[12px] text-muted-foreground" title="The template version the mapping names; no template folder on its path holds it" data-testid="delivery-mapping-template">{version}</span>
          )}
          <Button asChild size="sm" variant="outline" data-testid="delivery-mapping-open-builder">
            <RouterLink to={`/delivery/mappings/build?mappingId=${encodeURIComponent(mapping.id)}`}>
              <PencilRuler />
              Open in builder
            </RouterLink>
          </Button>
        </div>
      )}
    </div>
  );
}

/**
 * Everything a mapping is worked on with, in one window that does not scroll: one toolbar saying which rows are read and running
 * a dry run over them, one line saying what came of it, and under them the two ways to read the mapping. Staged records is the
 * records beside what they render to; Properties is the template the mapping fills, attribute by attribute. A dry run is one
 * session of the page, so what it found reads in either, and neither loses it when the reader moves between them.
 */
function MappingWorkspace({ detail }: { detail: DeliveryMappingDetail }) {
  const { mapping, yaml } = detail;
  const session = useValueCheckSession(mapping.id, mapping.reference);
  const staged = useStagedView(session);
  return (
    <ValueCheckContext.Provider value={session}>
      <WindowFrame testId="delivery-mapping-detail">
        <Tabs defaultValue="staged" className="min-h-0 flex-1">
          <div className="flex shrink-0 flex-wrap items-center gap-2" data-testid="value-check-controls">
            <TabsList className="h-8" data-testid="delivery-mapping-tabs">
              <TabsTrigger value="staged" className="text-[13px]" data-testid="delivery-mapping-tab-staged">Staged records</TabsTrigger>
              <TabsTrigger value="coverage" className="text-[13px]" data-testid="delivery-mapping-tab-coverage">Properties</TabsTrigger>
            </TabsList>
            <ValueCheckControls session={session} rowsLabel="How many rows a dry run reads" />
            <DryRunButton session={session} />
          </div>
          <MappingStatus session={session} />
          <TabsContent value="staged" className="flex min-h-0 flex-col">
            <StagedRecordsTab
              session={session}
              view={staged}
              yaml={<CodeView value={yaml} language="yaml" fill data-testid="delivery-mapping-yaml" />}
              shape={<MappingShapeView yaml={yaml} path={mapping.relativePath} contentHash={mapping.contentHash} repoId={mapping.repoId} />}
            />
          </TabsContent>
          <TabsContent value="coverage" className="flex min-h-0 flex-col">
            <MappingCoverageView
              yaml={yaml}
              path={mapping.relativePath}
              contentHash={mapping.contentHash}
              repoId={mapping.repoId}
              templatePath={mapping.templatePath === "" ? null : mapping.templatePath}
            />
          </TabsContent>
        </Tabs>
      </WindowFrame>
    </ValueCheckContext.Provider>
  );
}

/** A mapping on a page of its own, reached from the Mappings list or by its address, which is the same page a link gives anyone. */
export default function DeliveryMappingPage() {
  const { mappingId = "" } = useParams();
  const mapping = useQuery({
    queryKey: ["delivery", "mapping", mappingId],
    queryFn: () => deliveryApi.mapping(mappingId),
  });
  const detail = mapping.data;
  useTabTitle(detail?.mapping.reference);

  return (
    <div className="flex flex-col gap-2" data-testid="page-delivery-mapping">
      <MappingHeader detail={detail} id={mappingId} loading={mapping.isPending} />
      {mapping.isError && <ProblemView error={mapping.error} testId="delivery-mapping-error" />}
      {detail?.mapping.message && (
        <p className="flex items-start gap-1.5 text-[12px] text-destructive" data-testid="delivery-mapping-message">
          <OctagonAlert className="mt-0.5 size-3.5 shrink-0" />
          {detail.mapping.message}
        </p>
      )}
      {/* Another mapping, or this one synced with new content, is other rows and other documents: it starts afresh. */}
      {detail !== undefined && <MappingWorkspace key={`${detail.mapping.id}|${detail.mapping.contentHash}`} detail={detail} />}
    </div>
  );
}
