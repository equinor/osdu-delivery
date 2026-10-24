import { useMemo } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ClipboardList } from "lucide-react";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { DataTable, type Column } from "@/components/DataTable";
import { EmptyState } from "@/components/EmptyState";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import { inventoryApi, type Inventory } from "../../../api/inventories";
import { KindText } from "../KindText";
import { filtersOf, filtersText } from "../explorer/explorerModel";
import { useActivePartition } from "../activePartition";
import { ProblemView } from "../TemplateSheet";
import { RaisedCounts, RunStatusPill, StandingGlyph } from "./InventoryBadges";
import { InventoryLookupBox } from "./InventoryLookup";
import { InventoryReport } from "./InventoryReport";
import { EVERY_ID, MAX_ID_LENGTH, counted, inventoryRoute, standingOf, type InventoryRef, type InventoryView } from "./inventoryFormat";
import { keyText } from "./duplicates/duplicateModel";

/** How often the inventories are read again, so a run under way shows its outcome as it lands. */
const REFRESH_MS = 15000;

/** The partition a link names, when it is one. */
const PARTITION_ID = /^[A-Za-z0-9_.-]{1,64}$/;

/** The OSDU id the address opens in the report's panel, or null for none. */
function openIdOf(params: URLSearchParams): string | null {
  const id = params.get("id")?.trim() ?? "";
  return id === "" || id.length > MAX_ID_LENGTH ? null : id;
}

/** A positive whole number the address names (a selection's, a check's), or null for none. */
function numberOf(params: URLSearchParams, name: string): number | null {
  const value = Number.parseInt(params.get(name) ?? "", 10);
  return Number.isSafeInteger(value) && value > 0 ? value : null;
}

/** The inventory the address names, or null for every inventory. */
function referenceOf(params: URLSearchParams): InventoryRef | null {
  const partition = params.get("partition");
  const inventoryId = Number.parseInt(params.get("inventory") ?? "", 10);
  return partition !== null && PARTITION_ID.test(partition) && Number.isSafeInteger(inventoryId) && inventoryId > 0 ? { partition, inventoryId } : null;
}

/**
 * The inventories of the partition picked in the title bar (osdu/docs/reference/flow/inventory.md): every id an OSDU kind
 * holds, set against every ledger of the partition, each id with its finding. The page is two views of one address: every
 * inventory, with what its last reconcile raised and its newest run, and one inventory's report, whose grid takes the page's
 * whole width and scrolls inside it. A lookup by OSDU id across the partition's inventories is in the heading of both. The
 * inventory, the finding, the tab, the id open in the report's panel, the conditions its ids are selected by (`f`, as the
 * explorer writes them) or the selection listed by its number (`s`), and on the Duplicates tab the check shown (`dc`), the
 * key being built (`dk`) and the outcome listed (`do`) live in the address, so a link lands on the same view.
 */
export default function DeliveryInventoriesPage() {
  const [params, setParams] = useSearchParams();
  const navigate = useNavigate();
  const [active] = useActivePartition();
  const list = useQuery({
    queryKey: ["delivery", "inventories", "list", active],
    queryFn: () => inventoryApi.list(),
    refetchInterval: REFRESH_MS,
  });
  const reference = referenceOf(params);
  const tab = params.get("tab");
  const conditionsText = params.get("f");
  const conditions = useMemo(() => filtersOf(conditionsText), [conditionsText]);
  const view: InventoryView = tab === "runs" || tab === "removals" || tab === "duplicates" ? tab : "ids";

  const update = (changes: Record<string, string | null>) => setParams((current) => {
    const next = new URLSearchParams(current);
    for (const [key, changed] of Object.entries(changes)) {
      if (changed === null) {
        next.delete(key);
      } else {
        next.set(key, changed);
      }
    }

    return next;
  }, { replace: true });

  const open = (ref: InventoryRef | null) => (ref === null
    ? update({ partition: null, inventory: null, finding: null, tab: null, id: null, f: null, s: null, dc: null, dk: null, do: null })
    : navigate(inventoryRoute(ref)));

  if (reference !== null) {
    return (
      <Page data-testid="page-delivery-inventories">
        <InventoryReport
          key={`${reference.partition}-${reference.inventoryId}`}
          reference={reference}
          siblings={list.data?.inventories ?? []}
          finding={params.get("finding")}
          // The panel steps through the ids of the finding in view, so another finding closes it.
          onFinding={(finding) => update({ finding, id: null })}
          view={view}
          onView={(next) => update({ tab: next === "ids" ? null : next })}
          onOpen={open}
          openId={openIdOf(params)}
          onOpenId={(id) => update({ id })}
          conditions={conditions}
          // Other conditions select other ids, so the id open in the panel is let go, and so is a selection listed by its number.
          onConditions={(next) => update({ f: filtersText(next), s: null, id: null })}
          selectionId={numberOf(params, "s")}
          // A selection made of a duplicate check's picks opens on the Ids tab, on a finding a removal takes when it holds one.
          onSelectionId={(selectionId, finding) => update(selectionId === null
            ? { s: null, id: null }
            : { s: String(selectionId), f: null, tab: null, finding: finding ?? EVERY_ID, id: null })}
          duplicateCheck={numberOf(params, "dc")}
          onDuplicateCheck={(checkId) => update({ dc: checkId === null ? null : String(checkId), id: null })}
          duplicateKey={params.get("dk")}
          onDuplicateKey={(key) => update({ dk: key.length === 0 ? null : keyText(key) })}
          duplicateOutcome={params.get("do")}
          onDuplicateOutcome={(outcome) => update({ do: outcome, id: null })}
        />
      </Page>
    );
  }

  if (list.isError) {
    return (
      <Page data-testid="page-delivery-inventories">
        <PageHeader title="Inventories" />
        <ProblemView error={list.error} testId="inventories-error" />
      </Page>
    );
  }

  if (list.data === undefined) {
    return (
      <Page data-testid="page-delivery-inventories">
        <PageHeader title="Inventories" />
        <Skeleton className="h-96 w-full rounded-lg" />
      </Page>
    );
  }

  const inventories = list.data.inventories;
  const raised = inventories.reduce((sum, inventory) => sum + (inventory.raised ?? 0), 0);
  const columns: Column<Inventory>[] = [
    {
      // The flow that keeps it, under its name, so a narrow page spends no column on it.
      id: "inventory",
      header: "Inventory",
      render: (row) => (
        <span className="flex items-start gap-2">
          <span className="mt-0.5"><StandingGlyph standing={standingOf(row)} /></span>
          <span className="flex min-w-0 flex-col">
            <span className="font-medium">{row.name}</span>
            <TruncatedText text={row.flowName} mono maxWidth={240} className="text-[11.5px] text-muted-foreground" />
          </span>
        </span>
      ),
    },
    { id: "kind", header: "Kind", fill: true, floor: 110, render: (row) => <KindText kind={row.kind} /> },
    { id: "raised", header: "Raised", render: (row) => <RaisedCounts counts={row.reconciled} testId={`inventories-raised-${row.inventoryId}`} /> },
    {
      // A run under way, or one that failed after the last reconcile, is what needs a look; else when it was last reconciled.
      id: "reconciled",
      header: "Reconciled",
      render: (row) => {
        if (row.latest !== undefined && row.latest.inventoryRunId !== row.lastReconcileRunId) {
          return <RunStatusPill status={row.latest.status} testId={`inventories-newest-${row.inventoryId}`} />;
        }

        return row.lastReconciledUtc === undefined
          ? <span className="text-[12px] text-muted-foreground">{row.lastBuiltUtc === undefined ? "not built" : "not yet"}</span>
          : <span className="whitespace-nowrap text-[12px]"><RelativeTime value={row.lastReconciledUtc} absolute={false} /></span>;
      },
    },
  ];
  if (active === null) {
    columns.splice(1, 0, { id: "partition", header: "Partition", render: (row) => <span className="font-mono text-[12px]">{row.partition}</span> });
  }

  return (
    <Page data-testid="page-delivery-inventories">
      <PageHeader
        title="Inventories"
        subtitle={inventories.length === 0
          ? `No inventory${list.data.partition === undefined ? "" : ` in ${list.data.partition}`} yet.`
          : `${inventories.length.toLocaleString("en-US")} ${inventories.length === 1 ? "inventory" : "inventories"}${list.data.partition === undefined ? "" : ` in ${list.data.partition}`}, ${counted(raised, "id")} raised by their last reconciles.`}
        actions={<InventoryLookupBox />}
      />
      {inventories.length === 0
        ? (
          <Card className="gap-0 rounded-lg p-0">
            <EmptyState
              icon={<ClipboardList />}
              title="No inventory has been built here"
              description="An inventory flow (flowType: inventory) keeps every id the kinds it names hold, and compares each with the ledgers of the partition. Its first build registers its inventories."
              data-testid="inventories-empty"
            />
          </Card>
        )
        : (
          <DataTable
            columns={columns}
            rows={inventories}
            rowKey={(row) => `${row.partition}-${row.inventoryId}`}
            onRowClick={(row) => open({ partition: row.partition, inventoryId: row.inventoryId })}
            emptyMessage="No inventory."
            data-testid="inventories-table"
          />
        )}
    </Page>
  );
}
