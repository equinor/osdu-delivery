import { useMemo, useState, type ReactNode } from "react";
import { Link as RouterLink } from "react-router-dom";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { ChevronLeft, ChevronRight, ExternalLink, Info, Loader2, ScanSearch } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { TruncatedText } from "@/components/TruncatedText";
import { cn } from "@/lib/utils";
import {
  deliveryApi,
  flowLedgerRoute,
  type DeliveryMappingFlow,
  type DeliveryPreviewDocument,
  type DeliveryPreviewRows,
  type DeliveryPreviewSource,
  type DeliveryValueCheckFinding,
  type DeliveryValueCheckSample,
} from "../../api/delivery";
import { branchesToFold, columnsOfRow, missingFromDocument, pathsOfVariable, recordsOfFinding, sourceColumn } from "./checkedDocument";
import { JsonView } from "./OsduRecordInspector";
import { documentNode } from "./osduRecordModel";
import { envelopeFirst } from "./osduDocument";
import { wouldHold } from "./previewHolds";
import { HoldReasons, RecordPreviewView } from "./RecordPreviewView";
import { ProblemView, TaskProgress } from "./TemplateSheet";
import { splitPath } from "./templateFormat";
import { flowScope, useValueCheckContext, type CheckMeta, type ValueCheckSession } from "./useValueCheck";
import { findingKey, findingLabel, flowKey, formatCount, outcomeVisual, sampleOrigin } from "./valueCheck";

/** Which form of the document is shown: as the mapping renders it, or as the route sends it. */
type DocumentForm = "rendered" | "sent";

/** A reason rows fail an attribute, with the check that found it, which pages its records and read their rows. */
interface CheckedFinding {
  finding: DeliveryValueCheckFinding;
  target: string;
  meta: CheckMeta;
}

/** A titled part of the page, read top to bottom. */
function Section({ title, aside, testId, children }: { title: string; aside?: ReactNode; testId: string; children: ReactNode }) {
  return (
    <section className="flex min-w-0 flex-col gap-1.5" data-testid={testId}>
      <div className="flex min-w-0 flex-wrap items-baseline justify-between gap-x-3 gap-y-0.5">
        <h4 className="text-[13px] font-medium">{title}</h4>
        {aside}
      </div>
      {children}
    </section>
  );
}

/** Why a record is shown when no failing row is: said first, so the record below is not read as one that fails. */
function Notice({ children, testId }: { children: ReactNode; testId: string }) {
  return (
    <p className="flex items-start gap-1.5 rounded-md border border-dashed px-2.5 py-2 text-[12px] text-muted-foreground" data-testid={testId}>
      <Info className="mt-0.5 size-3.5 shrink-0" aria-hidden />
      <span>{children}</span>
    </p>
  );
}

/**
 * An attribute's Failing records tab: the rows a check found wrong with it, one record at a time, as a page read top to
 * bottom. It answers what a reader asks of a failing attribute, in the order they ask it: why rows fail, which record this
 * is, what is wrong in it, what its source row holds, and what that rendered to. Nothing is written or sent. The reason and
 * the place among its rows stay in view as the page scrolls. An attribute no row fails shows the first record of the scope,
 * and says so.
 */
export function VariableRecordsTab({ path }: { path: string }) {
  const session = useValueCheckContext();
  if (session === null) {
    return null;
  }

  if (session.flow === null || !session.ready) {
    return (
      <div className="flex flex-col items-start gap-2 rounded-md border border-dashed px-3 py-4" data-testid="value-check-records-blocked">
        <span className="inline-flex items-center gap-1.5 text-[13px] font-medium">
          <ScanSearch className="size-4 text-muted-foreground" />
          No records to show yet
        </span>
        <p className="text-[12px] text-muted-foreground">{session.blocked ?? "Pick a flow that renders with this mapping to read its rows."}</p>
      </div>
    );
  }

  return <FailingRecords key={flowKey(session.flow)} path={path} session={session} flow={session.flow} />;
}

/** The reasons an attribute's rows fail, the largest first: its own check's, or what a check of a value holding it found at it. */
function reasonsOf(path: string, session: ValueCheckSession): CheckedFinding[] {
  const own = session.resultFor(path);
  return own !== undefined
    ? own.variable.findings
      .filter((finding) => finding.outcome !== "notApplicable")
      .map((finding) => ({ finding, target: own.variable.target, meta: own.meta }))
    : session.nestedFor(path)
      .filter(({ finding }) => finding.outcome !== "notApplicable")
      .map(({ finding, from }) => ({ finding, target: from.variable.target, meta: from.meta }));
}

function FailingRecords({ path, session, flow }: { path: string; session: ValueCheckSession; flow: DeliveryMappingFlow }) {
  const own = session.resultFor(path);
  const reasons = reasonsOf(path, session);
  // A record opened on another attribute says nothing of this one, which shows its own.
  const picked = session.pickedRecord?.path === path ? session.pickedRecord : null;
  const current = reasons.find((reason) => findingKey(reason.finding) === picked?.finding) ?? reasons[0] ?? null;
  const listed = recordsOfFinding(current?.finding.samples ?? []);
  const shown = (picked !== null ? listed.find((record) => record.sourceKey === picked.key) : undefined) ?? listed[0] ?? null;
  const position = shown === null ? -1 : listed.indexOf(shown);
  const key = shown?.sourceKey ?? null;
  const values = current?.meta.values ?? own?.meta.values ?? session.sentValues;
  const scope = flowScope(flow);
  const showsRecord = current === null ? session.inside(path).length === 0 : listed.length > 0;

  // The answer is kept while the next record renders, so stepping through records does not make the page jump.
  const preview = useQuery({
    queryKey: ["delivery", "value-check-document", flow.pipelineId, flow.interface, flow.partition, key, values],
    queryFn: () => deliveryApi.preview(flow.pipelineId, { key, values }, scope),
    placeholderData: keepPreviousData,
    staleTime: 30000,
    retry: false,
    enabled: showsRecord,
  });

  const open = (reason: CheckedFinding, sourceKey: string) => session.pickRecord({ path, key: sourceKey, finding: findingKey(reason.finding) });
  const answer = showsRecord ? preview.data : undefined;
  const source = answer?.found ? answer.source ?? null : null;
  const previewValues: Record<string, string> = Object.keys(values).length === 0 ? {} : { previewValues: JSON.stringify(values) };
  const { leaf } = splitPath(path);

  return (
    <div className="flex flex-col gap-4" data-testid="value-check-records-page">
      {current !== null && (
        <RecordsHeader
          reasons={reasons}
          current={current}
          listed={listed.length}
          position={position}
          paging={session.isPaging(current.target, current.finding)}
          canList={session.ready}
          onReason={(reason) => {
            const first = recordsOfFinding(reason.finding.samples)[0];
            if (first !== undefined) {
              open(reason, first.sourceKey);
            }
          }}
          onStep={(by) => {
            const next = listed[position + by];
            if (next !== undefined) {
              open(current, next.sourceKey);
            } else if (by > 0) {
              session.more(current.target, current.finding);
            }
          }}
        />
      )}

      {current === null && session.inside(path).length > 0 && (
        <Notice testId="value-check-records-inside">{`Rows fail the attributes inside ${leaf}, not ${leaf} itself. Pick one of them in the tree.`}</Notice>
      )}
      {current === null && session.inside(path).length === 0 && (
        <Notice testId="value-check-records-none">
          {own !== undefined
            ? `No row of the ${formatCount(own.meta.rows.checked)} checked fails ${leaf}. This is the first record of the scope, as an example.`
            : `${leaf} has not been checked yet, so no failing rows are listed. Use Check values above. This is the first record of the scope, as an example.`}
        </Notice>
      )}
      {current !== null && listed.length === 0 && (
        <Notice testId="value-check-records-no-samples">The check kept no example records of this reason. Use Check again above to check this attribute alone and list them.</Notice>
      )}

      {showsRecord && preview.isError && <ProblemView error={preview.error} testId="value-check-records-error" />}
      {showsRecord && preview.isPending && <TaskProgress label="Rendering the record" testId="value-check-records-progress" />}
      {answer !== undefined && !answer.found && <RecordPreviewView preview={answer} />}

      {answer !== undefined && source !== null && (
        <div className={cn("flex flex-col gap-4 transition-opacity", preview.isPlaceholderData && "opacity-60")}>
          <div className="flex min-w-0 items-start gap-2" data-testid="value-check-records-record">
            <div className="flex min-w-0 flex-1 flex-col gap-0.5">
              <h3 className="min-w-0 break-words text-[14px] font-semibold" data-testid="value-check-records-title">{source.label ?? source.sourceKey}</h3>
              <p className="min-w-0 text-[12px] break-words text-muted-foreground" data-testid="value-check-records-origin">
                <span className="font-mono">{source.sourceKey}</span>
                {source.originFile && ` · ${source.originFile}`}
                {source.originRow !== null && source.originRow !== undefined && `, row ${source.originRow.toLocaleString("en-US")}`}
              </p>
            </div>
            {preview.isFetching && <Loader2 className="mt-1 size-3.5 shrink-0 animate-spin text-muted-foreground" aria-label="Rendering the record" />}
            <Tooltip>
              <TooltipTrigger asChild>
                <Button asChild size="icon-xs" variant="ghost" aria-label="Open in the flow's Preview">
                  <RouterLink to={flowLedgerRoute(flow.pipelineId, scope, { tab: "preview", previewKey: source.sourceKey, ...previewValues })} data-testid="value-check-records-preview">
                    <ExternalLink />
                  </RouterLink>
                </Button>
              </TooltipTrigger>
              <TooltipContent className="max-w-72">Open in the flow&apos;s Preview: what a delivery would do with it, how it was rendered, and the requests it would make</TooltipContent>
            </Tooltip>
          </div>

          {shown !== null && current !== null && (
            <Section title="What is wrong in this record" testId="value-check-records-wrong">
              <ul className="flex flex-col gap-1">
                {shown.samples.map((sample, index) => <WrongValue key={`${sample.item ?? ""}:${index}`} sample={sample} finding={current.finding} leaf={leaf} />)}
              </ul>
              {answer.document !== null && answer.document !== undefined && wouldHold(answer.document) && (
                <HoldReasons document={answer.document} testId="value-check-records-holds" />
              )}
            </Section>
          )}

          <Section title="In the source" aside={<TruncatedText text={flow.recordObject} mono maxWidth={300} title="The record table" className="text-[11px] text-muted-foreground" />} testId="value-check-records-source">
            <SourceData
              key={source.sourceKey}
              source={source}
              leaf={leaf}
              reads={own?.variable.columns ?? []}
              checked={own !== undefined}
              samples={shown?.samples ?? []}
            />
          </Section>

          {answer.document === null || answer.document === undefined
            ? <p className="text-[12px] text-muted-foreground" data-testid="value-check-records-no-document">{answer.noDocument ?? "The row renders no document."}</p>
            : <RenderedRecord key={`${source.sourceKey}:${answer.document.metadataHash}`} document={answer.document} path={path} leaf={leaf} />}
        </div>
      )}
    </div>
  );
}

/**
 * What stays in view as the page scrolls: why rows fail the attribute, the reason picked when there are several, and where
 * this record is among the rows. The count is the rows the reason holds for, not the few listed so far: past the last one
 * listed, the next is read from the node.
 */
function RecordsHeader({ reasons, current, listed, position, paging, canList, onReason, onStep }: {
  reasons: readonly CheckedFinding[];
  current: CheckedFinding;
  listed: number;
  position: number;
  paging: boolean;
  canList: boolean;
  onReason: (reason: CheckedFinding) => void;
  onStep: (by: number) => void;
}) {
  const { finding } = current;
  const visual = outcomeVisual(finding.outcome);
  const Icon = visual.icon;
  const unlisted = finding.count > finding.samplesFrom + finding.samples.length;
  const atEnd = position >= listed - 1;
  return (
    <div className="sticky -top-3 z-20 -mx-3 flex flex-col gap-1.5 border-b bg-background px-3 pt-1 pb-2" data-testid="value-check-records-header">
      {reasons.length > 1
        ? (
          <Select
            value={findingKey(finding)}
            onValueChange={(value) => {
              const reason = reasons.find((candidate) => findingKey(candidate.finding) === value);
              if (reason !== undefined) {
                onReason(reason);
              }
            }}
          >
            <SelectTrigger size="sm" className="h-8 w-full text-[12px]" aria-label="The reason rows fail this attribute" data-testid="value-check-records-reason">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {reasons.map((reason) => (
                <SelectItem key={findingKey(reason.finding)} value={findingKey(reason.finding)} className="text-[12px]">
                  {`${findingLabel(reason.finding)} · ${formatCount(reason.finding.rows)} rows`}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )
        : (
          <p className="flex min-w-0 items-center gap-1.5 text-[13px] font-medium" data-testid="value-check-records-reason">
            <Icon className={cn("size-3.5 shrink-0", visual.textClass)} aria-hidden />
            <span className="min-w-0 truncate" title={findingLabel(finding)}>{findingLabel(finding)}</span>
            <span className="shrink-0 font-normal text-muted-foreground">{`· ${formatCount(finding.rows)} ${finding.rows === 1 ? "row" : "rows"}`}</span>
          </p>
        )}
      <p className="text-[12px] break-words text-muted-foreground" data-testid="value-check-records-message">{finding.message}</p>
      <div className="flex items-center gap-1" data-testid="value-check-records-stepper">
        <Button type="button" size="icon-xs" variant="outline" aria-label="The record before" disabled={position <= 0} onClick={() => onStep(-1)} data-testid="value-check-records-previous">
          <ChevronLeft />
        </Button>
        <span className="px-1 text-[12px] tabular-nums" data-testid="value-check-records-position">
          {`Record ${formatCount(position + 1)} of ${formatCount(finding.rows)}`}
        </span>
        <Button
          type="button"
          size="icon-xs"
          variant="outline"
          aria-label="The record after"
          disabled={paging || (atEnd && (!unlisted || !canList))}
          onClick={() => onStep(1)}
          data-testid="value-check-records-next"
        >
          {paging ? <Loader2 className="animate-spin" /> : <ChevronRight />}
        </Button>
        {paging && <span className="text-[12px] text-muted-foreground" data-testid="value-check-records-paging">Reading the next rows</span>}
      </div>
    </div>
  );
}

/** One value of the record the reason holds for: the item it is in, why, the value behind it, and where in the file to fix it. */
function WrongValue({ sample, finding, leaf }: { sample: DeliveryValueCheckSample; finding: DeliveryValueCheckFinding; leaf: string }) {
  const visual = outcomeVisual(finding.outcome);
  const Icon = visual.icon;
  const value = sample.value ?? null;
  const message = sample.message ?? finding.message;
  const where = [sampleOrigin(sample), (sample.columns ?? []).length > 0 ? `column ${(sample.columns ?? []).join(", ")}` : null].filter((part) => part !== null).join(", ");
  return (
    <li className="flex min-w-0 items-start gap-1.5 text-[12px]" data-testid="value-check-records-wrong-value">
      <Icon className={cn("mt-0.5 size-3.5 shrink-0", visual.textClass)} aria-hidden />
      <span className="min-w-0 break-words">
        <span className="font-mono">{sample.item === null || sample.item === undefined ? leaf : `${leaf}, item ${sample.item}`}</span>
        {`: ${message}`}
        {value !== null && value !== "" && !message.includes(value) && (
          <span className="ml-1 text-muted-foreground"><TruncatedText text={value} mono maxWidth={200} title="The value" /></span>
        )}
        {where !== "" && <span className="block text-muted-foreground" data-testid="value-check-records-fix-at">{`Fix at: ${where}`}</span>}
      </span>
    </li>
  );
}

/** A source value as the difference matters when looking for what is wrong with it: no value, an empty text and a blank one each say so. */
function SourceValue({ value }: { value: string | null | undefined }) {
  if (value === null || value === undefined) {
    return <span className="text-muted-foreground">null</span>;
  }

  return value.trim() === ""
    ? <span className="text-muted-foreground">{value === "" ? "(empty)" : "(blank)"}</span>
    : <span className="whitespace-pre-wrap break-all">{value}</span>;
}

/**
 * What the record's row holds in the columns the attribute reads, a child dataset's rows with the ones found wrong marked and
 * the cell to fix picked out in them. Only those: the row's other columns are a click away, so what is shown is what bears on
 * the attribute. A row whose columns are not known yet, with no check of the attribute, shows them all.
 */
function SourceData({ source, leaf, reads, checked, samples }: {
  source: DeliveryPreviewSource;
  leaf: string;
  /** The source columns the attribute reads, as the mapping names them. */
  reads: readonly string[];
  /** Whether a check of the attribute said what it reads. */
  checked: boolean;
  /** What the check found wrong in this record, which says the child rows and the columns. */
  samples: readonly DeliveryValueCheckSample[];
}) {
  const [all, setAll] = useState(false);
  const columns = useMemo(() => reads.map(sourceColumn), [reads]);
  const own = columnsOfRow(source.row, columns.filter((column) => column.child === null).map((column) => column.column));
  const children = Object.entries(source.datasets ?? {});
  const reading = (name: string) => columns.some((column) => column.child?.toLowerCase() === name.toLowerCase());
  const whole = all || columns.length === 0;
  const shownColumns = whole ? [...own.read, ...own.others] : own.read;
  const shownChildren = children.filter(([name]) => whole || reading(name));
  const hidden = own.others.length + children.filter(([name]) => !reading(name)).length;

  return (
    <div className="flex flex-col gap-1.5">
      <p className="text-[12px] text-muted-foreground" data-testid="value-check-source-reads">
        {columns.length > 0
          ? <>{`${leaf} reads `}<span className="font-mono text-foreground">{reads.join(", ")}</span></>
          : checked
            ? `${leaf} reads no source column: nothing of the row writes it.`
            : `Check ${leaf} to see which columns it reads. The whole row is shown until then.`}
      </p>
      {source.omitted && <Alert><Info /><AlertDescription>{source.omitted}</AlertDescription></Alert>}
      <div className="flex flex-col gap-3 overflow-x-auto rounded-md border p-2 font-mono text-[12px]" data-testid="value-check-source-data">
        {shownColumns.length > 0 && (
          <table className="w-full table-fixed border-collapse" data-testid="value-check-source-columns">
            <tbody>
              {shownColumns.map((column) => (
                <tr key={column} className={cn("border-b last:border-b-0", own.read.includes(column) && "bg-info/10", cellToFix(samples, null, `dataset.${column}`) && "bg-warning/25")} data-read={own.read.includes(column) || undefined} data-fix={cellToFix(samples, null, `dataset.${column}`) || undefined}>
                  <td className="w-2/5 py-0.5 pr-2 align-top break-all text-muted-foreground">{column}</td>
                  <td className="py-0.5 align-top"><SourceValue value={source.row[column]} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        {shownChildren.map(([name, rows]) => (
          <ChildRows key={name} name={name} rows={rows} reads={columns.filter((column) => column.child?.toLowerCase() === name.toLowerCase()).map((column) => column.column)} samples={samples} all={whole} />
        ))}
        {shownColumns.length === 0 && shownChildren.length === 0 && (
          <p className="font-sans text-muted-foreground" data-testid="value-check-source-none">The row holds none of the columns this attribute reads.</p>
        )}
      </div>
      {columns.length > 0 && hidden > 0 && (
        <button
          type="button"
          onClick={() => setAll((was) => !was)}
          aria-expanded={all}
          className="inline-flex items-center gap-1 self-start text-[12px] text-muted-foreground hover:text-foreground"
          data-testid="value-check-source-all"
        >
          <ChevronRight className={cn("size-3.5 transition-transform", all && "rotate-90")} />
          {all ? "Only what this attribute reads" : `The rest of the row: ${hidden.toLocaleString("en-US")} more`}
        </button>
      )}
    </div>
  );
}

/** Whether the check says to fix a column, named as the mapping names it, in the record's row (no item) or one of its child rows. */
function cellToFix(samples: readonly DeliveryValueCheckSample[], item: number | null, column: string): boolean {
  return samples.some((sample) => (sample.item ?? null) === item && (sample.columns ?? []).some((named) => named.toLowerCase() === column.toLowerCase()));
}

/**
 * A child dataset's rows, the columns the attribute reads first (or alone), the rows the check found wrong marked by their
 * number and the cell to fix picked out in each.
 */
function ChildRows({ name, rows, reads, samples, all }: { name: string; rows: DeliveryPreviewRows; reads: readonly string[]; samples: readonly DeliveryValueCheckSample[]; all: boolean }) {
  const { read, others } = columnsOfRow(rows.rows[0] ?? {}, reads);
  const columns = all || read.length === 0 ? [...read, ...others] : read;
  const items = samples.map((sample) => sample.item).filter((item): item is number => item !== null && item !== undefined);
  const past = items.filter((item) => item > rows.rows.length);
  return (
    <section className="flex min-w-0 flex-col gap-1" data-testid={`value-check-source-child-${name}`}>
      <h5 className="font-sans text-[12px] text-muted-foreground">
        <span className="font-mono text-foreground">{name}</span>
        {rows.truncated ? `: the first ${rows.rows.length.toLocaleString("en-US")} of ${rows.total.toLocaleString("en-US")} rows` : `: ${rows.total.toLocaleString("en-US")} ${rows.total === 1 ? "row" : "rows"}`}
      </h5>
      <table className="w-full border-collapse">
        <thead>
          <tr className="border-b text-left text-[11px] text-muted-foreground">
            <th className="w-10 py-0.5 pr-2 font-normal">item</th>
            {columns.map((column) => <th key={column} className="py-0.5 pr-2 font-normal break-all">{column}</th>)}
          </tr>
        </thead>
        <tbody>
          {rows.rows.map((row, index) => {
            const wrong = items.includes(index + 1);
            return (
              <tr key={index} className={cn("border-b last:border-b-0", wrong && "bg-warning/10")} data-hit={wrong || undefined}>
                <td className="py-0.5 pr-2 align-top text-muted-foreground tabular-nums">{index + 1}</td>
                {columns.map((column) => {
                  const fix = cellToFix(samples, index + 1, `dataset.${name}.${column}`);
                  return <td key={column} className={cn("py-0.5 pr-2 align-top", fix && "bg-warning/25 font-medium")} data-fix={fix || undefined}><SourceValue value={row[column]} /></td>;
                })}
              </tr>
            );
          })}
        </tbody>
      </table>
      {past.length > 0 && (
        <p className="font-sans text-[12px] text-muted-foreground" data-testid="value-check-source-past">
          {`${past.length === 1 ? `Item ${past[0]} is` : `Items ${past.join(", ")} are`} past the rows shown here; Export rows in the Data tab has them.`}
        </p>
      )}
    </section>
  );
}

/**
 * What the record renders to: the whole document, folded down to the way to the attribute so where it is can be seen at a
 * glance, with each value of it marked and, where it is left out, a line saying so at the place it would have been. A value
 * a failed assertion leaves out is out of the document already, which is what that line shows.
 */
function RenderedRecord({ document, path, leaf }: { document: DeliveryPreviewDocument; path: string; leaf: string }) {
  const [form, setForm] = useState<DocumentForm>("rendered");
  const shown = form === "sent" && document.sent ? document.sent : document.rendered ?? null;
  const root = useMemo(() => (shown === null ? null : documentNode(envelopeFirst(shown))), [shown]);
  const { marked, missing, collapsed } = useMemo(() => {
    const hits = root === null ? [] : pathsOfVariable(root, path);
    const absent = root === null ? new Map<string, string>() : missingFromDocument(root, path);
    return { marked: new Set(hits), missing: absent, collapsed: root === null ? new Set<string>() : branchesToFold(root, [...hits, ...absent.keys()]) };
  }, [root, path]);

  return (
    <Section
      title="In the rendered record"
      aside={document.sent && (
        <ToggleGroup type="single" size="sm" variant="outline" value={form} onValueChange={(next) => { if (next === "sent" || next === "rendered") { setForm(next); } }} data-testid="value-check-records-form">
          <ToggleGroupItem value="rendered" data-testid="value-check-records-rendered">As rendered</ToggleGroupItem>
          <ToggleGroupItem value="sent" data-testid="value-check-records-sent">As sent</ToggleGroupItem>
        </ToggleGroup>
      )}
      testId="value-check-records-rendered-section"
    >
      {document.omitted
        ? <Alert data-testid="value-check-records-omitted"><Info /><AlertDescription>{document.omitted}</AlertDescription></Alert>
        : root !== null && (
          <>
            <p className="text-[12px] text-muted-foreground" data-testid="value-check-records-legend">
              {marked.size === 0 && missing.size === 0
                ? `${leaf} is not in this document.`
                : `${leaf} is marked where it was written${missing.size > 0 ? " and where it is left out" : ""}. The rest is folded.`}
            </p>
            <div className="rounded-md border" data-testid="value-check-records-json">
              <JsonView key={`${path}:${form}`} node={root} term="" hits={marked} absent={missing} collapsed={collapsed} ownId={null} />
            </div>
          </>
        )}
    </Section>
  );
}
