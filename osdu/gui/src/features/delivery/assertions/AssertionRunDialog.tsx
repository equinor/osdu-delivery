import { useState } from "react";
import { Download, ExternalLink, FileText, Loader2 } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { isApiError } from "@/api/client";
import { TriggerRunDialog } from "@/features/runs/TriggerRunDialog";
import type { AssertionReportFormat } from "../../../api/delivery";
import { downloadReport, downloadSetReport, openHtmlReport, openSetHtmlReport, selectionPayload } from "./assertionFormat";

/** A run of an assertion flow's tests to launch: the flow, and the tests and tags it runs (none runs them all). */
export interface AssertionLaunch {
  pipelineId: string;
  repoId: string;
  flowName: string;
  tests: readonly string[];
  tags: readonly string[];
  operation?: "test" | "plan";
}

/**
 * Launches a run of an assertion flow's tests through the platform's one trigger dialog, opened on the tests asked for:
 * the dialog shows them picked, and the operator can change the pick, the parameters and the operation before it runs.
 */
export function AssertionRunDialog({ launch, onClose }: { launch: AssertionLaunch | null; onClose: () => void }) {
  if (launch === null) {
    return null;
  }

  const payload = selectionPayload(launch.tests, launch.tags);
  return (
    <TriggerRunDialog
      open
      onClose={onClose}
      repoId={launch.repoId}
      flowName={launch.flowName}
      flowId={launch.pipelineId}
      flowKind="assertion"
      initialOperation={launch.operation ?? "test"}
      initialValues={null}
      initialPayload={Object.keys(payload).length > 0 ? payload : null}
    />
  );
}

const FORMATS: readonly { format: AssertionReportFormat; label: string; hint: string }[] = [
  { format: "html", label: "HTML", hint: "a page to read, print or send" },
  { format: "md", label: "Markdown", hint: "for a pull request or a wiki" },
  { format: "json", label: "JSON", hint: "every result, whole" },
  { format: "junit", label: "JUnit XML", hint: "for a CI dashboard" },
];

function failure(error: unknown): string {
  return isApiError(error) ? error.detail ?? error.title : error instanceof Error ? error.message : String(error);
}

/** A run's report, to open as a page of its own or to download in each format the control plane renders it in. */
export function ReportDownloads({ assertionRunId, flowName, partition, size = "sm", testId = "report-downloads" }: {
  assertionRunId: number;
  flowName: string;
  partition: string | null;
  size?: "sm" | "icon-sm";
  testId?: string;
}) {
  return (
    <ReportMenu
      onOpen={() => openHtmlReport(assertionRunId)}
      onDownload={(format) => downloadReport(flowName, partition, assertionRunId, format)}
      size={size}
      testId={testId}
    />
  );
}

/**
 * A set's report, to open as a page of its own (the page to present: the verdict, the figures, the flows at a glance and
 * what needs attention) or to download in each format the control plane renders it in.
 */
export function SetReportDownloads({ set, partition, size = "sm", testId = "set-report-downloads" }: {
  set: string;
  partition: string | null;
  size?: "sm" | "icon-sm";
  testId?: string;
}) {
  return (
    <ReportMenu
      onOpen={() => openSetHtmlReport(set)}
      onDownload={(format) => downloadSetReport(set, partition, format)}
      size={size}
      testId={testId}
    />
  );
}

function ReportMenu({ onOpen, onDownload, size, testId }: {
  onOpen: () => Promise<void>;
  onDownload: (format: AssertionReportFormat) => Promise<void>;
  size: "sm" | "icon-sm";
  testId: string;
}) {
  const [busy, setBusy] = useState(false);
  const run = async (work: () => Promise<void>) => {
    setBusy(true);
    try {
      await work();
    } catch (error) {
      toast.error(`The report could not be fetched: ${failure(error)}`);
    } finally {
      setBusy(false);
    }
  };

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="outline" size={size} disabled={busy} aria-label="The report" data-testid={testId}>
          {busy ? <Loader2 className="animate-spin" /> : <FileText />}
          {size === "sm" && "Report"}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="min-w-56">
        <DropdownMenuItem onSelect={() => void run(onOpen)} data-testid={`${testId}-open`}>
          <ExternalLink />
          Open as a page
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuLabel className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Download</DropdownMenuLabel>
        {FORMATS.map((entry) => (
          <DropdownMenuItem
            key={entry.format}
            onSelect={() => void run(() => onDownload(entry.format))}
            data-testid={`${testId}-${entry.format}`}
          >
            <Download />
            <span className="flex flex-col">
              <span>{entry.label}</span>
              <span className="text-[11px] text-muted-foreground">{entry.hint}</span>
            </span>
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
