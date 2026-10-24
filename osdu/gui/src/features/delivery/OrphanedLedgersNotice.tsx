import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ArrowRight, Unlink } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { deliveryApi } from "../../api/delivery";
import { heldCount, totals } from "./orphanedLedgers";

/**
 * A line the Delivery page shows while flows no repository declares any more have left ledgers behind in the workbench's
 * partition (osdu/docs/reference/concepts/renaming-a-pipeline.md, Missing documents): how many, what they hold, and the way
 * to the Orphaned ledgers page. Nothing shows while there are none, or while they cannot be read: the page itself says why.
 */
export function OrphanedLedgersNotice({ partition }: { partition: string | null }) {
  const documents = useQuery({
    queryKey: ["delivery", "orphaned-ledgers", partition],
    queryFn: () => deliveryApi.orphanedDocuments(partition),
    refetchInterval: 60000,
  });

  const all = documents.data ?? [];
  if (all.length === 0) {
    return null;
  }

  const sums = totals(all);
  const held = [
    sums.records > 0 ? heldCount("delivery", sums.records) : null,
    sums.claimed > 0 ? `${sums.claimed.toLocaleString("en-US")} with OSDU ids held back until taken over` : null,
  ].filter((part): part is string => part !== null);
  return (
    <Card className="flex-row items-center gap-3 rounded-lg px-4 py-3" data-testid="delivery-orphaned-notice">
      <Unlink className="size-4 shrink-0 text-warning" aria-hidden />
      <p className="min-w-0 flex-1 text-[13px]">
        <span className="font-medium">
          {`${sums.documents} ${sums.documents === 1 ? "flow" : "flows"} no repository declares any more left ${sums.documents === 1 ? "its ledger" : "their ledgers"} behind`}
        </span>
        {held.length > 0 && <span className="text-muted-foreground">{`: ${held.join(", ")}.`}</span>}
      </p>
      <Button variant="outline" size="sm" className="h-7 shrink-0" asChild>
        <Link to="/delivery/ledgers/orphaned" data-testid="delivery-orphaned-open">
          Orphaned ledgers
          <ArrowRight />
        </Link>
      </Button>
    </Card>
  );
}
