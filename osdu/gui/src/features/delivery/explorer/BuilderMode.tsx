import { Check, ChevronDown, DatabaseZap, Hammer, TableProperties, type LucideIcon } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { RichTooltip } from "@/components/RichTooltip";

/** What the explorer builds from the records it shows: a dimension, or a type of a partition's cache. */
export type BuildMode = "dimension" | "cache";

/** What each thing built is called, what it is, and its glyph, the same wherever the choice is offered. */
const BUILD_MODES: Record<BuildMode, { label: string; noun: string; help: string; icon: LucideIcon }> = {
  dimension: {
    label: "A dimension",
    noun: "dimension",
    help: "One row for each distinct value the records hold, named and with columns read from the records it names: a table to filter and group by. Written as an item of a dimension flow.",
    icon: TableProperties,
  },
  cache: {
    label: "A cached type",
    noun: "cached type",
    help: "The records themselves, with the values a mapping looks up while it delivers: a name it matches a source value against, the id it writes. Written as an item of a cache flow.",
    icon: DatabaseZap,
  },
};

const MODES: readonly BuildMode[] = ["dimension", "cache"];

/** The choices of what to build, each said by what it makes; the one being built is marked. */
function ModeItems({ current, onPick }: { current: BuildMode | null; onPick: (mode: BuildMode) => void }) {
  return (
    <>
      {MODES.map((mode) => {
        const { label, help, icon: Icon } = BUILD_MODES[mode];
        return (
          <DropdownMenuItem key={mode} onSelect={() => onPick(mode)} className="flex items-start gap-2.5" data-testid={`explorer-build-${mode}`}>
            <Icon className="mt-0.5 size-4 shrink-0" />
            <span className="flex min-w-0 flex-1 flex-col">
              <span className="font-medium">{label}</span>
              <span className="text-[11.5px] leading-4 text-muted-foreground">{help}</span>
            </span>
            {current === mode && <Check className="mt-0.5 size-3.5 shrink-0 text-primary" aria-label="being built" />}
          </DropdownMenuItem>
        );
      })}
    </>
  );
}

/**
 * **Build**, in the explorer's header: one button, and the choice of what to build from the records in view, a dimension
 * or a cached type. The builder opens beside the records, and the + beside each value of a record then offers what that
 * value can be in the thing chosen.
 */
export function BuildButton({ onStart }: { onStart: (mode: BuildMode) => void }) {
  return (
    <DropdownMenu>
      <RichTooltip
        title="Build from these records"
        body="Pick a dimension or a cached type from the records as you browse them, and get the YAML its flow lists. The builder opens beside the records."
      >
        <DropdownMenuTrigger asChild>
          <Button variant="outline" size="sm" className="ml-auto" data-testid="explorer-build">
            <Hammer />
            Build
            <ChevronDown className="size-3.5 text-muted-foreground" />
          </Button>
        </DropdownMenuTrigger>
      </RichTooltip>
      <DropdownMenuContent align="end" className="w-96">
        <DropdownMenuLabel className="text-[12px] font-normal text-muted-foreground">Build from these records</DropdownMenuLabel>
        <ModeItems current={null} onPick={onStart} />
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/**
 * What a docked builder is building, at the head of its panel, and the way to build the other thing instead: the choice
 * starts a new draft from the same records, and says so, since the draft in hand is not kept.
 */
export function BuildModeSwitch({ mode, onSwitch }: { mode: BuildMode; onSwitch: (mode: BuildMode) => void }) {
  const { noun, icon: Icon } = BUILD_MODES[mode];
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          className="inline-flex h-7 shrink-0 items-center gap-1.5 rounded-md px-1.5 text-[12px] text-muted-foreground outline-none hover:bg-accent/60 hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring data-[state=open]:bg-accent/60"
          aria-label={`Building a ${noun}: build something else`}
          data-testid="builder-mode"
          data-mode={mode}
        >
          <Icon className="size-4" aria-hidden />
          <span>{`New ${noun}`}</span>
          <ChevronDown className="size-3.5" aria-hidden />
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="w-96">
        <DropdownMenuLabel className="flex flex-col gap-0.5 font-normal">
          <span className="text-[12px] text-foreground">What to build</span>
          <span className="text-[11.5px] leading-4 text-muted-foreground">The other choice starts a new draft from the same records. This draft is not kept: copy its YAML first.</span>
        </DropdownMenuLabel>
        <ModeItems current={mode} onPick={(picked) => picked !== mode && onSwitch(picked)} />
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
