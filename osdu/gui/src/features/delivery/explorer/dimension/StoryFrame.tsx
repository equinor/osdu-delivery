import type { ComponentProps, ReactNode } from "react";
import { Link } from "react-router-dom";
import { ChevronDown, CirclePlus, CircleX, Info, TriangleAlert, type LucideIcon } from "lucide-react";
import { Input } from "@/components/ui/input";
import { cn } from "@/lib/utils";
import type { DimensionDraftIssue } from "../../../../api/explorer";
import type { StepState } from "./dimensionStory";

/**
 * One step of a builder's story, the dimension's or the cached type's
 * (osdu/docs/reference/concepts/explorer-dimension-builder.md, explorer-cache-builder.md): a marker on the rail
 * that joins the steps, saying where the step stands; a sentence of what the draft holds there, its lead quiet and its
 * choice in full; a line of facts; what the builder found wrong there, loud for an error and quiet for a warning; the ask
 * of the step that wants a pick next; and the step's settings, shown only while it is open.
 */
export function StoryFrame({ step, state, lead, answer, facts, issues = [], shownInBody = [], open = false, onToggle, hint, body, children, last = false }: {
  /** The step's name in its story (`from`, `rows`, `keeps`), which its test id carries. */
  step: string;
  state: StepState;
  /** The words the step's sentence starts with: `From`, `One row per`. */
  lead: string;
  /** What the draft holds at the step, said after the lead. */
  answer: ReactNode;
  /** One line of facts under the sentence: a path, a count. */
  facts?: ReactNode;
  issues?: DimensionDraftIssue[];
  /** Findings about the step that its body says where they are (a column's, under the column), which its marker counts too. */
  shownInBody?: DimensionDraftIssue[];
  /** Whether the step's settings are shown. */
  open?: boolean;
  /** Opens or closes the step's settings; absent for a step with none. */
  onToggle?: () => void;
  /** What the step asks for, while it is the one asking. */
  hint?: ReactNode;
  /** What the step always shows under its sentence: the table's example row. */
  body?: ReactNode;
  /** The step's settings. */
  children?: ReactNode;
  /** The last step, where the rail ends. */
  last?: boolean;
}) {
  const sentence = (
    <span className="min-w-0 flex-1">
      <span className="block text-[13.5px] leading-5">
        <span className="text-muted-foreground">{lead} </span>
        <span className="font-medium text-foreground">{answer}</span>
      </span>
      {shows(facts) && <span className="mt-0.5 block truncate text-[12px] leading-4 text-muted-foreground">{facts}</span>}
    </span>
  );

  return (
    <section className="grid grid-cols-[1.25rem_minmax(0,1fr)] gap-x-2.5" data-testid={`builder-step-${step}`} data-state={state}>
      <div className="flex flex-col items-center">
        <span className="mt-[3px] flex size-3.5 shrink-0 items-center justify-center">
          <Marker state={state} issues={[...issues, ...shownInBody]} />
        </span>
        {!last && <span aria-hidden className="mt-1 w-px flex-1 bg-input/80" />}
      </div>
      <div className={cn("min-w-0", last ? "pb-1" : "pb-5", state === "waiting" && "opacity-55")}>
        {onToggle === undefined
          ? sentence
          : (
            <button
              type="button"
              onClick={onToggle}
              aria-expanded={open}
              className="group -mx-1 -my-0.5 flex w-[calc(100%+0.5rem)] min-w-0 items-start gap-2 rounded-md px-1 py-0.5 text-left hover:bg-accent/50 focus-visible:outline-1 focus-visible:outline-ring"
              data-testid={`builder-step-${step}-toggle`}
            >
              {sentence}
              <ChevronDown
                className={cn("mt-0.5 size-4 shrink-0 text-muted-foreground transition-transform group-hover:text-foreground", !open && "-rotate-90")}
                aria-hidden
              />
            </button>
          )}
        {issues.map((issue, index) => <IssueLine key={`${index}-${issue.message}`} issue={issue} />)}
        {body}
        {shows(hint) && (
          <div className="mt-2.5 rounded-md border border-primary/35 bg-primary/[0.04] px-3 py-2.5 text-[12.5px] leading-5" data-testid="builder-next">
            {hint}
          </div>
        )}
        {open && children !== undefined && <div className="mt-3 flex flex-col gap-3">{children}</div>}
      </div>
    </section>
  );
}

/** Whether a part of a step has anything to show: not nothing, not false, not an empty text. */
function shows(node: ReactNode): boolean {
  return node !== undefined && node !== null && node !== false && node !== "";
}

/** Where a step stands, on the rail: a problem's glyph over everything, then chosen, asking, open or waiting. */
function Marker({ state, issues }: { state: StepState; issues: DimensionDraftIssue[] }) {
  if (issues.some((issue) => issue.severity === "error")) {
    return <CircleX className="size-3.5 text-destructive" aria-label="Something to put right here" />;
  }

  if (issues.length > 0) {
    return <TriangleAlert className="size-3.5 text-warning" aria-label="A warning here" />;
  }

  switch (state) {
    case "done":
      return <span className="size-2.5 rounded-full bg-primary" aria-label="Chosen" />;
    case "next":
      return <span className="size-3 rounded-full border-2 border-primary bg-card" aria-label="Asks for a pick" />;
    case "open":
      return <span className="size-2.5 rounded-full border border-input bg-card" aria-label="Nothing chosen, which is fine" />;
    default:
      return <span className="size-2 rounded-full bg-border" aria-label="Waits for a step before it" />;
  }
}

/** A finding of the builder about a part of the dimension: an error stops the YAML from loading, a warning does not. */
export function IssueLine({ issue, testId }: { issue: DimensionDraftIssue; testId?: string }) {
  const error = issue.severity === "error";
  const Icon = error ? CircleX : TriangleAlert;
  return (
    <div
      className="mt-1.5 flex min-w-0 items-start gap-1.5 text-[12px] leading-[18px]"
      data-testid="builder-issue"
      data-severity={issue.severity}
      data-target={issue.target ?? ""}
    >
      <Icon className={cn("mt-0.5 size-3.5 shrink-0", error ? "text-destructive" : "text-warning")} aria-hidden />
      <span className={cn("min-w-0 break-words", error ? "text-destructive" : "text-muted-foreground")} data-testid={testId}>
        {issue.message}
        {issue.code === "template" && (
          <>
            {" "}
            <Link to="/delivery/templates" className="whitespace-nowrap text-primary hover:underline" data-testid="builder-issue-templates">
              Download it on the Templates page
            </Link>
          </>
        )}
      </span>
    </div>
  );
}

/** What a read of OSDU or of the templates had to say at a step: nothing wrong with the draft, worth knowing. */
export function NoteLine({ children, testId }: { children: ReactNode; testId?: string }) {
  return (
    <div className="mt-1.5 flex min-w-0 items-start gap-1.5 text-[12px] leading-[18px] text-muted-foreground" data-testid={testId ?? "builder-note"}>
      <Info className="mt-0.5 size-3.5 shrink-0" aria-hidden />
      <span className="min-w-0 break-words">{children}</span>
    </div>
  );
}

/** The glyph of the button beside each value of a record while a dimension is built, which opens what the value can be. */
export const PICK_ICON: LucideIcon = CirclePlus;

/** The button beside each value of a record while a dimension is built, drawn inside a sentence that says to click it. */
export function PickGlyph() {
  return (
    <span className="mx-0.5 inline-flex size-5 items-center justify-center align-[-5px] text-primary" aria-label="the + beside the value">
      <PICK_ICON className="size-4" aria-hidden />
    </span>
  );
}

/** A choice of the menu beside a value, as a sentence names it. */
export function PickName({ children }: { children: ReactNode }) {
  return <span className="font-medium text-foreground">{children}</span>;
}

/** A field that reads as the text it holds until it is pointed at or typed in: a name, a description, a column's name. */
export function QuietInput({ className, ...props }: ComponentProps<typeof Input>) {
  return (
    <Input
      className={cn(
        "border-transparent bg-transparent shadow-none hover:border-input focus-visible:border-ring dark:bg-transparent",
        className,
      )}
      {...props}
    />
  );
}
