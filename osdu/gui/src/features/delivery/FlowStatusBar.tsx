import type { ReactNode } from "react";
import { AlertTriangle, CheckCircle2, CircleDashed, Hourglass, PauseCircle, ShieldCheck, Trash2, Undo2, XCircle, type LucideIcon } from "lucide-react";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { RelativeTime } from "@/components/RelativeTime";
import { cn } from "@/lib/utils";
import type { DeliveryFlowStats } from "../../api/delivery";

/** One status a flow's records can be in, as the bar draws it: the colour marks it, the icon and label name it. */
interface StatusPart {
  key: string;
  label: string;
  count: number;
  icon: LucideIcon;
  /** The segment's fill; the tone the record badges give the same status. */
  fill: string;
  /** The icon's colour in the legend. */
  ink: string;
  /** What the status means, for the segment's tooltip. */
  meaning: string;
}

/** The statuses in the order a record moves through them, so the bar reads from done to stuck. */
function partsOf(stats: DeliveryFlowStats): StatusPart[] {
  const sending = stats.pending + stats.delivering;
  return [
    { key: "delivered", label: "delivered", count: stats.delivered, icon: CheckCircle2, fill: "bg-success", ink: "text-success", meaning: "Delivered, as its latest version." },
    {
      key: "pending",
      label: "pending",
      count: sending,
      icon: CircleDashed,
      fill: "bg-info",
      ink: "text-info",
      meaning: stats.delivering > 0 ? `Planned and not yet delivered; ${stats.delivering} being sent now.` : "Planned and not yet delivered.",
    },
    { key: "waiting", label: "waiting", count: stats.waiting, icon: Hourglass, fill: "bg-info/45", ink: "text-info", meaning: "Waiting for a record it refers to." },
    { key: "held", label: "held", count: stats.held, icon: PauseCircle, fill: "bg-warning", ink: "text-warning", meaning: "Held back until its row changes or someone releases it." },
    { key: "failed", label: "failed", count: stats.failed, icon: XCircle, fill: "bg-destructive", ink: "text-destructive", meaning: "Failed on every try." },
    { key: "deleted", label: "deleted", count: stats.deleted, icon: Trash2, fill: "bg-muted-foreground/50", ink: "text-muted-foreground", meaning: "Removed from OSDU: by an operator, sent again when its row is read; by a reversal, blocked until its row changes or it is released; by a removal flow, kept out until a row of its key newer than its removal arrives." },
    {
      key: "reverted",
      label: "reverted",
      count: stats.reverted ?? 0,
      icon: Undo2,
      fill: "bg-muted-foreground/30",
      ink: "text-muted-foreground",
      meaning: "Put back by a reversal to the version OSDU held before, and blocked until its source changes or it is released.",
    },
  ];
}

function percent(count: number, total: number): string {
  const share = (count / total) * 100;
  return share > 0 && share < 1 ? "<1%" : `${Math.round(share)}%`;
}

/**
 * Where a flow's records stand, as one bar: the whole is every record the ledger holds for the flow, each segment a
 * status, drawn in the tone the record badges give it. The legend names the statuses that hold records, with their counts,
 * so a flow whose records are all delivered reads as one line rather than a row of zeros; drift since the last verify
 * sits beside them. The headline and the flow's actions ride on the first line.
 */
export function FlowStatusBar({ stats, actions }: { stats: DeliveryFlowStats; actions: ReactNode }) {
  const parts = partsOf(stats);
  const shown = parts.filter((part) => part.count > 0);
  return (
    <div className="flex flex-col gap-2.5" data-testid="delivery-stats">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <div className="flex items-baseline gap-2">
          <span className="font-mono text-2xl font-semibold tabular-nums" data-testid="delivery-kpi-total">{stats.total.toLocaleString()}</span>
          <span className="text-[13px] text-muted-foreground">{stats.total === 1 ? "record" : "records"}</span>
          {stats.deliveredLast24h > 0 && (
            <span className="text-[12px] text-muted-foreground" data-testid="delivery-kpi-last24h">
              {`${stats.deliveredLast24h.toLocaleString()} delivered in the last 24h`}
            </span>
          )}
        </div>
        <div className="ml-auto flex flex-wrap items-center gap-2">{actions}</div>
      </div>

      {stats.total === 0
        ? <p className="text-[13px] text-muted-foreground">No records yet. A flow&apos;s records appear once its first submission has been planned.</p>
        : (
          <>
            <div className="flex h-2.5 w-full gap-0.5" role="img" aria-label={shown.map((part) => `${part.count} ${part.label}`).join(", ")} data-testid="delivery-status-bar">
              {shown.map((part) => (
                <Tooltip key={part.key}>
                  <TooltipTrigger asChild>
                    {/* A segment keeps a sliver of width however few records it holds, so a single failure is never invisible. */}
                    <span
                      className={cn("h-full min-w-1.5 first:rounded-l-full last:rounded-r-full", part.fill)}
                      style={{ flexGrow: part.count, flexBasis: 0 }}
                      data-testid={`delivery-status-segment-${part.key}`}
                    />
                  </TooltipTrigger>
                  <TooltipContent>
                    <span className="font-medium">{`${part.count.toLocaleString()} ${part.label}`}</span>
                    {` (${percent(part.count, stats.total)}). ${part.meaning}`}
                  </TooltipContent>
                </Tooltip>
              ))}
            </div>
            <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-[13px]" data-testid="delivery-status-legend">
              {shown.map((part) => {
                const Icon = part.icon;
                return (
                  <span key={part.key} className="inline-flex items-center gap-1.5" data-testid={`delivery-kpi-${part.key}`}>
                    <Icon className={cn("size-3.5", part.ink)} aria-hidden="true" />
                    <span className="font-mono tabular-nums">{part.count.toLocaleString()}</span>
                    <span className="text-muted-foreground">{part.label}</span>
                  </span>
                );
              })}
              <Drift stats={stats} />
            </div>
          </>
        )}
    </div>
  );
}

/** Drift since the last verify: a warning when there is any, a quiet line saying when it was last looked for when not. */
function Drift({ stats }: { stats: DeliveryFlowStats }) {
  if (stats.drifted > 0) {
    return (
      <span className="inline-flex items-center gap-1.5 text-warning" data-testid="delivery-kpi-drifted">
        <AlertTriangle className="size-3.5" aria-hidden="true" />
        <span className="font-mono tabular-nums">{stats.drifted.toLocaleString()}</span>
        <span>drifted since the last verify</span>
      </span>
    );
  }

  return (
    <span className="inline-flex items-center gap-1.5 text-muted-foreground" data-testid="delivery-kpi-drifted">
      <ShieldCheck className="size-3.5" aria-hidden="true" />
      {stats.lastVerifiedUtc === null ? "never verified" : <span>none drifted, verified <RelativeTime value={stats.lastVerifiedUtc} /></span>}
    </span>
  );
}
