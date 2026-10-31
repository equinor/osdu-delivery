import { useState } from "react";
import { ChevronRight, CircleCheck, Download, Eye, Info, ListPlus, Loader2, ScanSearch } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { downloadFileName } from "@/lib/download";
import { cn } from "@/lib/utils";
import { CopyButton } from "@/components/CopyButton";
import { DataTable, type Column } from "@/components/DataTable";
import { GlyphRef } from "@/components/GlyphRef";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { OutcomePill } from "@/components/StatusBadge";
import { TruncatedText } from "@/components/TruncatedText";
import {
  type DeliveryValueCheckCounts,
  type DeliveryValueCheckFinding,
  type DeliveryValueCheckSample,
} from "../../api/delivery";
import { AssertionActionBadge } from "./AssertionFindingsView";
import { ExportRecordsButton } from "./ExportRecordsButton";
import { splitPath } from "./templateFormat";
import { useValueCheckContext, type ValueCheckSession, type VariableCheck } from "./useValueCheck";
import {
  ASSERTED,
  FINDING_OUTCOMES,
  OUTCOMES,
  assertedOf,
  compactCount,
  countOf,
  failingOf,
  findingLabel,
  flowLabel,
  formatCount,
  needsLook,
  outcomeVisual,
  percent,
  sampleOrigin,
  samplesCsv,
  worstFailing,
  worstOf,
  type OutcomeKey,
  type OutcomeVisual,
} from "./valueCheck";

/**
 * A variable's rows as one bar, a segment per outcome worst first, with the legend under it: each outcome some row came
 * to, with its glyph, word, count and share, so no outcome is told by its color alone. A legend entry picks that outcome's findings when
 * `onSelect` is given; a segment says the same on hover. The rows failing an assertion of the mapping are a legend entry
 * of their own past a rule, with no segment: they are among the segments already, held, invalid, empty or valid.
 */
export function OutcomeMeter({
  counts, unit, selected, onSelect, compact = false, testId,
}: {
  counts: DeliveryValueCheckCounts;
  /** What is counted: rows, or items of a repeated array. */
  unit: string;
  selected?: OutcomeKey | null;
  onSelect?: (outcome: OutcomeKey | null) => void;
  /** The bar alone, for a list of variables, the counts on its hover. */
  compact?: boolean;
  testId: string;
}) {
  const total = counts.total;
  const shown = OUTCOMES.filter((outcome) => counts[outcome.key] > 0);
  // The outcome picked stays in the legend when the count switched to has none of it, so it can be put down again.
  const listed = OUTCOMES.filter((outcome) => counts[outcome.key] > 0 || outcome.key === selected);
  const asserted = assertedOf(counts) > 0 || selected === "asserted";
  const bar = (
    <div
      className={cn("flex w-full gap-[2px] overflow-hidden rounded-full bg-muted", compact ? "h-1.5" : "h-2")}
      role="img"
      aria-label={shown.map((outcome) => `${formatCount(counts[outcome.key])} ${outcome.label.toLowerCase()}`).join(", ")}
      data-testid={testId}
    >
      {shown.map((outcome) => (
        <Tooltip key={outcome.key} delayDuration={150}>
          <TooltipTrigger asChild>
            <div
              className={cn("h-full", outcome.barClass)}
              // A share too small to see still gets a sliver, so an outcome that happened never vanishes from the bar.
              style={{ flex: `${counts[outcome.key]} 1 0`, minWidth: 3 }}
              data-testid={`${testId}-${outcome.key}`}
            />
          </TooltipTrigger>
          <TooltipContent>{`${outcome.label}: ${formatCount(counts[outcome.key])} ${unit} (${percent(counts[outcome.key], total)})`}</TooltipContent>
        </Tooltip>
      ))}
    </div>
  );

  if (compact) {
    return bar;
  }

  const entry = (outcome: OutcomeVisual) => {
    const count = countOf(counts, outcome.key);
    const Icon = outcome.icon;
    const active = selected === outcome.key;
    const body = (
      <>
        <Icon className={cn("size-3.5 shrink-0", count === 0 ? "text-muted-foreground/50" : outcome.textClass)} />
        <span className={cn("text-[12px]", count === 0 && "text-muted-foreground")}>{outcome.label}</span>
        <span className="font-mono text-[12px] tabular-nums">{formatCount(count)}</span>
        <span className="text-[11px] text-muted-foreground tabular-nums">{percent(count, total)}</span>
      </>
    );
    return (
      <RichTooltip key={outcome.key} body={outcome.meaning} title={outcome.label} delayDuration={500}>
        {onSelect === undefined || (count === 0 && !active)
          ? <span className="inline-flex items-center gap-1.5 rounded-md px-1.5 py-0.5" data-testid={`${testId}-legend-${outcome.key}`}>{body}</span>
          : (
            <button
              type="button"
              onClick={() => onSelect(active ? null : outcome.key)}
              aria-pressed={active}
              className={cn(
                "inline-flex items-center gap-1.5 rounded-md px-1.5 py-0.5 outline-none transition-colors hover:bg-accent/60 focus-visible:ring-2 focus-visible:ring-ring/50",
                active && "bg-accent ring-1 ring-primary/40",
              )}
              data-testid={`${testId}-legend-${outcome.key}`}
            >
              {body}
            </button>
          )}
      </RichTooltip>
    );
  };

  return (
    <div className="flex flex-col gap-2">
      {bar}
      <div className="flex flex-wrap gap-x-1 gap-y-1" role={onSelect === undefined ? undefined : "group"} aria-label="Outcomes">
        {listed.map(entry)}
        {asserted && (
          <>
            {listed.length > 0 && <span aria-hidden className="mx-1 w-px self-stretch bg-border" />}
            {entry(ASSERTED)}
          </>
        )}
      </div>
    </div>
  );
}

/**
 * What a check found of a variable, on its row of the tree: the worst outcome's glyph and how many rows will not give it
 * an expected value, then the rows failing an assertion of the mapping, the whole count on hover; a group, the worst of
 * what it holds; a variable being checked, a spinner. A variable every row gives an expected value, failing no assertion,
 * draws nothing, so the tree after a check shows where to look and nothing else; that it was checked is still said to
 * assistive tech.
 */
export function ValueCheckChip({ path }: { path: string }) {
  const session = useValueCheckContext();
  if (session === null) {
    return null;
  }

  if (session.isChecking(path)) {
    return (
      <span className="inline-flex shrink-0 text-muted-foreground" role="img" aria-label="Checking values" title="Checking values" data-testid={`value-check-chip-${path}`}>
        <Loader2 className="size-3 animate-spin" />
      </span>
    );
  }

  const own = session.resultFor(path);
  if (own !== undefined) {
    return <CountChip counts={own.variable.rows} unit="rows" path={path} />;
  }

  const nested = session.nestedFor(path);
  if (nested.length > 0) {
    const worst = FINDING_OUTCOMES.find((outcome) => nested.some((n) => n.finding.outcome === outcome.key)) ?? outcomeVisual("valid");
    const occurrences = nested.filter((n) => n.finding.outcome !== "notApplicable").reduce((sum, n) => sum + n.finding.count, 0);
    const Icon = worst.icon;
    return (
      <span
        role="img"
        aria-label={`${formatCount(occurrences)} values found wrong while checking ${nested[0].from.variable.target}`}
        title={`${formatCount(occurrences)} values found wrong while checking ${nested[0].from.variable.target}`}
        className={cn("inline-flex shrink-0 items-center gap-0.5 rounded-sm px-1 font-mono text-[10px] leading-4 tabular-nums", worst.chipClass)}
        data-testid={`value-check-chip-${path}`}
        data-outcome={worst.key}
      >
        <Icon className="size-3" />
        {compactCount(occurrences)}
      </span>
    );
  }

  const inside = session.inside(path);
  if (inside.length > 0) {
    const failing = inside.filter((result) => needsLook(result.variable.rows));
    if (failing.length === 0) {
      return <Quiet path={path} said={`Every row gives the ${inside.length} checked attributes inside an expected value`} />;
    }

    const worst = worstFailing((key) => inside.some((result) => countOf(result.variable.rows, key) > 0)) ?? outcomeVisual("valid");
    const Icon = worst.icon;
    const asserting = failing.some((result) => assertedOf(result.variable.rows) > 0);
    const said = `${failing.length} of the ${inside.length} checked attributes inside have rows that will not give an expected value`
      + (asserting ? ", or that fail an assertion of the mapping" : "");
    return (
      <span role="img" aria-label={said} title={said} className={cn("inline-flex shrink-0", worst.textClass)} data-testid={`value-check-chip-${path}`} data-outcome={worst.key}>
        <Icon className="size-3" />
      </span>
    );
  }

  return null;
}

/** A clean result, said to assistive tech and drawn as nothing. */
function Quiet({ path, said }: { path: string; said: string }) {
  return <span className="sr-only" data-testid={`value-check-chip-${path}`} data-outcome="valid">{said}</span>;
}

/**
 * A variable's own count: the rows that will not give it an expected value, on a chip in the worst outcome's tone, and the
 * rows failing an assertion of the mapping after it, the glyph alone in its tone. The two are never added, since a row
 * can be in both.
 */
function CountChip({ counts, unit, path }: { counts: DeliveryValueCheckCounts; unit: string; path: string }) {
  const failing = failingOf(counts);
  const asserted = assertedOf(counts);
  const said = `${formatCount(counts.held)} ${unit} held, ${formatCount(counts.invalid)} invalid, ${formatCount(counts.empty)} empty, `
    + `${formatCount(counts.valid)} valid, ${formatCount(counts.notApplicable)} not applicable, of ${formatCount(counts.total)} checked`
    + (asserted > 0 ? `; ${formatCount(asserted)} ${unit} failing an assertion of the mapping` : "");
  if (failing === 0 && asserted === 0) {
    return <Quiet path={path} said={said} />;
  }

  const worst = failing > 0 ? outcomeVisual(worstOf(counts) ?? "valid") : ASSERTED;
  const Icon = worst.icon;
  const AssertedIcon = ASSERTED.icon;
  return (
    <span
      role="img"
      aria-label={said}
      title={said}
      className="inline-flex shrink-0 items-center gap-1 font-mono text-[10px] leading-4 tabular-nums"
      data-testid={`value-check-chip-${path}`}
      data-outcome={worst.key}
      data-asserted={asserted > 0 ? asserted : undefined}
    >
      {failing > 0 && (
        <span className={cn("inline-flex items-center gap-0.5 rounded-sm px-1", worst.chipClass)}>
          <Icon className="size-3" />
          {compactCount(failing)}
        </span>
      )}
      {asserted > 0 && (
        <span className="inline-flex items-center gap-0.5 text-muted-foreground">
          <AssertedIcon className={cn("size-3", ASSERTED.textClass)} />
          {compactCount(asserted)}
        </span>
      )}
    </span>
  );
}

/**
 * The button that checks one variable's values, for the header of its properties: what the check of it reads (the
 * variable and everything inside it) is said on its hover, and why it cannot run when it cannot. `onCheck` lets the
 * properties turn to where the answer will land.
 */
export function CheckValuesButton({ path, checkable, onCheck }: { path: string; checkable: boolean; onCheck?: () => void }) {
  const session = useValueCheckContext();
  if (session === null) {
    return null;
  }

  const checking = session.isChecking(path);
  const why = !checkable
    ? "Nothing of the mapping fills this attribute, so there are no values to check."
    : session.blocked ?? `Render ${session.flow === null ? "the flow's" : `${flowLabel(session.flow)}'s`} rows and find the ones that will not give this attribute (and what it holds) the value its template expects.`;
  const checked = session.resultFor(path) !== undefined;
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        {/* A disabled button takes no hover, so its wrapper carries the reason it cannot run. */}
        <span className="inline-flex shrink-0">
          <Button
            type="button"
            size="xs"
            variant={checked ? "outline" : "default"}
            disabled={!checkable || !session.ready || checking}
            onClick={() => {
              session.check([path]);
              onCheck?.();
            }}
            data-testid="value-check-attribute"
          >
            {checking ? <Loader2 className="animate-spin" /> : <ScanSearch />}
            {checking ? "Checking" : checked ? "Check again" : "Check values"}
          </Button>
        </span>
      </TooltipTrigger>
      <TooltipContent className="max-w-80">{why}</TooltipContent>
    </Tooltip>
  );
}

/**
 * What the Data tab's label carries: a spinner while a check reaching the variable runs, and how many rows (or, for an
 * object, attributes inside) a check found failing, in the worst outcome's tone; for a variable whose rows only fail an
 * assertion of the mapping, how many do. Nothing when there is nothing wrong.
 */
export function DataTabBadge({ path }: { path: string }) {
  const session = useValueCheckContext();
  if (session === null) {
    return null;
  }

  if (session.isChecking(path)) {
    return <Loader2 className="size-3 animate-spin text-muted-foreground" aria-label="Checking values" data-testid="value-check-tab-busy" />;
  }

  const own = session.resultFor(path);
  const nested = session.nestedFor(path).filter((n) => n.finding.outcome !== "notApplicable");
  const inside = own === undefined ? session.inside(path).filter((result) => needsLook(result.variable.rows)) : [];
  const failing = own === undefined ? 0 : failingOf(own.variable.rows);
  const asserted = own === undefined ? 0 : assertedOf(own.variable.rows);
  const count = own !== undefined
    ? (failing > 0 ? failing : asserted)
    : nested.length > 0 ? nested.reduce((sum, n) => sum + n.finding.count, 0) : inside.length;
  if (count === 0) {
    return null;
  }

  const worst = own !== undefined
    ? (failing > 0 ? outcomeVisual(worstOf(own.variable.rows) ?? "valid") : ASSERTED)
    : worstFailing((key) => nested.some((n) => n.finding.outcome === key)
      || inside.some((result) => countOf(result.variable.rows, key) > 0)) ?? outcomeVisual("valid");
  const said = own !== undefined
    ? [
      failing > 0 ? `${formatCount(failing)} rows will not give an expected value` : null,
      asserted > 0 ? `${formatCount(asserted)} rows fail an assertion of the mapping` : null,
    ].filter((part): part is string => part !== null).join("; ")
    : nested.length > 0 ? `${formatCount(count)} values found wrong` : `${count} attributes inside have rows that fail`;
  return (
    <span
      className={cn("rounded-full px-1.5 font-mono text-[10px] leading-4 tabular-nums", worst.chipClass)}
      title={said}
      aria-label={said}
      data-testid="value-check-tab-count"
    >
      {compactCount(count)}
    </span>
  );
}

/**
 * A variable's Data tab: what the checks found of it, or, before any check reached it, what a check would tell and
 * what stands in its way. The check itself is the button at the top of the properties, the one place it is started.
 * `onRecord` opens a record the check lists in the Failing records tab.
 */
export function VariableDataTab({ path, checkable, onRecord }: { path: string; checkable: boolean; onRecord: OpenRecord }) {
  const session = useValueCheckContext();
  if (session === null) {
    return null;
  }

  if (session.resultFor(path) !== undefined || session.nestedFor(path).length > 0 || session.inside(path).length > 0) {
    return <VariableValueCheck path={path} onRecord={onRecord} />;
  }

  const checking = session.isChecking(path);
  const said = checking
    ? `Rendering the rows of ${session.flow === null ? "the flow" : flowLabel(session.flow)} on a node.`
    : !checkable
      ? "Nothing of the mapping fills this attribute, so no row has a value of it to check."
      : session.blocked
        ?? "Check values renders the rows of the flow picked above as a delivery would, and lists the ones that will not give this attribute the value its template expects (held, written with a value the template does not accept, or left out) and the ones whose value fails an assertion the mapping states.";
  return (
    <div className="flex flex-col items-start gap-2 rounded-md border border-dashed px-3 py-4" data-testid="value-check-unchecked">
      <span className="inline-flex items-center gap-1.5 text-[13px] font-medium">
        {checking ? <Loader2 className="size-4 animate-spin text-muted-foreground" /> : <ScanSearch className="size-4 text-muted-foreground" />}
        {checking ? "Checking" : "Not checked yet"}
      </span>
      <p className="text-[12px] text-muted-foreground">{said}</p>
    </div>
  );
}

/** Opens a record a check lists for a reason in the Failing records tab. */
export type OpenRecord = (key: string, finding: DeliveryValueCheckFinding) => void;

/**
 * What the checks found of one variable, in its properties: the rows of it by outcome, then the rows that will not give
 * it an expected value and those failing an assertion of the mapping, grouped by reason, each with the values behind it
 * and the records it holds for, and last the values it was written with. A variable found wrong only inside a value
 * another entry writes whole shows what that check found at it; a group shows the attributes inside it.
 */
export function VariableValueCheck({ path, onRecord }: { path: string; onRecord: OpenRecord }) {
  const session = useValueCheckContext();
  if (session === null) {
    return null;
  }

  const own = session.resultFor(path);
  const nested = session.nestedFor(path);
  const inside = own === undefined ? session.inside(path) : [];
  if (own === undefined && nested.length === 0 && inside.length === 0) {
    return null;
  }

  return (
    <section className="flex flex-col gap-4" aria-label="Values" data-testid="value-check-variable">
      {own !== undefined && <VariableResult check={own} session={session} onRecord={onRecord} />}
      {nested.length > 0 && (
        <div className="flex flex-col gap-2" data-testid="value-check-nested">
          <h4 className="text-[13px] font-medium">
            Found while checking <span className="font-mono text-[12px]">{nested[0].from.variable.target}</span>
          </h4>
          {nested.map(({ finding, from }) => (
            <FindingCard
              key={`${from.variable.target}:${finding.outcome}:${finding.message}`}
              target={from.variable.target}
              finding={finding}
              total={from.variable.rows.total}
              unit="values"
              session={session}
              onRecord={onRecord}
            />
          ))}
        </div>
      )}
      {inside.length > 0 && <InsideSummary results={inside} session={session} />}
    </section>
  );
}

function VariableResult({ check, session, onRecord }: { check: VariableCheck; session: ValueCheckSession; onRecord: OpenRecord }) {
  const { variable, meta } = check;
  const [unit, setUnit] = useState<"rows" | "items">("rows");
  const [only, setOnly] = useState<OutcomeKey | null>(null);
  const [showNotApplicable, setShowNotApplicable] = useState(false);
  const [showValues, setShowValues] = useState(false);
  const counts = unit === "items" && variable.items !== null && variable.items !== undefined ? variable.items : variable.rows;
  const failures = variable.findings.filter((finding) => finding.outcome !== "notApplicable" && (only === null || finding.outcome === only));
  const notApplicable = variable.findings.filter((finding) => finding.outcome === "notApplicable");
  const failing = failingOf(variable.rows);
  const asserted = assertedOf(variable.rows);

  return (
    <div className="flex flex-col gap-3" data-testid="value-check-result">
      <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-[12px] text-muted-foreground">
        <span>
          <span className="font-mono tabular-nums">{formatCount(meta.rows.checked)}</span>
          {" rows of "}
          <span className="font-mono">{flowLabel(meta.flow)}</span>
        </span>
        {!meta.rows.complete && (
          <GlyphRef
            icon={Info}
            title="Rows read"
            label="first rows only"
            body={`The check read the first ${formatCount(meta.rows.read)} rows of the scope${meta.rows.scopeRecords ? `, which holds about ${formatCount(meta.rows.scopeRecords)}` : ""}. Pick a larger row budget, or the whole scope, to read them all.`}
          />
        )}
        <span>&middot;</span>
        <RelativeTime value={meta.checkedUtc} />
        {variable.items !== null && variable.items !== undefined && (
          <span className="ml-auto inline-flex overflow-hidden rounded-md border" role="group" aria-label="Count">
            {(["rows", "items"] as const).map((choice) => (
              <button
                key={choice}
                type="button"
                onClick={() => setUnit(choice)}
                aria-pressed={unit === choice}
                className={cn("px-2 py-0.5 text-[11px] transition-colors", unit === choice ? "bg-accent text-foreground" : "hover:bg-accent/50")}
                data-testid={`value-check-unit-${choice}`}
              >
                {choice === "rows" ? "Rows" : "Items"}
              </button>
            ))}
          </span>
        )}
      </div>

      <OutcomeMeter counts={counts} unit={unit} selected={only} onSelect={setOnly} testId="value-check-meter" />

      {failing === 0 && asserted === 0
        ? (
          <p className="flex items-center gap-1.5 text-[13px] text-success" data-testid="value-check-clean">
            <CircleCheck className="size-4 shrink-0" />
            Every row checked gives this attribute a value the template accepts, or one the mapping means it not to have.
          </p>
        )
        : (
          <div className="flex flex-col gap-2" data-testid="value-check-findings">
            <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
              {/* The two counts stand side by side and are never added: a row can fail an assertion and the template both. */}
              <h4 className="flex min-w-0 flex-wrap items-baseline gap-x-1.5 text-[13px] font-medium">
                {failing > 0 && (
                  <span>
                    {"Rows that will not give an expected value "}
                    <span className="font-mono text-[12px] font-normal text-muted-foreground tabular-nums">{formatCount(failing)}</span>
                  </span>
                )}
                {failing > 0 && asserted > 0 && <span aria-hidden className="font-normal text-muted-foreground">&middot;</span>}
                {asserted > 0 && (
                  <span className={cn(failing > 0 && "font-normal")} data-testid="value-check-asserted-rows">
                    {failing > 0 ? "failing an assertion " : "Rows failing an assertion of the mapping "}
                    <span className="font-mono text-[12px] font-normal text-muted-foreground tabular-nums">{formatCount(asserted)}</span>
                  </span>
                )}
              </h4>
              <span className="ml-auto">
                <ExportRecordsButton
                  fetchWorkbook={() => session.exportIssues(check)}
                  fileName={downloadFileName([meta.flow.flow, meta.flow.interface, meta.flow.partition, splitPath(variable.target).leaf, "issues"], "xlsx", "issues")}
                  unavailable={session.blocked}
                  title="Save the rows failing this attribute as an Excel workbook: each with the file and row it came from, the reason, and what the row holds in the source columns the attribute reads. The rows are read again on a node, as this check read them."
                  label="Export rows"
                  what="the rows"
                  testId="value-check-export"
                />
              </span>
            </div>
            {failures.length === 0 && (
              <p className="text-[12px] text-muted-foreground">No finding of the outcome picked; pick it again in the legend to show them all.</p>
            )}
            {failures.map((finding) => (
              <FindingCard
                key={`${finding.outcome}:${finding.at}:${finding.message}`}
                target={variable.target}
                finding={finding}
                total={variable.repeater ? (variable.items?.total ?? variable.rows.total) : variable.rows.total}
                unit={variable.repeater ? "items" : "rows"}
                session={session}
                onRecord={onRecord}
              />
            ))}
            {variable.unlisted > 0 && (
              <p className="text-[12px] text-muted-foreground" data-testid="value-check-unlisted">
                {`${formatCount(variable.unlisted)} more occurrences fall under reasons not listed here. `}
                <span className="font-medium">Export rows</span> lists every one.
              </p>
            )}
          </div>
        )}

      {notApplicable.length > 0 && (
        <div className="flex flex-col gap-2">
          <button
            type="button"
            onClick={() => setShowNotApplicable((open) => !open)}
            className="inline-flex items-center gap-1 self-start text-[12px] text-muted-foreground hover:text-foreground"
            aria-expanded={showNotApplicable}
            data-testid="value-check-not-applicable-toggle"
          >
            <ChevronRight className={cn("size-3.5 transition-transform", showNotApplicable && "rotate-90")} />
            {`Not applicable: ${formatCount(variable.rows.notApplicable)} rows the mapping means no value for`}
          </button>
          {showNotApplicable && notApplicable.map((finding) => (
            <FindingCard
              key={`${finding.outcome}:${finding.at}:${finding.message}`}
              target={variable.target}
              finding={finding}
              total={variable.rows.total}
              unit="rows"
              session={session}
              onRecord={onRecord}
            />
          ))}
        </div>
      )}

      {variable.values.length > 0 && (
        <div className="flex flex-col gap-2">
          <button
            type="button"
            onClick={() => setShowValues((open) => !open)}
            className="inline-flex items-center gap-1 self-start text-[12px] text-muted-foreground hover:text-foreground"
            aria-expanded={showValues}
            data-testid="value-check-values-toggle"
          >
            <ChevronRight className={cn("size-3.5 transition-transform", showValues && "rotate-90")} />
            {`Values written: ${formatCount(variable.distinctValues)}${variable.moreValues ? "+" : ""} distinct, the most frequent first`}
          </button>
          {showValues && (
            <ul className="flex flex-col gap-0.5 pl-5" data-testid="value-check-values">
              {variable.values.map((value) => (
                <li key={value.value} className="flex min-w-0 items-center gap-2 text-[12px]">
                  <TruncatedText text={value.value} mono maxWidth={320} />
                  <span className="ml-auto shrink-0 font-mono text-muted-foreground tabular-nums">{formatCount(value.count)}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      {meta.notes.map((note) => (
        <p key={note} className="text-[12px] text-muted-foreground">{note}</p>
      ))}
    </div>
  );
}

/**
 * One reason rows will not give a variable an expected value, or one assertion of the mapping its values fail: the outcome
 * (with the template's rule or the assertion's name), how many and what share, the reason as the first row states it (for
 * an assertion, what its failure does), the values behind it, and, opened, the records it holds for, paged from the node
 * as far as they go.
 */
function FindingCard({
  target, finding, total, unit, session, onRecord,
}: {
  /** The variable checked, whose check pages the finding. */
  target: string;
  finding: DeliveryValueCheckFinding;
  total: number;
  unit: string;
  session: ValueCheckSession;
  onRecord: OpenRecord;
}) {
  const [open, setOpen] = useState(false);
  const visual = outcomeVisual(finding.outcome);
  const label = findingLabel(finding);
  const shownValues = finding.values.slice(0, 8);
  const moreValues = finding.values.length - shownValues.length;
  const leaf = finding.at === target ? null : finding.at;

  return (
    <div className="rounded-md border bg-card" data-testid="value-check-finding" data-outcome={finding.outcome}>
      <button
        type="button"
        onClick={() => setOpen((was) => !was)}
        aria-expanded={open}
        className="flex w-full items-start gap-2 rounded-md px-2.5 py-2 text-left outline-none transition-colors hover:bg-accent/40 focus-visible:ring-2 focus-visible:ring-ring/50"
        data-testid="value-check-finding-toggle"
      >
        <ChevronRight className={cn("mt-0.5 size-4 shrink-0 text-muted-foreground transition-transform", open && "rotate-90")} />
        <span className="flex min-w-0 flex-1 flex-col gap-1">
          <span className="flex flex-wrap items-center gap-2">
            <OutcomePill tone={visual.tone} label={label} icon={visual.icon} testId="value-check-finding-outcome" />
            {finding.onFail != null && <AssertionActionBadge action={finding.onFail} className="text-[11px]" />}
            <span className="font-mono text-[12px] tabular-nums" data-testid="value-check-finding-count">
              {`${formatCount(finding.count)} ${unit}`}
            </span>
            {/* A share of rows counts each row once, however many of its values the reason holds for. */}
            <span className="text-[11px] text-muted-foreground tabular-nums">{percent(unit === "rows" ? finding.rows : finding.count, total)}</span>
            {finding.rows !== finding.count && (
              <span className="text-[11px] text-muted-foreground">{`in ${formatCount(finding.rows)} rows`}</span>
            )}
            {leaf !== null && <span className="truncate font-mono text-[11px] text-muted-foreground">at {splitPath(leaf).leaf}</span>}
          </span>
          <span className="line-clamp-2 text-[13px] break-words" title={finding.message}>{finding.message}</span>
        </span>
      </button>

      {shownValues.length > 0 && (
        <div className="flex flex-wrap items-center gap-1 px-2.5 pb-2 pl-8" data-testid="value-check-finding-values">
          {shownValues.map((value) => (
            <RichTooltip key={value.value} body={value.value} title="Value" mono>
              <span className="inline-flex max-w-56 items-center gap-1 rounded-sm bg-muted px-1.5 py-0.5 font-mono text-[11px]">
                <span className="truncate">{value.value === "" ? "(empty text)" : value.value}</span>
                <span className="shrink-0 text-muted-foreground tabular-nums">{`×${compactCount(value.count)}`}</span>
              </span>
            </RichTooltip>
          ))}
          {(moreValues > 0 || finding.otherValues > 0) && (
            <span className="text-[11px] text-muted-foreground">
              {`+ ${formatCount(finding.otherValues + finding.values.slice(8).reduce((sum, v) => sum + v.count, 0))} with other values`}
            </span>
          )}
        </div>
      )}

      {open && <FindingRecords target={target} finding={finding} unit={unit} session={session} onRecord={onRecord} />}
    </div>
  );
}

interface SampleRow {
  index: number;
  sample: DeliveryValueCheckSample;
}

/**
 * The records a finding holds for, as far as they have been listed: the record's key and label, the file and row it came
 * from, the item, the value behind the reason and the reason as that record states it, each opening in the Failing records
 * tab, here beside the tree. The next page is read from the node on request; the loaded ones copy as keys or save as CSV.
 */
function FindingRecords({
  target, finding, unit, session, onRecord,
}: {
  target: string;
  finding: DeliveryValueCheckFinding;
  unit: string;
  session: ValueCheckSession;
  onRecord: OpenRecord;
}) {
  const rows: SampleRow[] = finding.samples.map((sample, index) => ({ index, sample }));
  const listed = finding.samplesFrom + finding.samples.length;
  const remaining = Math.max(0, finding.count - listed);
  const paging = session.isPaging(target, finding);
  const items = finding.samples.some((sample) => sample.item !== null && sample.item !== undefined);

  const columns: Column<SampleRow>[] = [
    {
      // The record, and under it what an operator knows it by and where its row came from: a side panel has room for
      // three columns, so the origin rides under the key rather than taking a column of its own.
      id: "record",
      header: "Record",
      fill: true,
      floor: 180,
      render: ({ sample }) => {
        const origin = sampleOrigin(sample);
        const under = [sample.label, origin].filter((part): part is string => part !== null && part !== undefined && part !== "").join(" · ");
        return (
          <span className="flex min-w-0 flex-col">
            <TruncatedText text={sample.sourceKey} mono maxWidth={420} />
            {under !== "" && <TruncatedText text={under} maxWidth={420} className="text-[11px] text-muted-foreground" />}
          </span>
        );
      },
    },
    ...(items
      ? [{
        id: "item",
        header: "Item",
        align: "right" as const,
        render: ({ sample }: SampleRow) => <span className="font-mono text-[12px] tabular-nums">{sample.item ?? "-"}</span>,
      }]
      : []),
    {
      id: "value",
      header: "Value",
      render: ({ sample }) => <TruncatedText text={sample.value} mono maxWidth={140} placeholder="-" />,
    },
    {
      id: "actions",
      header: "",
      align: "right",
      render: ({ sample }) => (
        <span className="inline-flex items-center gap-0.5">
          {sample.message && sample.message !== finding.message && (
            <GlyphRef icon={Info} title={finding.outcome === "asserted" ? "Why the value fails the assertion" : "Reason for this record"} body={sample.message} />
          )}
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                type="button"
                size="icon-xs"
                variant="ghost"
                aria-label="Open the record"
                onClick={() => onRecord(sample.sourceKey, finding)}
                data-testid="value-check-sample-open"
              >
                <Eye />
              </Button>
            </TooltipTrigger>
            <TooltipContent>Open the record: what is wrong, what the source holds, and what it rendered to</TooltipContent>
          </Tooltip>
          <CopyButton label="Copy the source key" text={sample.sourceKey} testId="value-check-sample-copy" iconOnly />
        </span>
      ),
    },
  ];

  const save = () => {
    const blob = new Blob([samplesCsv(target, finding, finding.samples)], { type: "text/csv;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = `${splitPath(target).leaf}-${finding.outcome}.csv`;
    link.click();
    URL.revokeObjectURL(url);
  };

  return (
    <div className="flex flex-col gap-2 border-t px-2.5 py-2" data-testid="value-check-records">
      {rows.length === 0
        ? <p className="text-[12px] text-muted-foreground">No example records were kept for this finding; check the attribute alone to list them.</p>
        : (
          <div className="max-h-80 overflow-auto">
            <DataTable
              columns={columns}
              rows={rows}
              rowKey={(row) => String(row.index)}
              emptyMessage="No records."
              data-testid="value-check-records-table"
            />
          </div>
        )}
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-[12px] text-muted-foreground tabular-nums" data-testid="value-check-records-range">
          {rows.length === 0
            ? `${formatCount(finding.count)} ${unit}`
            : `${formatCount(finding.samplesFrom + 1)}-${formatCount(listed)} of ${formatCount(finding.count)} ${unit}`}
        </span>
        {remaining > 0 && (
          <Button
            type="button"
            size="xs"
            variant="outline"
            disabled={paging || !session.ready}
            onClick={() => session.more(target, finding)}
            title={session.blocked ?? undefined}
            data-testid="value-check-records-more"
          >
            {paging ? <Loader2 className="animate-spin" /> : <ListPlus />}
            {paging ? "Listing" : `List ${formatCount(Math.min(remaining, 100))} more`}
          </Button>
        )}
        {rows.length > 0 && (
          <span className="ml-auto inline-flex items-center gap-1">
            <CopyButton label="Copy the keys" text={() => finding.samples.map((sample) => sample.sourceKey).join("\n")} testId="value-check-records-copy" />
            <Button type="button" size="xs" variant="ghost" onClick={save} data-testid="value-check-records-csv">
              <Download />
              CSV
            </Button>
          </span>
        )}
      </div>
    </div>
  );
}

/**
 * The checked attributes inside a group, the ones with rows that fail first and then those failing an assertion of the
 * mapping, each opening in the tree.
 */
function InsideSummary({ results, session }: { results: VariableCheck[]; session: ValueCheckSession }) {
  const ordered = [...results].sort((a, b) => failingOf(b.variable.rows) - failingOf(a.variable.rows)
    || assertedOf(b.variable.rows) - assertedOf(a.variable.rows));
  const AssertedIcon = ASSERTED.icon;
  return (
    <div className="flex flex-col gap-1.5" data-testid="value-check-inside">
      <h4 className="text-[13px] font-medium">Attributes inside</h4>
      <ul className="flex flex-col">
        {ordered.map((result) => {
          const failing = failingOf(result.variable.rows);
          const asserted = assertedOf(result.variable.rows);
          const worst = outcomeVisual(worstOf(result.variable.rows) ?? "valid");
          return (
            <li key={result.variable.target}>
              <button
                type="button"
                onClick={() => session.requestFocus(result.variable.target)}
                className="grid w-full grid-cols-[minmax(0,1fr)_6rem_4.5rem] items-center gap-3 rounded-md px-1.5 py-1 text-left outline-none hover:bg-accent/50 focus-visible:ring-2 focus-visible:ring-ring/50"
                data-testid={`value-check-inside-${result.variable.target}`}
              >
                <span className="truncate font-mono text-[12px]" title={result.variable.target}>{splitPath(result.variable.target).leaf}</span>
                <OutcomeMeter counts={result.variable.rows} unit="rows" compact testId={`value-check-inside-meter-${result.variable.target}`} />
                <span className="inline-flex items-center justify-end gap-1.5 font-mono text-[12px] tabular-nums">
                  {failing > 0 && <span className={worst.textClass}>{formatCount(failing)}</span>}
                  {asserted > 0 && (
                    <span
                      className="inline-flex items-center gap-0.5 text-muted-foreground"
                      title={`${formatCount(asserted)} rows fail an assertion of the mapping`}
                      data-testid={`value-check-inside-asserted-${result.variable.target}`}
                    >
                      <AssertedIcon className={cn("size-3", ASSERTED.textClass)} aria-label="failing an assertion" />
                      {failing === 0 && formatCount(asserted)}
                    </span>
                  )}
                  {failing === 0 && asserted === 0 && <span className="text-success">clean</span>}
                </span>
              </button>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
