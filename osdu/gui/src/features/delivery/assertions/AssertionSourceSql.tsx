import { useState } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { ExplainMark } from "@/components/ExplainTip";
import { deliveryApi, type AssertionSourceRows } from "../../../api/delivery";
import { ProblemView, TaskProgress } from "../TemplateSheet";
import { SourceQueryView } from "../VariableSqlTab";
import { counted } from "./assertionFormat";
import { scopeText, type StagedRows } from "./assertionReading";

/** Where a check's result is kept: the run that recorded it and its test, which a query of the rows it read names. */
export interface CheckSource {
  assertionRunId: number;
  test: string;
}

const ABOUT_THE_QUERY = "It reads the delivery flow's own tables, the rows a delivery reads in the scope the check read them in, as they stand now and before the mapping's lookups and expressions: each row's key, every column the mapping reads, and the file and row it was landed from. Copy it into SSMS and run it: nothing here runs it. A row with no child rows still shows, with nulls, and each child table beyond one has a statement of its own, so their rows do not multiply.";

/** Which rows the query shows, in the words of the line above it. */
function about(rows: StagedRows, picked: AssertionSourceRows, narrowed: number | undefined): string {
  if (picked === "all") {
    return "Every row of the scope the check read";
  }

  const listed = narrowed !== undefined && narrowed < rows.listed
    ? `The first ${narrowed.toLocaleString("en-US")} of the ${counted(rows.listed, "row")} the check lists`
    : `The ${counted(rows.listed, "row")} the check lists`;
  return rows.failing !== null && rows.failing > rows.listed ? `${listed}, of ${rows.failing.toLocaleString("en-US")} that fail it` : listed;
}

/**
 * The staged rows a check read, as T-SQL over its delivery flow's own tables, to copy into SSMS: the rows it lists by their
 * keys, which a gap is read from, or every row of the scope it read. The control plane writes it from what the result kept,
 * with the writer a mapping's source query is written by; nothing here runs it.
 */
export function StagedRowsSql({ source, rows }: { source: CheckSource; rows: StagedRows }) {
  const [picked, setPicked] = useState<AssertionSourceRows>(rows.listed > 0 ? "listed" : "all");
  const query = useQuery({
    queryKey: ["delivery", "assertion-source-sql", source.assertionRunId, source.test, rows.index, picked],
    queryFn: () => deliveryApi.assertionSourceSql(source.assertionRunId, { test: source.test, assertion: rows.index, rows: picked }),
    placeholderData: keepPreviousData,
    staleTime: 60000,
    retry: false,
  });
  const scope = scopeText(rows.values);
  return (
    <div className="flex min-h-0 flex-col gap-2" data-testid="assertion-source-sql">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-[12px] text-muted-foreground">
        {rows.listed > 0 && (
          <ToggleGroup
            type="single"
            size="sm"
            variant="outline"
            value={picked}
            onValueChange={(next) => { if (next === "listed" || next === "all") { setPicked(next); } }}
            data-testid="assertion-source-sql-rows"
          >
            <ToggleGroupItem value="listed" className="h-7 text-[12px]" data-testid="assertion-source-sql-rows-listed">Rows listed</ToggleGroupItem>
            <ToggleGroupItem value="all" className="h-7 text-[12px]" data-testid="assertion-source-sql-rows-all">Every staged row</ToggleGroupItem>
          </ToggleGroup>
        )}
        <span className="inline-flex items-center gap-1" data-testid="assertion-source-sql-about">
          {`${about(rows, picked, picked === "listed" && !query.isPlaceholderData ? query.data?.keys : undefined)}.`}
          <ExplainMark title="This query" text={ABOUT_THE_QUERY} testId="assertion-source-sql-explain" />
        </span>
      </div>
      <p className="truncate font-mono text-[11.5px] text-muted-foreground" title={scope} data-testid="assertion-source-sql-scope">
        {`${rows.flow}${rows.interface === null ? "" : `/${rows.interface}`}${scope === "" ? "" : ` · ${scope}`}`}
      </p>
      {query.isError && <ProblemView error={query.error} testId="assertion-source-sql-error" />}
      {query.isPending && <TaskProgress label="Writing the query" testId="assertion-source-sql-progress" />}
      {query.data !== undefined && !query.isError && <SourceQueryView answer={query.data} columns={false} />}
    </div>
  );
}

/** The staged rows of a check, in SQL, over the page that opened it: the dashboard's check carries no room for a script. */
export function StagedRowsSqlDialog({ source, rows, onClose }: { source: CheckSource; rows: StagedRows; onClose: () => void }) {
  return (
    <Dialog open onOpenChange={(open) => { if (!open) { onClose(); } }}>
      <DialogContent className="flex max-h-[90vh] flex-col gap-3 overflow-hidden sm:max-w-4xl" data-testid="assertion-source-sql-dialog">
        <DialogHeader>
          <DialogTitle>The staged rows in SQL</DialogTitle>
          <DialogDescription>{`${rows.label}: what the tables of ${rows.flow} hold of the rows it read, as they stand now.`}</DialogDescription>
        </DialogHeader>
        <StagedRowsSql source={source} rows={rows} />
      </DialogContent>
    </Dialog>
  );
}
