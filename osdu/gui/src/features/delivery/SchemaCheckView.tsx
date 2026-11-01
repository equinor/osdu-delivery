import { Info } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { DataTable, type Column } from "@/components/DataTable";
import { ExplainMark, ExplainTip } from "@/components/ExplainTip";
import { TruncatedText } from "@/components/TruncatedText";
import { cn } from "@/lib/utils";
import { guidanceOf, verdictIn, type ValidationGuidance, type ValidationOutcome } from "../../api/validation";
import type { DeliveryPreviewSchemaCheck, DeliverySchemaCrossMatch, DeliverySchemaDifference } from "../../api/delivery";
import { schemaMatchDetail, schemaMatchVisual } from "./dryRun";
import { outcomeWord } from "./validationModel";
import { ValidationVerdictView } from "./ValidationVerdictView";
import { formatCount } from "./valueCheck";

/** What the partition's Schema service says of a kind in a line: a glyph in its tone and a few words, the whole of it on hover. */
export function SchemaMatchLine({ match, testId }: { match: DeliverySchemaCrossMatch; testId: string }) {
  const visual = schemaMatchVisual(match);
  const Icon = visual.icon;
  return (
    <ExplainTip title="The Schema service" text={schemaMatchDetail(match) || "The partition's Schema service holds this kind."} testId={`${testId}-tip`}>
      <span className="inline-flex min-w-0 cursor-default items-center gap-1.5 text-[12px]" data-state-name={match.state} data-testid={testId}>
        <Icon className={cn("size-3.5 shrink-0", visual.tone)} aria-hidden />
        <span className="min-w-0 truncate">{visual.word}</span>
      </span>
    </ExplainTip>
  );
}

const CHANGE_WORDS: Record<DeliverySchemaDifference["change"], string> = {
  onlyInService: "only in the Schema service",
  onlyInTemplate: "only in the template",
  different: "different",
};

/** What differs of a property, in words; the whole of a long pattern is on hover. */
function differenceText(row: DeliverySchemaDifference): string {
  const fields = row.fields.map((field) => `${field.field}: ${field.template ?? "none"} in the template, ${field.service ?? "none"} in the service`);
  return [CHANGE_WORDS[row.change], ...fields].join("; ");
}

// The property is what a reader looks for, so it keeps the width; how it differs reads under it, whatever its length.
const differenceColumns: Column<DeliverySchemaDifference>[] = [
  {
    id: "path",
    header: "Property",
    fill: true,
    render: (row) => (
      <span className="flex min-w-0 flex-col">
        <TruncatedText text={row.path.replace(/^osdu\./, "")} mono maxWidth={520} />
        <TruncatedText text={differenceText(row)} maxWidth={520} className="text-[11.5px] text-muted-foreground" />
      </span>
    ),
  },
  {
    id: "impact",
    header: "For a document",
    render: (row) => (
      <Badge variant="outline" className={row.impact === "breaking" ? "font-normal text-destructive" : "font-normal"}>
        {row.impact === "breaking" ? "can be refused" : "still accepted"}
      </Badge>
    ),
  },
];

/** Each property the template and the Schema service's schema do not agree on, the ones that can refuse a document first. */
export function SchemaDifferences({ match, testId }: { match: DeliverySchemaCrossMatch; testId: string }) {
  if (match.differences.length === 0) {
    return null;
  }

  return (
    <section className="flex min-w-0 flex-col gap-1" data-testid={testId}>
      <h3 className="text-[13px] font-medium">What differs between the template and the Schema service</h3>
      <p className="text-[12px] text-muted-foreground">
        {match.breakingCount > 0
          ? `The mapping was written for the template, and the partition holds records to its own schema. ${formatCount(match.breakingCount)} of these can make it refuse a document the template accepts.`
          : "The mapping was written for the template, and the partition holds records to its own schema. None of these should refuse a document the template accepts."}
      </p>
      <DataTable columns={differenceColumns} rows={match.differences} rowKey={(row) => row.path} emptyMessage="Nothing differs." data-testid={`${testId}-table`} />
      {match.differenceCount > match.differences.length && (
        <p className="text-[12px] text-muted-foreground">{`The first ${formatCount(match.differences.length)} of ${formatCount(match.differenceCount)} are listed.`}</p>
      )}
    </section>
  );
}

/** Says where the template and the Schema service disagree about this one document; nothing when they agree. */
function Disagreement({ check, outcome }: { check: DeliveryPreviewSchemaCheck; outcome: ValidationOutcome | null }) {
  const template = (check.templateOutcome ?? null) as ValidationOutcome | null;
  if (template === null || outcome === null || template === outcome) {
    return null;
  }

  return (
    <Alert data-testid="schema-check-disagreement">
      <Info />
      <AlertDescription>
        {`The template the mapping pins says this document is ${outcomeWord(template)}, and the Schema service's schema says it is ${outcomeWord(outcome)}. OSDU holds records to the schema, so that is the answer to trust; the template is what to bring up to date.`}
      </AlertDescription>
    </Alert>
  );
}

/**
 * One document against the partition's Schema service: what the service says of the kind, where it and the template disagree,
 * the verdict with how to meet each rule, and each record the document's enrichments add, against its own kind. The verdict
 * is reached here, by the check a delivery makes: the service serves schemas and validates nothing itself, which the
 * section says, so a reader knows what they are looking at.
 */
export function SchemaCheckPane({ check }: { check: DeliveryPreviewSchemaCheck }) {
  const verdict = verdictIn(check.verdict);
  const guidance: ValidationGuidance | null = guidanceOf(check.guidance as Partial<ValidationGuidance> | null | undefined);
  return (
    <div className="flex min-w-0 flex-col gap-3" data-testid="schema-check">
      <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1">
        <SchemaMatchLine match={check.schema} testId="schema-check-match" />
        <TruncatedText text={check.schema.kind} mono maxWidth={420} className="text-[12px] text-muted-foreground" />
        {check.schema.status && <Badge variant="outline" className="font-normal" data-testid="schema-check-status">{check.schema.status}</Badge>}
        <ExplainMark
          title="How a document is held to the Schema service"
          text="The Schema service serves schemas and validates nothing itself. The schema it holds for this kind is read, with every schema it refers to, and the document is held to it here by the check a delivery makes before it sends a record, as the route would send it."
          testId="schema-check-about"
        />
      </div>
      {check.problem && <Alert data-testid="schema-check-problem"><Info /><AlertDescription>{check.problem}</AlertDescription></Alert>}
      <Disagreement check={check} outcome={verdict?.outcome ?? null} />
      {verdict !== null && <ValidationVerdictView verdict={verdict} guidance={guidance} />}
      <SchemaDifferences match={check.schema} testId="schema-check-differences" />
      {check.added.length > 0 && (
        <section className="flex min-w-0 flex-col gap-2" data-testid="schema-check-added">
          <h3 className="text-[13px] font-medium">Records its enrichments add</h3>
          {check.added.map((added) => {
            const addedVerdict = verdictIn(added.verdict);
            return (
              <div key={added.id} className="flex min-w-0 flex-col gap-1.5 rounded-md border px-3 py-2" data-testid="schema-check-added-record">
                <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1">
                  <TruncatedText text={added.id} mono maxWidth={420} copy className="text-[12px]" />
                  <SchemaMatchLine match={added.schema} testId="schema-check-added-match" />
                </div>
                {addedVerdict !== null
                  ? <ValidationVerdictView verdict={addedVerdict} />
                  : added.schema.why && <p className="text-[12px] text-muted-foreground">{added.schema.why}</p>}
              </div>
            );
          })}
        </section>
      )}
    </div>
  );
}
