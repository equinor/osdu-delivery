import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { isApiError } from "@/api/client";
import type { DeliveryTemplateDetail } from "../../api/delivery";
import { CodeView } from "@/components/CodeView";
import { CorrelationError } from "@/components/CorrelationError";
import { TemplateVariableExplorer } from "./TemplateVariableExplorer";
import { templateKey, templateSchemaQuery } from "./templateFormat";

interface TemplateViewProps {
  detail: DeliveryTemplateDetail;
  /** The schema being looked at before it is saved, for the Schema JSON tab. Without it, the saved template's schema is loaded. */
  previewSchema?: Record<string, unknown>;
}

/** The schema as JSON: the one being previewed, or the saved template's, loaded when the tab opens. */
function SchemaJson({ detail, previewSchema }: { detail: DeliveryTemplateDetail; previewSchema?: Record<string, unknown> }) {
  const saved = useQuery({ ...templateSchemaQuery(detail.kind, detail.version), enabled: previewSchema === undefined });
  const previewText = useMemo(
    () => (previewSchema === undefined ? null : JSON.stringify(previewSchema, null, 2)),
    [previewSchema],
  );

  if (previewText !== null) {
    return <CodeView value={previewText} language="json" height={560} data-testid="templates-view-schema-json" />;
  }

  if (saved.isError) {
    return isApiError(saved.error)
      ? <CorrelationError error={saved.error} />
      : <p className="text-[13px] text-destructive">{String(saved.error)}</p>;
  }

  if (saved.data === undefined) {
    return <Skeleton className="h-[560px] w-full rounded-lg" />;
  }

  return <CodeView value={saved.data} language="json" height={560} data-testid="templates-view-schema-json" />;
}

/**
 * A template laid out for reading, under the header its sheet gives it: its variables as the record's tree with the
 * selected variable's shape, requiredness, relationships, unit context and OSDU's description beside it, and the schema
 * itself. The Templates page shows a saved version, an OSDU schema fetched for a look, and an imported file through this
 * one view.
 */
export function TemplateView({ detail, previewSchema }: TemplateViewProps) {
  return (
    <Tabs defaultValue="variables" data-testid="templates-view">
      <TabsList data-testid="templates-view-tabs">
        <TabsTrigger value="variables" data-testid="templates-view-tab-variables">Variables</TabsTrigger>
        <TabsTrigger value="schema" data-testid="templates-view-tab-schema">Schema JSON</TabsTrigger>
      </TabsList>
      <TabsContent value="variables" className="pt-1">
        {/* A different template starts from its own tree: nothing opened or selected in the last one carries over. */}
        <TemplateVariableExplorer key={templateKey(detail.kind, detail.version)} variables={detail.variables} />
      </TabsContent>
      <TabsContent value="schema" className="pt-1">
        <SchemaJson detail={detail} previewSchema={previewSchema} />
      </TabsContent>
    </Tabs>
  );
}
