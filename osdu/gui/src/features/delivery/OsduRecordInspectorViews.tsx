// The OSDU record inspector's views of a record other than its JSON and its tables: the system fields, the access and
// legal, the records it links to, and a branch as rows of fields.

import { useMemo, useState, type ReactNode } from "react";
import { ChevronRight, Eye, Globe, Scale, UserRoundCog } from "lucide-react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { EmptyState } from "@/components/EmptyState";
import { RelativeTime } from "@/components/RelativeTime";
import { TruncatedText } from "@/components/TruncatedText";
import type { DeliveryOsduRead } from "../../api/delivery";
import { describeBranch, idParts, type RecordModel, type RecordNode } from "./osduRecordModel";
import { type OpenLink, text, texts } from "./osduRecordInspectorShared";
import { Chips, Highlight, Leaf, ReferenceLink } from "./OsduRecordInspectorValues";

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
export function RecordView({ read, record }: { read: DeliveryOsduRead; record: Record<string, unknown> }) {
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
export function AccessView({ record }: { record: Record<string, unknown> }) {
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
export function LinksView({ model, ownId, onOpenLink, opening, onSelect }: { model: RecordModel; ownId: string | null; onOpenLink?: OpenLink; opening?: string | null; onSelect: (path: string) => void }) {
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
export function FieldRows({ node, term, hits, ownId, onOpenLink, opening, onSelect, shown, onShowMore, actions }: {
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
