import { BotMessageSquare, CircleCheck, CircleX, Loader2, TriangleAlert } from "lucide-react";
import { Button } from "@/components/ui/button";
import { CopyButton } from "@/components/CopyButton";
import { RichTooltip } from "@/components/RichTooltip";
import { useExplain } from "@/features/chat/ExplainContext";
import type { DimensionDraftIssue } from "../../../../api/explorer";
import { flowFileQuestion, flowFileSubject, type FlowFileKind } from "./flowFileQuestion";

/** What a builder wrote and found, as its foot reads it: the item, what is wrong with it, and whether it loads. */
export interface BuilderComposed {
  yaml: string;
  issues: DimensionDraftIssue[];
  valid: boolean;
}

/** What Write the flow file says it does, by what the builder makes. */
const WRITES: Record<FlowFileKind, string> = {
  dimension:
    "The assistant reads the dimension flows the estate already has and writes the whole flow file holding this dimension, modelled on them, its table kept in the database the estate keeps its data in rather than the catalog's. It opens in a panel over the records and checks the file before showing it; nothing is saved.",
  cache:
    "The assistant reads the cache flows the estate already has and writes the whole flow file holding this type, modelled on them, and says where a flow caches a type of this name already. It opens in a panel over the records and checks the file before showing it; nothing is saved.",
};

/**
 * The foot of a builder docked in the explorer, the dimension's and the cached type's alike: whether the YAML is ready, in
 * one line, a click away from what to put right; **Write the flow file**, which asks the assistant for the whole flow
 * file holding the item, in its drawer over the records, wide since its answer is a file (offered while the assistant is
 * configured, and waiting until the YAML loads); and a copy of the YAML. On a narrow builder the actions go to a line of
 * their own, so the verdict keeps room to be read.
 */
export function BuilderFoot({ flow, ready, notReady, composed, composing, failure, name, partition, onShow }: {
  /** What the builder makes, and so the flow file the assistant writes. */
  flow: FlowFileKind;
  /** Whether there is anything to write yet: a kind picked. */
  ready: boolean;
  /** What the foot says while there is not. */
  notReady: string;
  composed: BuilderComposed | undefined;
  composing: boolean;
  failure: string | null;
  /** The name the conversation is headed by. */
  name: string;
  partition: string | null;
  /** Shows where a finding is. */
  onShow: (issue: DimensionDraftIssue) => void;
}) {
  const explain = useExplain();
  // Asked again with the same YAML while the drawer is open, it keeps the conversation the drawer holds.
  const writeFlow = (origin: Element) => {
    if (composed !== undefined && explain !== null) {
      explain.ask({ heading: "Assistant", subject: flowFileSubject(flow, name), question: flowFileQuestion(flow, composed.yaml, partition), wide: true }, origin);
    }
  };

  return (
    <div className="flex min-h-11 flex-wrap items-center gap-x-2 gap-y-1 border-t px-3 py-1.5">
      <div className="flex min-w-40 flex-1">
        <Verdict ready={ready} notReady={notReady} composed={composed} composing={composing} failure={failure} onShow={onShow} />
      </div>
      <span className="ml-auto flex shrink-0 items-center gap-1.5">
        {explain !== null && composed !== undefined && (
          <RichTooltip body={composed.valid ? WRITES[flow] : "The assistant writes the flow file once the YAML loads: put right what the line beside this names first."}>
            <span className="inline-flex" tabIndex={composed.valid ? -1 : 0}>
              <Button variant="outline" size="sm" disabled={!composed.valid} onClick={(event) => writeFlow(event.currentTarget)} data-testid="builder-write-flow">
                <BotMessageSquare />
                Write the flow file
              </Button>
            </span>
          </RichTooltip>
        )}
        {composed !== undefined && <CopyButton label="Copy the YAML" text={composed.yaml} testId="builder-copy" />}
      </span>
    </div>
  );
}

/**
 * Whether the YAML is ready to paste, in one line: quiet when it is, and naming what to put right when it is not, a click
 * away from where that is.
 */
function Verdict({ ready, notReady, composed, composing, failure, onShow }: {
  ready: boolean;
  notReady: string;
  composed: BuilderComposed | undefined;
  composing: boolean;
  failure: string | null;
  onShow: (issue: DimensionDraftIssue) => void;
}) {
  if (!ready) {
    return <span className="text-[12.5px] text-muted-foreground" data-testid="builder-status" data-valid="false">{notReady}</span>;
  }

  if (composed === undefined) {
    return (
      <span className="inline-flex items-center gap-1.5 text-[12.5px] text-muted-foreground" data-testid="builder-status" data-valid="false">
        <Loader2 className="size-3.5 animate-spin" aria-hidden />
        {failure === null ? "Writing the YAML" : `The YAML could not be written: ${failure}`}
      </span>
    );
  }

  const errors = composed.issues.filter((issue) => issue.severity === "error");
  const warnings = composed.issues.filter((issue) => issue.severity === "warning");
  const glyph = composing
    ? <Loader2 className="size-4 shrink-0 animate-spin text-muted-foreground" aria-hidden />
    : errors.length > 0
      ? <CircleX className="size-4 shrink-0 text-destructive" aria-hidden />
      : warnings.length > 0
        ? <TriangleAlert className="size-4 shrink-0 text-warning" aria-hidden />
        : <CircleCheck className="size-4 shrink-0 text-success" aria-hidden />;

  if (errors.length > 0) {
    return (
      <button
        type="button"
        onClick={() => onShow(errors[0])}
        className="inline-flex min-w-0 items-center gap-1.5 rounded-md px-1 py-0.5 text-left text-[12.5px] hover:bg-accent/60"
        title={errors[0].message}
        data-testid="builder-status"
        data-valid={composed.valid}
      >
        {glyph}
        <span className="truncate">
          <span className="font-medium">{`${errors.length} ${errors.length === 1 ? "thing" : "things"} to put right`}</span>
          <span className="text-muted-foreground">{` before the YAML loads: ${errors[0].message}`}</span>
        </span>
      </button>
    );
  }

  return (
    <span className="inline-flex min-w-0 items-center gap-1.5 px-1 text-[12.5px]" data-testid="builder-status" data-valid={composed.valid}>
      {glyph}
      <span className="truncate">
        Ready to paste
        {warnings.length > 0 && (
          <button type="button" onClick={() => onShow(warnings[0])} className="text-muted-foreground hover:text-foreground hover:underline" title={warnings[0].message}>
            {`, ${warnings.length} ${warnings.length === 1 ? "warning" : "warnings"}`}
          </button>
        )}
      </span>
    </span>
  );
}
