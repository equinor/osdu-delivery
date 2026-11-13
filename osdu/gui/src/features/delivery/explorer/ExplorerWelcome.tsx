import { useState, type ReactNode } from "react";
import { Braces, ChevronRight, Hash, History, Layers, Search, TextCursorInput, TextSearch, Trash2, Type } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { CopyButton } from "@/components/CopyButton";
import { EmptyState } from "@/components/EmptyState";
import { RelativeTime } from "@/components/RelativeTime";
import { cn } from "@/lib/utils";
import { idParts } from "../osduRecordModel";
import { ExplorerGrid, type GridColumn } from "./ExplorerGrid";
import { GroupGlyph } from "./ExplorerGlyphs";
import {
  forgetRecentRecords, kindParts, recentRecords, recentTypes, scopeLabel, type ExplorerScope, type RecentRecord, type RecentType,
} from "./explorerModel";

/** One panel of the welcome: a titled card, its glyph, a count and actions on one 40px line over what it holds. */
function Panel({ icon, title, count, actions, className, children, testId }: {
  icon: ReactNode;
  title: string;
  count?: number;
  actions?: ReactNode;
  className?: string;
  children: ReactNode;
  testId: string;
}) {
  return (
    <Card className={cn("min-h-0 gap-0 overflow-hidden rounded-lg p-0", className)} data-testid={testId}>
      <div className="flex h-10 shrink-0 items-center gap-2 border-b px-3">
        <span className="flex text-muted-foreground [&_svg]:size-4" aria-hidden>{icon}</span>
        <h2 className="text-[13px] font-medium">{title}</h2>
        {count !== undefined && count > 0 && <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{count}</span>}
        {actions !== undefined && <div className="ml-auto flex items-center gap-2">{actions}</div>}
      </div>
      {children}
    </Card>
  );
}

/** The group a remembered type sits in, and what tells it apart under its name: the group, or the kind's version. */
function typeFacts(scope: ExplorerScope): { group: string; detail: string } {
  switch (scope.level) {
    case "group":
      return { group: scope.group, detail: "every type in it" };
    case "type": {
      // A type naming no group is told apart by its namespace, as the list of types groups it.
      const group = kindParts(`*:*:${scope.entityType}:*`).group || scope.namespace || "";
      return { group, detail: group };
    }
    case "kind": {
      const parts = kindParts(scope.kind);
      return { group: parts.group, detail: `${parts.authority}:${parts.source}` };
    }
    default:
      return { group: "", detail: "" };
  }
}

/** What the search field takes, each with an example any partition answers and what Enter does with it. */
function searches(partition: string | null): { icon: ReactNode; what: string; example: string; does: string }[] {
  return [
    { icon: <Hash />, what: "A record id", example: `${partition ?? "dev"}:master-data--Wellbore:…`, does: "opens the record" },
    { icon: <TextCursorInput />, what: "The start of an id", example: "master-data--Well:", does: "ids starting with it" },
    { icon: <Type />, what: "A name or any text", example: "United States", does: "in every property" },
    { icon: <TextSearch />, what: "A value in one column", example: "In wellbore_uwi: UWI-0001", does: "pick it at the field's start" },
    { icon: <Braces />, what: "A Lucene query", example: "createTime:[2024-01-01 TO *]", does: "with { } on, sent as written" },
  ];
}

/**
 * Where the explorer opens: nothing read from OSDU until the reader asks. The records opened lately fill the main panel as
 * the grid the records of a type use, one click (or the arrow keys and Enter) from being opened again; beside them, every
 * type of the partition one click away with the types browsed lately under it, and what the search field takes. What is
 * remembered is this browser's own, so it shows at once however large the partition.
 */
export function ExplorerWelcome({ partition, onBrowseTypes, onOpenRecent, onOpenType }: {
  partition: string | null;
  onBrowseTypes: () => void;
  onOpenRecent: (record: RecentRecord) => void;
  onOpenType: (type: RecentType) => void;
}) {
  // Read once when the welcome shows: what this browser remembers, which changes only by what the reader does elsewhere.
  const [records, setRecords] = useState(() => recentRecords());
  const [types] = useState(() => recentTypes());
  const forget = () => {
    forgetRecentRecords();
    setRecords([]);
  };

  const columns: GridColumn<RecentRecord>[] = [
    {
      id: "name",
      header: "Name",
      flex: 2,
      render: (record) => {
        const parts = idParts(record.id);
        const group = record.kind !== null ? kindParts(record.kind).group : parts.group;
        return (
          <span className="flex min-w-0 items-center gap-2.5">
            <GroupGlyph group={group} />
            {record.name !== null
              ? <span className="min-w-0 truncate" title={record.name}>{record.name}</span>
              : <span className="min-w-0 truncate font-mono text-[12px] text-muted-foreground" title={record.id}>{parts.unique}</span>}
            {record.partition !== null && record.partition !== partition && (
              <span className="shrink-0 rounded-sm border px-1 font-mono text-[11px] text-muted-foreground" title={`Opened in ${record.partition}; opens there`}>
                {record.partition}
              </span>
            )}
          </span>
        );
      },
    },
    {
      id: "type",
      header: "Type",
      flex: 1.3,
      render: (record) => {
        const parts = record.kind !== null ? kindParts(record.kind) : null;
        return (
          <span className="min-w-0 truncate" title={record.kind ?? undefined}>
            {parts?.type ?? idParts(record.id).type}
            {parts !== null && <span className="ml-1.5 font-mono text-[11px] text-muted-foreground">{parts.version}</span>}
          </span>
        );
      },
    },
    {
      id: "id",
      header: "Id",
      flex: 1.1,
      render: (record) => (
        <span className="flex min-w-0 flex-1 items-center gap-1">
          {/* The end of an id is what tells it apart, so a narrow cell keeps the end and cuts the start. */}
          <span dir="rtl" className="min-w-0 flex-1 truncate text-left font-mono text-[12px] text-muted-foreground" title={record.id}>
            <bdi dir="ltr">{idParts(record.id).unique}</bdi>
          </span>
          <span className="opacity-0 transition-opacity group-hover/row:opacity-100 focus-within:opacity-100" onClick={(event) => event.stopPropagation()}>
            <CopyButton iconOnly label="Copy the id" text={record.id} testId="explorer-welcome-copy-id" />
          </span>
        </span>
      ),
    },
    {
      id: "opened",
      header: "Opened",
      width: 112,
      align: "right",
      render: (record) => (
        <span className="min-w-0 truncate text-[12px] text-muted-foreground">
          <RelativeTime value={record.openedUtc} absolute={false} />
        </span>
      ),
    },
  ];

  return (
    <div className="@container flex min-h-0 flex-1 flex-col" data-testid="explorer-welcome">
      {/* Side by side, the panels share the height; stacked on a narrow panel, each takes its own and the page scrolls. */}
      <div className="grid min-h-0 flex-1 gap-3 overflow-y-auto @3xl:auto-rows-[minmax(0,1fr)] @3xl:grid-cols-[minmax(0,1fr)_320px] @3xl:overflow-hidden @5xl:grid-cols-[minmax(0,1fr)_360px]">
        <Panel
          icon={<History />}
          title="Recently opened"
          count={records.length}
          actions={records.length > 0 && (
            <>
              <span className="text-[12px] text-muted-foreground">In this browser</span>
              <Button variant="ghost" size="xs" className="text-muted-foreground" onClick={forget} title="Forget the records opened in this browser" data-testid="explorer-welcome-forget">
                <Trash2 />
                Clear
              </Button>
            </>
          )}
          className="flex min-h-[280px] flex-col"
          testId="explorer-welcome-recent"
        >
          {records.length === 0
            ? (
              <div className="flex flex-1 items-center justify-center">
                <EmptyState
                  icon={<History />}
                  title="No records opened yet"
                  description="Records you open from a search or a type are listed here, newest first. Only this browser keeps them."
                  data-testid="explorer-welcome-recent-none"
                />
              </div>
            )
            : (
              <ExplorerGrid
                rows={records}
                columns={columns}
                rowKey={(record) => `${record.partition ?? ""}|${record.id}`}
                onOpen={onOpenRecent}
                label="Records opened lately"
                testId="explorer-welcome-grid"
                rowTestId="explorer-welcome-record"
              />
            )}
        </Panel>

        <div className="flex flex-col gap-3 @3xl:min-h-0">
          <Panel icon={<Layers />} title="Types" className="flex flex-col @3xl:min-h-0 @3xl:flex-1" testId="explorer-welcome-types">
            <div className="p-2">
              <button
                type="button"
                onClick={onBrowseTypes}
                className="group flex w-full items-center gap-3 rounded-md border bg-muted/40 px-3 py-2.5 text-left outline-none transition-colors hover:border-primary/40 hover:bg-accent/60 focus-visible:ring-2 focus-visible:ring-ring/50"
                data-testid="explorer-browse-types"
              >
                <span className="flex size-8 shrink-0 items-center justify-center rounded-md bg-primary/12 text-primary">
                  <Layers className="size-4" />
                </span>
                <span className="flex min-w-0 flex-1 flex-col">
                  <span className="text-[13px] font-medium">Browse every type</span>
                  <span className="truncate text-[12px] text-muted-foreground">{`Each type in ${partition ?? "the partition"}, counted`}</span>
                </span>
                <ChevronRight className="size-4 shrink-0 text-muted-foreground transition-colors group-hover:text-foreground" />
              </button>
            </div>
            {types.length > 0
              ? (
                <>
                  <h3 className="px-3 pt-1 pb-1 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Browsed lately</h3>
                  <div className="min-h-0 flex-1 overflow-y-auto px-1 pb-1">
                    {types.map((type) => {
                      const facts = typeFacts(type.scope);
                      return (
                        <button
                          key={type.kind}
                          type="button"
                          onClick={() => onOpenType(type)}
                          className="group flex h-8 w-full min-w-0 items-center gap-2.5 rounded-md px-2 text-left text-[13px] outline-none hover:bg-accent/60 focus-visible:ring-2 focus-visible:ring-ring/50"
                          title={type.kind}
                          data-testid="explorer-welcome-type"
                        >
                          <GroupGlyph group={facts.group} />
                          <span className="min-w-0 truncate">{scopeLabel(type.scope)}</span>
                          <span className={cn("min-w-0 flex-1 truncate text-[12px] text-muted-foreground", type.scope.level === "kind" && "font-mono text-[11px]")}>
                            {facts.detail}
                          </span>
                          <ChevronRight className="size-4 shrink-0 text-muted-foreground opacity-0 transition-opacity group-hover:opacity-100 group-focus-visible:opacity-100" />
                        </button>
                      );
                    })}
                  </div>
                </>
              )
              : <p className="px-3 pb-3 text-[12px] text-muted-foreground">The types you open are listed here, for the next visit.</p>}
          </Panel>

          <Panel icon={<Search />} title="Search syntax" className="shrink-0" testId="explorer-welcome-searches">
            <ul className="divide-y divide-border/60">
              {searches(partition).map((search) => (
                <li key={search.what} className="grid grid-cols-[16px_minmax(0,1fr)_auto] items-center gap-x-2.5 gap-y-1 px-3 py-2">
                  <span className="flex text-muted-foreground [&_svg]:size-4" aria-hidden>{search.icon}</span>
                  <span className="min-w-0 truncate text-[13px]">{search.what}</span>
                  <span className="text-right text-[12px] text-muted-foreground">{search.does}</span>
                  <code className="col-start-2 col-end-4 min-w-0 justify-self-start truncate rounded-sm bg-muted px-1.5 py-0.5 font-mono text-[11px] text-foreground/90 dark:bg-input/40">
                    {search.example}
                  </code>
                </li>
              ))}
            </ul>
          </Panel>
        </div>
      </div>
    </div>
  );
}
