// The OSDU record inspector's header: the version picker, and the location bar with its crumbs from the record in view
// to the branch shown and the way back to the record a linked one was opened from.

import type { ReactNode } from "react";
import { ArrowLeft, ChevronDown, CircleCheck, ChevronRight, History, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuLabel, DropdownMenuRadioGroup, DropdownMenuRadioItem, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { cn } from "@/lib/utils";
import type { DeliveryOsduRead } from "../../api/delivery";
import { idParts } from "./osduRecordModel";
import { type EarlierRecord, namedByType } from "./osduRecordInspectorShared";

/**
 * The record's history as one control: the version in view, with every version OSDU keeps of the record a pick away,
 * newest first, the latest and the one this flow's ledger holds as delivered marked. A version older than the latest
 * turns the control amber, so it is plain which record is on screen. A target that keeps no version list says so.
 */
export function VersionPicker({ read, shown, ledgerVersion, loading, onPick }: {
  read: DeliveryOsduRead;
  /** The version in view, which a pick replaces. */
  shown: number | null;
  ledgerVersion: number | null;
  /** Whether a picked version is still being read. */
  loading: boolean;
  onPick?: (version: number) => void;
}) {
  const versions = read.versions ?? null;
  if (versions === null) {
    return read.historyError
      ? <span className="text-[11px] text-muted-foreground" title={read.historyError} data-testid="osdu-history-error">version list unavailable</span>
      : <span className="text-[11px] text-muted-foreground" data-testid="osdu-no-history">no version list</span>;
  }

  const latest = versions[0] ?? null;
  const older = shown !== null && latest !== null && shown !== latest;
  const marksOf = (version: number) => [version === latest ? "latest" : null, version === ledgerVersion ? "delivered by this flow" : null].filter((mark): mark is string => mark !== null);
  return (
    <span className="inline-flex items-center gap-2" data-testid="osdu-record-versions">
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button
            variant="outline"
            size="sm"
            className={cn("h-7 gap-1.5 font-normal", older && "border-warning/60 text-warning")}
            disabled={onPick === undefined || versions.length === 0}
            title={`${versions.length} version${versions.length === 1 ? "" : "s"} in OSDU; pick one to see the record as it was then`}
            data-testid="osdu-record-version"
            data-state={shown !== null ? "active" : undefined}
          >
            {loading ? <Loader2 className="animate-spin" /> : <History />}
            {shown === null
              ? <span className="text-muted-foreground">{versions.length === 0 ? "No versions" : "Version"}</span>
              : <span className="font-mono text-[11px] tabular-nums">{shown}</span>}
            {shown !== null && !older && shown === latest && <span className="text-[10px] uppercase tracking-wide text-muted-foreground">latest</span>}
            {shown !== null && shown === ledgerVersion && <CircleCheck className="size-3.5 text-success" aria-label="delivered by this flow" />}
            {older && <span className="text-[10px] uppercase tracking-wide" data-testid="osdu-version-older">older version</span>}
            <ChevronDown className="size-3.5 opacity-60" />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="max-h-80 overflow-y-auto" data-testid="osdu-version-menu">
          <DropdownMenuLabel className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">{`${versions.length} version${versions.length === 1 ? "" : "s"} in OSDU, newest first`}</DropdownMenuLabel>
          <DropdownMenuSeparator />
          <DropdownMenuRadioGroup value={shown === null ? "" : String(shown)} onValueChange={(value) => onPick?.(Number(value))}>
            {versions.map((version) => (
              <DropdownMenuRadioItem key={version} value={String(version)} className="gap-2 font-mono text-[12px] tabular-nums" data-testid="osdu-version">
                {version}
                {marksOf(version).map((mark) => <span key={mark} className={cn("font-sans text-[10px] uppercase tracking-wide", mark === "latest" ? "text-muted-foreground" : "text-success")}>{mark}</span>)}
              </DropdownMenuRadioItem>
            ))}
          </DropdownMenuRadioGroup>
        </DropdownMenuContent>
      </DropdownMenu>
      {ledgerVersion !== null && versions.length > 0 && !versions.includes(ledgerVersion) && (
        <span className="text-[11px] text-warning" title={`The ledger holds version ${ledgerVersion} as delivered by this flow, and OSDU no longer lists it.`} data-testid="osdu-version-missing">delivered version gone</span>
      )}
    </span>
  );
}

function CrumbSeparator() {
  return <ChevronRight className="size-3.5 shrink-0 text-muted-foreground/50" aria-hidden="true" />;
}

/**
 * One step of the location, kept on one line with the separator that leads to it, so a path longer than the bar wraps
 * between steps and a wrapped line opens on its separator. A step wider than the whole bar clips rather than spills.
 */
export function CrumbStep({ first = false, children }: { first?: boolean; children: ReactNode }) {
  return (
    <span className="inline-flex min-w-0 max-w-full items-center gap-x-1">
      {!first && <CrumbSeparator />}
      {children}
    </span>
  );
}

/** One key of the path as a crumb: the current step in full weight, an earlier one a step back to it. */
export function Crumb({ label, mono = true, current = false, onClick, title }: { label: ReactNode; mono?: boolean; current?: boolean; onClick?: () => void; title?: string }) {
  const face = cn("min-w-0 truncate", mono && "font-mono");
  return current || onClick === undefined
    ? <span className={cn(face, current ? "font-medium text-foreground" : "text-muted-foreground")} title={title} aria-current={current ? "location" : undefined}>{label}</span>
    : <button type="button" className={cn(face, "cursor-pointer rounded-sm text-muted-foreground hover:text-foreground hover:underline")} onClick={onClick} title={title}>{label}</button>;
}

/**
 * The way back while a linked record is open: the record it was opened from, named as a crumb names it, and a click
 * returns to that record as it was left. Where in it the link stood is on hover, so the location names only the record
 * in view and the path inside it.
 */
function BackLink({ back, onBack }: { back: EarlierRecord; onBack: (level: number) => void }) {
  const parts = idParts(back.id);
  const label = parts.type === "" ? parts.unique : namedByType(back.id, back.level) ? parts.type : `${parts.type} ${parts.unique}`;
  return (
    <button
      type="button"
      className="inline-flex min-w-0 max-w-[40%] shrink-0 cursor-pointer items-center gap-1 rounded-sm text-[12px] leading-5 text-muted-foreground hover:text-foreground"
      onClick={() => onBack(back.level)}
      title={back.from === null ? `Back to ${back.id}` : `Back to ${back.id}, where ${back.from} names this record`}
      data-testid="osdu-linked-close"
    >
      <ArrowLeft className="size-3.5 shrink-0" />
      <span className="min-w-0 truncate">{label}</span>
    </button>
  );
}

/**
 * The inspector's header, which says where the reader is and holds what acts on it. The location has a row of its own
 * across the whole width: the way back while a linked record is open, then the record in view and the path inside it
 * to the branch shown, wrapping onto a further line rather than clipping when it is longer than the row. Below it, the
 * view on the left (Fields or JSON, the version) and the page's own controls over the read on the right. Nothing below
 * the header repeats the record's name or its place.
 */
export function LocationBar({ back, onBack, location, view, controls }: {
  /** The record this one was opened from; null for the page's own record. */
  back: EarlierRecord | null;
  onBack: (level: number) => void;
  /** The record in view and the path inside it, each a `CrumbStep`. */
  location: ReactNode;
  /** How the record is shown: the view and the version in it. */
  view?: ReactNode;
  /** The page's own controls over the read. */
  controls?: ReactNode;
}) {
  return (
    <div className="flex flex-col border-b">
      <div className={cn("flex min-h-10 items-start gap-x-2 py-2 pr-2", back === null ? "pl-3" : "pl-2")}>
        {back !== null && (
          <>
            <BackLink back={back} onBack={onBack} />
            <span className="mt-0.5 h-4 w-px shrink-0 bg-border" aria-hidden="true" />
          </>
        )}
        <nav className="flex min-w-0 flex-1 flex-wrap items-center gap-x-1 gap-y-1 text-[12px] leading-5" aria-label="Location" data-testid="osdu-trail">
          {location}
        </nav>
      </div>
      {(view !== undefined || controls !== undefined) && (
        <div className="flex flex-wrap items-center gap-1.5 px-2 pb-2">
          {view}
          {controls !== undefined && <div className="ml-auto flex flex-wrap items-center justify-end gap-1.5">{controls}</div>}
        </div>
      )}
    </div>
  );
}
