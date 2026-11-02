import type { ReactNode } from "react";
import { Link as RouterLink } from "react-router-dom";
import { format, formatDistanceToNow, isThisYear, isToday } from "date-fns";
import { AlertTriangle, type LucideIcon } from "lucide-react";
import { CopyButton } from "@/components/CopyButton";
import { RichTooltip } from "@/components/RichTooltip";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { parseUtc } from "@/lib/time";
import { cn } from "@/lib/utils";
import { deliveryRecordRoute, type DeliveryRecordRef } from "../../api/delivery";
import { RecordName } from "./RecordName";

/** The ingestion file a record's version was read from, and its row; either may be unknown. */
export interface RecordOrigin {
  fileName: string | null;
  rowNumber: number | null;
}

/**
 * A record in a listing as the operator knows it: its label, and under it its source key and the ingestion file and row
 * it came from, all whole. They are what a row is read by, so they wrap inside the column instead of clipping, and the
 * table gives them the width the compact columns beside them leave. A record with no label is named by its key. The
 * last error, when there is one, is why the record stands where it does, so it sits under the record in two lines at
 * most, whole on hover.
 */
export function RecordIdentity({ label, sourceKey, origin, error }: {
  label: string | null;
  sourceKey: string;
  origin?: RecordOrigin;
  error?: string | null;
}) {
  const file = origin?.fileName
    ? origin.rowNumber === null ? origin.fileName : `${origin.fileName} row ${origin.rowNumber}`
    : null;
  return (
    <div className="flex min-w-0 flex-col whitespace-normal" data-testid="record-identity">
      <span className="break-words font-medium">{label ?? sourceKey}</span>
      {(label !== null || file !== null) && (
        <span className="break-words font-mono text-[11px] text-muted-foreground">
          {label !== null && sourceKey}
          {label !== null && file !== null && <span aria-hidden="true"> · </span>}
          {file !== null && <span className="text-muted-foreground/75" data-testid="record-origin">{file}</span>}
        </span>
      )}
      {error && (
        <RichTooltip body={error} title="Last error">
          <span className="flex min-w-0 items-start gap-1 text-[11px] text-destructive" data-testid="record-last-error">
            <AlertTriangle className="mt-px size-3 shrink-0" aria-label="Last error" />
            <span className="line-clamp-2 break-words">{error}</span>
          </span>
        </RichTooltip>
      )}
    </div>
  );
}

/**
 * Where a record stands in OSDU, in a few characters: its type (WellLog, Wellbore), since the row's record column
 * already names it and an id's unique part is most often a minted hash. The whole id is on hover, with a copy beside
 * it. The type is itself the link to the record's OSDU tab, which reads the record as OSDU holds it; a row's own
 * click still opens the record's journey. A deleted record keeps its type and copy but no link, since OSDU no longer
 * serves it; a record with no id yet says so.
 */
export function OsduTarget({ record, copyTestId = "copy-osdu-id" }: {
  record: DeliveryRecordRef & { targetId: string | null; status: string };
  copyTestId?: string;
}) {
  if (record.targetId === null) {
    return <span className="text-[12px] text-muted-foreground">not yet</span>;
  }

  const name = <RecordName id={record.targetId} typeOnly className="max-w-[140px] text-[12px]" testId="osdu-target" />;
  const removed = record.status === "deleted";
  return (
    <span className="inline-flex min-w-0 max-w-full items-center gap-0.5">
      {/* A removed record opens its OSDU tab too, which asks OSDU for the id to check that it holds nothing there. */}
      <RouterLink
        to={`${deliveryRecordRoute(record)}?tab=osdu`}
        onClick={(event) => event.stopPropagation()}
        aria-label={removed ? `Check that OSDU no longer holds ${record.targetId}` : `Read ${record.targetId} as OSDU holds it`}
        className={removed
          ? "inline-flex min-w-0 items-center text-muted-foreground hover:text-foreground hover:underline"
          : "inline-flex min-w-0 items-center text-primary hover:underline"}
        data-testid="open-in-osdu"
      >
        {name}
      </RouterLink>
      <CopyButton iconOnly label="Copy the OSDU id" text={record.targetId} testId={copyTestId} />
    </span>
  );
}

/**
 * An instant in the fewest characters that still pin it: the clock alone today, the day and minute this year, the day
 * and year before that. Where a cell holds more than one instant an icon says which this is; the wall time to the
 * second, how long ago, and UTC are on hover, captioned. A missing instant reads as `absent`.
 */
export function CompactTime({ value, caption, icon: Icon, absent = "-", className, testId }: {
  value: string | null | undefined;
  /** What the instant is ("Updated", "Delivered"), heading the hover panel. */
  caption: string;
  /** Tells this instant from another in the same cell; a column whose header names the one instant needs none. */
  icon?: LucideIcon;
  absent?: string;
  className?: string;
  testId?: string;
}) {
  const face = (content: ReactNode) => (
    <span className={cn("inline-flex items-center gap-1", className)} data-testid={testId}>
      {Icon && <Icon className="size-3 shrink-0" aria-label={caption} />}
      {content}
    </span>
  );
  if (!value) {
    return face(<span>{absent}</span>);
  }

  const date = parseUtc(value);
  const short = isToday(date) ? format(date, "HH:mm:ss") : isThisYear(date) ? format(date, "MMM d HH:mm") : format(date, "MMM d yyyy");
  const utc = date.toISOString().replace("T", " ").replace(/\.\d+Z$/, " UTC");
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        {face(<span className="font-mono tabular-nums">{short}</span>)}
      </TooltipTrigger>
      <TooltipContent className="font-mono text-[11px]">
        {`${caption} ${format(date, "MMM d, HH:mm:ss")} · ${formatDistanceToNow(date, { addSuffix: true })} · ${utc}`}
      </TooltipContent>
    </Tooltip>
  );
}
