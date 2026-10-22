import { IdChip } from "@/components/IdChip";
import type { DeliveryRecord, DeliveryRecordLink } from "../../api/delivery";
import { RecordName } from "./RecordName";

/** How many of the records reading this one the header names as chips before it counts the rest. */
const READERS_NAMED = 3;

/** The most records reading this one the record's page is given (the control plane's `MaxWaitersShown`). */
const MAX_READERS_SHOWN = 50;

function recordRoute(link: DeliveryRecordLink): string {
  return `/delivery/records/${link.flowId}/${link.deliveryKey}`;
}

/**
 * The records linked to this one through a linked template (osdu/docs/reference/flow/mapping-parent.md): a record of a
 * custom template and its parent, the canonical record, are the same minted id in two namespaces, so each names the other
 * by its type, the part that differs, with the whole id on hover. The parent opens on its own record page when a flow of
 * the partition delivers it, and otherwise in the explorer; the records reading this one as their parent each open on
 * theirs. The version of the parent each document was rendered from is on the timeline, with the try that sent it.
 */
export function RecordParentChips({ record, parent, readBy, explorerPartition, canExplore }: {
  record: DeliveryRecord;
  /** The record of the ledger holding the parent's id, when one of the partition's flows delivers it. */
  parent: DeliveryRecordLink | null | undefined;
  /** The records reading this one as their parent, the first `MAX_READERS_SHOWN`. */
  readBy: DeliveryRecordLink[];
  /** The partition the explorer opens the parent in, for a flow that names its partitions. */
  explorerPartition: string | null;
  /** Whether the reader may open the explorer, which reads OSDU through the partition's connection. */
  canExplore: boolean;
}) {
  const parentId = record.parentId ?? null;
  const named = readBy.slice(0, READERS_NAMED);
  const more = readBy.length - named.length;
  const explorer = parentId === null
    ? undefined
    : `/delivery/explorer?${new URLSearchParams(explorerPartition ? { id: parentId, partition: explorerPartition } : { id: parentId }).toString()}`;

  return (
    <>
      {parentId !== null && (
        <IdChip
          label="parent"
          value={parentId}
          display={<RecordName id={parentId} typeOnly className="max-w-[200px]" />}
          to={parent ? recordRoute(parent) : canExplore ? explorer : undefined}
          testId="record-parent"
          copyTestId="copy-record-parent"
        />
      )}
      {named.map((reader) => (
        <IdChip
          key={`${reader.flowId}:${reader.deliveryKey}`}
          label="read by"
          value={reader.targetId ?? reader.deliveryKey}
          display={reader.targetId
            ? <RecordName id={reader.targetId} typeOnly className="max-w-[200px]" />
            : <span className="truncate">{reader.label ?? reader.sourceKey}</span>}
          to={recordRoute(reader)}
          testId="record-read-by"
          copyTestId="copy-record-read-by"
        />
      ))}
      {more > 0 && (
        <span className="text-[11px] text-muted-foreground" data-testid="record-read-by-more">
          {readBy.length >= MAX_READERS_SHOWN ? `and ${more} or more read by` : `and ${more} more read by`}
        </span>
      )}
    </>
  );
}
