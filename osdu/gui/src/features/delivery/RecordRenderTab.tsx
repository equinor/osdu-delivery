import { useState } from "react";
import { CircleAlert, FileCode2, Info, Loader2 } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { CodeView } from "@/components/CodeView";
import { DiffView } from "@/components/DiffView";
import { TruncatedText } from "@/components/TruncatedText";
import { cn } from "@/lib/utils";
import type { DeliveryOsduRead, DeliveryPreviewInputs, DeliveryRecord, DeliveryRecordPreview } from "../../api/delivery";
import { Fact, FactGrid, NoFact } from "./Facts";
import { canonicalText, differences, envelopeFirst, withoutOsduFields, type DifferenceKind } from "./osduDocument";
import { PreviewActionBadge, PreviewAssertions, PreviewChoices, PreviewEnrichment, PreviewHolds, PreviewSearches } from "./RecordPreviewView";
import type { Settled } from "./answers";

/**
 * What one render answered: the record rendered from its row, and the read of what OSDU holds (null when the record has
 * no id OSDU could hold), each with its result or why there is none, and when it was asked for.
 */
export interface RenderResults {
  preview: Settled<DeliveryRecordPreview>;
  read: Settled<DeliveryOsduRead> | null;
  renderedUtc: string;
}

const KIND_LABELS: Record<DifferenceKind, string> = {
  changed: "changed",
  onlyInOsdu: "only in OSDU",
  onlyInPreview: "would be added",
  placeholder: "given when sent",
};

const KIND_TONES: Record<DifferenceKind, string> = {
  changed: "bg-info/12 text-info",
  onlyInOsdu: "bg-warning/15 text-warning",
  onlyInPreview: "bg-success/15 text-success",
  placeholder: "bg-muted text-muted-foreground",
};

/** The caption voice of the inputs grid, the one `Facts` captions its values in. */
const CAPTION = "text-[11px] font-medium uppercase tracking-wide text-muted-foreground";

/** The mapping and cache a document was rendered from. */
interface RenderInputs {
  mapping: string | null;
  cache: string | null;
}

/** A cache and the version of it read, as one value; "none" for a render that read no cache. */
function cacheText(cache: string | null | undefined, version: string | null | undefined): string {
  return !cache ? "none" : version ? `${cache} ${version}` : cache;
}

/**
 * The mapping and cache a stored render context names, the inputs that rendered the delivered document. The rest of the
 * context (the flow's parameters, the schema snapshot, the system properties) is the submission's, the same for every
 * record it carried, and the document itself shows what the parameters became. Null when there is no context, or it
 * does not read as one.
 */
function deliveredInputs(context: string | null): RenderInputs | null {
  if (context === null || context === "") {
    return null;
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(context);
  } catch {
    return null;
  }

  if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
    return null;
  }

  const fields = parsed as Record<string, unknown>;
  const text = (name: string) => {
    const value = fields[name];
    return typeof value === "string" && value !== "" ? value : null;
  };
  return { mapping: text("mapping"), cache: cacheText(text("cache"), text("cacheVersion")) };
}

/** The inputs a render used now, as the node reported them. */
function currentInputs(inputs: DeliveryPreviewInputs): RenderInputs {
  return { mapping: inputs.mapping, cache: cacheText(inputs.cachePartition, inputs.cacheVersion) };
}

/** One input as a cell of the grid: the value, flagged where it moved on since the delivered one. */
function InputCell({ value, empty, changed = false, className, testId }: {
  value: string | null;
  empty: string;
  changed?: boolean;
  className?: string;
  testId: string;
}) {
  return (
    <span className={cn("flex min-w-0 items-baseline gap-2", className)} data-testid={testId}>
      {value === null ? <NoFact>{empty}</NoFact> : <TruncatedText text={value} mono maxWidth={360} />}
      {changed && <Badge variant="secondary" className="bg-info/12 text-info" data-testid={`${testId}-changed`}>changed</Badge>}
    </span>
  );
}

/**
 * What the record is rendered from, side by side: the mapping and cache version that rendered the document OSDU holds,
 * and those a render used now, each flagged where it moved on since. A document rendered from other inputs can differ
 * from OSDU's copy though its source row never changed.
 */
function InputsGrid({ delivered, now, removed }: { delivered: RenderInputs | null; now: RenderInputs | null; removed: boolean }) {
  const differs = (pick: (inputs: RenderInputs) => string | null) => {
    const was = delivered === null ? null : pick(delivered);
    const is = now === null ? null : pick(now);
    return was !== null && is !== null && was !== is;
  };
  const deliveredEmpty = delivered === null ? "not delivered" : "not recorded";
  // The caption column is as wide as a fact's caption, so the grid's values line up with the facts under it.
  return (
    <div
      className="grid grid-cols-[7rem_minmax(0,max-content)_minmax(0,max-content)] items-baseline gap-x-2.5 gap-y-1 text-[12px] leading-5"
      data-testid="record-render-inputs"
    >
      <span />
      <span className={CAPTION}>{removed ? "Last delivered" : "Delivered"}</span>
      <span className={cn(CAPTION, "pl-8")}>Rendered now</span>
      <span className={cn(CAPTION, "text-right")}>Mapping</span>
      <InputCell value={delivered?.mapping ?? null} empty={deliveredEmpty} testId="record-render-delivered-mapping" />
      <InputCell value={now?.mapping ?? null} empty="-" changed={differs((inputs) => inputs.mapping)} className="pl-8" testId="record-render-now-mapping" />
      <span className={cn(CAPTION, "text-right")}>Cache</span>
      <InputCell value={delivered?.cache ?? null} empty={deliveredEmpty} testId="record-render-delivered-cache" />
      <InputCell value={now?.cache ?? null} empty="-" changed={differs((inputs) => inputs.cache)} className="pl-8" testId="record-render-now-cache" />
    </div>
  );
}

/**
 * The record rendered now, beside what OSDU holds: what its route does with the manifest (the DDMS it reaches, the values
 * it adds or carries forward, which explain a difference the comparison alone cannot), what the next run would do with
 * it, why a delivery would hold it, what the mapping's assertions found of it, and the document itself, compared with
 * OSDU's copy or whole. OSDU's own fields (its version, who changed it and when) are set aside, keys are compared in
 * order, and a value the platform gives when the record is sent (a dataset id the File service mints) is counted as such
 * rather than as a change.
 */
function RenderResult({ results }: { results: RenderResults }) {
  const [view, setView] = useState<"beside" | "document">("beside");
  const preview = results.preview;
  const read = results.read ?? { result: null, failure: null };

  const result = preview?.result ?? null;
  const document = result?.document ?? null;
  const rendered = document === null ? null : document.sent ?? document.rendered ?? null;
  const decision = result?.decision ?? null;
  const readResult = read?.result ?? null;
  // Worked out on each render: a render follows only a click or an answer, and the texts compare by value.
  const holds = readResult?.found === true && readResult.record ? withoutOsduFields(readResult.record) : null;
  const compared = holds !== null && rendered !== null
    ? { ...differences(holds, rendered), original: canonicalText(holds), modified: canonicalText(rendered) }
    : null;
  const shown = compared === null ? "document" : view;
  // Nothing to set it beside: the record has no OSDU id a read could follow, or OSDU answered that it holds none.
  const noTarget = results.read === null;
  const absent = readResult !== null && !readResult.found;

  return (
    <div className="flex flex-col gap-3">
      {preview?.failure && (
        <Alert variant="destructive" data-testid="record-render-failed">
          <CircleAlert />
          <AlertTitle>The record could not be rendered</AlertTitle>
          <AlertDescription className="whitespace-pre-wrap">{preview.failure}</AlertDescription>
        </Alert>
      )}
      {read?.failure && (
        <Alert variant="destructive" data-testid="record-render-read-failed">
          <CircleAlert />
          <AlertTitle>OSDU could not be read</AlertTitle>
          <AlertDescription className="whitespace-pre-wrap">{read.failure}</AlertDescription>
        </Alert>
      )}
      {result !== null && !result.found && (
        <Alert data-testid="record-render-no-row">
          <Info />
          <AlertTitle>The record's row is not in the ingestion tables now</AlertTitle>
          <AlertDescription>{result.reason}</AlertDescription>
        </Alert>
      )}
      {result?.found && document === null && (
        <Alert data-testid="record-render-no-document">
          <Info />
          <AlertTitle>The row renders no document now</AlertTitle>
          <AlertDescription>{result.noDocument}</AlertDescription>
        </Alert>
      )}

      {result?.found && (result.route.ddms || result.notes.length > 0 || decision !== null) && (
        <FactGrid>
          {(result.route.ddms || result.notes.length > 0) && (
            <Fact label="Route" wide testId="record-render-route">
              <ul className="flex flex-col gap-0.5">
                {result.route.ddms && <li data-testid="record-render-ddms">{result.route.ddms}</li>}
                {result.notes.map((note) => <li key={note} className="text-muted-foreground">{note}</li>)}
              </ul>
            </Fact>
          )}
          {decision !== null && (
            <Fact label="Next run" wide testId="record-render-next">
              <span className="inline-flex flex-wrap items-baseline gap-2">
                <PreviewActionBadge decision={decision} />
                <span className="text-muted-foreground">{decision.reason}</span>
              </span>
            </Fact>
          )}
        </FactGrid>
      )}

      {document !== null && <PreviewHolds document={document} testId="record-render-holds" />}
      {result !== null && result.issues.length > 0 && (
        <Alert data-testid="record-render-issues">
          <Info />
          <AlertTitle>The preflight's warnings</AlertTitle>
          <AlertDescription>
            <ul className="list-disc pl-5">{result.issues.map((issue) => <li key={issue}>{issue}</li>)}</ul>
          </AlertDescription>
        </Alert>
      )}
      {document?.omitted && <Alert data-testid="record-render-omitted"><Info /><AlertDescription>{document.omitted}</AlertDescription></Alert>}
      {document !== null && <PreviewAssertions document={document} testId="record-render-assertions" />}

      {rendered !== null && (noTarget || absent) && (
        <Alert data-testid={noTarget ? "record-render-no-target" : "record-render-absent"}>
          <Info />
          <AlertTitle>{noTarget ? "OSDU holds nothing of this record to set it beside" : "OSDU holds no record under this id"}</AlertTitle>
          <AlertDescription>
            {noTarget
              ? "The record has no OSDU id this flow delivered to, or was removed. A delivery would send the document below."
              : "A delivery would create it with the document below."}
          </AlertDescription>
        </Alert>
      )}

      {rendered !== null && read !== null && (
        <div className="flex flex-col gap-2">
          <div className="flex flex-wrap items-center gap-3 text-[12px] text-muted-foreground">
            {compared !== null && (
              <ToggleGroup
                type="single"
                size="sm"
                variant="outline"
                value={shown}
                onValueChange={(next) => { if (next === "beside" || next === "document") { setView(next); } }}
                data-testid="record-render-view"
              >
                <ToggleGroupItem value="beside" data-testid="record-render-view-beside">Beside OSDU</ToggleGroupItem>
                <ToggleGroupItem value="document" data-testid="record-render-view-document">Whole document</ToggleGroupItem>
              </ToggleGroup>
            )}
            {shown === "beside" && compared !== null && (
              <span className="flex flex-wrap items-center gap-2" data-testid="record-render-counts">
                {compared.items.length === 0
                  ? <Badge variant="secondary" className="bg-success/15 text-success" data-testid="record-render-same">OSDU holds what the record renders to now</Badge>
                  : (Object.keys(KIND_LABELS) as DifferenceKind[]).map((kind) => {
                    const count = compared.items.filter((item) => item.kind === kind).length;
                    return count === 0 ? null : <Badge key={kind} variant="secondary" className={KIND_TONES[kind]}>{`${count} ${KIND_LABELS[kind]}`}</Badge>;
                  })}
                {compared.truncated && <span>(the first {compared.items.length} differences)</span>}
              </span>
            )}
            {shown === "document" && document !== null && <span>{document.sent ? "As the route sends it" : "As the mapping renders it"}</span>}
          </div>
          {/* The comparison stays mounted while the whole document is in view, and is only hidden: Monaco's diff editor
              throws as it unmounts (its models are disposed before the widget lets go of them), and it lays itself out
              again once shown. */}
          {compared !== null && (
            <div className={shown === "beside" ? "flex flex-col gap-2" : "hidden"}>
              <DiffView
                original={compared.original}
                modified={compared.modified}
                language="json"
                height={520}
                foldUnchanged
                sideLabels={{
                  original: `In OSDU${readResult?.version !== null && readResult?.version !== undefined ? ` (version ${readResult.version})` : ""}`,
                  modified: "Rendered now",
                }}
                data-testid="record-render-diff"
              />
            </div>
          )}
          {/* A value the platform gives when the record is sent reads as what it stands for, in place. */}
          {shown === "document" && (
            <CodeView value={JSON.stringify(envelopeFirst(rendered), null, 2)} language="json" height={520} data-testid="record-render-document" />
          )}
        </div>
      )}

      {document !== null && document.searches.length > 0 && (
        <div className="flex flex-col gap-1">
          <h3 className="text-[13px] font-medium">What the render found by searching the platform</h3>
          <PreviewSearches searches={document.searches} testId="record-render-searches" />
        </div>
      )}
      {document !== null && <PreviewChoices choices={document.choices} unverified={document.unverified} testId="record-render-coalesce" />}
      {document !== null && <PreviewEnrichment document={document} testId="record-render-enrichment" />}
    </div>
  );
}

/**
 * The record's output, built from the data available now: its manifest rendered from its current source row with the
 * flow's mapping and cache, set beside what OSDU holds. Only the manifest is built; the DDMS sections a DDMS route sends
 * besides it are not. Above the render, the mapping and cache version that rendered the document OSDU holds beside those
 * the render used, so a change of either is seen at a glance. The ledger keeps what it sent as a hash, not as the
 * document, so the document shown is always the one the record renders to now.
 */
export function RecordRenderTab({ record, canOperate, canRender, rendering, onRender, results }: {
  record: DeliveryRecord;
  /** Whether the viewer may render: the operate policy, which every signed-in user holds. */
  canOperate: boolean;
  /** Whether a render may be asked for: the record's flow is known and no other request of the page is in flight. */
  canRender: boolean;
  /** Whether a render is under way. */
  rendering: boolean;
  onRender: () => void;
  /** What the last render answered; null before any has. */
  results: RenderResults | null;
}) {
  const preview = results?.preview.result ?? null;

  return (
    <Card className="gap-3 rounded-lg p-3" data-testid="record-render">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <Button
          variant="outline"
          size="sm"
          onClick={onRender}
          disabled={!canOperate || !canRender || rendering}
          title={canOperate ? undefined : "A render reads the flow's tables and OSDU with the flow's credentials, which takes a signed-in session."}
          data-testid="record-render-run"
        >
          {rendering ? <Loader2 className="animate-spin" /> : <FileCode2 />}
          {results === null ? "Render" : "Render again"}
        </Button>
        <p className="min-w-0 flex-1 basis-96 text-[12px] text-muted-foreground" data-testid="record-render-scope">
          Builds the record&apos;s manifest from the data available now (source row, mapping, cache) and sets it beside
          what OSDU holds; nothing is sent. DDMS sections (bulk data, series, rows) are not built.
        </p>
      </div>
      <InputsGrid
        delivered={deliveredInputs(record.renderContext)}
        now={preview === null ? null : currentInputs(preview.inputs)}
        removed={record.status === "deleted"}
      />
      {results !== null && <RenderResult key={results.renderedUtc} results={results} />}
    </Card>
  );
}
