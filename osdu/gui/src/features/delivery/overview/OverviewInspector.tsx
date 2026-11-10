import { Fragment, type ReactNode } from "react";
import { Check, KeyRound, ShieldCheck, SlidersHorizontal, Table2, X } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import type { MappingOutline, MappingOutlineFlow, MappingOutlineNode, MappingOutlineRowRule } from "../../../api/delivery";
import { lastStepStart, outlinePathOf } from "../recordPath";
import type { OverviewPart } from "./OverviewCanvas";
import { PartLineageButton, SourceGlyph } from "./OverviewLineage";
import type { PartTarget } from "./partTargets";
import { cardsOf, fieldId, lookupOrigin, nodeAt, readersOf, wayOf, type OutlineCard, type WayStage } from "./outlineModel.ts";
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

function sourceName(cards: readonly OutlineCard[], id: string): string {
  return cards.find((card) => card.id === id)?.title ?? id;
}

/** A value of the record shown, under what holds it. */
function Shown({ value }: { value: string | undefined }) {
  return value === undefined ? null : <span className="block min-w-0 font-mono text-[11.5px] text-muted-foreground [overflow-wrap:anywhere]" data-testid="mapping-overview-way-value">{value}</span>;
}

/** A dotted name that breaks after a dot when it has to, never inside a step: `NameAliases.AliasName`. */
function dotted(name: string): ReactNode {
  return name.split(".").map((part, index) => (index === 0 ? part : <Fragment key={index}>.<wbr />{part}</Fragment>));
}

/** A field of a source, picked as the canvas picks it. */
function FieldButton({ source, field, cards, onPick }: { source: string; field: string; cards: readonly OutlineCard[]; onPick: (part: OverviewPart) => void }) {
  return (
    <button
      type="button"
      className="min-w-0 rounded-sm text-left font-mono text-[12px] break-words outline-none hover:underline focus-visible:underline"
      title={`${field}, of ${sourceName(cards, source)}`}
      onClick={() => onPick({ type: "field", source, field })}
    >
      {dotted(field)}
    </button>
  );
}

/** One stage of a value's way: a mark on the rail, what the stage is, and what it holds. */
function Stage({ title, aside, last = false, children }: { title: ReactNode; aside?: ReactNode; last?: boolean; children?: ReactNode }) {
  return (
    <li className="relative flex min-w-0 gap-2.5 pb-3 last:pb-0" data-testid="mapping-overview-way-stage">
      {!last && <span className="absolute top-3.5 bottom-0 left-[3.5px] w-px bg-border" aria-hidden />}
      <span className="mt-[5px] size-2 shrink-0 rounded-full border border-muted-foreground/70 bg-card" aria-hidden />
      <div className="flex min-w-0 flex-1 flex-col gap-1">
        <h4 className="flex min-w-0 flex-wrap items-baseline gap-x-1.5 text-[12px] font-medium">
          {title}
          {aside !== undefined && <span className="font-normal text-muted-foreground">{aside}</span>}
        </h4>
        {children}
      </div>
    </li>
  );
}

/** What a lookup compares, line by line: the field of the table it looks in, and the column or text that field has to hold. */
function Compared({ stage, cards, values, onPick }: {
  stage: Extract<WayStage, { kind: "compared" }>;
  cards: readonly OutlineCard[];
  values: ReadonlyMap<string, string>;
  onPick: (part: OverviewPart) => void;
}) {
  const { lookup, lines } = stage;
  // The other side's heading: the table every line compares a column of, else what it is.
  const sources = new Set(lines.map((line) => line.operand.read?.source ?? null));
  const [only] = sources;
  const against = sources.size === 1 && only !== null && only !== undefined ? sourceName(cards, only) : "Compared with";
  // A leading column numbers the lines, tried in turn, when there are several, and says "and" before a comparison that
  // joins the one before it on the same line.
  const numbered = lines.length > 1;
  const lineCount = lines.filter((line) => !line.and).length;
  const lineNumbers = lines.map((_, index) => lines.slice(0, index + 1).filter((line) => !line.and).length);
  // A row's value is shown once, at the first line comparing its column: the lines after it compare the same value.
  const shownAt = new Map<string, number>();
  lines.forEach((line, index) => {
    if (line.operand.read !== null && !shownAt.has(fieldId(line.operand.read.source, line.operand.read.field))) {
      shownAt.set(fieldId(line.operand.read.source, line.operand.read.field), index);
    }
  });
  return (
    <div
      className={cn(
        "grid min-w-0 items-baseline gap-x-2 gap-y-1 rounded-md border border-border bg-muted/30 px-2.5 py-1.5",
        numbered ? "grid-cols-[auto_auto_auto_minmax(0,1fr)]" : "grid-cols-[auto_auto_minmax(0,1fr)]",
      )}
      data-testid="mapping-overview-compared"
    >
      {numbered && <span aria-hidden />}
      <span className="truncate text-[11px] text-muted-foreground">{sourceName(cards, lookup.source)}</span>
      <span aria-hidden />
      <span className="truncate text-[11px] text-muted-foreground">{against}</span>
      {lines.map((line, index) => {
        const read = line.operand.read;
        return (
          <Fragment key={`${index}-${line.field}`}>
            {numbered && (
              <span className="text-[11px] text-muted-foreground tabular-nums" data-testid="mapping-overview-compared-mark">
                {line.and ? "and" : lineCount > 1 ? lineNumbers[index] : ""}
              </span>
            )}
            <FieldButton source={lookup.source} field={line.field} cards={cards} onPick={onPick} />
            <span className="font-mono text-[12px] text-muted-foreground">=</span>
            <span className="min-w-0" data-testid="mapping-overview-compared-operand">
              {read === null
                ? <span className="font-mono text-[12px] [overflow-wrap:anywhere]">{line.operand.text}</span>
                : <FieldButton source={read.source} field={read.field} cards={cards} onPick={onPick} />}
              {read !== null && !stage.changed && shownAt.get(fieldId(read.source, read.field)) === index && <Shown value={values.get(fieldId(read.source, read.field))} />}
            </span>
          </Fragment>
        );
      })}
    </div>
  );
}

/** A value's way to its property, in the order a render takes it: what enters, what changes it, what it is compared in and fetches, and where it is written. */
function Way({ flow, node, cards, value, values, onPick }: {
  flow: MappingOutlineFlow;
  node: MappingOutlineNode;
  cards: readonly OutlineCard[];
  /** The property's value in the record shown; undefined for an alternative, or when no record is shown. */
  value: string | undefined;
  values: ReadonlyMap<string, string>;
  onPick: (part: OverviewPart) => void;
}) {
  const stages = wayOf(flow);
  return (
    <ol className="flex min-w-0 flex-col" data-testid="mapping-overview-way">
      {stages.map((stage, index) => {
        const last = index === stages.length - 1;
        switch (stage.kind) {
          case "input":
            return (
              <Stage key={index} title="Input">
                <ul className="flex min-w-0 flex-col gap-1">
                  {stage.reads.map((read) => (
                    <li key={fieldId(read.source, read.field)} className="flex min-w-0 items-start gap-1.5">
                      <span className="mt-0.5 shrink-0 text-muted-foreground"><SourceGlyph kind={cards.find((card) => card.id === read.source)?.source.kind} /></span>
                      <span className="min-w-0 flex-1">
                        <span className="flex min-w-0 items-baseline gap-2">
                          <FieldButton source={read.source} field={read.field} cards={cards} onPick={onPick} />
                          <span className="ml-auto min-w-0 truncate text-[11.5px] text-muted-foreground">{sourceName(cards, read.source)}</span>
                        </span>
                        <Shown value={values.get(fieldId(read.source, read.field))} />
                      </span>
                    </li>
                  ))}
                  {stage.literal !== null && (
                    <li className="min-w-0">
                      <span className="block font-mono text-[12px] [overflow-wrap:anywhere]">{stage.literal}</span>
                      <span className="text-[12px] text-muted-foreground">Written as it stands, on every record.</span>
                    </li>
                  )}
                </ul>
              </Stage>
            );
          case "changed":
            return (
              <Stage key={index} title={stage.steps.every((step) => step.kind === "expr") ? "Computed" : "Changed"}>
                <ul className="flex min-w-0 flex-col gap-1">
                  {stage.steps.map((step, at) => (
                    <li key={`${at}-${step.kind}`} className="min-w-0">
                      <span className="block font-mono text-[12px] [overflow-wrap:anywhere]">{step.text}</span>
                      <span className="text-[12px] text-muted-foreground">{step.detail}</span>
                    </li>
                  ))}
                </ul>
              </Stage>
            );
          case "compared":
            return (
              <Stage
                key={index}
                title={<>Compared in <button type="button" className="font-mono outline-none hover:underline focus-visible:underline" onClick={() => onPick({ type: "card", source: stage.lookup.source })}>{sourceName(cards, stage.lookup.source)}</button></>}
                aside={cards.find((card) => card.id === stage.lookup.source)?.role}
              >
                <Compared stage={stage} cards={cards} values={values} onPick={onPick} />
                {stage.changed && <span className="text-[12px] text-muted-foreground">The value is compared as the steps above leave it.</span>}
                <span className="text-[12px] text-muted-foreground">{stage.step.detail}</span>
              </Stage>
            );
          case "fetched":
            return (
              <Stage key={index} title="Fetched">
                <span className="flex min-w-0 items-baseline gap-1.5 text-[12px]">
                  {stage.lookup.field === null || stage.lookup.field === undefined
                    ? <span className="text-muted-foreground">{`What ${sourceName(cards, stage.lookup.source)} holds for the value.`}</span>
                    : <>
                        <FieldButton source={stage.lookup.source} field={stage.lookup.field} cards={cards} onPick={onPick} />
                        <span className="text-muted-foreground">{`of ${sourceName(cards, stage.lookup.source)}`}</span>
                      </>}
                </span>
              </Stage>
            );
          default:
            return (
              <Stage key={index} last={last} title="Written to">
                <span className="block min-w-0 font-mono text-[12px] [overflow-wrap:anywhere]">{outlinePathOf(node.path)}</span>
                {value !== undefined
                  ? <Shown value={value} />
                  : flow.unverified && <span className="text-[12px] text-muted-foreground">Written even when the cache holds no such record.</span>}
              </Stage>
            );
        }
      })}
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
          <Way flow={node.flow} node={node} cards={cards} value={values.get(node.path)} values={values} onPick={onPick} />
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
                  {alternative.flow !== null && alternative.flow !== undefined && <Way flow={alternative.flow} node={alternative} cards={cards} value={undefined} values={values} onPick={onPick} />}
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

/** What breaking a rule on the rows does, and when it is judged. */
function ruleOutcome(rule: MappingOutlineRowRule): string {
  const when = rule.per === "run" ? "Judged once a run, over every row of its scope." : "Judged as each record renders.";
  return `${when} ${rule.onFail === "report" ? "A record that breaks it is sent, and the failure recorded." : "A record that breaks it is held."}`;
}

/** Rules a mapping states on the rows it renders from (`dataset.assert`), as the rules on a value are listed. */
function RowRules({ title, rules }: { title: string; rules: readonly MappingOutlineRowRule[] }) {
  if (rules.length === 0) {
    return null;
  }

  return (
    <Section title={title}>
      <ul className="flex min-w-0 flex-col gap-1.5" data-testid="mapping-overview-row-rules">
        {rules.map((rule) => (
          <li key={rule.label} className="flex min-w-0 items-start gap-1.5 text-[12px]">
            <ShieldCheck className="mt-0.5 size-3.5 shrink-0 text-muted-foreground" aria-hidden />
            <span className="min-w-0">
              <span className="font-medium">{rule.label}</span>
              <span className="block font-mono text-[11.5px] text-muted-foreground [overflow-wrap:anywhere]">{rule.expected}</span>
              <span className="block text-muted-foreground">{ruleOutcome(rule)}</span>
            </span>
          </li>
        ))}
      </ul>
    </Section>
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
  const rules = (outline.dataset.rules ?? []).filter((rule) => rule.reads.some((read) => read.source === card.id && read.field === name));
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
      <RowRules title="Rules reading it" rules={rules} />
    </>
  );
}

function CardPanel({ card, outline, onPick }: { card: OutlineCard; outline: MappingOutline; onPick: (part: OverviewPart) => void }) {
  const readers = readersOf(outline, card.id, null);
  const rules = (outline.dataset.rules ?? []).filter((rule) => rule.source === card.id);
  return (
    <>
      <p className="text-[12.5px] text-muted-foreground">{card.source.detail ?? card.role}</p>
      <RowRules title="Rules on these rows" rules={rules} />
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
      {card.lookups.length > 0 && (
        <Section title={`Lookups, ${card.lookups.length}`}>
          <ul className="flex min-w-0 flex-col gap-2" data-testid="mapping-overview-lookups">
            {card.lookups.map((lookup) => (
              <li key={lookup.name} className="flex min-w-0 flex-col gap-0.5 px-1">
                <span className="truncate font-mono text-[12px]" title={lookup.table}>{lookup.name}</span>
                <span className="text-[12px] text-muted-foreground">{lookupOrigin(lookup)}</span>
                <span className="truncate font-mono text-[11px] text-muted-foreground" title={lookup.columns.map((column) => column.name).join(", ")}>
                  {lookup.columns.map((column) => column.name).join(", ")}
                </span>
              </li>
            ))}
          </ul>
        </Section>
      )}
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
 * field and what it reaches, or a table and what is read of it, with the jump to where it stands in lineage.
 */
export function OverviewInspector({ part, outline, tables, cached, yaml, values, lineage, onPick, onPreview, onClose }: {
  part: OverviewPart | null;
  outline: MappingOutline;
  tables: Parameters<typeof cardsOf>[2];
  cached: Parameters<typeof cardsOf>[3];
  yaml: string;
  values: ReadonlyMap<string, string>;
  /** What the part picked opens in lineage: its table's node, or the nodes a property's value comes from. */
  lineage: readonly PartTarget[];
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
  const title = node !== null ? outlinePathOf(node.path) : part?.type === "field" ? part.field : card?.title ?? "";
  const leafStart = node !== null ? lastStepStart(title) : title.lastIndexOf(".") + 1;
  const leaf = leafStart > 0 ? leafStart - 1 : -1;

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
            {lineage.length > 0 && (
              <span className="flex shrink-0" data-testid="mapping-overview-inspector-lineage">
                <PartLineageButton targets={lineage} named={node !== null ? node.name : title} />
              </span>
            )}
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
