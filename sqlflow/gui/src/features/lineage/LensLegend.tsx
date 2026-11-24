/** One line of the legend: a sample of the line as the canvas draws it, and what it says. */
function Entry({ stroke, dash, width, opacity = 1, children }: {
  stroke: string;
  dash?: string;
  width: number;
  opacity?: number;
  children: string;
}) {
  return (
    <span className="inline-flex items-center gap-1.5 whitespace-nowrap">
      <svg width={22} height={8} aria-hidden className="shrink-0">
        <line x1={0} x2={22} y1={4} y2={4} stroke={stroke} strokeWidth={width} strokeDasharray={dash} opacity={opacity} />
      </svg>
      {children}
    </span>
  );
}

/**
 * What the data view's ink means, on the canvas where it is used: a line is drawn by what it carried in the day
 * looked at, and a card's chart and the day rail use the same inks for the same things. Each line is told by its
 * shape as well as its colour (solid, dashed, dotted), so the reading does not rest on colour.
 */
export function LensLegend() {
  return (
    <div
      data-testid="lens-legend"
      className="flex items-center gap-x-3 rounded-md border border-border bg-card/90 px-2.5 py-1 text-[11px] text-muted-foreground shadow-sm"
    >
      <Entry stroke="var(--chart-1)" width={3}>data moved, heavier for more</Entry>
      <Entry stroke="var(--muted-foreground)" dash="5 4" width={1.5}>ran, nothing moved</Entry>
      <Entry stroke="var(--destructive)" width={1.5}>failed</Entry>
      <Entry stroke="var(--muted-foreground)" dash="1 5" width={1.5} opacity={0.6}>no run</Entry>
    </div>
  );
}
