import type { ReactNode } from "react";
import { Link } from "react-router-dom";
import { Info, TriangleAlert, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { CopyButton } from "@/components/CopyButton";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import { KindText } from "../KindText";
import type {
  BlueprintColumn, BlueprintRead, BlueprintRecords, BlueprintTemplate, BlueprintUse, DeliveryDimension, DeliveryDimensionCoverage, SchemaSegmentReading,
} from "../../../api/delivery";
import {
  SOURCE_CARD, TABLE_CARD, columnElement, entityName, readElement, recordsElement, type BlueprintElement, type BlueprintLayout, type ExampleValue,
} from "./blueprintModel";
import { PathText } from "./BlueprintDiagram";

interface InspectorProps {
  layout: BlueprintLayout;
  dimension: DeliveryDimension;
  table: string;
  partition: string | null;
  coverage: Map<string, DeliveryDimensionCoverage>;
  picked: BlueprintElement | null;
  examples: Map<BlueprintElement, ExampleValue> | null;
  onPick: (element: BlueprintElement | null) => void;
}

/** The most ids one search by id asks for (DimensionLabeler.IdsPerQuery). */
const IDS_PER_SEARCH = 500;

/** The most records one step follows for one key (DimensionLabeler.MaxReferencesPerStep). */
const REFERENCES_PER_STEP = 20;

/** The most kinds the panel lists; a pattern matching more says how many more. */
const KINDS_LISTED = 12;

/**
 * What a picked part of the blueprint is and does, beside the YAML that declares it: a read with what the template says of
 * every segment of its path, how the build finds it and what it makes; records found by id; a column with what it holds
 * and the reads it is written from; or, with nothing picked, the table. Every fact comes from the declaration, the saved
 * templates and the last build.
 */
export function BlueprintInspector(props: InspectorProps) {
  const { layout, picked, onPick } = props;
  let body: ReactNode;
  let title: ReactNode;
  if (picked?.startsWith("read:") === true && layout.reads.has(picked.slice(5))) {
    const read = layout.reads.get(picked.slice(5))!;
    title = <PathText path={read.path} />;
    body = <ReadFacts read={read} {...props} />;
  } else if (picked === SOURCE_CARD) {
    title = "The records searched";
    body = <SourceFacts {...props} />;
  } else if (picked?.startsWith("card:") === true && layout.records.has(picked.slice(5))) {
    const node = layout.records.get(picked.slice(5))!;
    title = node.entityTypes.length === 0 ? "Records the ids name" : node.entityTypes.map((type) => entityName(type).name).join(" or ");
    body = <RecordsFacts node={node} {...props} />;
  } else if (picked?.startsWith("col:") === true && layout.columns.has(picked.slice(4))) {
    const column = layout.columns.get(picked.slice(4))!;
    title = <span className="font-mono">{column.name}</span>;
    body = <ColumnFacts column={column} {...props} />;
  } else {
    title = <span className="font-mono">{props.table}</span>;
    body = <TableFacts {...props} />;
  }

  return (
    <section className="flex min-w-0 flex-col overflow-hidden rounded-lg border border-border bg-card" data-testid="blueprint-inspector">
      <header className="flex min-w-0 items-center gap-2 border-b border-border bg-muted/40 px-3 py-2">
        <Info className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
        <h3 className="min-w-0 flex-1 truncate text-[13px] font-semibold" data-testid="blueprint-inspector-title">{title}</h3>
        {picked !== null && picked !== TABLE_CARD && (
          <Button variant="ghost" size="icon" className="size-6 text-muted-foreground" aria-label="Show the table" onClick={() => onPick(null)}>
            <X />
          </Button>
        )}
      </header>
      <dl className="divide-y divide-border/60">{body}</dl>
      {picked === null && (
        <p className="border-t border-border/60 px-4 py-2 text-[12px] text-muted-foreground">
          Point at a line of the YAML or a part of the diagram to see what makes it; click to keep it here.
        </p>
      )}
    </section>
  );
}

/** One fact: what it is on the left, always the same width, and the fact on the right. */
function Row({ label, children, testId }: { label: ReactNode; children: ReactNode; testId?: string }) {
  return (
    <div className="grid grid-cols-[7.5rem_minmax(0,1fr)] items-baseline gap-x-3 px-4 py-2" data-testid={testId}>
      <dt className="text-[12px] text-muted-foreground">{label}</dt>
      <dd className="min-w-0 text-[13px]">{children}</dd>
    </div>
  );
}

function Code({ children, className }: { children: ReactNode; className?: string }) {
  return <span className={cn("break-all font-mono text-[12px]", className)}>{children}</span>;
}

/** A part of the blueprint named as a link that picks it. */
function PickLink({ element, children, onPick }: { element: BlueprintElement; children: ReactNode; onPick: (element: BlueprintElement) => void }) {
  return (
    <button type="button" className="min-w-0 text-left hover:underline focus-visible:underline focus-visible:outline-none" onClick={() => onPick(element)}>
      {children}
    </button>
  );
}

/** A template, opening on the Templates page at the file holding it; or why none describes the records, with where templates are found. */
function TemplateFact({ template, missing }: { template: BlueprintTemplate | null; missing: string | null }) {
  if (template === null) {
    return (
      <span className="flex min-w-0 items-start gap-1.5">
        <TriangleAlert className="mt-0.5 size-3.5 shrink-0 text-warning" aria-hidden />
        <span className="min-w-0">
          {missing ?? "No template describes these records."}{" "}
          <Link to="/delivery/templates" className="text-primary hover:underline">Templates</Link>
        </span>
      </span>
    );
  }

  return (
    <Link
      to={`/delivery/templates?kind=${encodeURIComponent(template.kind)}&version=${encodeURIComponent(template.version)}`}
      className="flex min-w-0 items-baseline gap-1.5 hover:underline"
      data-testid="blueprint-template-link"
    >
      <span className="min-w-0"><KindText kind={template.kind} /></span>
      <span className="shrink-0 font-mono text-[11px] text-muted-foreground">{template.version}</span>
    </Link>
  );
}

/** What a read is for, in a line a person reads: the key, the value, an attribute, a step towards one. */
function purposeText(layout: BlueprintLayout, use: BlueprintUse): string {
  const chain = layout.chains.find((c) => c.role === use.role && c.attribute === use.attribute);
  const steps = chain === undefined ? 1 : chain.reads.filter((id) => id !== "key").length;
  const of = steps > 1 ? `, step ${use.step + 1} of ${steps}` : "";
  switch (use.role) {
    case "key":
      return `The key, column ${layout.keyColumn}: every distinct value is one, exactly as the index holds it.`;
    case "label":
      return use.last
        ? `The value, column ${layout.valueColumn}${of}: the first text found, cleaned.`
        : `The value's record${of}: every record it names is read next, at most ${REFERENCES_PER_STEP} a key.`;
    case "collect":
      return `${use.attribute}: every value the key's own records hold, a row of the table each.`;
    default:
      return use.last
        ? `${use.attribute}${of}: the first text found, at most 256 characters.`
        : `${use.attribute}${of}: every record it names is read next, at most ${REFERENCES_PER_STEP} a key.`;
  }
}

/** A read: what it is for, where and how it is read, what the template says of each segment, what it names, and what is wrong. */
function ReadFacts({ read, layout, examples, onPick }: InspectorProps & { read: BlueprintRead }) {
  const card = layout.readCard.get(read.id);
  const node = card === undefined || card === SOURCE_CARD ? null : layout.records.get(card.slice(5)) ?? null;
  const source = layout.blueprint.source;
  const example = examples?.get(readElement(read.id));
  return (
    <>
      <Row label="For" testId="blueprint-inspector-for">
        <span className="flex flex-col gap-0.5">{read.uses.map((use) => <span key={`${use.role}:${use.attribute}`}>{purposeText(layout, use)}</span>)}</span>
      </Row>
      <Row label="Read in">
        {node === null
          ? <PickLink element={SOURCE_CARD} onPick={onPick}><KindText kind={source.kind} /></PickLink>
          : (
            <PickLink element={recordsElement(node.id)} onPick={onPick}>
              {node.entityTypes.length === 0 ? "the records the ids name" : node.entityTypes.join(" or ")}
            </PickLink>
          )}
      </Row>
      <Row label="Template"><TemplateFact template={node === null ? source.template : node.template} missing={node === null ? source.missing : node.missing} /></Row>
      <Row label="How">
        {read.index !== null
          ? (
            <span className="flex flex-col gap-0.5">
              <span>Aggregated as <Code className="text-foreground">{read.index.aggregateBy}</Code> ({read.index.index}), paged by value ranges past the search's limit on values.</span>
              {read.index.repeats && <span className="text-[12px] text-muted-foreground">A record can hold several values here.</span>}
            </span>
          )
          : node !== null
            ? `Found by id, ${IDS_PER_SEARCH} ids a search, the search returning only this path; read from the records as they are.`
            : "Settled by the template of the kind, which no template folder holds."}
      </Row>
      <Row label="Template says" testId="blueprint-inspector-schema">
        {read.schema === null
          ? <span className="text-muted-foreground">Nothing: no template describes these records.</span>
          : <Segments segments={read.schema.segments} />}
      </Row>
      {read.references.length > 0 && (
        <Row label="Names">
          {read.leadsTo !== null
            ? <PickLink element={recordsElement(read.leadsTo)} onPick={onPick}><Code>{read.references.join(", ")}</Code></PickLink>
            : <Code>{read.references.join(", ")}</Code>}
        </Row>
      )}
      {read.problem !== null && (
        <Row label="Mind" testId="blueprint-inspector-problem">
          <span className="flex items-start gap-1.5">
            <TriangleAlert className="mt-0.5 size-3.5 shrink-0 text-warning" aria-hidden />
            <span>{read.problem}</span>
          </span>
        </Row>
      )}
      {example !== undefined && (
        <Row label="Example"><ExampleFact value={example} /></Row>
      )}
    </>
  );
}

/** Each segment of a path as the template describes it: its type, how the index stores an array, the form declaring it, its filter, and its description. */
function Segments({ segments }: { segments: SchemaSegmentReading[] }) {
  // The record's data holds every property and tells nothing of its own, unless the path filters it.
  const shown = segments.filter((segment, index) => !(index === 0 && segment.name === "data" && segment.found && segment.filter === null));
  return (
    <ol className="flex flex-col gap-1.5">
      {shown.map((segment, index) => (
        <li key={`${index}:${segment.name}`} className="flex min-w-0 flex-col gap-0.5" data-testid="blueprint-segment">
          <span className="flex min-w-0 flex-wrap items-baseline gap-x-1.5">
            <Code className={cn("font-medium", segment.found ? "text-foreground" : "text-warning")}>{segment.name}</Code>
            {segment.found
              ? <span className="text-[12px] text-muted-foreground">{typeText(segment)}</span>
              : <span className="text-[12px] text-warning">not in the template</span>}
          </span>
          {segment.branch !== null && <span className="text-[12px] text-muted-foreground">in the {segment.branch} entries</span>}
          {segment.filter !== null && (
            <span className="text-[12px] text-muted-foreground">
              keeps those whose <Code className="text-foreground">{segment.filter.property}</Code> {compareText(segment.filter.compare)} <Code className="text-foreground">{segment.filter.text}</Code>
              {!segment.filter.known && segment.found && <span className="text-warning"> (not a property the template declares there)</span>}
            </span>
          )}
          {segment.description !== null && (
            <RichTooltip title={segment.title ?? segment.name} body={segment.description}>
              <span className="line-clamp-2 text-[12px] text-muted-foreground">{segment.description}</span>
            </RichTooltip>
          )}
        </li>
      ))}
    </ol>
  );
}

function typeText(segment: SchemaSegmentReading): string {
  const items = segment.itemType === null || segment.itemType === "any" ? "values" : `${segment.itemType}s`;
  const type = segment.type === "array"
    ? `list of ${items}`
    : segment.format !== null ? `${segment.type ?? "value"}, ${segment.format}` : segment.type ?? "value";
  const indexed = segment.indexing === null ? "" : `, ${segment.indexing}`;
  const names = segment.references.length === 0 ? "" : `, names ${segment.references.join(" or ")}`;
  return `${type}${indexed}${names}`;
}

function compareText(compare: string): string {
  switch (compare) {
    case "contains":
      return "contains";
    case "endsWith":
      return "ends with";
    default:
      return "is";
  }
}

function ExampleFact({ value }: { value: ExampleValue }) {
  return (
    <span className="flex min-w-0 flex-col gap-0.5">
      {value.all.map((text, index) => <Code key={`${index}:${text}`} className="text-foreground">{text}</Code>)}
    </span>
  );
}

/** The dimension's own records: the kind and query, the kinds read with their records and templates, and how the key is read. */
function SourceFacts({ layout, dimension }: InspectorProps) {
  const source = layout.blueprint.source;
  return (
    <>
      <Row label="Kind"><KindText kind={source.kind} /></Row>
      <Row label="Query">{source.query === null ? <span className="text-muted-foreground">Every record of the kind.</span> : <Code>{source.query}</Code>}</Row>
      {dimension.builtQuery !== null && dimension.builtQuery !== source.query && <Row label="Query as built"><Code>{dimension.builtQuery}</Code></Row>}
      <Row label={source.built ? "Kinds read" : "Kinds matched"} testId="blueprint-inspector-kinds">
        {source.kinds.length === 0
          ? <span className="text-muted-foreground">No template matches the kind.</span>
          : (
            <ul className="flex flex-col gap-1">
              {source.kinds.slice(0, KINDS_LISTED).map((kind) => (
                <li key={kind.kind} className="flex min-w-0 items-baseline gap-2">
                  <span className="min-w-0 flex-1"><TemplateFact template={kind.template === null ? null : { kind: kind.kind, version: kind.template }} missing={`No template folder holds a template of ${kind.kind}.`} /></span>
                  {kind.records !== null && <span className="shrink-0 font-mono text-[12px] tabular-nums text-muted-foreground">{kind.records.toLocaleString("en-US")}</span>}
                </li>
              ))}
              {source.kinds.length > KINDS_LISTED && (
                <li className="text-[12px] text-muted-foreground">and {(source.kinds.length - KINDS_LISTED).toLocaleString("en-US")} more</li>
              )}
            </ul>
          )}
      </Row>
      {!source.built && source.kinds.length > 0 && (
        <Row label="Read">The templates the kind matches; a build reads every kind the partition holds of it, each by its own template.</Row>
      )}
      <Row label="How">One search by kind lists the kinds; each path is read by aggregating its field, paged by value ranges past the search's limit on values.</Row>
      <Row label="Records a value">{dimension.countRecords ? "Counted exactly, a search per value." : "Exact where a record holds one key, else summed."}</Row>
      <Row label="Most keys"><span className="font-mono tabular-nums">{dimension.maxValues.toLocaleString("en-US")}</span></Row>
    </>
  );
}

/** Records found by id: their entity type and template, the read whose ids name them, and the paths read from them. */
function RecordsFacts({ node, layout, onPick }: InspectorProps & { node: BlueprintRecords }) {
  const from = layout.reads.get(node.from);
  return (
    <>
      <Row label="Entity type">{node.entityTypes.length === 0 ? <span className="text-muted-foreground">Not known before a build reads the ids.</span> : <Code>{node.entityTypes.join(" or ")}</Code>}</Row>
      <Row label="Template"><TemplateFact template={node.template} missing={node.missing} /></Row>
      <Row label="Ids from">{from === undefined ? <span className="text-muted-foreground">Not known.</span> : <PickLink element={readElement(from.id)} onPick={onPick}><PathText path={from.path} /></PickLink>}</Row>
      <Row label="How">{`Found by id through the search, ${IDS_PER_SEARCH} ids a search in the kind of their entity type, every path below asked in the same searches.`}</Row>
      <Row label="Reads">
        <ul className="flex flex-col gap-0.5">
          {node.reads.map((read) => (
            <li key={read.id}><PickLink element={readElement(read.id)} onPick={onPick}><PathText path={read.path} /></PickLink></li>
          ))}
        </ul>
      </Row>
    </>
  );
}

/** What a column holds, by its role. */
function holds(column: BlueprintColumn, layout: BlueprintLayout, dimension: DeliveryDimension): string {
  const collected = layout.chains.some((c) => c.role === "collect" && c.attribute === column.attribute);
  switch (column.role) {
    case "id":
      return "The row's number, the table's key: what a table of facts joins on. It stays the same for as long as the dimension holds the row.";
    case "partition":
      return "The data partition the row was read in. A flow that builds in several partitions writes them all to this table.";
    case "keyId":
      return "The key's number: the same in every row of the key.";
    case "key":
      return `The key: ${dimension.path} exactly as the index holds it (an id, for a reference).`;
    case "value":
      return dimension.label.length === 0
        ? "The key's human-friendly value: the key itself, cleaned."
        : `The key's human-friendly value: its label, read at ${dimension.label[dimension.label.length - 1]} and cleaned.`;
    case "records":
      return "The records of the row: those holding the value the row collects, or every record of the key.";
    case "filter":
      return "The search that finds the key's records.";
    default:
      return collected
        ? `The attribute ${column.name}, collected from the key's own records: a row for each value a key holds.`
        : `The attribute ${column.name}, read from the record the key names.`;
  }
}

/** A column: what it holds, the reads it is written from, how a value is cleaned, how much of an attribute was read, and an example. */
function ColumnFacts({ column, layout, dimension, coverage, examples, onPick }: InspectorProps & { column: BlueprintColumn }) {
  const read = column.role === "attribute" ? coverage.get(column.name) : undefined;
  const example = examples?.get(columnElement(column.name));
  return (
    <>
      <Row label="Holds">{holds(column, layout, dimension)}</Row>
      {column.from.length > 0 && (
        <Row label="Written from">
          <ul className="flex flex-col gap-0.5">
            {column.from.map((id) => {
              const from = layout.reads.get(id);
              return from === undefined ? null : <li key={id}><PickLink element={readElement(id)} onPick={onPick}><PathText path={from.path} /></PickLink></li>;
            })}
          </ul>
        </Row>
      )}
      {column.role === "value" && (
        <>
          <Row label="Cleaned by">{dimension.clean.length === 0 ? <span className="text-muted-foreground">Nothing but a trim.</span> : <Code>{dimension.clean.join(" → ")}</Code>}</Row>
          {dimension.label.length > 0 && (
            <Row label="With no label">{dimension.unlabelled === null ? "The code its id ends with." : <span>Valued <span className="font-medium">{dimension.unlabelled}</span></span>}</Row>
          )}
        </>
      )}
      {column.role === "attribute" && dimension.unlabelled !== null && (
        <Row label="When not read">Valued <span className="font-medium">{dimension.unlabelled}</span></Row>
      )}
      {column.role === "attribute" && dimension.keys > 0 && (
        <Row label="Read for">
          {read === undefined
            ? <span className="text-muted-foreground">No key holds a value read for it.</span>
            : `${read.keys.toLocaleString("en-US")} of ${dimension.keys.toLocaleString("en-US")} keys, ${read.values.toLocaleString("en-US")} ${read.values === 1 ? "value" : "values"}.`}
        </Row>
      )}
      {example !== undefined && <Row label="Example"><ExampleFact value={example} /></Row>}
    </>
  );
}

/** The table: its name and the query that reads it, what a row is, and what joins on it. */
function TableFacts({ layout, dimension, table, partition }: InspectorProps) {
  const collected = layout.chains.find((c) => c.role === "collect");
  const query = `SELECT * FROM ${table}${partition === null ? "" : ` WHERE [partition] = N'${partition.replaceAll("'", "''")}'`};`;
  return (
    <>
      <Row label="Table" testId="blueprint-inspector-table">
        <span className="flex min-w-0 items-center gap-1.5">
          <Code className="font-medium">{table}</Code>
          {dimension.table !== null && <CopyButton iconOnly label="Copy the table's name" text={table} testId="blueprint-copy-table" />}
        </span>
      </Row>
      <Row label="A row">{collected === undefined ? "One a key." : `One for each ${collected.attribute} value a key holds.`}</Row>
      {dimension.table === null
        ? <Row label="Made">By the first build, with an identity key and the indexes it is read through.</Row>
        : (
          <Row label="Read it">
            <span className="flex min-w-0 items-center gap-1.5">
              <span className="min-w-0 font-mono text-[12px] [overflow-wrap:anywhere]">{query}</span>
              <CopyButton iconOnly label="Copy the query" text={query} testId="blueprint-copy-query" />
            </span>
          </Row>
        )}
      <Row label="Joined on"><span><Code>id</Code> for a row, <Code>key_id</Code> for a key.</span></Row>
      <Row label="Columns">
        {`${layout.blueprint.columns.length}: an attribute the flow starts to declare gets its column on the next build, its rows kept.`}
      </Row>
    </>
  );
}

