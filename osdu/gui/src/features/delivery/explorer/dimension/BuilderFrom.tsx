import { useId } from "react";
import { RotateCcw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { CopyButton } from "@/components/CopyButton";
import type { DimensionDraftIssue } from "../../../../api/explorer";
import { kindTypeName, type StepState } from "./dimensionStory";
import { StoryFrame } from "./StoryFrame";

/**
 * The first step of a builder's story, the dimension's and the cached type's alike: the records it is built from. Their
 * kind, every version of the type or the one of the record in view, and a query narrowing them; and, where the explorer
 * has moved to another type since, a way to start over from that one.
 */
export function RecordsStep({
  state, kind, query, total, recordKind, issues, open, onToggle, onKind, onQuery, startKind, onStartElsewhere, hint, queryHelp,
}: {
  state: StepState;
  /** The kind read, wildcards allowed per segment; empty while none is picked. */
  kind: string;
  query: string | null;
  /** How many records the kind (and the query) holds, once counted. */
  total: number | undefined;
  /** The kind of the record in view, whose version alone can be read; null for none. */
  recordKind: string | null;
  issues: DimensionDraftIssue[];
  open: boolean;
  onToggle: () => void;
  onKind: (kind: string) => void;
  onQuery: (query: string | null) => void;
  /** The kind the explorer is at now: offered to start over from when it is another. */
  startKind: string;
  onStartElsewhere: (kind: string) => void;
  /** What the step asks for while no kind is picked. */
  hint: string;
  /** What the query is read as, under its field. */
  queryHelp: string;
}) {
  const ids = useId();
  const segments = kind.split(":");
  const everyVersion = segments.length === 4 && segments[3] === "*";
  const facts = kind === ""
    ? null
    : [
      segments.length === 4 ? (everyVersion ? "every version" : `version ${segments[3]}`) : null,
      total === undefined ? null : `${total.toLocaleString("en-US")} ${total === 1 ? "record" : "records"}`,
      query === null ? null : "narrowed by a query",
    ].filter((fact) => fact !== null).join(" · ");
  const elsewhere = startKind !== "" && startKind !== kind ? startKind : null;

  return (
    <StoryFrame
      step="from"
      state={state}
      lead="From"
      answer={kind === ""
        ? <span className="font-normal text-muted-foreground">the records of one type</span>
        : <><span data-testid="builder-kind">{kindTypeName(kind)}</span> records</>}
      facts={facts}
      issues={issues}
      open={open}
      onToggle={kind === "" ? undefined : onToggle}
      hint={state === "next" ? hint : undefined}
    >
      <div className="flex min-w-0 items-center gap-1">
        <span className="min-w-0 truncate font-mono text-[12px]" title={kind}>{kind}</span>
        <CopyButton iconOnly label="Copy the kind" text={kind} testId="builder-kind-copy" />
      </div>
      {segments.length === 4 && (
        <div className="flex items-start gap-2.5">
          <Switch
            id={`${ids}-versions`}
            checked={everyVersion}
            onCheckedChange={(checked) => {
              if (checked) {
                onKind([...segments.slice(0, 3), "*"].join(":"));
              } else if (recordKind !== null) {
                onKind(recordKind);
              }
            }}
            disabled={everyVersion && recordKind === null}
            className="mt-0.5"
            data-testid="builder-every-version"
          />
          <Label htmlFor={`${ids}-versions`} className="flex flex-col items-start gap-0.5 text-[12.5px] font-normal">
            Every version of the type
            <span className="text-[12px] text-muted-foreground">
              {everyVersion
                ? recordKind === null ? "Open a record to read its version alone." : `Off, only ${recordKind.split(":")[3]}, the open record's version.`
                : `Only ${segments[3]} is read.`}
            </span>
          </Label>
        </div>
      )}
      <div className="flex flex-col gap-1">
        <Label htmlFor={`${ids}-query`} className="text-[12.5px] font-normal">Narrow the records</Label>
        <Input
          id={`${ids}-query`}
          value={query ?? ""}
          onChange={(event) => onQuery(event.target.value === "" ? null : event.target.value)}
          className="h-8 font-mono text-[12px] md:text-[12px]"
          placeholder={'a search query, such as data.Source:"CPI"'}
          data-testid="builder-query"
        />
        <span className="text-[12px] text-muted-foreground">{queryHelp}</span>
      </div>
      {elsewhere !== null && (
        <div>
          <Button variant="outline" size="sm" onClick={() => onStartElsewhere(elsewhere)} data-testid="builder-start-elsewhere">
            <RotateCcw />
            {`Start over from ${kindTypeName(elsewhere)} records`}
          </Button>
        </div>
      )}
    </StoryFrame>
  );
}
