// One report: its filters, and a tile per view of each query, under the headings the document gives them. Every query
// runs when the page opens, each on its own, and again only when a value it binds changes or someone asks. The
// filters' values live in the page's address, so a link to the page is a link to what it showed.

import { useCallback, useMemo, useState } from "react";
import { Link as RouterLink, useParams, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft, ChartColumn, EllipsisVertical, FileCode2, Link2, Loader2, RefreshCw } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Skeleton } from "@/components/ui/skeleton";
import { CorrelationError } from "@/components/CorrelationError";
import { EmptyState } from "@/components/EmptyState";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { isApiError } from "../../api/client";
import { reportsApi } from "../../api/endpoints";
import type { Report, ReportParameter } from "../../api/types";
import { useTabFlowKind, useTabTitle } from "../../layout/workbench/TabsContext";
import { REPORT_KIND } from "../../modules/reportKind";
import { ReportParameters } from "./ReportParameters";
import { ReportQuerySheet } from "./ReportQuerySheet";
import { ReportTile } from "./ReportTile";
import { clearValues, layOut, readValues, writeValue } from "./reportModel";
import { useReportQueries } from "./useReportQueries";

/** The key a report's definition is cached under. */
export const REPORT_KEY = "report";

function BackToReports() {
  return (
    <RouterLink
      to="/reports"
      className="inline-flex w-fit items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
      data-testid="report-back"
    >
      <ArrowLeft className="size-3.5" />
      Reports
    </RouterLink>
  );
}

export default function ReportPage() {
  const { reportId = "" } = useParams<{ reportId: string }>();
  const report = useQuery({ queryKey: [REPORT_KEY, reportId], queryFn: () => reportsApi.get(reportId) });
  useTabTitle(report.data?.title);
  // The tab wears the report kind's icon, as a pipeline's tab wears its kind's.
  useTabFlowKind(report.data === undefined ? undefined : REPORT_KIND);

  if (report.isPending) {
    return (
      <Page data-testid="page-report">
        <BackToReports />
        <div className="flex flex-col gap-2">
          <Skeleton className="h-7 w-72" />
          <Skeleton className="h-4 w-96" />
        </div>
        <div className="grid grid-cols-[repeat(auto-fit,minmax(12rem,1fr))] gap-3">
          {[0, 1, 2, 3].map((index) => <Skeleton key={index} className="h-[86px] rounded-lg" />)}
        </div>
        <Skeleton className="h-64 w-full rounded-lg" />
      </Page>
    );
  }

  if (report.isError) {
    const missing = isApiError(report.error) && report.error.status === 404;
    return (
      <Page data-testid="page-report">
        <BackToReports />
        {missing
          ? (
            <EmptyState
              icon={<ChartColumn />}
              title="This report is not here"
              description="It may have been renamed or taken out of its repository. The list holds the reports there are now."
              action={<Button size="sm" variant="outline" asChild><RouterLink to="/reports">All reports</RouterLink></Button>}
              data-testid="report-missing"
            />
          )
          : isApiError(report.error)
            ? <CorrelationError error={report.error} />
            : <p className="text-[13px] text-destructive">{String(report.error)}</p>}
      </Page>
    );
  }

  return <ReportBody report={report.data} />;
}

function ReportBody({ report }: { report: Report }) {
  const [searchParams, setSearchParams] = useSearchParams();
  const given = useMemo(() => readValues(searchParams, report.parameters), [searchParams, report.parameters]);
  const queries = useReportQueries(report, given);
  const sections = useMemo(() => layOut(report), [report]);
  const [detailsOf, setDetailsOf] = useState<string | null>(null);

  // One edit of the address per action, made from the address as it is then, so two quick clicks never undo each other.
  const setValue = useCallback((parameter: ReportParameter, value: string | null) => {
    setSearchParams((current) => writeValue(current, parameter, value), { replace: true });
  }, [setSearchParams]);
  const reset = useCallback(() => {
    setSearchParams((current) => clearValues(current, report.parameters), { replace: true });
  }, [setSearchParams, report.parameters]);

  const shown = [...queries.states.values()].filter((state) => state.query.views.length > 0);
  const inFlight = shown.filter((state) => state.status === "running" || state.refreshing).length;
  const copyLink = () => {
    // The clipboard is not there on a page served without a secure origin, and can be refused: either way, say so.
    const refused = () => toast.error("The link could not be copied.", { description: "Copy it from the address bar instead." });
    if (navigator.clipboard === undefined) {
      refused();
      return;
    }

    navigator.clipboard.writeText(window.location.href).then(
      () => toast.success("Link copied.", { description: "It opens this report with the filters as they are now." }),
      refused,
    );
  };

  return (
    <Page data-testid="page-report">
      <BackToReports />
      <PageHeader
        title={<span data-testid="report-title">{report.title}</span>}
        subtitle={report.description ?? undefined}
        actions={(
          <>
            <span className="font-mono text-xs text-muted-foreground" data-testid="report-as-of">
              {inFlight > 0
                ? `Reading ${inFlight} of ${shown.length}`
                : queries.asOf !== null
                  ? <span title={queries.asOf.toLocaleString()}>As of {queries.asOf.toLocaleTimeString()}</span>
                  : null}
            </span>
            <Button variant="outline" size="sm" onClick={queries.refresh} data-testid="report-refresh">
              {queries.running ? <Loader2 className="animate-spin" /> : <RefreshCw />}
              Refresh
            </Button>
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button variant="outline" size="icon-sm" aria-label="More for this report" data-testid="report-menu">
                  <EllipsisVertical />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuItem asChild data-testid="report-open-document">
                  <RouterLink to={`/pipelines/${report.id}?tab=yaml`}>
                    <FileCode2 />
                    Open its document
                  </RouterLink>
                </DropdownMenuItem>
                <DropdownMenuItem onSelect={copyLink} data-testid="report-copy-link">
                  <Link2 />
                  Copy a link to this view
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </>
        )}
      />

      <ReportParameters report={report} given={given} states={queries.states} onChange={setValue} onReset={reset} />

      {sections.length === 0
        ? (
          <EmptyState
            icon={<ChartColumn />}
            title="This report shows nothing"
            description="Every query of its document is hidden. Show one by leaving out its 'show: none'."
          />
        )
        : (
          <div className="@container/report flex flex-col gap-6">
            {sections.map((section, index) => (
              <section key={`${section.title ?? ""}-${index}`} className="flex flex-col gap-3" data-testid="report-section">
                {section.title !== null && <h2 className="text-base font-medium">{section.title}</h2>}
                <div className="grid grid-cols-12 gap-4">
                  {section.tiles.map((tile) => {
                    const state = queries.states.get(tile.query.name);
                    return state === undefined
                      ? null
                      : <ReportTile key={tile.id} report={report} tile={tile} state={state} given={given} onSelect={setValue} onDetails={setDetailsOf} />;
                  })}
                </div>
              </section>
            ))}
          </div>
        )}

      <ReportQuerySheet
        report={report}
        state={detailsOf === null ? null : queries.states.get(detailsOf) ?? null}
        given={given}
        onClose={() => setDetailsOf(null)}
      />
    </Page>
  );
}
