import { KeyRound, ListPlus, Tags, TableProperties, Type, type LucideIcon } from "lucide-react";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import type { InspectorField } from "../../OsduRecordInspector";
import { kindMatches, marksOf, planOf, typeName, type BuildAction, type BuildPick, type ValueMark } from "./dimensionDraft";
import { locationOf, namesOf } from "../../recordPath";
import type { DimensionBuild } from "./useDimensionBuild";

/** What each pick reads as, in the menu. */
const ACTIONS: Record<BuildAction, { label: string; help: string; icon: LucideIcon }> = {
  key: { label: "Make it the key", help: "Each distinct value is a key of the dimension, a row of its table.", icon: KeyRound },
  collect: { label: "Collect it", help: "An attribute holding every value the records of each key hold here, with how many hold each.", icon: ListPlus },
  value: { label: "Read it as the value", help: "Each key's value: what the record the key names holds here.", icon: Type },
  attribute: { label: "Read it as an attribute", help: "A column beside the value, read from the record the key names or one it names.", icon: Tags },
};

/** How a use the draft makes of a value reads. */
function markText(mark: ValueMark): string {
  switch (mark.role) {
    case "key":
      return "key";
    case "value":
      return "value";
    case "collect":
      return `${mark.name}, collected`;
    case "step":
      return `to ${mark.name}`;
    default:
      return mark.name;
  }
}

/**
 * Beside each value of a record on the explorer's trail while a dimension is built
 * (osdu/docs/reference/concepts/explorer.md, Building a dimension): what the draft reads there, a key's mark where the
 * kind's template says the value names another record (what a dimension's key most often is), and the picks a value at
 * that depth can be made: the key or the collected attribute in the dimension's own record, the value or an attribute
 * in a record its key leads to. A pick that cannot be made says why, and where a record is reached through another link
 * than the key's, making that link the key is offered.
 */
export function BuildFieldActions({ field, build }: { field: InspectorField; build: DimensionBuild }) {
  const draft = build.draft;
  if (draft === null || field.node.kind !== "leaf") {
    return null;
  }

  const root = field.trail[0]?.record ?? null;
  const rootKind = root !== null && typeof root.kind === "string" ? root.kind : null;
  const level = field.level;
  const plain = namesOf(locationOf(field.node.path)).join(".");
  const ours = rootKind !== null && (draft.kind === "" || kindMatches(draft.kind, rootKind));
  const candidate = level === 0 && ours ? build.candidates.data?.keys.find((c) => c.path === plain) ?? null : null;
  const marks = marksOf(draft, field.trail, field.node.path);
  const actions: BuildAction[] = level === 0 ? ["key", "collect"] : ["value", "attribute"];
  const offers = actions.map((action) => {
    const pick: BuildPick = { action, trail: field.trail, path: field.node.path };
    const plan = planOf(draft, pick);
    return { action, pick, refusal: "problem" in plan ? plan : null };
  });
  const rekey = offers.map((offer) => offer.refusal?.rekey ?? null).find((path) => path !== null) ?? null;

  return (
    <span className="inline-flex items-center gap-1" data-testid="builder-field" data-path={plain} data-level={level}>
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
      {candidate !== null && (
        <RichTooltip
          title="Can be the key"
          body={`Names ${candidate.names.map((name) => (name.includes("--") ? typeName(name) : name)).join(" or ")} records${candidate.repeated ? ", several to a record" : ""}: a key whose record gives each key its value and attributes.${candidate.description ? ` ${candidate.description}` : ""}`}
        >
          <KeyRound className="size-3.5 text-primary" aria-label="Can be the key" data-testid="builder-keyable" />
        </RichTooltip>
      )}
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <button
            type="button"
            className="inline-flex size-5 items-center justify-center rounded text-muted-foreground hover:bg-accent hover:text-foreground"
            aria-label={`Build the dimension with ${plain}`}
            data-testid="builder-field-menu"
          >
            <TableProperties className="size-3.5" />
          </button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="w-80">
          <DropdownMenuLabel className="truncate font-mono text-[11.5px] font-normal text-muted-foreground" title={plain}>{plain}</DropdownMenuLabel>
          {offers.map(({ action, pick, refusal }) => {
            const { label, help, icon: Icon } = ACTIONS[action];
            return (
              <DropdownMenuItem
                key={action}
                disabled={refusal !== null}
                onSelect={() => build.pick(pick)}
                className="flex items-start gap-2"
                data-testid={`builder-pick-${action}`}
              >
                <Icon className="mt-0.5 size-3.5 shrink-0" />
                <span className="flex min-w-0 flex-col">
                  <span>{label}</span>
                  <span className="text-[11.5px] leading-4 text-muted-foreground">{refusal?.problem ?? help}</span>
                </span>
              </DropdownMenuItem>
            );
          })}
          {rekey !== null && (
            <>
              <DropdownMenuSeparator />
              <DropdownMenuItem
                onSelect={() => build.pick({ action: "key", trail: field.trail.slice(0, 1), path: field.trail[1].from ?? rekey })}
                className="flex items-start gap-2"
                data-testid="builder-pick-rekey"
              >
                <KeyRound className="mt-0.5 size-3.5 shrink-0" />
                <span className="flex min-w-0 flex-col">
                  <span>Make <span className="font-mono">{rekey}</span> the key</span>
                  <span className="text-[11.5px] leading-4 text-muted-foreground">Then this record is the one the key names, and its values can be read.</span>
                </span>
              </DropdownMenuItem>
            </>
          )}
        </DropdownMenuContent>
      </DropdownMenu>
    </span>
  );
}
