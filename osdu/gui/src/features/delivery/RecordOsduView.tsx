import { useCallback, useState } from "react";
import { useHref, useNavigate } from "react-router-dom";
import { AppWindow, BookOpenCheck, Telescope } from "lucide-react";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/EmptyState";
import { IconAction } from "@/components/IconAction";
import { deliveryApi, type DeliveryFlowScope, type DeliveryRecord, type DeliveryRecordRef } from "../../api/delivery";
import { explorerApi, type ExplorerValidation } from "../../api/explorer";
import { ExplorerValidationView, ValidationFieldMark } from "./explorer/ExplorerValidation";
import type { InspectorExtras } from "./OsduRecordInspector";
import { OsduRecordPanel } from "./OsduRecordView";
import { osduEmptyText, wroteToOsdu } from "./recordTarget";
import type { RecordOsduRead } from "./useRecordOsduRead";

/**
 * A record as OSDU holds it, read through its flow's route, in the OSDU inspector: the read's controls, and
 * before any read what a read would show or why there is nothing to read. The record page's OSDU tab shows it under the
 * page's header, with a way to open it in a window of its own; that window shows it alone, filling the window. Its
 * Validation view checks the record in view against the schema of its kind as the explorer checks one, read through the
 * same flow, and the record's fields carry the marks of what the check found.
 */
export function RecordOsduView({ record, deliveryRef, pipelineId, flowScope, canOperate, disabled, osdu, fill = false, popout = false }: {
  record: DeliveryRecord;
  deliveryRef: DeliveryRecordRef;
  pipelineId: string | null;
  /** The record's ledger: the interface and partition a record opened from this one is read through. */
  flowScope: DeliveryFlowScope;
  /** Whether the viewer may start a node task: the operate policy, which every signed-in user holds. */
  canOperate: boolean;
  /** Whether another request of the page is in flight, which holds a read back until it lands. */
  disabled: boolean;
  osdu: RecordOsduRead;
  /** Whether the inspector fills its flex column parent: in a window of its own. */
  fill?: boolean;
  /** Whether to offer the view in a window of its own; not where it already is one. */
  popout?: boolean;
}) {
  const windowHref = useHref(`/delivery/records/${deliveryRef.flowId}/${deliveryRef.deliveryKey}/osdu`);
  const navigate = useNavigate();
  // The record as the explorer shows it: in its partition, among the records of its type and those that mention it.
  const explorerParams = new URLSearchParams(record.targetId === null ? {} : { id: record.targetId });
  if (flowScope.partition) {
    explorerParams.set("partition", flowScope.partition);
  }
  const canActOnTarget = wroteToOsdu(record);
  const reading = osdu.pending;
  const rootId = record.targetId ?? record.deliveryKey;

  // The last check of the record, kept so its fields carry the marks of the problems it found while the record is in view.
  const [validated, setValidated] = useState<ExplorerValidation | null>(null);
  const keepValidated = useCallback((result: ExplorerValidation | null) => setValidated(result), []);
  // A check reads OSDU as the tab's read does, so it takes the operate policy (every signed-in user holds it) and the flow the record is read through.
  const extras: InspectorExtras | undefined = pipelineId === null || !canOperate ? undefined : {
    validation: (checked, shownVersion, openPath) => (
      <ExplorerValidationView
        source={{
          key: ["flow", pipelineId, flowScope.interfaceName ?? null, flowScope.partition ?? null],
          validate: (asked) => explorerApi.validateThroughFlow(pipelineId, flowScope, asked),
        }}
        id={checked}
        version={shownVersion}
        onOpenPath={openPath}
        onResult={checked === rootId ? keepValidated : undefined}
      />
    ),
    fieldActions: (field) => <ValidationFieldMark result={validated} id={rootId} field={field} />,
  };
  const empty = osduEmptyText(record);
  // The read's controls: on their own above the empty state, and on the inspector's location bar once there is a read.
  const actions = (
    <>
      <Button
        variant="outline"
        size="sm"
        className="h-7"
        onClick={osdu.readAgain}
        disabled={disabled || reading || !canActOnTarget || !canOperate}
        title={canOperate ? "Reads the record as OSDU holds it now, through its flow's route and credentials. Nothing is written." : "Reading what OSDU holds takes a signed-in session."}
        data-testid="record-osdu-read"
      >
        <BookOpenCheck />
        {osdu.asked ? "Read again" : "Read"}
      </Button>
      {popout && record.targetId !== null && canOperate && (
        <IconAction
          label="Open in the explorer, among the records of its type and those that mention it"
          icon={<Telescope />}
          variant="ghost"
          className="size-7"
          onClick={() => navigate(`/delivery/explorer?${explorerParams.toString()}`)}
          data-testid="record-osdu-explorer"
        />
      )}
      {popout && (
        <IconAction
          label="Open the OSDU explorer in a window of its own"
          icon={<AppWindow />}
          variant="ghost"
          className="size-7"
          // One window per record: opening it again brings that window back rather than stacking another.
          onClick={() => window.open(windowHref, `osdu-record-${deliveryRef.deliveryKey}`, "popup=yes,width=1280,height=900")}
          data-testid="record-osdu-popout"
        />
      )}
    </>
  );

  return (
    <div className={fill ? "flex min-h-0 flex-1 flex-col gap-2" : "flex flex-col gap-2"} data-testid="record-osdu">
      {!osdu.asked && <div className="flex flex-wrap items-center gap-1">{actions}</div>}
      {!osdu.asked && (
        <EmptyState
          icon={<BookOpenCheck />}
          title={empty.title}
          description={empty.description}
          data-testid="record-osdu-empty"
        />
      )}
      {osdu.asked && (
        <OsduRecordPanel
          key={osdu.askedAt}
          readLinked={pipelineId === null ? null : (id, version) => deliveryApi.readOsdu(pipelineId, id, flowScope, version)}
          root={{ read: osdu.read, error: osdu.error, pending: osdu.pending }}
          targetId={rootId}
          readRootVersion={canActOnTarget && canOperate ? (version) => deliveryApi.read(deliveryRef, version) : undefined}
          ledgerVersion={record.targetVersion}
          actions={actions}
          extras={extras}
          fill={fill}
        />
      )}
    </div>
  );
}
