// The reports the repositories declare, by the batch each is filed under. Opening one opens its own page, which runs it.

import { useMemo } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ChartColumn, TriangleAlert } from "lucide-react";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { ConnectionRef } from "@/components/ConnectionRef";
import { CopyButton } from "@/components/CopyButton";
import { CorrelationError } from "@/components/CorrelationError";
import { DataTable, type Column, type TableGrouping } from "@/components/DataTable";
import { EmptyState } from "@/components/EmptyState";
import { FilterBar } from "@/components/FilterBar";
import { FilterCombobox } from "@/components/FilterCombobox";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import { isApiError } from "../../api/client";
import { reportsApi } from "../../api/endpoints";
import type { ReportSummary } from "../../api/types";
import { reportPath } from "../../modules/reportKind";

/** The key the list of reports is cached under. */
export const REPORTS_KEY = "reports";

/** A report to start from: one query, shown as its answer suggests. Copied from the empty list. */
const STARTER = `flowType: rpt
name: my-first-rpt
title: My first report
description: What this report is for, in a sentence.
connection: \${env:SQLFLOW_DW}

queries:
  rows_by_schema:
    title: Tables by schema
    sql: |
      SELECT s.name AS SchemaName, COUNT(*) AS Tables
      FROM sys.tables AS t
      JOIN sys.schemas AS s ON s.schema_id = t.schema_id
      GROUP BY s.name
      ORDER BY Tables DESC
`;

const NO_BATCH = "Not filed under a batch";

function matches(report: ReportSummary, words: readonly string[]): boolean {
  if (words.length === 0) return true;
  const text = [report.title, report.name, report.description ?? "", report.batch ?? "", report.repoName, report.relativePath]
    .join("\n").toLowerCase();
  return words.every((word) => text.includes(word));
}

export default function ReportsPage() {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const search = searchParams.get("q") ?? "";
  const repo = searchParams.get("repo") ?? "";
  const reports = useQuery({ queryKey: [REPORTS_KEY], queryFn: () => reportsApi.list() });

  // One edit of the address per action, made from the address as it is then.
  const setParam = (name: string, value: string) => setSearchParams((current) => {
    const next = new URLSearchParams(current);
    if (value === "") next.delete(name);
    else next.set(name, value);
    return next;
  }, { replace: true });

  const all = reports.data;
  const repos = useMemo(() => {
    const names = new Map<string, string>();
    for (const report of all ?? []) names.set(report.repoId, report.repoName);
    return [...names].map(([value, label]) => ({ value, label })).sort((a, b) => a.label.localeCompare(b.label));
  }, [all]);

  const rows = useMemo(() => {
    if (all === undefined) return undefined;
    const words = search.toLowerCase().split(/\s+/).filter((word) => word.length > 0);
    return all.filter((report) => (repo === "" || report.repoId === repo) && matches(report, words));
  }, [all, search, repo]);

  const columns = useMemo<Column<ReportSummary>[]>(() => [
    {
      id: "title",
      header: "Report",
      width: 280,
      render: (report) => (
        <span className="inline-flex min-w-0 max-w-full items-center gap-2">
          <ChartColumn className="size-4 shrink-0 text-muted-foreground" />
          <TruncatedText text={report.title} maxWidth={220} className="text-[13px] font-medium" />
          {report.problem !== null && (
            <Tooltip>
              <TooltipTrigger asChild>
                <span className="inline-flex shrink-0 items-center gap-1 text-xs text-warning" data-testid="report-row-problem">
                  <TriangleAlert className="size-3.5" />
                  does not load
                </span>
              </TooltipTrigger>
              <TooltipContent className="max-w-sm">{report.problem}</TooltipContent>
            </Tooltip>
          )}
        </span>
      ),
    },
    {
      id: "description",
      header: "What it shows",
      fill: true,
      floor: 160,
      render: (report) => <TruncatedText text={report.description} maxWidth={720} placeholder="" className="text-[13px] text-muted-foreground" />,
    },
    {
      id: "views",
      header: "Views",
      align: "right",
      width: 72,
      render: (report) => <span className="font-mono text-xs tabular-nums">{report.problem === null ? report.views : "-"}</span>,
    },
    {
      id: "parameters",
      header: "Filters",
      align: "right",
      width: 72,
      render: (report) => <span className="font-mono text-xs tabular-nums">{report.parameters === 0 ? "-" : report.parameters}</span>,
    },
    {
      id: "connection",
      header: "Reads",
      width: 170,
      render: (report) => <ConnectionRef value={report.connection} maxWidth={150} />,
    },
    ...(repos.length > 1
      ? [{
        id: "repo",
        header: "Repository",
        width: 170,
        render: (report: ReportSummary) => <TruncatedText text={report.repoName} maxWidth={150} className="text-[13px]" />,
      }]
      : []),
  ], [repos.length]);

  // The API answers the reports by batch, then title, so each batch's rows are together.
  const grouping = useMemo<TableGrouping<ReportSummary>>(() => ({
    levels: [{
      key: (report) => report.batch ?? "",
      renderHeader: (group) => (
        <span className="inline-flex items-center gap-2">
          <span className="text-[13px] font-medium">{group[0].batch ?? NO_BATCH}</span>
          <span className="font-mono text-xs text-muted-foreground">{group.length}</span>
        </span>
      ),
    }],
  }), []);

  const none = all !== undefined && all.length === 0;
  return (
    <Page data-testid="page-reports">
      <PageHeader
        title="Reports"
        subtitle="Read-only queries the repositories declare, run when you open one and shown as figures, charts and tables."
      />

      {reports.isError && (isApiError(reports.error)
        ? <CorrelationError error={reports.error} />
        : <p className="text-[13px] text-destructive">{String(reports.error)}</p>)}

      {none
        ? (
          <EmptyState
            icon={<ChartColumn />}
            title="No reports yet"
            description={(
              <>
                A report is a document in a repository that starts with <code className="font-mono text-xs">flowType: rpt</code>:
                a connection, and queries that only read. It appears here once its repository is synced.
              </>
            )}
            action={<CopyButton label="Copy a report to start from" text={STARTER} testId="reports-copy-starter" />}
            data-testid="reports-empty"
          />
        )
        : !reports.isError && (
          <>
            <FilterBar>
              <SearchInput
                value={search}
                onChange={(value) => setParam("q", value)}
                placeholder="Find a report"
                label="Find a report"
                testId="reports-search"
              />
              {repos.length > 1 && (
                <FilterCombobox
                  options={repos}
                  value={repo}
                  onChange={(value) => setParam("repo", value)}
                  placeholder="Every repository"
                  searchPlaceholder="Find a repository..."
                  ariaLabel="Repository"
                  testId="reports-repo"
                  className="w-56"
                />
              )}
            </FilterBar>
            <DataTable
              columns={columns}
              rows={rows}
              rowKey={(report) => report.id}
              onRowClick={(report) => navigate(reportPath(report.id))}
              grouping={grouping}
              emptyMessage="No report matches."
              data-testid="reports-table"
            />
          </>
        )}
    </Page>
  );
}
