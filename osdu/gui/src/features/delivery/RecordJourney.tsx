import { useLayoutEffect, useMemo, useState, type ReactNode } from "react";
import { format, formatDistanceToNow } from "date-fns";
import { ChevronRight, CircleDot, Layers, RefreshCw } from "lucide-react";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { parseUtc } from "@/lib/time";
import { EmptyState } from "@/components/EmptyState";
import { RelativeTime } from "@/components/RelativeTime";
import type { DeliveryActivity, DeliveryAttempt, DeliveryRecord, DeliveryRecordChain } from "../../api/delivery";
import { RunRef } from "./DeliveryRefs";
import { actorInitial, actorName } from "./recordStory";
import { type Chapter, clockOf, dayOf, type Entry, type Lane, type Tone } from "./recordJourneyEntry";
import { FactLine, Mono } from "./RecordJourneyParts";
import { buildStory } from "./recordJourneyStory";
export { RecordMilestones } from "./RecordMilestones";

/** What the timeline narrows to: one lane, or the steps someone asked for. */
type Filter = "all" | Lane | "people";

const LANES: readonly Lane[] = ["source", "ledger", "osdu"];
const LANE_X: Record<Lane, number> = { source: 22, ledger: 66, osdu: 110 };
const LANE_LABEL: Record<Lane, string> = { source: "Source", ledger: "Ledger", osdu: "OSDU" };

/** How far below the top of its row a node's centre sits, which is where the path meets it. */
const NODE_CENTRE = 14;

/** The time, the rail and the entry: one grid every row of the timeline shares, so times and nodes line up. */
const ROW = "grid grid-cols-[72px_132px_minmax(0,1fr)]";

const TONE_RING: Record<Tone, string> = {
  success: "border-success/50 bg-success/10 text-success",
  destructive: "border-destructive/50 bg-destructive/10 text-destructive",
  warning: "border-warning/50 bg-warning/10 text-warning",
  info: "border-info/50 bg-info/10 text-info",
  muted: "border-border bg-muted text-muted-foreground",
};

const TONE_TEXT: Record<Tone, string> = {
  success: "text-success",
  destructive: "text-destructive",
  warning: "text-warning",
  info: "text-info",
  muted: "text-muted-foreground",
};

/** The lowest the timeline's list goes, so a small window still shows a useful stretch of it and the page scrolls. */
const MIN_LIST_HEIGHT = 320;

/** The nearest ancestor that scrolls vertically (the workbench's content pane), or null when the window scrolls. */
function scrollingAncestor(node: HTMLElement): HTMLElement | null {
  for (let parent = node.parentElement; parent !== null; parent = parent.parentElement) {
    const overflow = getComputedStyle(parent).overflowY;
    if (overflow === "auto" || overflow === "scroll") {
      return parent;
    }
  }

  return null;
}

/**
 * The most a list may grow to so that it, and what its card holds under it, ends at the bottom of the pane the page
 * scrolls in: the rest of the page, whatever the header above it and the window hold. It is measured before paint,
 * from the list's place in the pane's content rather than on screen, so it holds while the page scrolls, and again
 * whenever the pane or anything in it changes size. A short list stays short; a long one fills the page and scrolls.
 */
function useFillHeight(): { cardRef: (node: HTMLDivElement | null) => void; listRef: (node: HTMLDivElement | null) => void; maxHeight: number | null } {
  const [card, setCard] = useState<HTMLDivElement | null>(null);
  const [list, setList] = useState<HTMLDivElement | null>(null);
  const [maxHeight, setMaxHeight] = useState<number | null>(null);
  useLayoutEffect(() => {
    if (card === null || list === null) {
      return;
    }

    const pane = scrollingAncestor(card);
    const measure = () => {
      const paneTop = pane === null ? 0 : pane.getBoundingClientRect().top;
      const paneHeight = pane === null ? window.innerHeight : pane.clientHeight;
      const scrolled = pane === null ? window.scrollY : pane.scrollTop;
      const padding = pane === null ? 0 : Number.parseFloat(getComputedStyle(pane).paddingBottom) || 0;
      const listBox = list.getBoundingClientRect();
      const cardBottom = card.getBoundingClientRect().bottom;
      const top = listBox.top - paneTop + scrolled;
      // What the card holds under the list, and what the page keeps under the card (its own padding, anything after it).
      const below = cardBottom - listBox.bottom;
      const contentBottom = Math.max(cardBottom, ...Array.from((pane ?? document.body).children, (child) => child.getBoundingClientRect().bottom));
      const after = contentBottom - cardBottom;
      setMaxHeight(Math.max(MIN_LIST_HEIGHT, Math.floor(paneHeight - top - below - after - padding)));
    };
    measure();

    // The pane resizes with the window; what the pane holds resizes when the header above the list grows or shrinks.
    const observer = new ResizeObserver(measure);
    observer.observe(pane ?? document.documentElement);
    for (const child of Array.from((pane ?? document.body).children)) {
      observer.observe(child);
    }

    return () => observer.disconnect();
  }, [card, list]);

  return { cardRef: setCard, listRef: setList, maxHeight };
}

function matches(entry: Entry, filter: Filter): boolean {
  return filter === "all" || (filter === "people" ? entry.actor !== null : entry.lane === filter);
}

const EMPTY: Record<Exclude<Filter, "all">, { title: string; description: string }> = {
  source: { title: "No change recorded", description: "The ledger holds no version of this record's row in its ingestion table." },
  ledger: { title: "Nothing decided in the ledger", description: "The ledger has taken no step of its own on this record." },
  osdu: { title: "Nothing delivered yet", description: "The record has not been delivered, removed or verified." },
  people: { title: "Nobody asked for anything", description: "Nobody has redelivered, released, verified or removed this record." },
};

/** A moment in the time column: the local clock, with the day under it when the row's chapter began on another day. */
function TimeCell({ at, day }: { at: string; day: string | null }) {
  const date = parseUtc(at);
  const utc = date.toISOString().replace("T", " ").replace(/\.\d+Z$/, " UTC");
  return (
    <div className="flex flex-col items-end pr-2.5 pt-[5px]">
      <Tooltip>
        <TooltipTrigger asChild>
          <span className="font-mono text-[12px] tabular-nums">
            {format(date, "HH:mm")}<span className="text-muted-foreground">{format(date, ":ss")}</span>
          </span>
        </TooltipTrigger>
        <TooltipContent className="font-mono text-[11px]">{`${formatDistanceToNow(date, { addSuffix: true })} · ${utc}`}</TooltipContent>
      </Tooltip>
      {day !== null && <span className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">{day}</span>}
    </div>
  );
}

const PATH = "absolute bg-muted-foreground/40";

/**
 * The rail beside one row: the three lanes, the path from the row above (across to this row's lane at the top of the
 * row, then down to its node) and on to the row below, and the node.
 */
function RailCell({ lane, from, last, children }: { lane: Lane; from: Lane | null; last: boolean; children?: ReactNode }) {
  const x = LANE_X[lane];
  const left = from === null ? x : Math.min(LANE_X[from], x);
  const right = from === null ? x : Math.max(LANE_X[from], x);
  return (
    <div className="relative" aria-hidden="true">
      {LANES.map((l) => <span key={l} className="absolute inset-y-0 w-px bg-border/70" style={{ left: LANE_X[l] }} />)}
      {from !== null && right > left && <span className={cn(PATH, "top-0 h-0.5")} style={{ left: left - 1, width: right - left + 2 }} />}
      {from !== null && <span className={cn(PATH, "top-0 w-0.5")} style={{ left: x - 1, height: NODE_CENTRE }} />}
      {!last && <span className={cn(PATH, "bottom-0 w-0.5")} style={{ left: x - 1, top: NODE_CENTRE }} />}
      {children}
    </div>
  );
}

/** A node on the rail: a ring in the colour of how the step went, around its icon or the initial of who asked. */
function RailNode({ lane, tone, size = 22, className, children }: { lane: Lane; tone: Tone; size?: number; className?: string; children: ReactNode }) {
  return (
    <span
      className={cn("absolute z-[1] rounded-full bg-card", className)}
      style={{ left: LANE_X[lane] - size / 2, top: NODE_CENTRE - size / 2, width: size, height: size }}
    >
      <span className={cn("flex size-full items-center justify-center rounded-full border", TONE_RING[tone])}>{children}</span>
    </span>
  );
}

function ScopeChip({ scope }: { scope: number }) {
  return scope === 1
    ? (
      <span className="inline-flex items-center gap-1 whitespace-nowrap rounded-full border border-primary/45 bg-primary/10 px-1.5 text-[11px] font-medium text-primary" title="The request named this record alone" data-testid="journey-scope">
        <CircleDot className="size-3" />this record only
      </span>
    )
    : (
      <span className="inline-flex items-center gap-1 whitespace-nowrap rounded-full border border-warning/45 bg-warning/10 px-1.5 text-[11px] font-medium text-warning" title={`The request named ${scope} records at once`} data-testid="journey-scope">
        <Layers className="size-3" />{`${scope} records`}
      </span>
    );
}

function EntryRow({ entry, from, last, day, open, onToggle }: {
  entry: Entry; from: Lane | null; last: boolean; day: string | null; open: boolean; onToggle: () => void;
}) {
  const Icon = entry.icon;
  const title = <span className="text-[13.5px] font-semibold">{entry.title}</span>;
  return (
    <li className={ROW} data-testid={entry.testId} data-state={open ? "open" : "closed"}>
      <TimeCell at={entry.at} day={day} />
      <RailCell lane={entry.lane} from={from} last={last}>
        <RailNode lane={entry.lane} tone={entry.tone}>
          {Icon !== null
            ? <Icon className="size-3" />
            : <span className="text-[10px] font-semibold" title={entry.actor ?? undefined}>{actorInitial(entry.actor ?? "")}</span>}
        </RailNode>
      </RailCell>
      <div className={cn("flex min-w-0 flex-col gap-0.5 pl-1 pt-0.5", last ? "pb-1" : "pb-3.5")}>
        <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
          {entry.more !== null
            ? (
              <button
                type="button"
                className="flex min-w-0 cursor-pointer items-center gap-1.5 text-left hover:underline hover:decoration-border hover:underline-offset-4"
                onClick={onToggle}
                aria-expanded={open}
                data-testid="journey-event-toggle"
              >
                <ChevronRight className={cn("size-3.5 shrink-0 text-muted-foreground transition-transform motion-reduce:transition-none", open && "rotate-90")} />
                {title}
              </button>
            )
            : <span className="flex min-w-0 items-center gap-1.5"><span className="size-3.5 shrink-0" aria-hidden="true" />{title}</span>}
          {entry.actor !== null && (
            <span className="whitespace-nowrap rounded-full border px-1.5 text-[11px] font-medium text-muted-foreground" title={entry.actor} data-testid="journey-actor">
              {actorName(entry.actor)}
            </span>
          )}
          {entry.scope !== null && <ScopeChip scope={entry.scope} />}
        </div>
        <FactLine parts={entry.facts} />
        {entry.then !== null && (
          <span className={cn("flex items-baseline gap-1.5 text-[12.5px]", TONE_TEXT[entry.then.tone])}>
            <span className="text-muted-foreground" aria-hidden="true">↳</span>{entry.then.text}
          </span>
        )}
        {entry.error !== null && <span className="text-[12px] text-destructive" data-testid="journey-attempt-error">{entry.error}</span>}
        {open && entry.more !== null && (
          <div className="mt-1.5 rounded-md border bg-muted/30 px-3 py-2" data-testid="journey-event-detail">
            {entry.more}
          </div>
        )}
      </div>
    </li>
  );
}

/** When a chapter ran: the day and the span of clock it covered, or both days when it ran past midnight. */
function chapterWhen(entries: Entry[]): string {
  const first = entries[0].at;
  const last = entries[entries.length - 1].at;
  if (dayOf(first) !== dayOf(last)) {
    return `${dayOf(first)} ${clockOf(first)} to ${dayOf(last)} ${clockOf(last)}`;
  }

  return clockOf(first) === clockOf(last) ? `${dayOf(first)} · ${clockOf(first)}` : `${dayOf(first)} · ${clockOf(first)} to ${clockOf(last)}`;
}

function ChapterRow({ chapter, count, from }: { chapter: Chapter; count: number; from: Lane | null }) {
  const newest = chapter.entries[chapter.entries.length - 1].at;
  return (
    <li className={ROW} data-testid="journey-chapter" aria-label={`Chapter ${chapter.number}: ${chapter.trigger}, ${chapter.outcome.text}`}>
      <div className="flex justify-end pr-2.5 pt-[15px]">
        <span className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">{dayOf(newest)}</span>
      </div>
      <div className="relative" aria-hidden="true">
        {LANES.map((l) => <span key={l} className="absolute inset-y-0 w-px bg-border/70" style={{ left: LANE_X[l] }} />)}
        {from !== null && <span className={cn(PATH, "inset-y-0 w-0.5")} style={{ left: LANE_X[from] - 1 }} />}
      </div>
      <div className="min-w-0 py-1.5 pl-1">
        <div className="flex flex-wrap items-center gap-x-2.5 gap-y-1 rounded-md border bg-muted/40 px-2.5 py-1.5">
          <span className="grid size-5 place-items-center rounded-md border bg-card text-[11px] font-semibold tabular-nums" title={`Chapter ${chapter.number} of ${count}`}>
            {chapter.number}
          </span>
          <span className="text-[13px] font-semibold" data-testid="journey-chapter-trigger">{chapter.trigger}</span>
          <span className="text-muted-foreground" aria-hidden="true">→</span>
          <span className={cn("rounded-full border px-2 text-[11.5px] font-semibold", TONE_RING[chapter.outcome.tone])} data-testid="journey-chapter-outcome">
            {chapter.outcome.text}
          </span>
          <span className="ml-auto text-[12px] tabular-nums text-muted-foreground">{chapterWhen(chapter.entries)}</span>
        </div>
      </div>
    </li>
  );
}

/** The syncs that read the row's history from the ingestion table into the ledger: bookkeeping of the timeline itself. */
function SyncLine({ syncs, table }: { syncs: DeliveryActivity[]; table: string }) {
  if (syncs.length === 0) {
    return null;
  }

  const shown = syncs.slice(0, 3);
  return (
    <div className="grid grid-cols-[auto_minmax(0,1fr)] items-baseline gap-x-2 border-t pt-2 text-[12px] text-muted-foreground" data-testid="journey-syncs">
      <span className="inline-flex items-center gap-1.5 self-start">
        <RefreshCw className="size-3.5" />
        <span>Timeline synced from <Mono>{table}</Mono>:</span>
      </span>
      <span className="flex min-w-0 flex-col gap-0.5">
        {shown.map((sync) => (
          <span key={sync.activityId} className="inline-flex flex-wrap items-baseline gap-x-1.5">
            <RelativeTime value={sync.startedUtc} />
            <span>{`by ${actorName(sync.actor)}${sync.summary !== null ? `, ${sync.summary}` : ""}`}</span>
            <RunRef runId={sync.runId} />
          </span>
        ))}
        {syncs.length > shown.length && <span>{`and ${syncs.length - shown.length} earlier`}</span>}
      </span>
    </div>
  );
}

type Row =
  | { kind: "chapter"; chapter: Chapter }
  | { kind: "entry"; entry: Entry; day: string | null };

/**
 * What happened to the record, newest first, in chapters: each opens with what set the record moving (its row arrived,
 * changed or was deleted, or someone asked for a redelivery, a release or a removal) and says how it ended, and its
 * steps sit on the rail's three lanes, the row's ingestion table, the ledger and OSDU, with a path between them. Times
 * share one column. A step someone asked for names who asked and how many records the request named. Every entry opens
 * to the rest known about it (the runs a change came from, the steps a try took and what they answered, a request's
 * parameters). The last thing that happened heads the list; where the record stands is the page header's and the
 * milestones' to say, and the latest chapter's band says it while work on the record is in flight. The syncs that read
 * the row's history into the ledger sit under the list. The list takes the rest of the page and scrolls inside it, so
 * its filter stays in view, and narrows to one lane, or to the steps someone asked for.
 */
export function RecordJourney({ record, attempts, activities, chain }: {
  record: DeliveryRecord;
  attempts: DeliveryAttempt[] | undefined;
  activities: DeliveryActivity[] | undefined;
  /** The changes of the record's row in its ingestion table; undefined while they are being read. */
  chain: DeliveryRecordChain | undefined;
}) {
  const [filter, setFilter] = useState<Filter>("all");
  const [opened, setOpened] = useState<ReadonlySet<string>>(() => new Set());
  const { cardRef, listRef, maxHeight } = useFillHeight();
  const loaded = attempts !== undefined && activities !== undefined;
  const story = useMemo(
    () => (loaded ? buildStory(record, attempts, activities, chain) : null),
    [loaded, record, attempts, activities, chain]);

  const all = useMemo(() => story?.chapters.flatMap((chapter) => chapter.entries) ?? [], [story]);
  const count = (f: Exclude<Filter, "all">) => all.filter((entry) => matches(entry, f)).length;

  const rows = useMemo(() => {
    if (story === null) {
      return [];
    }

    const list: Row[] = [];
    for (const chapter of [...story.chapters].reverse()) {
      const shown = chapter.entries.filter((entry) => matches(entry, filter)).reverse();
      if (shown.length === 0) {
        continue;
      }

      list.push({ kind: "chapter", chapter });
      const chapterDay = dayOf(chapter.entries[chapter.entries.length - 1].at);
      for (const entry of shown) {
        const day = dayOf(entry.at);
        list.push({ kind: "entry", entry, day: day === chapterDay ? null : day });
      }
    }

    return list;
  }, [story, filter]);

  const toggle = (id: string) => setOpened((current) => {
    const next = new Set(current);
    if (next.has(id)) {
      next.delete(id);
    } else {
      next.add(id);
    }

    return next;
  });

  // The path runs through the rows in the order they are listed; the last row with a node ends it.
  let lastNode = -1;
  rows.forEach((row, index) => {
    if (row.kind !== "chapter") {
      lastNode = index;
    }
  });

  const listed: ReactNode[] = [];
  let from: Lane | null = null;
  if (story !== null) {
    rows.forEach((row, index) => {
      switch (row.kind) {
        case "chapter":
          listed.push(<ChapterRow key={`chapter-${row.chapter.number}`} chapter={row.chapter} count={story.chapters.length} from={from} />);
          break;
        case "entry":
          listed.push(
            <EntryRow
              key={row.entry.id}
              entry={row.entry}
              from={from}
              last={index === lastNode}
              day={row.day}
              open={opened.has(row.entry.id)}
              onToggle={() => toggle(row.entry.id)}
            />,
          );
          from = row.entry.lane;
          break;
      }
    });
  }

  return (
    <Card ref={cardRef} className="gap-2 rounded-lg p-3" data-testid="record-journey">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <ToggleGroup
          type="single"
          value={filter}
          onValueChange={(value) => { if (value !== "") { setFilter(value as Filter); } }}
          variant="outline"
          size="sm"
          data-testid="record-journey-filter"
        >
          <ToggleGroupItem value="all" className="px-2.5 text-xs">{`All (${all.length})`}</ToggleGroupItem>
          <ToggleGroupItem value="source" className="px-2.5 text-xs">{`Source (${count("source")})`}</ToggleGroupItem>
          <ToggleGroupItem value="ledger" className="px-2.5 text-xs">{`Ledger (${count("ledger")})`}</ToggleGroupItem>
          <ToggleGroupItem value="osdu" className="px-2.5 text-xs">{`OSDU (${count("osdu")})`}</ToggleGroupItem>
          <ToggleGroupItem value="people" className="px-2.5 text-xs">{`Asked for (${count("people")})`}</ToggleGroupItem>
        </ToggleGroup>
        <span className="ml-auto text-[12px] text-muted-foreground">Newest first, in local time</span>
      </div>
      {story === null
        ? <Skeleton className="h-24 w-full rounded-md" />
        : rows.length === 0
          ? (
            <EmptyState
              title={filter === "all" ? "Nothing recorded yet" : EMPTY[filter].title}
              description={filter === "all" ? undefined : EMPTY[filter].description}
            />
          )
          : (
            <div ref={listRef} className="overflow-y-auto pr-1" style={{ maxHeight: maxHeight ?? MIN_LIST_HEIGHT }}>
              <div className={cn(ROW, "sticky top-0 z-[2] bg-card pb-0.5 pt-1")} aria-hidden="true">
                <span />
                <div className="relative h-5">
                  {LANES.map((lane) => (
                    <span key={lane} className="absolute top-0.5 -translate-x-1/2 whitespace-nowrap text-[9.5px] font-semibold uppercase tracking-wide text-muted-foreground" style={{ left: LANE_X[lane] }}>
                      {LANE_LABEL[lane]}
                    </span>
                  ))}
                </div>
                <span className="pl-1 pt-0.5 text-[11px] text-muted-foreground">where each step happened</span>
              </div>
              <ol className="flex flex-col" data-testid="record-journey-events">
                {listed}
              </ol>
            </div>
          )}
      {story !== null && <SyncLine syncs={story.syncs} table={story.table} />}
      {loaded && chain !== undefined && (chain.note !== null || chain.truncated) && (
        <p className="text-[12px] text-muted-foreground" data-testid="record-chain-note">
          {chain.truncated && "The row changed more often than the timeline lists: its newest changes and its arrival are shown. "}
          {chain.note}
        </p>
      )}
    </Card>
  );
}
