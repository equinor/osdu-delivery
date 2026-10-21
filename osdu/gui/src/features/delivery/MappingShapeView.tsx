import { useState } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { Loader2, OctagonAlert } from "lucide-react";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { deliveryApi } from "../../api/delivery";
import { CodeView } from "@/components/CodeView";
import { ProblemView } from "./TemplateSheet";

/** How long a parameter value has to stay unchanged before the shape is drawn again. */
const SHAPE_DELAY_MS = 400;

interface MappingShapeViewProps {
  /** The mapping document as the catalog holds it. */
  yaml: string;
  /** Its path in the repository, which the messages name. */
  path: string;
  /** The document's content hash, so a mapping synced with new content is drawn again. */
  contentHash: string;
  /** The synced repository the mapping is a document of, whose template folders settle a template it names by kind alone. */
  repoId?: string | null;
}

/**
 * The shape of the records a mapping renders, drawn by the control plane through the renderer delivery uses: `id`, `kind`
 * and the envelope as a render writes them, static values as they render, and a placeholder naming the template's type
 * and the source wherever a value comes from a row or the cache. No data is read; the parameter values only fill what
 * uses them.
 */
export function MappingShapeView({ yaml, path, contentHash, repoId }: MappingShapeViewProps) {
  const [values, setValues] = useState<Record<string, string>>({});
  const settled = useDebouncedValue(values, SHAPE_DELAY_MS);
  const shape = useQuery({
    queryKey: ["delivery", "mapping-shape", repoId ?? null, path, contentHash, settled],
    queryFn: () => deliveryApi.mappingShape(yaml, path, settled, repoId),
    placeholderData: keepPreviousData,
  });
  const data = shape.data;

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-3" data-testid="delivery-mapping-shape">
      {data !== undefined && data.parameters.length > 0 && (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          {data.parameters.map((parameter) => (
            <div key={parameter.name} className="flex flex-col gap-1">
              <Label htmlFor={`delivery-mapping-shape-${parameter.name}`} className="font-mono text-[12px]">
                {parameter.name}
              </Label>
              <Input
                id={`delivery-mapping-shape-${parameter.name}`}
                className="h-8 font-mono"
                placeholder={parameter.default ?? ""}
                value={values[parameter.name] ?? ""}
                onChange={(event) => {
                  const value = event.target.value;
                  setValues((current) => ({ ...current, [parameter.name]: value }));
                }}
                data-testid={`delivery-mapping-shape-parameter-${parameter.name}`}
              />
              {parameter.description !== null && <p className="text-xs text-muted-foreground">{parameter.description}</p>}
            </div>
          ))}
        </div>
      )}

      <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
        {shape.isFetching && <Loader2 className="size-3 shrink-0 animate-spin" />}
        Values in angle brackets are placeholders: the type the template gives the property, and where the value comes from.
        Static values show as they render.
      </p>

      {shape.isError && <ProblemView error={shape.error} testId="delivery-mapping-shape-error" />}
      {data?.issues.map((issue, index) => (
        <p
          key={`${index}-${issue.message}`}
          className="flex items-start gap-1.5 text-[13px] text-destructive"
          data-testid="delivery-mapping-shape-issue"
        >
          <OctagonAlert className="mt-0.5 size-3.5 shrink-0" />
          {issue.message}
        </p>
      ))}
      {data !== undefined && data.notes.length > 0 && (
        <ul className="flex list-disc flex-col gap-0.5 pl-4 text-xs text-muted-foreground" data-testid="delivery-mapping-shape-notes">
          {data.notes.map((note, index) => <li key={`${index}-${note}`}>{note}</li>)}
        </ul>
      )}

      {shape.isPending && <Skeleton className="min-h-80 w-full flex-1" />}
      {data !== undefined && data.record !== null && (
        <CodeView value={JSON.stringify(data.record, null, 2)} language="json" fill data-testid="delivery-mapping-shape-json" />
      )}
    </div>
  );
}
