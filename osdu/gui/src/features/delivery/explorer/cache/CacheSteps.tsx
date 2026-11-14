import { useId } from "react";
import { Columns3, Tag, TableProperties, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { CopyButton } from "@/components/CopyButton";
import type { CacheTypeDraft, DimensionDraftIssue } from "../../../../api/explorer";
import { RecordsStep } from "../dimension/BuilderFrom";
import { chainWords, kindTypeName, type StepState } from "../dimension/dimensionStory";
import { IssueLine, NoteLine, PickGlyph, PickName, QuietInput, StoryFrame } from "../dimension/StoryFrame";
import {
  MAX_CACHE_ATTRIBUTES, cacheAttributeOfTarget, cacheStepOfTarget, fieldName, lookupExample, pathName, typeNameOf, type CacheStep,
} from "./cacheStory";
import type { CacheBuild } from "./useCacheBuild";

/** What every step of the story is given: the build, where each step stands, which step is open, and the findings by step. */
interface StepProps {
  build: CacheBuild;
  draft: CacheTypeDraft;
  states: Record<CacheStep, StepState>;
  open: CacheStep | null;
  onOpen: (step: CacheStep | null) => void;
  issues: DimensionDraftIssue[];
}

const issuesAt = (issues: DimensionDraftIssue[], step: CacheStep) => issues.filter((issue) => cacheStepOfTarget(issue.target) === step);
const toggle = (props: StepProps, step: CacheStep) => () => props.onOpen(props.open === step ? null : step);

/**
 * The steps a person picks a cached type by (osdu/docs/reference/concepts/explorer-cache-builder.md), read as one
 * sentence: from these records, it keeps these values, with these read through the records they link to, read so, and
 * makes this type, which a mapping looks records up in.
 */
export function CacheSteps(props: StepProps & { startKind: string; onPreview: () => void }) {
  const { build, draft, states, startKind } = props;
  return (
    <>
      <RecordsStep
        state={states.from}
        kind={draft.kind}
        query={draft.query}
        total={undefined}
        recordKind={build.recordKind}
        issues={issuesAt(props.issues, "from")}
        open={props.open === "from"}
        onToggle={toggle(props, "from")}
        onKind={(kind) => build.update({ ...draft, kind })}
        onQuery={(query) => build.update({ ...draft, query })}
        startKind={startKind}
        onStartElsewhere={build.start}
        hint="Pick a type on the left, or open a record: a cached type holds the records of one type."
        queryHelp="Lucene, as the search service reads it. Left empty, every record of the type is cached."
      />
      <KeepsStep {...props} />
      <WithStep {...props} />
      <ReadStep {...props} />
      <MakesStep {...props} />
    </>
  );
}

/** What the cache keeps of each record: the values picked in a record of the type, each under a name. */
function KeepsStep(props: StepProps) {
  const { build, draft, states } = props;
  const kindType = kindTypeName(draft.kind);
  const fields = draft.fields;
  const rename = (index: number, as: string) =>
    build.update({ ...draft, fields: fields.map((field, i) => (i === index ? { ...field, as: as.trim() === "" ? null : as } : field)) });
  const guide = (
    <>
      {build.inView === null ? <>Open a {kindType} record on the left, then click </> : <>Click </>}
      <PickGlyph /> beside a value of it: <PickName>Keep in the cache</PickName>.
    </>
  );

  return (
    <StoryFrame
      step="keeps"
      state={states.keeps}
      lead="Keeps"
      answer={fields.length === 0
        ? <span className="font-normal text-muted-foreground">{draft.attributes.length > 0 ? "each record's id" : "nothing of each record yet"}</span>
        : <span className="font-mono" data-testid="builder-keeps">{fields.map(fieldName).join(", ")}</span>}
      facts={fields.length === 0 ? null : `${fields.length} ${fields.length === 1 ? "value" : "values"} of each record, beside its id`}
      issues={issuesAt(props.issues, "keeps")}
      open={props.open === "keeps"}
      onToggle={states.keeps === "waiting" ? undefined : toggle(props, "keeps")}
      hint={states.keeps === "next" && (
        <>
          Pick what the cache keeps of each {kindType} record: a value a mapping matches a source value against, such as a name or a code, or one it writes.{" "}
          {guide}
        </>
      )}
    >
      {fields.length > 0 && (
        <ul className="flex flex-col">
          {fields.map((field, index) => (
            <li key={`${index}-${field.path}`} className="flex min-w-0 items-center gap-2 border-b border-border/60 py-1 last:border-b-0" data-testid="builder-field-row">
              <Tag className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
              <span className="flex min-w-0 flex-1 flex-col">
                <QuietInput
                  value={field.as ?? ""}
                  placeholder={pathName(field.path)}
                  onChange={(event) => rename(index, event.target.value)}
                  className="-mx-1.5 h-7 w-[calc(100%+0.75rem)] px-1.5 font-mono text-[12.5px] md:text-[12.5px]"
                  aria-label={`The name ${field.path} is kept under`}
                  data-testid="builder-field-name"
                />
                <span className="truncate font-mono text-[11.5px] text-muted-foreground" title={field.path}>{field.path}</span>
              </span>
              <Button
                variant="ghost"
                size="icon-xs"
                onClick={() => build.update({ ...draft, fields: fields.filter((_, i) => i !== index) })}
                aria-label={`Stop keeping ${field.path}`}
                data-testid="builder-field-remove"
              >
                <Trash2 />
              </Button>
            </li>
          ))}
        </ul>
      )}
      {states.keeps !== "next" && <p className="text-[12px] text-muted-foreground">Keep another value: {guide} A value keeps whatever its path yields, a list or an object whole.</p>}
    </StoryFrame>
  );
}

/** The attributes: values read through the records a cached record links to, each kept on the cached record under a name. */
function WithStep(props: StepProps) {
  const { build, draft, states } = props;
  const kindType = kindTypeName(draft.kind);
  const attributes = draft.attributes;
  const named = issuesAt(props.issues, "with");
  const rename = (index: number, name: string) =>
    build.update({ ...draft, attributes: attributes.map((attribute, i) => (i === index ? { ...attribute, name } : attribute)) });

  return (
    <StoryFrame
      step="with"
      state={states.with}
      lead="With"
      answer={attributes.length === 0
        ? <span className="font-normal text-muted-foreground">nothing read through other records</span>
        : <span className="font-mono" data-testid="builder-with">{attributes.map((a) => a.name).join(", ")}</span>}
      facts={attributes.length === 0 ? null : `${attributes.length} ${attributes.length === 1 ? "attribute" : "attributes"} read through the records it links to`}
      issues={named.filter((issue) => cacheAttributeOfTarget(issue.target) === null)}
      shownInBody={named.filter((issue) => cacheAttributeOfTarget(issue.target) !== null)}
      open={props.open === "with"}
      onToggle={states.with === "waiting" ? undefined : toggle(props, "with")}
    >
      {attributes.length > 0 && (
        <ul className="flex flex-col">
          {attributes.map((attribute, index) => (
            <li key={`${index}-${attribute.steps.join("|")}`} className="border-b border-border/60 py-1 last:border-b-0" data-testid="builder-attribute">
              <div className="flex min-w-0 items-center gap-2">
                <Columns3 className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
                <span className="flex min-w-0 flex-1 flex-col">
                  <QuietInput
                    value={attribute.name}
                    onChange={(event) => rename(index, event.target.value)}
                    className="-mx-1.5 h-7 w-[calc(100%+0.75rem)] px-1.5 font-mono text-[12.5px] md:text-[12.5px]"
                    aria-label="The attribute's name"
                    data-testid="builder-attribute-name"
                  />
                  <span className="truncate text-[12px] text-muted-foreground" title={attribute.steps.join("\n")}>
                    {attribute.steps.length === 1 ? `${attribute.steps[0]} of the record itself` : `read through ${chainWords(attribute.steps)}`}
                  </span>
                </span>
                <Button
                  variant="ghost"
                  size="icon-xs"
                  onClick={() => build.update({ ...draft, attributes: attributes.filter((_, i) => i !== index) })}
                  aria-label={`Remove ${attribute.name}`}
                  data-testid="builder-attribute-remove"
                >
                  <Trash2 />
                </Button>
              </div>
              {named.filter((issue) => cacheAttributeOfTarget(issue.target) === attribute.name).map((issue, at) => <IssueLine key={`${at}-${issue.message}`} issue={issue} />)}
            </li>
          ))}
        </ul>
      )}
      {attributes.length >= MAX_CACHE_ATTRIBUTES
        ? <span className="text-[12px] text-muted-foreground">{`A cached type reads at most ${MAX_CACHE_ATTRIBUTES} attributes.`}</span>
        : (
          <p className="text-[12px] text-muted-foreground">
            A value of another record, kept on each {kindType} record: its well's name, the field it lies in. Open the record a value of a {kindType} record
            names (its link), and click <PickGlyph /> beside a value there: <PickName>Add as an attribute</PickName>. A mapping then finds a {kindType} by it,
            and nothing is read from OSDU while a record renders.
          </p>
        )}
    </StoryFrame>
  );
}

/** How the records are read and what a change to them does: from the index or whole from storage, updating at once or after approval. */
function ReadStep(props: StepProps) {
  const { build, draft, states } = props;
  const ids = useId();
  const answer = [draft.fetchRecords ? "whole from storage" : "from the search index", draft.onChange === "approve" ? "changes wait for approval" : null]
    .filter((part) => part !== null)
    .join(", ");

  return (
    <StoryFrame
      step="read"
      state={states.read}
      lead="Read"
      answer={<span data-testid="builder-read">{answer}</span>}
      issues={issuesAt(props.issues, "read")}
      open={props.open === "read"}
      onToggle={states.read === "waiting" ? undefined : toggle(props, "read")}
    >
      <div className="flex items-start gap-2.5">
        <Switch
          id={`${ids}-whole`}
          checked={draft.fetchRecords}
          onCheckedChange={(fetchRecords) => build.update({ ...draft, fetchRecords })}
          className="mt-0.5"
          data-testid="builder-fetch-records"
        />
        <Label htmlFor={`${ids}-whole`} className="flex flex-col items-start gap-0.5 text-[12.5px] font-normal">
          Read each record whole from storage
          <span className="text-[12px] text-muted-foreground">
            The search index holds only what the kind's schema describes. Turn this on to keep a value it leaves out, such as one under
            data.ExtensionProperties: a refresh then reads every record back from the storage service, which takes longer.
          </span>
        </Label>
      </div>
      <div className="flex items-start gap-2.5">
        <Switch
          id={`${ids}-approve`}
          checked={draft.onChange === "approve"}
          onCheckedChange={(approve) => build.update({ ...draft, onChange: approve ? "approve" : null })}
          className="mt-0.5"
          data-testid="builder-on-change"
        />
        <Label htmlFor={`${ids}-approve`} className="flex flex-col items-start gap-0.5 text-[12.5px] font-normal">
          Changes wait for approval
          <span className="text-[12px] text-muted-foreground">
            A changed value here means delivered records no longer match it. Off, their next run carries the update; on, they are held back until
            someone approves the change on the Cache page.
          </span>
        </Label>
      </div>
    </StoryFrame>
  );
}

/** The type the draft makes: the name a mapping reads it by, the flows that cache it already, and the lookup that reads it. */
function MakesStep(props: StepProps & { onPreview: () => void }) {
  const { build, draft, states, onPreview } = props;
  const composed = build.compose.data;
  const name = composed?.type?.name ?? typeNameOf(draft);
  const values = composed?.type?.fields ?? [];
  const first = values[0]?.name ?? (draft.fields[0] !== undefined ? fieldName(draft.fields[0]) : draft.attributes[0]?.name ?? null);
  const lookup = lookupExample(name, first);
  const declaredBy = composed?.declaredBy ?? [];

  return (
    <StoryFrame
      step="makes"
      state={states.makes}
      lead="Makes"
      answer={name === ""
        ? <span className="font-normal text-muted-foreground">a type of the partition's cache</span>
        : <span className="font-mono" data-testid="builder-makes">{`$cache.${name}`}</span>}
      facts={states.makes === "waiting"
        ? null
        : `one entry for each record, found by its id${values.length > 0 ? ` or by ${values.length === 1 ? "its value" : `one of its ${values.length} values`}` : ""}`}
      issues={issuesAt(props.issues, "makes")}
      last
      body={states.makes !== "waiting" && (
        <div className="mt-2.5 flex flex-col gap-2.5">
          {declaredBy.length > 0 && (
            <NoteLine testId="builder-declared-by">
              {`${name} is cached already by ${declaredBy.join(", ")}. A partition's cache keeps one type of a name, filled by every flow declaring it, their values added up: this item can go in that flow, or in another flow filling the same partition.`}
            </NoteLine>
          )}
          {lookup !== null && (
            <div className="rounded-md border border-border bg-muted/30" data-testid="builder-lookup">
              <div className="flex items-center justify-between gap-2 border-b border-border/60 px-2.5 py-1">
                <span className="text-[12px] text-muted-foreground">A mapping finds a record of it so</span>
                <CopyButton iconOnly label="Copy the lookup" text={lookup} testId="builder-lookup-copy" />
              </div>
              <pre className="overflow-x-auto px-2.5 py-1.5 font-mono text-[12px] leading-5">{lookup}</pre>
            </div>
          )}
          <div>
            <Button variant="outline" size="sm" onClick={onPreview} disabled={composed === undefined || !composed.valid} data-testid="builder-open-preview">
              <TableProperties />
              Preview its records
            </Button>
          </div>
        </div>
      )}
    />
  );
}
