import { useState, type ReactNode } from "react";
import { Check, ChevronDown, KeyRound, Plus, X } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { ExplainTip } from "@/components/ExplainTip";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import { DUPLICATE_COMPARISONS, MAX_DUPLICATE_KEY, type DuplicateComparison, type DuplicateKeyProperty, type Inventory } from "../../../../api/inventories";
import type { InspectorField } from "../../OsduRecordInspector";
import { ExplorerAttributeList } from "../../explorer/ExplorerAttributeList";
import { useExplorerFields } from "../../explorer/explorerFields";
import { fieldLabel } from "../../explorer/explorerModel";
import { locationOf, namesOf } from "../../explorer/dimension/dimensionDraft";
import { COMPARISON_VISUALS, comparisonOf, defaultComparison, keyPathProblem } from "./duplicateModel";

/** What a key is, as the mark beside the word says it. */
const KEY_HINT = [
  "Two records are copies of one another when they hold alike values at every property of the key: a list compares as the values it holds, in any order, and a property one record holds nothing at matches only a record holding nothing there too. A record holding no value at any of them is passed over.",
  "Each property compares exactly, ignoring case, or loose. Pick the properties from the list of the kind's properties, or open a record of the inventory and add one of its values' properties from it.",
  "The check reads the key of every id through OSDU's search, which returns only what its index keeps of a record; a property no record holds a value at is named once the check ends.",
].join("\n\n");

/** One property of the key: its name (its path on hover), how its values compare, picked from a menu, and the way to leave it out. */
function KeyChip({ property, onCompare, onRemove, disabled }: {
  property: DuplicateKeyProperty;
  onCompare: (compare: DuplicateComparison) => void;
  onRemove: () => void;
  disabled: boolean;
}) {
  const compare = comparisonOf(property.compare);
  return (
    <span
      className="inline-flex h-7 max-w-[22rem] items-stretch overflow-hidden rounded-md border border-border bg-muted/40 text-[12.5px]"
      data-testid="duplicate-key-property"
      data-path={property.path}
      data-compare={compare}
    >
      <RichTooltip title={fieldLabel(property.path)} body={property.path} mono>
        <span className="flex min-w-0 items-center gap-1 pl-2 pr-1.5">
          <KeyRound className="size-3 shrink-0 text-muted-foreground" aria-hidden />
          <span className="min-w-0 truncate font-medium">{fieldLabel(property.path)}</span>
        </span>
      </RichTooltip>
      <DropdownMenu>
        <DropdownMenuTrigger asChild disabled={disabled}>
          <button
            type="button"
            className="flex shrink-0 items-center gap-0.5 border-l border-border px-1.5 text-[11.5px] text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:bg-accent disabled:pointer-events-none"
            aria-label={`How ${fieldLabel(property.path)} compares: ${COMPARISON_VISUALS[compare].label}`}
            data-testid="duplicate-key-compare"
          >
            {COMPARISON_VISUALS[compare].label}
            <ChevronDown className="size-3" aria-hidden />
          </button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start" className="w-80">
          <DropdownMenuLabel className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">{`How ${fieldLabel(property.path)} compares`}</DropdownMenuLabel>
          <DropdownMenuSeparator />
          {DUPLICATE_COMPARISONS.map((option) => (
            <DropdownMenuItem key={option} onSelect={() => onCompare(option)} className="items-start gap-2" data-testid={`duplicate-key-compare-${option}`}>
              <Check className={cn("mt-0.5 size-3.5 shrink-0", option === compare ? "text-primary" : "invisible")} aria-hidden />
              <span className="flex min-w-0 flex-col">
                <span className="text-[12.5px] font-medium">{COMPARISON_VISUALS[option].label}</span>
                <span className="text-[11.5px] leading-snug text-muted-foreground">{COMPARISON_VISUALS[option].hint}</span>
              </span>
            </DropdownMenuItem>
          ))}
        </DropdownMenuContent>
      </DropdownMenu>
      <button
        type="button"
        onClick={onRemove}
        disabled={disabled}
        className="flex shrink-0 items-center border-l border-border px-1 text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:bg-accent disabled:pointer-events-none"
        aria-label={`Leave ${fieldLabel(property.path)} out of the key`}
        data-testid="duplicate-key-remove"
      >
        <X className="size-3.5" aria-hidden />
      </button>
    </span>
  );
}

/** Adds a property to the key, as `add` says it can be added, or says why not. */
function added(key: readonly DuplicateKeyProperty[], path: string, compare: DuplicateComparison): DuplicateKeyProperty[] | null {
  const problem = keyPathProblem(path, key);
  if (problem !== null) {
    toast.error(`${fieldLabel(path)} cannot be added to the key: ${problem}`);
    return null;
  }

  return [...key, { path, compare }];
}

/**
 * The key a duplicate check compares the inventory's records by, being built: a chip for each property, in the order it was
 * added, each saying how its values compare (picked from a menu that says what each comparison does) and dropped by its
 * cross; and the list of the kind's properties, read as the explorer reads them, to add one from. A text property is added
 * compared loose, anything else exactly. What the line ends with (the check's own actions) follows the chips.
 */
export function DuplicateKeyEditor({ inventory, draft, onDraft, disabled, trailing }: {
  inventory: Inventory;
  draft: readonly DuplicateKeyProperty[];
  onDraft: (key: DuplicateKeyProperty[]) => void;
  disabled: boolean;
  trailing?: ReactNode;
}) {
  const [open, setOpen] = useState(false);
  const fields = useExplorerFields(inventory.partition, inventory.kind, open);
  return (
    <div className="flex min-w-0 flex-wrap items-center gap-1.5 px-2 py-1.5" data-testid="duplicate-key">
      <ExplainTip title="The key" text={KEY_HINT} testId="duplicate-key-about">
        <button type="button" className="inline-flex h-7 items-center rounded-sm px-1 text-[12px] font-medium text-muted-foreground outline-none hover:text-foreground focus-visible:text-foreground">
          Key
        </button>
      </ExplainTip>
      {draft.map((property, index) => (
        <KeyChip
          key={property.path}
          property={property}
          disabled={disabled}
          onCompare={(compare) => onDraft(draft.map((p, i) => (i === index ? { ...p, compare } : p)))}
          onRemove={() => onDraft(draft.filter((_, i) => i !== index))}
        />
      ))}
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverTrigger asChild>
          <Button
            variant="ghost"
            size="sm"
            className="h-7 gap-1 px-2 text-[12.5px] text-muted-foreground hover:text-foreground"
            disabled={disabled || draft.length >= MAX_DUPLICATE_KEY}
            title={draft.length >= MAX_DUPLICATE_KEY ? `A key compares at most ${MAX_DUPLICATE_KEY} properties` : "Add a property of the kind's records to the key"}
            data-testid="duplicate-key-add"
          >
            <Plus className="size-3.5" />
            {draft.length === 0 ? "Add a property" : "Property"}
          </Button>
        </PopoverTrigger>
        <PopoverContent align="start" className="w-[26rem] p-0" data-testid="duplicate-key-add-panel">
          {open && (
            <ExplorerAttributeList
              read={fields}
              kind={inventory.kind}
              onPick={(field) => {
                const next = added(draft, field.path, defaultComparison(field.index));
                if (next !== null) {
                  onDraft(next);
                  setOpen(false);
                }
              }}
              placeholder="Find a property of the records"
              testId="duplicate-key-properties"
            />
          )}
        </PopoverContent>
      </Popover>
      {draft.length === 0 && (
        <span className="text-[12px] text-muted-foreground" data-testid="duplicate-key-empty">
          or open a record of the inventory and add a property from one of its values
        </span>
      )}
      {trailing !== undefined && <div className="ml-auto flex shrink-0 items-center gap-1">{trailing}</div>}
    </div>
  );
}

/**
 * Beside each value of the record open in the bottom panel while the duplicates tab is in view: the way to add the value's
 * property (its path with its list places left out) to the key being built, or the mark that the key compares it already.
 * Only the record's own values are offered, since a key compares what each record holds itself.
 */
export function DuplicateKeyFieldAction({ field, draft, onDraft }: {
  field: InspectorField;
  draft: readonly DuplicateKeyProperty[];
  onDraft: (key: DuplicateKeyProperty[]) => void;
}) {
  if (field.level !== 0 || field.node.kind !== "leaf") {
    return null;
  }

  const path = namesOf(locationOf(field.node.path)).join(".");
  if (path === "" || path === "id" || path === "version") {
    return null;
  }

  if (draft.some((p) => p.path === path)) {
    return (
      <RichTooltip body={`The key compares ${path}.`}>
        <span className="inline-flex items-center gap-0.5 rounded border border-primary/50 bg-primary/10 px-1 text-[11px] leading-4" data-testid="duplicate-key-mark" data-path={path}>
          <KeyRound className="size-3" aria-hidden />
          key
        </span>
      </RichTooltip>
    );
  }

  const compare = typeof field.node.value === "string" ? "loose" : "exact";
  return (
    <button
      type="button"
      onClick={(event) => {
        event.stopPropagation();
        const next = added(draft, path, compare);
        if (next !== null) {
          onDraft(next);
          toast.success(`${fieldLabel(path)} is in the key, compared ${COMPARISON_VISUALS[compare].label.toLowerCase()}.`);
        }
      }}
      className="inline-flex items-center gap-0.5 rounded border border-dashed border-border px-1 text-[11px] leading-4 text-muted-foreground outline-none hover:border-primary/60 hover:text-foreground focus-visible:border-primary/60 focus-visible:text-foreground"
      title={`Add ${path} to the duplicate key, compared ${COMPARISON_VISUALS[compare].label.toLowerCase()}`}
      data-testid="duplicate-key-field-add"
      data-path={path}
    >
      <Plus className="size-3" aria-hidden />
      key
    </button>
  );
}
