import { Button } from "@/components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";

/** One line of the key: a sample of the line as the canvas draws it, and what it says. */
function Entry({ stroke, dash, width, opacity = 1, name, children }: {
  stroke: string;
  dash?: string;
  width: number;
  opacity?: number;
  name: string;
  children: string;
}) {
  return (
    <li className="flex items-start gap-2.5">
      <svg width={26} height={16} aria-hidden className="mt-px shrink-0">
        <line x1={0} x2={26} y1={8} y2={8} stroke={stroke} strokeWidth={width} strokeDasharray={dash} opacity={opacity} />
      </svg>
      <span className="min-w-0">
        <span className="block text-[12px] leading-4 font-medium">{name}</span>
        <span className="block text-[11px] leading-4 text-muted-foreground">{children}</span>
      </span>
    </li>
  );
}

/**
 * What the data view's ink means, one click from the rail that picks its day: a line is drawn by what it carried in
 * the day looked at, and a card's chart and the rail's rows use the same inks for the same things. Each line is told
 * by its shape as well as its colour (solid, dashed, dotted), so the reading does not rest on colour. It opens on
 * request and stands on nothing: the words on the cards at a line's two ends already say what the line shows.
 */
export function LensLegend() {
  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button variant="ghost" size="xs" className="h-6 px-2 text-muted-foreground aria-expanded:bg-accent" data-testid="lens-legend-open">
          Key
        </Button>
      </PopoverTrigger>
      <PopoverContent side="top" align="end" className="w-72 p-3" data-testid="lens-legend">
        <ul className="flex flex-col gap-2">
          <Entry name="Data moved" stroke="var(--chart-1)" width={3}>
            The line is heavier the more it carried.
          </Entry>
          <Entry name="Ran, nothing moved" stroke="var(--muted-foreground)" dash="5 4" width={1.5}>
            The flow ran and changed nothing.
          </Entry>
          <Entry name="Failed" stroke="var(--destructive)" width={1.5}>
            Every run of the flow failed.
          </Entry>
          <Entry name="No run" stroke="var(--muted-foreground)" dash="1 5" width={1.5} opacity={0.6}>
            Nothing ran here in the day looked at.
          </Entry>
        </ul>
        <p className="mt-2.5 border-t border-border pt-2 text-[11px] leading-4 text-muted-foreground">
          A bar is how much moved on its day, on a card and in the rows below. A red foot under a bar is a failed run.
        </p>
      </PopoverContent>
    </Popover>
  );
}
