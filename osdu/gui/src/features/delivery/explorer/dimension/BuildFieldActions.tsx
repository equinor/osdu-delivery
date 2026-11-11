import { useRef, useState } from "react";
import { KeyRound, type LucideIcon } from "lucide-react";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { RichTooltip } from "@/components/RichTooltip";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { ROLE_GLYPHS } from "../../dimensions/blueprintModel";
import type { InspectorField } from "../../OsduRecordInspector";
import { kindMatches, marksOf, planOf, type BuildAction, type BuildPick, type ValueMark } from "./dimensionDraft";
import { typeName } from "./dimensionStory";
import { PICK_ICON } from "./StoryFrame";
import { locationOf, namesOf } from "../../recordPath";
import type { DimensionBuild } from "./useDimensionBuild";

/** What each pick reads as, in the menu: the words and the glyphs of the builder's story and its example row. */
const ACTIONS: Record<BuildAction, { label: string; help: string; icon: LucideIcon }> = {
  key: { label: "One row for each value", help: "Each distinct value here is a row of the dimension: its key.", icon: ROLE_GLYPHS.key.icon },
  collect: { label: "Collect into a column", help: "A column holding every value a row's records hold here, with how many hold each.", icon: ROLE_GLYPHS.collect.icon },
  value: {
    label: "Name each row by this",
    help: "The row's name, which the dimension's filters and views show. Rows whose names clean alike are one value.",
    icon: ROLE_GLYPHS.label.icon,
  },
  attribute: { label: "Add as a column", help: "A column beside the row's name, read from the record its key names or one that record names.", icon: ROLE_GLYPHS.attribute.icon },
};

/** What the button beside a value offers, by where the record is: the dimension's own records, or one its key leads to. */
const OFFERS = {
  own: "One row for each value, or collect it into a column.",
  named: "Name each row by this, or add it as a column.",
};

/** How a use the draft makes of a value reads. */
function markText(mark: ValueMark): string {
  switch (mark.role) {
    case "key":
      return "key";
    case "value":
      return "name";
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
 * (osdu/docs/reference/concepts/explorer-dimension-builder.md): what the draft reads there, a key's mark where the
 * kind's template says the value names another record (what a dimension's key most often is), and a button offering
 * the picks a value at that depth can be: the key or the collected attribute in the dimension's own record, the row's
 * name or an attribute in a record its key leads to. A pick that cannot be made says why, and where a record is reached
 * through another link than the key's, making that link the key is offered.
 */
export function BuildFieldActions({ field, build }: { field: InspectorField; build: DimensionBuild }) {
  const [menuOpen, setMenuOpen] = useState(false);
  const [tipOpen, setTipOpen] = useState(false);
  // The button takes the focus back when its menu closes, which would open its tooltip over the value just picked; it
  // stays closed until the pointer or the focus leaves the button.
  const quiet = useRef(false);
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
  const usable = rekey !== null || offers.some((offer) => offer.refusal === null);
  const leave = () => {
    quiet.current = false;
  };

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
          body={`Names ${candidate.names.map((name) => (name.includes("--") ? typeName(name) : name)).join(" or ")} records${candidate.repeated ? ", several to a record" : ""}: a key whose record gives each key its name and columns.${candidate.description ? ` ${candidate.description}` : ""}`}
        >
          <KeyRound className="size-3.5 text-primary" aria-label="Can be the key" data-testid="builder-keyable" />
        </RichTooltip>
      )}
      <DropdownMenu
        open={menuOpen}
        onOpenChange={(open) => {
          setMenuOpen(open);
          if (!open) {
            quiet.current = true;
          }
        }}
      >
        <Tooltip open={tipOpen && !menuOpen} onOpenChange={(open) => setTipOpen(open && !quiet.current)} delayDuration={300} disableHoverableContent>
          <TooltipTrigger asChild>
            <DropdownMenuTrigger asChild>
              <button
                type="button"
                className={cn(
                  "inline-flex size-6 items-center justify-center rounded-full outline-none focus-visible:ring-2 focus-visible:ring-ring",
                  usable
                    ? "text-primary hover:bg-primary/10 data-[state=open]:bg-primary/15"
                    : "text-muted-foreground/70 hover:bg-accent hover:text-muted-foreground",
                )}
                aria-label={`Use ${plain} in the dimension`}
                onPointerLeave={leave}
                onBlur={leave}
                data-testid="builder-field-menu"
                data-usable={usable}
              >
                <PICK_ICON className="size-4" />
              </button>
            </DropdownMenuTrigger>
          </TooltipTrigger>
          <TooltipContent variant="panel" side="left" align="center" className="pointer-events-none max-w-72" data-testid="builder-field-tip">
            <span className="mb-1 block text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Use in the dimension</span>
            <span className="block">{usable ? OFFERS[level === 0 ? "own" : "named"] : offers[0]?.refusal?.problem ?? OFFERS.own}</span>
          </TooltipContent>
        </Tooltip>
        <DropdownMenuContent align="end" className="w-80">
          <DropdownMenuLabel className="flex min-w-0 flex-col gap-0.5 font-normal">
            <span className="text-[12px] text-foreground">Use in the dimension</span>
            <span className="truncate font-mono text-[11.5px] text-muted-foreground" title={plain}>{plain}</span>
          </DropdownMenuLabel>
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
                  <span>One row for each <span className="font-mono">{rekey}</span></span>
                  <span className="text-[11.5px] leading-4 text-muted-foreground">Then a row's key names this record, and its values can name the row or be added as columns.</span>
                </span>
              </DropdownMenuItem>
            </>
          )}
        </DropdownMenuContent>
      </DropdownMenu>
    </span>
  );
}
