import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { COLUMN_LINEAGE_HINT, columnLineageAsk, type LineageColumn } from "./columnLineage";
import { useExplain } from "./ExplainContext";
import { ExplainIcon } from "./ExplainIcon";

/**
 * Explain its lineage, for a column a page lists: the assistant traces the column back to its source through the flows
 * and the lineage descriptions, and says where the lineage does not say. The answer opens in the drawer Explain opens.
 * Drawn only while the assistant is configured.
 */
export function ExplainColumnLineageButton({ subject, className, testId = "explain-column-lineage" }: {
  subject: LineageColumn;
  className?: string;
  testId?: string;
}) {
  const explainer = useExplain();
  if (explainer === null) {
    return null;
  }

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Button
          type="button"
          variant="ghost"
          size="icon-xs"
          aria-label={`Explain the lineage of ${subject.column}`}
          onClick={(event) => explainer.ask(columnLineageAsk(subject), event.currentTarget)}
          className={cn("shrink-0 text-muted-foreground hover:text-foreground", className)}
          data-testid={testId}
        >
          <ExplainIcon />
        </Button>
      </TooltipTrigger>
      <TooltipContent className="max-w-xs">{COLUMN_LINEAGE_HINT}</TooltipContent>
    </Tooltip>
  );
}
