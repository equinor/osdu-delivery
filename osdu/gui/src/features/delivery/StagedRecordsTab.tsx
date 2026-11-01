import { useCallback, useMemo, type ReactNode } from "react";
import { keepPreviousData, useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { CircleAlert, FileWarning, MousePointerClick, Rows3 } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Card } from "@/components/ui/card";
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from "@/components/ui/resizable";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import type { RunValue } from "@/api/types";
import { DataTable, type Column } from "@/components/DataTable";
import { EmptyState } from "@/components/EmptyState";
import { ExplainMark } from "@/components/ExplainTip";
import { GlyphRef } from "@/components/GlyphRef";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { usePersistentLayout } from "@/layout/workbench/usePersistentLayout";
import { cn } from "@/lib/utils";
import {
  deliveryApi,
  type DeliveryDryRun,
  type DeliveryDryRunOutcome,
  type DeliveryDryRunRule,
  type DeliveryDryRunVerdicts,
  type DeliveryMappingFlow,
} from "../../api/delivery";
import {
  DRY_RUN_OUTCOMES,
  lineOfKey,
  lineOfResult,
  lineOfRow,
  nextRunLine,
  resultsOf,
  type StagedLine,
} from "./dryRun";
import { Fact, FactGrid } from "./Facts";
import { RecordPreviewView } from "./RecordPreviewView";
import { SchemaDifferences, SchemaMatchLine } from "./SchemaCheckView";
import { StagedRecordList } from "./StagedRecordList";
import { ProblemView, TaskProgress } from "./TemplateSheet";
import type { StagedNarrowing as Narrowing, StagedSection, StagedView } from "./useStagedView";
import { flowScope, type CheckMeta, type ValueCheckSession } from "./useValueCheck";
import { formatCount } from "./valueCheck";

/** The rows a page of the list reads. */
const PAGE_SIZE = 200;

/** A page of the section's tab: a tab takes the focus by being shown, so its underline is the mark of where the focus is. */
const SECTION_TAB = "flex-none px-0.5 text-[13px] focus-visible:border-transparent focus-visible:ring-0 focus-visible:outline-none";

/** How long a search waits for the typing to stop, so a word is asked of the table once. */
const SEARCH_DELAY_MS = 300;

/** How long stepping through the records waits on one before it renders, so a held arrow key renders the last one only. */
const STEP_DELAY_MS = 150;

const COUNTED: readonly DeliveryDryRunOutcome[] = ["ready", "attention", "held", "passedOver"];

/** One rule of the run's findings, with its example a click away. */
function ruleColumns(onExample: (key: string) => void): Column<DeliveryDryRunRule>[] {
  return [
    { id: "at", header: "Property", fill: true, render: (row) => <TruncatedText text={row.at === "" ? "the record" : row.at} mono maxWidth={420} /> },
    { id: "rule", header: "Rule", render: (row) => <span className="font-mono text-[12px]">{row.rule}</span> },
    { id: "records", header: "Records", align: "right", render: (row) => <span className="font-mono tabular-nums">{formatCount(row.records)}</span> },
    {
      id: "example",
      header: "For example",
      render: (row) => (
        <button
          type="button"
          className="block max-w-36 truncate text-left font-mono text-[11.5px] text-primary hover:underline"
          title={`${row.exampleKey}: ${row.exampleMessage}`}
          onClick={() => onExample(row.exampleKey)}
          data-testid="dry-run-rule-example"
        >
          {row.exampleKey}
        </button>
      ),
    },
  ];
}

/** What the run found as a whole: what was checked, the rules broken most often, and where the template and the Schema service part. */
function DryRunFindings({ run, meta, onExample }: { run: DeliveryDryRun; meta: CheckMeta; onExample: (key: string) => void }) {
  const columns = useMemo(() => ruleColumns(onExample), [onExample]);
  const quiet = run.rules.length === 0 && run.schemaMatch.differences.length === 0;
  return (
    <div className="flex min-w-0 flex-col gap-4" data-testid="dry-run-findings">
      <DryRunChecks run={run} meta={meta} />
      {run.rules.length > 0 && (
        <section className="flex min-w-0 flex-col gap-1">
          <h3 className="text-[13px] font-medium">Rules broken most often</h3>
          <DataTable columns={columns} rows={run.rules} rowKey={(row) => `${row.at}|${row.rule}`} emptyMessage="No rule is broken." data-testid="dry-run-rules" />
          {run.rulesUnlisted > 0 && <p className="text-[12px] text-muted-foreground">{`${formatCount(run.rulesUnlisted)} more records break rules not listed here.`}</p>}
        </section>
      )}
      <SchemaDifferences match={run.schemaMatch} testId="dry-run-differences" />
      {run.addedSchemas.length > 0 && (
        <section className="flex min-w-0 flex-col gap-1" data-testid="dry-run-added-schemas">
          <h3 className="text-[13px] font-medium">Kinds of the records its enrichments add</h3>
          {run.addedSchemas.map((match) => (
            <div key={match.kind} className="flex min-w-0 items-center gap-3">
              <TruncatedText text={match.kind} mono maxWidth={380} className="text-[12px]" />
              <SchemaMatchLine match={match} testId="dry-run-added-match" />
            </div>
          ))}
        </section>
      )}
      {meta.issues.length > 0 && <GlyphRef icon={CircleAlert} title="The mapping's preflight warnings" label={`${meta.issues.length} warnings`} body={meta.issues.join("\n")} />}
      {run.notes.length > 0 && (
        <ul className="flex flex-col gap-1 text-[12px] text-muted-foreground" data-testid="dry-run-notes">
          {run.notes.map((note) => <li key={note}>{note}</li>)}
        </ul>
      )}
      {quiet && <p className="text-[12px] text-muted-foreground" data-testid="dry-run-quiet">No rule is broken. Pick a record to see its document; Up and Down step through them.</p>}
    </div>
  );
}

/** One record's document and everything known of it, rendered when it is picked; going through the list renders the one stopped on. */
function RecordPane({ line, flow, values }: { line: StagedLine; flow: DeliveryMappingFlow; values: Record<string, RunValue> }) {
  const key = useDebouncedValue(line.keyJson, STEP_DELAY_MS);
  const scope = flowScope(flow);
  const preview = useQuery({
    queryKey: ["delivery", "staged-record", flow.pipelineId, flow.interface, flow.partition, key, values],
    queryFn: () => deliveryApi.preview(flow.pipelineId, { key, values, schemaCheck: true }, scope),
    enabled: key !== null,
    placeholderData: keepPreviousData,
    staleTime: 30000,
    retry: false,
  });
  const stale = key !== line.keyJson || preview.isPlaceholderData;

  if (line.keyJson === null) {
    return (
      <div className="p-3" data-testid="staged-record">
        <Alert data-testid="staged-record-keyless">
          <FileWarning />
          <AlertTitle>{line.sourceKey}</AlertTitle>
          <AlertDescription>
            A part of this row's key is empty, so it has no record to name and a delivery holds it. Fill the key in the source, or filter the row out before it is staged.
          </AlertDescription>
        </Alert>
      </div>
    );
  }

  return (
    <div className="flex min-h-0 min-w-0 flex-1 flex-col" data-testid="staged-record">
      {preview.isError && <div className="p-3"><ProblemView error={preview.error} testId="staged-record-error" /></div>}
      {preview.isPending && <div className="p-3"><TaskProgress label="Rendering the record" testId="staged-record-progress" /></div>}
      {preview.data !== undefined && (
        <div className={cn("flex min-h-0 flex-1 flex-col transition-opacity", stale && "opacity-60")}>
          <RecordPreviewView preview={preview.data} fill />
        </div>
      )}
    </div>
  );
}

/** The chips narrowing the list to one outcome, each with its count; an outcome no row came to takes no room. */
function OutcomeChips({ run, narrowing, narrow }: { run: DeliveryDryRun; narrowing: Narrowing; narrow: (next: Narrowing) => void }) {
  return (
    <div className="flex flex-wrap items-center gap-1" data-testid="dry-run-outcomes">
      {COUNTED.map((outcome) => {
        const visual = DRY_RUN_OUTCOMES[outcome];
        const Icon = visual.icon;
        const count = run.outcomes[outcome];
        const active = narrowing === outcome;
        if (count === 0 && !active) {
          return null;
        }

        return (
          <button
            key={outcome}
            type="button"
            aria-pressed={active}
            title={visual.meaning}
            onClick={() => narrow(active ? null : outcome)}
            className={cn(
              "inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[11.5px] outline-none transition-colors hover:bg-accent/60 focus-visible:ring-2 focus-visible:ring-ring/50",
              active && "border-primary/50 bg-accent",
            )}
            data-testid={`dry-run-outcome-${outcome}`}
          >
            <Icon className={cn("size-3.5", visual.tone)} aria-hidden />
            {visual.label}
            <span className="font-mono tabular-nums">{formatCount(count)}</span>
          </button>
        );
      })}
    </div>
  );
}

/** How many documents break a schema's rules, or that none did. */
function breaksText(verdicts: DeliveryDryRunVerdicts): string {
  return verdicts.invalid > 0
    ? `${formatCount(verdicts.invalid)} ${verdicts.invalid === 1 ? "document breaks" : "documents break"} a rule`
    : "every document meets it";
}

const LEGAL_WORDS: Record<DeliveryDryRun["legalTags"]["state"], string> = {
  accepted: "accepted",
  refused: "refused",
  notAsked: "not asked",
  notChecked: "could not be asked",
  notApplicable: "none on these rows",
};

/** What was checked of every record alike, one fact to a line; a check that could not be made says so in its place. */
function DryRunChecks({ run, meta }: { run: DeliveryDryRun; meta: CheckMeta }) {
  const { legalTags, schemaMatch, template, schema } = run;
  const judged = schemaMatch.state === "matches" || schemaMatch.state === "differs";
  return (
    <section className="flex min-w-0 flex-col gap-1.5" data-testid="dry-run-checks">
      <h3 className="flex flex-wrap items-baseline gap-x-2 text-[13px] font-medium">
        What was checked
        <span className="text-[12px] font-normal text-muted-foreground"><RelativeTime value={meta.checkedUtc} /></span>
      </h3>
      <FactGrid>
        <Fact label="Next run" wide testId="dry-run-next-run">{nextRunLine(run)}</Fact>
        <Fact label="Template" wide testId="dry-run-template">
          <span className={cn(template.invalid > 0 && "text-warning")}>{breaksText(template)}</span>
        </Fact>
        <Fact label="Schema service" wide testId="dry-run-schema-fact">
          <span className="inline-flex min-w-0 flex-wrap items-center gap-x-2">
            <SchemaMatchLine match={schemaMatch} testId="dry-run-schema" />
            {judged && <span className={cn(schema.invalid > 0 && "text-warning")} data-testid="dry-run-schema-invalid">{breaksText(schema)}</span>}
            {!judged && schemaMatch.why && <span className="text-[12px] text-muted-foreground" data-testid="dry-run-schema-why">{schemaMatch.why}</span>}
          </span>
        </Fact>
        <Fact label="Legal tags" wide testId="dry-run-legal">
          <span
            className={cn(legalTags.state === "refused" && "text-destructive", legalTags.state === "notChecked" && "text-warning")}
            title={legalTags.why ?? legalTags.tags.join(", ")}
          >
            {legalTags.why && legalTags.state !== "notAsked" ? `${LEGAL_WORDS[legalTags.state]}: ${legalTags.why}` : LEGAL_WORDS[legalTags.state]}
          </span>
        </Fact>
        {run.withMissingReferences > 0 && (
          <Fact label="References" wide testId="dry-run-references">
            <span className="inline-flex min-w-0 items-center gap-1 text-warning">
              {`${formatCount(run.withMissingReferences)} ${run.withMissingReferences === 1 ? "row refers" : "rows refer"} to records not found`}
              <ExplainMark
                title="References not found"
                text="Neither the ledger, the cache nor OSDU's storage service holds them yet. A record another flow delivers first shows here until it does."
                testId="dry-run-references-about"
              />
            </span>
          </Fact>
        )}
        {run.occupied > 0 && (
          <Fact label="Ids taken" wide testId="dry-run-occupied">
            <span className="inline-flex min-w-0 items-center gap-1 text-destructive">
              {`${formatCount(run.occupied)} ${run.occupied === 1 ? "record's id is" : "records' ids are"} already in OSDU`}
              <ExplainMark
                title="Ids already taken"
                text="OSDU already holds a record at the id made from the key, and no ledger claims it, so a delivery holds these rather than write over it. Adopt them if they are this flow's."
                testId="dry-run-occupied-about"
              />
            </span>
          </Fact>
        )}
      </FactGrid>
    </section>
  );
}

/** The page of the section a reader lands on with no record to show: why, and what to do about it. */
function NoRecord({ session, answered, search, table }: { session: ValueCheckSession; answered: boolean; search: string; table: string | undefined }) {
  if (session.flow === null) {
    return (
      <EmptyState
        icon={<Rows3 />}
        title={session.otherPartitions.length > 0 ? "These records are in another partition" : "Nothing is staged for this mapping"}
        description={session.blocked ?? "No flow renders with this mapping yet. A delivery flow that pins it brings the rows of its record table here."}
        data-testid="staged-no-flow"
      />
    );
  }

  if (!session.ready && session.blocked !== null) {
    return (
      <EmptyState
        icon={<Rows3 />}
        title="The rows cannot be read yet"
        description="The line above says why. Source YAML and Record shape read the mapping alone, and are there in the meantime."
        data-testid="staged-scope-needed"
      />
    );
  }

  if (answered && search !== "") {
    return <EmptyState icon={<MousePointerClick />} title="No row to show" description={`No row holds '${search}'. The search reads the key, the label and the file.`} data-testid="staged-no-match" />;
  }

  if (answered) {
    return (
      <EmptyState
        icon={<Rows3 />}
        title="Nothing is staged here yet"
        description={`${table ?? "The record table"} holds no row in this scope: its ingestion flow has not loaded any, or the scope's values name none.`}
        data-testid="staged-nothing"
      />
    );
  }

  return <TaskProgress label="Reading the rows" testId="staged-rows-progress" />;
}

/**
 * The records staged for a mapping and what they come to, side by side in one frame, with a handle between them to give either
 * more room. On the left, the rows of the record table of a flow that renders with the mapping, listed as they stand: the list
 * reads the table a page at a time and asks it for a search, so it is the same whatever the table holds, and the rows a dry run
 * found something about are found by their outcome, wherever they are in the table. On the right, one section with one page at
 * a time: the record picked (the first row, until one is), the mapping as written, the shape it gives a record, and what a dry
 * run found. What is listed and picked stays when the reader moves to Properties and back.
 */
export function StagedRecordsTab({ session, view, yaml, shape }: {
  session: ValueCheckSession;
  view: StagedView;
  /** The mapping as written, for the Source YAML page. */
  yaml: ReactNode;
  /** The shape it gives a record, for the Record shape page. */
  shape: ReactNode;
}) {
  const { flow, ready } = session;
  const layout = usePersistentLayout("sqlflow.delivery.mapping.records.layout");
  const search = useDebouncedValue(view.typed.trim(), SEARCH_DELAY_MS);
  const values = session.sentValues;
  const rows = useInfiniteQuery({
    queryKey: ["delivery", "staged-rows", flow?.pipelineId ?? null, flow?.interface ?? null, flow?.partition ?? null, values, search],
    queryFn: ({ pageParam }) => deliveryApi.stagedRows(
      flow!.pipelineId,
      { values, search: search === "" ? undefined : search, after: pageParam, limit: PAGE_SIZE },
      flowScope(flow!)),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.next ?? undefined,
    enabled: flow !== null && ready,
    staleTime: 30000,
    retry: false,
  });

  const dryRun = session.dryRun;
  const run = dryRun !== null && !session.dryRunStale ? dryRun.run : null;
  const results = useMemo(() => resultsOf(run), [run]);
  const pages = rows.data?.pages;
  const first = pages?.[0];
  const { hasNextPage, isFetchingNextPage, fetchNextPage } = rows;
  const more = useCallback(() => { void fetchNextPage(); }, [fetchNextPage]);

  // Narrowed to an outcome, the list is what the dry run named, wherever those rows are in the table.
  const narrowing = run === null ? null : view.narrowing;
  const lines = useMemo<StagedLine[]>(() => {
    if (narrowing !== null && run !== null) {
      const text = view.typed.trim().toLowerCase();
      return run.records
        .filter((record) => record.outcome === narrowing)
        .map(lineOfResult)
        .filter((line) => text === "" || [line.sourceKey, line.label, line.file, line.result?.reason].some((value) => value?.toLowerCase().includes(text)));
    }

    return (pages ?? []).flatMap((page) => page.rows).map((row) => lineOfRow(row, results));
  }, [narrowing, run, view.typed, pages, results]);

  const total = narrowing !== null && run !== null ? run.outcomes[narrowing] : first?.total ?? null;
  const pick = view.pick;
  const openExample = useCallback((key: string) => {
    // A rule's example need not be among the rows listed: its key names it to a preview all the same.
    const record = run?.records.find((candidate) => candidate.sourceKey === key);
    pick(record === undefined ? lineOfKey(key) : lineOfResult(record));
  }, [run, pick]);

  // The page opens on a document: until a record is picked, the first the list holds that has one to show.
  const shown = view.picked ?? lines.find((line) => line.keyJson !== null && line.deletedUtc === null) ?? lines[0] ?? null;

  // With no flow there is nothing to render, and the mapping as written is what is left to read.
  const fallback: StagedSection = flow === null && !session.flowsLoading ? "yaml" : "render";
  const asked = view.section ?? fallback;
  const section = asked === "findings" && run === null ? fallback : asked;
  const tables = Object.keys(first?.datasets ?? {});
  const answered = first !== undefined && !rows.isFetching;

  return (
    <Card className="min-h-0 flex-1 gap-0 overflow-hidden rounded-lg p-0" data-testid="staged-records">
      <ResizablePanelGroup orientation="horizontal" className="min-h-0 flex-1" defaultLayout={layout.defaultLayout} onLayoutChanged={layout.onLayoutChanged}>
        <ResizablePanel id="staged-list" defaultSize={300} minSize={220} maxSize="60" className="flex min-h-0 flex-col">
          <StagedRecordList
            lines={lines}
            picked={shown?.sourceKey ?? null}
            onPick={view.pick}
            search={view.typed}
            onSearch={view.type}
            total={total}
            loading={rows.isFetching}
            hasMore={narrowing === null && hasNextPage}
            onMore={isFetchingNextPage ? () => undefined : more}
            empty={emptyText(session, search, narrowing !== null, first?.recordObject, rows.isError)}
            table={first === undefined ? null : { name: first.recordObject, children: tables }}
            head={(
              <>
                {run !== null && <OutcomeChips run={run} narrowing={narrowing} narrow={view.narrow} />}
                {first?.mappingUnread && (
                  <p className="flex items-start gap-1.5 text-[11.5px] text-warning" data-testid="staged-mapping-unread">
                    <CircleAlert className="mt-0.5 size-3.5 shrink-0" />
                    {`The rows are named by their key alone, as the mapping could not be read: ${first.mappingUnread}`}
                  </p>
                )}
              </>
            )}
          />
        </ResizablePanel>
        <ResizableHandle withHandle />
        <ResizablePanel id="staged-section" minSize="35" className="flex min-h-0 min-w-0 flex-col">
          <Tabs value={section} onValueChange={(next) => view.show(next as StagedSection)} className="min-h-0 flex-1 gap-0" data-testid="staged-pane">
            <div className="flex h-9 shrink-0 items-center border-b px-3">
              <TabsList variant="line" className="h-full gap-4 p-0" data-testid="delivery-mapping-sections">
                <TabsTrigger value="render" className={SECTION_TAB} data-testid="delivery-mapping-tab-render">Render</TabsTrigger>
                <TabsTrigger value="yaml" className={SECTION_TAB} data-testid="delivery-mapping-tab-yaml">Source YAML</TabsTrigger>
                <TabsTrigger value="shape" className={SECTION_TAB} data-testid="delivery-mapping-tab-shape">Record shape</TabsTrigger>
                {run !== null && <TabsTrigger value="findings" className={SECTION_TAB} data-testid="delivery-mapping-tab-findings">Dry run</TabsTrigger>}
              </TabsList>
            </div>
            <TabsContent value="render" className="flex min-h-0 flex-col">
              {session.flowsLoading && <div className="p-3"><TaskProgress label="Finding the flows that render with this mapping" testId="staged-flows-loading" /></div>}
              {rows.isError && <div className="p-3"><ProblemView error={rows.error} testId="staged-rows-error" /></div>}
              {!session.flowsLoading && !rows.isError && (shown !== null && flow !== null
                ? <RecordPane line={shown} flow={flow} values={values} />
                : <div className="p-3"><NoRecord session={session} answered={answered} search={search} table={first?.recordObject} /></div>)}
            </TabsContent>
            <TabsContent value="yaml" className="flex min-h-0 flex-col overflow-auto p-2">{yaml}</TabsContent>
            <TabsContent value="shape" className="flex min-h-0 flex-col overflow-auto p-2">{shape}</TabsContent>
            {run !== null && dryRun !== null && (
              <TabsContent value="findings" className="min-h-0 overflow-y-auto p-3">
                <DryRunFindings run={run} meta={dryRun.meta} onExample={openExample} />
              </TabsContent>
            )}
          </Tabs>
        </ResizablePanel>
      </ResizablePanelGroup>
    </Card>
  );
}

/** What an empty list says: why there is nothing to read yet, or that the table holds nothing for what was asked. */
function emptyText(session: ValueCheckSession, search: string, narrowed: boolean, table: string | undefined, failed: boolean): string {
  if (failed) {
    return "The rows could not be read.";
  }

  if (!session.ready) {
    return session.blocked === null ? "Reading the rows." : "No rows to list.";
  }

  if (narrowed) {
    return "No row the dry run listed matches.";
  }

  return search !== "" ? `No row holds '${search}'.` : `No row in ${table ?? "the record table"}.`;
}
