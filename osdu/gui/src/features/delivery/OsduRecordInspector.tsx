import { useCallback, useLayoutEffect, useMemo, useState, type ReactNode } from "react";
import { useMutation } from "@tanstack/react-query";
import {
  ArrowLeft, ChevronDown, CircleCheck, ChevronRight, ChevronsDownUp, ChevronsUpDown, Download, Eye, GitCompare, Globe, History,
  Loader2, Scale, SearchX, UserRoundCog, type LucideIcon,
} from "lucide-react";
import { VersionCompareDialog } from "./VersionCompare";
import { toast } from "sonner";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuLabel, DropdownMenuRadioGroup, DropdownMenuRadioItem, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from "@/components/ui/resizable";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { cn } from "@/lib/utils";
import { CopyButton } from "@/components/CopyButton";
import { DataTable, type Column } from "@/components/DataTable";
import { EmptyState } from "@/components/EmptyState";
import { IconAction } from "@/components/IconAction";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import type { DeliveryOsduRead } from "../../api/delivery";
import { downloadJson, fileNameOf, withoutVersion } from "./osduDocument";
import {
  branchPaths, buildModel, CONTENT_SECTION, describeBranch, documentNode, idParts, isMintedUnique, isReferenceNode, loadLayout, matching, recordNameOf, saveLayout, trail,
  type RecordModel, type RecordNode,
} from "./osduRecordModel";
import { RecordName } from "./RecordName";
import { failureText } from "./answers";
import { ProblemView, TaskProgress } from "./TemplateSheet";

/** How many rows of one branch show before the rest wait behind a button, so a curve list of thousands stays usable. */
const PAGE = 100;

/** The most columns a table of items shows, however wide it is; the rest of an item's fields are a click into the item away. */
const TABLE_COLUMNS = 8;

/** The width a field column of a table of items asks for, so a narrower table shows fewer fields instead of scrolling sideways. */
const TABLE_COLUMN_WIDTH = 170;

/** The width a table of items gives its item number, and its count of the columns it leaves out. */
const TABLE_INDEX_WIDTH = 48;
const TABLE_MORE_WIDTH = 80;

/** What a field's name takes in a header: a generous width per character of the header's face, and the cell's padding. */
const TABLE_HEADER_CHARACTER = 7;
const TABLE_CELL_PADDING = 24;

/** The border around a table of items, which its columns do not get to use. */
const TABLE_BORDER = 2;

/** The views the detail pane has that are not a block of the record: the whole document, its system fields, its access and legal, its links. */
const DOCUMENT = "document";
const RECORD = "record";
const ACCESS = "access";
const LINKS = "links";
const MENTIONS = "mentions";
const VALIDATION = "validation";

const VIEW_NAMES: Record<string, string> = {
  [DOCUMENT]: "Full document",
  [RECORD]: "System fields",
  [ACCESS]: "Access & legal",
  [LINKS]: "Linked records",
  [MENTIONS]: "Mentioned by",
  [VALIDATION]: "Validation",
};

/** How a branch of the record is shown: one level as fields (or a table of items), or the whole branch as JSON. */
type ViewMode = "fields" | "json";

/** One record open in the inspector: what its read answered, as it stands, and where in the record before it it was found. */
export interface InspectorEntry {
  id: string;
  /** What the read answered; undefined until it has. */
  read: DeliveryOsduRead | undefined;
  /** Why the read answered nothing: the control plane's problem, or OSDU's, with its words. */
  error?: unknown;
  /** Whether the read is under way. */
  pending: boolean;
  /** The path, in the record before it on the trail, of the value that named this record; absent for the first record. */
  from?: string | null;
}

/** The record a linked one was opened from, as the way back names it: which record, where in it the link stood, and its place. */
interface EarlierRecord {
  id: string;
  from: string | null;
  level: number;
}

/**
 * How a record opens another: the id, and the path of the value in this record that names it; null for a record opened
 * from somewhere else (one that mentions this record).
 */
type OpenLink = (id: string, from: string | null) => void;

/** One record of the trail as a page acting on values is given it: its id, what it holds as shown, and the value that named it. */
export interface InspectorTrailRecord {
  id: string;
  /** The record as the inspector shows it; null while its read has not answered. */
  record: Record<string, unknown> | null;
  /** The path, in the record before it on the trail, of the value that named it; null for the first, or one opened from its mentions. */
  from: string | null;
}

/** A value of a record on the trail, as a page that acts on values is given it. */
export interface InspectorField {
  /** The record's place on the trail: 0 for the record the page opened. */
  level: number;
  /** The value: its path in the record (`data.GeoContexts[1].GeoPoliticalEntityID`) and what it holds. */
  node: RecordNode;
  /** Every record on the trail up to this one, this one last, each as shown. */
  trail: InspectorTrailRecord[];
}

/** What a page adds to the inspector around the records it shows. */
export interface InspectorExtras {
  /** Where the first record sits, as crumb steps in front of its name (the explorer's partition, group and type). */
  place?: ReactNode;
  /**
   * The records that mention one, as a view under the outline's References: given the record's id, and a way to open one of
   * them on the trail after it.
   */
  mentions?: (id: string, open: (id: string) => void) => ReactNode;
  /** What to offer where OSDU holds no record under an id: the records whose ids are near it. */
  notFound?: (id: string) => ReactNode;
  /**
   * The record checked against its schema, as a view under the outline's Checks: given the record's id, the version in view
   * (null for its latest), and a way to open an element of the record by its path.
   */
  validation?: (id: string, version: number | null, open: (path: string) => void) => ReactNode;
  /**
   * What a page shows beside each value and each section of a record on the trail, in the fields and the JSON views (the
   * query of an element, the dimension builder's picks); a section is a node of kind `object` or `array`.
   */
  fieldActions?: (field: InspectorField) => ReactNode;
}

function text(value: unknown): string | null {
  return typeof value === "string" ? value : typeof value === "number" ? String(value) : null;
}

function texts(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : [];
}

/** Text with every occurrence of the search term marked, so a match shows where it is rather than only that it is. */
export function Highlight({ text: value, term }: { text: string; term: string }) {
  if (term === "") {
    return <>{value}</>;
  }

  const parts: ReactNode[] = [];
  const lower = value.toLowerCase();
  let from = 0;
  for (let at = lower.indexOf(term, from); at >= 0; at = lower.indexOf(term, from)) {
    if (at > from) {
      parts.push(value.slice(from, at));
    }

    parts.push(<mark key={at} className="rounded-sm bg-warning/40 text-inherit">{value.slice(at, at + term.length)}</mark>);
    from = at + term.length;
  }

  if (from < value.length) {
    parts.push(value.slice(from));
  }

  return <>{parts}</>;
}

/** A list of short values as chips, each carrying the glyph and hover title that say what it is; nothing when there are none. */
function Chips({ values, icon: Icon, what, testId }: { values: string[]; icon: LucideIcon; what: string; testId?: string }) {
  if (values.length === 0) {
    return null;
  }

  return (
    <span className="inline-flex flex-wrap gap-1" data-testid={testId}>
      {values.map((value) => (
        <Badge key={value} variant="outline" className="gap-1 font-mono text-[11px] font-normal" title={what}>
          <Icon className="size-3 text-muted-foreground" />
          {value}
        </Badge>
      ))}
    </span>
  );
}

/**
 * The record's history as one control: the version in view, with every version OSDU keeps of the record a pick away,
 * newest first, the latest and the one this flow's ledger holds as delivered marked. A version older than the latest
 * turns the control amber, so it is plain which record is on screen. A target that keeps no version list says so.
 */
function VersionPicker({ read, shown, ledgerVersion, loading, onPick }: {
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

/**
 * A value that names another OSDU record: the name itself is the link, and a click opens that record here, read
 * through the same flow's route, after this one on the trail. The copy beside it hands over the id verbatim; in a list
 * of records (`quietCopy`) it shows on the row's hover, as the explorer's grids show theirs.
 */
function ReferenceLink({ value, path, ownId, onOpenLink, opening, term, className, withoutType = false, quietCopy = false }: {
  value: string; path: string; ownId: string | null; onOpenLink?: OpenLink; opening?: string | null; term: string; className?: string;
  /** Names the record by its unique part alone, where a heading names its type. */
  withoutType?: boolean;
  /** Shows the copy on the hover of the row (`group/link`) rather than always. */
  quietCopy?: boolean;
}) {
  const id = withoutVersion(value);
  const self = ownId !== null && id === withoutVersion(ownId);
  const matched = term !== "" && value.toLowerCase().includes(term);
  const name = matched
    ? <span className="min-w-0 break-all font-mono text-[12px]"><Highlight text={value} term={term} /></span>
    : <RecordName id={value} withoutType={withoutType} className="text-[12px]" />;
  return (
    <span className={cn("inline-flex min-w-0 max-w-full items-center gap-1", className)} data-testid="osdu-record-link" data-value={value}>
      {onOpenLink !== undefined && !self
        ? (
          <button
            type="button"
            className="inline-flex min-w-0 max-w-full cursor-pointer items-center gap-1 rounded-sm text-primary underline-offset-2 hover:underline focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:cursor-wait"
            onClick={(event) => { event.stopPropagation(); onOpenLink(id, path); }}
            disabled={opening === id}
            title="Open this record here"
            data-testid="osdu-link-read"
          >
            {opening === id && <Loader2 className="size-3.5 shrink-0 animate-spin" />}
            {name}
          </button>
        )
        : <span className="inline-flex min-w-0 max-w-full items-center gap-1 text-muted-foreground">{name}{self && <span className="shrink-0 text-[11px]">(this record)</span>}</span>}
      <span className={cn("shrink-0", quietCopy && "opacity-0 transition-opacity group-hover/link:opacity-100 focus-within:opacity-100")}>
        <CopyButton iconOnly label="Copy the id" text={value} testId="copy-osdu-link" />
      </span>
    </span>
  );
}

/**
 * A leaf as it reads: a reference as a link, text as text, numbers and booleans tinted, the search term marked. A compact
 * leaf, in a table cell whose width it clips to, leaves long text without its copy.
 */
function Leaf({ node, term, ownId, onOpenLink, opening, compact = false }: {
  node: RecordNode; term: string; ownId: string | null; onOpenLink?: OpenLink; opening?: string | null; compact?: boolean;
}) {
  const value = node.value;
  if (isReferenceNode(node)) {
    return <ReferenceLink value={node.value} path={node.path} ownId={ownId} onOpenLink={onOpenLink} opening={opening} term={term} />;
  }

  const matched = term !== "" && node.valueText.includes(term);
  if (typeof value === "string") {
    return matched
      ? <span className="min-w-0 break-all text-[12px]"><Highlight text={value} term={term} /></span>
      : <TruncatedText text={value} maxWidth={640} copy={!compact && value.length > 40} className="text-[12px]" />;
  }

  if (typeof value === "number") {
    return <span className="font-mono text-[12px] tabular-nums text-info"><Highlight text={String(value)} term={term} /></span>;
  }

  if (typeof value === "boolean") {
    return <span className="font-mono text-[12px] text-warning"><Highlight text={String(value)} term={term} /></span>;
  }

  return <span className="font-mono text-[12px] text-muted-foreground"><Highlight text="null" term={term} /></span>;
}

/** One row of the outline: a branch of the record, or one of the views that are not a branch. */
function OutlineRow({ depth, selected, open, onToggle, onSelect, label, mono = false, detail, hits, testId }: {
  depth: number; selected: boolean; open?: boolean; onToggle?: () => void; onSelect: () => void;
  label: ReactNode; /** A label that is one of the record's own keys, set in the mono face as keys are everywhere else. */ mono?: boolean;
  detail?: string; hits?: number; testId?: string;
}) {
  return (
    <div
      className={cn("group flex min-w-0 items-center gap-1 rounded-md py-1 pr-2 text-[12px] hover:bg-accent/50", selected && "bg-accent text-accent-foreground")}
      style={{ paddingLeft: 6 + depth * 14 }}
      data-testid={testId}
      data-state={selected ? "active" : undefined}
    >
      {onToggle !== undefined
        ? (
          <button type="button" className="flex size-4 shrink-0 items-center justify-center text-muted-foreground hover:text-foreground" onClick={(event) => { event.stopPropagation(); onToggle(); }} aria-expanded={open} aria-label={open ? "Fold" : "Unfold"}>
            {open ? <ChevronDown className="size-3.5" /> : <ChevronRight className="size-3.5" />}
          </button>
        )
        : <span className="size-4 shrink-0" aria-hidden="true" />}
      <button type="button" className="flex min-w-0 flex-1 items-center gap-1.5 text-left" onClick={onSelect}>
        <span className={cn("truncate", mono && "font-mono")}>{label}</span>
        {detail !== undefined && <span className="shrink-0 text-[11px] text-muted-foreground">{detail}</span>}
        {hits !== undefined && hits > 0 && <span className="ml-auto shrink-0 rounded-full bg-warning/25 px-1.5 text-[10px] font-medium tabular-nums">{hits}</span>}
      </button>
    </div>
  );
}

/** A caption over a group of outline rows, in the workbench's own navigation voice. */
function OutlineGroup({ label }: { label: string }) {
  return <div className="px-2 pt-2 pb-0.5 text-[10px] font-medium uppercase tracking-wider text-muted-foreground">{label}</div>;
}

function CrumbSeparator() {
  return <ChevronRight className="size-3.5 shrink-0 text-muted-foreground/50" aria-hidden="true" />;
}

/**
 * One step of the location, kept on one line with the separator that leads to it, so a path longer than the bar wraps
 * between steps and a wrapped line opens on its separator. A step wider than the whole bar clips rather than spills.
 */
function CrumbStep({ first = false, children }: { first?: boolean; children: ReactNode }) {
  return (
    <span className="inline-flex min-w-0 max-w-full items-center gap-x-1">
      {!first && <CrumbSeparator />}
      {children}
    </span>
  );
}

/** One key of the path as a crumb: the current step in full weight, an earlier one a step back to it. */
function Crumb({ label, mono = true, current = false, onClick, title }: { label: ReactNode; mono?: boolean; current?: boolean; onClick?: () => void; title?: string }) {
  const face = cn("min-w-0 truncate", mono && "font-mono");
  return current || onClick === undefined
    ? <span className={cn(face, current ? "font-medium text-foreground" : "text-muted-foreground")} title={title} aria-current={current ? "location" : undefined}>{label}</span>
    : <button type="button" className={cn(face, "cursor-pointer rounded-sm text-muted-foreground hover:text-foreground hover:underline")} onClick={onClick} title={title}>{label}</button>;
}

/**
 * Whether a crumb names a record by its type alone. The first record is the page's own, which the page already names
 * above the inspector, and an id minted by a machine says nothing a reader knows; either would only take the path's
 * room. A linked record whose id reads as a name (a wellbore's, a reference value's) keeps it, as nothing else names it.
 */
function namedByType(id: string, level: number): boolean {
  return level === 0 || isMintedUnique(idParts(id).unique);
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
function LocationBar({ back, onBack, location, view, controls }: {
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

/** Rows of caption and value, the way the two envelope views read. */
function CaptionRows({ rows, testId }: { rows: { label: string; value: ReactNode }[]; testId: string }) {
  return (
    <div className="flex flex-col" data-testid={testId}>
      {rows.map((row) => (
        <div key={row.label} className="flex min-w-0 items-baseline gap-3 border-b px-3 py-1.5 last:border-b-0">
          <span className="w-32 shrink-0 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">{row.label}</span>
          <span className="min-w-0 flex-1">{row.value ?? <span className="text-muted-foreground">-</span>}</span>
        </div>
      ))}
    </div>
  );
}

/** The record's system fields, as OSDU keeps them on every record: what it is, which version, who wrote it and when; and what the read itself was. */
function RecordView({ read, record }: { read: DeliveryOsduRead; record: Record<string, unknown> }) {
  return (
    <CaptionRows
      testId="osdu-record-fields"
      rows={[
        { label: "Id", value: <TruncatedText text={read.targetId} mono maxWidth={560} copy title="OSDU id" /> },
        { label: "Kind", value: <TruncatedText text={text(record.kind)} mono maxWidth={560} copy title="Kind" /> },
        { label: "Version", value: <span className="font-mono text-[12px] tabular-nums">{text(record.version) ?? "-"}</span> },
        {
          label: "Created",
          value: <span className="text-[12px]"><RelativeTime value={text(record.createTime)} absolute />{text(record.createUser) && <span className="text-muted-foreground">{` by ${text(record.createUser)}`}</span>}</span>,
        },
        {
          label: "Last modified",
          value: <span className="text-[12px]"><RelativeTime value={text(record.modifyTime)} absolute />{text(record.modifyUser) && <span className="text-muted-foreground">{` by ${text(record.modifyUser)}`}</span>}</span>,
        },
        { label: "Read", value: <span className="text-[12px]"><RelativeTime value={read.readUtc} absolute />{` through ${read.flow}`}</span> },
        { label: "Correlation id", value: <TruncatedText text={read.correlationId ?? null} mono maxWidth={360} copy={Boolean(read.correlationId)} /> },
      ]}
    />
  );
}

/** The record's access and legal, as OSDU's `acl` and `legal` blocks hold them: who may see and own it, and under which legal tags. */
function AccessView({ record }: { record: Record<string, unknown> }) {
  const acl = (record.acl ?? {}) as Record<string, unknown>;
  const legal = (record.legal ?? {}) as Record<string, unknown>;
  return (
    <CaptionRows
      testId="osdu-access"
      rows={[
        { label: "Viewers", value: <Chips values={texts(acl.viewers)} icon={Eye} what="viewer group" testId="osdu-record-viewers" /> },
        { label: "Owners", value: <Chips values={texts(acl.owners)} icon={UserRoundCog} what="owner group" testId="osdu-record-owners" /> },
        { label: "Legal tags", value: <Chips values={texts(legal.legaltags)} icon={Scale} what="legal tag" testId="osdu-record-legal" /> },
        { label: "Countries", value: <Chips values={texts(legal.otherRelevantDataCountries)} icon={Globe} what="relevant country" /> },
        { label: "Legal status", value: <span className="text-[12px]">{text(legal.status) ?? "-"}</span> },
      ]}
    />
  );
}

/** The paths a linked record is named at that its row lists before **more**; the others are one click away. */
const LINK_PATHS_SHOWN = 2;

/** The records a record links to, by their type (`WellLogSamplingDomainType`), the types in name order, each with its records. */
function linksByType(references: RecordModel["references"]): { type: string; references: RecordModel["references"] }[] {
  const byType = new Map<string, RecordModel["references"]>();
  for (const reference of references) {
    const type = idParts(reference.id).type || "Other";
    byType.set(type, [...(byType.get(type) ?? []), reference]);
  }

  return [...byType.entries()]
    .sort(([a], [b]) => a.localeCompare(b, "en", { sensitivity: "base" }))
    .map(([type, held]) => ({ type, references: [...held].sort((x, y) => idParts(x.id).unique.localeCompare(idParts(y.id).unique, "en", { numeric: true })) }));
}

/**
 * The records this one refers to, under a heading per type with how many there are, each opened by its name (its unique
 * part: the heading names the type), the copy on the row's hover. Beside it, in a column of its own share that never
 * crowds the name, the paths that name it, each a step to that place: the first two, and the others behind **more**, so a
 * unit named by every curve of a log takes one row, not a column of paths.
 */
function LinksView({ model, ownId, onOpenLink, opening, onSelect }: { model: RecordModel; ownId: string | null; onOpenLink?: OpenLink; opening?: string | null; onSelect: (path: string) => void }) {
  const [unfolded, setUnfolded] = useState<ReadonlySet<string>>(() => new Set());
  const groups = useMemo(() => linksByType(model.references), [model.references]);
  if (model.references.length === 0) {
    return <EmptyState title="No linked records" description="No value of this record names another record." />;
  }

  const toggle = (id: string) => setUnfolded((was) => {
    const next = new Set(was);
    if (next.has(id)) {
      next.delete(id);
    } else {
      next.add(id);
    }

    return next;
  });

  return (
    <div className="flex flex-col" data-testid="osdu-links">
      {groups.map((group) => (
        <section key={group.type} aria-label={group.type} data-testid="osdu-links-type">
          <div className="flex items-baseline gap-2 border-b bg-muted/40 px-3 py-1 text-[11px] text-muted-foreground">
            <span className="min-w-0 truncate font-medium text-foreground/80" title={group.type}>{group.type}</span>
            <span className="shrink-0 tabular-nums">{group.references.length}</span>
          </div>
          {group.references.map((reference) => {
            const open = unfolded.has(reference.id);
            const paths = open ? reference.paths : reference.paths.slice(0, LINK_PATHS_SHOWN);
            const more = reference.paths.length - LINK_PATHS_SHOWN;
            return (
              <div
                key={reference.id}
                className="group/link grid grid-cols-[minmax(0,1fr)_minmax(0,38%)] items-start gap-3 border-b px-3 py-1 last:border-b-0 hover:bg-accent/30"
                data-testid="osdu-link"
              >
                <ReferenceLink
                  value={reference.id}
                  path={reference.paths[0]}
                  ownId={ownId}
                  onOpenLink={onOpenLink}
                  opening={opening}
                  term=""
                  withoutType
                  quietCopy
                  className="min-w-0"
                />
                {/* Each path a line as tall as the name's, so the first sits beside the name it belongs to. */}
                <span className="flex min-w-0 flex-col items-end text-[11px] leading-6 text-muted-foreground">
                  {paths.map((path) => {
                    const holder = model.byPath.get(path)?.parent ?? "";
                    return (
                      <button
                        key={path}
                        type="button"
                        className="max-w-full cursor-pointer truncate font-mono hover:text-foreground hover:underline"
                        onClick={() => onSelect(holder === "" ? path : holder)}
                        title={`${path}\nGo to where the record names it`}
                        data-testid="osdu-link-path"
                      >
                        {path}
                      </button>
                    );
                  })}
                  {more > 0 && (
                    <button
                      type="button"
                      className="cursor-pointer rounded-sm px-1 text-[11px] leading-5 text-muted-foreground hover:bg-accent hover:text-foreground"
                      onClick={() => toggle(reference.id)}
                      aria-expanded={open}
                      data-testid="osdu-link-more"
                    >
                      {open ? "fewer" : `+${more} more`}
                    </button>
                  )}
                </span>
              </div>
            );
          })}
        </section>
      ))}
    </div>
  );
}

/** A branch as rows: each field or item with its value, a nested branch as a step into it. */
function FieldRows({ node, term, hits, ownId, onOpenLink, opening, onSelect, shown, onShowMore, actions }: {
  node: RecordNode; term: string; hits: Set<string>; ownId: string | null; onOpenLink?: OpenLink; opening?: string | null;
  onSelect: (path: string) => void; shown: number; onShowMore: () => void;
  /** What a page shows beside a value or a section, at the end of its row. */
  actions?: (node: RecordNode) => ReactNode;
}) {
  return (
    <div className="flex flex-col">
      {node.children.slice(0, shown).map((child) => (
        <div key={child.path} className={cn("flex min-w-0 items-baseline gap-3 border-b px-3 py-1.5 last:border-b-0", hits.has(child.path) && "bg-warning/10")}>
          {/* The name takes its share of a narrow panel (a dimension's builder docked beside it) and no more, so the value keeps room. */}
          <span className="w-[40%] min-w-24 max-w-56 shrink-0 truncate font-mono text-[12px] text-muted-foreground">
            <Highlight text={node.kind === "array" ? `[${child.key}]` : child.key} term={term} />
          </span>
          <span className="min-w-0 flex-1">
            {child.kind === "leaf"
              ? <Leaf node={child} term={term} ownId={ownId} onOpenLink={onOpenLink} opening={opening} />
              : (
                <button type="button" className="inline-flex cursor-pointer items-center gap-1 text-[12px] text-primary hover:underline" onClick={() => onSelect(child.path)} data-testid="osdu-drill">
                  {describeBranch(child)}
                  <ChevronRight className="size-3.5" />
                </button>
              )}
          </span>
          {actions !== undefined && <span className="shrink-0 self-center">{actions(child)}</span>}
        </div>
      ))}
      {shown < node.children.length && (
        <div className="px-3 py-2">
          <Button variant="outline" size="sm" className="h-7" onClick={onShowMore} data-testid="osdu-show-more">{`Show the other ${node.children.length - shown}`}</Button>
        </div>
      )}
    </div>
  );
}

/** The keys the items of a table share, the most common first: the order its columns take as its width allows. */
function tableColumns(items: RecordNode[]): string[] {
  const counts = new Map<string, number>();
  for (const item of items) {
    for (const child of item.children) {
      counts.set(child.key, (counts.get(child.key) ?? 0) + 1);
    }
  }

  return [...counts.entries()].sort((a, b) => b[1] - a[1]).map(([key]) => key);
}

/** A field column of a table of items: the field it shows and the width it is given, padding included. */
interface ItemColumn {
  key: string;
  width: number;
}

/**
 * The field columns that fit a table of items this wide, taken in order, at most {@link TABLE_COLUMNS}. A column asks
 * for its share or for its name, whichever is wider, since a header does not clip the way a value does; what the
 * columns that fit leave over is split between them. The first column is always shown, in whatever room there is.
 */
function columnsThatFit(keys: string[], width: number): ItemColumn[] {
  let room = width - TABLE_BORDER - TABLE_INDEX_WIDTH - TABLE_MORE_WIDTH;
  const fitting: ItemColumn[] = [];
  for (const key of keys.slice(0, TABLE_COLUMNS)) {
    const ask = Math.max(TABLE_COLUMN_WIDTH, key.length * TABLE_HEADER_CHARACTER + TABLE_CELL_PADDING);
    if (fitting.length > 0 && ask > room) {
      break;
    }

    const given = fitting.length === 0 ? Math.min(ask, Math.max(room, TABLE_COLUMN_WIDTH / 2)) : ask;
    fitting.push({ key, width: given });
    room -= given;
  }

  const spare = fitting.length === 0 ? 0 : Math.max(0, Math.floor(room / fitting.length));
  return fitting.map((column) => ({ key: column.key, width: column.width + spare }));
}

/**
 * The width an element has, followed as its panel is resized; null until it is laid out. Measured before the browser
 * paints, so what depends on it never shows at a width it does not have.
 */
function useWidth(): [(node: HTMLDivElement | null) => void, number | null] {
  const [node, setNode] = useState<HTMLDivElement | null>(null);
  const [width, setWidth] = useState<number | null>(null);
  useLayoutEffect(() => {
    if (node === null) {
      return;
    }

    const measure = () => setWidth(node.clientWidth);
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(node);
    return () => observer.disconnect();
  }, [node]);

  return [setNode, width];
}

/**
 * An array of objects as a table: one row per item, each row a step into its item, and one column per shared field,
 * the first as many as the pane has room for. Each column is given its width and clips its values to it, so the table
 * never scrolls sideways; the columns it leaves out are counted in the last header, and a row opens its whole item.
 */
function ItemTable({ node, term, hits, ownId, onOpenLink, opening, onSelect, shown, onShowMore, actions }: {
  node: RecordNode; term: string; hits: Set<string>; ownId: string | null; onOpenLink?: OpenLink; opening?: string | null;
  onSelect: (path: string) => void; shown: number; onShowMore: () => void;
  /** What a page shows beside an item, at the end of its row. */
  actions?: (node: RecordNode) => ReactNode;
}) {
  const shared = useMemo(() => tableColumns(node.children), [node]);
  const [measured, width] = useWidth();
  const fields = columnsThatFit(shared, width ?? 0);
  const hidden = shared.length - fields.length;
  const columns: Column<RecordNode>[] = [
    { id: "#", header: "#", width: TABLE_INDEX_WIDTH, align: "right", render: (item) => <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{item.key}</span> },
    ...fields.map(({ key, width: given }): Column<RecordNode> => ({
      id: key,
      header: key,
      width: given,
      render: (item) => {
        const field = item.children.find((child) => child.key === key);
        // The cell's content is held to the column's width, so a long value clips inside it instead of widening the table.
        return (
          <div className="overflow-hidden" style={{ width: given - TABLE_CELL_PADDING }}>
            {field === undefined
              ? <span className="text-muted-foreground">-</span>
              : field.kind === "leaf"
                ? <Leaf node={field} term={term} ownId={ownId} onOpenLink={onOpenLink} opening={opening} compact />
                : <span className="text-[11px] text-muted-foreground">{describeBranch(field)}</span>}
          </div>
        );
      },
    })),
    {
      id: "more",
      header: hidden > 0 ? `+${hidden} more` : "",
      width: TABLE_MORE_WIDTH,
      align: "right",
      render: (item) => (
        <span className="flex items-center justify-end gap-1">
          {actions?.(item)}
          <ChevronRight className="size-3.5 text-muted-foreground" aria-label="Open the item" />
        </span>
      ),
    },
  ];
  return (
    <div className="flex flex-col gap-2 p-2">
      <div ref={measured} className="min-w-0">
        <DataTable
          columns={columns}
          rows={node.children.slice(0, shown)}
          rowKey={(item) => item.path}
          onRowClick={(item) => onSelect(item.path)}
          rowSx={(item) => (hits.has(item.path) || item.children.some((child) => hits.has(child.path)) ? { backgroundColor: "color-mix(in oklab, var(--warning) 10%, transparent)" } : undefined)}
          emptyMessage="An empty list"
          data-testid="osdu-item-table"
        />
      </div>
      {shown < node.children.length && (
        <div>
          <Button variant="outline" size="sm" className="h-7" onClick={onShowMore} data-testid="osdu-show-more">{`Show the other ${node.children.length - shown}`}</Button>
        </div>
      )}
    </div>
  );
}

const PUNCTUATION = "text-muted-foreground";

/**
 * A branch of the record as JSON that can be worked with, not only read: every object and list folds at its caret, a
 * key that holds a branch steps into it (and the brace of a list's item into that item), and a value that names another
 * OSDU record opens it. Lists show their first hundred items and the rest on a click; the search term is marked.
 */
function JsonView({ node, term, hits, ownId, onOpenLink, opening, onSelect, actions }: {
  node: RecordNode; term: string; hits: Set<string>; ownId: string | null; onOpenLink?: OpenLink; opening?: string | null; onSelect: (path: string) => void;
  /** What a page shows beside a value or a section, after the line it starts on. */
  actions?: (node: RecordNode) => ReactNode;
}) {
  const [folded, setFolded] = useState<ReadonlySet<string>>(() => new Set());
  const [shownItems, setShownItems] = useState<ReadonlyMap<string, number>>(() => new Map());
  const fold = (path: string) => setFolded((current) => {
    const next = new Set(current);
    if (next.has(path)) {
      next.delete(path);
    } else {
      next.add(path);
    }

    return next;
  });

  const lines: ReactNode[] = [];
  const line = (key: string, depth: number, content: ReactNode, caret?: { open: boolean; path: string }, hit = false) => {
    lines.push(
      <div key={key} className={cn("flex min-w-0 items-start rounded-sm pr-2", hit && "bg-warning/10")} style={{ paddingLeft: depth * 16 }}>
        <span className="flex h-5 w-4 shrink-0 items-center justify-center">
          {caret !== undefined && (
            <button type="button" className="text-muted-foreground hover:text-foreground" onClick={() => fold(caret.path)} aria-label={caret.open ? "Fold" : "Unfold"} data-testid="osdu-json-fold">
              {caret.open ? <ChevronDown className="size-3.5" /> : <ChevronRight className="size-3.5" />}
            </button>
          )}
        </span>
        <span className="min-w-0 whitespace-pre-wrap break-all">{content}</span>
      </div>,
    );
  };

  const leafValue = (leaf: RecordNode): ReactNode => {
    const value = leaf.value;
    if (isReferenceNode(leaf)) {
      const id = withoutVersion(leaf.value);
      const self = ownId !== null && id === withoutVersion(ownId);
      return (
        <span>
          <span className="text-foreground">&quot;</span>
          {onOpenLink !== undefined && !self
            ? (
              <button type="button" className="cursor-pointer text-left text-primary underline-offset-2 hover:underline disabled:cursor-wait" onClick={() => onOpenLink(id, leaf.path)} disabled={opening === id} title="Open this record here" data-testid="osdu-json-link">
                <Highlight text={leaf.value} term={term} />
              </button>
            )
            : <span className="text-foreground"><Highlight text={leaf.value} term={term} /></span>}
          <span className="text-foreground">&quot;</span>
        </span>
      );
    }

    if (typeof value === "string") {
      return <span className="text-foreground">&quot;<Highlight text={value} term={term} />&quot;</span>;
    }

    if (typeof value === "number") {
      return <span className="tabular-nums text-info"><Highlight text={String(value)} term={term} /></span>;
    }

    if (typeof value === "boolean") {
      return <span className="text-warning"><Highlight text={String(value)} term={term} /></span>;
    }

    return <span className="text-muted-foreground">null</span>;
  };

  const walk = (current: RecordNode, depth: number, keyOf: RecordNode | null, last: boolean) => {
    const comma = last ? null : <span className={PUNCTUATION}>,</span>;
    const label = keyOf === null
      ? null
      : current.kind === "leaf"
        ? <span className="text-muted-foreground">&quot;<Highlight text={current.key} term={term} />&quot;<span className={PUNCTUATION}>: </span></span>
        : (
          <span>
            <button type="button" className="cursor-pointer text-muted-foreground hover:text-primary hover:underline" onClick={() => onSelect(current.path)} title={`Go to ${current.path}`} data-testid="osdu-json-key">
              &quot;<Highlight text={current.key} term={term} />&quot;
            </button>
            <span className={PUNCTUATION}>: </span>
          </span>
        );
    const hit = hits.has(current.path);
    if (current.kind === "leaf") {
      line(current.path, depth, <>{label}{leafValue(current)}{comma}{actions !== undefined && <span className="ml-1.5 inline-flex align-middle">{actions(current)}</span>}</>, undefined, hit);
      return;
    }

    const [open, close] = current.kind === "array" ? ["[", "]"] : ["{", "}"];
    // An item of a list has no key to step into it by, so its opening brace does that work.
    const opener = keyOf === null && depth > 0
      ? <button type="button" className={cn(PUNCTUATION, "cursor-pointer hover:text-primary")} onClick={() => onSelect(current.path)} title={`Go to ${current.path}`}>{open}</button>
      : <span className={PUNCTUATION}>{open}</span>;
    if (current.children.length === 0) {
      line(current.path, depth, <>{label}<span className={PUNCTUATION}>{open}{close}</span>{comma}</>, undefined, hit);
      return;
    }

    if (folded.has(current.path)) {
      line(
        current.path,
        depth,
        <>
          {label}{opener}
          <button type="button" className="mx-1 rounded bg-muted px-1 text-[11px] text-muted-foreground hover:text-foreground" onClick={() => fold(current.path)}>{describeBranch(current)}</button>
          <span className={PUNCTUATION}>{close}</span>{comma}
        </>,
        { open: false, path: current.path },
        hit,
      );
      return;
    }

    line(
      `${current.path}:open`,
      depth,
      <>{label}{opener}{actions !== undefined && depth > 0 && <span className="ml-1.5 inline-flex align-middle">{actions(current)}</span>}</>,
      { open: true, path: current.path },
      hit,
    );
    const limit = current.kind === "array" ? Math.min(current.children.length, shownItems.get(current.path) ?? PAGE) : current.children.length;
    current.children.slice(0, limit).forEach((child, index) => {
      walk(child, depth + 1, current.kind === "object" ? child : null, index === current.children.length - 1);
    });
    if (limit < current.children.length) {
      lines.push(
        <div key={`${current.path}:more`} style={{ paddingLeft: (depth + 1) * 16 + 16 }}>
          <button type="button" className="cursor-pointer text-[11px] text-primary hover:underline" onClick={() => setShownItems((was) => new Map(was).set(current.path, current.children.length))} data-testid="osdu-json-more">
            {`${current.children.length - limit} more items`}
          </button>
        </div>,
      );
    }

    line(`${current.path}:close`, depth, <><span className={PUNCTUATION}>{close}</span>{comma}</>);
  };

  walk(node, 0, null, true);
  return (
    <div className="relative p-2 font-mono text-[12px] leading-5" data-testid="osdu-json">
      <div className="sticky top-0 z-10 float-right">
        <CopyButton iconOnly label="Copy this branch as JSON" text={JSON.stringify(node.value, null, 2)} testId="copy-osdu-json" />
      </div>
      {lines}
    </div>
  );
}

/**
 * The outline row that stands for a path: the path itself when the outline shows it, otherwise the nearest branch
 * above it that does. The outline shows a record's objects but not the items of its lists, so an item opened from a
 * table keeps its list marked on the left.
 */
function outlineRowFor(model: RecordModel, path: string): string {
  let row = path;
  for (const crumb of trail(model, path)) {
    row = crumb.path;
    if (crumb.kind === "array") {
      break;
    }
  }

  return row;
}

/**
 * One record as an inspector: an outline of its branches on the left that never moves, and on the right one level of
 * it at a time, as fields or as JSON, so a record of ten thousand values is read the way a file tree is, never as one
 * tall page. Where the reader is lives in one place, the location bar: the record and the branch in view, with the way
 * back to the record it was opened from. A picked version replaces the record in place, with the outline and the place
 * in it kept.
 */
function RecordInspector({ read, level, back, ledgerVersion, onOpenLink, onBack, readVersion, opening, actions, place, mentions, validation, fieldActions }: {
  /** The read of the record at its latest. */
  read: DeliveryOsduRead & { record: Record<string, unknown> };
  level: number;
  /** The record this one was opened from; null for the page's own record. */
  back: EarlierRecord | null;
  ledgerVersion: number | null;
  onOpenLink?: OpenLink;
  onBack: (level: number) => void;
  /** Queues a read of this record at one of its versions; absent where it cannot be asked for. */
  readVersion?: (version: number) => Promise<DeliveryOsduRead>;
  opening?: string | null;
  /** The page's own controls over the read (read again, open in a window), kept on the location bar. */
  actions?: ReactNode;
  /** Where the record sits, as crumb steps in front of its name; none for a record the page itself names. */
  place?: ReactNode;
  /** The records that mention this one, given a way to open one of them on the trail; absent where a page cannot list them. */
  mentions?: (open: (id: string) => void) => ReactNode;
  /** The record in view checked against its schema, given its version (null for the latest) and a way to open an element; absent where a page cannot check it. */
  validation?: (version: number | null, open: (path: string) => void) => ReactNode;
  /** What a page shows beside each value and section of the record as shown (the version picked, or its latest). */
  fieldActions?: (node: RecordNode, record: Record<string, unknown>) => ReactNode;
}) {
  const [picked, setPicked] = useState<{ version: number; read: DeliveryOsduRead } | null>(null);
  const [compareOpen, setCompareOpen] = useState(false);
  const pick = useMutation({
    mutationFn: (version: number) => readVersion!(version),
    onSuccess: (answer, version) => setPicked({ version, read: answer }),
    onError: (error) => toast.error(failureText(error)),
  });
  const latestVersion = read.readVersion ?? read.version ?? null;
  const pickedRead = picked?.read ?? null;
  const pickedRecord = pickedRead?.found === true && pickedRead.record ? pickedRead.record : null;
  const pickedFailure = picked !== null && pickedRecord === null ? `The record has no version ${picked.version}` : null;
  const pickedLoading = pick.isPending;
  // The record in view: the picked version once it has arrived, the latest until then and when the pick is the latest.
  const showingPicked = pickedRecord !== null && picked !== null && picked.version !== latestVersion;
  const shownRead = showingPicked && pickedRead !== null ? pickedRead : read;
  const record = showingPicked ? pickedRecord : read.record;
  const shownVersion = showingPicked ? picked.version : latestVersion;

  const kind = text(record.kind);
  const model = useMemo(() => buildModel(record, read.targetId), [record, read.targetId]);
  const leafActions = fieldActions === undefined ? undefined : (node: RecordNode) => fieldActions(node, record);
  const whole = useMemo(() => documentNode(record), [record]);
  const [chosen, setChosen] = useState<string>(() => (model.sections.length > 0 ? model.sections[0].path : RECORD));
  const [mode, setMode] = useState<ViewMode>("fields");
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => loadLayout(kind));
  const [search, setSearch] = useState("");
  const [shownCounts, setShownCounts] = useState<ReadonlyMap<string, number>>(() => new Map());
  const [matchAt, setMatchAt] = useState(0);
  // A version may lack the branch chosen in another: the record's first section stands in, and the choice is kept.
  const selected = chosen in VIEW_NAMES || model.byPath.has(chosen) ? chosen : (model.sections[0]?.path ?? RECORD);
  const term = search.trim().toLowerCase();
  const found = useMemo(() => (term === "" ? null : matching(model, term)), [model, term]);
  const hits = found?.hits ?? new Set<string>();
  const hitList = useMemo(() => (found === null ? [] : [...found.hits]), [found]);

  const remember = useCallback((next: ReadonlySet<string>) => {
    setExpanded(next);
    saveLayout(kind, next);
  }, [kind]);
  const isOpen = (path: string) => expanded.has(path) || (found !== null && found.holding.has(path));
  const toggle = (path: string) => {
    const next = new Set(expanded);
    if (next.has(path)) {
      next.delete(path);
    } else {
      next.add(path);
    }

    remember(next);
  };
  const select = (path: string) => {
    setChosen(path);
    // Selecting a branch opens the way to it in the outline, so what is shown is always marked on the left.
    if (model.byPath.has(path)) {
      const next = new Set(expanded);
      for (const crumb of trail(model, path)) {
        if (crumb.kind === "object") {
          next.add(crumb.path);
        }
      }

      remember(next);
    }
  };
  // An element a check names: its branch opened as fields, the value's own branch for a value; the envelope's views for a
  // value of the record itself, its access and legal.
  const openPath = (path: string) => {
    const target = model.byPath.get(path);
    if (target === undefined || (target.kind === "leaf" && target.parent === "")) {
      select(path.startsWith("acl") || path.startsWith("legal") ? ACCESS : RECORD);
      return;
    }

    setMode("fields");
    select(target.kind === "leaf" ? target.parent : target.path);
  };
  const goFromDocument = (path: string) => {
    if (model.byPath.has(path)) {
      setMode("json");
      select(path);
    } else if (path.startsWith("acl") || path.startsWith("legal")) {
      select(ACCESS);
    }
  };
  const nextMatch = () => {
    if (hitList.length === 0) {
      return;
    }

    const at = matchAt % hitList.length;
    const hit = model.byPath.get(hitList[at]);
    if (hit !== undefined) {
      select(hit.kind === "leaf" ? (hit.parent === "" ? hit.path : hit.parent) : hit.path);
    }

    setMatchAt(at + 1);
  };
  const pickVersion = (version: number) => {
    if (version === latestVersion) {
      setPicked(null);
    } else if (picked?.version !== version) {
      pick.mutate(version);
    }
  };

  const node = model.byPath.get(selected);
  const marked = node === undefined ? selected : outlineRowFor(model, node.path);
  const renderOutline = (branch: RecordNode, depth: number): ReactNode => {
    if (branch.kind === "leaf") {
      return null;
    }

    // An array's items are the detail pane's to show, as a table or a list: the outline stays the record's shape
    // (its sections and the objects within them) rather than one row per curve.
    const branches = branch.kind === "array" ? [] : branch.children.filter((child) => child.kind !== "leaf");
    const holding = found?.holding.get(branch.path);
    if (found !== null && holding === undefined && marked !== branch.path) {
      return null;
    }

    const open = isOpen(branch.path);
    return (
      <div key={branch.path}>
        <OutlineRow
          depth={depth}
          selected={marked === branch.path}
          open={branches.length > 0 ? open : undefined}
          onToggle={branches.length > 0 ? () => toggle(branch.path) : undefined}
          onSelect={() => select(branch.path)}
          label={branch.key}
          mono
          detail={describeBranch(branch)}
          hits={holding}
          testId="osdu-outline-branch"
        />
        {open && branches.map((child) => renderOutline(child, depth + 1))}
      </div>
    );
  };

  const shown = node === undefined ? 0 : Math.min(node.children.length, shownCounts.get(node.path) ?? PAGE);
  const showMore = () => { if (node !== undefined) { setShownCounts((current) => new Map(current).set(node.path, node.children.length)); } };
  const isTable = node !== undefined && node.kind === "array" && node.children.length > 0 && node.children.every((child) => child.kind === "object");
  // The record's name stands for its content, so the content section is no step of its own.
  const crumbs = node === undefined ? [] : trail(model, node.path).filter((crumb) => crumb.path !== CONTENT_SECTION);
  const atContent = selected === CONTENT_SECTION;

  // The copy of the path rides on the last step, so a wrapped location never leaves it alone on a line.
  const copyPath = node === undefined ? null : <CopyButton iconOnly label="Copy the path" text={node.path} testId="copy-osdu-path" />;
  const location = (
    <>
      {place}
      <CrumbStep first={place === undefined}>
        <button
          type="button"
          className={cn("min-w-0 max-w-full cursor-pointer rounded-sm hover:underline", atContent ? "text-foreground" : "text-muted-foreground hover:text-foreground")}
          onClick={() => select(model.sections[0]?.path ?? RECORD)}
          title="The record's content"
          aria-current={atContent ? "location" : undefined}
          data-testid="osdu-crumb-record"
        >
          {place !== undefined
            ? <span className="font-medium" title={read.targetId} data-testid="osdu-record-name">{recordNameOf(record) ?? idParts(read.targetId).unique}</span>
            : <RecordName id={read.targetId} kind={kind} typeOnly={namedByType(read.targetId, level)} className="font-medium" />}
        </button>
      </CrumbStep>
      {node === undefined
        ? <CrumbStep><Crumb label={VIEW_NAMES[selected]} mono={false} current /></CrumbStep>
        : crumbs.map((crumb, index) => {
          const isItem = model.byPath.get(crumb.parent)?.kind === "array";
          const last = index === crumbs.length - 1;
          return (
            <CrumbStep key={crumb.path}>
              <Crumb label={isItem ? `[${crumb.key}]` : crumb.key} current={last} onClick={() => select(crumb.path)} />
              {last && copyPath}
            </CrumbStep>
          );
        })}
    </>
  );

  const view = (
    <>
      {node !== undefined && (
        <ToggleGroup type="single" value={mode} onValueChange={(value) => { if (value === "fields" || value === "json") { setMode(value); } }} variant="outline" size="sm" data-testid="osdu-view-mode">
          <ToggleGroupItem value="fields" className="h-7 px-2.5 text-xs" data-testid="osdu-mode-fields">Fields</ToggleGroupItem>
          <ToggleGroupItem value="json" className="h-7 px-2.5 text-xs" data-testid="osdu-tree-raw">JSON</ToggleGroupItem>
        </ToggleGroup>
      )}
      <VersionPicker read={read} shown={shownVersion} ledgerVersion={level === 0 ? ledgerVersion : null} loading={pickedLoading} onPick={readVersion === undefined ? undefined : pickVersion} />
      {readVersion !== undefined && (read.versions?.length ?? 0) >= 2 && (
        <Button
          variant="outline"
          size="sm"
          className="h-7"
          onClick={() => setCompareOpen(true)}
          title={showingPicked ? "What changed between this version and the latest" : "What changed between any two versions OSDU keeps"}
          data-testid="osdu-version-compare-toggle"
        >
          <GitCompare />
          Compare
        </Button>
      )}
      {pickedFailure !== null && <span className="max-w-[240px] truncate text-[11px] text-destructive" title={pickedFailure} data-testid="osdu-version-error">{pickedFailure}</span>}
    </>
  );

  const controls = (
    <>
      {actions}
      <IconAction label="Download the record as JSON" icon={<Download />} variant="ghost" className="size-7" onClick={() => downloadJson(fileNameOf("osdu", read.targetId, shownVersion === null ? null : String(shownVersion)), record)} data-testid="osdu-record-download" />
    </>
  );

  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="osdu-record">
      <LocationBar back={back} onBack={onBack} location={location} view={view} controls={controls} />
      <ResizablePanelGroup orientation="horizontal" className="min-h-0 flex-1">
        <ResizablePanel defaultSize={260} minSize={200} maxSize="45" className="flex min-h-0 flex-col">
          <div className="flex items-center gap-1 border-b p-2">
            <SearchInput value={search} onChange={(value) => { setSearch(value); setMatchAt(0); }} placeholder="Find in record" label="Find a field or value in the record" className="min-w-0 flex-1" testId="osdu-tree-search" />
          </div>
          {term !== "" && (
            <div className="flex items-center gap-2 border-b px-2 py-1 text-[11px] text-muted-foreground">
              <span data-testid="osdu-tree-hits">{`${hitList.length} match${hitList.length === 1 ? "" : "es"}`}</span>
              {hitList.length > 0 && <Button variant="ghost" size="sm" className="ml-auto h-6 px-2 text-[11px]" onClick={nextMatch} data-testid="osdu-next-match">Next match</Button>}
            </div>
          )}
          <div className="min-h-0 flex-1 overflow-y-auto p-1" data-testid="osdu-outline">
            <OutlineGroup label="Record" />
            <OutlineRow depth={0} selected={selected === DOCUMENT} onSelect={() => select(DOCUMENT)} label={VIEW_NAMES[DOCUMENT]} detail={`${whole.leaves} values`} testId="osdu-outline-document" />
            <OutlineRow depth={0} selected={selected === RECORD} onSelect={() => select(RECORD)} label={VIEW_NAMES[RECORD]} testId="osdu-outline-record" />
            <OutlineRow depth={0} selected={selected === ACCESS} onSelect={() => select(ACCESS)} label={VIEW_NAMES[ACCESS]} testId="osdu-outline-access" />
            <OutlineGroup label="Content" />
            {model.sections.map((section) => renderOutline(section, 0))}
            <OutlineGroup label="References" />
            <OutlineRow depth={0} selected={selected === LINKS} onSelect={() => select(LINKS)} label={VIEW_NAMES[LINKS]} detail={`${model.references.length}`} testId="osdu-outline-links" />
            {mentions !== undefined && <OutlineRow depth={0} selected={selected === MENTIONS} onSelect={() => select(MENTIONS)} label={VIEW_NAMES[MENTIONS]} testId="osdu-outline-mentions" />}
            {validation !== undefined && (
              <>
                <OutlineGroup label="Checks" />
                <OutlineRow depth={0} selected={selected === VALIDATION} onSelect={() => select(VALIDATION)} label={VIEW_NAMES[VALIDATION]} testId="osdu-outline-validation" />
              </>
            )}
          </div>
          <div className="flex items-center gap-1 border-t p-1">
            <IconAction label="Unfold every branch" icon={<ChevronsUpDown />} variant="ghost" className="size-7" onClick={() => remember(branchPaths(model.sections))} data-testid="osdu-tree-expand" />
            <IconAction label="Fold every branch" icon={<ChevronsDownUp />} variant="ghost" className="size-7" onClick={() => remember(new Set())} data-testid="osdu-tree-collapse" />
          </div>
        </ResizablePanel>
        <ResizableHandle />
        <ResizablePanel className="flex min-h-0 flex-col">
          {/* Keyed by what is shown, so every step opens at the top of what it shows rather than where the last view was scrolled. */}
          <div key={`${selected}:${mode}:${shownVersion ?? ""}`} className="min-h-0 flex-1 overflow-y-auto" data-testid="osdu-record-json">
            {selected === DOCUMENT
              ? <JsonView key={`document:${shownVersion ?? ""}`} node={whole} term={term} hits={hits} ownId={read.targetId} onOpenLink={onOpenLink} opening={opening} onSelect={goFromDocument} actions={leafActions} />
              : selected === RECORD
              ? <RecordView read={shownRead} record={record} />
              : selected === ACCESS
                ? <AccessView record={record} />
                : selected === LINKS
                  ? <LinksView model={model} ownId={read.targetId} onOpenLink={onOpenLink} opening={opening} onSelect={select} />
                  : selected === MENTIONS && mentions !== undefined
                  ? mentions((id) => onOpenLink?.(id, null))
                  : selected === VALIDATION && validation !== undefined
                  ? validation(showingPicked ? shownVersion : null, openPath)
                  : node === undefined
                    ? <EmptyState title="Nothing selected" description="Pick a branch of the record on the left." />
                    : mode === "json"
                      ? <JsonView key={node.path} node={node} term={term} hits={hits} ownId={read.targetId} onOpenLink={onOpenLink} opening={opening} onSelect={select} actions={leafActions} />
                      : node.children.length === 0
                        ? <EmptyState title={node.kind === "array" ? "An empty list" : "An empty object"} />
                        : isTable
                          ? <ItemTable node={node} term={term} hits={hits} ownId={read.targetId} onOpenLink={onOpenLink} opening={opening} onSelect={select} shown={shown} onShowMore={showMore} actions={leafActions} />
                          : <FieldRows node={node} term={term} hits={hits} ownId={read.targetId} onOpenLink={onOpenLink} opening={opening} onSelect={select} shown={shown} onShowMore={showMore} actions={leafActions} />}
          </div>
        </ResizablePanel>
      </ResizablePanelGroup>
      {readVersion !== undefined && read.versions && read.versions.length >= 2 && (
        <VersionCompareDialog
          open={compareOpen}
          onOpenChange={setCompareOpen}
          read={read}
          kind={kind}
          versions={read.versions}
          latestVersion={latestVersion}
          latestRecord={read.record}
          ledgerVersion={level === 0 ? ledgerVersion : null}
          readVersion={readVersion}
          // The version in view against the latest, or the latest against the one before it.
          from={showingPicked ? picked.version : latestVersion !== null && latestVersion !== read.versions[0] ? latestVersion : read.versions[1]}
          to={read.versions[0]}
        />
      )}
    </div>
  );
}

/**
 * The records open in the inspector, the first read from the page and each next one opened from a link in the one
 * before: a trail across records, not a stack of them. Every record on the trail stays as the reader left it, and
 * the last is shown; the way back on the location bar returns to the record before, as it was, and closes what was
 * opened after it.
 */
export function OsduRecordInspector({ entries, ledgerVersion, opening, onOpenLink, readVersionAt, onBack, actions, extras, fill = false }: {
  entries: InspectorEntry[];
  ledgerVersion?: number | null;
  opening?: string | null;
  /** Opens a record after the one at `level` on the trail: one a value of it names (from that value's path), or one that mentions it. */
  onOpenLink?: (level: number, id: string, from: string | null) => void;
  /** How the record at `level` is read at one of its versions; undefined where it cannot be asked for. */
  readVersionAt: (level: number) => ((version: number) => Promise<DeliveryOsduRead>) | undefined;
  /** Steps back to the entry at `level`, closing everything opened after it. */
  onBack: (level: number) => void;
  /** The page's own controls over the read, shown on the location bar. */
  actions?: ReactNode;
  /** What the page adds around the records: the first one's place, the records that mention one, what to offer for an id not found. */
  extras?: InspectorExtras;
  /**
   * Whether the inspector takes the height its flex column parent gives it (a window of its own) rather than a fixed
   * share of the viewport under a page's header (a tab).
   */
  fill?: boolean;
}) {
  const shownLevel = entries.length - 1;

  const body = (entry: InspectorEntry, level: number): { content: ReactNode; inspector: boolean } => {
    const back: EarlierRecord | null = level === 0 ? null : { id: entries[level - 1].id, from: entry.from ?? null, level: level - 1 };
    const read = entry.pending ? null : entry.read ?? null;
    if (read !== null && read.found && read.record) {
      return {
        inspector: true,
        content: (
          <RecordInspector
            read={{ ...read, record: read.record }}
            level={level}
            back={back}
            ledgerVersion={ledgerVersion ?? null}
            onOpenLink={onOpenLink === undefined ? undefined : (id, from) => onOpenLink(level, id, from)}
            onBack={onBack}
            readVersion={readVersionAt(level)}
            opening={opening}
            actions={actions}
            place={level === 0 ? extras?.place : undefined}
            mentions={extras?.mentions === undefined || onOpenLink === undefined ? undefined : (open) => extras.mentions!(read.targetId, open)}
            validation={extras?.validation === undefined ? undefined : (version, open) => extras.validation!(read.targetId, version, open)}
            fieldActions={extras?.fieldActions === undefined ? undefined : (leaf, shown) => extras.fieldActions!({
              level,
              node: leaf,
              trail: [
                ...entries.slice(0, level).map((earlier) => ({ id: earlier.id, record: earlier.read?.record ?? null, from: earlier.from ?? null })),
                { id: entry.id, record: shown, from: entry.from ?? null },
              ],
            })}
          />
        ),
      };
    }

    let message: ReactNode;
    if (entry.error !== undefined) {
      message = <div className="p-3"><ProblemView error={entry.error} /></div>;
    } else if (entry.pending || read === null) {
      message = <div className="p-3"><TaskProgress label={level === 0 ? "Reading the record from OSDU" : "Reading the linked record from OSDU"} testId="osdu-read-progress" /></div>;
    } else {
      message = (
        <Alert className="m-3 w-auto" data-testid="osdu-not-found">
          <SearchX />
          <AlertTitle>OSDU holds no record under this id</AlertTitle>
          <AlertDescription>
            <RecordName id={read.targetId} copy className="text-[12px]" />
            <span className="text-[12px] text-muted-foreground">
              {`Read through ${read.flow}`}
              {read.correlationId ? `, correlation id ${read.correlationId}` : ""}
              {". A record never written, or one since removed, reads this way."}
            </span>
          </AlertDescription>
        </Alert>
      );
      if (extras?.notFound !== undefined) {
        message = <>{message}{extras.notFound(read.targetId)}</>;
      }
    }

    return {
      inspector: false,
      content: (
        <>
          <LocationBar
            back={back}
            onBack={onBack}
            location={(
              <>
                {level === 0 ? extras?.place : undefined}
                <CrumbStep first={level !== 0 || extras?.place === undefined}>
                  {level === 0 && extras?.place !== undefined
                    ? <span className="font-medium" title={entry.id}>{idParts(entry.id).unique}</span>
                    : <RecordName id={entry.id} typeOnly={namedByType(entry.id, level)} className="font-medium" />}
                </CrumbStep>
              </>
            )}
            controls={actions}
          />
          {message}
        </>
      ),
    };
  };

  return (
    <div className={fill ? "flex min-h-0 flex-1 flex-col" : undefined} data-testid="osdu-panel">
      {entries.map((entry, level) => {
        const { content, inspector } = body(entry, level);
        return (
          // Every record on the trail stays mounted, so stepping back finds it as it was left; only the last is shown.
          <div
            key={`${level}:${entry.id}`}
            hidden={level !== shownLevel}
            className={fill ? "flex min-h-0 flex-1 flex-col" : undefined}
            data-testid={level > 0 ? "osdu-linked" : undefined}
          >
            <Card
              className={cn(
                "flex flex-col gap-0 overflow-hidden rounded-lg p-0",
                !inspector ? "h-auto" : fill ? "min-h-[520px] flex-1" : "h-[72vh] min-h-[520px]",
              )}
            >
              {content}
            </Card>
          </div>
        );
      })}
    </div>
  );
}
