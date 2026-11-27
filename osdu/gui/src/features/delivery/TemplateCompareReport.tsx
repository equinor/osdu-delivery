import { useState } from "react";
import { ClipboardCopy, Download, FileText, Loader2 } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { downloadFileName, downloadText } from "@/lib/download";
import { deliveryApi, type TemplateMoveReportFormat } from "../../api/delivery";
import { problemText } from "./problemText";
import { copyPending, reportAbout, reportFileParts, type ReportSide } from "./templateMoveReport";

const DOWNLOADS: readonly { format: TemplateMoveReportFormat; label: string; hint: string; type: string }[] = [
  { format: "md", label: "Markdown", hint: "to read, share or keep", type: "text/markdown" },
  { format: "json", label: "JSON", hint: "for a script or a tool", type: "application/json" },
];

/**
 * The report on moving from one version of the kind to the other, copied as Markdown or saved as a file: what breaks, what
 * each synced mapping of the kind has to change, and what a mapping may now use. The control plane writes it, so it reads the
 * same however it is taken.
 */
export function TemplateCompareReport({ from, to, disabled }: { from: ReportSide; to: ReportSide; disabled: boolean }) {
  const [busy, setBusy] = useState(false);
  const read = (format: TemplateMoveReportFormat) => deliveryApi.osduCompareReport(from, to, format);

  const copy = async () => {
    setBusy(true);
    const text = read("md");
    try {
      await copyPending(text);
      toast.success("The report is on the clipboard, as Markdown.");
    } catch {
      // Told apart here: a report the control plane could not make, or a clipboard the browser did not open.
      const failed = await text.then(() => null, (error: unknown) => error);
      toast.error(failed === null
        ? "The browser did not let the report onto the clipboard. Download it instead."
        : `The report could not be made: ${problemText(failed)}`);
    } finally {
      setBusy(false);
    }
  };

  const save = async (format: TemplateMoveReportFormat, type: string) => {
    setBusy(true);
    try {
      downloadText(downloadFileName(reportFileParts(from, to), format), await read(format), type);
    } catch (error) {
      toast.error(`The report could not be made: ${problemText(error)}`);
    } finally {
      setBusy(false);
    }
  };

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="outline" size="sm" className="h-8" disabled={disabled || busy} data-testid="templates-compare-report">
          {busy ? <Loader2 className="animate-spin" /> : <FileText />}
          Report
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-72">
        <DropdownMenuLabel className="text-xs font-normal leading-snug text-muted-foreground" data-testid="templates-compare-report-about">
          {reportAbout(from, to)}
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        <DropdownMenuItem onSelect={() => void copy()} data-testid="templates-compare-report-copy">
          <ClipboardCopy />
          <span className="flex flex-col">
            <span>Copy as Markdown</span>
            <span className="text-[11px] text-muted-foreground">to paste into a ticket, a pull request or a wiki</span>
          </span>
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuLabel className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Download</DropdownMenuLabel>
        {DOWNLOADS.map((entry) => (
          <DropdownMenuItem key={entry.format} onSelect={() => void save(entry.format, entry.type)} data-testid={`templates-compare-report-${entry.format}`}>
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
