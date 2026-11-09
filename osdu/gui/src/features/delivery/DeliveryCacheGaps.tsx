import { useState } from "react";
import { Switch } from "@/components/ui/switch";
import { deliveryApi, type DeliveryCacheGap } from "../../api/delivery";
import { PagedTable, type Column } from "@/components/PagedTable";
import { compositeLookedUp } from "./findByLines";

/** What each kind of gap means for the records built without it, as the table's tooltip says it. */
const GAP_MEANING: Record<string, string> = {
  unlisted: "No cached record answered to the value, or to the values a line compared together, so the records were built without one: a wellbore the cache does not hold yet.",
  listed: "No row was listed under any key a $findAll read (a wellbore's field, every one of its countries): a field or a country no access group lists yet.",
  unverified: "The records reference an id the cache holds no record under, written because the mapping says $unverified.",
  empty: "The path read held nothing, so the records were built without a value there: a wellbore without a field, or an empty field a $findAll asked for.",
};

const GAP_LABEL: Record<string, string> = {
  unlisted: "not found",
  listed: "no rows",
  unverified: "unverified",
  empty: "empty",
};

/**
 * What delivered records of one partition were built without, most records first: the values the cache did not hold when
 * they were rendered (a wellbore loaded after its logs, the access group a data office has not listed for a field yet).
 * Each is filled by the refresh that brings it, which tags the records like any cache change and updates them on their
 * next delivery; until then this is where they are flagged. It is the Missing from cache state of the Deliveries tab's list.
 */
export function DeliveryCacheGaps({ scope, type }: { scope: string; type: string | null }) {
  const [empty, setEmpty] = useState(false);

  const columns: Column<DeliveryCacheGap>[] = [
    {
      id: "type",
      header: "Type",
      render: (gap) => <span className="font-mono text-[12.5px]">{gap.typeName}</span>,
    },
    {
      id: "kind",
      header: "Missing",
      render: (gap) => (
        <span className="text-[12.5px]" title={GAP_MEANING[gap.kind] ?? gap.kind} data-testid="delivery-cache-gap-kind">
          {GAP_LABEL[gap.kind] ?? gap.kind}
        </span>
      ),
    },
    {
      id: "looked-for",
      header: "Looked for",
      fill: true,
      floor: 260,
      render: (gap) => {
        // Values a findBy line of several comparisons looked up together, which no row met all of.
        const together = gap.kind === "unlisted" ? compositeLookedUp(gap.path, gap.value) : null;
        return together !== null
          ? <span className="min-w-0 truncate font-mono text-[12.5px]" title={together}>{together}</span>
          : (
            <span className="min-w-0 truncate font-mono text-[12.5px]" title={`${gap.path} = ${gap.value || gap.key}`}>
              <span className="text-muted-foreground">{gap.path} = </span>
              {gap.kind === "unlisted" && gap.value ? gap.value : gap.key}
            </span>
          );
      },
    },
    {
      id: "records",
      header: "Records",
      align: "right",
      render: (gap) => <span className="font-mono tabular-nums" data-testid="delivery-cache-gap-records">{gap.records}</span>,
    },
  ];

  const toolbar = (
    <label className="flex items-center gap-2 border-b border-border px-3 py-1.5 text-[12.5px] text-muted-foreground">
      <Switch checked={empty} onCheckedChange={setEmpty} data-testid="delivery-cache-gaps-empty" />
      Include paths read empty
    </label>
  );

  return (
    <PagedTable
      queryKey={["delivery", "cache", "gaps", scope, type, empty]}
      fetchPage={(page, pageSize) => deliveryApi.cacheGaps({ scope, type: type ?? undefined, empty, page, pageSize })}
      columns={columns}
      rowKey={(gap) => `${gap.typeName}|${gap.path}|${gap.kind}|${gap.key}|${gap.value}`}
      emptyMessage="Nothing missing: every delivered record found what it looked for in this partition's cache."
      toolbar={toolbar}
      data-testid="delivery-cache-gaps-table"
    />
  );
}
