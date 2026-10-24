import { useState } from "react";
import { useQueries } from "@tanstack/react-query";
import { ArrowLeftRight } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { explorerApi } from "../../../../api/explorer";
import type { DuplicateCopy, DuplicateGroup } from "../../../../api/inventories";
import { RecordName } from "../../RecordName";
import { DocumentCompare, type ComparedSide } from "../../VersionCompare";
import { withoutOsduFields } from "../../osduDocument";
import { idParts } from "../../osduRecordModel";
import { roleVisual } from "./duplicateModel";

/** Two copies compared without what always differs between two records: their ids, and the fields OSDU stamps on every version. */
function comparable(record: Record<string, unknown>): Record<string, unknown> {
  const rest = { ...withoutOsduFields(record) };
  delete rest.id;
  return rest;
}

/** A copy as its side of the comparison names it: its role in the group, and the unique part of its id. */
function sideLabel(copy: DuplicateCopy): string {
  const unique = idParts(copy.record.targetId).unique;
  return `${roleVisual(copy.role).label}: ${unique === "" ? copy.record.targetId : unique}`;
}

/** One copy's heading over its side: its role glyph, its name, and how many values it holds. */
function SideHeading({ copy, side }: { copy: DuplicateCopy; side: "left" | "right" }) {
  const visual = roleVisual(copy.role);
  const Icon = visual.icon;
  return (
    <div className="flex min-w-0 items-center gap-1.5 text-[12.5px]" data-testid={`duplicate-compare-${side}`}>
      <Icon className={`size-3.5 shrink-0 ${visual.tone}`} aria-hidden />
      <span className="shrink-0 font-medium">{visual.label}</span>
      <RecordName id={copy.record.targetId} kind={copy.record.kind} copy className="min-w-0 text-[12px]" />
      {copy.attributes !== undefined && (
        <span className="shrink-0 font-mono text-[11.5px] tabular-nums text-muted-foreground">{`${copy.attributes.toLocaleString("en-US")} values`}</span>
      )}
    </div>
  );
}

/**
 * Two copies of a group of duplicates compared, as OSDU holds them now (read live, through the partition's connection, as the
 * explorer reads a record): what one holds that the other does not, side by side with the unchanged stretches folded, and every
 * value that differs with its path. The left copy is the one kept when the pair holds it. Their ids, and the fields OSDU stamps
 * on every version, are left out, since they always differ. A list compares item by item in the order each holds it.
 */
export function DuplicateCompareDialog({ open, onOpenChange, partition, group, pair }: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  partition: string;
  group: DuplicateGroup;
  /** The two copies compared, in the order they were picked. */
  pair: readonly [DuplicateCopy, DuplicateCopy];
}) {
  // The copy kept goes left, as the earlier side reads: what the right holds beyond it is what it adds.
  const ordered: [DuplicateCopy, DuplicateCopy] = pair[1].role === "keep" && pair[0].role !== "keep" ? [pair[1], pair[0]] : [pair[0], pair[1]];
  const [swapped, setSwapped] = useState(false);
  const [left, right] = swapped ? [ordered[1], ordered[0]] : ordered;
  const reads = useQueries({
    queries: [left, right].map((copy) => ({
      queryKey: ["explorer", "read", partition, copy.record.targetId, null],
      queryFn: () => explorerApi.read(partition, copy.record.targetId),
      enabled: open,
      staleTime: 60_000,
      retry: false,
    })),
  });

  const sideOf = (copy: DuplicateCopy, index: number): ComparedSide => {
    const read = reads[index];
    const label = sideLabel(copy);
    if (read === undefined || read.isPending) {
      return { label, record: null, loading: true, error: null };
    }

    if (read.isError) {
      return { label, record: null, loading: false, error: read.error instanceof Error ? read.error.message : String(read.error) };
    }

    return read.data.found && read.data.record
      ? { label, record: read.data.record, loading: false, error: null }
      : { label, record: null, loading: false, error: `OSDU holds no record under ${copy.record.targetId} now.` };
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent
        className="flex max-h-[92vh] flex-col gap-3 overflow-hidden sm:max-w-6xl"
        onOpenAutoFocus={(event) => { event.preventDefault(); (event.currentTarget as HTMLElement).focus(); }}
        data-testid="duplicate-compare-dialog"
      >
        <DialogHeader>
          <DialogTitle>{`Compare two copies of group ${group.groupNumber}`}</DialogTitle>
          <DialogDescription>
            What each copy holds as OSDU serves it now. Their ids, and the fields OSDU stamps on every version, are left out.
          </DialogDescription>
        </DialogHeader>
        <div className="flex min-w-0 items-center gap-2">
          <div className="min-w-0 flex-1"><SideHeading copy={left} side="left" /></div>
          <Button
            variant="ghost"
            size="icon"
            className="size-8 shrink-0"
            onClick={() => setSwapped((was) => !was)}
            aria-label="Swap the two copies"
            title="Swap the two copies"
            data-testid="duplicate-compare-swap"
          >
            <ArrowLeftRight />
          </Button>
          <div className="flex min-w-0 flex-1 justify-end"><SideHeading copy={right} side="right" /></div>
        </div>
        <div className="min-h-0 flex-1 overflow-y-auto">
          {open && (
            <DocumentCompare
              left={sideOf(left, 0)}
              right={sideOf(right, 1)}
              prepare={comparable}
              testId="duplicate-compare"
              partsId="duplicate-compare"
            />
          )}
        </div>
      </DialogContent>
    </Dialog>
  );
}
