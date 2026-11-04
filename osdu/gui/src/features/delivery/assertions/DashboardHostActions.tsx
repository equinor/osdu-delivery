import { useState } from "react";
import { Link } from "react-router-dom";
import { Download, FileText, Loader2, Play } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { isApiError } from "@/api/client";
import { useAuth } from "@/auth/AuthContext";
import type { DeliveryAssertionFlow } from "../../../api/delivery";
import { StagedRowsSqlDialog } from "./AssertionSourceSql";
import { downloadSetReport, readResults, type SetReportFormat } from "./assertionFormat";
import { stagedRowsOf } from "./assertionReading";
import type { DashView } from "./dashboard/AssertionDashboard";
import type { CheckOutcome, DashData, DashTest } from "./dashboard/dashboardModel";
import { downloadDashboard } from "./dashboard/exportDashboard";

// What the workbench adds to the assertion dashboard: the downloads, an assertion's run, report and flow, and the SQL of the
// staged rows a check read. A downloaded dashboard has none of them.

function failure(error: unknown): string {
  return isApiError(error) ? error.detail ?? error.title : error instanceof Error ? error.message : String(error);
}

const SET_FORMATS: readonly { format: SetReportFormat; label: string; hint: string }[] = [
  { format: "md", label: "Markdown", hint: "for a pull request or a wiki" },
  { format: "json", label: "JSON", hint: "every result, whole" },
  { format: "junit", label: "JUnit XML", hint: "for a CI dashboard" },
];

/**
 * The dashboard's downloads: the dashboard itself as one page that works offline, as it is being read (the set picked,
 * disabled assertions in or out), and with a set picked the set's report as Markdown, JSON or JUnit XML.
 */
export function DashboardDownloads({ data, view }: { data: DashData; view: DashView }) {
  const [busy, setBusy] = useState(false);
  const run = async (what: string, work: () => Promise<void>) => {
    setBusy(true);
    try {
      await work();
    } catch (error) {
      toast.error(`The ${what} could not be downloaded: ${failure(error)}`);
    } finally {
      setBusy(false);
    }
  };

  const set = view.set;
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="outline" size="sm" disabled={busy} data-testid="dashboard-download">
          {busy ? <Loader2 className="animate-spin" /> : <Download />}
          Download
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="min-w-60">
        <DropdownMenuItem
          onSelect={() => void run("dashboard", () => downloadDashboard({ data, set, includeDisabled: view.includeDisabled, loadResults: readResults }))}
          data-testid="dashboard-download-page"
        >
          <FileText />
          <span className="flex flex-col">
            <span>Dashboard</span>
            <span className="text-[11px] text-muted-foreground">this page as one file, to present or send</span>
          </span>
        </DropdownMenuItem>
        {typeof set === "string" && (
          <>
            <DropdownMenuSeparator />
            <DropdownMenuLabel className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">The set&apos;s report</DropdownMenuLabel>
            {SET_FORMATS.map((entry) => (
              <DropdownMenuItem
                key={entry.format}
                onSelect={() => void run("report", () => downloadSetReport(set, data.partition, entry.format))}
                data-testid={`dashboard-download-${entry.format}`}
              >
                <Download />
                <span className="flex flex-col">
                  <span>{entry.label}</span>
                  <span className="text-[11px] text-muted-foreground">{entry.hint}</span>
                </span>
              </DropdownMenuItem>
            ))}
          </>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/**
 * An open check's action in the workbench, under its failing records: the staged rows it read, as T-SQL over its delivery
 * flow's tables, for a staged delivered assertion. A check that reads OSDU alone has none, and nor does a download, which
 * cannot ask the control plane to write it; writing it reads the flow's tables, so it is offered to those who may operate.
 */
export function DashboardCheckActions({ test, check }: { test: DashTest; check: CheckOutcome }) {
  const [open, setOpen] = useState(false);
  const { hasScope } = useAuth();
  const rows = stagedRowsOf(check);
  if (rows === null || test.reportId === null || !hasScope("operate")) {
    return null;
  }

  return (
    <>
      <button type="button" className="adb-link" onClick={() => setOpen(true)} data-testid="dashboard-check-sql">
        Show the rows in SQL
      </button>
      {open && <StagedRowsSqlDialog source={{ assertionRunId: test.reportId, test: test.name }} rows={rows} onClose={() => setOpen(false)} />}
    </>
  );
}

/**
 * An open assertion's actions in the workbench: run it again (not while its document disables it, nor while its flow
 * cannot run here), the report its result is from, and its flow.
 */
export function DashboardTestActions({ test, flow, onRun, showFlow = false }: {
  test: DashTest;
  flow: DeliveryAssertionFlow | undefined;
  onRun: (flow: DeliveryAssertionFlow, test: DashTest) => void;
  showFlow?: boolean;
}) {
  const runnable = flow !== undefined && flow.testsPartition && flow.problem === null;
  return (
    <>
      {test.reportId !== null && (
        <Button variant="ghost" size="xs" asChild>
          <Link to={`/delivery/assertions/runs/${test.reportId}`} data-testid="dashboard-test-report">Report #{test.reportId}</Link>
        </Button>
      )}
      {showFlow && (
        <Button variant="ghost" size="xs" asChild>
          <Link to={`/pipelines/${test.pipelineId}?tab=tests`} data-testid="dashboard-test-flow">Open the flow</Link>
        </Button>
      )}
      {test.standing !== "disabled" && (
        <Button
          variant="outline"
          size="xs"
          disabled={!runnable}
          title={runnable ? undefined : flow?.problem ?? "Its flow does not check this partition."}
          onClick={() => flow !== undefined && onRun(flow, test)}
          data-testid="dashboard-test-run"
        >
          <Play />
          Run again
        </Button>
      )}
    </>
  );
}
