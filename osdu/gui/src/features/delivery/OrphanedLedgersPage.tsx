import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { ArrowRightLeft, Link2, RefreshCw, Sparkles, TriangleAlert } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { DataTable, type Column } from "@/components/DataTable";
import { EmptyState } from "@/components/EmptyState";
import { FilterBar } from "@/components/FilterBar";
import { FlowKindBadge } from "@/components/FlowKindBadge";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { SummaryStrip } from "@/components/SummaryStrip";
import { TruncatedText } from "@/components/TruncatedText";
import { isApiError } from "@/api/client";
import { deliveryApi, type OrphanedDocument } from "../../api/delivery";
import { useActivePartition } from "./activePartition";
import { useLinkedPartition } from "./useLinkedPartition";
import { NoFact } from "./Facts";
import { BulkTakeoverDialog } from "./BulkTakeoverDialog";
import { heldCount, heldText, KINDS, ledgerLabel, matches, suggestedOf, totals } from "./orphanedLedgers";
import { TakeoverDialog } from "./TakeoverDialog";

const ALL = "all";

const keyOf = (document: OrphanedDocument) => `${document.kind}:${document.flowName}`;

/** Who would take a missing document over: the suggested flow, else how many flows could, else that none can. */
function TakenOverBy({ document }: { document: OrphanedDocument }) {
  const suggested = suggestedOf(document);
  if (suggested !== undefined) {
    return (
      <span className="flex min-w-0 items-center gap-1.5" title="The one flow that shares the most with it. Nothing moves until you take it over.">
        <Sparkles className="size-3.5 shrink-0 text-primary" aria-hidden />
        <span className="block max-w-[300px] @max-5xl/table:max-w-[170px] @max-4xl/table:max-w-[140px] @max-3xl/table:max-w-[110px]">
          <TruncatedText text={suggested.flowName} mono maxWidth={1600} />
        </span>
      </span>
    );
  }

  return document.candidates.length === 0
    ? <span className="text-muted-foreground" title="No declared flow of its kind keeps a ledger in its partition">no declared flow can</span>
    : <span className="text-muted-foreground">{`pick one of ${document.candidates.length}`}</span>;
}

/**
 * The missing documents (osdu/docs/reference/concepts/renaming-a-pipeline.md, Missing documents): the flows no repository
 * declares any more whose ledgers still hold their records and history, in the workbench's partition. The repository sync
 * hands a flow's ledger to the flow that replaced it by itself when the two are one document under two names; each flow
 * listed here was renamed and changed at once, deleted in one sync and added back in another, moved, or kept its ledger
 * before the sync recorded documents. The page heads with what they left behind; a document opens the takeover, where a
 * person picks the declared flow that takes it over, the one that alone shares the most with it offered first; and the
 * documents with a suggestion can be taken over together. Until then a flow delivering the same records holds those whose
 * OSDU ids a missing one claimed.
 */
export default function OrphanedLedgersPage() {
  useLinkedPartition();
  const [active] = useActivePartition();
  const [search, setSearch] = useState("");
  const [kind, setKind] = useState(ALL);
  const [chosen, setChosen] = useState<OrphanedDocument | null>(null);
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [bulk, setBulk] = useState<OrphanedDocument[] | null>(null);
  const documents = useQuery({
    queryKey: ["delivery", "orphaned-ledgers", active],
    queryFn: () => deliveryApi.orphanedDocuments(active),
    refetchInterval: 30000,
  });

  const all = useMemo(() => documents.data ?? [], [documents.data]);
  const kinds = useMemo(() => KINDS.filter((k) => all.some((d) => d.kind === k)), [all]);
  const shown = useMemo(() => all.filter((d) => matches(d, search, kind === ALL ? null : kind)), [all, search, kind]);
  const sums = totals(all);
  const picked = all.filter((d) => selected.has(keyOf(d)) && suggestedOf(d) !== undefined);
  const where = active === null ? "" : ` in ${active}`;

  const columns: Column<OrphanedDocument>[] = [
    {
      id: "flow",
      header: "Missing flow",
      // The flow and the file it was last declared in: what a person knows the missing document by.
      render: (row) => (
        <span className="flex min-w-0 flex-col gap-0.5 py-0.5">
          <span className="flex min-w-0 items-center gap-1.5">
            {row.kind !== "delivery" && <FlowKindBadge kind={row.kind} testId={`orphaned-kind-${row.flowName}`} />}
            {active === null && [...new Set(row.ledgers.map((l) => l.partition))].map((p) => (
              <Badge key={p} variant="secondary" className="shrink-0 font-mono text-[10px]" title="The partition its ledger is kept in">{p}</Badge>
            ))}
            <span className="block max-w-[340px] @max-5xl/table:max-w-[210px] @max-4xl/table:max-w-[170px] @max-3xl/table:max-w-[130px]">
              <TruncatedText text={row.flowName} mono maxWidth={1600} className="font-medium" />
            </span>
          </span>
          <span className="block max-w-[340px] text-[11px] text-muted-foreground @max-5xl/table:max-w-[210px] @max-4xl/table:max-w-[170px] @max-3xl/table:max-w-[130px]">
            {row.relativePath
              ? <TruncatedText text={row.relativePath} mono maxWidth={1600} />
              : <span title="The read model holds no row of where it was declared">file not on record</span>}
          </span>
        </span>
      ),
    },
    { id: "left", header: "Left", render: (row) => (row.leftUtc ? <RelativeTime value={row.leftUtc} absolute={false} /> : <NoFact />) },
    {
      id: "held",
      header: "Left behind",
      fill: true,
      floor: 160,
      render: (row) => (
        <span title={[heldText(row.kind, row.rows, row.claimed), ...row.ledgers.map((l) => `${ledgerLabel(l)}: ${heldText(row.kind, l.rows, l.claimed)}`)].join("\n")}>
          <TruncatedText text={heldCount(row.kind, row.rows) + (row.ledgers.length > 1 ? ` in ${row.ledgers.length} ledgers` : "")} maxWidth={1600} />
        </span>
      ),
    },
    { id: "by", header: "Taken over by", render: (row) => <TakenOverBy document={row} /> },
    {
      id: "action",
      header: "",
      align: "right",
      render: (row) => (
        <Button
          variant="outline"
          size="sm"
          className="h-7"
          onClick={(event) => {
            event.stopPropagation();
            setChosen(row);
          }}
          aria-label={`Take over ${row.flowName}`}
          title="Take over"
          data-testid={`orphaned-takeover-${row.flowName}`}
        >
          <ArrowRightLeft />
          <span className="@max-5xl/table:hidden">Take over</span>
        </Button>
      ),
    },
  ];

  const toolbar = picked.length > 0 && (
    <div className="flex flex-wrap items-center gap-2 border-b border-border bg-accent/40 px-3 py-1.5" data-testid="orphaned-selection-bar">
      <span className="text-[13px] font-medium tabular-nums">{`${picked.length} selected`}</span>
      <Button variant="link" size="sm" className="h-6 px-0 text-[13px] text-muted-foreground" onClick={() => setSelected(new Set())} data-testid="orphaned-clear-selection">
        Clear
      </Button>
      <Button size="sm" className="ml-auto h-7" onClick={() => setBulk(picked)} data-testid="orphaned-take-over-suggested">
        <ArrowRightLeft />
        Take over with suggested flows
      </Button>
    </div>
  );

  let body;
  if (documents.isError) {
    body = (
      <Card className="gap-0 rounded-lg p-0" data-testid="orphaned-ledgers-error">
        <EmptyState
          icon={<TriangleAlert />}
          title="The orphaned ledgers could not be read"
          description={isApiError(documents.error) ? documents.error.detail ?? documents.error.title : String(documents.error)}
          action={(
            <Button variant="outline" size="sm" onClick={() => void documents.refetch()}>
              <RefreshCw />
              Try again
            </Button>
          )}
        />
      </Card>
    );
  } else if (documents.isSuccess && all.length === 0) {
    body = (
      <Card className="gap-0 rounded-lg p-0">
        <EmptyState
          icon={<Link2 />}
          title={`No orphaned ledgers${where}`}
          description="Every ledger that holds records or history is kept by a flow a repository declares. A flow renamed and changed at once, or deleted in one sync and added back in another, leaves its ledger here for you to hand to the flow that replaced it."
          data-testid="orphaned-ledgers-none"
        />
      </Card>
    );
  } else {
    body = (
      <DataTable
        columns={columns}
        rows={documents.isSuccess ? shown : undefined}
        rowKey={keyOf}
        selection={{
          selected,
          onChange: setSelected,
          isSelectable: (row) => suggestedOf(row) !== undefined,
        }}
        toolbar={toolbar}
        emptyMessage="No missing document matches the search."
        data-testid="orphaned-ledgers-table"
      />
    );
  }

  return (
    <Page data-testid="page-delivery-orphaned-ledgers">
      <PageHeader
        title="Orphaned ledgers"
        subtitle={`Flows no repository declares any more, with the records and history their ledgers keep${where}. Hand each to the flow that replaced it.`}
      />
      {all.length > 0 && (
        <SummaryStrip
          data-testid="orphaned-ledgers-summary"
          cells={[
            { label: "Missing documents", value: sums.documents.toLocaleString("en-US"), caption: sums.history > 0 ? `${sums.history} of them keep history, not records` : "flows no repository declares", testId: "orphaned-summary-documents" },
            { label: "Records left behind", value: sums.records.toLocaleString("en-US"), caption: "with their history and attempts", testId: "orphaned-summary-records" },
            {
              label: "OSDU ids claimed",
              value: sums.claimed.toLocaleString("en-US"),
              caption: sums.claimed > 0 ? "held back until taken over" : "none claimed",
              tone: sums.claimed > 0 ? "warning" : undefined,
              testId: "orphaned-summary-claimed",
            },
            { label: "Suggested takeovers", value: `${sums.suggested} of ${sums.documents}`, caption: "select them to take over together", testId: "orphaned-summary-suggested" },
          ]}
        />
      )}
      {all.length > 0 && (
        <FilterBar>
          <SearchInput value={search} onChange={setSearch} placeholder="Flow or file" label="Search the missing documents" testId="orphaned-search" />
          {kinds.length > 1 && (
            <Select value={kind} onValueChange={setKind}>
              <SelectTrigger size="sm" className="h-8 w-40" data-testid="orphaned-kind"><SelectValue /></SelectTrigger>
              <SelectContent>
                <SelectItem value={ALL}>All kinds</SelectItem>
                {kinds.map((k) => <SelectItem key={k} value={k}>{k}</SelectItem>)}
              </SelectContent>
            </Select>
          )}
        </FilterBar>
      )}
      {body}
      <TakeoverDialog document={chosen} partition={active} onClose={() => setChosen(null)} />
      <BulkTakeoverDialog
        documents={bulk}
        partition={active}
        onClose={() => setBulk(null)}
        onDone={(taken) => setSelected((current) => new Set([...current].filter((key) => !taken.includes(key))))}
      />
    </Page>
  );
}
