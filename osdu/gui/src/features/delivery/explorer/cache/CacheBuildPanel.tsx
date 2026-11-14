import { useRef, useState } from "react";
import { Loader2, RotateCcw, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { RichTooltip } from "@/components/RichTooltip";
import type { CacheTypeCompose, DimensionDraftIssue } from "../../../../api/explorer";
import { failureText } from "../../answers";
import { BuildModeSwitch, type BuildMode } from "../BuilderMode";
import { BuilderFilterDialog } from "../dimension/BuilderFilterDialog";
import { BuilderFoot } from "../dimension/BuilderFoot";
import { PlainYaml } from "../dimension/BuilderYaml";
import { kindTypeName } from "../dimension/dimensionStory";
import { IssueLine, QuietInput } from "../dimension/StoryFrame";
import { CachePreview } from "./CachePreview";
import { CacheSteps } from "./CacheSteps";
import { cacheStepOfTarget, cacheStepStates, defaultTypeName, typeNameOf, type CacheStep } from "./cacheStory";
import type { CacheBuild } from "./useCacheBuild";

/** What the panel shows: the story the type is picked by, the records it would hold previewed, or its YAML. */
type BuilderView = "story" | "preview" | "yaml";

/** Whether a value the view toggle gives is one of the panel's views. */
function isView(value: string): value is BuilderView {
  return value === "story" || value === "preview" || value === "yaml";
}

/**
 * The cached type being built in the explorer (osdu/docs/reference/concepts/explorer-cache-builder.md), docked beside
 * the records it is built from, as a dimension is (DimensionBuildPanel), and told as a story read top to bottom: from
 * these records, it keeps these values, with these read through the records they link to, read so, it makes this type.
 * The picks are made on the values of the records: the + beside one keeps it, or adds it as an attribute in a record a
 * link led to. Its name heads it, with what a mapping reads it as; the records it would hold, previewed, and its YAML are
 * views of their own; and the foot says whether the YAML is ready, copies it, and asks the assistant for the whole cache
 * flow file holding it.
 */
export function CacheBuildPanel({ build, startKind, onSwitch }: {
  build: CacheBuild;
  /** The kind a build started over caches: the explorer's place, or the record in view's kind; empty for none. */
  startKind: string;
  /** Builds the other thing the explorer builds instead, from the same records. */
  onSwitch: (mode: BuildMode) => void;
}) {
  const draft = build.draft;
  const [view, setView] = useState<BuilderView>("story");
  const [open, setOpen] = useState<CacheStep | null>(null);
  const [closing, setClosing] = useState(false);
  const story = useRef<HTMLDivElement>(null);
  if (draft === null) {
    return null;
  }

  const states = cacheStepStates(draft);
  const compose = build.compose;
  const issues = compose.data?.issues ?? [];
  const named = issues.filter((issue) => cacheStepOfTarget(issue.target) === "name");
  const keeps = draft.fields.length > 0 || draft.attributes.length > 0;
  // The preview reads what the type keeps, so a draft keeping nothing shows its story.
  const shown: BuilderView = view === "preview" && !keeps ? "story" : view;

  // A problem named in the foot is shown where it is: its step opened in the story, and brought into view.
  const showProblem = (issue: DimensionDraftIssue) => {
    const step = cacheStepOfTarget(issue.target);
    setView("story");
    if (step !== "name" && step !== "makes") {
      setOpen(step);
    }

    window.requestAnimationFrame(() => {
      const target = step === "name" ? null : story.current?.querySelector(`[data-testid="builder-step-${step}"]`);
      (target ?? story.current)?.scrollIntoView({ block: "nearest", behavior: "smooth" });
    });
  };

  return (
    <div className="flex min-h-0 flex-1 flex-col" data-testid="builder-panel" data-mode="cache">
      <div className="flex flex-col border-b px-4 pb-2.5 pt-2">
        <div className="flex h-8 min-w-0 items-center gap-2">
          <BuildModeSwitch mode="cache" onSwitch={onSwitch} />
          {draft.kind !== "" && <span className="-ml-1.5 truncate text-[12px] text-muted-foreground">{`of ${kindTypeName(draft.kind)} records`}</span>}
          <span className="ml-auto flex shrink-0 items-center gap-1">
            <ToggleGroup
              type="single"
              variant="outline"
              size="sm"
              value={shown}
              onValueChange={(value) => {
                if (isView(value)) {
                  setView(value);
                }
              }}
              aria-label="What the builder shows"
              data-testid="builder-view"
            >
              <ToggleGroupItem value="story" className="h-7 px-2.5 text-[12.5px]" data-testid="builder-view-story">Build</ToggleGroupItem>
              <ToggleGroupItem value="preview" className="h-7 px-2.5 text-[12.5px]" disabled={!keeps} data-testid="builder-view-preview">Preview</ToggleGroupItem>
              <ToggleGroupItem value="yaml" className="h-7 px-2.5 text-[12.5px]" disabled={draft.kind === ""} data-testid="builder-view-yaml">YAML</ToggleGroupItem>
            </ToggleGroup>
            <RichTooltip body={startKind === "" ? "Start over: a new cached type." : `Start over: a new cached type of ${kindTypeName(startKind)} records.`}>
              <Button variant="ghost" size="icon-xs" onClick={() => build.start(startKind)} aria-label="Start over" data-testid="builder-restart">
                <RotateCcw />
              </Button>
            </RichTooltip>
            <RichTooltip body="Close the builder. Copy the YAML first: the draft is not kept.">
              <Button variant="ghost" size="icon-xs" onClick={() => (keeps ? setClosing(true) : build.stop())} aria-label="Close the builder" data-testid="builder-close">
                <X />
              </Button>
            </RichTooltip>
          </span>
        </div>
        <QuietInput
          value={draft.name}
          onChange={(event) => build.update({ ...draft, name: event.target.value })}
          placeholder={draft.kind === "" ? "Name the type" : defaultTypeName(draft.kind)}
          className="-mx-2 h-9 w-[calc(100%+1rem)] px-2 text-[17px] font-semibold placeholder:text-foreground/80 md:text-[17px]"
          aria-label="The type's name"
          aria-invalid={named.some((issue) => issue.severity === "error")}
          data-testid="builder-name"
        />
        <p className="px-0 py-0.5 text-[12.5px] text-muted-foreground" data-testid="builder-reads-as">
          {draft.kind === ""
            ? "The name a mapping reads the type by."
            : <>A mapping reads it as <span className="font-mono text-foreground">{`$cache.${typeNameOf(draft)}`}</span>{draft.name.trim() === "" ? ", the type's own name; type another to change it." : "."}</>}
        </p>
        {named.map((issue, index) => <IssueLine key={`${index}-${issue.message}`} issue={issue} />)}
      </div>

      {shown === "story"
        ? (
          <div ref={story} className="min-h-0 flex-1 overflow-y-auto px-4 pb-4 pt-4" data-testid="builder-story">
            <CacheSteps
              build={build}
              draft={draft}
              states={states}
              open={open}
              onOpen={setOpen}
              issues={issues}
              startKind={startKind}
              onPreview={() => setView("preview")}
            />
          </div>
        )
        : shown === "preview"
          ? <CachePreview build={build} draft={draft} />
          : <CacheYaml compose={compose.data} name={typeNameOf(draft)} failure={compose.isError ? failureText(compose.error) : null} />}

      <BuilderFoot
        flow="cache"
        ready={draft.kind !== ""}
        notReady="The YAML is written once a type is picked."
        composed={compose.data}
        composing={compose.isFetching}
        failure={compose.isError ? failureText(compose.error) : null}
        name={typeNameOf(draft)}
        partition={build.partition}
        onShow={showProblem}
      />

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
 * The type as YAML, the builder's output: the item a cache flow lists under `types`, as the control plane wrote it and
 * read it back through the loader a flow is read by, never a copy made here, with what is wrong with it above it.
 */
function CacheYaml({ compose, name, failure }: { compose: CacheTypeCompose | undefined; name: string; failure: string | null }) {
  const issues = [...(compose?.issues.filter((issue) => issue.severity === "error") ?? []), ...(compose?.issues.filter((issue) => issue.severity === "warning") ?? [])];
  return (
    <div className="flex min-h-0 flex-1 flex-col gap-2.5 overflow-auto px-4 py-3" data-testid="builder-expression">
      <p className="text-[12.5px] text-muted-foreground">
        Paste it under <span className="font-mono text-foreground">types:</span> in a cache flow; a refresh of the flow fills{" "}
        <span className="font-mono text-foreground">{name === "" ? "the type" : `$cache.${name}`}</span> in each partition it fills.
      </p>

      {failure !== null && (
        <IssueLine issue={{ severity: "error", message: `The YAML could not be written: ${failure}`, target: null, code: null }} testId="builder-compose-failed" />
      )}
      {issues.length > 0 && (
        <ul className="flex flex-col" data-testid="builder-issues">
          {issues.map((issue, index) => <li key={`${index}-${issue.message}`}><IssueLine issue={issue} /></li>)}
        </ul>
      )}

      {compose === undefined
        ? (
          <span className="inline-flex items-center gap-1.5 text-[12px] text-muted-foreground">
            <Loader2 className="size-3.5 animate-spin" aria-hidden />
            Writing the YAML
          </span>
        )
        : <PlainYaml lines={compose.lines} />}
    </div>
  );
}
