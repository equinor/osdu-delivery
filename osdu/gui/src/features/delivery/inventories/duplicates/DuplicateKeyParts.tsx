import type { ReactNode } from "react";
import { Check, ChevronDown, KeyRound } from "lucide-react";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { ExplainTip } from "@/components/ExplainTip";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import { DUPLICATE_COMPARISONS, type DuplicateComparison, type DuplicateKeyProperty } from "../../../../api/inventories";
import { fieldLabel } from "../../explorer/explorerModel";
import { COMPARISON_VISUALS, KEY_HINT, comparisonOf } from "./duplicateModel";

/**
 * How one property of the key compares, as a menu that says what each comparison does: on the property's row in the key
 * being built, and on its line in the record it was picked in.
 */
export function ComparisonMenu({ path, compare, onCompare, disabled, className, testId = "duplicate-key-compare" }: {
  path: string;
  compare: string;
  onCompare: (compare: DuplicateComparison) => void;
  disabled: boolean;
  /** The trigger's own classes, where it stands. */
  className?: string;
  testId?: string;
}) {
  const known = comparisonOf(compare);
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild disabled={disabled}>
        <button
          type="button"
          className={cn(
            "flex shrink-0 items-center gap-0.5 px-1.5 text-[11.5px] text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:bg-accent disabled:pointer-events-none",
            className,
          )}
          aria-label={`How ${fieldLabel(path)} compares: ${COMPARISON_VISUALS[known].label}`}
          data-testid={testId}
        >
          {COMPARISON_VISUALS[known].label}
          <ChevronDown className="size-3" aria-hidden />
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-80">
        <DropdownMenuLabel className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">{`How ${fieldLabel(path)} compares`}</DropdownMenuLabel>
        <DropdownMenuSeparator />
        {DUPLICATE_COMPARISONS.map((option) => (
          <DropdownMenuItem key={option} onSelect={() => onCompare(option)} className="items-start gap-2" data-testid={`duplicate-key-compare-${option}`}>
            <Check className={cn("mt-0.5 size-3.5 shrink-0", option === known ? "text-primary" : "invisible")} aria-hidden />
            <span className="flex min-w-0 flex-col">
              <span className="text-[12.5px] font-medium">{COMPARISON_VISUALS[option].label}</span>
              <span className="text-[11.5px] leading-snug text-muted-foreground">{COMPARISON_VISUALS[option].hint}</span>
            </span>
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/** The word that names the key, with what a key is behind it. */
export function KeyTitle({ className }: { className?: string }) {
  return (
    <ExplainTip title="The key" text={KEY_HINT} testId="duplicate-key-about">
      <button type="button" className={cn("inline-flex h-7 items-center rounded-sm px-1 text-[12px] font-medium text-muted-foreground outline-none hover:text-foreground focus-visible:text-foreground", className)}>
        Key
      </button>
    </ExplainTip>
  );
}

/**
 * The key of the check in view, as one line that reads it: a chip for each property, its name (its path on hover) and how it
 * compares. It is changed in the key's builder, which the line's actions open, so a click on the groups never changes it.
 */
export function DuplicateKeySummary({ keyOfCheck, trailing }: { keyOfCheck: readonly DuplicateKeyProperty[]; trailing?: ReactNode }) {
  return (
    <div className="flex min-w-0 flex-wrap items-center gap-1.5 px-2 py-1.5" data-testid="duplicate-key">
      <KeyTitle />
      {keyOfCheck.map((property) => (
        <RichTooltip key={property.path} title={fieldLabel(property.path)} body={`${property.path}, compared ${COMPARISON_VISUALS[comparisonOf(property.compare)].label.toLowerCase()}.`} mono>
          <span
            className="inline-flex h-7 max-w-[22rem] items-center gap-1 rounded-md border border-border bg-muted/40 pl-2 pr-1.5 text-[12.5px]"
            data-testid="duplicate-key-property"
            data-path={property.path}
            data-compare={comparisonOf(property.compare)}
          >
            <KeyRound className="size-3 shrink-0 text-muted-foreground" aria-hidden />
            <span className="min-w-0 truncate font-medium">{fieldLabel(property.path)}</span>
            <span className="shrink-0 border-l border-border pl-1.5 text-[11.5px] text-muted-foreground">{COMPARISON_VISUALS[comparisonOf(property.compare)].label}</span>
          </span>
        </RichTooltip>
      ))}
      {trailing !== undefined && <div className="ml-auto flex shrink-0 items-center gap-1">{trailing}</div>}
    </div>
  );
}
