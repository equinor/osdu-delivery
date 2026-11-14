import { BotMessageSquare, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ExplainConversationView } from "@/features/chat/ExplainContext";

/**
 * The assistant writing the whole flow file for the builder's dimension (**Write the flow file**), in the builder's own
 * panel where its YAML was, so the dimension stays beside the records it was built from. The conversation is a fresh one,
 * followed up here and kept with the others on the Assistant page, where it is still there once this closes.
 */
export function BuilderFlowFile({ subject, question, onClose }: {
  /** What the conversation is about, in a few words. */
  subject: string;
  question: string;
  onClose: () => void;
}) {
  return (
    <section className="flex min-h-0 flex-1 flex-col" aria-label="The flow file" data-testid="builder-flow-file">
      <header className="flex min-w-0 shrink-0 items-center gap-2 border-b border-border px-4 py-1.5 text-[12.5px]">
        <BotMessageSquare className="size-3.5 shrink-0 text-primary" aria-hidden />
        <span className="min-w-0 truncate text-muted-foreground" title={subject} data-testid="builder-flow-file-subject">{subject}</span>
        <Button
          variant="ghost"
          size="icon-xs"
          className="ml-auto"
          aria-label="Close the conversation"
          title="Close: the conversation stays on the Assistant page"
          onClick={onClose}
          data-testid="builder-flow-file-close"
        >
          <X />
        </Button>
      </header>
      <div className="min-h-0 flex-1">
        <ExplainConversationView question={question} />
      </div>
    </section>
  );
}
