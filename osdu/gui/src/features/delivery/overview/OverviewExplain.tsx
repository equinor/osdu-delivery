import { X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ExplainConversationView } from "@/features/chat/ExplainContext";
import { ExplainIcon } from "@/features/chat/ExplainIcon";

/**
 * The assistant's explanation of the mapping's YAML, under the canvas where a table's rows open, so the lines it explains
 * stay in view beside it rather than under a drawer. Each question is a fresh conversation, followed up here and kept with
 * the others on the Assistant page, where it is still there after the panel closes.
 */
export function OverviewExplain({ subject, question, onClose }: {
  /** What is explained, in a few words: "YAML, lines 12 to 15 of 80". */
  subject: string;
  question: string;
  onClose: () => void;
}) {
  return (
    <section className="flex h-full min-h-0 flex-col bg-card" aria-label="Explain" data-testid="mapping-overview-explain" data-overview-explain="">
      <header className="flex min-w-0 shrink-0 items-center gap-x-3 border-b border-border px-3 py-1.5 text-[12.5px]">
        <span className="flex shrink-0 items-center gap-1.5">
          <ExplainIcon className="size-3.5 shrink-0 text-primary" aria-hidden />
          <span className="font-medium">Explain</span>
        </span>
        <span className="min-w-0 truncate text-muted-foreground" title={subject} data-testid="mapping-overview-explain-subject">{subject}</span>
        <span className="ml-auto flex items-center gap-2">
          <Button
            variant="ghost"
            size="icon-xs"
            aria-label="Close the explanation"
            title="Close: the conversation stays on the Assistant page"
            onClick={onClose}
            data-testid="mapping-overview-explain-close"
          >
            <X />
          </Button>
        </span>
      </header>
      <div className="min-h-0 flex-1">
        <ExplainConversationView question={question} />
      </div>
    </section>
  );
}
