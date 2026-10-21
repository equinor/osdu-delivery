import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { FileCode, Info, Loader2, Sparkles } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { CopyButton } from "@/components/CopyButton";
import { RichTooltip } from "@/components/RichTooltip";
import { deliveryApi, type DeliveryDimensionViewSuggestion } from "../../../api/delivery";
import { counted } from "../assertions/assertionFormat";
import { ProblemView } from "../TemplateSheet";
import { TOKEN_CLASS, tokensOf } from "./yamlTokens";

/** A block of YAML drawn as the flow's YAML blocks draw it: keys, values, punctuation and comments told apart. */
function YamlText({ text }: { text: string }) {
  return (
    <pre className="min-w-max px-3 py-2 font-mono text-[12px] leading-5">
      {text.split("\n").map((line, number) => {
        const tokens = tokensOf(line);
        const runs: { text: string; token: (typeof tokens)[number] }[] = [];
        for (let at = 0; at < line.length; at++) {
          const previous = runs[runs.length - 1];
          if (previous !== undefined && previous.token === tokens[at]) {
            previous.text += line[at];
          } else {
            runs.push({ text: line[at], token: tokens[at] });
          }
        }

        return (
          <div key={number}>
            <code>{runs.map((run, index) => <span key={index} className={TOKEN_CLASS[run.token]}>{run.text}</span>)}</code>
          </div>
        );
      })}
    </pre>
  );
}

/** The joins offered, each with why on hover, and the `join:` block to paste, with Copy. */
function Suggestion({ suggestion }: { suggestion: DeliveryDimensionViewSuggestion }) {
  if (suggestion.joins.length === 0) {
    return (
      <p className="text-[12.5px] text-muted-foreground" data-testid="dimension-view-suggest-none">
        No join is offered: no column of {suggestion.from} holds a key that another dimension of the flow is keyed by, one row a key, of a type its template names.
      </p>
    );
  }

  return (
    <div className="flex min-w-0 flex-col gap-3">
      <ul className="flex max-h-56 min-w-0 flex-col overflow-y-auto rounded-md border border-border text-[12.5px]" data-testid="dimension-view-suggest-joins">
        {suggestion.joins.map((join) => (
          <li key={`${join.on}-${join.as}`} className="flex min-w-0 items-center gap-2 border-b border-border/60 px-2.5 py-1.5 last:border-b-0" data-testid="dimension-view-suggest-join">
            <span className="shrink-0 font-mono font-medium">{join.as}</span>
            <span className="min-w-0 truncate font-mono text-[11.5px] text-muted-foreground">
              {join.as === join.to ? "" : `${join.to} `}on {join.on}
            </span>
            <RichTooltip title="Why it is offered" body={join.note}>
              <Info className="ml-auto size-3.5 shrink-0 text-muted-foreground" aria-label="Why it is offered" />
            </RichTooltip>
          </li>
        ))}
      </ul>
      <section className="flex min-w-0 flex-col overflow-hidden rounded-md border border-border bg-card" data-testid="dimension-view-suggest-yaml">
        <header className="flex min-w-0 items-center gap-2 border-b border-border bg-muted/40 px-3 py-1.5">
          <FileCode className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
          <span className="text-[12.5px] font-semibold">YAML</span>
          <span className="min-w-0 truncate text-[11.5px] text-muted-foreground">the join block of a view from {suggestion.from}</span>
          <span className="ml-auto shrink-0">
            <CopyButton iconOnly label="Copy the join block" text={suggestion.yaml} testId="dimension-view-suggest-copy" />
          </span>
        </header>
        <div className="max-h-56 min-w-0 overflow-auto">
          <YamlText text={suggestion.yaml} />
        </div>
      </section>
    </div>
  );
}

/**
 * The joins a view could make, offered for a flow's Views section: pick the dimension whose rows the view's rows are, and
 * the dialog lists the joins the flow's dimensions and its templates allow, each with why, and the `join:` block to
 * paste into the view's item of the flow's YAML. It reads the catalog and the templates; nothing is written.
 */
export function DimensionViewSuggestButton({ pipelineId, dimensions }: {
  pipelineId: string;
  /** The dimensions the flow declares, which a view can read its rows from. */
  dimensions: readonly string[];
}) {
  const [open, setOpen] = useState(false);
  const [from, setFrom] = useState<string | null>(null);
  const suggestion = useQuery({
    queryKey: ["delivery", "dimensions", "views", "suggest", pipelineId, from],
    queryFn: () => deliveryApi.suggestDimensionViewJoins(pipelineId, from!),
    enabled: open && from !== null,
  });

  if (dimensions.length === 0) {
    return null;
  }

  return (
    <>
      <RichTooltip body="The joins a view could make from one of the flow's dimensions, as the flow's dimensions and its templates allow, with the join block to paste into its YAML. Nothing is written.">
        <Button variant="ghost" size="xs" onClick={() => setOpen(true)} data-testid="dimension-view-suggest">
          <Sparkles />
          Suggest joins
        </Button>
      </RichTooltip>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="sm:max-w-3xl" data-testid="dimension-view-suggest-dialog">
          <DialogHeader>
            <DialogTitle>Suggest joins</DialogTitle>
            <DialogDescription>
              The joins a view whose rows are a dimension's rows could make. Keep what you need in the view's item of the flow's YAML; nothing is written here.
            </DialogDescription>
          </DialogHeader>
          <div className="flex flex-wrap items-center gap-2">
            <Label htmlFor="dimension-view-suggest-from" className="text-[12.5px]">From</Label>
            <Select value={from ?? ""} onValueChange={(value) => setFrom(value === "" ? null : value)}>
              <SelectTrigger id="dimension-view-suggest-from" size="sm" className="min-w-48 font-mono text-[12px]" data-testid="dimension-view-suggest-from">
                <SelectValue placeholder="pick a dimension" />
              </SelectTrigger>
              <SelectContent>
                {dimensions.map((name) => <SelectItem key={name} value={name} className="font-mono text-[12px]">{name}</SelectItem>)}
              </SelectContent>
            </Select>
            {suggestion.isFetching && <Loader2 className="size-4 animate-spin text-muted-foreground" aria-label="Reading" />}
            {suggestion.data !== undefined && from !== null && (
              <span className="text-[12px] text-muted-foreground" data-testid="dimension-view-suggest-count">{counted(suggestion.data.joins.length, "join")} offered</span>
            )}
          </div>
          {from !== null && (suggestion.isError
            ? <ProblemView error={suggestion.error} testId="dimension-view-suggest-error" />
            : suggestion.data !== undefined && <Suggestion suggestion={suggestion.data} />)}
        </DialogContent>
      </Dialog>
    </>
  );
}
