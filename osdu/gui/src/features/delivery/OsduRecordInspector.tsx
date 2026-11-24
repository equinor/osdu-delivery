import { useCallback, useMemo, useState, type ReactNode } from "react";
import { useMutation } from "@tanstack/react-query";
import { ChevronDown, ChevronRight, ChevronsDownUp, ChevronsUpDown, Download, GitCompare, SearchX } from "lucide-react";
import { VersionCompareDialog } from "./VersionCompare";
import { toast } from "sonner";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from "@/components/ui/resizable";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { cn } from "@/lib/utils";
import { CopyButton } from "@/components/CopyButton";
import { EmptyState } from "@/components/EmptyState";
import { IconAction } from "@/components/IconAction";
import { SearchInput } from "@/components/SearchInput";
import type { DeliveryOsduRead } from "../../api/delivery";
import { downloadJson, fileNameOf } from "./osduDocument";
import {
  branchPaths, buildModel, CONTENT_SECTION, describeBranch, documentNode, idParts, loadLayout, matching, recordNameOf, saveLayout,
  trail, type RecordModel, type RecordNode,
} from "./osduRecordModel";
import { RecordName } from "./RecordName";
import { inspectorPath } from "./recordPath";
import { failureText } from "./answers";
import { ProblemView, TaskProgress } from "./TemplateSheet";
import { Crumb, CrumbStep, LocationBar, VersionPicker } from "./OsduRecordInspectorHeader";
import { JsonView } from "./OsduRecordInspectorJson";
import { type EarlierRecord, namedByType, type OpenLink, PAGE, text } from "./osduRecordInspectorShared";
import { ItemTable } from "./OsduRecordInspectorTable";
import { AccessView, FieldRows, LinksView, RecordView } from "./OsduRecordInspectorViews";
export { Highlight } from "./OsduRecordInspectorValues";
export { JsonView } from "./OsduRecordInspectorJson";

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
  // value of the record itself, its access and legal. A check names a key holding a dot in brackets (tags['lra.dataID']),
  // which the inspector holds as the step after tags.
  const openPath = (path: string) => {
    const target = model.byPath.get(path) ?? model.byPath.get(inspectorPath(path));
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
