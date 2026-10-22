import { useEffect, useMemo, useState } from "react";
import { CircleAlert, Info, RefreshCw, ShieldCheck } from "lucide-react";
import { IconAction } from "@/components/IconAction";
import { RichTooltip } from "@/components/RichTooltip";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import {
  EXPLORER_MAX_CHECKED, explorerApi, type ExplorerAnswer, type ExplorerListValidation, type ExplorerSchemaChoice, type ExplorerSearchRequest,
  type ExplorerValidateRequest, type ExplorerValidation,
} from "../../../api/explorer";
import { whereOf } from "../../../api/validation";
import { AssertionActionBadge, AssertionActionGlyph } from "../AssertionFindingsView";
import { idParts } from "../osduRecordModel";
import type { InspectorField } from "../OsduRecordInspector";
import { TaskProgress } from "../TemplateSheet";
import { ValidationCount, ValidationGlyph } from "../ValidationMark";
import { ValidationVerdictView } from "../ValidationVerdictView";
import { assertionActionWord, assertionFailuresAt, problemsAt } from "../validationModel";
import { ExplorerMappingPicker } from "./ExplorerMappingPicker";
import { ExplorerProblem } from "./ExplorerProblem";
import { counted, kindParts, useExplorerRead } from "./explorerModel";

/** Which schema a check reads: the Schema service's, or a template a synced repository holds (its version, or the one synced last). */
interface SchemaChoice {
  schema: ExplorerSchemaChoice;
  templateVersion?: string;
}

/** The schema picker of a check: OSDU's own, or a repository's template, with the versions the repositories hold to pick from where they are known. */
function SchemaPicker({ choice, held, onChoose }: { choice: SchemaChoice; held: string[]; onChoose: (choice: SchemaChoice) => void }) {
  return (
    <span className="inline-flex flex-wrap items-center gap-2">
      <ToggleGroup
        type="single"
        value={choice.schema}
        onValueChange={(value) => { if (value === "osdu" || value === "template") { onChoose({ schema: value }); } }}
        variant="outline"
        size="sm"
        data-testid="explorer-validate-schema"
      >
        <ToggleGroupItem value="osdu" className="h-7 px-2.5 text-xs" title="What the partition's Schema service holds for the kind" data-testid="explorer-validate-osdu">OSDU schema</ToggleGroupItem>
        <ToggleGroupItem value="template" className="h-7 px-2.5 text-xs" title="A template a synced repository holds in a template folder, read from its file" data-testid="explorer-validate-template">Repository template</ToggleGroupItem>
      </ToggleGroup>
      {choice.schema === "template" && held.length > 0 && (
        <Select value={choice.templateVersion ?? held[0]} onValueChange={(templateVersion) => onChoose({ schema: "template", templateVersion })}>
          <SelectTrigger size="sm" className="h-7 w-[190px] font-mono text-[11px]" data-testid="explorer-validate-version">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {held.map((version, index) => (
              <SelectItem key={version} value={version} className="font-mono text-[11px]">{index === 0 ? `${version} (synced last)` : version}</SelectItem>
            ))}
          </SelectContent>
        </Select>
      )}
    </span>
  );
}

/**
 * Where a check reads the record it checks: through the partition's own connection, as the explorer reads, or through the
 * route and credentials of the flow a record page reads the record by. `key` tells one source's answers from another's.
 */
export interface ValidationSource {
  key: readonly unknown[];
  validate: (request: ExplorerValidateRequest) => Promise<ExplorerAnswer<ExplorerValidation>>;
}

/**
 * A record OSDU holds checked against the schema of its kind (osdu/docs/reference/concepts/explorer.md, Validate), in
 * the explorer or on a record page's OSDU tab: by default what the partition's Schema service holds, which is what OSDU
 * expects of the record; or a template a synced repository holds, read from its file. The problems open their element in the record. What was
 * read to make the schema, and what could not be resolved, is in the tooltip beside the schema. In the explorer a mapping
 * can be picked whose assertions are judged on the record too (osdu/docs/reference/flow/mapping-assertions.md); a record
 * page's check judges those of the flow's own mapping. The answer is handed up, so the record's fields can carry its marks.
 */
export function ExplorerValidationView({ source, id, version, onOpenPath, onResult, pickMapping = false }: {
  source: ValidationSource;
  id: string;
  /** The version in view; null for the latest. */
  version: number | null;
  onOpenPath: (path: string) => void;
  onResult?: (result: ExplorerValidation | null) => void;
  /** Whether the check offers a mapping whose assertions it judges; where it does not, the source decides which, if any. */
  pickMapping?: boolean;
}) {
  const [choice, setChoice] = useState<SchemaChoice>({ schema: "osdu" });
  const [mapping, setMapping] = useState<string | null>(null);
  const read = useExplorerRead<ExplorerValidation>(
    ["validate", ...source.key, id, version, choice.schema, choice.templateVersion ?? null, mapping],
    () => source.validate({ targetId: id, version: version ?? undefined, schema: choice.schema, templateVersion: choice.templateVersion, mapping: mapping ?? undefined }),
  );
  const answer = read.data?.answer;
  useEffect(() => onResult?.(answer ?? null), [answer, onResult]);

  const schemaNotes = answer?.schema
    ? [
      `${answer.schema.kind}, content version ${answer.schema.version}, from ${answer.schema.source === "schema-service" ? "the partition's Schema service" : "a template a synced repository holds"}.`,
      answer.schema.read.length > 1 ? `Read from the Schema service to make it: ${answer.schema.read.join(", ")}.` : null,
      ...answer.schema.unresolved.map((why) => `Not resolved: ${why}.`),
    ].filter((line): line is string => line !== null)
    : [];

  return (
    <div className="flex flex-col gap-3 p-3" data-testid="explorer-validation">
      <div className="flex flex-wrap items-center gap-2 text-[12px]">
        <ShieldCheck className="size-4 text-muted-foreground" aria-hidden />
        <span className="font-medium">Checked against</span>
        <SchemaPicker choice={choice} held={answer?.templateVersions ?? []} onChoose={setChoice} />
        {schemaNotes.length > 0 && (
          <RichTooltip title="The schema" body={schemaNotes.join("\n\n")}>
            <Info className="size-3.5 text-muted-foreground" aria-label="The schema" data-testid="explorer-validate-schema-info" />
          </RichTooltip>
        )}
        {pickMapping && (
          <>
            <span className="font-medium">and</span>
            <ExplorerMappingPicker kind={answer?.kind ?? null} value={mapping} onChoose={setMapping} />
          </>
        )}
        <IconAction
          label="Check again"
          icon={<RefreshCw className={read.isFetching ? "animate-spin" : undefined} />}
          variant="ghost"
          className="ml-auto size-7"
          disabled={read.isFetching}
          onClick={() => void read.refetch()}
          data-testid="explorer-validate-again"
        />
      </div>
      {read.isPending && <TaskProgress label="Checking the record against its schema" testId="explorer-validate-progress" />}
      {read.isError && <ExplorerProblem error={read.error} />}
      {answer?.problem && (
        <Alert data-testid="explorer-validate-problem">
          <CircleAlert />
          <AlertTitle>The record was not checked</AlertTitle>
          <AlertDescription>
            <span className="text-[12px]">{answer.problem}</span>
            {choice.schema === "osdu" && answer.templateVersions.length > 0 && (
              <Button variant="outline" size="sm" className="mt-1 h-7 w-fit" onClick={() => setChoice({ schema: "template", templateVersion: answer.templateVersions[0] })} title={`The repository's template of content version ${answer.templateVersions[0]}`} data-testid="explorer-validate-use-template">
                Check against the repository's template
              </Button>
            )}
          </AlertDescription>
        </Alert>
      )}
      {answer?.verdict && <ValidationVerdictView verdict={answer.verdict} guidance={answer.guidance} onOpenPath={onOpenPath} />}
    </div>
  );
}

/**
 * The mark a field of the record `id` carries when the last check of it found a problem at the field, or a value there
 * that fails an assertion of the mapping judged, or either inside it for a section: the glyph (a problem's before an
 * assertion's, a hold before a report), and what is wrong in its tooltip. Only the version the check read carries marks,
 * since another version in view holds other values.
 */
export function ValidationFieldMark({ result, id, field }: { result: ExplorerValidation | null; id: string; field: InspectorField }) {
  if (field.level !== 0 || result === null || result.targetId !== id) {
    return null;
  }

  const shown = field.trail[field.level]?.record;
  if (shown === null || shown === undefined || (result.version !== null && shown.version !== result.version)) {
    return null;
  }

  const section = field.node.kind === "object" || field.node.kind === "array";
  const problems = result.verdict?.problems ?? [];
  const found = problemsAt(problems, field.node.path, section);
  const failed = assertionFailuresAt(result.verdict?.assertions?.failures ?? [], field.node.path, section);
  if (found.length === 0 && failed.length === 0) {
    return null;
  }

  if (found.length === 0) {
    const strongest = failed.find((f) => f.onFail === "hold") ?? failed.find((f) => f.onFail === "report") ?? failed[0];
    const lines = failed.slice(0, 8).map((f) => `${f.path} fails "${f.assertion}" (${assertionActionWord(f.onFail)}): ${f.message}`).join("\n\n")
      + (failed.length > 8 ? `\n\nand ${failed.length - 8} more` : "");
    return (
      <RichTooltip title={`${failed.length} assertion failure${failed.length === 1 ? "" : "s"} here`} body={lines}>
        <span className="inline-flex size-5 items-center justify-center" data-testid="explorer-assertion-mark">
          <AssertionActionGlyph action={strongest.onFail} />
        </span>
      </RichTooltip>
    );
  }

  // Each problem with how to fix it, where the check said; the guidance lists one guide per problem, in the verdict's order.
  const advice = (problem: (typeof problems)[number]) => {
    const guide = result.guidance?.problems[problems.indexOf(problem)];
    return guide !== undefined && guide.path === problem.path && guide.advice !== null ? `\nFix: ${guide.advice}` : "";
  };
  const body = found.slice(0, 8).map((p) => `${whereOf(p)} (${p.rule}): ${p.message}${advice(p)}`).join("\n\n")
    + (found.length > 8 ? `\n\nand ${found.length - 8} more` : "")
    + (failed.length > 0 ? `\n\nand ${failed.length} assertion failure${failed.length === 1 ? "" : "s"} of ${result.verdict?.assertions?.mapping ?? "the mapping"}` : "");
  return (
    <RichTooltip title={`${found.length} problem${found.length === 1 ? "" : "s"} the schema finds here`} body={body}>
      <span className="inline-flex size-5 items-center justify-center" data-testid="explorer-validation-mark">
        <ValidationGlyph outcome="invalid" />
      </span>
    </RichTooltip>
  );
}

/**
 * The records a search finds checked against their schemas, up to 1,000 (osdu/docs/reference/concepts/explorer.md,
 * Validate): how many came to each outcome, the rules broken most often with the records that break them and an example
 * of each, and each record with its first problem; a record opens in the explorer. With a mapping picked, its assertions
 * are judged on the records of the entity type it renders, and the assertions failed most often are counted the same way.
 * A search matching more records than are read says so: the counts are of the first ones, and a whole kind is an
 * assertion flow's conforms test (or its mapping test, for a mapping's assertions).
 */
export function ExplorerValidateDialog({ partition, request, open, onOpenChange, onOpenRecord }: {
  partition: string | null;
  request: ExplorerSearchRequest;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onOpenRecord: (id: string) => void;
}) {
  const [schema, setSchema] = useState<ExplorerSchemaChoice>("osdu");
  const [mapping, setMapping] = useState<string | null>(null);
  const search = useMemo(() => ({ ...request, offset: undefined, limit: undefined, facet: undefined }), [request]);
  const read = useExplorerRead<ExplorerListValidation>(
    ["validate-list", partition, search, schema, mapping],
    open ? () => explorerApi.validateList(partition, { search, max: EXPLORER_MAX_CHECKED, schema, mapping: mapping ?? undefined }) : null,
  );
  const answer = read.data?.answer;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="flex max-h-[85vh] flex-col gap-3 overflow-hidden sm:max-w-5xl" data-testid="explorer-validate-list">
        <DialogHeader>
          <DialogTitle>Validate these records</DialogTitle>
          <DialogDescription>
            {`The first ${EXPLORER_MAX_CHECKED.toLocaleString("en-US")} records the search finds, read from OSDU and checked against the schema of their kind.`}
          </DialogDescription>
        </DialogHeader>
        <div className="flex flex-wrap items-center gap-2 text-[12px]">
          <span className="font-medium">Checked against</span>
          <SchemaPicker choice={{ schema }} held={[]} onChoose={(choice) => setSchema(choice.schema)} />
          <span className="font-medium">and</span>
          <ExplorerMappingPicker kind={request.kind ?? null} value={mapping} onChoose={setMapping} />
          <IconAction label="Check again" icon={<RefreshCw className={read.isFetching ? "animate-spin" : undefined} />} variant="ghost" className="ml-auto size-7" disabled={read.isFetching} onClick={() => void read.refetch()} data-testid="explorer-validate-list-again" />
        </div>
        {read.isPending && <TaskProgress label="Reading the records from OSDU and checking them" testId="explorer-validate-list-progress" />}
        {read.isError && <ExplorerProblem error={read.error} />}
        {answer && (
          <div className="flex min-h-0 flex-1 flex-col gap-3 overflow-y-auto pr-1">
            <div className="flex flex-wrap items-center gap-x-4 gap-y-1" data-testid="explorer-validate-list-counts">
              <span className="text-[12px]">
                <span className="font-mono tabular-nums">{answer.read.toLocaleString("en-US")}</span>
                <span className="text-muted-foreground">{` of ${counted(answer.matched, "record")} checked`}</span>
              </span>
              <ValidationCount outcome="valid" count={answer.valid} />
              <ValidationCount outcome="invalid" count={answer.invalid} />
              <ValidationCount outcome="unverified" count={answer.unverified} />
              {answer.notChecked > 0 && <ValidationCount outcome="notValidated" count={answer.notChecked} />}
              {answer.mapping != null && (
                <span className={(answer.failingAssertions ?? 0) === 0 ? "text-[12px] opacity-60" : "text-[12px]"} data-testid="explorer-validate-list-asserted">
                  <span className="font-mono tabular-nums">{(answer.failingAssertions ?? 0).toLocaleString("en-US")}</span>
                  <span className="text-muted-foreground">{` of ${counted(answer.asserted ?? 0, "record")} failing an assertion of `}</span>
                  <span className="font-mono text-[11px]">{answer.mapping}</span>
                </span>
              )}
            </div>
            {answer.cut && (
              <p className="text-[12px] text-muted-foreground" data-testid="explorer-validate-list-cut">
                The search finds more records than a check reads, so these counts are of the first ones. Narrow the search, or check a whole kind with an assertion flow: its conforms test for the schema, its mapping test for a mapping's assertions.
              </p>
            )}
            {[...answer.notes, ...answer.unavailable.map((u) => `${counted(u.records, "record")} of ${u.kind} not checked: ${u.why}`), ...(answer.notFound.length > 0 ? [`${counted(answer.notFound.length, "record")} the search found, storage did not return.`] : [])].map((note) => (
              <p key={note} className="text-[12px] text-muted-foreground" data-testid="explorer-validate-list-note">{note}</p>
            ))}
            {answer.rules.length > 0 && (
              <section className="rounded-md border" data-testid="explorer-validate-list-rules">
                <header className="border-b px-3 py-1.5 text-[12px] font-medium">Rules broken most often</header>
                <div className="grid grid-cols-[minmax(0,1.4fr)_auto_auto_minmax(0,2.4fr)]">
                  {answer.rules.map((rule) => (
                    <div key={`${rule.at}:${rule.rule}`} className="col-span-full grid min-w-0 grid-cols-subgrid items-start gap-3 border-b px-3 py-1.5 text-[12px] last:border-b-0" data-testid="explorer-validate-list-rule">
                      <span className="min-w-0 truncate font-mono text-[11px]" title={rule.at || "the record"}>{rule.at || "the record"}</span>
                      <span className="font-mono text-[11px] text-muted-foreground">{rule.rule}</span>
                      <span className="whitespace-nowrap text-right font-mono tabular-nums" title={`${rule.problems.toLocaleString("en-US")} problem(s) in all`}>{counted(rule.records, "record")}</span>
                      <span className="flex min-w-0 flex-col gap-0.5">
                        <span>
                          <span className="break-words">{rule.exampleMessage}</span>
                          <button type="button" className="ml-2 text-primary hover:underline" onClick={() => onOpenRecord(rule.exampleId)} title={rule.exampleId} data-testid="explorer-validate-list-example">
                            {idParts(rule.exampleId).unique}
                          </button>
                        </span>
                        {rule.expected !== null && (
                          <span className="text-muted-foreground" data-testid="explorer-validate-list-expected">
                            {"Takes "}
                            <span className="text-foreground">{rule.expected}</span>
                          </span>
                        )}
                        {rule.advice !== null && (
                          <span data-testid="explorer-validate-list-advice">
                            <span className="font-medium">{"Fix: "}</span>
                            {rule.advice}
                          </span>
                        )}
                      </span>
                    </div>
                  ))}
                </div>
              </section>
            )}
            {(answer.assertions ?? []).length > 0 && (
              <section className="rounded-md border" data-testid="explorer-validate-list-assertions">
                <header className="border-b px-3 py-1.5 text-[12px] font-medium">Assertions failed most often</header>
                <div className="grid grid-cols-[minmax(0,1.4fr)_auto_auto_minmax(0,2.4fr)]">
                  {(answer.assertions ?? []).map((failed) => (
                    <div key={`${failed.at}:${failed.assertion}`} className="col-span-full grid min-w-0 grid-cols-subgrid items-start gap-3 border-b px-3 py-1.5 text-[12px] last:border-b-0" data-testid="explorer-validate-list-assertion">
                      <span className="min-w-0 truncate font-mono text-[11px]" title={failed.at}>{failed.at}</span>
                      <span className="flex min-w-0 flex-wrap items-center gap-1">
                        <span className="max-w-[220px] truncate font-mono text-[11px] text-muted-foreground" title={failed.assertion}>{failed.assertion}</span>
                        <AssertionActionBadge action={failed.onFail} className="text-[11px]" />
                      </span>
                      <span className="whitespace-nowrap text-right font-mono tabular-nums" title={`${failed.failures.toLocaleString("en-US")} failure(s) in all`}>{counted(failed.records, "record")}</span>
                      <span className="min-w-0">
                        <span className="break-words">{failed.exampleMessage}</span>
                        <button type="button" className="ml-2 text-primary hover:underline" onClick={() => onOpenRecord(failed.exampleId)} title={`${failed.exampleId} at ${failed.examplePath}`} data-testid="explorer-validate-list-assertion-example">
                          {idParts(failed.exampleId).unique}
                        </button>
                      </span>
                    </div>
                  ))}
                </div>
              </section>
            )}
            <section className="rounded-md border" data-testid="explorer-validate-list-records">
              <header className="border-b px-3 py-1.5 text-[12px] font-medium">Records</header>
              {answer.records.map((record) => (
                <button
                  key={record.id}
                  type="button"
                  onClick={() => onOpenRecord(record.id)}
                  className="grid w-full min-w-0 cursor-pointer grid-cols-[auto_minmax(0,1.2fr)_minmax(0,0.8fr)_minmax(0,2.4fr)] items-center gap-3 border-b px-3 py-1 text-left text-[12px] last:border-b-0 hover:bg-accent/50"
                  title={record.id}
                  data-testid="explorer-validate-list-record"
                >
                  <ValidationGlyph outcome={record.outcome} />
                  <span className="min-w-0 truncate text-primary">{idParts(record.id).unique}</span>
                  <span className="min-w-0 truncate text-muted-foreground">{record.kind === null ? "" : kindParts(record.kind).type}</span>
                  <span className="min-w-0 truncate text-muted-foreground">
                    {(record.assertionFailures ?? 0) > 0 && (
                      <span className="text-foreground" data-testid="explorer-validate-list-record-asserted">
                        {`${(record.assertionFailures ?? 0).toLocaleString("en-US")} assertion failure${record.assertionFailures === 1 ? "" : "s"}`}
                        {record.first != null && "; "}
                      </span>
                    )}
                    {record.first ?? ""}
                  </span>
                </button>
              ))}
            </section>
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}
