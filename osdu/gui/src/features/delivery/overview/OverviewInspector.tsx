import type { ReactNode } from "react";
import { ArrowRight, Check, CircleDot, Database, KeyRound, Search, ShieldCheck, SlidersHorizontal, SquareFunction, Table2, X } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import type { MappingOutline, MappingOutlineFlow, MappingOutlineNode, MappingOutlineStep } from "../../../api/delivery";
import type { OverviewPart } from "./OverviewCanvas";
import { cardsOf, fieldId, nodeAt, readersOf, type OutlineCard } from "./outlineModel.ts";
import { cardPreviewLabel } from "./tableRows.ts";

/** A section of the panel: a quiet heading over what it holds. */
function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="flex min-w-0 flex-col gap-1.5">
      <h3 className="text-[12px] font-medium text-muted-foreground">{title}</h3>
      {children}
    </section>
  );
}

/** One step of a value's way: a mark on a line, what it is, and what it does. */
function Stage({ glyph, title, children, last = false }: { glyph: ReactNode; title: ReactNode; children?: ReactNode; last?: boolean }) {
  return (
    <li className="relative flex min-w-0 gap-2 pb-2.5">
      {!last && <span className="absolute top-5 bottom-0 left-[7.5px] w-px bg-border" aria-hidden />}
      <span className="mt-0.5 flex size-4 shrink-0 items-center justify-center text-muted-foreground">{glyph}</span>
      <span className="flex min-w-0 flex-col gap-0.5">
        <span className="min-w-0 font-mono text-[12px] [overflow-wrap:anywhere]">{title}</span>
        {children !== undefined && <span className="text-[12px] text-muted-foreground">{children}</span>}
      </span>
    </li>
  );
}

function sourceName(cards: readonly OutlineCard[], id: string): string {
  return cards.find((card) => card.id === id)?.title ?? id;
}

/** What a step reads, as one short phrase. */
function stepGlyph(step: MappingOutlineStep): ReactNode {
  if (step.kind === "find" && step.label === "search") {
    return <Search className="size-3.5" aria-hidden />;
  }

  if (step.lookups.length > 0) {
    return <Database className="size-3.5" aria-hidden />;
  }

  return step.kind === "expr" ? <SquareFunction className="size-3.5" aria-hidden /> : <CircleDot className="size-3.5" aria-hidden />;
}

/** A value's way to its property, in the order a render takes it. */
function Way({ flow, node, cards, value }: { flow: MappingOutlineFlow; node: MappingOutlineNode; cards: readonly OutlineCard[]; value: string | undefined }) {
  const reads = flow.reads.filter((read) => read.role !== "condition");
  return (
    <ol className="flex min-w-0 flex-col" data-testid="mapping-overview-way">
      {reads.map((read) => (
        <Stage key={fieldId(read.source, read.field)} glyph={<Table2 className="size-3.5" aria-hidden />} title={read.field}>
          {`${read.role === "match" ? "Compared, from" : "Read from"} ${sourceName(cards, read.source)}`}
        </Stage>
      ))}
      {flow.origin === "literal" && (
        <Stage glyph={<CircleDot className="size-3.5" aria-hidden />} title={flow.literal}>Written as it stands, on every record.</Stage>
      )}
      {flow.steps.map((step, index) => (
        <Stage key={`${index}-${step.kind}`} glyph={stepGlyph(step)} title={step.text}>{step.detail}</Stage>
      ))}
      <Stage last glyph={<ArrowRight className="size-3.5" aria-hidden />} title={node.path.split("|")[0]}>
        {value === undefined ? (flow.unverified ? "Written even when the cache holds no such record." : undefined) : <>In this record: <span className="font-mono text-foreground [overflow-wrap:anywhere]">{value}</span></>}
      </Stage>
    </ol>
  );
}

/** The lines of the document that write a node. */
function Written({ node, lines }: { node: MappingOutlineNode; lines: readonly string[] }) {
  if (node.line === null || node.line === undefined || node.endLine === null || node.endLine === undefined) {
    return null;
  }

  const shown = lines.slice(node.line - 1, node.endLine);
  const indent = Math.min(...shown.filter((line) => line.trim() !== "").map((line) => line.length - line.trimStart().length));
  return (
    <Section title={`In the document, lines ${node.line} to ${node.endLine}`}>
      <pre className="max-h-56 overflow-auto rounded-md border border-border bg-muted/40 px-2.5 py-1.5 font-mono text-[11.5px] leading-[18px]" data-testid="mapping-overview-written">
        {shown.map((line) => line.slice(indent)).join("\n")}
      </pre>
    </Section>
  );
}

function RowPanel({ node, cards, lines, values, onPick }: {
  node: MappingOutlineNode;
  cards: readonly OutlineCard[];
  lines: readonly string[];
  values: ReadonlyMap<string, string>;
  onPick: (part: OverviewPart) => void;
}) {
  const siblings = node.shape === "choice" ? node.children : [];
  return (
    <>
      {node.description !== null && node.description !== undefined && node.description !== "" && <p className="text-[12.5px] text-muted-foreground">{node.description}</p>}
      {node.when !== null && node.when !== undefined && (
        <Section title="Only when">
          <p className="font-mono text-[12px] [overflow-wrap:anywhere]">{node.when}</p>
        </Section>
      )}
      {node.flow !== null && node.flow !== undefined && (
        <Section title="How it is filled">
          <Way flow={node.flow} node={node} cards={cards} value={values.get(node.path)} />
        </Section>
      )}
      {siblings.length > 0 && (
        <Section title="The first that gives a value">
          {values.has(node.path) && <p className="text-[12px] text-muted-foreground">In this record: <span className="font-mono text-foreground [overflow-wrap:anywhere]">{values.get(node.path)}</span></p>}
          <ol className="flex min-w-0 flex-col gap-2">
            {siblings.map((alternative) => {
              const said = values.get(alternative.path);
              return (
                <li key={alternative.path} className={cn("min-w-0 rounded-md border border-border p-2", said === "gave the value" && "border-primary/60")} data-testid="mapping-overview-alternative">
                  <button type="button" className="mb-1 flex w-full items-center gap-1.5 text-left text-[12px] font-medium" onClick={() => onPick({ type: "row", path: alternative.path })}>
                    <span>{`Alternative ${alternative.name}`}</span>
                    {said === "gave the value" && <Check className="size-3.5 text-success" aria-label="Gave the value" />}
                    {said !== undefined && said !== "gave the value" && <span className="font-normal text-muted-foreground">{said}</span>}
                  </button>
                  {alternative.flow !== null && alternative.flow !== undefined && <Way flow={alternative.flow} node={alternative} cards={cards} value={undefined} />}
                </li>
              );
            })}
          </ol>
        </Section>
      )}
      {node.assertions.length > 0 && (
        <Section title="Rules on this value">
          <ul className="flex min-w-0 flex-col gap-1.5">
            {node.assertions.map((rule) => (
              <li key={rule.label} className="flex min-w-0 items-start gap-1.5 text-[12px]">
                <ShieldCheck className="mt-0.5 size-3.5 shrink-0 text-muted-foreground" aria-hidden />
                <span className="min-w-0">
                  <span className="font-medium">{rule.label}</span>
                  <span className="block font-mono text-[11.5px] text-muted-foreground [overflow-wrap:anywhere]">{rule.expected}</span>
                  <span className="block text-muted-foreground">{rule.onFail === "hold" ? "A record that breaks it is held." : rule.onFail === "report" ? "A record that breaks it is sent, and the failure recorded." : "A value that breaks it is left out."}</span>
                </span>
              </li>
            ))}
          </ul>
        </Section>
      )}
      <Written node={node} lines={lines} />
    </>
  );
}

/** A property that reads a field, as a link to it. */
function PropertyLink({ node, onPick }: { node: MappingOutlineNode; onPick: (part: OverviewPart) => void }) {
  return (
    <li>
      <button
        type="button"
        className="w-full truncate rounded-sm px-1 py-0.5 text-left font-mono text-[12px] outline-none hover:bg-accent/60 focus-visible:bg-accent/60"
        title={node.path}
        onClick={() => onPick({ type: "row", path: node.path })}
      >
        {node.path}
      </button>
    </li>
  );
}

function FieldPanel({ card, name, outline, value, onPick }: { card: OutlineCard; name: string; outline: MappingOutline; value: string | undefined; onPick: (part: OverviewPart) => void }) {
  const field = card.fields.find((candidate) => candidate.name === name);
  const readers = readersOf(outline, card.id, name);
  return (
    <>
      <p className="text-[12.5px] text-muted-foreground">
        {`A ${card.source.kind === "cache" ? "field" : card.source.kind === "parameters" ? "parameter" : "column"} of ${card.title}.`}
        {field?.type !== null && field?.type !== undefined && <> Type <span className="font-mono text-foreground">{field.type}</span>.</>}
      </p>
      {value !== undefined && (
        <Section title="In this record">
          <p className="font-mono text-[12px] [overflow-wrap:anywhere]">{value}</p>
        </Section>
      )}
      {field?.key === true && <p className="flex items-center gap-1.5 text-[12px]"><KeyRound className="size-3.5 text-muted-foreground" aria-hidden />Part of the record's key: it decides which record a row is.</p>}
      {field?.displayOnly === true && <p className="text-[12px] text-muted-foreground">Only the label or the identity read it. It names the record to a person, and never reaches the record.</p>}
      {readers.length > 0 && (
        <Section title={`Reaches ${readers.length} ${readers.length === 1 ? "property" : "properties"}`}>
          <ul className="flex min-w-0 flex-col">{readers.map((node) => <PropertyLink key={node.path} node={node} onPick={onPick} />)}</ul>
        </Section>
      )}
    </>
  );
}

function CardPanel({ card, outline, onPick }: { card: OutlineCard; outline: MappingOutline; onPick: (part: OverviewPart) => void }) {
  const readers = readersOf(outline, card.id, null);
  return (
    <>
      <p className="text-[12.5px] text-muted-foreground">{card.source.detail ?? card.role}</p>
      {readers.length > 0 && (
        <Section title={`Reaches ${readers.length} ${readers.length === 1 ? "property" : "properties"}`}>
          <ul className="flex min-w-0 flex-col" data-testid="mapping-overview-readers">{readers.map((node) => <PropertyLink key={node.path} node={node} onPick={onPick} />)}</ul>
        </Section>
      )}
      <Section title={`Read by the mapping, ${card.fields.length}`}>
        <ul className="flex min-w-0 flex-col">
          {card.fields.map((field) => (
            <li key={field.id}>
              <button
                type="button"
                className="flex w-full min-w-0 items-center gap-1.5 rounded-sm px-1 py-0.5 text-left font-mono text-[12px] outline-none hover:bg-accent/60 focus-visible:bg-accent/60"
                onClick={() => onPick({ type: "field", source: card.id, field: field.name })}
              >
                <span className="min-w-0 truncate">{field.name}</span>
                {field.type !== null && <span className="ml-auto shrink-0 text-[11px] text-muted-foreground">{field.type}</span>}
              </button>
            </li>
          ))}
        </ul>
      </Section>
      {card.unread.length > 0 && (
        <Section title={`Not read, ${card.unread.length}`}>
          <ul className="flex min-w-0 flex-col" data-testid="mapping-overview-unread">
            {card.unread.map((column) => (
              <li key={column.name} className="flex min-w-0 items-center gap-1.5 px-1 py-0.5 font-mono text-[12px] text-muted-foreground">
                <span className="min-w-0 truncate">{column.name}</span>
                {column.dataType !== null && <span className="ml-auto shrink-0 text-[11px]">{column.dataType}</span>}
              </li>
            ))}
          </ul>
        </Section>
      )}
    </>
  );
}

export const INSPECTOR_WIDTH = 380;

/**
 * What was picked on the canvas, sliding in from the right without taking the canvas over: the way a value goes to a property, a
 * field and what it reaches, or a table and what is read of it.
 */
export function OverviewInspector({ part, outline, tables, cached, yaml, values, onPick, onPreview, onClose }: {
  part: OverviewPart | null;
  outline: MappingOutline;
  tables: Parameters<typeof cardsOf>[2];
  cached: Parameters<typeof cardsOf>[3];
  yaml: string;
  values: ReadonlyMap<string, string>;
  onPick: (part: OverviewPart) => void;
  /** Shows the rows of the table picked, or of the table holding the field picked. */
  onPreview: (part: OverviewPart) => void;
  onClose: () => void;
}) {
  const cards = cardsOf(outline, new Set(), tables, cached);
  const lines = yaml.replace(/\r\n/g, "\n").split("\n");
  const node = part?.type === "row" ? nodeAt(outline, part.path) : null;
  const card = part !== null && part.type !== "row" ? cards.find((candidate) => candidate.id === part.source) ?? null : null;
  const preview = card === null ? null : cardPreviewLabel(card);
  const title = node !== null ? node.path.split("|")[0] : part?.type === "field" ? part.field : card?.title ?? "";
  const leaf = title.lastIndexOf(".");

  return (
    <aside
      className={cn(
        "absolute inset-y-0 right-0 z-10 flex max-w-[85%] flex-col border-l border-border bg-card shadow-md transition-transform duration-200 ease-out motion-reduce:transition-none",
        part === null ? "translate-x-full" : "translate-x-0",
      )}
      style={{ width: INSPECTOR_WIDTH }}
      aria-hidden={part === null}
      data-testid="mapping-overview-inspector"
    >
      {part !== null && (
        <>
          <header className="flex min-w-0 items-start gap-2 border-b border-border px-3 py-2">
            <div className="flex min-w-0 flex-1 flex-col gap-1">
              <h2 className="min-w-0 font-mono text-[13px] [overflow-wrap:anywhere]">
                {leaf > 0 && <span className="text-muted-foreground">{title.slice(0, leaf + 1)}</span>}
                <span className="font-semibold">{leaf > 0 ? title.slice(leaf + 1) : title}</span>
              </h2>
              {node !== null && (
                <span className="flex flex-wrap items-center gap-1">
                  {node.type !== null && node.type !== undefined && <Badge variant="outline" className="font-mono text-[10.5px] font-normal">{node.type}</Badge>}
                  {!node.required && <Badge variant="outline" className="text-[10.5px] font-normal">optional</Badge>}
                  {node.flow?.unverified === true && <Badge variant="outline" className="text-[10.5px] font-normal">unverified</Badge>}
                </span>
              )}
            </div>
            {preview !== null && (
              <Button variant="ghost" size="icon-xs" aria-label={preview} title={preview} onClick={() => onPreview(part)} data-testid="mapping-overview-inspector-preview">
                {card?.source.kind === "parameters" ? <SlidersHorizontal /> : <Table2 />}
              </Button>
            )}
            <Button variant="ghost" size="icon-xs" aria-label="Close" onClick={onClose} data-testid="mapping-overview-inspector-close"><X /></Button>
          </header>
          <div className="flex min-h-0 flex-1 flex-col gap-3 overflow-y-auto p-3">
            {node !== null && <RowPanel node={node} cards={cards} lines={lines} values={values} onPick={onPick} />}
            {part.type === "field" && card !== null && <FieldPanel card={card} name={part.field} outline={outline} value={values.get(fieldId(part.source, part.field))} onPick={onPick} />}
            {part.type === "card" && card !== null && <CardPanel card={card} outline={outline} onPick={onPick} />}
          </div>
        </>
      )}
    </aside>
  );
}
