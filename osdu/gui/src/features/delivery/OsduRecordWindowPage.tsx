import { useEffect, useMemo } from "react";
import { Navigate, useParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "@/api/client";
import { useAuth } from "@/auth/AuthContext";
import { CorrelationError } from "@/components/CorrelationError";
import { deliveryApi, type DeliveryRecordRef } from "../../api/delivery";
import { RecordStatusBadge } from "./DeliveryBadges";
import { RecordOsduView } from "./RecordOsduView";
import { wroteToOsdu } from "./recordTarget";
import { useRecordOsduRead } from "./useRecordOsduRead";

export default function OsduRecordWindowPage() {
  const { flowId, key } = useParams<{ flowId: string; key: string }>();
  if (!flowId || !key) {
    return <Navigate to="/delivery" replace />;
  }

  return <OsduRecordWindow flowId={flowId} deliveryKey={key} />;
}

/**
 * One record's OSDU explorer in a window of its own, opened from the record page's OSDU tab: the record named on one line,
 * and the inspector filling the rest of the window, read from OSDU as the window opens. Nothing of the workbench is
 * around it, so it can sit on a second screen beside the page it was opened from.
 */
function OsduRecordWindow({ flowId, deliveryKey }: DeliveryRecordRef) {
  const ref = useMemo<DeliveryRecordRef>(() => ({ flowId, deliveryKey }), [flowId, deliveryKey]);
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");
  const query = useQuery({ queryKey: ["delivery", "record", flowId, deliveryKey], queryFn: () => deliveryApi.record(ref) });
  const detail = query.data;
  const readable = detail !== undefined && wroteToOsdu(detail.record) && canOperate;
  const osdu = useRecordOsduRead(ref, { readable, readAtOnce: true });
  const label = detail === undefined ? null : detail.record.label ?? detail.record.sourceKey;

  // The window's title names the record, so a second screen of windows tells one from another in the task bar.
  useEffect(() => {
    if (label !== null) {
      document.title = `${label} · OSDU`;
    }
  }, [label]);

  if (query.isError) {
    return (
      <div className="p-3" data-testid="page-osdu-window">
        {isApiError(query.error) ? <CorrelationError error={query.error} /> : <p className="text-[13px] text-destructive">{String(query.error)}</p>}
      </div>
    );
  }

  if (detail === undefined) {
    return (
      <div className="flex min-h-0 flex-1 flex-col gap-2 p-3" data-testid="page-osdu-window">
        <Skeleton className="h-7 w-80 rounded-md" />
        <Skeleton className="min-h-0 flex-1 rounded-lg" />
      </div>
    );
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-2 p-3" data-testid="page-osdu-window">
      <div className="flex min-w-0 items-center gap-2">
        <h1 className="min-w-0 truncate text-[15px] font-semibold" title={label ?? undefined} data-testid="osdu-window-title">{label}</h1>
        <RecordStatusBadge status={detail.record.status} />
      </div>
      <RecordOsduView
        record={detail.record}
        deliveryRef={ref}
        pipelineId={detail.pipelineId}
        flowScope={{ interfaceName: detail.interface ?? null, partition: detail.partition ?? null }}
        canOperate={canOperate}
        disabled={false}
        osdu={osdu}
        fill
      />
    </div>
  );
}
