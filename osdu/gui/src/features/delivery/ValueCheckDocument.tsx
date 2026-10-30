import { useEffect, useMemo, useRef, useState } from "react";
import { Link as RouterLink } from "react-router-dom";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { ChevronLeft, ChevronRight, CircleMinus, Database, ExternalLink, Info, Loader2, ScanSearch } from "lucide-react";
import type { RunValue } from "@/api/types";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { TruncatedText } from "@/components/TruncatedText";
import { useScrollFit } from "@/hooks/useScrollFit";
import { cn } from "@/lib/utils";
import {
  deliveryApi,
  flowLedgerRoute,
  type DeliveryMappingFlow,
  type DeliveryPreviewDocument,
  type DeliveryPreviewRows,
  type DeliveryPreviewSource,
} from "../../api/delivery";
import { AssertionActionGlyph } from "./AssertionFindingsView";
import { columnsOfRow, pathsOfVariable, recordsOfFindings, sourceColumn } from "./checkedDocument";
import { JsonView } from "./OsduRecordInspector";
import { documentNode } from "./osduRecordModel";
import { envelopeFirst } from "./osduDocument";
import { wouldHold } from "./previewHolds";
import { HoldReasons, PreviewActionBadge, RecordPreviewView } from "./RecordPreviewView";
import { ProblemView, TaskProgress } from "./TemplateSheet";
import { splitPath } from "./templateFormat";
import { flowScope, useValueCheckContext, type ValueCheckSession } from "./useValueCheck";
import { flowKey, formatCount, within } from "./valueCheck";

/** Which form of the document is shown: as the mapping renders it, or as the route sends it. */
type DocumentForm = "rendered" | "sent";

/** The room the document leaves under itself in the panel (the panel's padding), and the least it shrinks to. */
const DOCUMENT_FIT_BELOW = 12;
const DOCUMENT_FIT_MIN = 240;

/** How far under the top of the box the attribute lands: a line above it, and the most of what follows it the box can show. */
const DOCUMENT_LINE_ABOVE = 24;

/** Scrolls a box so the first marked line in it sits near its top, the box alone moving. */
function bringMarkIntoBox(box: HTMLElement | null): void {
  const mark = box?.querySelector("[data-hit]");
  if (box !== null && box !== undefined && mark !== null && mark !== undefined) {
    const top = mark.getBoundingClientRect().top - box.getBoundingClientRect().top + box.scrollTop;
    box.scrollTop = Math.max(0, top - DOCUMENT_LINE_ABOVE);
  }
}

/**
 * An attribute's Document and Source tabs, one record shown two ways, so what a check counts can be seen in the record itself:
 * the document, rendered exactly as a delivery would render it and sent nowhere, with the attribute marked where it landed;
 * or the base data behind it, what the row holds in the source columns the attribute reads, with where the row is. Staying
 * beside the tree, they follow the reader from one attribute to the next. The record is the one picked (from the Data tab's
 * records, by stepping through the ones the check lists for the attribute, or by a key), else the first the check lists for
 * it, else the scope's first.
 */
export function VariableDocumentTab({ path, view }: { path: string; view: "document" | "source" }) {
  const session = useValueCheckContext();
  if (session === null) {
    return null;
  }

  if (session.flow === null || !session.ready) {
    return (
      <div className="flex flex-col items-start gap-2 rounded-md border border-dashed px-3 py-4" data-testid="value-check-document-blocked">
        <span className="inline-flex items-center gap-1.5 text-[13px] font-medium">
          <ScanSearch className="size-4 text-muted-foreground" />
          No record to show yet
        </span>
        <p className="text-[12px] text-muted-foreground">{session.blocked ?? "Pick a flow that renders with this mapping to render one of its records."}</p>
      </div>
    );
  }

  return <CheckedRecord key={flowKey(session.flow)} path={path} view={view} session={session} flow={session.flow} />;
}

function CheckedRecord({ path, view, session, flow }: { path: string; view: "document" | "source"; session: ValueCheckSession; flow: DeliveryMappingFlow }) {
  const check = session.resultFor(path);
  const listed = useMemo(() => recordsOfFindings(check?.variable.findings ?? []), [check]);
  const picked = session.pickedRecord;
  const key = picked?.key ?? listed[0]?.sourceKey ?? null;
  // A record a check named is read with the values that check read it with, so it is found though the fields have moved on.
  const values: Record<string, RunValue> = picked?.values ?? check?.meta.values ?? session.sentValues;
  const scope = flowScope(flow);

  // The answer is kept while the next record renders, so stepping through records does not make the page jump.
  const preview = useQuery({
    queryKey: ["delivery", "value-check-document", flow.pipelineId, flow.interface, flow.partition, key, values],
    queryFn: () => deliveryApi.preview(flow.pipelineId, { key, values }, scope),
    placeholderData: keepPreviousData,
    staleTime: 30000,
    retry: false,
  });

  const position = key === null ? -1 : listed.findIndex((record) => record.sourceKey === key);
  const step = (by: number) => {
    const next = listed[Math.min(Math.max(position + by, 0), listed.length - 1)];
    if (next !== undefined) {
      session.pickRecord({ key: next.sourceKey, values: check?.meta.values ?? session.sentValues });
    }
  };

  const answer = preview.data;
  const source = answer?.source ?? null;
  const previewValues: Record<string, string> = Object.keys(values).length === 0 ? {} : { previewValues: JSON.stringify(values) };
  return (
    <div className="flex flex-col gap-2" data-testid="value-check-document">
      <div className="flex flex-wrap items-center gap-1.5">
        {listed.length > 0 && (
          <span className="inline-flex items-center gap-0.5" data-testid="value-check-document-stepper">
            <Button type="button" size="icon-xs" variant="ghost" aria-label="The record before" disabled={position <= 0} onClick={() => step(-1)} data-testid="value-check-document-previous">
              <ChevronLeft />
            </Button>
            <Tooltip>
              <TooltipTrigger asChild>
                <span className="cursor-default font-mono text-[11px] text-muted-foreground tabular-nums" data-testid="value-check-document-position">
                  {`${position < 0 ? "-" : formatCount(position + 1)} of ${formatCount(listed.length)}`}
                </span>
              </TooltipTrigger>
              <TooltipContent className="max-w-72">The records the check lists for this attribute. List more of them in its Data tab, where Export rows saves every one.</TooltipContent>
            </Tooltip>
            <Button type="button" size="icon-xs" variant="ghost" aria-label="The record after" disabled={position >= listed.length - 1} onClick={() => step(1)} data-testid="value-check-document-next">
              <ChevronRight />
            </Button>
          </span>
        )}
        <RecordKeyField key={key ?? ""} shown={key ?? ""} onPick={(typed) => session.pickRecord(typed === null ? null : { key: typed, values: session.sentValues })} />
      </div>

      {preview.isError && <ProblemView error={preview.error} testId="value-check-document-error" />}
      {preview.isPending && <TaskProgress label="Rendering the record" testId="value-check-document-progress" />}

      {answer !== undefined && !answer.found && <RecordPreviewView preview={answer} />}

      {answer !== undefined && answer.found && source !== null && (
        <>
          <div className="flex min-w-0 items-center gap-2" data-testid="value-check-document-record">
            {answer.decision !== null && answer.decision !== undefined && <PreviewActionBadge decision={answer.decision} />}
            <span className="min-w-0 flex-1 truncate text-[13px] font-medium" title={source.sourceKey} data-testid="value-check-document-title">
              {source.label ?? source.sourceKey}
            </span>
            {preview.isFetching && <Loader2 className="size-3.5 shrink-0 animate-spin text-muted-foreground" aria-label="Rendering the record" />}
            <Tooltip>
              <TooltipTrigger asChild>
                <Button asChild size="icon-xs" variant="ghost" aria-label="Open in the flow's Preview">
                  <RouterLink to={flowLedgerRoute(flow.pipelineId, scope, { tab: "preview", previewKey: source.sourceKey, ...previewValues })} data-testid="value-check-document-preview">
                    <ExternalLink />
                  </RouterLink>
                </Button>
              </TooltipTrigger>
              <TooltipContent className="max-w-72">Open in the flow&apos;s Preview: how it was rendered, the requests a delivery would make, and the rows it came from</TooltipContent>
            </Tooltip>
          </div>

          {view === "source"
            ? (
              <SourceData
                key={source.sourceKey}
                source={source}
                table={flow.recordObject}
                keyColumns={answer.asked.keyColumns}
                path={path}
                reads={check?.variable.columns ?? []}
                checked={check !== undefined}
                items={listed[position]?.items ?? []}
                dimmed={preview.isPlaceholderData}
              />
            )
            : answer.document === null || answer.document === undefined
              ? <p className="text-[12px] text-muted-foreground" data-testid="value-check-document-none">{answer.noDocument ?? "The row renders no document."}</p>
              : <RenderedDocument key={answer.document.metadataHash} document={answer.document} path={path} dimmed={preview.isPlaceholderData} />}
        </>
      )}
    </div>
  );
}

/** The key of the record shown, editable: a source key, a delivery key, an OSDU id the ledger holds, or the key's parts. */
function RecordKeyField({ shown, onPick }: { shown: string; onPick: (key: string | null) => void }) {
  const [text, setText] = useState(shown);
  return (
    <form
      className="min-w-40 flex-1"
      onSubmit={(event) => {
        event.preventDefault();
        onPick(text.trim() === "" ? null : text.trim());
      }}
      data-testid="value-check-document-form"
    >
      <Input
        value={text}
        onChange={(event) => setText(event.target.value)}
        placeholder="First record, or a key or OSDU id"
        aria-label="The record to render"
        title="Press Enter to render the record this names: a source key, a delivery key or an OSDU id the ledger holds. Left empty, the first record the check lists, or the first of the scope."
        className="h-7 font-mono text-[12px]"
        spellCheck={false}
        autoComplete="off"
        data-testid="value-check-document-key"
      />
    </form>
  );
}

/**
 * The record's document as JSON with the attribute marked and brought into view, under what it says of the attribute: where
 * it landed, or that it is not there, and what the mapping's assertions and the render said of it. A value a failed
 * assertion leaves out is already out of the document, so that is said here, with the value, rather than left to be missed.
 */
function RenderedDocument({ document, path, dimmed }: { document: DeliveryPreviewDocument; path: string; dimmed: boolean }) {
  const [form, setForm] = useState<DocumentForm>("rendered");
  const shown = form === "sent" && document.sent ? document.sent : document.rendered ?? null;
  const root = useMemo(() => (shown === null ? null : documentNode(envelopeFirst(shown))), [shown]);
  const hits = useMemo(() => (root === null ? [] : pathsOfVariable(root, path)), [root, path]);
  const marked = useMemo(() => new Set(hits), [hits]);
  const failures = (document.assertions?.failures ?? []).filter((failure) => within(`osdu.${failure.at}`, path));
  const leftOut = failures.some((failure) => failure.onFail === "omit");
  const { leaf } = splitPath(path);

  // The document scrolls in a box of its own that fills what the panel leaves, so the record and what is said of the attribute
  // stay in view above it. The attribute may be a screen down a long document, so each one chosen is brought into the box.
  const body = useRef<HTMLDivElement>(null);
  useScrollFit(body, DOCUMENT_FIT_BELOW, DOCUMENT_FIT_MIN, undefined, "height");
  useEffect(() => bringMarkIntoBox(body.current), [hits]);

  return (
    <div className={cn("flex flex-col gap-2 transition-opacity", dimmed && "opacity-60")}>
      <div className="flex flex-col gap-1" data-testid="value-check-document-attribute" data-found={hits.length}>
        <p className="flex items-start gap-1.5 text-[12px]">
          {hits.length > 0
            ? <Info className="mt-0.5 size-3.5 shrink-0 text-muted-foreground" aria-hidden />
            : <CircleMinus className="mt-0.5 size-3.5 shrink-0 text-muted-foreground" aria-hidden />}
          <span className="min-w-0 break-words">
            <span className="font-mono">{leaf}</span>
            {hits.length > 0
              ? `: ${formatCount(hits.length)} ${hits.length === 1 ? "value" : "values"} in this document, marked below`
              : leftOut ? " is left out of this document" : " is not in this document"}
            {hits.length > 0 && leftOut ? ", and some of its values are left out" : ""}
          </span>
        </p>
        {failures.length > 0 && (
          <ul className="flex max-h-32 flex-col gap-1 overflow-y-auto pl-5" data-testid="value-check-document-failures">
            {failures.map((failure, index) => (
              <li key={`${failure.path}:${failure.assertion}:${index}`} className="flex min-w-0 items-start gap-1.5 text-[12px]" data-action={failure.onFail}>
                <AssertionActionGlyph action={failure.onFail} className="mt-0.5" />
                <span className="min-w-0 break-words">
                  <span className="font-mono">{failure.path !== "" ? failure.path : failure.at}</span>
                  {`: ${failure.message}`}
                  {failure.value !== "" && !failure.message.includes(failure.value) && (
                    <span className="ml-1 text-muted-foreground"><TruncatedText text={failure.value} mono maxWidth={200} title="The value" /></span>
                  )}
                </span>
              </li>
            ))}
          </ul>
        )}
      </div>

      {wouldHold(document) && <HoldReasons document={document} testId="value-check-document-holds" />}

      {document.sent && (
        <ToggleGroup type="single" size="sm" variant="outline" value={form} onValueChange={(next) => { if (next === "sent" || next === "rendered") { setForm(next); } }} className="self-start" data-testid="value-check-document-form-choice">
          <ToggleGroupItem value="rendered" data-testid="value-check-document-rendered">As the mapping renders it</ToggleGroupItem>
          <ToggleGroupItem value="sent" data-testid="value-check-document-sent">As the route sends it</ToggleGroupItem>
        </ToggleGroup>
      )}

      {document.omitted
        ? <Alert data-testid="value-check-document-omitted"><Info /><AlertDescription>{document.omitted}</AlertDescription></Alert>
        : root !== null && (
          <div ref={body} className="overflow-auto rounded-md border" data-testid="value-check-document-json">
            <JsonView node={root} term="" hits={marked} ownId={null} />
          </div>
        )}
    </div>
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
 * The base data behind an attribute in one record: where the row is (the table, the file and row, its key), then what it holds
 * in the columns the attribute reads, a child dataset's rows with the ones the check found wrong marked. Only those: the
 * row's other columns are a click away, so what is shown is what bears on the attribute. A row whose columns are not known
 * yet, with no check of the attribute, shows them all.
 */
function SourceData({ source, table, keyColumns, path, reads, checked, items, dimmed }: {
  source: DeliveryPreviewSource;
  /** The record table the row was read from. */
  table: string;
  keyColumns: readonly string[];
  path: string;
  /** The source columns the attribute reads, as the mapping names them. */
  reads: readonly string[];
  /** Whether a check of the attribute said what it reads. */
  checked: boolean;
  /** The child rows the check found wrong, counting from 1. */
  items: readonly number[];
  dimmed: boolean;
}) {
  const [all, setAll] = useState(false);
  const body = useRef<HTMLDivElement>(null);
  useScrollFit(body, DOCUMENT_FIT_BELOW, DOCUMENT_FIT_MIN, undefined, "height");
  useEffect(() => bringMarkIntoBox(body.current), [items]);

  const columns = useMemo(() => reads.map(sourceColumn), [reads]);
  const own = columnsOfRow(source.row, columns.filter((column) => column.child === null).map((column) => column.column));
  const children = Object.entries(source.datasets ?? {});
  const reading = (name: string) => columns.some((column) => column.child?.toLowerCase() === name.toLowerCase());
  const whole = all || columns.length === 0;
  const shownColumns = whole ? [...own.read, ...own.others] : own.read;
  const shownChildren = children.filter(([name]) => whole || reading(name));
  const hidden = own.others.length + children.filter(([name]) => !reading(name)).length;
  const key = keyColumns.length > 0 && keyColumns.length === source.keyParts.length
    ? keyColumns.map((column, index) => `${column}=${source.keyParts[index] ?? "null"}`).join(", ")
    : source.keyParts.map((part) => part ?? "null").join(", ");
  const { leaf } = splitPath(path);

  return (
    <div className={cn("flex flex-col gap-2 transition-opacity", dimmed && "opacity-60")}>
      <div className="flex flex-col gap-0.5 text-[12px]" data-testid="value-check-source-where">
        <span className="flex min-w-0 items-center gap-1.5">
          <Database className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
          <TruncatedText text={table} mono maxWidth={420} title="The record table" />
        </span>
        <span className="min-w-0 break-words pl-5 text-muted-foreground" data-testid="value-check-source-row">
          {source.originFile ? `${source.originFile}${source.originRow !== null && source.originRow !== undefined ? `, row ${source.originRow.toLocaleString("en-US")}` : ""}` : "No file recorded"}
          {key !== "" && <span className="font-mono">{` · ${key}`}</span>}
        </span>
      </div>

      <p className="text-[12px] text-muted-foreground" data-testid="value-check-source-reads">
        <span className="font-mono text-foreground">{leaf}</span>
        {columns.length > 0
          ? ` reads ${reads.join(", ")}`
          : checked
            ? " reads no source column: nothing of the row writes it."
            : ": check it to see which columns it reads. The whole row is shown until then."}
      </p>

      {source.omitted && <Alert><Info /><AlertDescription>{source.omitted}</AlertDescription></Alert>}

      <div ref={body} className="flex flex-col gap-3 overflow-auto rounded-md border p-2 font-mono text-[12px]" data-testid="value-check-source-data">
        {shownColumns.length > 0 && (
          <table className="w-full table-fixed border-collapse" data-testid="value-check-source-columns">
            <tbody>
              {shownColumns.map((column) => (
                <tr key={column} className={cn("border-b last:border-b-0", own.read.includes(column) && "bg-warning/10")} data-read={own.read.includes(column) || undefined}>
                  <td className="w-2/5 py-0.5 pr-2 align-top break-all text-muted-foreground">{column}</td>
                  <td className="py-0.5 align-top"><SourceValue value={source.row[column]} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        {shownChildren.map(([name, rows]) => (
          <ChildRows key={name} name={name} rows={rows} reads={columns.filter((column) => column.child?.toLowerCase() === name.toLowerCase()).map((column) => column.column)} items={items} all={whole} />
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

/** A child dataset's rows, the columns the attribute reads first (or alone), the rows the check found wrong marked by their number. */
function ChildRows({ name, rows, reads, items, all }: { name: string; rows: DeliveryPreviewRows; reads: readonly string[]; items: readonly number[]; all: boolean }) {
  const { read, others } = columnsOfRow(rows.rows[0] ?? {}, reads);
  const columns = all || read.length === 0 ? [...read, ...others] : read;
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
            <th className="w-8 py-0.5 pr-2 font-normal">#</th>
            {columns.map((column) => <th key={column} className="py-0.5 pr-2 font-normal break-all">{column}</th>)}
          </tr>
        </thead>
        <tbody>
          {rows.rows.map((row, index) => {
            const wrong = items.includes(index + 1);
            return (
              <tr key={index} className={cn("border-b last:border-b-0", wrong && "bg-warning/10")} data-hit={wrong || undefined}>
                <td className="py-0.5 pr-2 align-top text-muted-foreground tabular-nums">{index + 1}</td>
                {columns.map((column) => <td key={column} className="py-0.5 pr-2 align-top"><SourceValue value={row[column]} /></td>)}
              </tr>
            );
          })}
        </tbody>
      </table>
      {past.length > 0 && (
        <p className="font-sans text-[12px] text-muted-foreground" data-testid="value-check-source-past">
          {`${past.length === 1 ? `Row ${past[0]} is` : `Rows ${past.join(", ")} are`} past the rows shown here; Export rows in the Data tab has them.`}
        </p>
      )}
    </section>
  );
}
