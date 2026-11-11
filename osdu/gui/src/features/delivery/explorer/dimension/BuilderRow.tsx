import { useState } from "react";
import { ChevronLeft, ChevronRight, Loader2, Rows3, Workflow } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { ExplainMark } from "@/components/ExplainTip";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import type { DimensionDraft, DimensionDraftAttribute, DimensionDraftIssue } from "../../../../api/explorer";
import { ROLE_GLYPHS } from "../../dimensions/blueprintModel";
import { exampleCount, kindTypeName, lastProperty, stepOfTarget, type StepState } from "./dimensionStory";
import { IssueLine, NoteLine, QuietInput, StoryFrame } from "./StoryFrame";
import type { DimensionBuild } from "./useDimensionBuild";

/** The columns every dimension's table holds besides its key, its value and its attributes. */
const TABLE_COLUMNS = "Every dimension's table also holds id, partition, key_id, records (how many records hold the key) and filter (the query that finds them).";

/**
 * The end of the dimension's story (osdu/docs/reference/concepts/explorer-dimension-builder.md): the table a build of
 * it writes, and one of its rows as a build would make it, for the record in view's key or one of the commonest, stepped
 * through, with the way to the table's preview. Each column is named where it is shown, and what the builder found about
 * a column, or about the whole, is said there too.
 */
export function BuilderRow({ build, draft, state, issues, onShowRow, onPreview }: {
  build: DimensionBuild;
  draft: DimensionDraft;
  state: StepState;
  issues: DimensionDraftIssue[];
  /** Opens how a build makes each column, drawn as a flow's own dimension is. */
  onShowRow: () => void;
  /** Shows the table previewed: the rows of the keys most records hold. */
  onPreview: () => void;
}) {
  const compose = build.compose.data;
  const keyed = draft.path !== null && draft.path !== "";
  const table = compose?.table ?? null;
  const whole = issues.filter((issue) => stepOfTarget(issue.target) === null);

  return (
    <StoryFrame
      step="makes"
      state={state}
      lead="Makes"
      answer={keyed && table !== null
        ? (
          <span className="inline-flex max-w-full items-center gap-1 align-bottom">
            <span className="truncate font-mono" data-testid="builder-table-name">{table}</span>
            <ExplainMark title="The table" text={TABLE_COLUMNS} testId="builder-table-about" />
          </span>
        )
        : <span className="font-normal text-muted-foreground">a table, a row per key</span>}
      issues={whole}
      shownInBody={issues.filter((issue) => stepOfTarget(issue.target) === "makes")}
      body={keyed && <ExampleRow build={build} draft={draft} issues={issues} onShowRow={onShowRow} onPreview={onPreview} />}
      last
    />
  );
}

function ExampleRow({ build, draft, issues, onShowRow, onPreview }: {
  build: DimensionBuild;
  draft: DimensionDraft;
  issues: DimensionDraftIssue[];
  onShowRow: () => void;
  onPreview: () => void;
}) {
  const compose = build.compose.data;
  const example = compose?.example ?? null;
  const dimension = compose?.dimension ?? null;
  const count = build.examples.length;
  const own = build.exampleKey !== null && build.exampleKey === build.recordKey;
  const records = example?.records ?? null;
  const problemsOf = (target: string) => issues.filter((issue) => issue.target === target);
  const valuesOf = (attribute: DimensionDraftAttribute) => {
    const held = (example?.attributes ?? []).filter((a) => a.name === attribute.name);
    return held.length === 0
      ? null
      : held.map((a) => (a.records === null || attribute.collect === null ? a.value : `${a.value} (${a.records.toLocaleString("en-US")})`)).join(", ");
  };
  const setAttribute = (index: number, name: string) =>
    build.update({ ...draft, attributes: draft.attributes.map((a, i) => (i === index ? { ...a, name } : a)) });
  const keyName = dimension?.keyColumn ?? lastProperty(draft.path ?? "");
  const valueName = dimension?.valueColumn ?? (draft.label.length > 0 ? lastProperty(draft.label[draft.label.length - 1]) : draft.name || "value");

  return (
    <div className="mt-2.5 flex flex-col">
      <div className="overflow-hidden rounded-lg border border-border" data-testid="builder-example">
        <div className="flex min-w-0 items-center gap-2 border-b border-border/60 bg-muted/40 py-1 pl-3 pr-1">
          <span className="shrink-0 text-[12px] font-medium">Example row</span>
          {own && <span className="min-w-0 truncate text-[12px] text-muted-foreground">the open record's key</span>}
          {build.compose.isFetching && <Loader2 className="size-3.5 shrink-0 animate-spin text-muted-foreground" aria-label="making the row" />}
          <span className="ml-auto flex shrink-0 items-center">
            <Button variant="ghost" size="icon-xs" aria-label="The previous example" onClick={() => build.stepExample(-1)} disabled={count < 2} data-testid="builder-example-previous">
              <ChevronLeft />
            </Button>
            <RichTooltip body="Each row is made of one key: the open record's first, then those the most records hold.">
              <span className="min-w-12 text-center font-mono text-[12px] tabular-nums text-muted-foreground" data-testid="builder-example-count">
                {exampleCount(build.exampleAt, count)}
              </span>
            </RichTooltip>
            <Button variant="ghost" size="icon-xs" aria-label="The next example" onClick={() => build.stepExample(1)} disabled={count < 2} data-testid="builder-example-next">
              <ChevronRight />
            </Button>
          </span>
        </div>
        {compose === undefined
          ? (
            <div className="flex flex-col gap-2 px-3 py-2.5">
              <Skeleton className="h-4 w-4/5" />
              <Skeleton className="h-4 w-3/5" />
            </div>
          )
          : (
            <div className="flex flex-col py-1">
              <ColumnLine
                glyph="key"
                name={draft.keyColumn}
                shown={keyName}
                onName={(name) => build.update({ ...draft, keyColumn: name })}
                value={example?.key ?? null}
                fromEnd
                problems={problemsOf("columns.key")}
                testId="builder-column-key"
              />
              <ColumnLine
                glyph="label"
                name={draft.valueColumn}
                shown={valueName}
                onName={(name) => build.update({ ...draft, valueColumn: name })}
                value={example === null ? null : example.value}
                missing={example === null || example.value !== null ? null : `left out: ${example.leftOut ?? "no value"}`}
                problems={problemsOf("columns.value")}
                testId="builder-column-value"
              />
              {draft.attributes.map((attribute, index) => (
                <ColumnLine
                  key={index}
                  glyph={attribute.collect !== null ? "collect" : "attribute"}
                  name={attribute.name}
                  shown={attribute.name}
                  onName={(name) => setAttribute(index, name ?? "")}
                  value={valuesOf(attribute)}
                  problems={[]}
                  testId="builder-column-attribute"
                />
              ))}
            </div>
          )}
        <div className="flex min-w-0 items-center gap-2 border-t border-border/60 py-1 pl-3 pr-1.5 text-[12px] text-muted-foreground">
          <span className="min-w-0 truncate" data-testid="builder-example-records">
            {records === null
              ? ""
              : `${records.toLocaleString("en-US")} ${kindTypeName(draft.kind)} ${records === 1 ? "record holds" : "records hold"} this key`}
          </span>
          <Button variant="ghost" size="sm" className="ml-auto h-7 shrink-0 px-2 text-[12px]" onClick={onShowRow} disabled={compose?.blueprint == null} data-testid="builder-show-row">
            <Workflow />
            How it is built
          </Button>
          <RichTooltip body="The table's rows for the keys most records hold, read from OSDU as a build reads them.">
            <Button variant="ghost" size="sm" className="h-7 shrink-0 px-2 text-[12px]" onClick={onPreview} data-testid="builder-preview-open">
              <Rows3 />
              Preview the table
            </Button>
          </RichTooltip>
        </div>
      </div>
      {compose?.exampleProblem && <NoteLine>{`The example row could not be made: ${compose.exampleProblem}`}</NoteLine>}
      {(example?.notes ?? []).map((note, index) => <NoteLine key={`${index}-${note}`}>{note}</NoteLine>)}
    </div>
  );
}

/**
 * A column of the example row: what it holds and its name, which is changed where it is shown. A name left as the build
 * names it stays unset, so the YAML says nothing of it; emptied, it goes back to that name.
 */
function ColumnLine({ glyph, name, shown, onName, value, missing = null, fromEnd = false, problems, testId }: {
  glyph: keyof typeof ROLE_GLYPHS;
  /** The name the draft gives the column; null where the build names it. */
  name: string | null;
  /** The name the column goes by. */
  shown: string;
  onName: (name: string | null) => void;
  value: string | null;
  /** What the column says where it holds nothing. */
  missing?: string | null;
  /** Whether a long value is cut at its start rather than its end: an id, which its end tells apart. */
  fromEnd?: boolean;
  problems: DimensionDraftIssue[];
  testId: string;
}) {
  const [typed, setTyped] = useState<string | null>(null);
  const Icon = ROLE_GLYPHS[glyph].icon;
  const errors = problems.filter((problem) => problem.severity === "error");
  return (
    <div className="px-2 py-0.5" data-testid={testId}>
      <div className="grid grid-cols-[1rem_minmax(0,0.85fr)_minmax(0,1.15fr)] items-center gap-x-1.5">
        <RichTooltip body={ROLE_GLYPHS[glyph].label}>
          <Icon className="size-3.5 justify-self-center text-muted-foreground" aria-label={ROLE_GLYPHS[glyph].label} />
        </RichTooltip>
        <QuietInput
          value={typed ?? name ?? shown}
          onChange={(event) => {
            const text = event.target.value;
            setTyped(text);
            onName(text === "" ? null : text);
          }}
          onBlur={() => setTyped(null)}
          className={cn("h-7 px-1.5 font-mono text-[12px] md:text-[12px]", errors.length > 0 && "border-destructive")}
          aria-invalid={errors.length > 0}
          aria-label={`The column's name, ${shown}`}
          title="Rename the column"
          data-testid={`${testId}-name`}
        />
        {value === null
          ? <span className="truncate text-[12px] italic text-muted-foreground">{missing ?? "nothing"}</span>
          : (
            <RichTooltip body={value} mono>
              <span
                dir={fromEnd ? "rtl" : undefined}
                className="block truncate text-left font-mono text-[12px]"
                data-testid={`${testId}-example`}
              >
                {fromEnd ? <bdi dir="ltr">{value}</bdi> : value}
              </span>
            </RichTooltip>
          )}
      </div>
      {problems.map((problem, index) => (
        <div key={`${index}-${problem.message}`} className="pl-6">
          <IssueLine issue={problem} testId={`${testId}-problem`} />
        </div>
      ))}
    </div>
  );
}
