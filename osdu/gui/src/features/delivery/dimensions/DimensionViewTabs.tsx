import { useRef, useState } from "react";
import { Link } from "react-router-dom";
import { ArrowRightLeft, Info, TriangleAlert } from "lucide-react";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { CodeView } from "@/components/CodeView";
import { DataTable, type Column } from "@/components/DataTable";
import { DiffView } from "@/components/DiffView";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { TruncatedText } from "@/components/TruncatedText";
import { cn } from "@/lib/utils";
import type {
  DeliveryDimensionView, DeliveryDimensionViewCheck, DeliveryDimensionViewColumn, DeliveryDimensionViewColumnCheck, DeliveryDimensionViewDetail,
  DeliveryDimensionViewJoin, DeliveryDimensionViewJoinVerdict,
} from "../../../api/delivery";
import { counted, duration } from "../assertions/assertionFormat";
import { ExplainTip } from "@/components/ExplainTip";
import { shortId } from "../idTail";
import { useWindowFit } from "../useWindowFit";
import { DimensionGrid } from "./DimensionGrid";
import { CheckGlyph, JoinVerdictBadge, NotesMark } from "./DimensionViewBadges";
import { DimensionViewYaml } from "./DimensionViewYaml";
import {
  VERDICT_VISUALS, VIEW_KEY_COLUMNS, columnFindings, exampleText, joinFindings, joinFoundNothing, joinVerdicts, tableObject, verdictText, viewObject,
} from "./dimensionViewFormat";

/** Opens a dimension of the view's flow on the Dimensions page, when the page knows it; null when it does not. */
export type OpenDimension = ((dimension: string) => void) | null;

/** A dimension the view reads, as a link to its page when the page knows it, else its name; its table on hover when given. */
function DimensionName({ name, table, onOpen, strong = false }: { name: string; table?: string; onOpen: OpenDimension; strong?: boolean }) {
  const shown = onOpen === null
    ? <span className={cn("font-mono text-[12px]", strong && "font-medium")}>{name}</span>
    : (
      <button
        type="button"
        onClick={(event) => { event.stopPropagation(); onOpen(name); }}
        className={cn("rounded-sm font-mono text-[12px] outline-none hover:underline focus-visible:underline", strong && "font-medium")}
        data-testid="dimension-view-dimension-link"
      >
        {name}
      </button>
    );
  return table === undefined ? shown : <RichTooltip title="Table" body={tableObject(table)} mono>{shown}</RichTooltip>;
}

/** A count from the newest check, or a muted dash when no check counted it; a zero stays quiet. */
function Counted({ value, testId }: { value: number | null | undefined; testId?: string }) {
  if (value == null) {
    return <span className="text-muted-foreground" data-testid={testId}>-</span>;
  }

  return <span className={cn("font-mono text-[12px] tabular-nums", value === 0 && "text-muted-foreground")} data-testid={testId}>{value.toLocaleString("en-US")}</span>;
}

/** The values a column's conversion could not read: the count, the first example, and every example on hover with Copy. */
function Unconverted({ column, check }: { column: DeliveryDimensionViewColumn; check: DeliveryDimensionViewColumnCheck | undefined }) {
  if (check?.unconverted == null) {
    return <span className="text-muted-foreground">-</span>;
  }

  if (check.unconverted === 0) {
    return <span className="font-mono text-[12px] tabular-nums text-muted-foreground">0</span>;
  }

  const examples = check.examples.map(exampleText);
  return (
    <ExplainTip
      title={`${column.name}: not converted`}
      text={`${counted(check.unconverted, "value")} of ${column.name} did not convert to ${column.dataType ?? column.type}; the view holds null for them.${examples.length > 0 ? `\n\nFor example:\n${examples.join("\n")}` : ""}`}
      testId={`dimension-view-unconverted-${column.name}`}
    >
      <span className="flex min-w-0 items-center gap-1.5 whitespace-nowrap text-[12px]" data-testid={`dimension-view-unconverted-${column.name}`}>
        <TriangleAlert className="size-3.5 shrink-0 text-warning" aria-hidden />
        <span className="font-mono tabular-nums">{check.unconverted.toLocaleString("en-US")}</span>
        {check.examples.length > 0 && <span className="max-w-32 truncate font-mono text-[11.5px] text-muted-foreground">'{check.examples[0].value}'</span>}
      </span>
    </ExplainTip>
  );
}

/** A column's type, and for one the view converts, a mark saying so, with what a conversion does on hover. */
function TypeText({ column }: { column: DeliveryDimensionViewColumn }) {
  if (column.dataType == null) {
    return <span className="font-mono text-[12px]">{column.type}</span>;
  }

  return (
    <RichTooltip title="Converted" body={`The expression's value is converted to ${column.dataType}. A value the conversion cannot read is null in the view, and the check counts it.`}>
      <span className="flex items-center gap-1 font-mono text-[12px]">
        <ArrowRightLeft className="size-3 shrink-0 text-muted-foreground" aria-label="converted" />
        {column.dataType}
      </span>
    </RichTooltip>
  );
}

/**
 * The view's columns in order, `partition` and `id` first: each one's type (the type it is converted to, marked so, or
 * its expression's), the expression it is computed by, and what it holds on hover, with what the newest check found of
 * it: the rows holding a value, and the values its conversion could not read, with examples by row.
 */
export function ViewColumns({ view }: { view: DeliveryDimensionView }) {
  const checks = columnFindings(view.lastCheck);
  const columns: Column<DeliveryDimensionViewColumn>[] = [
    {
      id: "name",
      header: "Column",
      render: (row) => (VIEW_KEY_COLUMNS.includes(row.name.toLowerCase())
        ? (
          <RichTooltip body={row.name.toLowerCase() === "id"
            ? `The number of the ${view.from} row the view's row stands for: the view's key, the same for as long as the dimension holds the row.`
            : "The partition the row was built in."}
          >
            <span className="font-mono text-[12px] text-muted-foreground">{row.name}</span>
          </RichTooltip>
        )
        : (
          <span className="flex items-center gap-1.5">
            <span className="font-mono text-[12px] font-medium">{row.name}</span>
            {row.description != null && (
              <RichTooltip title="What it holds" body={row.description}>
                <Info className="size-3.5 shrink-0 text-muted-foreground" aria-label="What the column holds" data-testid={`dimension-view-column-description-${row.name}`} />
              </RichTooltip>
            )}
          </span>
        )),
    },
    { id: "type", header: "Type", render: (row) => <TypeText column={row} /> },
    {
      id: "expression",
      header: "Expression",
      fill: true,
      floor: 140,
      render: (row) => <TruncatedText text={row.expression} mono maxWidth={720} title="Expression" />,
    },
    { id: "values", header: "Values", align: "right", render: (row) => <Counted value={checks.get(row.name.toLowerCase())?.values} /> },
    { id: "unconverted", header: "Not converted", render: (row) => <Unconverted column={row} check={checks.get(row.name.toLowerCase())} /> },
  ];

  return (
    <DimensionGrid testId="dimension-view-columns-grid">
      <DataTable columns={columns} rows={view.columns} rowKey={(row) => row.name} emptyMessage="The view lists no column." data-testid="dimension-view-columns" />
    </DimensionGrid>
  );
}

/**
 * The rows of a join whose value named no row, as the newest check counted them: the count and the first value, every
 * value it kept on hover with Copy; a join whose rows held no value to join on at all says so.
 */
function JoinUnmatched({ join, check, rows }: { join: DeliveryDimensionViewJoin; check: DeliveryDimensionViewCheck | null | undefined; rows: number }) {
  const found = joinFindings(check).get(join.alias.toLowerCase());
  if (found === undefined) {
    return <span className="text-muted-foreground">-</span>;
  }

  if (joinFoundNothing(found, rows)) {
    return (
      <span className="flex items-center gap-1.5 text-[12px] text-muted-foreground" data-testid={`dimension-view-join-nothing-${join.alias}`}>
        <TriangleAlert className="size-3.5 shrink-0 text-warning" aria-hidden />
        <span className="truncate">no row holds a value to join on</span>
      </span>
    );
  }

  if (found.unmatched === 0) {
    return <span className="font-mono text-[12px] tabular-nums text-muted-foreground">0</span>;
  }

  const quoted = found.examples.map((example) => `'${example}'`);
  return (
    <ExplainTip
      title={`${join.alias}: values that named no row`}
      text={`${counted(found.unmatched, "row")} hold a value of ${join.on} that names no row of ${join.to}; the view holds null in their ${join.alias} columns.${quoted.length > 0 ? `\n\nFor example:\n${quoted.join("\n")}` : ""}`}
      testId={`dimension-view-join-unmatched-${join.alias}`}
    >
      <span className="flex min-w-0 items-center gap-1.5 text-[12px]" data-testid={`dimension-view-join-unmatched-${join.alias}`}>
        <TriangleAlert className="size-3.5 shrink-0 text-warning" aria-hidden />
        <span className="font-mono tabular-nums">{found.unmatched.toLocaleString("en-US")}</span>
        {quoted.length > 0 && <span className="min-w-0 truncate font-mono text-[11.5px] text-muted-foreground">{quoted.join(", ")}</span>}
      </span>
    </ExplainTip>
  );
}

/**
 * The view's joins in order: each one's alias and the dimension it joins (its table on hover), the column joined on, and
 * what the flow's templates say of it, with what the newest check found: the rows that found their row, and those whose
 * value named none, with the first of those values.
 */
export function ViewJoins({ view, verdicts, onOpenDimension }: {
  view: DeliveryDimensionView;
  verdicts: readonly DeliveryDimensionViewJoinVerdict[] | undefined;
  onOpenDimension: OpenDimension;
}) {
  const check = view.lastCheck;
  const checks = joinFindings(check);
  const said = joinVerdicts(verdicts);
  const rows = check?.rows ?? 0;
  const columns: Column<DeliveryDimensionViewJoin>[] = [
    {
      // The dimension beside the alias where the tab is wide enough; where it is not, the alias alone, the dimension on
      // hover. The values that named no row are the one column that gives up width, so every column stays in view.
      id: "join",
      header: "Join",
      render: (row) => (
        <span className="flex items-baseline gap-2">
          {row.alias === row.to
            ? <DimensionName name={row.to} table={row.table} onOpen={onOpenDimension} strong />
            : (
              <>
                <RichTooltip title="Joins" body={`${row.to} (${tableObject(row.table)})`} mono>
                  <span className="font-mono text-[12px] font-medium">{row.alias}</span>
                </RichTooltip>
                <span className="hidden text-muted-foreground @4xl/joins:inline"><DimensionName name={row.to} table={row.table} onOpen={onOpenDimension} /></span>
              </>
            )}
        </span>
      ),
    },
    { id: "on", header: "On", render: (row) => <TruncatedText text={row.on} mono maxWidth={220} /> },
    {
      id: "template",
      header: "Template",
      render: (row) => {
        const verdict = said.get(row.alias.toLowerCase());
        return verdict === undefined
          ? <span className="text-muted-foreground">-</span>
          : <JoinVerdictBadge verdict={verdict} testId={`dimension-view-join-verdict-${row.alias}`} />;
      },
    },
    { id: "matched", header: "Matched", align: "right", render: (row) => <Counted value={checks.get(row.alias.toLowerCase())?.matched} /> },
    { id: "unmatched", header: "Unmatched", fill: true, floor: 110, render: (row) => <JoinUnmatched join={row} check={check} rows={rows} /> },
  ];

  return (
    <div className="@container/joins flex flex-col gap-2" data-testid="dimension-view-joins-tab">
      <p className="flex flex-wrap items-baseline gap-x-1.5 text-[12.5px] text-muted-foreground">
        <span>{view.groupBy?.length ? "A row for each group of the rows of" : "A row for each row of"}</span>
        <DimensionName name={view.from} onOpen={onOpenDimension} />
        <span className="font-mono text-[11.5px]">{tableObject(view.tables[0] ?? `dim_${view.from}`)}</span>
        {view.where ? (
          <>
            <span>where</span>
            <span className="font-mono text-[11.5px]" data-testid="dimension-view-where">{view.where}</span>
          </>
        ) : null}
        {view.groupBy?.length ? (
          <>
            <span>grouped by</span>
            <span className="font-mono text-[11.5px]" data-testid="dimension-view-group-by">{view.groupBy.join(", ")}</span>
          </>
        ) : null}
      </p>
      <DimensionGrid testId="dimension-view-joins-grid">
        <DataTable columns={columns} rows={view.joins} rowKey={(row) => row.alias} emptyMessage="The view joins nothing: it reads its from dimension alone." data-testid="dimension-view-joins" />
      </DimensionGrid>
    </div>
  );
}

/** The view's newest checks, newest first: where and when each ran, what it came to, the rows it read and what it found. */
export function ViewChecks({ checks }: { checks: DeliveryDimensionViewCheck[] }) {
  const columns: Column<DeliveryDimensionViewCheck>[] = [
    { id: "checked", header: "Checked", render: (row) => <span className="text-[12px]"><RelativeTime value={row.checkedUtc} absolute={false} /></span> },
    { id: "partition", header: "Partition", render: (row) => <span className="font-mono text-[12px]">{row.partition}</span> },
    {
      id: "status",
      header: "Status",
      render: (row) => (
        <span className="inline-flex items-center gap-1.5 align-middle text-[12px]" data-status={row.status}>
          <CheckGlyph status={row.status} />
          {row.status}
        </span>
      ),
    },
    { id: "rows", header: "Rows", align: "right", render: (row) => (row.status === "passed" ? <Counted value={row.rows} /> : <span className="text-muted-foreground">-</span>) },
    {
      id: "found",
      header: "Found",
      fill: true,
      floor: 160,
      render: (row) => (row.status === "failed"
        ? (
          <ExplainTip title="Why the check failed" text={row.error ?? "The check kept no reason."} testId={`dimension-view-check-error-${row.checkId}`}>
            <span className="block truncate text-[12.5px] text-destructive">{row.error ?? "The check kept no reason."}</span>
          </ExplainTip>
        )
        : row.notes.length === 0
          ? <span className="text-[12px] text-muted-foreground">every join found its row, every value converted</span>
          : <NotesMark notes={row.notes} title="What the check found" testId={`dimension-view-check-notes-${row.checkId}`} />),
    },
    { id: "took", header: "Took", align: "right", render: (row) => <span className="font-mono text-[12px] tabular-nums">{duration(row.durationMs)}</span> },
    {
      id: "run",
      header: "Run",
      render: (row) => (row.runId == null
        ? <span className="text-muted-foreground">-</span>
        : (
          <Link to={`/runs/${row.runId}`} className="font-mono text-[12px] hover:underline" onClick={(event) => event.stopPropagation()} title={row.runId}>
            {shortId(row.runId)}
          </Link>
        )),
    },
  ];

  return (
    <DimensionGrid testId="dimension-view-checks-grid">
      <DataTable
        columns={columns}
        rows={checks}
        rowKey={(row) => row.checkId}
        emptyMessage="No build has checked the view yet: each build of its flow checks it in the run's partition after writing it."
        data-testid="dimension-view-checks"
      />
    </DimensionGrid>
  );
}

/** Which SQL the tab shows: as a build last wrote it, as the flow declares it now, or the difference. */
type SqlShown = "written" | "declared" | "difference";

/** What a code surface keeps under it: the page's padding and the workbench's status bar. */
const BELOW_SQL = 40;

/** The least height the SQL keeps, so a short window still shows a few lines. */
const MIN_SQL_HEIGHT = 280;

/**
 * The statement that writes the view: as a build last wrote it, as its flow's document writes it now, and, when the two
 * differ, the difference, which the next build writes. Each fills what the window leaves and scrolls inside itself.
 */
export function ViewSql({ detail }: { detail: DeliveryDimensionViewDetail }) {
  const written = detail.sql ?? null;
  const declared = detail.declaredSql ?? null;
  const differs = written !== null && declared !== null && written !== declared;
  const [picked, setPicked] = useState<SqlShown>("difference");
  // With no difference there is one statement to show, and nothing to pick between.
  const shown: SqlShown = differs ? picked : written !== null ? "written" : "declared";
  const frame = useRef<HTMLDivElement>(null);
  useWindowFit(frame, BELOW_SQL, MIN_SQL_HEIGHT, undefined, "height");

  if (written === null && declared === null) {
    return <p className="text-[13px] text-muted-foreground" data-testid="dimension-view-sql-none">No build has written the view, and no flow declares it now.</p>;
  }

  const caption = written !== null && declared !== null
    ? differs ? "The flow declares it differently from how a build last wrote it: the next build writes the declared statement." : "The flow declares it as a build last wrote it."
    : written === null ? "No build has written it yet: the next build of its flow writes this statement." : "No flow declares it now: this is how a build last wrote it.";
  return (
    <div className="flex min-w-0 flex-col gap-2" data-testid="dimension-view-sql">
      <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1.5">
        {differs && (
          <ToggleGroup
            type="single"
            variant="outline"
            size="sm"
            value={shown}
            onValueChange={(value) => { if (value !== "") { setPicked(value as SqlShown); } }}
            aria-label="Which statement"
          >
            <ToggleGroupItem value="difference" className="h-7 px-2.5 text-[12.5px]" data-testid="dimension-view-sql-difference">Difference</ToggleGroupItem>
            <ToggleGroupItem value="written" className="h-7 px-2.5 text-[12.5px]" data-testid="dimension-view-sql-written">As written</ToggleGroupItem>
            <ToggleGroupItem value="declared" className="h-7 px-2.5 text-[12.5px]" data-testid="dimension-view-sql-declared">As declared</ToggleGroupItem>
          </ToggleGroup>
        )}
        <span className="min-w-0 text-[12px] text-muted-foreground">{caption}</span>
      </div>
      <div
        ref={frame}
        className={cn(
          "flex min-h-0 min-w-0 flex-col",
          // The comparison sizes its editor by a height; given the frame's, it takes what the frame leaves under its toolbar.
          "[&>[data-testid=dimension-view-sql-diff]]:flex [&>[data-testid=dimension-view-sql-diff]]:min-h-0 [&>[data-testid=dimension-view-sql-diff]]:flex-1 [&>[data-testid=dimension-view-sql-diff]]:flex-col",
          "[&>[data-testid=dimension-view-sql-diff]>section]:min-h-0 [&>[data-testid=dimension-view-sql-diff]>section]:flex-1",
        )}
      >
        {shown === "difference" && written !== null && declared !== null
          ? (
            <DiffView
              original={written}
              modified={declared}
              language="sql"
              height="100%"
              sideLabels={{ original: `${viewObject(detail.view)} as written`, modified: "as declared, which the next build writes" }}
              data-testid="dimension-view-sql-diff"
            />
          )
          : <CodeView value={(shown === "written" ? written : declared) ?? ""} language="sql" fill data-testid="dimension-view-sql-code" />}
      </div>
    </div>
  );
}

/** What one entry of the declared list says: its kind, its name, what it makes, and what the newest check found of it. */
interface DeclaredEntry {
  key: string;
  kind: "from" | "join" | "column";
  name: string;
  makes: string;
  found: { text: string; warn: boolean } | null;
  /** What the flow's templates say of a join. */
  verdict?: DeliveryDimensionViewJoinVerdict;
}

function declaredEntries(view: DeliveryDimensionView, verdicts: readonly DeliveryDimensionViewJoinVerdict[] | undefined): DeclaredEntry[] {
  const check = view.lastCheck;
  const rows = check?.rows ?? 0;
  const joins = joinFindings(check);
  const columns = columnFindings(check);
  const said = joinVerdicts(verdicts);
  const entries: DeclaredEntry[] = [{
    key: "from",
    kind: "from",
    name: view.from,
    makes: `a row for each row of ${tableObject(view.tables[0] ?? `dim_${view.from}`)}`,
    found: check?.status === "passed" ? { text: counted(rows, "row"), warn: false } : null,
  }];
  view.joins.forEach((join, index) => {
    const found = joins.get(join.alias.toLowerCase());
    entries.push({
      key: `join.${index}`,
      kind: "join",
      name: join.alias,
      makes: `${join.alias === join.to ? "" : `${join.to} `}on ${join.on}`,
      found: found === undefined
        ? null
        : found.unmatched > 0
          ? { text: `${found.unmatched.toLocaleString("en-US")} unmatched`, warn: true }
          : joinFoundNothing(found, rows) ? { text: "nothing to join on", warn: true } : { text: `${found.matched.toLocaleString("en-US")} matched`, warn: false },
      verdict: said.get(join.alias.toLowerCase()),
    });
  });
  for (const column of view.columns) {
    if (VIEW_KEY_COLUMNS.includes(column.name.toLowerCase())) {
      continue;
    }

    const found = columns.get(column.name.toLowerCase());
    entries.push({
      key: `columns.${column.name}`,
      kind: "column",
      name: column.name,
      makes: `${column.expression ?? column.name}${column.dataType != null ? `, as ${column.dataType}` : `, ${column.type}`}`,
      found: found?.unconverted != null && found.unconverted > 0 ? { text: `${found.unconverted.toLocaleString("en-US")} not converted`, warn: true } : null,
    });
  }

  return entries;
}

const KIND_LABEL: Record<DeclaredEntry["kind"], string> = { from: "From", join: "Join", column: "Column" };

/** What the templates say of a join, as its glyph alone, with the verdict and its sentence on hover. */
function VerdictGlyph({ verdict }: { verdict: DeliveryDimensionViewJoinVerdict }) {
  const visual = VERDICT_VISUALS[verdict.verdict] ?? VERDICT_VISUALS.unchecked;
  const Icon = visual.icon;
  return (
    <RichTooltip title={`Template: ${visual.label}`} body={verdictText(verdict)}>
      <span className="inline-flex shrink-0 self-center" data-verdict={verdict.verdict}>
        <Icon className={cn("size-3.5", visual.tone)} aria-label={`Template: ${visual.label}`} />
      </span>
    </RichTooltip>
  );
}

/**
 * How the flow's YAML declares the view: its item of the YAML, every line linked to what it declares, and beside it what
 * each declaration makes, with what the newest check found of it. Pointing at either lights the other.
 */
export function ViewDefinition({ detail }: { detail: DeliveryDimensionViewDetail }) {
  const [lit, setLit] = useState<string | null>(null);
  const entries = declaredEntries(detail.view, detail.joinChecks);
  return (
    <div className="grid min-w-0 items-start gap-3 xl:grid-cols-[minmax(0,1.1fr)_minmax(0,0.9fr)]" data-testid="dimension-view-definition">
      <DimensionViewYaml yaml={detail.yaml} lit={lit} onPoint={setLit} />
      <section className="flex min-w-0 flex-col overflow-hidden rounded-lg border border-border bg-card" data-testid="dimension-view-declared">
        <header className="flex min-w-0 items-center gap-2 border-b border-border bg-muted/40 px-3 py-2">
          <span className="text-[13px] font-semibold">What it declares</span>
          <span className="truncate text-[11.5px] text-muted-foreground">and what the newest check found</span>
        </header>
        <ul className="flex min-w-0 flex-col py-1" onMouseLeave={() => setLit(null)}>
          {entries.map((entry, index) => (
            <li
              key={entry.key}
              onMouseEnter={() => setLit(entry.key)}
              className={cn(
                "grid min-w-0 grid-cols-[3.5rem_minmax(0,1fr)_auto] items-baseline gap-x-2 border-l-2 px-3 py-1 text-[12.5px]",
                lit === entry.key ? "border-primary bg-primary/[0.06]" : "border-transparent",
                index > 0 && entries[index - 1].kind !== entry.kind && "mt-1.5",
              )}
              data-testid="dimension-view-declared-entry"
              data-declared={entry.key}
            >
              <span className="text-[11px] uppercase tracking-wider text-muted-foreground">{index === 0 || entries[index - 1].kind !== entry.kind ? KIND_LABEL[entry.kind] : ""}</span>
              <span className="flex min-w-0 items-baseline gap-2">
                <span className="shrink-0 font-mono font-medium">{entry.name}</span>
                <span className="truncate font-mono text-[11.5px] text-muted-foreground" title={entry.makes}>{entry.makes}</span>
              </span>
              <span className="inline-flex items-center gap-2 align-middle whitespace-nowrap text-[11.5px]">
                {entry.verdict !== undefined && <VerdictGlyph verdict={entry.verdict} />}
                {entry.found !== null && (
                  <span className={cn("inline-flex items-center gap-1", !entry.found.warn && "text-muted-foreground")}>
                    {entry.found.warn && <TriangleAlert className="size-3.5 shrink-0 text-warning" aria-hidden />}
                    <span className="font-mono tabular-nums">{entry.found.text}</span>
                  </span>
                )}
              </span>
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}
