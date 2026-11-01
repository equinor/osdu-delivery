import { CircleAlert, Info, Loader2, ScanSearch } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { cn } from "@/lib/utils";
import { GlyphRef } from "@/components/GlyphRef";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { RunParameterFields } from "@/features/runs/RunParameterFields";
import { splitPath } from "./templateFormat";
import { type FailingFilter, type ValueCheckSession } from "./useValueCheck";
import { ROW_BUDGETS, assertionRows, assertionRowsLine, flowKey, flowLabel, formatCount, outcomeVisual, percent } from "./valueCheck";

const HOW_IT_WORKS = [
  "A dry run reads the rows of a flow that renders with this mapping, on a node, and takes them through everything a",
  "delivery does short of sending them: the same columns, expressions, lookups, cache and searches. Every value written is",
  "held to what the template says of its attribute (type, format, pattern, allowed values, lengths, ranges, what a reference",
  "may point to), every document to the partition's Schema service as well, and where the mapping states assertions",
  "($assert), each value they judge is held to them. Staged records says what became of each record.",
  "\n\nProperties says the same attribute by attribute: the rows held (not delivered), the rows writing a value the template",
  "does not accept, and the rows leaving an attribute out; apart from them, the rows failing an assertion, and how many of",
  "those a failure would hold before they are sent. Pick one of them to narrow the tree to the attributes behind it; select",
  "an attribute and open its Data tab for the reasons and the values behind them, or its Failing records tab to go through",
  "the rows one at a time: what is wrong, what the source holds, and what it rendered to. Nothing is written anywhere.",
].join(" ");

/**
 * The outcomes the summary counts, each narrowing the tree to the attributes it is found for. A failed assertion is
 * counted apart (its rows are also held, invalid, left out or clean by the template) and said only when a row failed
 * one: a mapping may state no assertion at all.
 */
const COUNTED: readonly {
  filter: Exclude<FailingFilter, "any">;
  label: string;
  rows: (summary: NonNullable<ValueCheckSession["summary"]>) => number;
  apart?: boolean;
}[] = [
  { filter: "held", label: "Held", rows: (last) => last.rows.withHeld },
  { filter: "invalid", label: "Invalid", rows: (last) => last.rows.withInvalid },
  { filter: "empty", label: "Left out", rows: (last) => last.rows.withEmpty },
  { filter: "asserted", label: "Assertion failed", rows: (last) => assertionRows(last.rows).failed, apart: true },
];

/** Which rows a check reads: the flow, the values of its scope, and how many. */
export function ValueCheckControls({ session, rowsLabel = "How many rows to read" }: { session: ValueCheckSession; rowsLabel?: string }) {
  if (session.flows.length === 0) {
    return null;
  }

  return (
    <>
      {/* A mapping is read from the one table its flow names, so there is something to pick only when several flows render with it. */}
      {session.flows.length > 1 && (
        <Select value={session.flow === null ? undefined : flowKey(session.flow)} onValueChange={session.chooseFlow}>
          <SelectTrigger
            size="sm"
            className="h-8 max-w-64 min-w-0 flex-[1_1_9rem] font-mono text-[12px]"
            aria-label="The flow whose rows are read"
            title={session.flow === null ? undefined : `Rows of ${flowLabel(session.flow)} (${session.flow.recordObject})`}
            data-testid="value-check-flow"
          >
            <SelectValue placeholder="Pick a flow" />
          </SelectTrigger>
          <SelectContent>
            {session.flows.map((flow) => (
              <SelectItem key={flowKey(flow)} value={flowKey(flow)} className="font-mono text-[12px]">
                {flowLabel(flow)}
                <span className="ml-2 font-sans text-[11px] text-muted-foreground">{flow.recordObject}</span>
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      )}
      <RunParameterFields
        pipelineId={session.flow?.pipelineId ?? null}
        parameters={session.parameters}
        values={session.values}
        onChange={session.setValue}
        lookupValues={session.flow?.partition ? { partition: session.flow.partition } : undefined}
        prefix="value-check"
        layout="inline"
        problems={session.parameterProblems}
        className="w-56 [&_input]:min-w-2"
      />
      <Select value={String(session.maxRows)} onValueChange={(value) => session.setMaxRows(Number(value))}>
        <SelectTrigger size="sm" className="h-8 w-32 text-[12px]" aria-label={rowsLabel} data-testid="value-check-rows">
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          {ROW_BUDGETS.map((budget) => (
            <SelectItem key={budget.value} value={String(budget.value)} className="text-[12px]">{budget.label}</SelectItem>
          ))}
        </SelectContent>
      </Select>
    </>
  );
}

/** The one button that takes the rows picked through everything a delivery does short of sending them, with what that means on hover beside it. */
export function DryRunButton({ session }: { session: ValueCheckSession }) {
  return (
    <span className="ml-auto inline-flex items-center gap-1">
      <GlyphRef icon={Info} title="What a dry run does" body={HOW_IT_WORKS} />
      <RichTooltip body={session.blocked ?? "Take the rows picked through everything a delivery does, short of sending them. Nothing reaches OSDU."} delayDuration={300}>
        {/* A disabled button takes no hover, so its wrapper carries the reason it cannot run. */}
        <span className="inline-flex">
          <Button
            type="button"
            size="sm"
            disabled={!session.ready || session.checkingAll}
            onClick={() => session.check(null)}
            data-testid="value-check-all"
          >
            {session.checkingAll ? <Loader2 className="animate-spin" /> : <ScanSearch />}
            {session.checkingAll ? "Rendering" : "Dry run"}
          </Button>
        </span>
      </RichTooltip>
    </span>
  );
}

/**
 * What the latest check found, by attribute, on one line above the tree: how many rows, then how many are held, write an
 * invalid value, leave an attribute out or fail an assertion of the mapping, each a button narrowing the tree to the attributes
 * behind it, and how many are clean. Nothing until a check has run: what is under way, or why none can, is said once for the
 * whole page, over its tabs.
 */
export function ValueCheckBar({ session }: { session: ValueCheckSession }) {
  const last = session.summary;
  if (last === null) {
    return null;
  }

  const narrow = (filter: FailingFilter) => session.setFilter(session.filter === filter ? null : filter);
  const rows = last.rows;
  const assertions = assertionRows(rows);
  return (
    <div className="flex flex-wrap items-center gap-x-1.5 gap-y-1 text-[12px]" data-testid="value-check-summary">
      <span className="mr-1 text-muted-foreground">
        {last.targets === null ? "" : <span className="font-mono">{`${last.targets.map((target) => splitPath(target).leaf).join(", ")} · `}</span>}
        <span className="font-mono text-foreground tabular-nums">{formatCount(rows.checked)}</span>
        {rows.complete ? " rows, the whole scope" : ` of about ${formatCount(rows.scopeRecords ?? rows.read)} rows`}
        {" · "}
        <RelativeTime value={last.checkedUtc} />
      </span>
      {COUNTED.map(({ filter, label, rows: count, apart = false }) => {
        const visual = outcomeVisual(filter);
        const Icon = visual.icon;
        const n = count(last);
        const active = session.filter === filter;
        if (apart && n === 0 && !active) {
          return null;
        }

        // A failed assertion says first how many rows failed one and how many of them it would hold.
        const held = filter === "asserted" ? assertions.held : 0;
        const said = filter === "asserted" ? `${assertionRowsLine(rows) ?? ""}. ` : "";
        return (
          <button
            key={filter}
            type="button"
            disabled={n === 0 && !active}
            onClick={() => narrow(filter)}
            aria-pressed={active}
            title={n === 0 ? `No row is ${label.toLowerCase()}` : `${said}${visual.meaning} Show the attributes it is found for.`}
            className={cn(
              "inline-flex items-center gap-1 rounded-full border px-2 py-0.5 outline-none transition-colors focus-visible:ring-2 focus-visible:ring-ring/50",
              n === 0 ? "border-transparent text-muted-foreground" : "hover:bg-accent/60",
              active && "border-primary/50 bg-accent",
            )}
            data-testid={`value-check-summary-${filter}`}
          >
            <Icon className={cn("size-3.5", n === 0 ? "opacity-50" : visual.textClass)} />
            {label}
            <span className="font-mono tabular-nums">{formatCount(n)}</span>
            {held > 0 && <span className="text-muted-foreground" data-testid="value-check-summary-asserted-held">{`· ${formatCount(held)} to be held`}</span>}
          </button>
        );
      })}
      <span
        className={cn("inline-flex items-center gap-1 px-2 py-0.5", rows.clean > 0 ? "text-success" : "text-muted-foreground")}
        title={`Rows every attribute checked gives a value the template accepts, or one the mapping means it not to have${assertions.failed > 0 ? ", and that fail no assertion of the mapping" : ""}`}
        data-testid="value-check-summary-clean"
      >
        Clean
        <span className="font-mono tabular-nums">{formatCount(rows.clean)}</span>
        <span className="text-muted-foreground">{`(${percent(rows.clean, rows.checked)})`}</span>
      </span>
      {rows.keyless > 0 && (
        <GlyphRef
          icon={CircleAlert}
          title="Rows that cannot be tracked"
          label={`${formatCount(rows.keyless)} keyless`}
          body={`${formatCount(rows.keyless)} rows have an empty part of the record key, so their records cannot be tracked and a run holds them.`}
        />
      )}
      {rows.passedOver > 0 && (
        <GlyphRef
          icon={Info}
          title="Rows passed over"
          label={`${formatCount(rows.passedOver)} passed over`}
          body={rows.passedOverWhy.map((why) => `${formatCount(why.count)}: ${why.reason}`).join("\n")}
        />
      )}
      {last.issues.length > 0 && (
        <GlyphRef icon={CircleAlert} title="The mapping's preflight warnings" label={`${last.issues.length} warnings`} body={last.issues.join("\n")} />
      )}
    </div>
  );
}
