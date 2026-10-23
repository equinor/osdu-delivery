import type { DeliveryTotals } from "./deliveryTotals";

type CountTone = "success" | "warning" | "destructive" | "info";

const TONES: Record<CountTone, string> = {
  success: "text-success",
  warning: "text-warning",
  destructive: "text-destructive",
  info: "text-info",
};

/**
 * One count of a flow's records. A tone says what the records it counts are (delivered, held), so a count of none takes
 * no tone: a zero is the absence of the thing, and is drawn as quietly as an empty cell.
 */
export function Count({ label, value, tone, testId }: { label: string; value: number; tone?: CountTone; testId?: string }) {
  const color = value === 0 ? "text-muted-foreground" : tone === undefined ? "" : TONES[tone];
  return (
    <div className="min-w-0" data-testid={testId}>
      <div className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">{label}</div>
      <div className={`font-mono text-lg tabular-nums ${color}`}>{value.toLocaleString()}</div>
    </div>
  );
}

/**
 * The records by where they stand, as one bar: delivered, pending, held and failed in their status colors, and the rest
 * (deleted, waiting) unfilled. Each part names its count on hover; the counts beside it say the same in words.
 */
export function DeliveryBar({ totals }: { totals: DeliveryTotals }) {
  const parts = [
    { label: "delivered", value: totals.delivered, className: "bg-success" },
    { label: "pending", value: totals.pending, className: "bg-info" },
    { label: "held", value: totals.held, className: "bg-warning" },
    { label: "failed", value: totals.failed, className: "bg-destructive" },
  ].filter((part) => part.value > 0);
  return (
    <div className="flex h-2 w-full overflow-hidden rounded bg-muted" data-testid="delivery-bar">
      {totals.total > 0 && parts.map((part) => (
        <div
          key={part.label}
          className={part.className}
          style={{ width: `${(part.value / totals.total) * 100}%` }}
          title={`${part.value.toLocaleString()} ${part.label}`}
        />
      ))}
    </div>
  );
}
