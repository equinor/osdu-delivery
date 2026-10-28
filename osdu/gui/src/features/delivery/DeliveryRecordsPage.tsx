import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { CloudUpload, History, TextSearch } from "lucide-react";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Badge } from "@/components/ui/badge";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import {
  DELIVERY_RECORD_STATUSES, deliveryApi, deliveryRecordRoute, type DeliveryRecordHit, type DeliveryRecordStatus,
} from "../../api/delivery";
import type { Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { FilterCombobox, type FilterOption } from "@/components/FilterCombobox";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { PagedTable } from "@/components/PagedTable";
import { SearchInput } from "@/components/SearchInput";
import { downloadFileName } from "@/lib/download";
import { useActivePartition } from "./activePartition";
import { useLinkedPartition } from "./useLinkedPartition";
import { RecordStatusBadge } from "./DeliveryBadges";
import { ExportRecordsButton } from "./ExportRecordsButton";
import { ledgerFlowOptions } from "./ledgerFlowOptions";
import { CompactTime, OsduTarget, RecordIdentity } from "./RecordCells";

const ALL = "all";

/** What each kind of matched value is, in the operator's terms; the badge's tooltip says it. */
const MATCH_KINDS: Record<string, string> = {
  identity: "an identity the mapping declares (a wellbore id, a well name)",
  key: "the record's key, or one of its columns",
  label: "a word of the record's label",
  osdu: "the OSDU id, or its own part",
  file: "the ingestion file the record came from",
};

/** How long the field waits after the last keystroke before the term becomes the lookup: a lookup per keystroke is noise. */
const DEBOUNCE_MS = 300;

function isRecordStatus(value: string | null): value is DeliveryRecordStatus {
  return value !== null && (DELIVERY_RECORD_STATUSES as readonly string[]).includes(value);
}

const statusColumn: Column<DeliveryRecordHit> = {
  id: "status", header: "Status", render: (row) => <RecordStatusBadge status={row.status as DeliveryRecordStatus} />,
};

/**
 * Why a row is a hit, under the record it explains: the values the term matched, each captioned by what it is on hover.
 * Only a search has them, so the recency listing shows none.
 */
function MatchedValues({ matched }: { matched: DeliveryRecordHit["matched"] }) {
  if (!matched || matched.length === 0) {
    return null;
  }

  return (
    <span className="inline-flex flex-wrap items-center gap-1" data-testid="lookup-matched">
      <TextSearch className="size-3 shrink-0 text-muted-foreground" aria-label="Matched" />
      {matched.map((match) => (
        <Badge key={`${match.kind}:${match.value}`} variant="outline" className="max-w-full truncate font-mono text-[10px]" title={MATCH_KINDS[match.kind] ?? match.kind}>
          {match.value}
        </Badge>
      ))}
    </span>
  );
}

/**
 * The record as the operator knows it, whole, with the ingestion file and row it came from: the one column that takes
 * the width the others leave.
 */
const recordColumn = (searching: boolean): Column<DeliveryRecordHit> => ({
  id: "record",
  header: "Record",
  fill: true,
  floor: 200,
  render: (row) => (
    <div className="flex min-w-0 flex-col gap-0.5 whitespace-normal">
      <RecordIdentity label={row.label} sourceKey={row.sourceKey} origin={{ fileName: row.sourceFileName ?? null, rowNumber: row.sourceRowNumber ?? null }} />
      {searching && <MatchedValues matched={row.matched} />}
    </div>
  ),
});

/**
 * The flow that delivers the record, named in full: which pipeline delivered a record is what an operator reads a row
 * by, next to the record itself. A source's interfaces share the flow's name, so an interface is named under it, and so
 * is the partition of a flow that works in partitions, which keeps a ledger per partition.
 */
const flowColumn: Column<DeliveryRecordHit> = {
  id: "flow",
  header: "Flow",
  render: (row) => (
    row.flowName === null
      ? <span className="text-[12px] text-muted-foreground" title={row.flowId}>no longer synced</span>
      : (
        <div className="flex flex-col">
          <span className="font-mono text-[12px]">{row.flowName}</span>
          {(row.interface || row.partition) && (
            <span className="flex flex-wrap gap-1">
              {row.interface && <Badge variant="outline" className="w-fit font-mono text-[10px]">{row.interface}</Badge>}
              {row.partition && (
                <Badge variant="secondary" className="w-fit font-mono text-[10px]" title="The OSDU partition this record's ledger delivers to">
                  {row.partition}
                </Badge>
              )}
            </span>
          )}
        </div>
      )
  ),
};

/** Where the record stands in OSDU, last: the row's way to what OSDU holds. */
const osduColumn: Column<DeliveryRecordHit> = {
  id: "osdu", header: "OSDU", render: (row) => <OsduTarget record={row} copyTestId="copy-hit-target" />,
};

/** The instant the listing is ordered by, and under it the one that matters most: when the record last landed in OSDU. */
const whenColumn: Column<DeliveryRecordHit> = {
  id: "when",
  header: "When",
  render: (row) => (
    <div className="flex flex-col text-[12px]">
      <CompactTime value={row.updatedUtc} caption="Updated" icon={History} />
      <CompactTime value={row.lastDeliveredUtc} caption="Delivered" icon={CloudUpload} absent="not yet" className="text-muted-foreground" />
    </div>
  ),
};

/**
 * The columns of a search, and of the recency listing. The record and its flow are shown whole, and a search's matched
 * values ride under the record; the rest is a few characters each, so the grid fits its panel instead of scrolling.
 */
const columnsFor = (searching: boolean): Column<DeliveryRecordHit>[] => [
  statusColumn,
  recordColumn(searching),
  flowColumn,
  whenColumn,
  osduColumn,
];

/** How often the recency listing refreshes itself: a page left open is a view of what is arriving now. */
const RECENT_POLL_MS = 10_000;

/**
 * Where an operator starts from what they hold, not from a flow: a well name, an OSDU id, a delivery key or the file a
 * record came from finds the record across every flow, and its row opens the record's page with its whole history.
 * With nothing typed it opens on the records the ledger last took in or sent, so the page answers "what has come in?"
 * before it is asked anything. It narrows as a flow's Records tab does (a state, drift, a held step, a term matched
 * anywhere), and by flow. The term and the filters travel in the URL, so a lookup is a link that can be sent on; a link
 * that names a partition opens it in that one.
 */
export default function DeliveryRecordsPage() {
  const navigate = useNavigate();
  useLinkedPartition();
  const [searchParams, setSearchParams] = useSearchParams();
  const term = searchParams.get("q")?.trim() ?? "";
  const status = isRecordStatus(searchParams.get("status")) ? searchParams.get("status")! : ALL;
  // The flow tab's switches, named in the address as the lookup's API names them.
  const anywhere = searchParams.get("mode") === "contains";
  const drifted = searchParams.get("drifted") === "true";
  const stepHeld = searchParams.get("stepHeld") === "true";
  const [typed, setTyped] = useState(term);

  // The address as last asked. It is drawn a render after each change, so every change builds on this rather than on the
  // address last drawn, and filters changed closer together than a render all stick.
  const asked = useRef(searchParams);
  useEffect(() => {
    asked.current = searchParams;
  }, [searchParams]);
  const setParam = useCallback((name: string, next: string | null) => {
    const params = new URLSearchParams(asked.current);
    if (next === null) {
      params.delete(name);
    } else {
      params.set(name, next);
    }

    asked.current = params;
    setSearchParams(params, { replace: true });
  }, [setSearchParams]);

  // The field is what the operator types; the URL is what is looked up, a moment after they stop.
  useEffect(() => {
    if (typed.trim() === term) {
      return;
    }

    const handle = window.setTimeout(() => setParam("q", typed.trim() === "" ? null : typed.trim()), DEBOUNCE_MS);
    return () => window.clearTimeout(handle);
  }, [typed, term, setParam]);

  // A link that arrives with a term (from the Delivery page's field, or sent on) fills the field it would have been typed in.
  const [shownTerm, setShownTerm] = useState(term);
  if (term !== shownTerm) {
    setShownTerm(term);
    if (typed.trim() !== term) {
      setTyped(term);
    }
  }

  const searching = term !== "";
  const columns = useMemo(() => columnsFor(searching), [searching]);

  // The flow is a ledger identity holding records, named as `ledgerFlowOptions` names a ledger. Every read here is the
  // workbench partition's: the flows to narrow to are the ledgers it keeps, and the records are its. With no partition
  // picked (a catalog that knows none), every partition's, each flow naming its own.
  const [active] = useActivePartition();
  const flow = searchParams.get("flow") ?? "";
  const flows = useQuery({ queryKey: ["delivery", "record-flows", active], queryFn: () => deliveryApi.recordFlows(active) });
  // A link can name a flow the list leaves out, because it holds no records or no synced pipeline names it any more.
  const flowOptions = useMemo<FilterOption[]>(
    () => ledgerFlowOptions(flows.data, active, flow, flows.isSuccess, `${flow}: no records${active === null ? "" : ` in ${active}`}, or no longer synced`),
    [flows.data, flows.isSuccess, flow, active],
  );

  const selectStatus = (next: string) => setParam("status", next === ALL ? null : next);
  const selectFlow = (next: string) => setParam("flow", next === "" ? null : next);

  // The lookup the table pages through and the export writes whole: the same one, so the file is what the page lists.
  const lookup = useMemo(() => ({
    search: searching ? term : undefined,
    mode: searching && anywhere ? "contains" as const : undefined,
    status: status === ALL ? undefined : (status as DeliveryRecordStatus),
    drifted: drifted || undefined,
    stepHeld: stepHeld || undefined,
    flowId: flow === "" ? undefined : flow,
    partition: active ?? undefined,
  }), [searching, term, anywhere, status, drifted, stepHeld, flow, active]);
  const narrowed = status !== ALL || flow !== "" || drifted || stepHeld;
  // How many records the lookup reached, as its last page counted them; capped when more match than it reaches.
  const [reached, setReached] = useState<{ total: number; capped: boolean } | null>(null);
  const onPageLoaded = useCallback((_rows: DeliveryRecordHit[], total: number, capped: boolean) => {
    setReached((known) => (known?.total === total && known.capped === capped ? known : { total, capped }));
  }, []);
  const flowLabel = flow === "" ? null : flowOptions.find((option) => option.value === flow)?.label ?? flow;

  return (
    <Page data-testid="page-delivery-records">
      <PageHeader
        title="Records"
        subtitle={`The records of every flow${active === null ? "" : ` in ${active}`}, newest first, and a way back to any one of them: a wellbore id or well name the mapping declares, a source key or label, an OSDU id, a delivery key, or the ingestion file it came from. A term matches the start of a value, or anywhere in one with Match anywhere. Every flow is searched unless one is chosen, and each hit says which value matched.`}
        // What the lookup lists, every record it reaches rather than the page in view: a page-wide action, so it stays in
        // the header at any width rather than wrapping the filters.
        actions={(
          <ExportRecordsButton
            fetchWorkbook={() => deliveryApi.exportLookup(lookup)}
            fileName={downloadFileName(["records", active, flowLabel, status === ALL ? null : status], "xlsx", "records")}
            unavailable={reached === null
              ? "The lookup has not answered yet."
              : reached.total === 0 ? "No records match, so there is nothing to export." : null}
            title={reached?.capped
              ? `Download the first ${reached.total.toLocaleString()} records the lookup reaches as an Excel workbook, with the flow of each; more match, and the workbook says so. A flow's Records tab exports every record its filter matches.`
              : `Download the ${(reached?.total ?? 0).toLocaleString()} record${reached?.total === 1 ? "" : "s"} the lookup finds as an Excel workbook: a row each with its flow, status, issue, problem, source file and row, and OSDU id`}
            testId="delivery-lookup-export"
          />
        )}
      />
      <FilterBar>
        <SearchInput
          value={typed}
          onChange={setTyped}
          placeholder="Wellbore id, well name, source key, OSDU id, delivery key or file"
          label="Look up records"
          testId="delivery-lookup-search"
          className="sm:w-[28rem]"
        />
        <Select value={status} onValueChange={selectStatus}>
          <SelectTrigger size="sm" className="h-8 w-40" data-testid="delivery-lookup-status">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>All statuses</SelectItem>
            {DELIVERY_RECORD_STATUSES.map((value) => <SelectItem key={value} value={value}>{value}</SelectItem>)}
          </SelectContent>
        </Select>
        <FilterCombobox
          options={flowOptions}
          value={flow}
          onChange={selectFlow}
          placeholder="All flows"
          searchPlaceholder="Search flows"
          emptyText="No flow matches."
          ariaLabel="Filter by flow"
          testId="delivery-lookup-flow"
          className="w-64"
        />
        <Label className="flex items-center gap-2 text-[13px] font-normal" title="Records whose last verify found OSDU holding something else, or nothing">
          <Switch checked={drifted} onCheckedChange={(on) => setParam("drifted", on ? "true" : null)} data-testid="delivery-lookup-drifted" />
          Drifted only
        </Label>
        <Label className="flex items-center gap-2 text-[13px] font-normal" title="Records an enrichment held in and was set aside on: each went ahead with what the enrichment added to its last delivery">
          <Switch checked={stepHeld} onCheckedChange={(on) => setParam("stepHeld", on ? "true" : null)} data-testid="delivery-lookup-step-held" />
          Step held
        </Label>
        <Label
          className="flex items-center gap-2 text-[13px] font-normal"
          title="Match the term anywhere in the source key, the label, the OSDU id or the ingestion file, not only at the start of a value. It reads every record the other filters leave, so with more than 100,000 of them it asks for a flow or a status first."
        >
          <Switch checked={anywhere} onCheckedChange={(on) => setParam("mode", on ? "contains" : null)} data-testid="delivery-lookup-contains" />
          Match anywhere
        </Label>
      </FilterBar>
      {!searching && (
        <p className="flex items-center gap-1.5 text-[12px] text-muted-foreground" data-testid="delivery-lookup-caption">
          <History className="size-3.5 shrink-0 text-primary" aria-hidden="true" />
          <span>
            The latest records the delivery system took in or sent, newest first. Type above to find one by what you
            hold; open a row for its journey through pre-ingestion, ingestion and delivery.
          </span>
        </p>
      )}
      <PagedTable
        queryKey={["delivery", "lookup", term, anywhere, status, drifted, stepHeld, flow, active]}
        fetchPage={(page, pageSize) => deliveryApi.lookupRecords({ ...lookup, page, pageSize })}
        onPageLoaded={onPageLoaded}
        columns={columns}
        rowKey={(row) => `${row.flowId}/${row.deliveryKey}`}
        onRowClick={(row) => navigate(deliveryRecordRoute(row))}
        pollMs={searching ? undefined : RECENT_POLL_MS}
        emptyMessage={searching
          ? anywhere
            ? `No record${narrowed ? " the filters leave" : ""} holds that in its source key, label, OSDU id or ingestion file.`
            : `No record${narrowed ? " the filters leave" : ""} starts with that. A term matches the start of a value the record is known by; try a shorter one, or turn on Match anywhere.`
          : narrowed
            ? "No records match the filters."
            : "No records yet. A delivery flow takes its records into the ledger on its first run, and they appear here as they arrive."}
        data-testid="delivery-lookup-table"
      />
    </Page>
  );
}
