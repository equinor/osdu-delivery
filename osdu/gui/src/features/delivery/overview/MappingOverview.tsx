import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { useQuery } from "@tanstack/react-query";
import {
  ChevronLeft, ChevronRight, ChevronsDownUp, ChevronsUpDown, Code, Copy, FileCode, FileText, Loader2, Maximize, OctagonAlert, PanelRight, SlidersHorizontal, Table2, TriangleAlert,
} from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from "@/components/ui/resizable";
import { Skeleton } from "@/components/ui/skeleton";
import { ExplainMark } from "@/components/ExplainTip";
import { repoApi } from "@/api/endpoints";
import { SearchInput } from "@/components/SearchInput";
import { useExplain } from "@/features/chat/ExplainContext";
import { usePersistentLayout } from "@/layout/workbench/usePersistentLayout";
import { cn } from "@/lib/utils";
import { deliveryApi, type DeliveryMappingDetail, type MappingDraftIssue, type MappingOutline, type MappingOutlineHow } from "../../../api/delivery";
import { useActivePartition } from "../activePartition";
import { LedgerStanding } from "../LedgerStanding";
import { mappingLineageTarget, osduTypeLineageTarget } from "../lineageTargets";
import { ProblemView } from "../TemplateSheet";
import { ValueCheckScope } from "../ValueCheckBar";
import { flowScope, type ValueCheckSession } from "../useValueCheck";
import { formatCount } from "../valueCheck";
import { isWithin, outlinePathOf } from "../recordPath";
import { OverviewCanvas, type OverviewHandlers, type OverviewMenuTarget, type OverviewPart, type OverviewTip } from "./OverviewCanvas";
import { OverviewExplain } from "./OverviewExplain";
import { INSPECTOR_WIDTH, OverviewInspector } from "./OverviewInspector";
import { PartLineageEntry } from "./OverviewLineage";
import { partTargets, type LineagePlace, type PartTarget } from "./partTargets";
import { OverviewRail } from "./OverviewRail";
import { OverviewDocument, OverviewParameters } from "./OverviewRendered";
import { OverviewRows } from "./OverviewRows";
import { OverviewYaml } from "./OverviewYaml";
import { linesOfPart, type LineRun, type LinesPart } from "./partLines.ts";
import { yamlLines, type ExplainedYaml } from "./yamlExplain.ts";
import { layoutOf } from "./outlineLayout.ts";
import { lineageOfPart, type LineageScope } from "./outlineLineage.ts";
import {
  cardsOf, countsOf, defaultOpen, everyFolding, foundOf, lightOfCard, lightOfField, lightOfRows, marksFor, nodeAt, nodeAtLine, opensTo, pathsOfHow, pathsOfSearch, rowShowing,
  rowsOf, type OutlineLight,
} from "./outlineModel.ts";
import { cardPreviewLabel, rowsSourceOf } from "./tableRows.ts";
import { useRecordValues, type RecordValues } from "./useRecordValues";

const ABOUT = "Each line is one property of the record, read left to right: the table it starts from, what is done to it, and the property it fills. "
  + "A dot is where a line is looked up in a table on the shelf above. A dashed mark is a condition, and a hollow end an optional property. "
  + "Click a section to open it, and a property to keep its lines lit; double-click it to see how it is filled. "
  + "Right-click anything for what can be done with it: a table's rows, the parameters' values, the rendered record. "
  + "A search zooms to what it finds; hold Ctrl and scroll to zoom yourself. Nothing is rendered here, unless you ask for it.";

/** How long the view waits for the typing to stop before it moves to what a search found. */
const SEARCH_SETTLE_MS = 300;

/**
 * What the panel under the canvas shows: a table's rows (or, with `sql`, the T-SQL that reads them in SSMS), the values the
 * parameters render with, the rendered record, or the assistant explaining lines of the YAML (each question its own
 * conversation, numbered so asking again starts afresh).
 */
type Looking =
  | { what: "rows"; card: string; field: string | null; sql: boolean }
  | { what: "parameters"; field: string | null }
  | { what: "document" }
  | { what: "explain"; asked: number; subject: string; question: string };

/** The parts of a record's key, from the JSON array a staged row names it by; null for text that is not one. */
function keyParts(keyJson: string | null | undefined): string[] | null {
  if (keyJson === null || keyJson === undefined) {
    return null;
  }

  try {
    const parts: unknown = JSON.parse(keyJson);
    return Array.isArray(parts) && parts.every((part) => typeof part === "string") ? parts as string[] : null;
  } catch {
    // A key that is no JSON array names no row to mark.
    return null;
  }
}

/** One thing a menu offers: what it says, its glyph, and what it does. */
interface MenuItem {
  id: string;
  label: string;
  glyph: ReactNode;
  act: () => void;
}

/** What a menu holds: its items, and where what it is on stands in lineage. */
type MenuEntry = MenuItem | { id: "lineage"; targets: PartTarget[] };

/** Copies a name for a reader, and says whether the browser let it. */
function copyName(text: string, what: string): void {
  navigator.clipboard.writeText(text).then(
    () => toast.success(`Copied ${what}`),
    () => toast.error("The browser denied clipboard access."),
  );
}

/**
 * The menu a right click opens, on whatever it is on: a table, a column, a property, the record's heading or the canvas. Each
 * names what it is on, and offers what can be done with it there.
 */
function OverviewMenu({ menu, named, items, onClose }: {
  menu: { x: number; y: number } | null;
  named: string;
  items: MenuEntry[];
  onClose: () => void;
}) {
  return (
    <DropdownMenu open={menu !== null} onOpenChange={(open) => !open && onClose()}>
      {/* An anchor at the pointer, so the menu opens where the click was. */}
      <DropdownMenuTrigger asChild>
        <span aria-hidden className="fixed size-px" style={{ top: menu?.y ?? 0, left: menu?.x ?? 0 }} />
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="w-64" data-testid="mapping-overview-menu">
        <DropdownMenuLabel className="truncate font-mono text-[12px] font-normal text-muted-foreground" title={named}>{named}</DropdownMenuLabel>
        <DropdownMenuSeparator />
        {items.map((item) => ("targets" in item
          ? <PartLineageEntry key={item.id} targets={item.targets} onClose={onClose} />
          : (
            <DropdownMenuItem key={item.id} onSelect={item.act} data-testid={`mapping-overview-menu-${item.id}`}>
              {item.glyph}
              {item.label}
            </DropdownMenuItem>
          )))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/** What a canvas showing no record is given, one map for every render so nothing is drawn again for a new empty one. */
const NO_VALUES: ReadonlyMap<string, string> = new Map();

/** What the YAML marks when nothing names a column, one map for every render. */
const NO_MARKS: ReadonlyMap<number, readonly LineRun[]> = new Map();

/** The column or table a part of the canvas is, as the document names it; null for a property, which has lines of its own. */
function linesPartOf(part: OverviewPart | null): LinesPart | null {
  return part === null || part.type === "row" ? null : { source: part.source, field: part.type === "field" ? part.field : null };
}

/** The name of a kind's entity: `WellLog` of `osdu:wks:work-product-component--WellLog:1.4.0`. */
function entityOf(kind: string): { entity: string; version: string } {
  const parts = kind.split(":");
  const type = parts[2] ?? kind;
  return { entity: type.slice(type.lastIndexOf("-") + 1), version: parts[3] ?? "" };
}

/** A piece of the sentence: plain words, a name, or a table's name. */
type LeadPart = { text: string; as?: "name" | "table" };

/** The one sentence saying what the mapping does, in pieces: the rows it reads, the record it makes, and what repeats. */
function leadOf(outline: MappingOutline, table: string | null, repeats: { name: string; table: string | null }[]): LeadPart[] {
  const { entity } = entityOf(outline.kind);
  if (outline.enrichment) {
    return [{ text: "Adds to each " }, { text: entity, as: "name" }, { text: " record another mapping renders." }];
  }

  const tableOf = (name: string | null, otherwise: string): LeadPart => (name === null ? { text: otherwise } : { text: name, as: "table" });
  return [
    { text: "Each row of " },
    tableOf(table, "the record table"),
    { text: " becomes one " },
    { text: entity, as: "name" },
    { text: " record" },
    ...repeats.flatMap((repeat): LeadPart[] => [{ text: ", with one item of " }, { text: repeat.name, as: "name" }, { text: " per row of " }, tableOf(repeat.table, "its child table")]),
    { text: "." },
  ];
}

/** The sentence, cut short when the row is too narrow for it, with the whole of it on hover. */
function Lead({ parts }: { parts: LeadPart[] }) {
  return (
    <span className="min-w-0 truncate" title={parts.map((part) => part.text).join("")}>
      {/* The pieces are fixed by their place in the sentence, so their place is their key. */}
      {parts.map((part, index) => (part.as === undefined
        ? <span key={index}>{part.text}</span>
        : <span key={index} className={cn("font-medium", part.as === "table" && "font-mono")}>{part.text}</span>))}
    </span>
  );
}

/** A tooltip at a place on the canvas, kept inside the frame's width by its own transform. */
function CanvasTip({ tip }: { tip: OverviewTip }) {
  return (
    <div
      role="tooltip"
      className="pointer-events-none absolute z-20 flex max-w-80 -translate-x-1/2 -translate-y-full flex-col gap-0.5 rounded-md border border-border bg-popover px-2 py-1.5 text-popover-foreground shadow-md"
      style={{ left: Math.max(170, tip.x), top: Math.max(56, tip.y - 8) }}
      data-testid="mapping-overview-tip"
    >
      <span className="font-mono text-[11.5px] font-medium [overflow-wrap:anywhere]">{tip.title}</span>
      {tip.body !== "" && <span className="whitespace-pre-line text-[12px] text-muted-foreground [overflow-wrap:anywhere]">{tip.body}</span>}
    </div>
  );
}

/** The record being followed through the mapping, in place of the sentence: which row, where it stands, and whose rows these are. */
function RecordLine({ record, session }: { record: RecordValues; session: ValueCheckSession }) {
  const { lines, line } = record;
  const name = line?.label ?? line?.sourceKey;
  return (
    <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1" data-testid="mapping-overview-record">
      {lines !== null && lines.length > 0 && (
        <span className="flex min-w-0 items-center gap-0.5">
          <Button variant="ghost" size="icon-xs" aria-label="The record before" disabled={record.at === 0} onClick={() => record.step(-1)}><ChevronLeft /></Button>
          <span className="max-w-80 truncate font-mono text-[12px]" title={name}>{name}</span>
          <Button variant="ghost" size="icon-xs" aria-label="The record after" disabled={record.at >= lines.length - 1} onClick={() => record.step(1)}><ChevronRight /></Button>
        </span>
      )}
      {lines !== null && lines.length === 0 && <span className="text-muted-foreground" data-testid="mapping-overview-no-row">No row is staged in this scope.</span>}
      {record.ledger !== undefined && <LedgerStanding ledger={record.ledger} />}
      {record.loading && <Loader2 className="size-3.5 animate-spin text-muted-foreground" aria-label="Reading the record" />}
      <ValueCheckScope session={session} />
    </div>
  );
}

/** The canvas and what surrounds it, for a mapping whose outline has been read. */
function OverviewBody({ detail, outline, unfilled, issues, session, tools, onProperties }: {
  detail: DeliveryMappingDetail;
  outline: MappingOutline;
  unfilled: number | null;
  issues: MappingDraftIssue[];
  session: ValueCheckSession;
  tools: HTMLElement | null;
  onProperties: () => void;
}) {
  const { mapping, yaml } = detail;
  const { flow } = session;
  const [open, setOpen] = useState(() => defaultOpen(outline));
  // What was clicked stays lit; its details open on a double click, and then follow what is clicked until they are closed.
  const [picked, setPicked] = useState<OverviewPart | null>(null);
  const [detailsOpen, setDetailsOpen] = useState(false);
  const inspected = detailsOpen ? picked : null;
  const [pointed, setPointed] = useState<OverviewPart | null>(null);
  const [linePointed, setLinePointed] = useState<string | null>(null);
  const [how, setHow] = useState<MappingOutlineHow | null>(null);
  const [search, setSearch] = useState("");
  const [showYaml, setShowYaml] = useState(false);
  const [tip, setTip] = useState<OverviewTip | null>(null);
  const [menu, setMenu] = useState<{ target: OverviewMenuTarget; x: number; y: number } | null>(null);
  // Whether a menu is open, from the moment it is asked for until the key that closed it has gone by (see Escape below).
  const menuOpen = useRef(false);
  const [looking, setLooking] = useState<Looking | null>(null);
  const explainer = useExplain();
  const asked = useRef(0);
  const layout = usePersistentLayout("sqlflow.delivery.mapping.overview.layout");
  const rowsLayout = usePersistentLayout("sqlflow.delivery.mapping.overview.rows.layout");
  // A conversation needs more of the height than a table's rows, so the explanation's panel keeps a size of its own.
  const explainLayout = usePersistentLayout("sqlflow.delivery.mapping.overview.explain.layout");

  const [partition] = useActivePartition();
  const tables = useQuery({
    queryKey: ["delivery", "flow-source-tables", flow?.pipelineId ?? null, flow?.interface ?? null, flow?.partition ?? null],
    queryFn: () => deliveryApi.sourceTables(flow!.pipelineId, flowScope(flow!)),
    enabled: flow !== null,
    retry: false,
  });
  const caches = useQuery({ queryKey: ["delivery", "mapping-builder", "caches"], queryFn: deliveryApi.builderCaches, retry: false });
  const cached = useMemo(() => caches.data?.find((candidate) => candidate.scope === partition)?.types ?? null, [caches.data, partition]);
  // The flow's node in lineage is listed under its repository's name: only the flow's parameters open it.
  const repo = useQuery({
    queryKey: ["repos", "detail", mapping.repoId],
    queryFn: () => repoApi.getById(mapping.repoId),
    enabled: flow !== null,
    retry: false,
  });

  const record = useRecordValues(outline, session);
  const rows = useMemo(() => rowsOf(outline, open), [outline, open]);
  const measured = useMemo(() => rowsOf(outline, everyFolding(outline)), [outline]);
  const cards = useMemo(() => cardsOf(outline, open, tables.data ?? null, cached), [outline, open, tables.data, cached]);
  const placed = useMemo(() => layoutOf(rows, cards, measured), [rows, cards, measured]);
  const counts = useMemo(() => countsOf(outline), [outline]);

  const dryRun = session.dryRun !== null && !session.dryRunStale ? session.dryRun.run : null;
  const marks = useMemo(() => marksFor(rows, [
    ...issues.filter((issue) => issue.target !== null).map((issue) => ({ path: issue.target!.replace(/^osdu\./, ""), tone: issue.severity, text: issue.message })),
    ...(dryRun?.rules ?? []).filter((rule) => rule.at !== "").map((rule) => ({
      path: rule.at.replace(/^osdu\./, "").replace(/\[\d+\]/g, "[]"), tone: "warning" as const, text: `${formatCount(rule.records)} ${rule.records === 1 ? "record breaks" : "records break"} ${rule.rule}. ${rule.exampleMessage}`,
    })),
  ]), [issues, dryRun, rows]);

  const frame = useRef<HTMLDivElement>(null);
  const svg = useRef<SVGSVGElement>(null);
  const canvas = useRef<OverviewCanvas | null>(null);

  // What was just opened, whose rows are brought into view once they are drawn.
  const opened = useRef<string | null>(null);

  const handlers = useRef<OverviewHandlers>({
    point: setPointed,
    pick: setPicked,
    open: (part) => {
      setPicked(part);
      setDetailsOpen(true);
    },
    toggle: (path) => setOpen((current) => {
      const next = new Set(current);
      if (!next.delete(path)) {
        next.add(path);
        opened.current = path;
      }

      return next;
    }),
    // The canvas says where on the page; the tooltip is placed in the frame.
    tip: (said) => {
      const box = frame.current?.getBoundingClientRect();
      setTip(said === null || box === undefined ? null : { ...said, x: said.x - box.left, y: said.y - box.top });
    },
    clear: () => {
      setPicked(null);
      setDetailsOpen(false);
    },
    leftOut: onProperties,
    menu: (target, at) => {
      menuOpen.current = true;
      setMenu({ target, ...at });
    },
  });

  useEffect(() => {
    if (svg.current === null) {
      return undefined;
    }

    const drawn = new OverviewCanvas(svg.current, {
      point: (part) => handlers.current.point(part),
      pick: (part) => handlers.current.pick(part),
      open: (part) => handlers.current.open(part),
      toggle: (path) => handlers.current.toggle(path),
      tip: (said) => handlers.current.tip(said),
      clear: () => handlers.current.clear(),
      leftOut: () => handlers.current.leftOut(),
      menu: (target, at) => handlers.current.menu(target, at),
    });
    canvas.current = drawn;
    return () => {
      drawn.destroy();
      canvas.current = null;
    };
  }, []);

  const { entity, version } = entityOf(outline.kind);

  // Where each part stands in lineage: a table's node, a column's table's, the nodes a property's value comes from, the record's
  // kind on its heading, and the mapping on the canvas itself.
  const repoName = repo.data?.name ?? null;
  const place = useMemo<LineagePlace>(() => ({ partition, flow, repo: { id: mapping.repoId, name: repoName }, cached }), [partition, flow, mapping.repoId, repoName, cached]);
  const lineageScope = useMemo<LineageScope>(() => ({ tables: tables.data ?? null, flow: flow !== null }), [tables.data, flow]);
  const lineageOf = useCallback((target: OverviewMenuTarget): PartTarget[] => {
    if (target.type === "canvas") {
      return [{ id: "mapping", title: mapping.reference, mark: "mapping", target: mappingLineageTarget(mapping, partition) }];
    }

    if (target.type === "record") {
      const title = `${entity} ${version}`;
      return [{ id: "record", title, mark: "type", target: osduTypeLineageTarget(outline.kind, partition, title) }];
    }

    return partTargets(lineageOfPart(target, outline, cards, lineageScope), place);
  }, [mapping, partition, entity, version, outline, cards, lineageScope, place]);

  useEffect(() => {
    canvas.current?.render(placed, {
      title: `${entity} ${version}`,
      subtitle: outline.kind.split(":").slice(0, 2).join(":"),
      values: record.on ? record.values : NO_VALUES,
      marks,
      unfilled,
    });

    const path = opened.current;
    if (path !== null) {
      opened.current = null;
      const under = placed.rows.filter((row) => isWithin(outlinePathOf(row.row.path), path) || row.row.path.startsWith(`${path}|`));
      if (under.length > 0) {
        canvas.current?.revealRows(path, under[under.length - 1].row.path);
      }
    }
  }, [placed, entity, version, outline.kind, record.on, record.values, marks, unfilled]);

  // What is pointed at comes first; failing that what a line of the YAML writes, what was picked, then a filter or a search.
  const filtered = useMemo(() => {
    if (search.trim() !== "") {
      return pathsOfSearch(outline, search).paths;
    }

    return how === null ? null : pathsOfHow(outline, how).paths;
  }, [outline, search, how]);

  const light = useMemo<OutlineLight | null>(() => {
    const part = pointed ?? (linePointed === null ? picked : { type: "row" as const, path: linePointed });
    if (part !== null) {
      // A line of the document can write what is folded: the row folding it stands for it.
      return part.type === "row"
        ? lightOfRows(rows, new Set([rowShowing(rows, part.path)?.path ?? part.path]))
        : part.type === "field" ? lightOfField(rows, part.source, part.field) : lightOfCard(rows, part.source);
    }

    return filtered === null ? null : lightOfRows(rows, filtered);
  }, [rows, pointed, linePointed, picked, filtered]);

  const searched = useMemo(() => (search.trim() === "" || filtered === null ? null : foundOf(rows, filtered, search)), [rows, filtered, search]);

  useEffect(() => {
    canvas.current?.setLight(light, picked, searched);
  }, [light, picked, searched, placed]);

  // What a search finds is framed once the typing stops, close enough to read: the properties and the columns they read. Clearing
  // the search goes back to the view it started from. A mapping can be taller than the window.
  const framedTerm = useRef("");
  useEffect(() => {
    const term = search.trim();
    if (term === framedTerm.current) {
      return undefined;
    }

    if (term === "") {
      framedTerm.current = "";
      canvas.current?.unfocus();
      return undefined;
    }

    const wait = setTimeout(() => {
      framedTerm.current = term;
      if (searched !== null) {
        canvas.current?.focus(searched);
      }
    }, SEARCH_SETTLE_MS);
    return () => clearTimeout(wait);
  }, [search, searched]);

  // What a filter finds is brought into view once, when it is asked for: it is too many properties to frame.
  const shownFilter = useRef<ReadonlySet<string> | null>(null);
  useEffect(() => {
    const shown = how === null ? null : filtered;
    if (shown !== null && shown !== shownFilter.current) {
      const first = placed.rows.find((row) => shown.has(row.row.path));
      if (first !== undefined) {
        canvas.current?.reveal(first.row.path);
      }
    }

    shownFilter.current = shown;
  }, [how, filtered, placed]);

  useEffect(() => {
    canvas.current?.setInset(0, inspected === null ? 0 : Math.min(INSPECTOR_WIDTH, (frame.current?.clientWidth ?? 0) * 0.85));
  }, [inspected]);

  useEffect(() => {
    if (picked?.type === "row") {
      canvas.current?.reveal(picked.path);
    }
  }, [picked]);

  // Escape closes what was opened last: the details first, then what is lit, then what is shown under the canvas. A menu open
  // takes it for itself: the menu is counted open from the moment it is asked for until the key that closed it has gone by, which
  // a render after it would be too late to say.
  const escapes = useRef<{ details: boolean; picked: boolean }>({ details: false, picked: false });
  useEffect(() => {
    escapes.current = { details: inspected !== null, picked: picked !== null };
  }, [inspected, picked]);

  const closeMenu = useCallback(() => {
    setMenu(null);
    setTimeout(() => {
      menuOpen.current = false;
    }, 0);
  }, []);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      // A menu or a picker of the panels (a lineage jump's) takes the key for itself, and marks it so as it closes.
      if (event.key !== "Escape" || menuOpen.current || event.defaultPrevented) {
        return;
      }

      // Inside the explanation the key is the conversation's: a follow-up being written is not closed under its writer.
      if (event.target instanceof Element && event.target.closest("[data-overview-explain]") !== null) {
        return;
      }

      if (escapes.current.details) {
        setDetailsOpen(false);
      } else if (escapes.current.picked) {
        setPicked(null);
      } else {
        setLooking(null);
      }
    };

    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const opensFor = useCallback((paths: ReadonlySet<string>) => {
    if (paths.size > 0) {
      setOpen((current) => new Set([...current, ...paths]));
    }
  }, []);

  // A pick from the document or a panel can name what is folded, so what holds it opens with it. A pick lights it; asking for its
  // details opens them as well.
  const pickShown = useCallback((part: OverviewPart) => {
    if (part.type === "row") {
      opensFor(opensTo(outline, part.path));
    }

    setPicked(part);
  }, [outline, opensFor]);

  const openShown = useCallback((part: OverviewPart) => {
    pickShown(part);
    setDetailsOpen(true);
  }, [pickShown]);

  const fitView = useCallback(() => canvas.current?.fit(true), []);

  // What a table holds is looked at from the table or one of its fields, whose column or line is then brought into view: a
  // table's rows, or for the flow's parameters the values they render with. `sql` opens the T-SQL that reads the rows instead.
  const lookAt = useCallback((part: OverviewPart, sql = false) => {
    if (part.type === "row") {
      return;
    }

    const field = part.type === "field" ? part.field : null;
    setLooking(part.source === cards.find((card) => card.source.kind === "parameters")?.id ? { what: "parameters", field } : { what: "rows", card: part.source, field, sql });
  }, [cards]);

  const showSql = useCallback((sql: boolean) => setLooking((current) => (current?.what === "rows" ? { ...current, sql } : current)), []);

  const onSearch = (next: string) => {
    setSearch(next);
    setHow(null);
    if (next.trim() !== "") {
      opensFor(pathsOfSearch(outline, next).opens);
    }
  };

  const onHow = (next: MappingOutlineHow) => {
    const turnedOn = how !== next;
    setHow(turnedOn ? next : null);
    setSearch("");
    if (turnedOn) {
      opensFor(pathsOfHow(outline, next).opens);
    }
  };

  const found = search.trim() === "" ? null : filtered?.size ?? 0;
  const allOpen = rows.every((row) => !row.folding || row.open);

  // A column or a table lights what reads it in the YAML and marks where it is named, whether it is folded on the canvas or
  // not; the one picked is brought into view, the one pointed at only lit.
  const documentLines = useMemo(() => yamlLines(yaml), [yaml]);
  const lightLines = useMemo(() => {
    const part = linesPartOf(pointed ?? (linePointed === null ? picked : null));
    return part === null ? null : linesOfPart(outline, documentLines, part);
  }, [outline, documentLines, pointed, linePointed, picked]);
  const pickedLines = useMemo(() => {
    const part = linesPartOf(picked);
    return part === null ? null : linesOfPart(outline, documentLines, part);
  }, [outline, documentLines, picked]);

  const litLines = useMemo(() => {
    if (lightLines !== null) {
      return lightLines.lit;
    }

    const lit = new Set<number>();
    if (light === null) {
      return lit;
    }

    for (const row of rows) {
      const { line, endLine } = row.node;
      if (light.rows.has(row.path) && !row.open && line !== null && line !== undefined && endLine !== null && endLine !== undefined) {
        for (let at = line; at <= endLine; at++) {
          lit.add(at);
        }
      }
    }

    return lit;
  }, [light, rows, lightLines]);

  const pickedNode = picked?.type === "row" ? nodeAt(outline, picked.path) : null;
  const main = cards.find((card) => card.source.kind === "record");
  const repeats = cards.filter((card) => card.source.kind === "child" && card.repeater !== null)
    .map((card) => ({ name: nodeAt(outline, card.repeater!)?.name ?? card.title, table: card.subtitle === null ? null : card.title }));
  const problems = issues.filter((issue) => issue.target === null);
  const lookingCard = looking?.what !== "rows" ? null : cards.find((card) => card.id === looking.card && rowsSourceOf(card) !== null) ?? null;
  const parametersCard = cards.find((card) => card.source.kind === "parameters") ?? null;
  const explaining = looking?.what === "explain" ? looking : null;
  const lookingAt = lookingCard !== null || (looking?.what === "parameters" && parametersCard !== null) || looking?.what === "document" || explaining !== null;
  const belowLayout = explaining === null ? rowsLayout : explainLayout;
  const recordKey = record.on ? record.line?.keyJson ?? null : null;

  /** What the menu on `target` names it by, and what it offers there, where it stands in lineage among it. */
  function menuFor(target: OverviewMenuTarget | null): { named: string; items: MenuEntry[] } {
    const yaml: MenuItem = { id: "yaml", label: showYaml ? "Hide the YAML" : "Show the YAML", glyph: <FileCode />, act: () => setShowYaml((current) => !current) };
    if (target === null) {
      return { named: "", items: [] };
    }

    const lineage: MenuEntry = { id: "lineage", targets: lineageOf(target) };
    if (target.type === "canvas") {
      return {
        named: mapping.reference,
        items: [
          { id: "fit", label: "Fit to the window", glyph: <Maximize />, act: fitView },
          { id: "open-all", label: allOpen ? "Fold" : "Open all", glyph: allOpen ? <ChevronsDownUp /> : <ChevronsUpDown />, act: () => setOpen(allOpen ? defaultOpen(outline) : everyFolding(outline)) },
          yaml,
          lineage,
        ],
      };
    }

    if (target.type === "record") {
      return {
        named: outline.kind,
        items: [
          { id: "document", label: "Preview the rendered record", glyph: <FileText />, act: () => setLooking({ what: "document" }) },
          yaml,
          lineage,
          { id: "copy", label: "Copy the kind", glyph: <Copy />, act: () => copyName(outline.kind, "the kind") },
        ],
      };
    }

    if (target.type === "row") {
      const path = outlinePathOf(target.path);
      return {
        named: path,
        items: [
          { id: "details", label: "Details", glyph: <PanelRight />, act: () => openShown(target) },
          { id: "yaml-line", label: "Show in the YAML", glyph: <FileCode />, act: () => { setShowYaml(true); pickShown(target); } },
          lineage,
          { id: "copy", label: "Copy the path", glyph: <Copy />, act: () => copyName(path, "the path") },
        ],
      };
    }

    const card = cards.find((candidate) => candidate.id === target.source);
    const label = card === undefined ? null : cardPreviewLabel(card);
    const read = card === undefined ? null : rowsSourceOf(card)?.from ?? null;
    const table = card === undefined ? undefined : tables.data?.find((candidate) => (card.source.kind === "record" ? candidate.dataset === null : `dataset.${candidate.dataset}` === card.id));
    const named = target.type === "field" ? target.field : card?.title ?? target.source;
    const written = linesOfPart(outline, documentLines, linesPartOf(target)!).first !== null;
    return {
      named,
      items: [
        ...(label === null ? [] : [{ id: "preview", label, glyph: card?.source.kind === "parameters" ? <SlidersHorizontal /> : <Table2 />, act: () => lookAt(target) }]),
        // SQL reads a flow's table and the partition's cache; OSDU's search is asked, not queried.
        ...(read === "table" || read === "cache" ? [{ id: "sql", label: "Show the SQL", glyph: <Code />, act: () => lookAt(target, true) }] : []),
        { id: "details", label: "Details", glyph: <PanelRight />, act: () => openShown(target) },
        ...(written ? [{ id: "yaml-line", label: "Show in the YAML", glyph: <FileCode />, act: () => { setShowYaml(true); pickShown(target); } }] : []),
        lineage,
        target.type === "field"
          ? { id: "copy", label: "Copy the name", glyph: <Copy />, act: () => copyName(target.field, "the name") }
          : { id: "copy", label: table === undefined ? "Copy the name" : "Copy the table's name", glyph: <Copy />, act: () => copyName(table?.table ?? named, "the name") },
      ],
    };
  }

  const menuOf = menuFor(menu?.target ?? null);
  const { entity: recordEntity, version: recordVersion } = entityOf(outline.kind);

  // Explain over the YAML answers under the canvas, where a table's rows open, so the lines it explains stay in view beside it.
  const explainYaml = explainer === null ? null : (code: ExplainedYaml) => {
    asked.current += 1;
    setLooking({ what: "explain", asked: asked.current, ...explainer.codeQuestion(code) });
  };

  // Leaving the canvas for what is shown under it leaves a moment's wait behind, which would put the light out again.
  const pointFromBelow = (part: OverviewPart | null) => {
    canvas.current?.stopPointing();
    setPointed(part);
  };

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-2" data-testid="mapping-overview">
      <OverviewMenu menu={menu} named={menuOf.named} items={menuOf.items} onClose={closeMenu} />
      {problems.map((issue, index) => (
        <p key={`${index}-${issue.message}`} className={cn("flex items-start gap-1.5 text-[13px]", issue.severity === "error" ? "text-destructive" : "text-warning")} data-testid="mapping-overview-issue">
          {issue.severity === "error" ? <OctagonAlert className="mt-0.5 size-3.5 shrink-0" /> : <TriangleAlert className="mt-0.5 size-3.5 shrink-0" />}
          {issue.message}
        </p>
      ))}

      {/* The page's heading row takes the search and the sentence, so the canvas starts right under it. */}
      {tools !== null && createPortal(
        <>
          <SearchInput value={search} onChange={onSearch} placeholder="Property or column" label="Find a property or a column" className="sm:w-60" testId="mapping-overview-search" />
          {found !== null && (
            <span className="shrink-0 text-[12px] text-muted-foreground" data-testid="mapping-overview-found">
              <span className="font-mono tabular-nums">{formatCount(found)}</span> of <span className="font-mono tabular-nums">{formatCount(counts.properties)}</span> found
            </span>
          )}
          <div className="flex min-w-0 flex-[1_1_26rem] items-center text-[13px]">
            {record.on
              ? <RecordLine record={record} session={session} />
              : (
                <p className="flex min-w-0 items-center gap-1" data-testid="mapping-overview-lead">
                  <Lead parts={leadOf(outline, main?.subtitle === null || main === undefined ? null : main.title, repeats)} />
                  <ExplainMark title="How to read this" text={ABOUT} testId="mapping-overview-about" />
                </p>
              )}
          </div>
        </>,
        tools,
      )}
      {record.problem !== null && <ProblemView error={record.problem} testId="mapping-overview-record-error" />}

      <Card className="min-h-0 flex-1 flex-row gap-0 overflow-hidden rounded-lg p-0">
        <OverviewRail
          counts={counts}
          how={how}
          onHow={onHow}
          record={{ on: record.on, blocked: flow === null ? record.blocked : null, onToggle: record.toggle }}
          yaml={{ on: showYaml, onToggle: () => setShowYaml((current) => !current) }}
          allOpen={allOpen}
          onOpenAll={() => setOpen(allOpen ? defaultOpen(outline) : everyFolding(outline))}
          onZoom={(factor) => canvas.current?.zoomBy(factor)}
          onFit={fitView}
        />
        {/* The canvas keeps its place in the tree whether or not the YAML is open: D3 owns what is inside its svg. */}
        <ResizablePanelGroup orientation="horizontal" className="min-h-0 min-w-0 flex-1" defaultLayout={showYaml ? layout.defaultLayout : undefined} onLayoutChanged={showYaml ? layout.onLayoutChanged : undefined}>
          <ResizablePanel id="overview-canvas" minSize="40" className="flex min-h-0 min-w-0 flex-col">
            {/* The same for what is shown under it: the canvas's panel stays first, whether or not anything is. */}
            <ResizablePanelGroup
              orientation="vertical"
              className="min-h-0 flex-1"
              defaultLayout={lookingAt ? belowLayout.defaultLayout : undefined}
              onLayoutChanged={lookingAt ? belowLayout.onLayoutChanged : undefined}
            >
              <ResizablePanel id="overview-canvas-frame" minSize="25" className="flex min-h-0 min-w-0 flex-col">
                <div ref={frame} className="relative h-full min-h-0 min-w-0 overflow-hidden" data-testid="mapping-overview-canvas">
                  <svg ref={svg} className="size-full cursor-grab bg-background active:cursor-grabbing" role="group" aria-label={`How ${mapping.reference} fills a record`} />
                  <OverviewInspector
                    part={inspected}
                    outline={outline}
                    tables={tables.data ?? null}
                    cached={cached}
                    yaml={yaml}
                    values={record.on ? record.values : NO_VALUES}
                    lineage={inspected === null ? [] : lineageOf(inspected)}
                    onPick={openShown}
                    onPreview={lookAt}
                    onClose={() => setDetailsOpen(false)}
                  />
                  {tip !== null && <CanvasTip tip={tip} />}
                </div>
              </ResizablePanel>
              {explaining !== null && (
                <>
                  <ResizableHandle withHandle />
                  <ResizablePanel id="overview-explain" defaultSize="65" minSize={240} maxSize="85" className="flex min-h-0 min-w-0 flex-col">
                    <OverviewExplain key={explaining.asked} subject={explaining.subject} question={explaining.question} onClose={() => setLooking(null)} />
                  </ResizablePanel>
                </>
              )}
              {lookingAt && explaining === null && (
                <>
                  <ResizableHandle withHandle />
                  <ResizablePanel id="overview-rows" defaultSize="40" minSize={120} maxSize="70" className="flex min-h-0 min-w-0 flex-col">
                    {lookingCard !== null && (
                      <OverviewRows
                        key={lookingCard.id}
                        card={lookingCard}
                        focus={looking?.what === "rows" ? looking.field : null}
                        sql={looking?.what === "rows" && looking.sql}
                        onSql={showSql}
                        session={session}
                        partition={partition}
                        tables={tables.data ?? null}
                        shownKey={record.on ? keyParts(record.line?.keyJson) : null}
                        lineage={lineageOf({ type: "card", source: lookingCard.id })}
                        onPoint={pointFromBelow}
                        onPick={pickShown}
                        onOpen={openShown}
                        onClose={() => setLooking(null)}
                      />
                    )}
                    {looking?.what === "parameters" && parametersCard !== null && (
                      <OverviewParameters
                        card={parametersCard}
                        focus={looking.field}
                        session={session}
                        recordKey={recordKey}
                        lineage={lineageOf({ type: "card", source: parametersCard.id })}
                        onPoint={pointFromBelow}
                        onPick={pickShown}
                        onOpen={openShown}
                        onClose={() => setLooking(null)}
                      />
                    )}
                    {looking?.what === "document" && (
                      <OverviewDocument
                        title={`${recordEntity} ${recordVersion}`}
                        session={session}
                        recordKey={recordKey}
                        lineage={lineageOf({ type: "record" })}
                        onClose={() => setLooking(null)}
                      />
                    )}
                  </ResizablePanel>
                </>
              )}
            </ResizablePanelGroup>
          </ResizablePanel>
          {showYaml && (
            <>
              <ResizableHandle withHandle />
              <ResizablePanel id="overview-yaml" defaultSize={420} minSize={280} maxSize="60" className="flex min-h-0 min-w-0 flex-col">
                <OverviewYaml
                  file={mapping.relativePath}
                  yaml={yaml}
                  lit={litLines}
                  marks={lightLines?.marks ?? NO_MARKS}
                  focus={picked?.type === "row" ? pickedNode?.line ?? null : pickedLines?.first ?? null}
                  focusKey={picked === null ? "" : JSON.stringify(picked)}
                  onLine={(line) => setLinePointed(line === null ? null : nodeAtLine(outline, line)?.path ?? null)}
                  onPick={(line) => {
                    const node = nodeAtLine(outline, line);
                    if (node !== null) {
                      pickShown({ type: "row", path: node.path });
                    }
                  }}
                  onExplain={explainYaml}
                  blockOf={(line) => {
                    const node = nodeAtLine(outline, line);
                    return node?.line == null || node.endLine == null ? null : { range: { start: node.line, end: node.endLine }, named: outlinePathOf(node.path) };
                  }}
                />
              </ResizablePanel>
            </>
          )}
        </ResizablePanelGroup>
      </Card>
    </div>
  );
}

/**
 * What a mapping does, on one canvas: the tables it reads on the left, the record it fills on the right, one line per property
 * between them with what is done to its value on the way, and what it looks values up in on a shelf above. It opens on the
 * sections of the record, each section a click from what it holds, and is read from the document alone until a reader asks to
 * follow a record through it (osdu/docs/reference/concepts/mapping-overview.md).
 */
export function MappingOverview({ detail, session, tools, onProperties }: {
  detail: DeliveryMappingDetail;
  session: ValueCheckSession;
  /** Where in the page's toolbar the overview's own controls go. */
  tools: HTMLElement | null;
  onProperties: () => void;
}) {
  const { mapping, yaml } = detail;
  const outline = useQuery({
    queryKey: ["delivery", "mapping-outline", mapping.repoId, mapping.relativePath, mapping.contentHash],
    queryFn: () => deliveryApi.mappingOutline(yaml, mapping.relativePath, mapping.repoId),
  });

  if (outline.isPending) {
    return <Skeleton className="min-h-80 w-full flex-1" data-testid="mapping-overview-loading" />;
  }

  if (outline.isError) {
    return <ProblemView error={outline.error} testId="mapping-overview-error" />;
  }

  const { outline: read, unfilled, issues } = outline.data;
  if (read === null) {
    return (
      <div className="flex flex-col gap-1" data-testid="mapping-overview-unread">
        {issues.map((issue, index) => (
          <p key={`${index}-${issue.message}`} className="flex items-start gap-1.5 text-[13px] text-destructive">
            <OctagonAlert className="mt-0.5 size-3.5 shrink-0" />
            {issue.message}
          </p>
        ))}
      </div>
    );
  }

  return <OverviewBody key={mapping.contentHash} detail={detail} outline={read} unfilled={unfilled} issues={issues} session={session} tools={tools} onProperties={onProperties} />;
}
