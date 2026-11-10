import { useId, useState } from "react";
import { Check, Filter } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { cn } from "@/lib/utils";
import { filterProblem, keeps, suggestFilter, type AmbiguousPick, type FilterCompare, type SegmentFilter } from "./dimensionDraft";
import { lastProperty } from "./dimensionStory";

const COMPARES: { value: FilterCompare; label: string }[] = [
  { value: "=", label: "is" },
  { value: "*=", label: "contains" },
  { value: "$=", label: "ends with" },
];

/**
 * Asks how a pick that reaches several values is read. A build keeps the first value it finds (or, for a step, follows every
 * record named), so a person either keeps that, or narrows the objects to those whose property compares with a text: the
 * filter that keeps the object picked is suggested, its effect shown on every object as it is changed.
 */
export function BuilderFilterDialog({ pick, onRead, onCancel }: {
  pick: AmbiguousPick | null;
  onRead: (filter: SegmentFilter | null) => void;
  onCancel: () => void;
}) {
  return (
    <Dialog open={pick !== null} onOpenChange={(open) => { if (!open) { onCancel(); } }}>
      {pick !== null && <FilterForm key={`${pick.step}|${pick.segment}|${pick.what}|${pick.chosen}|${pick.names.join("|")}`} pick={pick} onRead={onRead} onCancel={onCancel} />}
    </Dialog>
  );
}

function FilterForm({ pick, onRead, onCancel }: { pick: AmbiguousPick; onRead: (filter: SegmentFilter | null) => void; onCancel: () => void }) {
  const ids = useId();
  const { candidates, suggested } = suggestFilter(pick.objects, pick.chosen, pick.leaf);
  const [filter, setFilter] = useState<SegmentFilter>(
    suggested ?? { property: candidates[0]?.property ?? "", compare: "=", value: "" },
  );
  const problem = filterProblem(filter);
  const kept = pick.objects.map((object) => problem === null && keeps(object, filter));
  const first = (indexes: number[]) => {
    for (const index of indexes) {
      const value = pick.values[index]?.find((v) => v.trim() !== "");
      if (value !== undefined) {
        return value;
      }
    }

    return null;
  };

  const all = pick.objects.map((_, index) => index);
  const unfiltered = pick.follows ? null : first(all);
  const filtered = pick.follows ? null : first(all.filter((index) => kept[index]));
  const candidate = candidates.find((c) => c.property === filter.property);
  const noneKept = problem === null && !kept.some(Boolean);
  const list = lastProperty(pick.list);

  return (
    <DialogContent className="sm:max-w-xl" data-testid="builder-filter-dialog">
      <DialogHeader>
        <DialogTitle>{pick.follows ? `Which items of ${list} to follow?` : `Which item of ${list} gives ${pick.what}?`}</DialogTitle>
        <DialogDescription>
          {pick.follows
            ? `${pick.list} holds ${pick.objects.length} items, and a build follows every one of them. Narrow them to those you mean, or follow them all.`
            : `${pick.list} holds ${pick.objects.length} items, and a build keeps the first value it finds${unfiltered === null ? "" : `, ${unfiltered}`}. Narrow them to the one you mean, or keep the first.`}
        </DialogDescription>
      </DialogHeader>

      <ul className="flex max-h-48 flex-col overflow-auto rounded-md border border-border text-[12.5px]" data-testid="builder-filter-objects">
        {pick.objects.map((_, index) => (
          <li key={index} className={cn("flex min-w-0 items-center gap-2 border-b border-border/60 px-2.5 py-1.5 last:border-b-0", kept[index] && "bg-primary/[0.06]")}>
            <span className="flex size-4 shrink-0 items-center justify-center">{kept[index] && <Check className="size-3.5 text-primary" aria-label="kept" />}</span>
            <span className={cn("min-w-0 truncate", index === pick.chosen && "font-medium")}>{pick.names[index]}</span>
            <span className="ml-auto min-w-0 truncate font-mono text-[11.5px] text-muted-foreground">{(pick.values[index] ?? []).join(", ") || "nothing"}</span>
          </li>
        ))}
      </ul>

      <div className="flex flex-col gap-2">
        <Label className="text-[12.5px]">Keep only the items whose</Label>
        <div className="flex flex-wrap items-center gap-2">
          <Select value={filter.property} onValueChange={(property) => setFilter((current) => ({ ...current, property }))}>
            <SelectTrigger size="sm" className="min-w-40 font-mono text-[12px]" aria-label="Property" data-testid="builder-filter-property">
              <SelectValue placeholder="property" />
            </SelectTrigger>
            <SelectContent>
              {candidates.map((c) => <SelectItem key={c.property} value={c.property} className="font-mono text-[12px]">{c.property}</SelectItem>)}
            </SelectContent>
          </Select>
          <Select value={filter.compare} onValueChange={(compare) => setFilter((current) => ({ ...current, compare: compare as FilterCompare }))}>
            <SelectTrigger size="sm" className="w-28 text-[12px]" aria-label="Compared" data-testid="builder-filter-compare">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {COMPARES.map((c) => <SelectItem key={c.value} value={c.value} className="text-[12px]">{c.label}</SelectItem>)}
            </SelectContent>
          </Select>
          <Input
            list={`${ids}-values`}
            value={filter.value}
            onChange={(event) => setFilter((current) => ({ ...current, value: event.target.value }))}
            className="h-8 min-w-48 flex-1 font-mono text-[12px]"
            aria-label="Text"
            data-testid="builder-filter-value"
          />
          <datalist id={`${ids}-values`}>
            {(candidate?.values ?? []).map((value) => <option key={value} value={value} />)}
          </datalist>
        </div>
        {problem !== null && filter.property !== "" && <p className="text-[12px] text-destructive">{problem}</p>}
        {noneKept && <p className="text-[12px] text-destructive">No item here is kept by this filter.</p>}
        {candidates.length === 0 && (
          <p className="text-[12px] text-muted-foreground">The items hold no property to tell them apart by, so the first value is kept.</p>
        )}
        {!pick.follows && problem === null && !noneKept && (
          <p className="text-[12.5px]" data-testid="builder-filter-result">
            With the filter, a build reads <span className="font-medium">{filtered ?? "nothing"}</span> here.
          </p>
        )}
      </div>

      <DialogFooter className="gap-2">
        <Button variant="ghost" size="sm" onClick={onCancel} data-testid="builder-filter-cancel">Cancel</Button>
        <Button variant="outline" size="sm" onClick={() => onRead(null)} data-testid="builder-filter-none">
          {pick.follows ? "Follow them all" : "Keep the first found"}
        </Button>
        <Button size="sm" onClick={() => onRead(filter)} disabled={problem !== null || noneKept} data-testid="builder-filter-apply">
          <Filter />
          Use the filter
        </Button>
      </DialogFooter>
    </DialogContent>
  );
}
