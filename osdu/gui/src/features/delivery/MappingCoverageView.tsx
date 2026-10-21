import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { OctagonAlert, TriangleAlert } from "lucide-react";
import { cn } from "@/lib/utils";
import { deliveryApi } from "../../api/delivery";
import { MappingPropertiesView } from "./MappingPropertiesView";
import { TemplateVariableExplorer } from "./TemplateVariableExplorer";
import { ProblemView } from "./TemplateSheet";
import { useParsedMapping } from "./useParsedMapping";
import { ValueCheckContext, useValueCheckSession } from "./useValueCheck";
import { ValueCheckBar } from "./ValueCheckBar";
import { variableRows } from "./variableRows";

interface MappingCoverageViewProps {
  /** The mapping as the catalog holds it, whose flows a value check reads the rows of. */
  mappingId: string;
  /** Its reference (Name@version), which a flow checked must render with. */
  reference: string;
  /** The mapping document as the catalog holds it. */
  yaml: string;
  /** Its path in the repository, which the messages name. */
  path: string;
  /** The document's content hash, so a mapping synced with new content is measured again. */
  contentHash: string;
  /** The synced repository the mapping is a document of, whose template folders settle a template it names by kind alone. */
  repoId?: string | null;
}

/**
 * The template a mapping pins with the mapping laid over it: the data check on one line, then the record's tree beside
 * the selected attribute. The tree opens on what the mapping fills and what a check names, and marks a row only where
 * something asks for attention, so the few that need a look are the ones that stand out. Nothing is rendered and no
 * cache is read; this is the document against the schema alone, until a data check renders the rows of a flow that
 * uses the mapping and says, attribute by attribute, which rows will not give it the value its template expects.
 */
export function MappingCoverageView({ mappingId, reference, yaml, path, contentHash, repoId }: MappingCoverageViewProps) {
  const valueCheck = useValueCheckSession(mappingId, reference);
  const coverage = useQuery({
    queryKey: ["delivery", "mapping-coverage", repoId ?? null, path, contentHash],
    queryFn: () => deliveryApi.mappingCoverage(yaml, path, repoId),
  });
  const parsed = useParsedMapping(yaml, path, contentHash, repoId);

  const kind = coverage.data?.kind ?? null;
  const version = coverage.data?.version ?? null;
  const template = useQuery({
    queryKey: ["delivery", "template-detail", kind, version],
    queryFn: () => deliveryApi.templateDetail(kind!, version!),
    enabled: kind !== null && version !== null && (coverage.data?.variables.length ?? 0) > 0,
  });

  // A finding on a variable is shown on the variable itself; what is left here is about the document as a whole.
  const issues = (coverage.data?.issues ?? []).filter((issue) => issue.target === null);

  // An entry the template does not let a mapping fill sits on no row, so it is named here instead of going unseen.
  const outside = useMemo(() => {
    const draft = parsed.data?.draft ?? null;
    return template.data === undefined || draft === null
      ? []
      : variableRows(template.data, draft).filter((row) => row.kind === "outside");
  }, [template.data, parsed.data]);

  // A document the builder cannot draft (lookups, $findAll, lists) is still laid over its template, without its entries.
  const refused = parsed.isSuccess && parsed.data.draft === null;
  const overlay = coverage.data !== undefined && coverage.data.variables.length > 0 && (parsed.data?.draft != null || refused)
    ? { coverage: coverage.data, entries: parsed.data?.draft?.entries ?? [] }
    : undefined;

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-3" data-testid="delivery-mapping-coverage">
      {coverage.isError && <ProblemView error={coverage.error} testId="delivery-mapping-coverage-error" />}
      {template.isError && <ProblemView error={template.error} testId="delivery-mapping-coverage-template-error" />}
      {parsed.isError && <ProblemView error={parsed.error} testId="delivery-mapping-coverage-parse-error" />}
      {refused && overlay !== undefined && (
        <p className="flex items-start gap-1.5 text-[13px] text-muted-foreground" data-testid="delivery-mapping-coverage-entries-unread">
          <TriangleAlert className="mt-0.5 size-3.5 shrink-0" />
          <span>
            The tree shows what the mapping fills, but not the entry filling each property:{" "}
            {parsed.data?.issues.map((issue) => issue.message).join(" ")} The YAML tab has the document as written.
          </span>
        </p>
      )}
      {issues.map((issue, index) => (
        <p
          key={`${index}-${issue.message}`}
          className={cn("flex items-start gap-1.5 text-[13px]", issue.severity === "error" ? "text-destructive" : "text-warning")}
          data-testid="delivery-mapping-coverage-issue"
        >
          {issue.severity === "error"
            ? <OctagonAlert className="mt-0.5 size-3.5 shrink-0" />
            : <TriangleAlert className="mt-0.5 size-3.5 shrink-0" />}
          {issue.message}
        </p>
      ))}
      {outside.map((row) => (
        <p key={row.key} className="flex items-start gap-1.5 text-[13px] text-destructive" data-testid="delivery-mapping-coverage-outside">
          <OctagonAlert className="mt-0.5 size-3.5 shrink-0" />
          {row.outside}
        </p>
      ))}

      {template.data !== undefined && overlay !== undefined && (
        <ValueCheckContext.Provider value={valueCheck}>
          <ValueCheckBar session={valueCheck} />
          <TemplateVariableExplorer variables={template.data.variables} mapping={overlay} fill />
        </ValueCheckContext.Provider>
      )}

      {/*
        * Without a template to lay the mapping over, its own entries are still what a reader came for, so the document
        * is listed as it is written: a mapping pinning a version nobody saved is exactly when one wants to read it.
        */}
      {coverage.isSuccess && coverage.data.variables.length === 0 && (
        <MappingPropertiesView yaml={yaml} path={path} contentHash={contentHash} repoId={repoId} />
      )}
    </div>
  );
}
