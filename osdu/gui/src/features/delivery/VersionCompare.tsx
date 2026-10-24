import { useMemo, useState } from "react";
import { useQueries } from "@tanstack/react-query";
import { ArrowLeftRight, CircleCheck, Loader2 } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { DataTable, type Column } from "@/components/DataTable";
import { DiffView } from "@/components/DiffView";
import { TruncatedText } from "@/components/TruncatedText";
import type { DeliveryOsduRead } from "../../api/delivery";
import { ChangeCount, ChangeGlyph } from "./ChangeMark";
import { canonicalText, differences, shortValue, withoutOsduFields, type JsonDifference } from "./osduDocument";
import { RecordName } from "./RecordName";

/** How one leaf moved between the two versions, from the earlier side's point of view. */
type Moved = "added" | "changed" | "removed";

/** A leaf's move, from the difference the two documents show: present in the later one alone, in the earlier one alone, or changed. */
function moved(difference: JsonDifference): Moved {
  return difference.kind === "onlyInOsdu" ? "added" : difference.kind === "onlyInPreview" ? "removed" : "changed";
}

/** The versions a record keeps, as a picker lists them: newest first, the latest and the delivered one marked. */
function VersionSelect({ value, versions, latest, ledgerVersion, onChange, label, testId }: {
  value: number;
  versions: number[];
  latest: number | null;
  ledgerVersion: number | null;
  onChange: (version: number) => void;
  label: string;
  testId: string;
}) {
  return (
    <Select value={String(value)} onValueChange={(picked) => onChange(Number(picked))}>
      <SelectTrigger className="h-8 w-auto min-w-48 gap-2 font-mono text-[12px] tabular-nums" aria-label={label} data-testid={testId}>
        <SelectValue />
      </SelectTrigger>
      <SelectContent className="max-h-80">
        {versions.map((version) => (
          <SelectItem key={version} value={String(version)} className="font-mono text-[12px] tabular-nums" data-testid={`${testId}-option`}>
            {version}
            {version === latest && <span className="ml-2 font-sans text-[10px] uppercase tracking-wide text-muted-foreground">latest</span>}
            {version === ledgerVersion && <CircleCheck className="ml-1 inline size-3.5 text-success" aria-label="delivered by this flow" />}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}

/** One side of a comparison: what it is called, and its document once read, or why it could not be. */
export interface ComparedSide {
  label: string;
  record: Record<string, unknown> | null;
  loading: boolean;
  error: string | null;
}

/**
 * Two documents compared, the left as the earlier: what moved is counted (added, changed, removed, from the left's point of
 * view), the two stand side by side with their unchanged stretches folded, and every moved value is listed with its path and
 * both values. `prepare` takes out of each document what always differs between the two and so says nothing (the fields OSDU
 * stamps on every version; for two records, their ids too). `same` is what the counts say when the two sides are one.
 */
export function DocumentCompare({ left, right, prepare, same, testId = "osdu-version-compare", partsId = "osdu-version" }: {
  left: ComparedSide;
  right: ComparedSide;
  prepare: (record: Record<string, unknown>) => Record<string, unknown>;
  /** What the counts say when the two sides are the same document; null when they never are. */
  same?: string | null;
  /** The test id of the whole, its counts and its problem. */
  testId?: string;
  /** The test id the side-by-side documents and the list of differences are named after. */
  partsId?: string;
}) {
  const compared = useMemo(() => {
    if (left.record === null || right.record === null) {
      return null;
    }

    const before = prepare(left.record);
    const after = prepare(right.record);
    return { ...differences(after, before), original: canonicalText(before), modified: canonicalText(after) };
  }, [left.record, right.record, prepare]);

  const counts = compared === null
    ? null
    : (["added", "changed", "removed"] as Moved[]).map((kind) => ({ kind, count: compared.items.filter((item) => moved(item) === kind).length }));
  const columns: Column<JsonDifference>[] = [
    {
      id: "path",
      header: "Path",
      render: (row) => (
        <span className="inline-flex min-w-0 items-center gap-1.5">
          <ChangeGlyph change={moved(row)} />
          <span className="font-mono text-[12px] break-all">{row.path || "(the record)"}</span>
        </span>
      ),
    },
    { id: "from", header: left.label, fill: true, render: (row) => <TruncatedText text={row.preview === undefined ? null : shortValue(row.preview)} mono maxWidth={360} /> },
    { id: "to", header: right.label, fill: true, render: (row) => <TruncatedText text={row.osdu === undefined ? null : shortValue(row.osdu)} mono maxWidth={360} /> },
  ];
  const problem = left.error ?? right.error;

  return (
    <div className="flex min-h-0 flex-col gap-3" data-testid={testId}>
      <div className="flex flex-wrap items-center justify-end gap-3" data-testid={`${testId}-counts`}>
        {(left.loading || right.loading) && (
          <span className="inline-flex items-center gap-1.5 text-[12px] text-muted-foreground">
            <Loader2 className="size-3.5 animate-spin" />
            Reading the documents
          </span>
        )}
        {counts !== null && (same !== undefined && same !== null
          ? <span className="text-[12px] text-muted-foreground">{same}</span>
          : compared!.items.length === 0
            ? <span className="text-[12px] text-muted-foreground">Nothing differs between the two</span>
            : counts.map(({ kind, count }) => <ChangeCount key={kind} change={kind} count={count} className={count === 0 ? "opacity-50" : undefined} />))}
        {compared?.truncated && <span className="text-[12px] text-muted-foreground">{`the first ${compared.items.length} differences`}</span>}
      </div>
      {problem !== null && (
        <Alert variant="destructive" data-testid={`${testId}-error`}>
          <AlertTitle>A document could not be read</AlertTitle>
          <AlertDescription className="whitespace-pre-wrap">{problem}</AlertDescription>
        </Alert>
      )}
      {compared !== null && (
        <>
          <DiffView
            original={compared.original}
            modified={compared.modified}
            language="json"
            height={420}
            foldUnchanged
            sideLabels={{ original: left.label, modified: right.label }}
            data-testid={`${partsId}-diff`}
          />
          {compared.items.length > 0 && (
            <DataTable columns={columns} rows={compared.items} rowKey={(row) => `${row.kind}|${row.path}`} emptyMessage="No differences." data-testid={`${partsId}-differences`} />
          )}
        </>
      )}
      {compared === null && problem === null && <div className="h-[420px] animate-pulse rounded-md bg-muted/40" aria-hidden />}
    </div>
  );
}

/**
 * Two versions of one record compared: any two of those OSDU keeps, picked on either side, the earlier on the left by
 * default, as {@link DocumentCompare} compares two documents. A version is read once and kept: a version of a record
 * never changes, so swapping back and forth reads nothing again. The fields OSDU stamps on every version (its number, who
 * wrote it when) are left out, since they always differ.
 */
export function VersionCompare({ id, versions, latestVersion, latestRecord, ledgerVersion, readVersion, from, to }: {
  id: string;
  /** Every version OSDU keeps of the record, newest first. */
  versions: number[];
  latestVersion: number | null;
  latestRecord: Record<string, unknown>;
  ledgerVersion: number | null;
  readVersion: (version: number) => Promise<DeliveryOsduRead>;
  /** The versions compared when the comparison opens. */
  from: number;
  to: number;
}) {
  const [left, setLeft] = useState(from);
  const [right, setRight] = useState(to);
  const asked = [...new Set([left, right])].filter((version) => version !== latestVersion);
  const reads = useQueries({
    queries: asked.map((version) => ({
      queryKey: ["osdu-record-version", id, version],
      queryFn: () => readVersion(version),
      // A version of a record is what it was written as, for as long as OSDU keeps it.
      staleTime: Number.POSITIVE_INFINITY,
      retry: false,
    })),
  });

  const sideOf = (version: number): ComparedSide => {
    const label = `Version ${version}`;
    if (version === latestVersion) {
      return { label, record: latestRecord, loading: false, error: null };
    }

    const read = reads[asked.indexOf(version)];
    if (read === undefined || read.isPending) {
      return { label, record: null, loading: true, error: null };
    }

    if (read.isError) {
      return { label, record: null, loading: false, error: read.error instanceof Error ? read.error.message : String(read.error) };
    }

    return read.data.found && read.data.record
      ? { label, record: read.data.record, loading: false, error: null }
      : { label, record: null, loading: false, error: `OSDU keeps no version ${version} of this record.` };
  };

  return (
    <div className="flex min-h-0 flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <VersionSelect value={left} versions={versions} latest={latestVersion} ledgerVersion={ledgerVersion} onChange={setLeft} label="The version on the left" testId="osdu-compare-from" />
        <Button
          variant="ghost"
          size="icon"
          className="size-8"
          onClick={() => { setLeft(right); setRight(left); }}
          aria-label="Swap the two versions"
          title="Swap the two versions"
          data-testid="osdu-compare-swap"
        >
          <ArrowLeftRight />
        </Button>
        <VersionSelect value={right} versions={versions} latest={latestVersion} ledgerVersion={ledgerVersion} onChange={setRight} label="The version on the right" testId="osdu-compare-to" />
      </div>
      <DocumentCompare
        left={sideOf(left)}
        right={sideOf(right)}
        prepare={withoutOsduFields}
        same={left === right ? "The same version on both sides" : null}
      />
    </div>
  );
}

/** The comparison in a dialog of its own, over the record's location. */
export function VersionCompareDialog({ open, onOpenChange, read, kind, ...compare }: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  read: DeliveryOsduRead;
  kind: string | null;
} & Omit<Parameters<typeof VersionCompare>[0], "id">) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent
        className="flex max-h-[92vh] flex-col gap-3 overflow-hidden sm:max-w-6xl"
        // Focus lands on the dialog itself rather than its first button, whose tooltip would otherwise open with it.
        onOpenAutoFocus={(event) => { event.preventDefault(); (event.currentTarget as HTMLElement).focus(); }}
        data-testid="osdu-version-compare-dialog"
      >
        <DialogHeader>
          <DialogTitle>Compare versions</DialogTitle>
          <DialogDescription asChild>
            <div className="flex min-w-0 items-center gap-1 text-[12px]">
              <RecordName id={read.targetId} kind={kind} copy />
            </div>
          </DialogDescription>
        </DialogHeader>
        <div className="min-h-0 flex-1 overflow-y-auto">
          {open && <VersionCompare id={read.targetId} {...compare} />}
        </div>
      </DialogContent>
    </Dialog>
  );
}
