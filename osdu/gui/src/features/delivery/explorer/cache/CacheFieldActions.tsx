import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import type { InspectorField } from "../../OsduRecordInspector";
import { locationOf, namesOf } from "../../recordPath";
import { PICK_ICON } from "../dimension/StoryFrame";
import { cacheMarksOf, planCachePick, type CacheAction, type CacheMark, type CachePick } from "./cacheDraft";
import type { CacheBuild } from "./useCacheBuild";

/** What the + beside a value does while a cached type is built, by where the record is: the cached record itself, or one it links to. */
const ACTIONS: Record<CacheAction, { label: string; help: string }> = {
  field: {
    label: "Keep in the cache",
    help: "The cache keeps this value of every record of the type, under a name a mapping matches a source value against or writes.",
  },
  attribute: {
    label: "Add as an attribute",
    help: "The cache reads this value through the link that led here, for every record of the type, and keeps it on that record under a name.",
  },
};

/** How a use the draft makes of a value reads. */
function markText(mark: CacheMark): string {
  return mark.role === "step" ? `to ${mark.name}` : mark.name;
}

/**
 * Beside each value of a record on the explorer's trail while a cached type is built
 * (osdu/docs/reference/concepts/explorer-cache-builder.md): what the draft keeps or reads there, and the + that makes
 * the one thing a value at that depth can be, at once: kept as it is in a record the type caches, an attribute in a
 * record one of its links leads to. One choice needs no menu, so a click makes it; a pick that cannot be made says why.
 */
export function CacheFieldActions({ field, build }: { field: InspectorField; build: CacheBuild }) {
  const draft = build.draft;
  if (draft === null || field.node.kind !== "leaf") {
    return null;
  }

  const plain = namesOf(locationOf(field.node.path)).join(".");
  const action: CacheAction = field.level === 0 ? "field" : "attribute";
  const pick: CachePick = { action, trail: field.trail, path: field.node.path };
  const plan = planCachePick(draft, pick);
  const refusal = "problem" in plan ? plan.problem : null;
  const marks = cacheMarksOf(draft, field.trail, field.node.path);
  const { label, help } = ACTIONS[action];

  return (
    <span className="inline-flex items-center gap-1" data-testid="builder-field" data-path={plain} data-level={field.level}>
      {marks.map((mark, index) => (
        <span
          key={index}
          className={cn(
            "inline-flex items-center rounded border px-1 text-[11px] leading-4",
            mark.role === "step" ? "border-dashed border-border text-muted-foreground" : "border-primary/50 bg-primary/10 text-foreground",
          )}
          data-testid="builder-mark"
          data-role={mark.role}
        >
          {markText(mark)}
        </span>
      ))}
      <Tooltip delayDuration={300} disableHoverableContent>
        <TooltipTrigger asChild>
          <button
            type="button"
            onClick={() => build.pick(pick)}
            className={cn(
              "inline-flex size-6 items-center justify-center rounded-full outline-none focus-visible:ring-2 focus-visible:ring-ring",
              refusal === null ? "text-primary hover:bg-primary/10" : "text-muted-foreground/70 hover:bg-accent hover:text-muted-foreground",
            )}
            aria-label={`${label}: ${plain}`}
            aria-disabled={refusal !== null}
            data-testid={`builder-pick-${action}`}
            data-usable={refusal === null}
          >
            <PICK_ICON className="size-4" />
          </button>
        </TooltipTrigger>
        <TooltipContent variant="panel" side="left" align="center" className="pointer-events-none max-w-72" data-testid="builder-field-tip">
          <span className="mb-1 block text-[11px] font-medium uppercase tracking-wider text-muted-foreground">{label}</span>
          <span className="block">{refusal ?? help}</span>
        </TooltipContent>
      </Tooltip>
    </span>
  );
}
