import { type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, ChevronUp, Telescope, Unplug } from "lucide-react";
import { EmptyState } from "@/components/EmptyState";
import { IconAction } from "@/components/IconAction";
import { explorerApi } from "../../../api/explorer";
import type { InventoryRecord } from "../../../api/inventories";
import type { InspectorField } from "../OsduRecordInspector";
import { ExplorerProblem } from "../explorer/ExplorerProblem";
import { ExplorerRecord } from "../explorer/ExplorerRecord";
import { ALL_KINDS, scopeKind } from "../explorer/explorerModel";
import { TaskProgress } from "../TemplateSheet";
import { ExplainTip } from "@/components/ExplainTip";
import { FindingGlyph } from "./InventoryBadges";
import { findingVisual, findingWhy } from "./inventoryFormat";

/** The explorer's address for what it opens: a record, a place, or a search, in the partition named. */
function explorerHref(partition: string, changes: Record<string, string>): string {
  return `/delivery/explorer?${new URLSearchParams({ partition, ...changes }).toString()}`;
}

/**
 * One id of an inventory as OSDU holds it, in the workbench's bottom panel, over the page as a run's trace is: read
 * through the partition's connection in the explorer's record view (its versions, its links, the records that mention
 * it, its check against its schema), under a line with the id's finding and why (the whole of it on hover, to copy),
 * where the id is among the ids the grid has read, the way to the id before and after, and the explorer, which opens
 * the id, or a place or search the record leads to, with everything it offers. The panel's own bar names it and closes
 * it. What the record view reads is OSDU's alone: nothing of the ledgers is in the record.
 */
export function InventoryIdPanel({ partition, id, record, place, canBack, canForward, onStep, onOpenId, fieldActions }: {
  partition: string;
  /** The OSDU id in the panel. */
  id: string;
  /** The id's row in the grid, once the grid has read it: its finding and why. */
  record: InventoryRecord | undefined;
  /** Where the id is among the ids the grid has read, counted from one, and how many the grid can show; null when it is not among them. */
  place: { at: number; of: number | null } | null;
  canBack: boolean;
  canForward: boolean;
  onStep: (by: -1 | 1) => void;
  /** Opens another id in the panel: one the record view offers in place of an id OSDU holds nothing under. */
  onOpenId: (id: string) => void;
  /** What shows beside each value of the record: the way to add its property to a duplicate check's key, while one is built. */
  fieldActions?: (field: InspectorField) => ReactNode;
}) {
  const navigate = useNavigate();
  const connection = useQuery({
    queryKey: ["explorer", "connection", partition],
    queryFn: () => explorerApi.connection(partition),
    staleTime: 60_000,
  });
  const visual = record === undefined ? null : findingVisual(record.finding);

  let body: ReactNode;
  if (connection.isError) {
    body = <div className="p-3"><ExplorerProblem error={connection.error} /></div>;
  } else if (connection.data === undefined) {
    body = <div className="p-3"><TaskProgress label={`Finding the connection to ${partition}`} testId="inventory-panel-connecting" /></div>;
  } else if (!connection.data.available) {
    body = (
      <EmptyState
        icon={<Unplug />}
        title={`No connection to ${partition}`}
        description={connection.data.reason ?? undefined}
        data-testid="inventory-panel-no-connection"
      />
    );
  } else {
    body = (
      <ExplorerRecord
        key={`${partition}|${id}`}
        partition={partition}
        id={id}
        version={null}
        onScope={(scope) => navigate(explorerHref(partition, { kind: scopeKind(scope) ?? ALL_KINDS }))}
        onOpenId={onOpenId}
        onBrowseQuery={(query) => navigate(explorerHref(partition, { q: query, lq: "1" }))}
        onSwitchPartition={(other) => navigate(explorerHref(other, { id }))}
        fieldActions={fieldActions}
      />
    );
  }

  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="inventory-id-panel" data-id={id}>
      <div className="flex h-9 shrink-0 items-center gap-2 border-b border-border px-3" data-testid="inventory-id-panel-header">
        {record !== undefined && visual !== null
          ? (
            <ExplainTip title={`Why ${visual.label.toLowerCase()}`} text={findingWhy(record)} testId="inventory-id-panel-why">
              <span className="flex min-w-0 items-center gap-1.5 text-[12.5px]" data-testid="inventory-id-panel-finding" data-finding={record.finding}>
                <FindingGlyph finding={record.finding} />
                <span className="shrink-0 font-medium">{visual.label}</span>
                {record.detail !== undefined && record.detail !== "" && <span className="min-w-0 truncate text-muted-foreground">{record.detail}</span>}
              </span>
            </ExplainTip>
          )
          : <span className="text-[12.5px] text-muted-foreground">As OSDU holds it</span>}
        <div className="ml-auto flex shrink-0 items-center gap-0.5">
          {place !== null && (
            <span className="mr-1 font-mono text-[11.5px] tabular-nums text-muted-foreground" data-testid="inventory-id-panel-place">
              {place.at.toLocaleString("en-US")}
              {place.of === null ? "" : ` of ${place.of.toLocaleString("en-US")}`}
            </span>
          )}
          <IconAction label="The id before (Up)" icon={<ChevronUp />} disabled={!canBack} onClick={() => onStep(-1)} className="size-7" data-testid="inventory-id-panel-back" />
          <IconAction label="The id after (Down)" icon={<ChevronDown />} disabled={!canForward} onClick={() => onStep(1)} className="size-7" data-testid="inventory-id-panel-forward" />
          <span className="mx-1 h-4 w-px bg-border" aria-hidden />
          <IconAction
            label="Open in the explorer"
            icon={<Telescope />}
            onClick={() => navigate(explorerHref(partition, { id }))}
            className="size-7"
            data-testid="inventory-id-panel-explorer"
          />
        </div>
      </div>
      {/* The record view's card takes what the panel leaves rather than the height a window of its own would ask for. */}
      <div className="flex min-h-0 flex-1 flex-col p-2 [&_[data-testid=osdu-panel]>div>[data-slot=card]]:min-h-0">
        {body}
      </div>
    </div>
  );
}
