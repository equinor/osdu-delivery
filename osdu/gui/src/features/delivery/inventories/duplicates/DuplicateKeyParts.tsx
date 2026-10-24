import type { ReactNode } from "react";
import { KeyRound, Loader2, Plus } from "lucide-react";
import { ExplainTip } from "@/components/ExplainTip";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import type { DuplicateKeyProperty } from "../../../../api/inventories";
import { fieldLabel } from "../../explorer/explorerModel";
import { KEY_HINT } from "./duplicateModel";

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
 * The key of the check in view, as one line that reads it, and the way to change it among what the check downloaded: a chip
 * for each property the check downloaded, its name (its path on hover), the key's first and in its order, then the others.
 * A click on a chip adds its property to the key or takes it out (`onToggle`), which groups the check's download again in
 * SQL, without reading OSDU; the key keeps at least one property. Without `onToggle` (the reader may not act, or the check is
 * busy, as `busy` says) the chips only read. A property the key needs that the check did not download is added in the key's
 * builder, which the line's actions open.
 */
export function DuplicateKeySummary({ keyOfCheck, attributes, onToggle, busy, letGo, regrouping, trailing }: {
  keyOfCheck: readonly DuplicateKeyProperty[];
  /** Every property the check downloaded, by path; the key's alone before a download. */
  attributes: readonly string[];
  onToggle?: (path: string) => void;
  /** Why the chips only read now, when they could change the key otherwise; null when they can. */
  busy?: string | null;
  /** What grouping again lets go of the check's groups (their picks, an operator's choices), said after a chip's action. */
  letGo?: string | null;
  /** Whether the key just asked for is being grouped. */
  regrouping?: boolean;
  trailing?: ReactNode;
}) {
  const keyed = new Set(keyOfCheck.map((p) => p.path));
  const others = attributes.filter((path) => !keyed.has(path));
  const chips = [...keyOfCheck.map((p) => p.path), ...others];
  const after = letGo === undefined || letGo === null ? "" : ` ${letGo}`;
  return (
    <div className="flex min-w-0 flex-wrap items-center gap-1.5 px-2 py-1.5" data-testid="duplicate-key">
      <KeyTitle />
      {chips.map((path, index) => {
        const inKey = keyed.has(path);
        const last = inKey && keyed.size === 1;
        const live = onToggle !== undefined && (busy ?? null) === null && regrouping !== true && !last;
        const said = onToggle === undefined
          ? inKey ? "In the key: the copies of a group hold equal values here." : "Downloaded by the check, and not in its key."
          : (busy ?? null) !== null
            ? busy!
            : last
              ? "A key holds at least one property: add another before taking this one out."
              : inKey
                ? `Take it out of the key: the check groups what it downloaded again at once, reading no OSDU.${after}`
                : `Add it to the key: the check downloaded it, so the groups form again at once, reading no OSDU.${after}`;
        const chip = (
          <RichTooltip key={path} title={fieldLabel(path)} body={`${path}\n\n${said}`}>
            {/* Not disabled while it cannot act, so its tooltip still says why. */}
            <button
              type="button"
              aria-disabled={!live}
              onClick={() => {
                if (live) {
                  onToggle?.(path);
                }
              }}
              aria-pressed={onToggle === undefined ? undefined : inKey}
              className={cn(
                "inline-flex h-7 max-w-[22rem] items-center gap-1 rounded-md border px-2 text-[12.5px] outline-none transition-colors focus-visible:ring-2 focus-visible:ring-ring/50",
                inKey ? "border-primary/40 bg-primary/10 text-foreground" : "border-dashed border-border bg-transparent text-muted-foreground",
                live && (inKey ? "hover:bg-primary/15" : "hover:border-solid hover:bg-accent hover:text-foreground"),
                !live && "cursor-default",
              )}
              data-testid="duplicate-key-property"
              data-path={path}
              data-keyed={inKey ? "true" : undefined}
            >
              {inKey ? <KeyRound className="size-3 shrink-0 text-primary" aria-hidden /> : <Plus className="size-3 shrink-0" aria-hidden />}
              <span className={cn("min-w-0 truncate", inKey && "font-medium")}>{fieldLabel(path)}</span>
            </button>
          </RichTooltip>
        );
        return index === keyOfCheck.length && others.length > 0
          ? [<span key="also" className="pl-1 text-[11.5px] text-muted-foreground" data-testid="duplicate-key-also">also downloaded</span>, chip]
          : chip;
      })}
      {regrouping === true && (
        <span className="inline-flex items-center gap-1 text-[12px] text-muted-foreground" data-testid="duplicate-key-regrouping">
          <Loader2 className="size-3.5 animate-spin" aria-hidden />
          grouping
        </span>
      )}
      {trailing !== undefined && <div className="ml-auto flex shrink-0 items-center gap-1">{trailing}</div>}
    </div>
  );
}
