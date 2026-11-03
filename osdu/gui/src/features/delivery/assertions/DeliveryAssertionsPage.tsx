import { useQuery } from "@tanstack/react-query";
import { Skeleton } from "@/components/ui/skeleton";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { deliveryApi } from "../../../api/delivery";
import { useActivePartition } from "../activePartition";
import { ProblemView } from "../TemplateSheet";
import { AssertionBoard } from "./AssertionBoard";
import { counted } from "./assertionFormat";

/** What the page covers, in one line; how the tests stand is the status strip's to say. */
function subtitle(tests: number, flows: number, sets: number, partition: string | null): string {
  return `${counted(tests, "assertion")} in ${counted(flows, "flow")} of ${counted(sets, "set")}${partition === null ? "" : `, in ${partition}`}, each by its latest result.`;
}

/**
 * Every test of every assertion flow in the workbench's partition, on one board of sets: how OSDU stands now that the data
 * has landed, and where it does not hold. A set opens its report; a test runs from here on its own, with others picked,
 * with every test of its flow, or with every flow of its set.
 */
export default function DeliveryAssertionsPage() {
  const [active] = useActivePartition();
  const board = useQuery({
    queryKey: ["delivery", "assertions", "board", active],
    queryFn: () => deliveryApi.assertionBoard(),
    refetchInterval: 15000,
  });

  return (
    <Page data-testid="page-delivery-assertions">
      <PageHeader title="Assertions" subtitle={board.data ? subtitle(board.data.totals.tests, board.data.totals.flows, new Set(board.data.flows.map((flow) => flow.set ?? "")).size, board.data.partition) : undefined} />
      {board.isError
        ? <ProblemView error={board.error} testId="assertion-board-error" />
        : board.data === undefined
          ? <Skeleton className="h-72 w-full rounded-lg" />
          : <AssertionBoard board={board.data} scope="all" />}
    </Page>
  );
}
