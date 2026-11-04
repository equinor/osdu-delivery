import type { ReactNode } from "react";
import {
  ChevronsDownUp, ChevronsUpDown, Copy, DatabaseSearch, FileCode, Link2, Maximize, Minus, Pin, Plus, Rows3, SquareFunction, type LucideIcon,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import type { MappingOutlineHow } from "../../../api/delivery";
import { formatCount } from "../valueCheck";
import type { OutlineCounts } from "./outlineModel.ts";

/** The ways a property can be filled that the rail lights, each with its glyph, its count and what it means. */
const HOWS: { how: MappingOutlineHow; label: string; count: keyof OutlineCounts; glyph: LucideIcon; meaning: string }[] = [
  { how: "copied", label: "copied", count: "copied", glyph: Copy, meaning: "A column, written as it is." },
  { how: "changed", label: "changed", count: "changed", glyph: SquareFunction, meaning: "A column or an expression, changed on its way." },
  { how: "lookedUp", label: "looked up", count: "lookedUp", glyph: DatabaseSearch, meaning: "Found in the partition's cache, or by a search of OSDU." },
  { how: "fixed", label: "fixed", count: "fixed", glyph: Pin, meaning: "The same on every record: a literal, or a parameter of the flow." },
  { how: "linked", label: "linked", count: "linked", glyph: Link2, meaning: "Read from another record: the parent, a child, or an enrichment." },
];

/** The width the rail takes from the canvas, in px. */
const RAIL_WIDTH = 40;

/** One button of the rail: its glyph, what it does in a tooltip beside it, and for a switch whether it is on. */
function RailButton({ label, detail, pressed, disabled = false, onClick, testId, children }: {
  label: string;
  /** A sentence under the label, for what a word cannot say. */
  detail?: string;
  /** Whether the switch is on; left out for a button that acts once. */
  pressed?: boolean;
  disabled?: boolean;
  onClick: () => void;
  testId: string;
  children: ReactNode;
}) {
  return (
    <Tooltip delayDuration={300} disableHoverableContent>
      <TooltipTrigger asChild>
        {/* A disabled button takes no pointer, so the tooltip hangs off what holds it and still says why. */}
        <span className="inline-flex">
          <Button
            variant="ghost"
            size="icon-sm"
            aria-label={label}
            aria-pressed={pressed}
            disabled={disabled}
            onClick={onClick}
            className={pressed === true ? "bg-primary/15 text-primary hover:bg-primary/20 hover:text-primary dark:hover:bg-primary/20" : "text-muted-foreground hover:text-foreground"}
            data-testid={testId}
          >
            {children}
          </Button>
        </span>
      </TooltipTrigger>
      <TooltipContent side="right" variant={detail === undefined ? "chip" : "panel"} className="pointer-events-none">
        {detail === undefined
          ? label
          : (
            <>
              <span className="block font-medium">{label}</span>
              <span className="block text-muted-foreground">{detail}</span>
            </>
          )}
      </TooltipContent>
    </Tooltip>
  );
}

function RailRule() {
  return <span aria-hidden className="my-1 h-px w-5 shrink-0 bg-border" />;
}

/**
 * The tools of the canvas, down its left side: what is shown with it (a staged record, the YAML), the properties filled one way,
 * and at its foot how the view is folded and framed. A count is said when its glyph is pointed at, so the page's heading stays
 * one line.
 */
export function OverviewRail({ counts, how, onHow, record, yaml, allOpen, onOpenAll, onZoom, onFit }: {
  counts: OutlineCounts;
  how: MappingOutlineHow | null;
  onHow: (how: MappingOutlineHow) => void;
  /** Following a staged record: whether it is on, why it cannot be when it cannot, and the switch. */
  record: { on: boolean; blocked: string | null; onToggle: () => void };
  yaml: { on: boolean; onToggle: () => void };
  allOpen: boolean;
  onOpenAll: () => void;
  onZoom: (factor: number) => void;
  onFit: () => void;
}) {
  const shown = HOWS.filter((item) => counts[item.count] > 0 || how === item.how);
  return (
    <div
      className="flex shrink-0 flex-col items-center gap-0.5 overflow-y-auto border-r border-border bg-muted/40 py-1.5 dark:bg-muted/20"
      style={{ width: RAIL_WIDTH }}
      role="group"
      aria-label="The canvas's tools"
      data-testid="mapping-overview-rail"
    >
      <RailButton
        label="Show a record"
        detail={record.blocked ?? "Follow one staged record through the mapping: each column's value and each property's. Nothing is sent."}
        pressed={record.on}
        disabled={record.blocked !== null}
        onClick={record.onToggle}
        testId="mapping-overview-show-record"
      >
        <Rows3 />
      </RailButton>
      <RailButton
        label="YAML"
        detail="The mapping as written, beside the canvas: pointing at a line lights the property it writes."
        pressed={yaml.on}
        onClick={yaml.onToggle}
        testId="mapping-overview-toggle-yaml"
      >
        <FileCode />
      </RailButton>

      {shown.length > 0 && (
        <>
          <RailRule />
          <div className="flex flex-col items-center gap-0.5" role="group" aria-label="Light the properties filled one way" data-testid="mapping-overview-hows">
            {shown.map((item) => {
              const Glyph = item.glyph;
              return (
                <RailButton
                  key={item.how}
                  label={`${formatCount(counts[item.count])} of ${formatCount(counts.properties)} ${item.label}`}
                  detail={item.meaning}
                  pressed={how === item.how}
                  onClick={() => onHow(item.how)}
                  testId={`mapping-overview-how-${item.how}`}
                >
                  <Glyph />
                </RailButton>
              );
            })}
          </div>
        </>
      )}

      <div className="mt-auto flex flex-col items-center gap-0.5 pt-1" role="group" aria-label="The view">
        <RailButton label={allOpen ? "Fold" : "Open all"} onClick={onOpenAll} testId="mapping-overview-open-all">
          {allOpen ? <ChevronsDownUp /> : <ChevronsUpDown />}
        </RailButton>
        <RailRule />
        <RailButton label="Zoom in" onClick={() => onZoom(1.25)} testId="mapping-overview-zoom-in"><Plus /></RailButton>
        <RailButton label="Zoom out" onClick={() => onZoom(0.8)} testId="mapping-overview-zoom-out"><Minus /></RailButton>
        <RailButton label="Fit to the window" onClick={onFit} testId="mapping-overview-fit"><Maximize /></RailButton>
      </div>
    </div>
  );
}
