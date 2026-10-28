import { Link as RouterLink } from "react-router-dom";
import { CirclePlay } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { deliveryRecordRoute } from "../../api/delivery";
import { shortId } from "./idTail";

const REF_CLASS = "font-mono text-[12px] text-primary hover:underline";

/**
 * What an id is ("run", "submission", "record"), before its tail. In a narrow table the word is left to the hover and to a
 * screen reader, so the column keeps to the tail; anywhere else it always shows.
 */
function RefKind({ children }: { children: string }) {
  return <span className="@max-3xl/table:sr-only">{`${children} `}</span>;
}

/**
 * A link to a platform run by the random end of its id (`run …1a2b3c`), the whole id on hover. The run is the execution:
 * its status, its log and its trace. It swallows the click so a link inside a clickable row opens the run, not the row.
 * `iconOnly` shows the icon runs wear in the menu instead, for a grid whose rows open something else and only lead to
 * their run; the id is then on hover alone.
 */
export function RunRef({ runId, iconOnly = false }: { runId: string | null; iconOnly?: boolean }) {
  if (runId === null) {
    return null;
  }

  if (iconOnly) {
    return (
      <Tooltip>
        <TooltipTrigger asChild>
          <Button asChild variant="ghost" size="icon-xs" className="size-5 align-middle text-muted-foreground hover:text-primary">
            <RouterLink
              to={`/runs/${runId}`}
              aria-label={`Open run ${shortId(runId)}`}
              onClick={(event) => event.stopPropagation()}
              data-testid="run-ref-icon"
            >
              <CirclePlay />
            </RouterLink>
          </Button>
        </TooltipTrigger>
        <TooltipContent>{`Open run ${runId.toLowerCase()}`}</TooltipContent>
      </Tooltip>
    );
  }

  return (
    <RouterLink
      to={`/runs/${runId}`}
      className={REF_CLASS}
      title={`run ${runId.toLowerCase()}`}
      onClick={(event) => event.stopPropagation()}
    >
      <RefKind>run</RefKind>{shortId(runId)}
    </RouterLink>
  );
}

/**
 * A link to a submission by the random end of its id. The submission is the plan a deliver run worked under: which rows
 * of the ingestion tables it read, and how each of their records ended up.
 */
export function SubmissionRef({ submissionId }: { submissionId: string | null }) {
  return submissionId === null
    ? null
    : (
      <RouterLink
        to={`/delivery/submissions/${submissionId}`}
        className={REF_CLASS}
        title={`submission ${submissionId.toLowerCase()}`}
        onClick={(event) => event.stopPropagation()}
      >
        <RefKind>submission</RefKind>{shortId(submissionId)}
      </RouterLink>
    );
}

/** A link to one flow's record by the random end of its delivery key: a record is its flow's, so the link names both. */
export function RecordRef({ flowId, deliveryKey }: { flowId: string; deliveryKey: string | null }) {
  return deliveryKey === null
    ? null
    : (
      <RouterLink
        to={deliveryRecordRoute({ flowId, deliveryKey })}
        className={REF_CLASS}
        title={`record ${deliveryKey.toLowerCase()}`}
        onClick={(event) => event.stopPropagation()}
      >
        <RefKind>record</RefKind>{shortId(deliveryKey)}
      </RouterLink>
    );
}
