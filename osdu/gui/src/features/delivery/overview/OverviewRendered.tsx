import { useMemo, useRef, useState, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import Markdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";
import { FileText, Loader2, SlidersHorizontal, TriangleAlert, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { CodeView } from "@/components/CodeView";
import { CopyButton } from "@/components/CopyButton";
import { cn } from "@/lib/utils";
import { deliveryApi } from "../../../api/delivery";
import { ProblemView } from "../TemplateSheet";
import { ValueCheckScope } from "../ValueCheckBar";
import { flowScope, type ValueCheckSession } from "../useValueCheck";
import type { OverviewPart } from "./OverviewCanvas";
import { PartLineageButton } from "./OverviewLineage";
import type { PartTarget } from "./partTargets";
import type { OutlineCard } from "./outlineModel.ts";
import { recordMarkdown, recordSections, type RecordSection } from "./recordMarkdown.ts";

/**
 * One staged record rendered, as a delivery would render it now: the record the canvas shows, or with none shown the scope's
 * first. The canvas's own preview of the record shown reads the same, so the two ask once between them. Nothing is sent.
 */
function useRendered(session: ValueCheckSession, recordKey: string | null) {
  const { flow, ready, sentValues } = session;
  const preview = useQuery({
    queryKey: ["delivery", "overview-record", flow?.pipelineId ?? null, flow?.interface ?? null, flow?.partition ?? null, recordKey, sentValues],
    queryFn: () => deliveryApi.preview(flow!.pipelineId, { key: recordKey, values: sentValues }, flowScope(flow!)),
    enabled: flow !== null && ready,
    staleTime: 30000,
    retry: false,
  });
  const blocked = flow === null ? "No flow renders with this mapping yet, so nothing renders." : ready ? null : session.blocked;
  return { preview, blocked };
}

/** The line over a panel under the canvas: what it shows, where it comes from, and its own controls. */
function Heading({ glyph, title, said, loading, children, onClose, testId }: {
  glyph: ReactNode;
  title: string;
  said: ReactNode;
  loading: boolean;
  children?: ReactNode;
  onClose: () => void;
  testId: string;
}) {
  return (
    <header className="flex min-w-0 shrink-0 flex-wrap items-center gap-x-3 gap-y-1 border-b border-border px-3 py-1.5 text-[12.5px]">
      <span className="flex min-w-0 items-center gap-1.5">
        {glyph}
        <span className="truncate font-mono font-medium">{title}</span>
      </span>
      <span className="min-w-0 text-muted-foreground" data-testid={`${testId}-said`}>{said}</span>
      {loading && <Loader2 className="size-3.5 animate-spin text-muted-foreground" aria-label="Rendering the record" />}
      <span className="ml-auto flex items-center gap-2">
        {children}
        <Button variant="ghost" size="icon-xs" aria-label="Close" title="Close" onClick={onClose} data-testid={`${testId}-close`}><X /></Button>
      </span>
    </header>
  );
}

/** Why nothing is shown yet: a scope still to settle, a failed read, or a record that did not render. */
function Unshown({ blocked, error, reason, testId }: { blocked: string | null | undefined; error: unknown; reason: string | null; testId: string }) {
  if (blocked !== null && blocked !== undefined) {
    return <p className="px-3 py-2 text-[12.5px] text-muted-foreground" data-testid={`${testId}-blocked`}>{blocked}</p>;
  }

  if (error !== null && error !== undefined) {
    return <div className="p-3"><ProblemView error={error} testId={`${testId}-error`} /></div>;
  }

  return reason === null ? null : <p className="px-3 py-2 text-[12.5px] text-muted-foreground" data-testid={`${testId}-unrendered`}>{reason}</p>;
}

/**
 * The values the flow's parameters render with, under the canvas: the ones the mapping reads first, in the order the table on
 * the canvas lists them, then any other the mapping declares. They are read from one record's render, so they are the values
 * that reach a record, every reference resolved. Pointing at one lights its lines, and a click opens its details.
 */
export function OverviewParameters({ card, focus, session, recordKey, lineage, onPoint, onPick, onClose }: {
  card: OutlineCard;
  focus: string | null;
  session: ValueCheckSession;
  recordKey: string | null;
  /** The flow supplying the values, in lineage. */
  lineage: readonly PartTarget[];
  onPoint: (part: OverviewPart | null) => void;
  onPick: (part: OverviewPart) => void;
  onClose: () => void;
}) {
  const { preview, blocked } = useRendered(session, recordKey);
  const given = preview.data?.inputs.parameters ?? null;
  const rows = useMemo(() => {
    if (given === null) {
      return [];
    }

    const read = card.fields.map((field) => field.name);
    const rest = Object.keys(given).filter((name) => !read.includes(name)).sort((a, b) => a.localeCompare(b));
    return [...read, ...rest].map((name) => ({ name, value: given[name] ?? null, read: read.includes(name) }));
  }, [card.fields, given]);

  return (
    <section className="flex h-full min-h-0 flex-col bg-card" aria-label="The parameters' values" data-testid="mapping-overview-parameters">
      <Heading
        glyph={<SlidersHorizontal className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />}
        title={card.title}
        said={given === null ? "" : `The values the mapping renders with${session.flow === null ? "" : ` for ${session.flow.flow}`}`}
        loading={preview.isFetching}
        onClose={onClose}
        testId="mapping-overview-parameters"
      >
        <ValueCheckScope session={session} />
        <PartLineageButton targets={lineage} named={card.title} />
      </Heading>
      <Unshown blocked={blocked} error={preview.error} reason={preview.data !== undefined && given === null ? preview.data.reason ?? "The record did not render, so its parameters were not read." : null} testId="mapping-overview-parameters" />
      {rows.length > 0 && (
        <div className="min-h-0 flex-1 overflow-auto">
          <table className="border-separate border-spacing-0 text-[12px]">
            <thead>
              <tr>
                <th scope="col" className="sticky top-0 border-b border-border bg-card px-3 py-1 text-left font-medium text-muted-foreground">Parameter</th>
                <th scope="col" className="sticky top-0 border-b border-border bg-card px-3 py-1 text-left font-medium text-muted-foreground">Value</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => {
                const part: OverviewPart = { type: "field", source: card.id, field: row.name };
                return (
                  <tr
                    key={row.name}
                    className={cn("cursor-pointer hover:bg-accent/60", row.name === focus && "bg-accent/50")}
                    onMouseEnter={row.read ? () => onPoint(part) : undefined}
                    onMouseLeave={row.read ? () => onPoint(null) : undefined}
                    onClick={row.read ? () => onPick(part) : undefined}
                    data-testid={`mapping-overview-parameter-${row.name}`}
                  >
                    <td className={cn("border-b border-border/60 px-3 py-1 font-mono whitespace-nowrap", !row.read && "text-muted-foreground")}>{row.name}</td>
                    <td className="border-b border-border/60 px-3 py-1 font-mono [overflow-wrap:anywhere]">
                      {row.value === null || row.value === "" ? <span className="text-muted-foreground/60 italic">none</span> : row.value}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

/** A node of the Markdown's syntax tree, as far as a heading's or a cell's words are read from it. */
interface MarkdownNode {
  type: string;
  value?: string;
  children?: MarkdownNode[];
}

const textOf = (node: MarkdownNode | undefined): string => (node === undefined ? "" : node.value ?? (node.children ?? []).map(textOf).join(""));

/** A property table's heading row, `Property | Value`: kept in the Markdown a reader copies, left off the page, where it says nothing. */
const isPropertyHead = (node: MarkdownNode | undefined) => {
  const cells = node?.children?.find((child) => child.type === "element")?.children?.filter((child) => child.type === "element") ?? [];
  return cells.length === 2 && textOf(cells[0]) === "Property" && textOf(cells[1]) === "Value";
};

/** How the rendered record's Markdown is drawn: the workbench's type, quiet section headings, property names muted, data in mono. */
const MARKDOWN: Components = {
  h1: ({ children }) => <h1 className="mb-2 font-mono text-[14px] font-semibold [overflow-wrap:anywhere]">{children}</h1>,
  h2: ({ children, node }) => (
    <h2 data-section={textOf(node as MarkdownNode | undefined)} className="mt-5 mb-1 scroll-mt-2 border-b border-border pb-1 font-mono text-[12.5px] font-semibold first:mt-0">{children}</h2>
  ),
  h3: ({ children, node }) => (
    <h3 data-section={textOf(node as MarkdownNode | undefined)} className="mt-4 mb-1 scroll-mt-2 font-mono text-[12px] font-medium text-muted-foreground">{children}</h3>
  ),
  p: ({ children }) => <p className="my-1.5 text-[12.5px] text-muted-foreground [overflow-wrap:anywhere]">{children}</p>,
  em: ({ children }) => <em className="text-muted-foreground/70">{children}</em>,
  table: ({ children }) => <div className="max-w-full overflow-x-auto"><table className="border-separate border-spacing-0 text-[12px]">{children}</table></div>,
  thead: ({ children, node }) => (isPropertyHead(node as MarkdownNode | undefined) ? null : <thead>{children}</thead>),
  th: ({ children, style }) => <th style={style} className="border-b border-border px-2 py-1 text-left font-medium whitespace-nowrap text-muted-foreground">{children}</th>,
  td: ({ children, style }) => (
    <td style={style} className="max-w-[40rem] border-b border-border/50 px-2 py-[3px] align-top font-mono [overflow-wrap:anywhere] first:pr-6 first:whitespace-nowrap first:text-muted-foreground">
      {children}
    </td>
  ),
};

/** The parts of the record beside its document, each a click from its place in it, the part being read marked. */
function Outline({ sections, active, onGo }: { sections: RecordSection[]; active: string | null; onGo: (heading: string) => void }) {
  return (
    <nav className="hidden w-52 shrink-0 overflow-y-auto border-r border-border py-2 @2xl/document:block" aria-label="The record's parts" data-testid="mapping-overview-document-outline">
      <ul className="flex flex-col">
        {sections.map((section) => (
          <li key={section.heading}>
            <button
              type="button"
              aria-current={section.heading === active ? "true" : undefined}
              onClick={() => onGo(section.heading)}
              className={cn(
                "flex w-full min-w-0 items-center gap-2 py-1 pr-3 text-left font-mono text-[12px] outline-none hover:bg-accent/60 focus-visible:bg-accent/60",
                section.level === 2 ? "pl-3" : "pl-6 text-muted-foreground",
                section.heading === active && "bg-accent text-foreground",
              )}
            >
              <span className="min-w-0 truncate">{section.level === 3 ? section.heading.replace(/ \(\d[\d,]*\)$/, "") : section.heading}</span>
              <span className="ml-auto shrink-0 text-[11px] text-muted-foreground tabular-nums">{section.count.toLocaleString("en-US")}</span>
            </button>
          </li>
        ))}
      </ul>
    </nav>
  );
}

/**
 * One staged record as the mapping renders it, under the canvas: as a Markdown document a person reads, or as the JSON a
 * delivery would send, either copied whole. A record the render holds says why above it. Nothing is sent.
 */
export function OverviewDocument({ title, session, recordKey, lineage, onClose }: {
  /** The record's kind as the canvas heads it. */
  title: string;
  session: ValueCheckSession;
  recordKey: string | null;
  /** The record's kind, in lineage. */
  lineage: readonly PartTarget[];
  onClose: () => void;
}) {
  const [shown, setShown] = useState<"markdown" | "json">("markdown");
  const [active, setActive] = useState<string | null>(null);
  const page = useRef<HTMLDivElement>(null);
  const { preview, blocked } = useRendered(session, recordKey);
  const document = preview.data?.document ?? null;
  const rendered = document?.rendered ?? null;
  const name = preview.data?.source?.label ?? preview.data?.source?.sourceKey ?? title;
  const markdown = useMemo(() => (rendered === null ? "" : recordMarkdown(rendered, name)), [rendered, name]);
  const sections = useMemo(() => (rendered === null ? [] : recordSections(rendered)), [rendered]);
  const json = useMemo(() => (rendered === null ? "" : JSON.stringify(rendered, null, 2)), [rendered]);

  const headings = () => [...(page.current?.querySelectorAll<HTMLElement>("[data-section]") ?? [])];
  const go = (heading: string) => headings().find((element) => element.dataset.section === heading)?.scrollIntoView({ block: "start" });

  // The part being read is the last whose heading has scrolled to the top of the page.
  const onScroll = () => {
    const top = page.current?.getBoundingClientRect().top ?? 0;
    const passed = headings().filter((element) => element.getBoundingClientRect().top - top <= 16);
    setActive(passed.at(-1)?.dataset.section ?? sections[0]?.heading ?? null);
  };

  return (
    <section className="flex h-full min-h-0 flex-col bg-card" aria-label="The rendered record" data-testid="mapping-overview-document">
      <Heading
        glyph={<FileText className="size-3.5 shrink-0 text-chart-1" aria-hidden />}
        title={title}
        said={rendered === null ? "" : `${recordKey === null ? "The scope's first row" : "The record shown"}, as a delivery renders it. Nothing is sent.`}
        loading={preview.isFetching}
        onClose={onClose}
        testId="mapping-overview-document"
      >
        {rendered !== null && (
          <>
            <span className="flex items-center rounded-md border border-border p-0.5" role="group" aria-label="Shown as">
              {(["markdown", "json"] as const).map((as) => (
                <Button
                  key={as}
                  variant={shown === as ? "secondary" : "ghost"}
                  size="xs"
                  aria-pressed={shown === as}
                  onClick={() => setShown(as)}
                  data-testid={`mapping-overview-document-as-${as}`}
                >
                  {as === "markdown" ? "Document" : "JSON"}
                </Button>
              ))}
            </span>
            {/* The JSON's viewer copies it from its own toolbar. */}
            {shown === "markdown" && <CopyButton label="Copy as Markdown" text={markdown} iconOnly testId="mapping-overview-document-copy" />}
          </>
        )}
        <ValueCheckScope session={session} />
        <PartLineageButton targets={lineage} named={title} />
      </Heading>
      <Unshown
        blocked={blocked}
        error={preview.error}
        reason={preview.data !== undefined && rendered === null ? preview.data.reason ?? "The record did not render." : null}
        testId="mapping-overview-document"
      />
      {document !== null && document.held && (
        <p className="flex shrink-0 items-start gap-1.5 border-b border-border px-3 py-1.5 text-[12.5px] text-warning" data-testid="mapping-overview-document-held">
          <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden />
          <span>A delivery would hold this record: {document.holds.join("; ")}</span>
        </p>
      )}
      {rendered !== null && shown === "markdown" && (
        <div className="@container/document flex min-h-0 flex-1">
          <Outline sections={sections} active={active ?? sections[0]?.heading ?? null} onGo={go} />
          <div ref={page} onScroll={onScroll} className="min-h-0 min-w-0 flex-1 overflow-auto px-5 py-3" data-testid="mapping-overview-document-markdown">
            <Markdown remarkPlugins={[remarkGfm]} components={MARKDOWN}>{markdown}</Markdown>
          </div>
        </div>
      )}
      {rendered !== null && shown === "json" && (
        <div className="flex min-h-0 flex-1 flex-col p-2" data-testid="mapping-overview-document-json">
          <CodeView value={json} language="json" fill readOnly />
        </div>
      )}
    </section>
  );
}
