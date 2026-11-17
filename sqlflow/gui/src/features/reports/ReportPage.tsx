// One report: a header of one line that stays in view (where the report is, its name, the page's filters, when it was
// read), and under it a tile per view of each query, under the headings the document gives them. Every query runs when
// the page opens, each on its own, and again only when a value it binds changes or someone asks. The filters' values
// live in the page's address, so a link to the page is a link to what it showed.

import { useCallback, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Link as RouterLink, useParams, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ChartColumn, EllipsisVertical, FileCode2, FunnelX, Link2, Loader2, RefreshCw } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Skeleton } from "@/components/ui/skeleton";
import { CorrelationError } from "@/components/CorrelationError";
import { EmptyState } from "@/components/EmptyState";
import { ExplainMark } from "@/components/ExplainTip";
import { Page } from "@/components/Page";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import { reportsApi } from "../../api/endpoints";
import type { Report, ReportParameter } from "../../api/types";
import { useTabFlowKind, useTabTitle } from "../../layout/workbench/TabsContext";
import { REPORT_KIND } from "../../modules/reportKind";
import { usePageAddress } from "../../embed/hostState";
import { ExplainPageButton } from "../chat/ExplainPageButton";
import { ReportParameters } from "./ReportParameters";
import { ReportQuerySheet } from "./ReportQuerySheet";
import { ReportTile } from "./ReportTile";
import { clearValues, isSet, layOut, placeParameters, readValues, writeValue } from "./reportModel";
import { useReportQueries } from "./useReportQueries";

/** The key a report's definition is cached under. */
export const REPORT_KEY = "report";

/** The room between the header's three parts, in pixels: what `gap-x-4` lays between them. */
const HEADER_GAP = 16;

/** The room between two filters of the header, in pixels: what `gap-x-3` lays between them. */
const FILTER_GAP = 12;

/**
 * The header's frame: one line that stays at the top of the page while its tiles scroll under it, so what the report
 * is narrowed to is in view wherever a mark is clicked. It reaches over the page's own padding to the editor's edges.
 * The filters sit between the name and the actions while all three fit on the line; when they do not (a narrow
 * window, a report with many filters) the filters take one line of their own under the other two, and wrap there.
 * Which it is comes from the parts' own widths, so it holds for any report and any window.
 */
function HeaderFrame({ name, filters, actions }: { name: ReactNode; filters?: ReactNode; actions?: ReactNode }) {
  const frame = useRef<HTMLDivElement>(null);
  const nameBox = useRef<HTMLDivElement>(null);
  const filterBox = useRef<HTMLDivElement>(null);
  const actionBox = useRef<HTMLDivElement>(null);
  const [beside, setBeside] = useState(true);

  useLayoutEffect(() => {
    const boxes = [frame.current, nameBox.current, filterBox.current, actionBox.current];
    if (boxes.some((box) => box === null)) return undefined;
    const measure = () => {
      const fields = [...(filterBox.current?.children ?? [])] as HTMLElement[];
      const wanted = fields.reduce((sum, field) => sum + field.offsetWidth, 0) + FILTER_GAP * Math.max(0, fields.length - 1);
      // The name is measured by what it holds, not by the box, which is as wide as the line once the filters are under it.
      const named = [...(nameBox.current?.children ?? [])] as HTMLElement[];
      const nameWidth = named.reduce((sum, part) => sum + part.offsetWidth, 0) + 6 * Math.max(0, named.length - 1);
      const room = (frame.current?.clientWidth ?? 0) - nameWidth - (actionBox.current?.offsetWidth ?? 0) - 2 * HEADER_GAP;
      setBeside(wanted <= room);
    };
    // The observer answers once as it starts and again whenever a part changes size: a filter set, a longer title.
    const observer = new ResizeObserver(measure);
    for (const box of boxes) {
      if (box !== null) observer.observe(box);
    }

    return () => observer.disconnect();
  }, []);

  return (
    <header
      className="sticky top-0 z-20 -mx-4 -mt-4 border-b border-border bg-background px-4 py-2 md:-mx-6 md:-mt-6 md:px-6"
      data-testid="report-header"
      data-filters={beside ? "beside" : "under"}
    >
      <div ref={frame} className="grid grid-cols-[auto_minmax(0,1fr)_auto] items-start gap-x-4 gap-y-2">
        <div ref={nameBox} className="flex min-h-8 min-w-0 items-center gap-1.5">
          <RouterLink
            to="/reports"
            className="shrink-0 text-[13px] text-muted-foreground hover:text-foreground"
            data-testid="report-back"
          >
            Reports
          </RouterLink>
          <span className="shrink-0 text-muted-foreground/60" aria-hidden>/</span>
          {name}
        </div>
        <div
          ref={filterBox}
          className={cn(
            "flex min-w-0 items-center gap-x-3 gap-y-1.5",
            beside ? "flex-nowrap" : "order-last col-span-3 flex-wrap",
          )}
        >
          {filters}
        </div>
        <div ref={actionBox} className={cn("flex min-h-8 shrink-0 items-center gap-2", !beside && "col-start-3")}>{actions}</div>
      </div>
    </header>
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
        <HeaderFrame name={<Skeleton className="h-6 w-56" />} />
        <Skeleton className="h-[62px] w-full rounded-lg" />
        <Skeleton className="h-64 w-full rounded-lg" />
      </Page>
    );
  }

  if (report.isError) {
    const missing = isApiError(report.error) && report.error.status === 404;
    return (
      <Page data-testid="page-report">
        <HeaderFrame name={<span className="text-[13px] text-muted-foreground">Not found</span>} />
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
  const places = useMemo(() => placeParameters(report), [report]);
  const pageFilters = useMemo(
    () => report.parameters.filter((parameter) => places.get(parameter.name) === null), [report, places]);
  const anySet = report.parameters.some((parameter) => isSet(parameter, given));
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
  // Where this view is opened from elsewhere; none inside an editor that does not know the web GUI's address.
  const address = usePageAddress();
  const copyLink = () => {
    // The clipboard is not there on a page served without a secure origin, and can be refused: either way, say so.
    const refused = () => toast.error("The link could not be copied.", { description: "Copy it from the address bar instead." });
    if (address === null || navigator.clipboard === undefined) {
      refused();
      return;
    }

    navigator.clipboard.writeText(address).then(
      () => toast.success("Link copied.", { description: "It opens this report with the filters as they are now." }),
      refused,
    );
  };

  return (
    <Page data-testid="page-report">
      <HeaderFrame
        name={(
          <>
            <h1
              className="min-w-0 max-w-[28rem] truncate text-lg font-semibold leading-7"
              title={report.title}
              data-testid="report-title"
            >
              {report.title}
            </h1>
            {report.description !== null && (
              <ExplainMark title={report.title} text={report.description} testId="report-about" />
            )}
            <ExplainPageButton />
          </>
        )}
        filters={(
          <>
            <ReportParameters report={report} parameters={pageFilters} given={given} states={queries.states} onChange={setValue} />
            {/* Kept in its place while nothing is set, so setting a filter never moves the header or the tiles under it. */}
            {report.parameters.length > 0 && (
              <Button
                variant="ghost"
                size="sm"
                onClick={reset}
                className={cn(!anySet && "invisible")}
                data-testid="report-parameters-reset"
              >
                <FunnelX />
                Reset
              </Button>
            )}
          </>
        )}
        actions={(
          <>
            <span className="whitespace-nowrap font-mono text-xs text-muted-foreground" data-testid="report-as-of">
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
                {address !== null && (
                  <DropdownMenuItem onSelect={copyLink} data-testid="report-copy-link">
                    <Link2 />
                    Copy a link to this view
                  </DropdownMenuItem>
                )}
              </DropdownMenuContent>
            </DropdownMenu>
          </>
        )}
      />

      {sections.length === 0
        ? (
          <EmptyState
            icon={<ChartColumn />}
            title="This report shows nothing"
            description="Every query of its document is hidden. Show one by leaving out its 'show: none'."
          />
        )
        : (
          <div className="@container/report flex flex-col gap-5">
            {sections.map((section, index) => (
              <section key={`${section.title ?? ""}-${index}`} className="flex flex-col gap-3" data-testid="report-section">
                {section.title !== null && <h2 className="text-base font-medium leading-6">{section.title}</h2>}
                <div className="grid grid-cols-12 gap-3">
                  {section.tiles.map((tile) => {
                    const state = queries.states.get(tile.query.name);
                    return state === undefined
                      ? null
                      : (
                        <ReportTile
                          key={tile.id}
                          report={report}
                          tile={tile}
                          state={state}
                          states={queries.states}
                          given={given}
                          places={places}
                          onSelect={setValue}
                          onDetails={setDetailsOf}
                        />
                      );
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
