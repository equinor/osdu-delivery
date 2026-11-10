import { useMemo, useRef, useState } from "react";
import { CircleCheck, CircleX, Loader2, RotateCcw, TableProperties, TriangleAlert, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Textarea } from "@/components/ui/textarea";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { CopyButton } from "@/components/CopyButton";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import type { DeliveryDimensionKey } from "../../../../api/delivery";
import type { DimensionCompose, DimensionDraftIssue, DimensionExample } from "../../../../api/explorer";
import { failureText } from "../../answers";
import { BlueprintDiagram } from "../../dimensions/BlueprintDiagram";
import { examplesOf, layoutOf } from "../../dimensions/blueprintModel";
import { BuilderFilterDialog } from "./BuilderFilterDialog";
import { BuilderRow } from "./BuilderRow";
import { BuilderSteps } from "./BuilderStory";
import { BuilderYaml } from "./BuilderYaml";
import { kindTypeName, nextStep, stepOfTarget, stepStates, type StoryStep } from "./dimensionStory";
import { IssueLine, QuietInput } from "./StoryFrame";
import type { DimensionBuild } from "./useDimensionBuild";

/** What the panel shows: the story the dimension is picked by, or the YAML it makes. */
type BuilderView = "story" | "yaml";

/** The example row a dimension's pages draw a key from, made of what the builder's example read. */
function keyOf(example: DimensionExample): DeliveryDimensionKey {
  return {
    keyId: 0,
    key: example.key,
    label: example.label,
    labelFrom: example.labelFrom,
    valueId: null,
    value: example.value,
    leftOut: null,
    note: example.note,
    count: example.records ?? 0,
    filterable: example.filter !== null,
    filter: example.filter,
    firstSeenBuildId: 0,
    firstSeenUtc: "",
    valueSinceBuildId: 0,
    removedBuildId: null,
    removedUtc: null,
    attributes: example.attributes,
  };
}

/**
 * The dimension being built in the explorer (osdu/docs/reference/concepts/explorer-dimension-builder.md), docked
 * beside the records it is built from and told as a story read top to bottom, one step per choice: from these records,
 * one row per this key, shown as this, with these columns, cleaned so, it makes this table, whose example row ends it.
 * Only the step asking for a pick says what to do, with the one action that does it; every step's settings open in
 * place, and the picks themselves are made on the values of the records. Its name heads it, the YAML is a view of its
 * own, and the foot says whether the YAML is ready and copies it.
 */
export function DimensionBuildPanel({ build, startKind }: {
  build: DimensionBuild;
  /** The kind a build started over reads: the explorer's place, or the record in view's kind; empty for none. */
  startKind: string;
}) {
  const draft = build.draft;
  const [view, setView] = useState<BuilderView>("story");
  const [open, setOpen] = useState<StoryStep | null>(null);
  const [showRow, setShowRow] = useState(false);
  const [closing, setClosing] = useState(false);
  const story = useRef<HTMLDivElement>(null);

  const states = draft === null ? null : stepStates(draft, build.keyEntity !== null);
  const next = states === null ? null : nextStep(states);
  // The key, when it is what the story asks for, is picked among the template's suggestions: its step opens on them, and
  // closes again once a key is picked (the template's likeliest, as a build starts), so the step asking next is in view.
  const [seenNext, setSeenNext] = useState(next);
  if (seenNext !== next) {
    setSeenNext(next);
    if (next === "rows") {
      setOpen("rows");
    } else if (seenNext === "rows" && open === "rows") {
      setOpen(null);
    }
  }

  if (draft === null || states === null) {
    return null;
  }

  const compose = build.compose;
  const issues = compose.data?.issues ?? [];
  const named = issues.filter((issue) => stepOfTarget(issue.target) === "name");
  const keyed = draft.path !== null && draft.path !== "";

  // A problem named in the foot is shown where it is: its step opened in the story, and brought into view.
  const showProblem = (issue: DimensionDraftIssue) => {
    const step = stepOfTarget(issue.target);
    setView("story");
    if (step !== null && step !== "name" && step !== "makes") {
      setOpen(step);
    }

    window.requestAnimationFrame(() => {
      const target = step === null || step === "name" ? null : story.current?.querySelector(`[data-testid="builder-step-${step}"]`);
      (target ?? story.current)?.scrollIntoView({ block: "nearest", behavior: "smooth" });
    });
  };

  return (
    <div className="flex min-h-0 flex-1 flex-col" data-testid="builder-panel">
      <div className="flex flex-col border-b px-4 pb-2.5 pt-2">
        <div className="flex h-8 min-w-0 items-center gap-2">
          <TableProperties className="size-4 shrink-0 text-muted-foreground" aria-hidden />
          <span className="truncate text-[12px] text-muted-foreground">
            {draft.kind === "" ? "New dimension" : `New dimension of ${kindTypeName(draft.kind)} records`}
          </span>
          <span className="ml-auto flex shrink-0 items-center gap-1">
            <ToggleGroup
              type="single"
              variant="outline"
              size="sm"
              value={view}
              onValueChange={(value) => {
                if (value === "story" || value === "yaml") {
                  setView(value);
                }
              }}
              aria-label="What the builder shows"
              data-testid="builder-view"
            >
              <ToggleGroupItem value="story" className="h-7 px-2.5 text-[12.5px]" data-testid="builder-view-story">Build</ToggleGroupItem>
              <ToggleGroupItem value="yaml" className="h-7 px-2.5 text-[12.5px]" disabled={draft.kind === ""} data-testid="builder-view-yaml">YAML</ToggleGroupItem>
            </ToggleGroup>
            <RichTooltip body={startKind === "" ? "Start over: a new dimension." : `Start over: a new dimension of ${kindTypeName(startKind)} records.`}>
              <Button variant="ghost" size="icon-xs" onClick={() => build.start(startKind)} aria-label="Start over" data-testid="builder-restart">
                <RotateCcw />
              </Button>
            </RichTooltip>
            <RichTooltip body="Close the builder. Copy the YAML first: the draft is not kept.">
              <Button variant="ghost" size="icon-xs" onClick={() => (keyed ? setClosing(true) : build.stop())} aria-label="Close the builder" data-testid="builder-close">
                <X />
              </Button>
            </RichTooltip>
          </span>
        </div>
        <QuietInput
          value={draft.name}
          onChange={(event) => build.update({ ...draft, name: event.target.value })}
          placeholder="Name the dimension"
          className="-mx-2 h-9 w-[calc(100%+1rem)] px-2 text-[17px] font-semibold md:text-[17px]"
          aria-label="The dimension's name"
          aria-invalid={named.some((issue) => issue.severity === "error" && issue.target === "name")}
          data-testid="builder-name"
        />
        <Textarea
          value={draft.description ?? ""}
          onChange={(event) => build.update({ ...draft, description: event.target.value === "" ? null : event.target.value })}
          placeholder="Add a description: what the dimension is for"
          rows={1}
          className="-mx-2 min-h-0 w-[calc(100%+1rem)] resize-none border-transparent bg-transparent px-2 py-0.5 text-[12.5px] text-muted-foreground shadow-none placeholder:text-muted-foreground/70 hover:border-input focus-visible:border-ring focus-visible:text-foreground md:text-[12.5px] dark:bg-transparent"
          aria-label="The dimension's description"
          data-testid="builder-description"
        />
        {named.map((issue, index) => <IssueLine key={`${index}-${issue.message}`} issue={issue} />)}
      </div>

      {view === "story"
        ? (
          <div ref={story} className="min-h-0 flex-1 overflow-y-auto px-4 pb-4 pt-4" data-testid="builder-story">
            <BuilderSteps build={build} draft={draft} states={states} open={open} onOpen={setOpen} issues={issues} startKind={startKind} />
            <BuilderRow build={build} draft={draft} state={states.makes} issues={issues} onShowRow={() => setShowRow(true)} />
          </div>
        )
        : <BuilderYaml compose={compose.data} failure={compose.isError ? failureText(compose.error) : null} />}

      <div className="flex min-h-11 items-center gap-2 border-t px-3 py-1.5">
        <Verdict
          ready={draft.kind !== ""}
          compose={compose.data}
          composing={compose.isFetching}
          failure={compose.isError ? failureText(compose.error) : null}
          onShow={showProblem}
        />
        <span className="ml-auto shrink-0">
          {compose.data !== undefined && <CopyButton label="Copy the YAML" text={compose.data.yaml} testId="builder-copy" />}
        </span>
      </div>

      <Dialog open={showRow} onOpenChange={setShowRow}>
        <DialogContent className="max-h-[90vh] overflow-auto sm:max-w-[min(1400px,95vw)]" data-testid="builder-row-dialog">
          <DialogHeader>
            <DialogTitle>How a row is built</DialogTitle>
            <DialogDescription>
              The records searched, each record found by id, and the table, as a build reads them
              {compose.data?.example ? `, filled with what the key ${compose.data.example.value ?? compose.data.example.key} reads` : ""}.
            </DialogDescription>
          </DialogHeader>
          <RowPreview compose={compose.data} />
        </DialogContent>
      </Dialog>
      <BuilderFilterDialog pick={build.question} onRead={build.answer} onCancel={build.cancel} />
      <ConfirmDialog
        open={closing}
        title="Close the builder?"
        message="The draft is not kept anywhere: copy its YAML first if you want it."
        confirmLabel="Close the builder"
        onConfirm={() => {
          setClosing(false);
          build.stop();
        }}
        onClose={() => setClosing(false)}
      />
    </div>
  );
}

/**
 * Whether the YAML is ready to paste, in one line: quiet when it is, and naming what to put right when it is not, a click
 * away from where that is.
 */
function Verdict({ ready, compose, composing, failure, onShow }: {
  ready: boolean;
  compose: DimensionCompose | undefined;
  composing: boolean;
  failure: string | null;
  onShow: (issue: DimensionDraftIssue) => void;
}) {
  if (!ready) {
    return <span className="text-[12.5px] text-muted-foreground" data-testid="builder-status" data-valid="false">The YAML is written once a type is picked.</span>;
  }

  if (compose === undefined) {
    return (
      <span className="inline-flex items-center gap-1.5 text-[12.5px] text-muted-foreground" data-testid="builder-status" data-valid="false">
        <Loader2 className="size-3.5 animate-spin" aria-hidden />
        {failure === null ? "Writing the YAML" : `The YAML could not be written: ${failure}`}
      </span>
    );
  }

  const errors = compose.issues.filter((issue) => issue.severity === "error");
  const warnings = compose.issues.filter((issue) => issue.severity === "warning");
  const glyph = composing
    ? <Loader2 className="size-4 shrink-0 animate-spin text-muted-foreground" aria-hidden />
    : errors.length > 0
      ? <CircleX className="size-4 shrink-0 text-destructive" aria-hidden />
      : warnings.length > 0
        ? <TriangleAlert className="size-4 shrink-0 text-warning" aria-hidden />
        : <CircleCheck className="size-4 shrink-0 text-success" aria-hidden />;

  if (errors.length > 0) {
    return (
      <button
        type="button"
        onClick={() => onShow(errors[0])}
        className="inline-flex min-w-0 items-center gap-1.5 rounded-md px-1 py-0.5 text-left text-[12.5px] hover:bg-accent/60"
        title={errors[0].message}
        data-testid="builder-status"
        data-valid={compose.valid}
      >
        {glyph}
        <span className="truncate">
          <span className="font-medium">{`${errors.length} ${errors.length === 1 ? "thing" : "things"} to put right`}</span>
          <span className="text-muted-foreground">{` before the YAML loads: ${errors[0].message}`}</span>
        </span>
      </button>
    );
  }

  return (
    <span className={cn("inline-flex min-w-0 items-center gap-1.5 px-1 text-[12.5px]")} data-testid="builder-status" data-valid={compose.valid}>
      {glyph}
      <span className="truncate">
        Ready to paste
        {warnings.length > 0 && (
          <button type="button" onClick={() => onShow(warnings[0])} className="text-muted-foreground hover:text-foreground hover:underline" title={warnings[0].message}>
            {`, ${warnings.length} ${warnings.length === 1 ? "warning" : "warnings"}`}
          </button>
        )}
      </span>
    </span>
  );
}

/** How a build makes each column of the drafted dimension, drawn as a flow's own dimension is, filled with the example key's row. */
function RowPreview({ compose }: { compose: DimensionCompose | undefined }) {
  const layout = useMemo(() => (compose?.blueprint == null ? null : layoutOf(compose.blueprint)), [compose]);
  const examples = useMemo(
    () => (layout === null || compose?.example == null ? null : examplesOf(layout, keyOf(compose.example))),
    [layout, compose],
  );
  if (layout === null || compose?.dimension == null) {
    return <p className="p-4 text-[12.5px] text-muted-foreground">The row is drawn once the YAML reads.</p>;
  }

  return (
    <div className="min-w-0 overflow-x-auto rounded-lg border border-border" data-testid="builder-row-preview">
      <BlueprintDiagram
        layout={layout}
        dimension={compose.dimension}
        table={compose.table ?? ""}
        coverage={new Map()}
        light={null}
        picked={null}
        examples={examples}
        onPoint={() => undefined}
        onPick={() => undefined}
      />
    </div>
  );
}
