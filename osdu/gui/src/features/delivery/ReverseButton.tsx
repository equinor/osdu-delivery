import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Undo2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { deliveryApi, type DeliveryFlowScope, type DeliveryReversalAccepted, type ReversalSource } from "../../api/delivery";
import { ReverseDialog } from "./ReverseDialog";
import { useRunStarted } from "./runStarted";

interface ReverseButtonProps {
  pipelineId: string;
  /** The ledger the source belongs to: the interface of a source (null for the single form) and the partition. */
  flowScope: DeliveryFlowScope;
  /** The ledger's name as the dialog names it. */
  flowName: string;
  source: ReversalSource;
  /** What the button says when it would open a reversal ("Reverse this run"); a reversal that exists is resumed instead. */
  label: string;
  testId: string;
  /** What the page does once the reverse run is queued, beside the trace it opens and the toast that links to it. */
  onQueued?: (accepted: DeliveryReversalAccepted) => void;
}

/**
 * The one way into a reversal (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a run or a submission),
 * wherever a run or a submission is shown: the run's page, the submission's page and the audit trail's entry of a run. It
 * asks the control plane whether the source has anything to reverse now, by the answer the request and the reverse run go
 * by, and shows only when it has: a source that delivered nothing, one whose reversal settled every record, and one a
 * reverse run is already working on offer nothing. A reversal that stopped is offered as a resume.
 */
export function ReverseButton({ pipelineId, flowScope, flowName, source, label, testId, onQueued }: ReverseButtonProps) {
  const queryClient = useQueryClient();
  const runStarted = useRunStarted();
  const [open, setOpen] = useState(false);
  const state = useQuery({
    queryKey: ["delivery", "reversible", pipelineId, flowScope.interfaceName ?? null, flowScope.partition ?? null, source.kind, source.id],
    queryFn: () => deliveryApi.reversible(pipelineId, flowScope, source),
  });

  // The dialog stays while it is open, so an answer that changes under it (its own request queued) does not tear it away.
  if (state.data?.reversible !== true && !open) {
    return null;
  }

  const resumes = state.data?.resumes === true;
  return (
    <>
      <Button
        variant="destructive-outline"
        size="sm"
        onClick={() => setOpen(true)}
        title={resumes ? "Take the records the reversal has not settled, those that failed and those passed over as busy." : "Put OSDU back as it was before it."}
        data-testid={testId}
      >
        <Undo2 />
        {resumes ? "Resume the reversal" : label}
      </Button>
      <ReverseDialog
        open={open}
        onClose={() => setOpen(false)}
        pipelineId={pipelineId}
        flowScope={flowScope}
        flowName={flowName}
        source={source}
        onQueued={(accepted) => {
          void queryClient.invalidateQueries({ queryKey: ["delivery", "reversible"] });
          void queryClient.invalidateQueries({ queryKey: ["delivery", "reversals"] });
          runStarted({
            message: `Reversal of ${accepted.records.toLocaleString()} record(s) queued.`,
            runId: accepted.runId,
            title: `Reverse · ${flowName}`,
          });
          onQueued?.(accepted);
        }}
      />
    </>
  );
}
