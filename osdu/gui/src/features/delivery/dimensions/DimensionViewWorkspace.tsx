import type { ReactNode } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ChevronsUpDown, CircleDashed, CircleSlash, CircleX, Info, PencilLine, Play } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { CopyButton } from "@/components/CopyButton";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import { deliveryApi, type DeliveryDimensionFlow, type DeliveryDimensionView, type DeliveryDimensionViewDetail } from "../../../api/delivery";
import { useActivePartition } from "../activePartition";
import { ProblemView } from "../TemplateSheet";
import { DimensionsCrumb } from "./DimensionBadges";
import type { DimensionLaunch } from "./DimensionBuildDialog";
import { ViewStandingGlyph } from "./DimensionViewBadges";
import { DimensionViewRemoveButton } from "./DimensionViewRemoveButton";
import { ViewChecks, ViewColumns, ViewDefinition, ViewJoins, ViewSql, type OpenDimension } from "./DimensionViewTabs";
import { Fact } from "./DimensionWorkspace";
import { dimensionRef } from "./dimensionFormat";
import {
  VIEW_KEY_COLUMNS, VIEW_STANDING_VISUALS, VIEW_TABS, checkWhen, unconvertedOf, unmatchedOf, utcText, viewObject, viewStandingOf, type ViewTab,
} from "./dimensionViewFormat";

/** How often the view is read again, so a build under way shows what it wrote and checked as it lands. */
const REFRESH_MS = 15000;

/** What each tab is for, as hovering its name says it. */
const TAB_PURPOSE: Record<ViewTab, string> = {
  columns: "The view's columns in order, partition and id first: each one's type, the expression it is computed by, the type it is converted to, and what the newest check found: the rows holding a value, and the values the conversion could not read, with examples.",
  joins: "The dimensions joined to the from dimension, in order: the alias each one's columns are read by and the column joined on, with the rows the newest check found their row for, and those whose value named none.",
  checks: "Every check a build made of the view, newest first: the partition, what it came to, the rows it read and what it found.",
  sql: "The statement that writes the view: as a build last wrote it, as the flow declares it now, and the difference the next build writes.",
  definition: "How the flow's YAML declares the view, every line linked to what it declares. Point at a line to see what it makes.",
};

const TAB_LABEL: Record<ViewTab, string> = {
  columns: "Columns",
  joins: "Joins",
  checks: "Checks",
  sql: "SQL",
  definition: "Definition",
};

/** A tab's name with what the tab is for on hover; the hover sits on the name, so it never touches the tab's own state. */
function TabName({ tab, children }: { tab: ViewTab; children: ReactNode }) {
  return (
    <RichTooltip body={TAB_PURPOSE[tab]}>
      <span>{children}</span>
    </RichTooltip>
  );
}

/** What the view's newest check asks of the reader: a check that could not read it. */
function CheckAttention({ view }: { view: DeliveryDimensionView }) {
  const check = view.lastCheck;
  if (check?.status !== "failed") {
    return null;
  }

  return (
    <Alert variant="destructive" data-testid="dimension-view-check-failed">
      <CircleX />
      <AlertTitle>The newest check could not read the view <RelativeTime value={check.checkedUtc} absolute={false} /></AlertTitle>
      <AlertDescription className="flex flex-col gap-1">
        <span className="break-words">{check.error ?? "The check kept no reason."}</span>
        <span className="text-muted-foreground">The view keeps its definition: what fails is the data in {check.partition}. The next build that reads it whole passes.</span>
      </AlertDescription>
    </Alert>
  );
}

/** What the view's standing asks of the reader, when anything does. */
function Attention({ detail, onRemoved }: { detail: DeliveryDimensionViewDetail; onRemoved: () => void }) {
  const { view } = detail;
  const standing = viewStandingOf(view);
  switch (standing) {
    case "changed":
      return (
        <Alert className="border-warning/40 bg-warning/8" data-testid="dimension-view-changed">
          <PencilLine className="text-warning" />
          <AlertTitle>The flow declares the view differently from how it was written</AlertTitle>
          <AlertDescription>The next build of {view.flowName} writes it again; the SQL tab shows the difference.</AlertDescription>
        </Alert>
      );
    case "dropped":
      return (
        <Alert className="border-warning/40 bg-warning/8" data-testid="dimension-view-dropped">
          <CircleSlash className="text-warning" />
          <AlertTitle>The database does not hold the view now</AlertTitle>
          <AlertDescription className="break-words">{view.note ?? VIEW_STANDING_VISUALS.dropped.hint}</AlertDescription>
        </Alert>
      );
    case "notWritten":
      return (
        <Alert data-testid="dimension-view-not-written">
          <CircleDashed />
          <AlertTitle>{VIEW_STANDING_VISUALS.notWritten.label}</AlertTitle>
          <AlertDescription>{VIEW_STANDING_VISUALS.notWritten.hint}</AlertDescription>
        </Alert>
      );
    case "undeclared":
      return (
        <Alert data-testid="dimension-view-undeclared">
          <Info />
          <AlertTitle>{VIEW_STANDING_VISUALS.undeclared.label}</AlertTitle>
          <AlertDescription className="flex flex-wrap items-center gap-x-3 gap-y-1">
            <span>{VIEW_STANDING_VISUALS.undeclared.hint}</span>
            <DimensionViewRemoveButton view={view} onRemoved={onRemoved} />
          </AlertDescription>
        </Alert>
      );
    default:
      return null;
  }
}

/** The other views, one click away from the one in view. */
function Switcher({ name, onOpen }: { name: string; onOpen: (name: string) => void }) {
  const views = useQuery({
    queryKey: ["delivery", "dimensions", "views", "all"],
    queryFn: () => deliveryApi.dimensionViews(),
    staleTime: 60_000,
  });
  const others = (views.data ?? []).filter((view) => view.name.toLowerCase() !== name.toLowerCase());
  if (others.length === 0) {
    return null;
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="icon" className="size-6 text-muted-foreground" aria-label="Open another view" data-testid="dimension-view-switcher">
          <ChevronsUpDown />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="max-h-80 min-w-64 overflow-y-auto">
        {(views.data ?? []).map((view) => (
          <DropdownMenuItem
            key={view.name}
            onSelect={() => onOpen(view.name)}
            className={cn("gap-2", view.name.toLowerCase() === name.toLowerCase() && "bg-accent/60")}
            data-testid="dimension-view-switcher-item"
          >
            <ViewStandingGlyph view={view} />
            <span className="min-w-0 flex-1 truncate">{view.name}</span>
            <span className="shrink-0 font-mono text-[11px] text-muted-foreground">{view.flowName}</span>
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/** When the view was last written and by whom, as a hover says it; empty when no build has written it. */
function writtenText(view: DeliveryDimensionView): string {
  return view.writtenUtc == null
    ? ""
    : `Written ${utcText(view.writtenUtc)} by ${view.writtenBy ?? "a build"}${view.writtenRunId != null ? ` in run ${view.writtenRunId}` : ""}; the SQL tab shows the statement.`;
}

/**
 * The facts the view is read against, in a line: its rows, what its joins and conversions left, the joins its templates say
 * find nothing, and when it was checked (in another partition than the title bar's, which), with when it was written on
 * hover; each opens the tab that explains it.
 */
function Facts({ view, differs, onTab }: { view: DeliveryDimensionView; differs: number; onTab: (tab: ViewTab) => void }) {
  const [active] = useActivePartition();
  const check = view.lastCheck;
  const dot = <span className="text-muted-foreground/50" aria-hidden>·</span>;
  const unmatched = unmatchedOf(check);
  const unconverted = unconvertedOf(check);
  const written = writtenText(view);
  return (
    <div className="flex flex-wrap items-center gap-x-1.5 gap-y-0.5 text-[13px]" data-testid="dimension-view-summary">
      {check == null
        ? <span>not checked yet</span>
        : (
          <>
            {check.status === "passed" && (
              <>
                <Fact hint={checkWhen(check)} onClick={() => onTab("checks")} testId="dimension-view-summary-rows">
                  <span className="font-mono font-medium tabular-nums text-foreground">{check.rows.toLocaleString("en-US")}</span> {check.rows === 1 ? "row" : "rows"}
                </Fact>
                {unmatched > 0 && (
                  <>
                    {dot}
                    <Fact hint="Rows whose value names no row of the dimension joined, so the view holds null in that join's columns. The Joins tab says which join, with examples." tone="warning" onClick={() => onTab("joins")} testId="dimension-view-summary-unmatched">
                      <span className="font-mono tabular-nums">{unmatched.toLocaleString("en-US")}</span> unmatched
                    </Fact>
                  </>
                )}
                {unconverted > 0 && (
                  <>
                    {dot}
                    <Fact hint="Values a column's conversion could not read, so the view holds null for them. The Columns tab says which column, with examples by row." tone="warning" onClick={() => onTab("columns")} testId="dimension-view-summary-unconverted">
                      <span className="font-mono tabular-nums">{unconverted.toLocaleString("en-US")}</span> not converted
                    </Fact>
                  </>
                )}
                {dot}
              </>
            )}
            <Fact hint={`${checkWhen(check)}${written === "" ? "" : ` ${written}`}`} onClick={() => onTab("checks")} testId="dimension-view-summary-checked">
              {check.status === "failed" ? "check failed" : "checked"} <RelativeTime value={check.checkedUtc} absolute={false} />
              {check.partition !== active && <> in <span className="font-mono">{check.partition}</span></>}
            </Fact>
          </>
        )}
      {differs > 0 && (
        <>
          {dot}
          <Fact
            hint="Joins to a dimension of another entity type than the template says their column names: such a join finds nothing. The Joins tab says which, and what the template names."
            tone="warning"
            onClick={() => onTab("joins")}
            testId="dimension-view-summary-differs"
          >
            <span className="font-mono tabular-nums">{differs.toLocaleString("en-US")}</span> {differs === 1 ? "join differs" : "joins differ"} from the template
          </Fact>
        </>
      )}
      {check == null && view.writtenUtc != null && (
        <>
          {dot}
          <Fact hint={written} onClick={() => onTab("sql")} testId="dimension-view-summary-written">
            written <RelativeTime value={view.writtenUtc} absolute={false} />
          </Fact>
        </>
      )}
    </div>
  );
}

/**
 * One view of a dimension flow, on the whole width of the Dimensions page
 * (osdu/docs/reference/flow/dimension-views.md). Its heading is one block: the way back to every dimension, its name
 * and where it stands, the other views a click away, and Run pipeline; under it, in a line, what its newest check found
 * (rows, what the joins and conversions left) and when it was checked and written, each opening the tab that explains
 * it, then what it is in the database, what it reads and the flow that declares it. Then what its check or its standing
 * asks of the reader, and its tabs, whose grids scroll inside the page: its columns, its joins, its checks, its SQL and
 * how the flow's YAML declares it.
 */
export function DimensionViewWorkspace({ name, tab, onTab, onOpen, onBack, onOpenDimension, flows, onLaunch }: {
  name: string;
  tab: ViewTab;
  onTab: (tab: ViewTab) => void;
  /** Opens another view by its name. */
  onOpen: (name: string) => void;
  /** Back to every dimension; also where the page goes once an admin removed the view. */
  onBack: () => void;
  /** Opens a dimension by its link name on the Dimensions page. */
  onOpenDimension: (ref: string) => void;
  /** The dimension flows the page knows, so the view's flow can be run and its dimensions opened. */
  flows: DeliveryDimensionFlow[];
  onLaunch: (launch: DimensionLaunch) => void;
}) {
  const detail = useQuery({
    queryKey: ["delivery", "dimensions", "views", "detail", name],
    queryFn: () => deliveryApi.dimensionView(name),
    refetchInterval: REFRESH_MS,
  });

  if (detail.isError) {
    return (
      <div className="flex min-w-0 flex-col gap-3" data-testid="dimension-view-workspace">
        <PageHeader title={<span className="flex items-center gap-x-1.5"><DimensionsCrumb onBack={onBack} /><span>{name}</span></span>} />
        <ProblemView error={detail.error} testId="dimension-view-error" />
      </div>
    );
  }

  if (detail.data === undefined) {
    return (
      <div className="flex min-w-0 flex-col gap-3" data-testid="dimension-view-workspace">
        <PageHeader title={<span className="flex items-center gap-x-1.5"><DimensionsCrumb onBack={onBack} /><span>{name}</span></span>} />
        <Skeleton className="h-10 w-full rounded-lg" />
        <Skeleton className="h-80 w-full rounded-lg" />
      </div>
    );
  }

  const { view } = detail.data;
  const standing = viewStandingOf(view);
  const flow = view.pipelineId == null ? undefined : flows.find((candidate) => candidate.pipelineId === view.pipelineId);
  const openDimension: OpenDimension = flow === undefined
    ? null
    : (dimension: string) => {
      const found = flow.dimensions.find((candidate) => candidate.name.toLowerCase() === dimension.toLowerCase());
      onOpenDimension(found === undefined ? `${flow.pipelineId}/${dimension}` : dimensionRef(flow, found));
    };
  // How many of each a tab lists, beside its name: the columns the flow lists (partition and id aside), the joins, the checks.
  const counts: Partial<Record<ViewTab, number>> = {
    columns: view.columns.filter((column) => !VIEW_KEY_COLUMNS.includes(column.name.toLowerCase())).length,
    joins: view.joins.length,
    checks: detail.data.checks.length,
  };
  const dot = <span className="text-muted-foreground/50" aria-hidden>·</span>;

  return (
    <div className="flex min-w-0 flex-col gap-3" data-testid="dimension-view-workspace" data-view={view.name}>
      <PageHeader
        title={(
          <span className="flex min-w-0 flex-wrap items-center gap-x-1.5 gap-y-1">
            <DimensionsCrumb onBack={onBack} />
            <ViewStandingGlyph view={view} testId="dimension-view-standing" />
            <span className="min-w-0 truncate" data-testid="dimension-view-name">{view.name}</span>
            <Switcher name={view.name} onOpen={onOpen} />
            {standing !== "written" && <span className="text-[12px] font-normal text-muted-foreground">{VIEW_STANDING_VISUALS[standing].label}</span>}
            {view.description != null && (
              <RichTooltip title="What it holds" body={view.description}>
                <Info className="size-4 shrink-0 text-muted-foreground" aria-label="What the view holds" data-testid="dimension-view-description" />
              </RichTooltip>
            )}
          </span>
        )}
        subtitle={(
          <div className="flex flex-col gap-0.5">
            <Facts view={view} differs={(detail.data.joinChecks ?? []).filter((verdict) => verdict.verdict === "differs").length} onTab={onTab} />
            <div className="flex min-w-0 flex-wrap items-center gap-x-1.5 gap-y-0.5 text-[12px]">
              <span className="inline-flex items-center gap-0.5">
                <span className="font-mono text-foreground" data-testid="dimension-view-object">{viewObject(view)}</span>
                <CopyButton iconOnly label="Copy the view's name in the database" text={viewObject(view)} testId="dimension-view-object-copy" />
              </span>
              {dot}
              <span>from</span>
              <span className="text-foreground">
                {openDimension === null
                  ? <span className="font-mono">{view.from}</span>
                  : <button type="button" onClick={() => openDimension(view.from)} className="rounded-sm font-mono outline-none hover:underline focus-visible:underline" data-testid="dimension-view-from">{view.from}</button>}
              </span>
              {dot}
              <span>declared by</span>
              {view.pipelineId != null
                ? <Link to={`/pipelines/${view.pipelineId}?tab=dimensions`} className="font-mono text-foreground hover:underline" data-testid="dimension-view-flow">{view.flowName}</Link>
                : <span className="font-mono text-foreground" data-testid="dimension-view-flow">{view.flowName}</span>}
            </div>
          </div>
        )}
        actions={view.declared && flow !== undefined
          ? (
            <RichTooltip body={`Runs ${flow.name}: a build writes the flow's views after its dimensions, whichever it builds, and checks them in the run's partition. The dialog picks the dimensions it builds.`}>
              <Button size="sm" onClick={() => onLaunch({ pipelineId: flow.pipelineId, repoId: flow.repoId, flowName: flow.name, dimensions: [] })} data-testid="dimension-view-build">
                <Play />
                Run pipeline
              </Button>
            </RichTooltip>
          )
          : undefined}
      />

      <CheckAttention view={view} />
      <Attention detail={detail.data} onRemoved={onBack} />

      <Tabs value={tab} onValueChange={(next) => onTab(next as ViewTab)} className="min-w-0 gap-2">
        <div className="border-b border-border">
          <TabsList variant="line" data-testid="dimension-view-tabs">
            {VIEW_TABS.map((value) => (
              <TabsTrigger key={value} value={value} data-testid={`dimension-view-tab-${value}`}>
                <TabName tab={value}>{TAB_LABEL[value]}</TabName>
                {counts[value] !== undefined && <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{counts[value]}</span>}
              </TabsTrigger>
            ))}
          </TabsList>
        </div>
        <TabsContent value="columns">
          <ViewColumns view={view} />
        </TabsContent>
        <TabsContent value="joins">
          <ViewJoins view={view} verdicts={detail.data.joinChecks} onOpenDimension={openDimension} />
        </TabsContent>
        <TabsContent value="checks">
          <ViewChecks checks={detail.data.checks} />
        </TabsContent>
        <TabsContent value="sql">
          <ViewSql detail={detail.data} />
        </TabsContent>
        <TabsContent value="definition">
          <ViewDefinition detail={detail.data} />
        </TabsContent>
      </Tabs>
    </div>
  );
}
