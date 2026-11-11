import { useId, useState } from "react";
import { ArrowLeft, Check, Columns3, Layers, Loader2, Plus, RotateCcw, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { CopyButton } from "@/components/CopyButton";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import type { DimensionDraft, DimensionDraftIssue, DimensionKeyCandidate } from "../../../../api/explorer";
import { failureText } from "../../answers";
import { applyPlan, firstValueAt, MAX_ATTRIBUTES, type PickPlan } from "./dimensionDraft";
import {
  PLAIN_STEPS, STEP_HELP, chainWords, cleanWords, kindTypeName, lastProperty, shortId, stepOfTarget, typeName, type StepState, type StoryStep,
} from "./dimensionStory";
import { NoteLine, PickGlyph, PickName, StoryFrame } from "./StoryFrame";
import type { DimensionBuild } from "./useDimensionBuild";

/** The suggested keys listed before a field to narrow them is offered. */
const LISTED_BEFORE_FILTER = 6;

/** What every step of the story is given: the build, where each step stands, which step is open, and the findings by step. */
interface StepProps {
  build: DimensionBuild;
  draft: DimensionDraft;
  states: Record<StoryStep, StepState>;
  open: StoryStep | null;
  onOpen: (step: StoryStep | null) => void;
  issues: DimensionDraftIssue[];
}

const issuesAt = (issues: DimensionDraftIssue[], step: StoryStep) => issues.filter((issue) => stepOfTarget(issue.target) === step);
const toggle = (props: StepProps, step: StoryStep) => () => props.onOpen(props.open === step ? null : step);

/**
 * The steps a person picks a dimension by (osdu/docs/reference/concepts/explorer-dimension-builder.md), read as one
 * sentence: from these records, one row per this key, named by this, with these columns, cleaned so. The table it
 * makes ends the story ({@link BuilderRow}).
 */
export function BuilderSteps(props: StepProps & { startKind: string }) {
  return (
    <>
      <FromStep {...props} />
      <RowsStep {...props} />
      <ShownStep {...props} />
      <WithStep {...props} />
      <CleanedStep {...props} />
    </>
  );
}

/** The records the dimension reads: their kind, every version or one, and the query narrowing them. */
function FromStep(props: StepProps & { startKind: string }) {
  const { build, draft, states, startKind } = props;
  const ids = useId();
  const segments = draft.kind.split(":");
  const everyVersion = segments.length === 4 && segments[3] === "*";
  const total = build.keys.data?.answer.total;
  const facts = draft.kind === ""
    ? null
    : [
      segments.length === 4 ? (everyVersion ? "every version" : `version ${segments[3]}`) : null,
      total === undefined ? null : `${total.toLocaleString("en-US")} ${total === 1 ? "record" : "records"}`,
      draft.query === null ? null : "narrowed by a query",
    ].filter((fact) => fact !== null).join(" · ");
  const elsewhere = startKind !== "" && startKind !== draft.kind ? startKind : null;

  return (
    <StoryFrame
      step="from"
      state={states.from}
      lead="From"
      answer={draft.kind === ""
        ? <span className="font-normal text-muted-foreground">the records of one type</span>
        : <><span data-testid="builder-kind">{kindTypeName(draft.kind)}</span> records</>}
      facts={facts}
      issues={issuesAt(props.issues, "from")}
      open={props.open === "from"}
      onToggle={draft.kind === "" ? undefined : toggle(props, "from")}
      hint={states.from === "next" ? "Pick a type on the left, or open a record: a dimension reads the records of one type." : undefined}
    >
      <div className="flex min-w-0 items-center gap-1">
        <span className="min-w-0 truncate font-mono text-[12px]" title={draft.kind}>{draft.kind}</span>
        <CopyButton iconOnly label="Copy the kind" text={draft.kind} testId="builder-kind-copy" />
      </div>
      {segments.length === 4 && (
        <div className="flex items-start gap-2.5">
          <Switch
            id={`${ids}-versions`}
            checked={everyVersion}
            onCheckedChange={(checked) => {
              if (checked) {
                build.update({ ...draft, kind: [...segments.slice(0, 3), "*"].join(":") });
              } else if (build.recordKind !== null) {
                build.update({ ...draft, kind: build.recordKind });
              }
            }}
            disabled={everyVersion && build.recordKind === null}
            className="mt-0.5"
            data-testid="builder-every-version"
          />
          <Label htmlFor={`${ids}-versions`} className="flex flex-col items-start gap-0.5 text-[12.5px] font-normal">
            Every version of the type
            <span className="text-[12px] text-muted-foreground">
              {everyVersion
                ? build.recordKind === null ? "Open a record to read its version alone." : `Off, only ${build.recordKind.split(":")[3]}, the open record's version.`
                : `Only ${segments[3]} is read.`}
            </span>
          </Label>
        </div>
      )}
      <div className="flex flex-col gap-1">
        <Label htmlFor={`${ids}-query`} className="text-[12.5px] font-normal">Narrow the records</Label>
        <Input
          id={`${ids}-query`}
          value={draft.query ?? ""}
          onChange={(event) => build.update({ ...draft, query: event.target.value === "" ? null : event.target.value })}
          className="h-8 font-mono text-[12px] md:text-[12px]"
          placeholder={'a search query, such as data.Source:"CPI"'}
          data-testid="builder-query"
        />
        <span className="text-[12px] text-muted-foreground">Lucene, as the search service reads it; {"{partition}"} is the partition a build runs in.</span>
      </div>
      {elsewhere !== null && (
        <div>
          <Button variant="outline" size="sm" onClick={() => build.start(elsewhere)} data-testid="builder-start-elsewhere">
            <RotateCcw />
            {`Start over from ${kindTypeName(elsewhere)} records`}
          </Button>
        </div>
      )}
    </StoryFrame>
  );
}

/** The key: what each row stands for, picked among the template's suggestions or on a value of a record of the kind. */
function RowsStep(props: StepProps) {
  const { build, draft, states } = props;
  const ids = useId();
  const keyed = draft.path !== null && draft.path !== "";
  const kindType = kindTypeName(draft.kind);
  const suggestions = build.candidates.data;
  const answer = build.keys.data?.answer;
  const keyCount = answer === undefined ? null : `${answer.keys.length.toLocaleString("en-US")}${answer.moreKeys ? " or more" : ""} ${answer.keys.length === 1 && !answer.moreKeys ? "key" : "keys"}`;
  const notes = [
    ...(suggestions?.missing ? [suggestions.missing] : []),
    ...(answer?.notes ?? []),
    ...(answer?.refusal ? [`The search service refused the keys: ${answer.refusal}`] : []),
    ...(build.keys.isError ? [`The keys could not be read: ${failureText(build.keys.error)}`] : []),
    ...(build.candidates.isError ? [`The template's keys could not be read: ${failureText(build.candidates.error)}`] : []),
  ];
  const makeKey = (candidate: DimensionKeyCandidate) => {
    const plan: PickPlan = { action: "key", kind: draft.kind, key: candidate.path, keyExample: null, path: candidate.path, steps: [], questions: [] };
    build.update(applyPlan(draft, plan, [], [], candidate.names));
  };

  return (
    <StoryFrame
      step="rows"
      state={states.rows}
      lead="One row per"
      answer={keyed
        ? <span data-testid="builder-rows">{build.keyType ?? lastProperty(draft.path!)}</span>
        : <span className="font-normal text-muted-foreground">value the records hold</span>}
      facts={keyed && (
        <>
          <span className="font-mono" data-testid="builder-key-path">{draft.path}</span>
          {build.keys.isFetching ? <Loader2 className="ml-1.5 inline size-3.5 animate-spin" aria-label="counting the keys" /> : keyCount !== null && <span>{` · ${keyCount}`}</span>}
        </>
      )}
      issues={issuesAt(props.issues, "rows")}
      open={props.open === "rows"}
      onToggle={draft.kind === "" ? undefined : toggle(props, "rows")}
      hint={states.rows === "next" && (
        <>
          Pick what each row stands for{suggestions !== undefined && suggestions.keys.length > 0 ? ": one of the values below, which name other records, or" : ":"} any value of a {kindType} record,
          clicking <PickGlyph /> beside it, then <PickName>One row for each value</PickName>.
        </>
      )}
    >
      {suggestions !== undefined && suggestions.keys.length > 0 && (
        <KeyChoices candidates={suggestions.keys} picked={draft.path} inView={build.inView} onPick={makeKey} />
      )}
      {(suggestions === undefined || suggestions.keys.length === 0) && build.candidates.isFetching && (
        <span className="text-[12px] text-muted-foreground"><Loader2 className="mr-1.5 inline size-3.5 animate-spin" />Reading the template's suggestions</span>
      )}
      {states.rows !== "next" && (
        <p className="text-[12px] text-muted-foreground">
          Any value of a {kindType} record can be the key: click <PickGlyph /> beside it, then <PickName>One row for each value</PickName>.
        </p>
      )}
      <div className="flex flex-col gap-2.5 border-t border-border/60 pt-3">
        <div className="flex items-start gap-2.5">
          <Switch
            id={`${ids}-count`}
            checked={draft.countRecords}
            onCheckedChange={(countRecords) => build.update({ ...draft, countRecords })}
            className="mt-0.5"
            data-testid="builder-count"
          />
          <Label htmlFor={`${ids}-count`} className="flex flex-col items-start gap-0.5 text-[12.5px] font-normal">
            Count each key's records exactly
            <span className="text-[12px] text-muted-foreground">Where a record holds several keys, the search's count can be off; this counts them one by one.</span>
          </Label>
        </div>
        <div className="flex items-center gap-2">
          <Label htmlFor={`${ids}-max`} className="text-[12.5px] font-normal">At most</Label>
          <Input
            id={`${ids}-max`}
            type="number"
            min={1}
            value={draft.maxValues ?? ""}
            onChange={(event) => {
              const value = Number.parseInt(event.target.value, 10);
              build.update({ ...draft, maxValues: Number.isSafeInteger(value) ? value : null });
            }}
            className="h-8 w-32 font-mono text-[12px] md:text-[12px]"
            placeholder="1000000"
            data-testid="builder-max"
          />
          <span className="text-[12.5px]">keys</span>
        </div>
      </div>
      {notes.map((note, index) => <NoteLine key={`${index}-${note}`}>{note}</NoteLine>)}
    </StoryFrame>
  );
}

/** The keys the kind's template suggests, the likeliest first, each with what the record in view holds there; one is picked. */
function KeyChoices({ candidates, picked, inView, onPick }: {
  candidates: DimensionKeyCandidate[];
  picked: string | null;
  inView: Record<string, unknown> | null;
  onPick: (candidate: DimensionKeyCandidate) => void;
}) {
  const [filter, setFilter] = useState("");
  const wanted = filter.trim().toLowerCase();
  const shown = wanted === ""
    ? candidates
    : candidates.filter((candidate) => candidate.path.toLowerCase().includes(wanted) || candidate.names.some((name) => name.toLowerCase().includes(wanted)));

  return (
    <div className="flex flex-col gap-1.5" data-testid="builder-suggestions">
      {candidates.length > LISTED_BEFORE_FILTER && (
        <Input
          value={filter}
          onChange={(event) => setFilter(event.target.value)}
          placeholder={`Find among ${candidates.length} suggested keys`}
          className="h-8 text-[12.5px] md:text-[12.5px]"
          aria-label="Find a suggested key"
          data-testid="builder-suggestions-find"
        />
      )}
      <div className="flex max-h-72 flex-col overflow-y-auto" role="radiogroup" aria-label="The key">
        {shown.map((candidate) => {
          const named = candidate.names.find((name) => name.includes("--"));
          const held = firstValueAt(inView, candidate.path);
          const isPicked = picked === candidate.path;
          return (
            <RichTooltip
              key={candidate.path}
              title={candidate.path}
              body={`Names ${candidate.names.map((name) => (name.includes("--") ? typeName(name) : name)).join(" or ")} records${candidate.repeated ? ", several to a record" : ""}.${candidate.description ? ` ${candidate.description}` : ""}`}
              side="left"
            >
              <button
                type="button"
                role="radio"
                aria-checked={isPicked}
                aria-pressed={isPicked}
                onClick={() => onPick(candidate)}
                className={cn("flex min-w-0 items-center gap-2.5 rounded-md px-2 py-1.5 text-left hover:bg-accent/60", isPicked && "bg-accent/60")}
                data-testid="builder-suggestion"
                data-path={candidate.path}
              >
                <span
                  className={cn("size-3.5 shrink-0 rounded-full border bg-card", isPicked ? "border-[4px] border-primary" : "border-input")}
                  aria-hidden
                />
                <span className="flex min-w-0 flex-1 flex-col">
                  <span className="truncate text-[13px] font-medium">{named === undefined ? lastProperty(candidate.path) : typeName(named)}</span>
                  <span className="truncate font-mono text-[11.5px] text-muted-foreground">
                    {candidate.path}
                    {candidate.repeated ? <span className="font-sans">{" · several to a record"}</span> : null}
                  </span>
                </span>
                {held !== null && <span className="max-w-[38%] shrink-0 truncate font-mono text-[11.5px] text-muted-foreground" title={held}>{shortId(held)}</span>}
              </button>
            </RichTooltip>
          );
        })}
        {shown.length === 0 && <span className="px-2 py-1.5 text-[12px] text-muted-foreground">No suggested key holds that.</span>}
      </div>
    </div>
  );
}

/** Opens the record the example key names on the left, for its values to be picked. */
function OpenKeyRecord({ build }: { build: DimensionBuild }) {
  if (build.keyType === null) {
    return null;
  }

  const example = build.exampleKey;
  return (
    <RichTooltip body={example === null ? "Waiting for the keys to be read." : `Opens ${example} on the left, after a ${kindTypeName(build.draft?.kind ?? "")} record that holds it.`} mono={example !== null}>
      <span className="inline-flex">
        <Button
          variant="outline"
          size="sm"
          onClick={build.openKeyRecord}
          disabled={example === null || build.openingKeyRecord}
          data-testid="builder-open-key-record"
        >
          {build.openingKeyRecord ? <Loader2 className="animate-spin" /> : <ArrowLeft />}
          {`Open the ${build.keyType} record`}
        </Button>
      </span>
    </RichTooltip>
  );
}

/**
 * What each row is named by: a value read in the record the key names, or the key's own code when none is picked, and the
 * name a row takes where its record holds none. The name is what the dimension's filters and views show, and rows whose
 * names clean alike are one value; the columns beside it are carried as they are read.
 */
function ShownStep(props: StepProps) {
  const { build, draft, states } = props;
  const ids = useId();
  const labelled = draft.label.length > 0;
  const reads = labelled || draft.attributes.some((a) => a.collect === null);
  const keyType = build.keyType;
  const facts = labelled
    ? [
      draft.label.length === 1 ? `read in the ${keyType ?? "named"} record` : `read through ${chainWords(draft.label.slice(0, -1))}`,
      draft.unlabelled === null ? null : `otherwise "${draft.unlabelled}"`,
    ].filter((fact) => fact !== null).join(" · ")
    : keyType !== null ? `the code its ${keyType}'s id ends with` : "the key's own text";
  const editable = labelled || reads || keyType !== null;

  return (
    <StoryFrame
      step="shown"
      state={states.shown}
      lead="Named by"
      answer={labelled
        ? <span className="font-mono" data-testid="builder-shown">{lastProperty(draft.label[draft.label.length - 1])}</span>
        : <span data-testid="builder-shown">{keyType !== null ? "the key's code" : "the key itself"}</span>}
      facts={states.shown === "waiting" ? null : facts}
      issues={issuesAt(props.issues, "shown")}
      open={props.open === "shown"}
      onToggle={states.shown === "waiting" || !editable ? undefined : toggle(props, "shown")}
      hint={states.shown === "next" && (
        <div className="flex flex-col items-start gap-2.5">
          <span>
            A row's name is what the dimension's filters and views show, and rows whose names clean alike are one value. Each row
            is named by its {keyType}'s code now; to name it by what the {keyType} holds, open the {keyType} and click <PickGlyph /> beside
            that value, then <PickName>Name each row by this</PickName>.
          </span>
          <OpenKeyRecord build={build} />
        </div>
      )}
    >
      {labelled && (
        <div className="flex flex-col gap-1.5">
          <ol className="flex flex-col gap-0.5 font-mono text-[12px]">
            {draft.label.map((step, index) => (
              <li key={index} className="flex min-w-0 items-baseline gap-2">
                <span className="w-4 shrink-0 text-right text-muted-foreground tabular-nums">{index + 1}</span>
                <span className="min-w-0 truncate" title={step}>{step}</span>
              </li>
            ))}
          </ol>
          <div className="flex flex-wrap items-center gap-2">
            <OpenKeyRecord build={build} />
            <Button variant="ghost" size="sm" onClick={() => build.update({ ...draft, label: [] })} data-testid="builder-shown-clear">
              <Trash2 />
              Name by the key's code
            </Button>
          </div>
        </div>
      )}
      {!labelled && keyType !== null && states.shown !== "next" && (
        <div className="flex flex-col items-start gap-2">
          <span className="text-[12px] text-muted-foreground">
            To name each row by what its {keyType} holds, open the {keyType} and click <PickGlyph /> beside that value, then <PickName>Name each row by this</PickName>.
          </span>
          <OpenKeyRecord build={build} />
        </div>
      )}
      {reads && (
        <div className="flex flex-col gap-1">
          <Label htmlFor={`${ids}-unlabelled`} className="text-[12.5px] font-normal">Where a key's record holds nothing there</Label>
          <Input
            id={`${ids}-unlabelled`}
            value={draft.unlabelled ?? ""}
            placeholder="the code its id ends with"
            onChange={(event) => build.update({ ...draft, unlabelled: event.target.value === "" ? null : event.target.value })}
            className="h-8 max-w-72 text-[12.5px] md:text-[12.5px]"
            data-testid="builder-unlabelled"
          />
          <span className="text-[12px] text-muted-foreground">The row's name, and every column read from its record, take this.</span>
        </div>
      )}
    </StoryFrame>
  );
}

/** The columns beside the name: read through the record a key names, or collected from the dimension's own records. */
function WithStep(props: StepProps) {
  const { build, draft, states } = props;
  const attributes = draft.attributes;
  const kindType = kindTypeName(draft.kind);
  const keyType = build.keyType;
  const remove = (index: number) => build.update({ ...draft, attributes: attributes.filter((_, i) => i !== index) });

  return (
    <StoryFrame
      step="with"
      state={states.with}
      lead="With"
      answer={attributes.length === 0
        ? <span className="font-normal text-muted-foreground">no more columns</span>
        : <span className="font-mono" data-testid="builder-with">{attributes.map((a) => a.name).join(", ")}</span>}
      facts={attributes.length === 0 ? null : `${attributes.length} ${attributes.length === 1 ? "column" : "columns"} to narrow by`}
      issues={issuesAt(props.issues, "with")}
      open={props.open === "with"}
      onToggle={states.with === "waiting" ? undefined : toggle(props, "with")}
    >
      {attributes.length > 0 && (
        <ul className="flex flex-col">
          {attributes.map((attribute, index) => {
            const collected = attribute.collect !== null;
            const Icon = collected ? Layers : Columns3;
            return (
              <li key={`${index}-${attribute.name}`} className="flex min-w-0 items-center gap-2 border-b border-border/60 py-1.5 last:border-b-0" data-testid="builder-attribute">
                <Icon className="size-3.5 shrink-0 text-muted-foreground" aria-label={collected ? "collected" : "read"} />
                <span className="flex min-w-0 flex-1 flex-col">
                  <span className="truncate font-mono text-[12.5px]">{attribute.name}</span>
                  <span className="truncate text-[12px] text-muted-foreground" title={collected ? attribute.collect! : (attribute.steps ?? []).join("\n")}>
                    {collected
                      ? `every value its ${kindType} records hold at ${attribute.collect}`
                      : (attribute.steps ?? []).length === 1
                        ? `${attribute.steps![0]} of the ${keyType ?? "named"} record`
                        : `read through ${chainWords(attribute.steps ?? [])}`}
                  </span>
                </span>
                <Button variant="ghost" size="icon-xs" onClick={() => remove(index)} aria-label={`Remove ${attribute.name}`} data-testid="builder-attribute-remove">
                  <Trash2 />
                </Button>
              </li>
            );
          })}
        </ul>
      )}
      {attributes.length >= MAX_ATTRIBUTES
        ? <span className="text-[12px] text-muted-foreground">{`A dimension reads at most ${MAX_ATTRIBUTES} columns besides its name.`}</span>
        : (
          <div className="flex flex-col items-start gap-2">
            <span className="text-[12px] text-muted-foreground">
              {keyType !== null && (
                <>Add a column from the {keyType}, or a record it names: click <PickGlyph /> beside the value, then <PickName>Add as a column</PickName>. </>
              )}
              Collect what a row's {kindType} records hold: click <PickGlyph /> beside a value of one, then <PickName>Collect into a column</PickName>.
            </span>
            <OpenKeyRecord build={build} />
          </div>
        )}
    </StoryFrame>
  );
}

/** How each row's name is cleaned before rows are grouped by it: the plain steps picked, and replacements, in order. */
function CleanedStep(props: StepProps) {
  const { build, draft, states } = props;
  const plain = new Set(draft.clean.filter((c) => c.step !== "replace").map((c) => c.step));
  const flip = (step: string) => build.update({
    ...draft,
    clean: plain.has(step) ? draft.clean.filter((c) => c.step !== step) : [...draft.clean, { step, pattern: null, with: null }],
  });
  const replaces = draft.clean.map((c, index) => ({ c, index })).filter(({ c }) => c.step === "replace");
  const setReplace = (index: number, change: { pattern?: string; with?: string }) =>
    build.update({ ...draft, clean: draft.clean.map((s, i) => (i === index ? { ...s, ...change } : s)) });

  return (
    <StoryFrame
      step="cleaned"
      state={states.cleaned}
      lead="Cleaned"
      answer={draft.clean.length === 0
        ? <span className="font-normal text-muted-foreground">as found</span>
        : <span data-testid="builder-cleaned">{draft.clean.map(cleanWords).join(", then ")}</span>}
      facts={draft.clean.length === 0 ? null : "names that clean alike are one value"}
      issues={issuesAt(props.issues, "cleaned")}
      open={props.open === "cleaned"}
      onToggle={states.cleaned === "waiting" ? undefined : toggle(props, "cleaned")}
    >
      <div className="flex flex-col" data-testid="builder-clean">
        {PLAIN_STEPS.map((step) => {
          const on = plain.has(step);
          return (
            <button
              key={step}
              type="button"
              onClick={() => flip(step)}
              aria-pressed={on}
              className="flex min-w-0 items-start gap-2.5 rounded-md px-2 py-1 text-left hover:bg-accent/60"
              data-testid={`builder-clean-${step}`}
            >
              <span
                className={cn(
                  "mt-0.5 flex size-3.5 shrink-0 items-center justify-center rounded-sm border",
                  on ? "border-primary bg-primary text-primary-foreground" : "border-input bg-card",
                )}
                aria-hidden
              >
                {on && <Check className="size-3" strokeWidth={3} />}
              </span>
              <span className="w-28 shrink-0 font-mono text-[12px]">{step}</span>
              <span className="min-w-0 text-[12px] text-muted-foreground">{STEP_HELP[step]}</span>
            </button>
          );
        })}
      </div>
      {replaces.map(({ c, index }) => (
        <div key={index} className="flex items-center gap-1.5" data-testid="builder-clean-replace-row">
          <Input
            value={c.pattern ?? ""}
            placeholder="pattern, a regular expression"
            onChange={(event) => setReplace(index, { pattern: event.target.value })}
            className="h-8 font-mono text-[12px] md:text-[12px]"
            aria-label="Pattern"
          />
          <Input
            value={c.with ?? ""}
            placeholder="replaced by"
            onChange={(event) => setReplace(index, { with: event.target.value })}
            className="h-8 font-mono text-[12px] md:text-[12px]"
            aria-label="Replacement"
          />
          <Button variant="ghost" size="icon-xs" aria-label="Remove the replacement" onClick={() => build.update({ ...draft, clean: draft.clean.filter((_, i) => i !== index) })}>
            <Trash2 />
          </Button>
        </div>
      ))}
      <div className="flex flex-wrap items-center gap-2">
        <Button
          variant="ghost"
          size="sm"
          onClick={() => build.update({ ...draft, clean: [...draft.clean, { step: "replace", pattern: "", with: "" }] })}
          data-testid="builder-clean-replace"
        >
          <Plus />
          Replace a pattern
        </Button>
        {draft.clean.length > 1 && <span className="text-[12px] text-muted-foreground">{`In the order picked: ${draft.clean.map((c) => c.step).join(", then ")}.`}</span>}
      </div>
    </StoryFrame>
  );
}
