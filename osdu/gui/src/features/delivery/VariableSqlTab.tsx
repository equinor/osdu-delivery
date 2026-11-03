import { useMemo, useState } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { DatabaseSearch, TriangleAlert } from "lucide-react";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { CodeView } from "@/components/CodeView";
import { EmptyState } from "@/components/EmptyState";
import { ExplainMark } from "@/components/ExplainTip";
import { deliveryApi, type DeliverySourceSql } from "../../api/delivery";
import { failingKeys } from "./sourceQuery";
import { ProblemView, TaskProgress } from "./TemplateSheet";
import { flowScope, useValueCheckContext, type ValueCheckSession } from "./useValueCheck";
import { failingOf, formatCount } from "./valueCheck";

/** The height the query keeps in the pane, which is taller than most of them run to. */
const CODE_HEIGHT = 340;

const ABOUT_THE_QUERY = "It reads the flow's own source tables, the rows a delivery reads in this scope, as they stand now and before the mapping's lookups and expressions. Copy it into SSMS and run it: nothing here runs it. A record with no child rows still shows, with nulls, and each child table beyond one has a statement of its own, so their rows do not multiply.";

/** Why there is no query to show, in the one place a reader looks for it. */
function NoQuery({ title, description }: { title: string; description: string }) {
  return <EmptyState icon={<DatabaseSearch />} title={title} description={description} data-testid="variable-sql-none" />;
}

/** What the attribute reads and the script that shows it, or why it reads nothing. */
function SourceQueryView({ answer }: { answer: DeliverySourceSql }) {
  return (
    <>
      {answer.missing.length > 0 && (
        <p className="flex items-start gap-1.5 text-[12px] text-warning" data-testid="variable-sql-missing">
          <TriangleAlert className="mt-0.5 size-3.5 shrink-0" />
          <span>{`The tables do not hold ${answer.missing.join(", ")}, so the query leaves it out, and a delivery of this mapping would not find it either.`}</span>
        </p>
      )}
      {answer.reads.length > 0 && (
        <ul className="flex flex-col gap-0.5 text-[12px]" data-testid="variable-sql-reads">
          {answer.reads.map((read) => (
            <li key={read.column} className="flex min-w-0 items-baseline gap-2">
              <span className="shrink-0 font-mono">{read.column}</span>
              <span className="min-w-0 truncate font-mono text-[11.5px] text-muted-foreground" title={read.table}>{read.table}</span>
            </li>
          ))}
        </ul>
      )}
      {answer.sql
        ? <CodeView value={answer.sql} language="sql" height={CODE_HEIGHT} data-testid="variable-sql" />
        : answer.entries === 0
          ? <NoQuery title="Nothing of the mapping fills this" description="No entry reaches this attribute, so no source column reads into it." />
          : <NoQuery title="No source column reads into this" description="Its value is written in the mapping, or comes from the cache or the platform." />}
    </>
  );
}

/**
 * The T-SQL that shows what an attribute reads, to copy into SSMS: once a check has found rows wrong with the attribute, over the
 * ones it named, else over every row of the scope. Written by the control plane from the flow's own tables, so it follows
 * the page's flow, scope values and row budget, and it is never run here.
 */
function SqlQuery({ session, path }: { session: ValueCheckSession; path: string }) {
  const { flow, sentValues, maxRows, ready } = session;
  // Null until the reader picks: the query opens on the failing rows once a check has listed any, which is what it is read for.
  const [picked, setPicked] = useState<"all" | "failing" | null>(null);
  const found = session.resultFor(path);
  const nested = session.nestedFor(path);
  const failing = useMemo(
    () => failingKeys([...(found?.variable.findings ?? []), ...nested.map((item) => item.finding)]),
    [found, nested],
  );
  const failingRows = found === undefined ? 0 : failingOf(found.variable.rows);
  const narrowed = failing.keys.length > 0 && (picked ?? "failing") === "failing";
  const keys = narrowed ? failing.keys : [];
  const query = useQuery({
    queryKey: ["delivery", "source-sql", flow?.pipelineId ?? null, flow?.interface ?? null, flow?.partition ?? null, path, sentValues, maxRows, keys],
    queryFn: () => deliveryApi.sourceSql(flow!.pipelineId, { target: path, values: sentValues, maxRows, keys: keys.length > 0 ? keys : undefined }, flowScope(flow!)),
    enabled: flow !== null && ready,
    placeholderData: keepPreviousData,
    staleTime: 30000,
    retry: false,
  });

  if (flow === null) {
    return <NoQuery title="No tables to read yet" description={session.blocked ?? "No flow renders with this mapping yet, and the tables are a flow's."} />;
  }

  if (!ready && session.blocked !== null) {
    return <NoQuery title="The query cannot be written yet" description={session.blocked} />;
  }

  // With no query there is nothing for a choice of rows to be about.
  const reads = query.data?.sql !== null && query.data?.sql !== undefined;
  return (
    <>
      {reads && (
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-[12px] text-muted-foreground">
        {failing.keys.length > 0 && (
          <ToggleGroup
            type="single"
            size="sm"
            variant="outline"
            value={narrowed ? "failing" : "all"}
            onValueChange={(next) => { if (next === "all" || next === "failing") { setPicked(next); } }}
            data-testid="variable-sql-rows"
          >
            <ToggleGroupItem value="all" className="h-7 text-[12px]" data-testid="variable-sql-rows-all">Every row</ToggleGroupItem>
            <ToggleGroupItem value="failing" className="h-7 text-[12px]" data-testid="variable-sql-rows-failing">Failing rows</ToggleGroupItem>
          </ToggleGroup>
        )}
        <span className="inline-flex items-center gap-1" data-testid="variable-sql-about">
          {narrowed
            ?`The ${formatCount(failing.keys.length)} ${failing.keys.length === 1 ? "record" : "records"} the check listed${failingRows > failing.keys.length ? `, of ${formatCount(failingRows)} that fail` : ""}.`
            : maxRows === 0 ? "Every row of the scope." : `The first ${formatCount(maxRows)} rows of the scope.`}
          <ExplainMark title="This query" text={ABOUT_THE_QUERY} testId="variable-sql-explain" />
        </span>
      </div>
      )}
      {query.isError && <ProblemView error={query.error} testId="variable-sql-error" />}
      {query.isPending && ready && <TaskProgress label="Writing the query" testId="variable-sql-progress" />}
      {query.data !== undefined && !query.isError && <SourceQueryView answer={query.data} />}
    </>
  );
}

/** A variable's SQL tab; the Templates page reads the same tree without a mapping, and shows no such tab. */
export function VariableSqlTab({ path }: { path: string }) {
  const session = useValueCheckContext();
  return (
    <div className="flex flex-col gap-2" data-testid="variable-sql-tab">
      {session !== null && <SqlQuery session={session} path={path} />}
    </div>
  );
}
