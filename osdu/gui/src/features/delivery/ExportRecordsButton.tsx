import { useState } from "react";
import { FileSpreadsheet, Loader2 } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { downloadBlob } from "@/lib/download";
import { failureText } from "./answers";

/**
 * Downloads the records a page lists as an Excel workbook: a working list of what to fix where the records come from.
 * The control plane writes the file (the same one `sqlflow records export` writes), so a page only names what it lists.
 * A button that cannot export says why on hover, which the disabled button itself would not show.
 */
export function ExportRecordsButton({ fetchWorkbook, fileName, unavailable, title, testId }: {
  /** The workbook of what the page lists, as the control plane serves it. */
  fetchWorkbook: () => Promise<Blob>;
  /** The name the download is saved under. */
  fileName: string;
  /** Why nothing can be exported now, or null when the records can be. */
  unavailable: string | null;
  /** What the export writes, on hover. */
  title: string;
  testId: string;
}) {
  const [busy, setBusy] = useState(false);

  const exportRecords = async () => {
    setBusy(true);
    try {
      downloadBlob(fileName, await fetchWorkbook());
      toast.success(`Exported the records to ${fileName}`);
    } catch (error) {
      toast.error(`The records could not be exported: ${failureText(error)}`);
    } finally {
      setBusy(false);
    }
  };

  return (
    <span className="inline-flex sm:ml-auto" title={unavailable ?? title} data-testid={`${testId}-hint`}>
      <Button
        variant="outline"
        size="sm"
        className="h-8"
        disabled={busy || unavailable !== null}
        onClick={() => void exportRecords()}
        data-testid={testId}
      >
        {busy ? <Loader2 className="animate-spin" /> : <FileSpreadsheet />}
        {busy ? "Exporting" : "Export"}
      </Button>
    </span>
  );
}
