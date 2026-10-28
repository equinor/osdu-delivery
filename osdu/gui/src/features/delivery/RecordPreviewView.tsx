import { useRef, useState, type ReactNode } from "react";
import { Link as RouterLink } from "react-router-dom";
import {
  Ban, CircleMinus, CirclePause, CirclePlus, Download, ExternalLink, FileWarning, Info, RefreshCw, SearchX, TriangleAlert,
  type LucideIcon,
} from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { CodeView } from "@/components/CodeView";
import { DataTable, type Column } from "@/components/DataTable";
import { ExplainMark, ExplainTip } from "@/components/ExplainTip";
import { IdChip } from "@/components/IdChip";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import { useScrollFit } from "@/hooks/useScrollFit";
import { runValueAssignment } from "@/lib/runValues";
import { cn } from "@/lib/utils";
import {
  deliveryRecordRoute,
  type DeliveryPreviewAction,
  type DeliveryPreviewChoice,
  type DeliveryPreviewDecision,
  type DeliveryPreviewDocument,
  type DeliveryPreviewEnrichStep,
  type DeliveryPreviewFile,
  type DeliveryPreviewPayloadPart,
  type DeliveryPreviewReference,
  type DeliveryPreviewSearch,
  type DeliveryPreviewStep,
  type DeliveryRecordPreview,
} from "../../api/delivery";
import { AssertionFindingsView } from "./AssertionFindingsView";
import { effectValueText, enrichOutcomeText, suppliesText } from "./enrichSteps";
import { RecordStatusBadge } from "./DeliveryBadges";
import { Fact, FactGrid, NoFact } from "./Facts";
import { IssueMessage } from "./IssueMessage";
import { assertionHoldText, heldByAssertion, wouldHold } from "./previewHolds";
import { RecordName } from "./RecordName";
import { downloadJson, envelopeFirst, fileNameOf, formatBytes } from "./osduDocument";

/**
 * What the next run would do with the record, drawn as a delivery's other states are: a neutral chip led by a glyph in the
 * action's tone, so a page of them never reads as a block of colour. Sending is info, holding is warning, a blocked record
 * is destructive, and a skip is quiet.
 */
const ACTIONS: Record<DeliveryPreviewAction, { word: string; icon: LucideIcon; tone: string }> = {
  create: { word: "would create", icon: CirclePlus, tone: "text-info" },
  updateMetadata: { word: "would update the record", icon: RefreshCw, tone: "text-info" },
  updatePayload: { word: "would update the payload", icon: RefreshCw, tone: "text-info" },
  updateBoth: { word: "would update record and payload", icon: RefreshCw, tone: "text-info" },
  skip: { word: "unchanged, would skip", icon: CircleMinus, tone: "text-muted-foreground" },
  hold: { word: "would hold", icon: CirclePause, tone: "text-warning" },
  blocked: { word: "blocked", icon: Ban, tone: "text-destructive" },
};

const HOW_LABELS: Record<DeliveryRecordPreview["asked"]["how"], string> = {
  first: "the first record of the scope",
  "delivery key": "a delivery key",
  "osdu id": "an OSDU id the ledger holds",
  "source key": "a source key",
  "key parts": "the key's parts",
};

/** What a preview's tabs show: the document, how it was rendered, how a delivery would send it, and the rows it came from. */
const PREVIEW_TABS = ["document", "render", "delivery", "source"] as const;
type PreviewTab = (typeof PREVIEW_TABS)[number];

/** Which form of the document is shown: as the route sends it, or as the mapping renders it. */
type DocumentForm = "sent" | "rendered";

/** The room the document leaves under itself in the page (the page's bottom padding), and the least it shrinks to. */
const DOCUMENT_FIT_BELOW = 24;
const DOCUMENT_FIT_MIN = 320;

/** What the next run would do with the record, as a chip; the reason is on hover. */
export function PreviewActionBadge({ decision }: { decision: DeliveryPreviewDecision }) {
  const action = ACTIONS[decision.action];
  const Icon = action?.icon ?? CircleMinus;
  return (
    <Badge
      variant="secondary"
      className="shrink-0 rounded-md border-border bg-muted/60 font-normal text-foreground"
      title={decision.reason}
      data-action={decision.action}
      data-testid="preview-action"
    >
      <Icon className={cn("size-3.5 shrink-0", action?.tone ?? "text-muted-foreground")} aria-hidden />
      {action?.word ?? decision.action}
    </Badge>
  );
}

/**
 * Why a delivery would hold the record, as a preview says it: each of the render's own holds under the property it names,
 * with what each alternative of a `$coalesce` said as a numbered list, then a failed assertion of the mapping whose failure
 * holds it at the check before sending, its document kept. Sized like the facts it sits among.
 */
export function HoldReasons({ document, testId }: { document: DeliveryPreviewDocument; testId: string }) {
  return (
    <div className="flex min-w-0 flex-col gap-2" data-testid={testId}>
      {document.holds.map((hold) => <IssueMessage key={hold} text={hold} compact />)}
      {heldByAssertion(document) && <span className="text-[12px] leading-5" data-testid={`${testId}-assertion`}>{assertionHoldText(document)}</span>}
    </div>
  );
}

/**
 * What the mapping's assertions found of the rendered document, kept to a height of its own so a long list scrolls in
 * place. Nothing for a mapping that states none.
 */
export function PreviewAssertions({ document, testId }: { document: DeliveryPreviewDocument; testId: string }) {
  if (document.assertions === null || document.assertions === undefined) {
    return null;
  }

  return (
    <div className="max-h-64 overflow-y-auto" data-testid={testId}>
      <AssertionFindingsView findings={document.assertions} />
    </div>
  );
}

const searchColumns: Column<DeliveryPreviewSearch>[] = [
  { id: "kind", header: "Kind searched", render: (row) => <TruncatedText text={row.kind} mono maxWidth={260} /> },
  { id: "field", header: "Field", render: (row) => <span className="font-mono text-[12px]">{row.field}</span> },
  { id: "value", header: "Value", render: (row) => <TruncatedText text={row.value} maxWidth={200} /> },
  { id: "outcome", header: "Outcome", render: (row) => <Badge variant="outline">{row.outcome}</Badge> },
  { id: "id", header: "Found", fill: true, render: (row) => (row.id === null || row.id === undefined ? <span className="text-muted-foreground">-</span> : <RecordName id={row.id} copy className="max-w-[420px] text-[12px]" copyTestId="copy-preview-found" />) },
];

/** The searches a render made, one row each: what it asked the platform and what it found. */
export function PreviewSearches({ searches, testId }: { searches: DeliveryPreviewSearch[]; testId: string }) {
  return <DataTable columns={searchColumns} rows={searches} rowKey={(row) => `${row.kind}|${row.field}|${row.value}`} emptyMessage="No searches." data-testid={testId} />;
}

const choiceColumns: Column<DeliveryPreviewChoice>[] = [
  { id: "target", header: "Property", render: (row) => <TruncatedText text={row.target} mono maxWidth={320} /> },
  { id: "alternative", header: "Taken from", render: (row) => <span className="whitespace-nowrap text-[12px]">{`alternative ${row.alternative} of ${row.of}`}</span> },
  { id: "origin", header: "Reads", fill: true, render: (row) => <TruncatedText text={row.origin} mono maxWidth={520} /> },
  {
    id: "values",
    header: "Values",
    render: (row) => (
      <span className="inline-flex items-center gap-1.5 text-[12px]">
        {row.values.toLocaleString()}
        {row.unverified && <Badge variant="outline" className="border-warning/40 text-warning" title="A value it gave is a reference to a record the cache does not hold, written because the mapping says $unverified">unverified</Badge>}
      </span>
    ),
  },
];

/**
 * Where each $coalesce node of the mapping took its value from, and the references written without a record the cache
 * holds: what makes a record as complete as its sources allow, and what of it is not verified against the partition.
 */
export function PreviewChoices({ choices, unverified, testId }: { choices: DeliveryPreviewChoice[]; unverified: string[]; testId: string }) {
  if (choices.length === 0 && unverified.length === 0) {
    return null;
  }

  return (
    <div className="flex flex-col gap-2" data-testid={testId}>
      {choices.length > 0 && (
        <div className="flex flex-col gap-1">
          <h3 className="text-[13px] font-medium">Where each $coalesce took its value</h3>
          <DataTable columns={choiceColumns} rows={choices} rowKey={(row) => `${row.target}|${row.alternative}`} emptyMessage="No $coalesce node gave a value." data-testid={`${testId}-choices`} />
        </div>
      )}
      {unverified.length > 0 && (
        <div className="flex flex-col gap-1" data-testid={`${testId}-unverified`}>
          <h3 className="text-[13px] font-medium">References written without a record the cache holds</h3>
          <ul className="flex flex-col gap-1 text-[12px]">
            {unverified.map((id) => <li key={id}><RecordName id={id} copy className="max-w-[520px] text-[12px]" copyTestId="copy-preview-unverified" /></li>)}
          </ul>
        </div>
      )}
    </div>
  );
}

/** The most changes a step's row lists; the rest are counted, and the document shows them all. */
const EFFECTS_SHOWN = 8;

const enrichColumns: Column<DeliveryPreviewEnrichStep>[] = [
  { id: "step", header: "Enrichment", render: (row) => <span className="font-mono text-[12px]">{row.step}</span> },
  {
    id: "mapping",
    header: "Mapping",
    render: (row) => {
      const supplies = suppliesText(row.supplies);
      return (
        <span className="flex min-w-0 flex-col gap-0.5">
          {row.mapping
            ? <TruncatedText text={row.mapping} mono maxWidth={220} />
            : <span className="text-[12px] text-muted-foreground" title="The enrichment adds no paths to the record">no paths</span>}
          {supplies !== null && (
            <span className="text-[11px] text-muted-foreground" title="What the enrichment does beside adding paths: the records it adds, the record's bulk data, the workflow run over it" data-testid="preview-enrich-supplies">
              {`with ${supplies}`}
            </span>
          )}
        </span>
      );
    },
  },
  {
    id: "outcome",
    header: "Outcome",
    render: (row) => (
      <span className="whitespace-nowrap text-[12px]" title={row.holds.length > 0 ? row.holds.join("; ") : undefined}>
        {enrichOutcomeText(row)}
      </span>
    ),
  },
  {
    id: "effects",
    header: "What it added",
    fill: true,
    render: (row) => (row.effects.length === 0 && (row.records ?? []).length === 0 && (row.keptRecords ?? []).length === 0
      ? <span className="text-muted-foreground">-</span>
      : (
        <ul className="flex flex-col gap-0.5 text-[12px]">
          {(row.keptRecords ?? []).map((id) => (
            <li key={`kept|${id}`} className="flex min-w-0 items-baseline gap-1.5" data-testid="preview-enrich-kept-record">
              <Badge variant="outline" className="shrink-0 px-1 py-0 text-[10px]" title="Added by the record's last delivery, and still linked while the enrichment is set aside">kept</Badge>
              <RecordName id={id} copy className="min-w-0 max-w-[420px] text-[12px]" copyTestId="copy-preview-enrich-kept-record" />
            </li>
          ))}
          {(row.records ?? []).map((added) => (
            <li key={added.id} className="flex min-w-0 items-baseline gap-1.5" data-testid="preview-enrich-record">
              <Badge variant="outline" className="shrink-0 px-1 py-0 text-[10px]" title={`Added as ${added.kind} from row ${added.row + 1} of the enrichment's dataset, under the id the record links`}>record</Badge>
              <RecordName id={added.id} copy className="min-w-0 max-w-[420px] text-[12px]" copyTestId="copy-preview-enrich-record" />
            </li>
          ))}
          {row.effects.slice(0, EFFECTS_SHOWN).map((effect) => {
            const value = effectValueText(effect.value);
            return (
              <li key={`${effect.kind}|${effect.path}`} className="flex min-w-0 items-baseline gap-1.5">
                <Badge variant="outline" className="shrink-0 px-1 py-0 text-[10px]" title={effect.kind === "add" ? "Items added to the list here" : "A value set where the record held none"}>{effect.kind}</Badge>
                <span className="shrink-0 font-mono">{effect.path}</span>
                {value !== null && <TruncatedText text={value} mono maxWidth={360} className="text-muted-foreground" />}
              </li>
            );
          })}
          {row.effects.length > EFFECTS_SHOWN && (
            <li className="text-muted-foreground">{`and ${(row.effects.length - EFFECTS_SHOWN).toLocaleString()} more`}</li>
          )}
        </ul>
      )),
  },
];

/**
 * What each enrichment of the mapping added to the record, in the order they render (osdu/docs/reference/flow/enrich.md):
 * its enrichment mapping, how many rows the record had, and each path it set or list it added to. The document is the
 * record with every enrichment in it, the one a delivery sends. Nothing for a mapping that declares no enrichment.
 */
export function PreviewEnrichment({ document, testId }: { document: DeliveryPreviewDocument; testId: string }) {
  const steps = document.enrichment ?? [];
  if (steps.length === 0) {
    return null;
  }

  return (
    <div className="flex flex-col gap-1" data-testid={testId}>
      <h3 className="text-[13px] font-medium">What each enrichment added</h3>
      <DataTable columns={enrichColumns} rows={steps} rowKey={(row) => row.step} emptyMessage="The flow declares no step." data-testid={`${testId}-steps`} />
    </div>
  );
}

const referenceColumns: Column<DeliveryPreviewReference>[] = [
  { id: "id", header: "OSDU id", fill: true, render: (row) => <RecordName id={row.id} copy className="max-w-[420px] text-[12px]" copyTestId="copy-preview-reference" /> },
  { id: "property", header: "Property", render: (row) => <span className="font-mono text-[12px]">{row.property}</span> },
  {
    id: "holder",
    header: "In the ledger",
    render: (row) => (row.holder
      ? (
        <span className="inline-flex items-center gap-2">
          <RecordStatusBadge status={row.holder.status} testId="preview-reference-status" />
          <RouterLink
            to={deliveryRecordRoute({ flowId: row.holder.flowId, deliveryKey: row.holder.deliveryKey })}
            className="text-[12px] text-primary hover:underline"
            data-testid="preview-reference-link"
          >
            {row.holder.label ?? row.holder.sourceKey}
          </RouterLink>
        </span>
      )
      : <span className="text-[12px] text-muted-foreground">not a record of the ledger</span>),
  },
];

const fileColumns: Column<DeliveryPreviewFile>[] = [
  { id: "name", header: "File", fill: true, render: (row) => <TruncatedText text={row.name} mono maxWidth={360} /> },
  { id: "size", header: "Size", align: "right", render: (row) => <span className="font-mono tabular-nums" title={`${row.size.toLocaleString()} bytes`}>{formatBytes(row.size)}</span> },
  { id: "modified", header: "Modified", render: (row) => <RelativeTime value={row.modifiedUtc ?? null} /> },
  {
    id: "shape",
    header: "Parquet",
    render: (row) => (row.parquet
      ? (
        <span className="font-mono text-[12px] tabular-nums" title={row.parquet.columnNames.join(", ") + (row.parquet.columnNamesTruncated ? ", ..." : "")}>
          {`${row.parquet.rows.toLocaleString()} rows x ${row.parquet.columns} columns`}
        </span>
      )
      : <span className="text-muted-foreground">-</span>),
  },
  { id: "problem", header: "Footer", render: (row) => (row.footerProblem ? <TruncatedText text={row.footerProblem} maxWidth={320} /> : <span className="text-muted-foreground">-</span>) },
];

/** One payload part: where its files are, how many and how large, and each file listed. */
function PayloadPart({ part }: { part: DeliveryPreviewPayloadPart }) {
  return (
    <div className="flex flex-col gap-2" data-testid="preview-payload-part">
      <div className="flex flex-wrap items-baseline gap-2 text-[13px]">
        <span className="font-medium">{part.role ?? part.payload}</span>
        {part.role && <span className="font-mono text-[12px] text-muted-foreground">{part.payload}</span>}
        {part.location && <TruncatedText text={part.location} mono maxWidth={420} copy />}
        {part.problem === null || part.problem === undefined
          ? (
            <span className="text-muted-foreground">
              {`${part.totalFiles.toLocaleString()} ${part.totalFiles === 1 ? "file" : "files"}, ${formatBytes(part.totalBytes)}`}
              {part.truncated ? `; the first ${part.files.length} listed` : ""}
            </span>
          )
          : <span className="text-warning" data-testid="preview-payload-problem">{part.problem}</span>}
      </div>
      {part.files.length > 0 && (
        <DataTable columns={fileColumns} rows={part.files} rowKey={(row) => row.name} emptyMessage="No files." data-testid="preview-payload-files" />
      )}
    </div>
  );
}

/** One request of the route, a line of the list: its place, the service and the request, then what it does and returns. */
function Step({ step }: { step: DeliveryPreviewStep }) {
  const [open, setOpen] = useState(false);
  return (
    <li className="flex flex-col gap-0.5 py-2" data-testid="preview-step">
      <div className="flex min-w-0 flex-wrap items-baseline gap-x-2 gap-y-0.5 text-[13px]">
        <span className="w-5 shrink-0 text-right font-mono text-[12px] tabular-nums text-muted-foreground">{`${step.order}.`}</span>
        <Badge variant="outline" className="shrink-0">{step.service}</Badge>
        <span className="min-w-0 break-all font-mono text-[12px]">{step.request}</span>
        {step.repeats && <span className="text-[12px] text-muted-foreground">{step.repeats}</span>}
        {step.body && (
          <Button variant="link" size="sm" className="ml-auto h-5 px-0 text-[12px]" onClick={() => setOpen((was) => !was)} data-testid="preview-step-body-toggle">
            {open ? "Hide the body" : "Body"}
          </Button>
        )}
      </div>
      <p className="pl-7 text-[12px] text-muted-foreground">
        {step.what}
        {step.returns ? `${/[.!?]$/.test(step.what) ? "" : "."} Returns ${step.returns}.` : ""}
      </p>
      {open && step.body && (
        <div className="pl-7 pt-1">
          <CodeView value={JSON.stringify(step.body, null, 2)} language="json" height={260} data-testid="preview-step-body" />
        </div>
      )}
    </li>
  );
}

/** A section of a tab: its heading, with what it is about on an info mark where the heading alone cannot say. */
function Section({ title, about, testId, children }: { title: string; about?: string; testId: string; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-1.5" data-testid={testId}>
      <h3 className="flex items-center gap-1 text-[13px] font-medium">
        {title}
        {about !== undefined && <ExplainMark title={title} text={about} testId={`${testId}-about`} />}
      </h3>
      {children}
    </section>
  );
}

/** How the preview picked its row, as the verdict says it: the scope's first, or the form the key given was read as. */
function pickedText(asked: DeliveryRecordPreview["asked"]): string {
  if (asked.key) {
    return `read as ${HOW_LABELS[asked.how] ?? asked.how}`;
  }

  return `${HOW_LABELS.first}${asked.scopeRecords !== null && asked.scopeRecords !== undefined ? ` (${asked.scopeRecords.toLocaleString()} in scope)` : ""}`;
}

/** The line of a notice under the verdict: a glyph in its tone and a short text, the whole of it on hover with a copy. */
function Notice({ icon: Icon, tone, title, text, testId, children }: {
  icon: LucideIcon;
  tone: string;
  title: string;
  text: string;
  testId: string;
  children: ReactNode;
}) {
  return (
    <ExplainTip title={title} text={text} testId={testId}>
      <span className="inline-flex min-w-0 max-w-full cursor-default items-center gap-1.5 text-[12px]" data-testid={testId}>
        <Icon className={cn("size-3.5 shrink-0", tone)} aria-hidden />
        <span className="min-w-0 truncate">{children}</span>
      </span>
    </ExplainTip>
  );
}

/**
 * The answer of a preview in a few lines: what the next run would do with which record and why, where it would land and
 * how the ledger holds it, where its row came from and how it was picked; then, only when there is one, what a reader
 * should know before trusting it (rows passed over, the preflight's warnings, assertions that failed, references nothing
 * verified), each in a line with the whole on hover, and, when a delivery would hold the record, why, in full.
 */
function PreviewVerdict({ preview, onShow }: { preview: DeliveryRecordPreview; onShow: (tab: PreviewTab) => void }) {
  const source = preview.source!;
  const decision = preview.decision!;
  const document = preview.document ?? null;
  const ledger = decision.ledger ?? null;
  const asked = preview.asked;
  const recordLink = ledger !== null && source.deliveryKey
    ? deliveryRecordRoute({ flowId: preview.flowId, deliveryKey: source.deliveryKey })
    : null;
  const assertions = document?.assertions ?? null;
  // A failure that holds is said under the verdict with the other holds; one that reports or leaves a value out is a notice.
  const quietFailures = assertions === null ? 0 : assertions.failed - assertions.held;
  const unverified = document?.unverified.length ?? 0;

  return (
    <Card className="gap-2 rounded-lg px-4 py-3" data-testid="preview-header">
      <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1.5">
        <PreviewActionBadge decision={decision} />
        <h2 className="min-w-0 truncate text-[15px] font-semibold" title={source.label ?? source.sourceKey} data-testid="preview-title">
          {source.label ?? source.sourceKey}
        </h2>
        <div className="ml-auto flex shrink-0 items-center gap-2">
          {recordLink !== null && (
            <Button variant="outline" size="sm" asChild data-testid="preview-open-record">
              <RouterLink to={recordLink}>
                <ExternalLink />
                Open record
              </RouterLink>
            </Button>
          )}
          {/* The preview's flow is the flow's label, which names the interface already (flow/interface). */}
          <Button
            variant="outline"
            size="sm"
            onClick={() => downloadJson(fileNameOf("preview", preview.flow, source.sourceKey), preview)}
            data-testid="preview-download"
          >
            <Download />
            Download JSON
          </Button>
        </div>
      </div>

      <span className="min-w-0 text-[13px]" data-testid="preview-reason">
        <TruncatedText text={decision.reason} maxWidth={4000} title="Next run" className="text-muted-foreground" />
      </span>

      <div className="flex min-w-0 flex-wrap items-center gap-x-4 gap-y-1.5 text-[12px] text-muted-foreground" data-testid="preview-facts">
        {document?.targetId && (
          <IdChip label="osdu" value={document.targetId} display={<RecordName id={document.targetId} className="max-w-[260px]" />} testId="preview-target" copyTestId="copy-preview-target" />
        )}
        <span className="inline-flex items-center gap-1.5" data-testid="preview-ledger">
          {ledger === null
            ? "not in the ledger yet"
            : (
              <>
                <RecordStatusBadge status={ledger.status} testId="preview-ledger-status" />
                {/* Its version in OSDU is on the record's page; here, when it was delivered and whether it is this document. */}
                {(ledger.lastDeliveredUtc || (ledger.sameDocument !== null && ledger.sameDocument !== undefined)) && (
                  <span>
                    {ledger.lastDeliveredUtc && <RelativeTime value={ledger.lastDeliveredUtc} />}
                    {ledger.lastDeliveredUtc && ledger.sameDocument !== null && ledger.sameDocument !== undefined && ", "}
                    {ledger.sameDocument === true && <span data-testid="preview-same">same as this render</span>}
                    {ledger.sameDocument === false && <span data-testid="preview-differs">differs from this render</span>}
                  </span>
                )}
              </>
            )}
        </span>
        {source.originFile && (
          <span className="inline-flex min-w-0 items-baseline gap-1">
            from
            <TruncatedText text={source.originFile} mono maxWidth={260} />
            {source.originRow !== null && source.originRow !== undefined && <span>{`row ${source.originRow}`}</span>}
          </span>
        )}
        <span data-testid="preview-asked">{pickedText(asked)}</span>
      </div>

      {(asked.passedOver > 0 || preview.issues.length > 0 || quietFailures > 0 || unverified > 0 || (document === null && preview.noDocument)) && (
        <div className="flex min-w-0 flex-wrap items-center gap-x-5 gap-y-1" data-testid="preview-notices">
          {document === null && preview.noDocument && (
            <Notice icon={FileWarning} tone="text-warning" title="The row renders no document" text={preview.noDocument} testId="preview-no-document">
              {`The row renders no document: ${preview.noDocument}`}
            </Notice>
          )}
          {preview.issues.length > 0 && (
            <Notice icon={TriangleAlert} tone="text-warning" title="The preflight's warnings" text={preview.issues.join("\n\n")} testId="preview-issues">
              {preview.issues.length === 1 ? preview.issues[0] : `${preview.issues[0]} (and ${preview.issues.length - 1} more)`}
            </Notice>
          )}
          {asked.passedOver > 0 && (
            <Notice
              icon={Info}
              tone="text-muted-foreground"
              title="Rows passed over"
              text={`The rows before this one that cannot render:\n${asked.passedOverWhy.map((why) => `- ${why}`).join("\n")}`}
              testId="preview-passed-over"
            >
              {`${asked.passedOver.toLocaleString()} ${asked.passedOver === 1 ? "row" : "rows"} before it passed over`}
            </Notice>
          )}
          {quietFailures > 0 && assertions !== null && (
            <button type="button" className="inline-flex items-center gap-1.5 text-[12px] hover:underline" onClick={() => onShow("render")} data-testid="preview-assertions-note">
              <TriangleAlert className="size-3.5 shrink-0 text-warning" aria-hidden />
              {`${quietFailures.toLocaleString()} of ${assertions.checked.toLocaleString()} ${assertions.checked === 1 ? "assertion" : "assertions"} failed without holding it`}
            </button>
          )}
          {unverified > 0 && (
            <button type="button" className="inline-flex items-center gap-1.5 text-[12px] hover:underline" onClick={() => onShow("render")} data-testid="preview-unverified-note">
              <TriangleAlert className="size-3.5 shrink-0 text-warning" aria-hidden />
              {`${unverified.toLocaleString()} ${unverified === 1 ? "reference" : "references"} the cache does not hold`}
            </button>
          )}
        </div>
      )}

      {document !== null && wouldHold(document) && (
        <div className="border-t pt-2">
          <FactGrid>
            <Fact label="Held for" wide testId="preview-holds">
              <HoldReasons document={document} testId="preview-hold-reasons" />
            </Fact>
          </FactGrid>
        </div>
      )}
    </Card>
  );
}

/**
 * The record's document, filling the window under the verdict and scrolling in place: as the route sends it, or as the
 * mapping renders it where the route adds to it, with what the platform fills in when it is sent on hover.
 */
function DocumentPane({ document, form, onForm }: { document: DeliveryPreviewDocument; form: DocumentForm; onForm: (form: DocumentForm) => void }) {
  const fitted = useRef<HTMLDivElement>(null);
  useScrollFit(fitted, DOCUMENT_FIT_BELOW, DOCUMENT_FIT_MIN, undefined, "height");
  const shown = form === "sent" && document.sent ? document.sent : document.rendered ?? null;

  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-[12px] text-muted-foreground">
        {document.sent
          ? (
            <ToggleGroup type="single" size="sm" variant="outline" value={form} onValueChange={(next) => { if (next === "sent" || next === "rendered") { onForm(next); } }} data-testid="preview-form">
              <ToggleGroupItem value="sent" data-testid="preview-form-sent">As the route sends it</ToggleGroupItem>
              <ToggleGroupItem value="rendered" data-testid="preview-form-rendered">As the mapping renders it</ToggleGroupItem>
            </ToggleGroup>
          )
          : <span>As the mapping renders it, which the route sends as it is</span>}
        {form === "sent" && document.sent && document.placeholders.length > 0 && (
          <Notice
            icon={Info}
            tone="text-muted-foreground"
            title="Values the platform gives when the record is sent"
            text={document.placeholders.map((placeholder) => `${placeholder.path}: ${placeholder.standsFor}`).join("\n")}
            testId="preview-placeholders"
          >
            {`${document.placeholders.length.toLocaleString()} ${document.placeholders.length === 1 ? "value" : "values"} given when it is sent`}
          </Notice>
        )}
        <span className="ml-auto font-mono" title={`The hash of the rendered document, which the ledger compares to decide what is sent: ${document.metadataHash}`}>
          {`${document.characters.toLocaleString()} characters`}
          {document.cacheValues > 0 ? `, ${document.cacheValues.toLocaleString()} values from the cache` : ""}
        </span>
      </div>
      {document.omitted
        ? <Alert data-testid="preview-document-omitted"><Info /><AlertDescription>{document.omitted}</AlertDescription></Alert>
        : shown !== null && (
          <div ref={fitted} className="flex min-h-0 flex-col">
            <CodeView value={JSON.stringify(envelopeFirst(shown), null, 2)} language="json" fill data-testid="preview-document" />
          </div>
        )}
    </div>
  );
}

/** How the document was made: the inputs it was rendered with, what the mapping's assertions found, and where values came from. */
function RenderPane({ preview }: { preview: DeliveryRecordPreview }) {
  const document = preview.document ?? null;
  const madeOf = document !== null && (document.searches.length > 0 || document.choices.length > 0 || document.unverified.length > 0 || (document.enrichment ?? []).length > 0);
  return (
    <div className="flex flex-col gap-4" data-testid="preview-render">
      <FactGrid data-testid="preview-inputs">
        <Fact label="Mapping"><span className="font-mono">{preview.inputs.mapping}</span></Fact>
        <Fact label="Kind"><TruncatedText text={preview.inputs.kind} mono maxWidth={420} /></Fact>
        <Fact label="Cache">
          {preview.inputs.cachePartition
            ? <span className="font-mono">{`${preview.inputs.cachePartition} ${preview.inputs.cacheVersion ?? ""}`.trim()}</span>
            : <NoFact>none read</NoFact>}
        </Fact>
        {document !== null && <Fact label="Hash"><TruncatedText text={document.metadataHash} mono maxWidth={260} copy copyTestId="copy-preview-hash" /></Fact>}
      </FactGrid>
      {document === null && <p className="text-[13px] text-muted-foreground">{preview.noDocument ?? "The row renders no document."}</p>}
      {document !== null && <PreviewAssertions document={document} testId="preview-assertions" />}
      {document !== null && document.searches.length > 0 && (
        <Section title="What the render found by searching the platform" testId="preview-searches-section">
          <PreviewSearches searches={document.searches} testId="preview-searches" />
        </Section>
      )}
      {document !== null && <PreviewChoices choices={document.choices} unverified={document.unverified} testId="preview-coalesce" />}
      {document !== null && <PreviewEnrichment document={document} testId="preview-enrichment" />}
      {document !== null && !madeOf && (
        <p className="text-[13px] text-muted-foreground" data-testid="preview-render-plain">
          The render searched the platform for nothing, took no $coalesce alternative and ran no enrichment: every value came from the row, the cache or the mapping itself.
        </p>
      )}
    </div>
  );
}

/** How a delivery would send the record: the route, its requests in order, the files it would upload, and what it refers to. */
function DeliveryPane({ preview }: { preview: DeliveryRecordPreview }) {
  return (
    <div className="flex flex-col gap-4" data-testid="preview-delivery">
      <FactGrid>
        <Fact label="Route" wide testId="preview-route">
          <span>
            <span className="font-mono">{preview.route.protocol}</span>
            {preview.route.ddms ? `: ${preview.route.ddms}` : ""}
          </span>
        </Fact>
      </FactGrid>

      <Section title="Requests" about="The requests a delivery of this record makes, in order, as the flow's route makes them. Nothing was sent." testId="preview-steps-section">
        {preview.steps.length === 0
          ? <p className="text-[13px] text-muted-foreground">The route makes no request for this record.</p>
          : <ol className="flex flex-col divide-y rounded-lg border px-3" data-testid="preview-steps">{preview.steps.map((step) => <Step key={step.order} step={step} />)}</ol>}
        {preview.notes.length > 0 && (
          <ul className="flex flex-col gap-1 pt-1 text-[12px] text-muted-foreground" data-testid="preview-notes">
            {preview.notes.map((note) => <li key={note}>{note}</li>)}
          </ul>
        )}
      </Section>

      <Section title="Files" testId="preview-payload-section">
        {preview.payload.length === 0
          ? <p className="text-[13px] text-muted-foreground" data-testid="preview-no-payload">A delivery of this record uploads no files: the route sends the document alone, or the row names none.</p>
          : <div className="flex flex-col gap-4">{preview.payload.map((part) => <PayloadPart key={`${part.role ?? ""}|${part.payload}`} part={part} />)}</div>}
      </Section>

      <Section
        title="Records it refers to"
        about={preview.referenceCount > preview.references.length
          ? `The document refers to ${preview.referenceCount} records; the first ${preview.references.length} are looked up in the ledger.`
          : "The records the document refers to, from the relationships of the template its mapping pins. A record of the ledger that is not delivered yet is one a delivery waits for."}
        testId="preview-references-section"
      >
        <DataTable
          columns={referenceColumns}
          rows={preview.references}
          rowKey={(row) => `${row.id}|${row.property}`}
          emptyMessage="The document refers to no other record."
          data-testid="preview-references"
        />
      </Section>
    </div>
  );
}

/** The record's rows as the ingestion tables hold them now, with its keys and how the preview picked it. */
function SourcePane({ preview }: { preview: DeliveryRecordPreview }) {
  const source = preview.source!;
  const asked = preview.asked;
  const values = Object.entries(asked.values ?? {});
  const datasets = Object.entries(source.datasets ?? {});
  const keyText = asked.keyColumns.length > 0 && asked.keyColumns.length === source.keyParts.length
    ? asked.keyColumns.map((column, index) => `${column}=${source.keyParts[index] ?? "null"}`).join(", ")
    : source.keyParts.map((part) => part ?? "null").join(", ");

  return (
    <div className="flex flex-col gap-4" data-testid="preview-source">
      <FactGrid>
        {/* The long values take the whole row, so a narrow page clips none of them; the short ones share the rows after. */}
        <Fact label="Source key" wide><TruncatedText text={source.sourceKey} mono maxWidth={4000} copy copyTestId="copy-preview-source-key" /></Fact>
        <Fact label="Key" wide><TruncatedText text={keyText} mono maxWidth={4000} /></Fact>
        <Fact label="Received from" wide>
          {source.originFile
            ? (
              <span className="inline-flex min-w-0 items-baseline gap-1">
                <TruncatedText text={source.originFile} mono maxWidth={4000} copy />
                {source.originRow !== null && source.originRow !== undefined && <span className="text-muted-foreground">{`row ${source.originRow}`}</span>}
              </span>
            )
            : <NoFact>not recorded</NoFact>}
        </Fact>
        {source.deliveryKey && <Fact label="Delivery key"><TruncatedText text={source.deliveryKey} mono maxWidth={320} copy copyTestId="copy-preview-delivery-key" /></Fact>}
        <Fact label="Picked as">
          {asked.key ? `'${asked.key}', ${pickedText(asked)}` : pickedText(asked)}
        </Fact>
        {values.length > 0 && <Fact label="Scope"><span className="font-mono">{values.map(([name, value]) => runValueAssignment(name, value)).join(", ")}</span></Fact>}
        {source.originUpdatedUtc && <Fact label="Row changed"><RelativeTime value={source.originUpdatedUtc} /></Fact>}
      </FactGrid>
      {source.omitted && <Alert><Info /><AlertDescription>{source.omitted}</AlertDescription></Alert>}
      <Section title="The record row" testId="preview-source-row-section">
        <CodeView value={JSON.stringify(source.row, null, 2)} language="json" height={240} data-testid="preview-source-row" />
      </Section>
      {datasets.map(([name, rows]) => (
        <Section
          key={name}
          title={`${name}: ${rows.truncated ? `the first ${rows.rows.length} of ${rows.total.toLocaleString()} rows` : `${rows.total.toLocaleString()} ${rows.total === 1 ? "row" : "rows"}`}`}
          testId="preview-source-dataset-section"
        >
          <CodeView value={JSON.stringify(rows.rows, null, 2)} language="json" height={200} data-testid="preview-source-dataset" />
        </Section>
      ))}
    </div>
  );
}

/**
 * A record preview as the Preview tab shows it: the verdict first (what the next run would do with which record, and why),
 * then the record in four views: the document a delivery would send, how it was rendered, how it would be sent, and the
 * rows it came from. A preview that found no row says why.
 */
export function RecordPreviewView({ preview }: { preview: DeliveryRecordPreview }) {
  const [tab, setTab] = useState<PreviewTab>("document");
  // Kept here rather than in the Document tab, which a switch to another tab unmounts, so the choice outlasts the switch.
  const [form, setForm] = useState<DocumentForm>("sent");
  const asked = preview.asked;

  if (!preview.found) {
    const values = Object.entries(asked.values ?? {});
    return (
      <Alert data-testid="preview-not-found">
        <SearchX />
        <AlertTitle>Nothing to preview</AlertTitle>
        <AlertDescription>
          <p data-testid="preview-reason">{preview.reason}</p>
          <p className="text-[12px] text-muted-foreground">
            {`Asked for ${asked.key ? `'${asked.key}', ${pickedText(asked)}` : pickedText(asked)}`}
            {values.length > 0 ? `, with ${values.map(([name, value]) => runValueAssignment(name, value)).join(", ")}` : ""}
            {asked.keyColumns.length > 0 ? `. The records are keyed by ${asked.keyColumns.join(", ")}.` : "."}
          </p>
          {asked.passedOverWhy.length > 0 && (
            <ul className="list-disc pl-5 text-[12px] text-muted-foreground">
              {asked.passedOverWhy.map((why) => <li key={why}>{why}</li>)}
            </ul>
          )}
        </AlertDescription>
      </Alert>
    );
  }

  const document = preview.document ?? null;
  // A record that renders no document has nothing for the Document tab to show; its rows say why.
  const shown: PreviewTab = tab === "document" && document === null ? "source" : tab;

  return (
    <div className="flex flex-col gap-3" data-testid="preview-result">
      <PreviewVerdict preview={preview} onShow={setTab} />
      <Tabs value={shown} onValueChange={(next) => { const picked = PREVIEW_TABS.find((value) => value === next); if (picked !== undefined) { setTab(picked); } }}>
        <TabsList data-testid="preview-tabs">
          <TabsTrigger value="document" disabled={document === null} data-testid="preview-tab-document">Document</TabsTrigger>
          <TabsTrigger value="render" data-testid="preview-tab-render">Render</TabsTrigger>
          <TabsTrigger value="delivery" data-testid="preview-tab-delivery">Delivery</TabsTrigger>
          <TabsTrigger value="source" data-testid="preview-tab-source">Source</TabsTrigger>
        </TabsList>
        <TabsContent value="document">
          {/* Mounted afresh for another document, so it is fitted to the window again whatever the last one showed. */}
          {document !== null && <DocumentPane key={document.metadataHash} document={document} form={form} onForm={setForm} />}
        </TabsContent>
        <TabsContent value="render"><RenderPane preview={preview} /></TabsContent>
        <TabsContent value="delivery"><DeliveryPane preview={preview} /></TabsContent>
        <TabsContent value="source"><SourcePane preview={preview} /></TabsContent>
      </Tabs>
    </div>
  );
}
