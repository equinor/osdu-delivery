import { createContext, useContext, useMemo, useState } from "react";
import {
  Braces,
  Brackets,
  Calendar,
  ChevronsDownUp,
  ChevronsUpDown,
  Circle,
  CircleCheck,
  CircleDot,
  Cloud,
  Cog,
  Contrast,
  DatabaseZap,
  FileJson,
  Hash,
  Info,
  Link2,
  List,
  ListChecks,
  OctagonAlert,
  Ruler,
  SlidersHorizontal,
  ToggleLeft,
  TriangleAlert,
  Type,
  type LucideIcon,
} from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from "@/components/ui/resizable";
import { Switch } from "@/components/ui/switch";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { cn } from "@/lib/utils";
import type {
  CoverageState, DeliveryMappingCoverage, DeliveryTemplateRole, DeliveryTemplateVariable, MappingDraftEntry, MappingDraftIssue,
} from "../../api/delivery";
import { CopyButton } from "@/components/CopyButton";
import { DetailPair } from "@/components/DetailPair";
import { FilterBar, activeFilterClass } from "@/components/FilterBar";
import { GlyphRef } from "@/components/GlyphRef";
import { IconAction } from "@/components/IconAction";
import { SearchInput } from "@/components/SearchInput";
import { StatePill } from "@/components/StatusBadge";
import { TreeNode, TreeRoot, type TreeState } from "@/components/Tree";
import { usePersistentLayout } from "@/layout/workbench/usePersistentLayout";
import { assertionLine, assertionText } from "./mappingAssertionText";
import { entryText, propertyRow } from "./mappingDraft";
import { EntryDetail } from "./MappingEntryDetail";
import { holderPath, roleLabel, shapeText, splitPath } from "./templateFormat";
import { useValueCheckContext } from "./useValueCheck";
import { CheckValuesButton, DataTabBadge, ValueCheckChip, VariableDataTab } from "./ValueCheckResult";
import { outcomeVisual } from "./valueCheck";
import { withKeyVariables } from "./variableRows";

/** One variable in the tree, with the variables it holds that survive the filter. */
interface VariableNode {
  variable: DeliveryTemplateVariable;
  /** True when the variable itself passes the filter; false for a holder kept only because something inside it does. */
  matched: boolean;
  children: VariableNode[];
}

/** A mapping laid over the template: what it fills of it, and the entries doing the filling. */
export interface MappingOverlay {
  /** What the mapping fills, variable by variable, as the coverage endpoint answers it. */
  coverage: DeliveryMappingCoverage;
  /** The document's entries, so a variable can show what fills it and not only that something does. */
  entries: MappingDraftEntry[];
}

/**
 * How the mapping being looked at reaches one variable, what fills it, and what the checks found there. A row shows the
 * state as a glyph and nothing more: the entry itself is on the row's hover and in the properties beside the tree,
 * where it can be read whole instead of crowding every row.
 */
interface CoverageView {
  state: CoverageState;
  /** The entry filling the variable itself; null for an object filled through what it holds, and for an empty one. */
  entry: MappingDraftEntry | null;
  /** For a variable an entry further up writes (its static value, or a value it writes whole): that entry's target. */
  writtenBy: string | null;
  /** The entry at writtenBy, when the draft has it. */
  holder: MappingDraftEntry | null;
  /** With writtenBy: the values that static value gives the variable, once each. */
  values: string[];
  /** The worst finding on the variable, and what it says. */
  finding: MappingDraftIssue | null;
}

/**
 * What the mapping fills, by variable, for the tree to show. Null when no mapping is being looked at, which is how the
 * Templates page reads the same tree: a template on its own, with nothing to say about who fills it.
 */
const CoverageContext = createContext<ReadonlyMap<string, CoverageView> | null>(null);

/**
 * How a variable's state reads: the word, the glyph and the tone, for a row and for the properties beside it. A
 * variable the mapping fills carries a check whether or not every record ends up with it, because it is filled either
 * way; the tone is what says a record may go without it. Only what nothing fills reads as empty.
 */
function coverageVisual(state: CoverageState): { label: string; tone: "success" | "warning" | "muted"; icon: LucideIcon; textClass: string } {
  switch (state) {
    case "Always":
      return { label: "Filled on every row", tone: "success", icon: CircleCheck, textClass: "text-success" };
    case "Sometimes":
      return { label: "Filled on some rows", tone: "warning", icon: CircleCheck, textClass: "text-warning" };
    case "Empty":
      return { label: "Not filled", tone: "muted", icon: Circle, textClass: "text-muted-foreground" };
  }
}

/**
 * Whether the overview leaves a variable out: one the mapping does not reach and no check names. A variable the
 * coverage says nothing about (what OSDU writes, a nested list) is left to the option that lists its kind.
 */
function inOverview(covered: CoverageView | undefined): boolean {
  return covered !== undefined && covered.state === "Empty" && covered.finding === null;
}

/**
 * Whether a variable is one of the gaps: nothing fills it, or a check names it. An entry that may leave a value out is
 * not a gap, because the mapping does fill that variable; what such an entry costs is said where it is required, by
 * the check that names it.
 */
function isGap(covered: CoverageView | undefined): boolean {
  return covered === undefined || covered.state === "Empty" || covered.finding !== null;
}

/**
 * Whether a variable carries the filter text: its path, title, description or the entity types it points to, and what
 * the mapping fills it with when there is one, so a source column, a cached type or a modifier finds its variables.
 */
function textMatches(variable: DeliveryTemplateVariable, term: string, covered: CoverageView | undefined): boolean {
  return term === ""
    || variable.path.toLowerCase().includes(term)
    || (variable.title ?? "").toLowerCase().includes(term)
    || (variable.description ?? "").toLowerCase().includes(term)
    || variable.relationships.some((relationship) => relationship.toLowerCase().includes(term))
    || (covered?.entry != null && entryText(covered.entry).toLowerCase().includes(term))
    || (covered?.entry?.assertions ?? []).some((a) => `${a.name ?? ""} ${assertionText(a)}`.toLowerCase().includes(term))
    || (covered?.values ?? []).some((value) => value.toLowerCase().includes(term));
}

/**
 * What fills a variable, on one line for a row's hover: its entry with the assertions it states, the static value further
 * up that writes it, or the items of a list of objects that do.
 */
function fillText(covered: CoverageView | undefined): string[] {
  if (covered?.entry != null) {
    const entry = covered.entry;
    return [entryText(entry), ...(entry.assertions ?? []).map((a) => `asserts ${assertionLine(a, entry.target, entry.target)}`)];
  }

  if (covered?.writtenBy != null && covered.holder?.input === "List") {
    return [`items of ${covered.writtenBy}${covered.values.length === 0 ? `: ${entryText(covered.holder)}` : `: ${covered.values.join(", ")}`}`];
  }

  if (covered?.writtenBy != null) {
    return covered.holder?.input === "Static" || covered.values.length > 0
      ? [`static value of ${covered.writtenBy}${covered.values.length === 0 ? "" : `: ${covered.values.join(", ")}`}`]
      : [`written whole by ${covered.writtenBy}${covered.holder === null ? "" : `: ${entryText(covered.holder)}`}`];
  }

  return [];
}

/**
 * The variables as a tree: each one under the variable that holds it, in schema order. A variable the switches hide
 * passes its children up to the nearest holder still shown, and a holder with no match inside it is dropped. The API
 * lists parents before their children, so a holder is always placed before anything it holds.
 */
function buildTree(
  variables: DeliveryTemplateVariable[],
  include: (variable: DeliveryTemplateVariable) => boolean,
  matches: (variable: DeliveryTemplateVariable) => boolean,
): { roots: VariableNode[]; matched: number } {
  const placed = new Map<string, VariableNode>();
  const roots: VariableNode[] = [];
  for (const variable of variables) {
    if (!include(variable)) {
      continue;
    }

    const node: VariableNode = { variable, matched: matches(variable), children: [] };
    placed.set(variable.path, node);
    let holder = holderPath(variable.path);
    while (holder !== null && !placed.has(holder)) {
      holder = holderPath(holder);
    }

    const parent = holder === null ? undefined : placed.get(holder);
    if (parent === undefined) {
      roots.push(node);
    } else {
      parent.children.push(node);
    }
  }

  let matched = 0;
  const prune = (nodes: VariableNode[]): VariableNode[] => nodes.flatMap((node) => {
    const children = prune(node.children);
    if (node.matched) {
      matched += 1;
    }
    return node.matched || children.length > 0 ? [{ ...node, children }] : [];
  });
  return { roots: prune(roots), matched };
}

/**
 * Every holder on the way to a variable that matches, so a match is never hidden inside a collapsed branch. Nothing is
 * revealed while the tree shows everything: a reader opening a fresh template reads its sections as an outline first.
 */
function revealedPaths(
  variables: DeliveryTemplateVariable[],
  matches: (variable: DeliveryTemplateVariable) => boolean,
  narrowed: boolean,
): ReadonlySet<string> {
  const paths = new Set<string>();
  if (!narrowed) {
    return paths;
  }

  for (const variable of variables) {
    if (matches(variable)) {
      for (let holder = holderPath(variable.path); holder !== null; holder = holderPath(holder)) {
        paths.add(holder);
      }
    }
  }
  return paths;
}

/**
 * How a variable no mapping may fill stands out from the ones a mapping fills: a glyph and a tone per writer, so a
 * variable OSDU sets reads differently from one OSDU Delivery writes at a glance. Null for a variable a mapping fills.
 */
function roleVisual(role: DeliveryTemplateRole): { label: string; tone: "info" | "warning"; icon: LucideIcon; textClass: string } | null {
  const label = roleLabel(role);
  if (label === null) {
    return null;
  }

  return role === "Osdu"
    ? { label, tone: "info", icon: Cloud, textClass: "text-info" }
    : { label, tone: "warning", icon: Cog, textClass: "text-warning" };
}

/** The glyph for a variable's shape: an object, a list of objects, a list of values, a whole value, or one value by its type. */
function shapeIcon(variable: DeliveryTemplateVariable): LucideIcon {
  switch (variable.shape) {
    case "Group":
      return Braces;
    case "GroupList":
      return Brackets;
    case "ValueList":
      return List;
    case "Whole":
      return FileJson;
    case "Value":
      if (variable.format === "date" || variable.format === "date-time") {
        return Calendar;
      }
      if (variable.type === "number" || variable.type === "integer") {
        return Hash;
      }
      if (variable.type === "boolean") {
        return ToggleLeft;
      }
      return variable.type === "string" ? Type : CircleDot;
  }
}

function Glyph({ icon: Icon, className }: { icon: LucideIcon; className: string }) {
  return <Icon className={className} />;
}

/**
 * How a row marks what the mapping makes of its variable. Only what asks for attention is drawn: a finding (an error or a
 * warning), a variable filled on some rows only (an optional entry, or one with a condition), and one nothing fills.
 * A variable filled on every row, and an object whatever it holds, draw nothing: the row's silence is the good news, and a
 * tree of green marks buries the few that matter. The state is still said to assistive tech and on hover.
 */
function rowMark(variable: DeliveryTemplateVariable, covered: CoverageView): { icon: LucideIcon; className: string } | null {
  if (covered.finding !== null) {
    return covered.finding.severity === "error"
      ? { icon: OctagonAlert, className: "text-destructive" }
      : { icon: TriangleAlert, className: "text-warning" };
  }

  if (covered.state === "Empty") {
    return { icon: Circle, className: "text-muted-foreground/60" };
  }

  const holder = variable.shape === "Group" || variable.shape === "GroupList";
  return covered.state === "Sometimes" && !holder ? { icon: Contrast, className: "text-muted-foreground" } : null;
}

/**
 * A tree row's content: the shape glyph, the variable's name, and its shape at the far end, with a mark only where
 * something asks for attention: a required marker, a finding or a partial fill of the mapping, what a data check found
 * wrong, and who writes a variable no mapping fills.
 */
function VariableLabel({ node }: { node: VariableNode }) {
  const { variable } = node;
  const role = roleVisual(variable.role);
  const covered = useContext(CoverageContext)?.get(variable.path);
  const coverage = covered === undefined ? null : coverageVisual(covered.state);
  const finding = covered?.finding ?? null;
  const mark = covered === undefined ? null : rowMark(variable, covered);
  return (
    <>
      <Glyph icon={shapeIcon(variable)} className={cn("size-3.5 shrink-0", role?.textClass ?? "text-muted-foreground")} />
      <span
        className={cn(
          "min-w-0 truncate font-mono text-[12px]",
          role?.textClass,
          finding?.severity === "error" && "text-destructive",
          !node.matched && "opacity-60",
        )}
        title={variable.path}
        data-testid={`templates-view-variable-${variable.path}`}
        data-role={variable.role}
      >
        {splitPath(variable.path).leaf}
      </span>
      {variable.required && (
        <span className="shrink-0 font-mono text-[12px] font-semibold text-destructive" title="Required" aria-label="Required">*</span>
      )}
      {coverage !== null && (
        <span
          role="img"
          aria-label={finding?.message ?? coverage.label}
          title={[finding?.message ?? coverage.label, ...fillText(covered)].join("\n")}
          className={cn("shrink-0", mark === null ? "sr-only" : cn("inline-flex", mark.className))}
          data-testid={`templates-view-coverage-${variable.path}`}
          data-coverage={covered?.state}
        >
          {mark !== null && <Glyph icon={mark.icon} className="size-3" />}
        </span>
      )}
      <AssertionMark covered={covered} path={variable.path} />
      <ValueCheckChip path={variable.path} />
      {role !== null && (
        <span
          role="img"
          aria-label={role.label}
          title={role.label}
          className={cn("inline-flex shrink-0", role.textClass)}
          data-testid={`templates-view-role-${variable.path}`}
        >
          <Glyph icon={role.icon} className="size-3" />
        </span>
      )}
      <span className="ml-auto shrink-0 pl-3 font-mono text-[11px] font-normal text-muted-foreground">{shapeText(variable)}</span>
    </>
  );
}

/**
 * The quiet mark of a variable whose entry states assertions (osdu/docs/reference/flow/mapping-assertions.md): rules its
 * value is held to beside the template's, listed in the tooltip. It is drawn muted, since a rule asks for no attention
 * until a check finds a value breaking it, which the value check's chip says.
 */
function AssertionMark({ covered, path }: { covered: CoverageView | undefined; path: string }) {
  const entry = covered?.entry ?? null;
  const assertions = entry?.assertions ?? [];
  if (entry === null || assertions.length === 0) {
    return null;
  }

  const count = `${assertions.length} assertion${assertions.length === 1 ? "" : "s"}`;
  return (
    <span
      role="img"
      aria-label={count}
      title={[count, ...assertions.map((a) => assertionLine(a, entry.target, entry.target))].join("\n")}
      className="inline-flex shrink-0 text-muted-foreground"
      data-testid={`templates-view-assertions-${path}`}
    >
      <Glyph icon={ListChecks} className="size-3" />
    </span>
  );
}

function VariableBranch({ node }: { node: VariableNode }) {
  return (
    <TreeNode id={node.variable.path} label={<VariableLabel node={node} />}>
      {node.children.length === 0
        ? undefined
        : node.children.map((child) => <VariableBranch key={child.variable.path} node={child} />)}
    </TreeNode>
  );
}

/** A list of kinds under a caption with its glyph and count: what a variable points to, or the cached types that answer it. */
function KindList({ icon, label, values, testId }: { icon: LucideIcon; label: string; values: string[]; testId: string }) {
  if (values.length === 0) {
    return null;
  }

  return (
    <DetailPair label={<span className="inline-flex items-center gap-1"><Glyph icon={icon} className="size-3" />{label} ({values.length})</span>}>
      <ul className="flex flex-col gap-0.5 font-mono text-[12px]" data-testid={testId}>
        {values.map((value, index) => <li key={`${index}:${value}`} className="break-all">{value}</li>)}
      </ul>
    </DetailPair>
  );
}

/** The tabs of a variable's properties, kept as the reader moves from one variable to the next. */
type DetailTab = "fill" | "data" | "schema";

/**
 * The selected variable: its name, path and the facts that shape it at the top, then, with a mapping laid over the
 * template, three tabs a reader moves between as they go down the tree: what fills it, what a data check found of it,
 * and what the schema says. Without a mapping (the Templates page) the schema is all there is, and it is shown as it is.
 */
function VariableProperties({ variable, holds, tab, onTab }: {
  variable: DeliveryTemplateVariable;
  holds: number;
  tab: DetailTab;
  onTab: (tab: DetailTab) => void;
}) {
  const { leaf } = splitPath(variable.path);
  const role = roleVisual(variable.role);
  const covered = useContext(CoverageContext)?.get(variable.path);
  const valueCheck = useValueCheckContext();
  // What nothing of the mapping fills has no values to check.
  const checkable = covered !== undefined && (covered.entry !== null || covered.writtenBy !== null || covered.state !== "Empty");
  const shown: DetailTab = tab === "data" && valueCheck === null ? "fill" : tab;
  return (
    <div className="flex flex-col gap-3" data-testid="templates-view-properties-variable">
      <div className="flex items-start gap-2">
        <div className="flex min-w-0 flex-1 flex-col gap-0.5">
          <div className="flex min-w-0 items-center gap-1">
            <span className={cn("min-w-0 break-all font-mono text-[14px] font-semibold", role?.textClass)}>{leaf}</span>
            <CopyButton iconOnly label="Copy the path" text={variable.path} testId="templates-view-properties-copy" />
          </div>
          <span className="break-all font-mono text-[12px] text-muted-foreground" data-testid="templates-view-properties-path">
            {variable.path}
          </span>
        </div>
        {covered !== undefined && valueCheck !== null && (
          <CheckValuesButton path={variable.path} checkable={checkable} onCheck={() => onTab("data")} />
        )}
      </div>

      {(variable.required || covered?.entry != null || role !== null || variable.nested) && (
        <div className="flex flex-wrap items-center gap-1.5">
          {variable.required && <Badge variant="secondary" className="text-[10px]" data-testid="templates-view-required">Required</Badge>}
          {/* A holder reads from what it holds, so its own entry being optional is said here, where it is not lost. */}
          {covered?.entry != null && !covered.entry.required && (
            <Badge variant="outline" className="text-[10px]" data-testid="templates-view-properties-optional">optional</Badge>
          )}
          {role !== null && <StatePill tone={role.tone} label={role.label} icon={role.icon} testId="templates-view-properties-role" />}
          {variable.nested && <Badge variant="outline" className="text-[10px]">nested list</Badge>}
        </div>
      )}

      {covered?.finding != null && (
        <p
          className={cn(
            "flex items-start gap-1.5 rounded-md border px-2.5 py-2 text-[13px]",
            covered.finding.severity === "error" ? "border-destructive/40 bg-destructive/5 text-destructive" : "border-warning/40 bg-warning/5 text-warning",
          )}
          data-testid="templates-view-properties-finding"
        >
          {covered.finding.severity === "error"
            ? <OctagonAlert className="mt-0.5 size-3.5 shrink-0" />
            : <TriangleAlert className="mt-0.5 size-3.5 shrink-0" />}
          {covered.finding.message}
        </p>
      )}

      {covered === undefined
        ? <SchemaDetails variable={variable} holds={holds} />
        : (
          <Tabs value={shown} onValueChange={(next) => onTab(next as DetailTab)} className="gap-3">
            <TabsList variant="line" className="w-full justify-start gap-4 border-b" data-testid="templates-view-properties-tabs">
              <TabsTrigger value="fill" className="flex-none px-0.5 text-[13px]" data-testid="templates-view-properties-tab-fill">Filled by</TabsTrigger>
              {valueCheck !== null && (
                <TabsTrigger value="data" className="flex-none px-0.5 text-[13px]" data-testid="templates-view-properties-tab-data">
                  Data
                  <DataTabBadge path={variable.path} />
                </TabsTrigger>
              )}
              <TabsTrigger value="schema" className="flex-none px-0.5 text-[13px]" data-testid="templates-view-properties-tab-schema">Schema</TabsTrigger>
            </TabsList>
            <TabsContent value="fill">
              <FillDetails covered={covered} />
            </TabsContent>
            {valueCheck !== null && (
              <TabsContent value="data">
                <VariableDataTab path={variable.path} checkable={checkable} />
              </TabsContent>
            )}
            <TabsContent value="schema">
              <SchemaDetails variable={variable} holds={holds} />
            </TabsContent>
          </Tabs>
        )}
    </div>
  );
}

/**
 * What fills a variable: whether it reaches every row, and the entry that fills it laid out as the pipeline it is, the
 * static value further up that writes it, or the entries filling what it holds.
 */
function FillDetails({ covered }: { covered: CoverageView }) {
  const coverage = coverageVisual(covered.state);
  return (
    <div className="flex flex-col gap-3">
      <div>
        <StatePill tone={coverage.tone} label={coverage.label} icon={coverage.icon} testId="templates-view-properties-coverage" />
      </div>
      {covered.entry === null && covered.writtenBy === null && (
        <p className="text-[13px] text-muted-foreground">
          {covered.state === "Empty"
            ? "No entry of the mapping fills it."
            : "The entries filling what it holds fill it; select one of them in the tree to read it."}
        </p>
      )}
      {covered.entry === null && covered.writtenBy !== null && (
        <div data-testid="templates-view-properties-written-by">
          {covered.holder?.input === "List"
            ? (
              <span className="text-[13px]">
                {"The items of "}
                <span className="font-mono text-[12px]">{covered.writtenBy}</span>
                {covered.values.length === 0 ? ", which write it" : ", which give it"}
                <span className="mt-1 block font-mono text-[12px] break-all">{entryText(covered.holder)}</span>
              </span>
            )
            : covered.holder?.input === "Static" || covered.values.length > 0
            ? (
              <span className="text-[13px]">
                {covered.holder?.input === "Static" ? "The static value of " : "A static alternative of "}
                <span className="font-mono text-[12px]">{covered.writtenBy}</span>
                {covered.values.length === 0 ? ", which writes what it holds" : ", which gives it"}
              </span>
            )
            : (
              <span className="text-[13px]">
                {"The value "}
                <span className="font-mono text-[12px]">{covered.writtenBy}</span>
                {" writes whole, which holds it on the rows its value does"}
                {covered.holder !== null && <span className="mt-1 block font-mono text-[12px] break-all">{entryText(covered.holder)}</span>}
              </span>
            )}
          {covered.values.length > 0 && (
            <ul className="mt-1 flex flex-col gap-0.5">
              {covered.values.map((value) => (
                <li key={value} className="font-mono text-[12px] break-all" data-testid="templates-view-properties-written-value">{value}</li>
              ))}
            </ul>
          )}
        </div>
      )}
      {covered.entry !== null && (
        <div data-testid="templates-view-properties-entry">
          <EntryDetail row={propertyRow(covered.entry)} target={false} />
        </div>
      )}
    </div>
  );
}

/** What the template says of a variable: its description, its shape and what constrains it, and what it points to. */
function SchemaDetails({ variable, holds }: { variable: DeliveryTemplateVariable; holds: number }) {
  const { leaf } = splitPath(variable.path);
  const role = roleVisual(variable.role);
  return (
    <div className="flex flex-col gap-4">
      {(variable.title !== null || variable.description !== null) && (
        <div className="flex flex-col gap-1">
          {variable.title !== null && variable.title !== leaf && <div className="text-[13px] font-medium">{variable.title}</div>}
          {variable.description !== null && (
            <p className="whitespace-pre-wrap text-[13px] text-muted-foreground" data-testid="templates-view-properties-description">
              {variable.description}
            </p>
          )}
        </div>
      )}

      <div className="grid grid-cols-2 gap-x-4 gap-y-3">
        <DetailPair label="Shape"><span className="font-mono text-[12px]">{shapeText(variable)}</span></DetailPair>
        <DetailPair label="Written by">{role?.label ?? "A mapping fills it"}</DetailPair>
        {variable.format !== null && <DetailPair label="Format"><span className="font-mono text-[12px]">{variable.format}</span></DetailPair>}
        {variable.keyValueType !== null && (
          <DetailPair label="Free keys of"><span className="font-mono text-[12px]">{variable.keyValueType}</span></DetailPair>
        )}
        {holds > 0 && <DetailPair label="Holds">{holds} variable{holds === 1 ? "" : "s"}</DetailPair>}
        {variable.unitContext !== null && (
          <div className="col-span-2">
            <DetailPair label={<span className="inline-flex items-center gap-1"><Ruler className="size-3" />Unit context</span>}>
              <span className="break-all font-mono text-[12px]">{variable.unitContext}</span>
            </DetailPair>
          </div>
        )}
        {variable.pattern !== null && (
          <div className="col-span-2">
            <DetailPair label="Pattern"><span className="break-all font-mono text-[12px]">{variable.pattern}</span></DetailPair>
          </div>
        )}
      </div>

      {variable.nested && (
        <p className="text-xs text-muted-foreground">A list inside a repeated item: listed for reference, a mapping cannot fill it.</p>
      )}
      <KindList icon={Link2} label="Points to" values={variable.relationships} testId="templates-view-properties-relationships" />
      <KindList icon={DatabaseZap} label="Cached types" values={variable.cacheTypes} testId="templates-view-properties-cache-types" />
    </div>
  );
}

/** Which of a mapping's attributes the tree shows: the overview, the gaps against the schema, what a data check failed, or all. */
type Lens = "filled" | "missing" | "unfilled" | "failing" | "all";

const LENS_HELP = [
  "Filled: what the mapping fills, and every required attribute a check names. The view an author reads first.",
  "Missing: what this record requires and the mapping does not fill on every row, which is whether the mapping satisfies the schema. A delivery is stopped only by a required property of data that nothing fills; the other findings are warnings, and the record is sent for OSDU to judge.",
  "Unfilled: every attribute of the template nothing fills, which is what the mapping could carry and does not.",
  "Failing: once a data check has run, the attributes it found rows for that will not give an expected value, or whose values fail an assertion of the mapping.",
  "All: the whole template.",
  "A row is marked only where something asks for attention: a red or amber sign for a finding, a half circle for an attribute filled on some rows only, an open circle for one nothing fills, and a count for the rows a data check found failing.",
].join("\n\n");

/** Whether a variable is in a view; `failing` answers for the Failing view, and is null where no check can be asked. */
function inLens(lens: Lens, covered: CoverageView | undefined, failing: (() => boolean) | null): boolean {
  switch (lens) {
    case "filled":
      return !inOverview(covered);
    case "missing":
      return covered?.finding != null;
    case "unfilled":
      return isGap(covered);
    case "failing":
      return failing === null || failing();
    case "all":
      return true;
  }
}

/** Whether a variable is listed at all: what OSDU and OSDU Delivery write, and nested lists, only when asked for. */
function listedKind(variable: DeliveryTemplateVariable, minted: boolean, nested: boolean): boolean {
  return (minted || variable.role === "Mapping") && (nested || !variable.nested);
}

/** The first variable of the tree that matches, depth first, for the tree to select when it narrows to something new. */
function firstMatched(nodes: VariableNode[]): string | null {
  for (const node of nodes) {
    if (node.matched) {
      return node.variable.path;
    }

    const inner = firstMatched(node.children);
    if (inner !== null) {
      return inner;
    }
  }
  return null;
}

/**
 * The views of a mapping's tree as one segmented control, each with how many attributes it holds, so the reader picks the
 * question instead of combining switches. Picking the view in view again goes back to the overview.
 */
function LensBar({ lens, counts, failing, failingLabel, onChoose }: {
  lens: Lens;
  counts: Record<Exclude<Lens, "failing">, number>;
  /** How many attributes a data check found failing rows for; null before any check. */
  failing: number | null;
  failingLabel: string;
  onChoose: (lens: Lens) => void;
}) {
  const items: { lens: Lens; label: string; count: number; testId: string; alarm: boolean; hint: string }[] = [
    { lens: "filled", label: "Filled", count: counts.filled, testId: "templates-view-lens-filled", alarm: false, hint: "What the mapping fills, and every required attribute a check names" },
    { lens: "missing", label: "Missing", count: counts.missing, testId: "templates-view-show-missing", alarm: counts.missing > 0, hint: "What this record requires and the mapping does not fill on every row" },
    { lens: "unfilled", label: "Unfilled", count: counts.unfilled, testId: "templates-view-show-gaps", alarm: false, hint: "Every attribute nothing fills" },
    ...(failing === null
      ? []
      : [{ lens: "failing" as const, label: failingLabel, count: failing, testId: "templates-view-show-failing", alarm: failing > 0, hint: "The attributes a data check found rows for that will not give an expected value, or whose values fail an assertion of the mapping" }]),
    { lens: "all", label: "All", count: counts.all, testId: "templates-view-show-everything", alarm: false, hint: "The whole template" },
  ];
  return (
    <ToggleGroup
      type="single"
      variant="outline"
      size="sm"
      value={lens}
      // Picking the view in view again clears it, which a single toggle group says with an empty value.
      onValueChange={(value) => onChoose(value === "" ? lens : (value as Lens))}
      aria-label="Which attributes to show"
      data-testid="templates-view-lens"
    >
      {items.map((item) => (
        <ToggleGroupItem
          key={item.lens}
          value={item.lens}
          title={item.hint}
          className="h-8 gap-1.5 px-2.5 text-[13px]"
          data-testid={item.testId}
        >
          {item.label}
          <span
            className={cn(
              "rounded-full px-1.5 font-mono text-[11px] tabular-nums",
              item.alarm ? "bg-destructive/12 text-destructive" : "bg-muted text-muted-foreground",
            )}
          >
            {item.count}
          </span>
        </ToggleGroupItem>
      ))}
    </ToggleGroup>
  );
}

/** The options a reader needs now and then, out of the way until asked for: what the tree lists beyond a mapping's own attributes. */
function ViewOptions({ required, minted, nested, onRequired, onMinted, onNested }: {
  required: boolean;
  minted: boolean;
  nested: boolean;
  onRequired: (next: boolean) => void;
  onMinted: (next: boolean) => void;
  onNested: (next: boolean) => void;
}) {
  const on = [required, minted, nested].filter(Boolean).length;
  const option = (label: string, hint: string, checked: boolean, onChange: (next: boolean) => void, testId: string) => (
    <Label className="flex items-start justify-between gap-4 text-[13px] font-normal">
      <span className="flex flex-col gap-0.5">
        {label}
        <span className="text-[12px] text-muted-foreground">{hint}</span>
      </span>
      <Switch checked={checked} onCheckedChange={onChange} className="mt-0.5" data-testid={testId} />
    </Label>
  );
  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button
          type="button"
          size="sm"
          variant="outline"
          className={cn("h-8 gap-1.5 px-2.5 text-[13px] font-normal", on > 0 && activeFilterClass)}
          aria-label="View options"
          data-testid="templates-view-options"
        >
          <SlidersHorizontal />
          View
          {on > 0 && <span className="rounded-full bg-primary px-1.5 font-mono text-[11px] leading-4 text-primary-foreground tabular-nums">{on}</span>}
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="flex w-80 flex-col gap-3 p-3" data-testid="templates-view-options-panel">
        {option("Required", "Only what the schema requires, under what holds it.", required, onRequired, "templates-view-show-required")}
        {option("Minted", "What OSDU Delivery writes and OSDU sets: the id, kind, ACL, legal and the rest no mapping fills.", minted, onMinted, "templates-view-show-minted")}
        {option("Nested", "Lists inside a repeated item, which a mapping cannot fill.", nested, onNested, "templates-view-show-nested")}
      </PopoverContent>
    </Popover>
  );
}

/**
 * A template's variables as the record's tree beside a properties panel: each variable under the one that holds it, the
 * search, the view and its options narrowing the tree, and the selected variable on the right. Given a `mapping` laid over
 * it, the tree opens on what that mapping fills and what the schema requires, the overview an author reads first, and a
 * row is marked only where something asks for attention; the properties say what fills the variable, what a data check
 * found of it, and what the schema says, a tab each. Without a mapping the tree is the template on its own. With `fill`
 * the tree and the properties take the height their container leaves, which must be a bounded flex column; without it
 * they take a height of their own.
 */
export function TemplateVariableExplorer({ variables, mapping, fill = false }: {
  variables: DeliveryTemplateVariable[];
  mapping?: MappingOverlay;
  fill?: boolean;
}) {
  const [filter, setFilter] = useState("");
  const [showMinted, setShowMinted] = useState(false);
  const [showNested, setShowNested] = useState(false);
  const [showRequired, setShowRequired] = useState(false);
  // The view of a mapping's tree. Failing is the value check's own narrowing, which the session keeps, so the data check's
  // summary and this control are one choice.
  const [baseLens, setBaseLens] = useState<Exclude<Lens, "failing">>("filled");
  const [tab, setTab] = useState<DetailTab>("fill");
  // What the reader opened, and what they closed while a filter had revealed it; the filter's own reveal sits between.
  // The tree opens fully folded, top level included, so a record's sections read as an outline first.
  const [opened, setOpened] = useState<ReadonlySet<string>>(() => new Set());
  const [closed, setClosed] = useState<ReadonlySet<string>>(() => new Set());
  const [selectedId, setSelectedId] = useState<string | null>(
    () => variables.find((variable) => variable.role === "Mapping" && !variable.nested)?.path ?? null,
  );
  const layout = usePersistentLayout("sqlflow.templates.variables.layout");

  // A value check of the mapping being looked at narrows the tree to what fails, and names attributes to show.
  const valueCheck = useValueCheckContext();
  const failingFilter = valueCheck?.filter ?? null;
  const fails = valueCheck?.fails;
  const checkedAny = (valueCheck?.checked.length ?? 0) > 0;
  const focus = valueCheck?.focus ?? null;
  const [seenFocus, setSeenFocus] = useState<number | null>(focus?.nonce ?? null);
  if (focus !== null && focus.nonce !== seenFocus) {
    setSeenFocus(focus.nonce);
    setSelectedId(focus.path);
    setTab("data");
    setOpened((current) => {
      const next = new Set(current);
      for (let holder = holderPath(focus.path); holder !== null; holder = holderPath(holder)) {
        next.add(holder);
      }
      return next;
    });
    setClosed(new Set());
  }

  // What the mapping fills, by variable: the entry filling it, and the worst finding on it, an error over a warning.
  const covered = useMemo(() => {
    if (mapping === undefined) {
      return null;
    }

    const worst = new Map<string, MappingDraftIssue>();
    for (const issue of mapping.coverage.issues) {
      if (issue.target !== null && worst.get(issue.target)?.severity !== "error") {
        worst.set(issue.target, issue);
      }
    }

    const byTarget = new Map(mapping.entries.map((entry) => [entry.target, entry]));
    return new Map<string, CoverageView>(
      mapping.coverage.variables.map((variable) => [
        variable.target,
        {
          state: variable.state,
          entry: variable.direct ? byTarget.get(variable.target) ?? null : null,
          writtenBy: variable.direct ? null : variable.writtenBy ?? null,
          holder: variable.direct || variable.writtenBy == null ? null : byTarget.get(variable.writtenBy) ?? null,
          values: variable.values ?? [],
          finding: worst.get(variable.target) ?? null,
        },
      ]),
    );
  }, [mapping]);

  // An entry filling a free key of an object that takes them is a row of its own, under the object holding it.
  const listed = useMemo(
    () => (mapping === undefined ? variables : withKeyVariables(variables, mapping.entries)),
    [variables, mapping],
  );

  // Without a mapping there is no view to pick: the tree is the template. The Failing view holds while the check's
  // narrowing does, whichever of the two controls set it.
  const lens: Lens | null = covered === null ? null : failingFilter !== null ? "failing" : baseLens;
  const failingKind = failingFilter ?? "any";
  const term = filter.trim().toLowerCase();
  const narrowed = term !== "" || showRequired || (lens !== null && lens !== "all");
  const matches = useMemo(
    () => (variable: DeliveryTemplateVariable) => (!showRequired || variable.required)
      && (lens === null || inLens(lens, covered?.get(variable.path), fails === undefined ? null : () => fails(variable.path, failingKind)))
      && textMatches(variable, term, covered?.get(variable.path)),
    [covered, lens, showRequired, term, fails, failingKind],
  );
  const tree = useMemo(
    () => buildTree(listed, (variable) => listedKind(variable, showMinted, showNested), matches),
    [listed, showMinted, showNested, matches],
  );

  // How many attributes each view holds, under the options and the search, so the reader sees where to look before
  // looking. The Failing view is counted once a check has answered.
  const lensCounts = useMemo(() => {
    if (covered === null) {
      return null;
    }

    const counted = { filled: 0, missing: 0, unfilled: 0, all: 0, failing: 0 };
    for (const variable of listed) {
      if (!listedKind(variable, showMinted, showNested) || (showRequired && !variable.required)) {
        continue;
      }

      const at = covered.get(variable.path);
      if (!textMatches(variable, term, at)) {
        continue;
      }

      for (const which of ["filled", "missing", "unfilled", "all"] as const) {
        if (inLens(which, at, null)) {
          counted[which] += 1;
        }
      }

      if (fails !== undefined && fails(variable.path, failingKind)) {
        counted.failing += 1;
      }
    }
    return counted;
  }, [covered, listed, showMinted, showNested, showRequired, term, fails, failingKind]);

  const byPath = useMemo(() => new Map(listed.map((variable) => [variable.path, variable])), [listed]);
  const holds = useMemo(() => {
    const counts = new Map<string, number>();
    for (const variable of variables) {
      const holder = holderPath(variable.path);
      if (holder !== null) {
        counts.set(holder, (counts.get(holder) ?? 0) + 1);
      }
    }
    return counts;
  }, [variables]);
  const revealed = useMemo(() => revealedPaths(listed, matches, narrowed), [listed, matches, narrowed]);

  // Narrowing to what a check found failing, from here or from the check's summary, opens the attributes it names with
  // their Data tab, and selects the first of them unless the one selected is among them.
  const [seenFailing, setSeenFailing] = useState(failingFilter);
  if (failingFilter !== seenFailing) {
    setSeenFailing(failingFilter);
    setClosed(new Set());
    if (failingFilter !== null) {
      setTab("data");
      const current = selectedId === null ? undefined : byPath.get(selectedId);
      if (current === undefined || !matches(current)) {
        setSelectedId(firstMatched(tree.roots) ?? selectedId);
      }
    }
  }

  const treeState = useMemo<TreeState>(() => {
    const expanded = new Set(opened);
    for (const path of revealed) {
      if (!closed.has(path)) {
        expanded.add(path);
      }
    }

    const setOpen = (id: string, open: boolean) => {
      setOpened((current) => {
        const next = new Set(current);
        if (open) {
          next.add(id);
        } else {
          next.delete(id);
        }
        return next;
      });
      setClosed((current) => {
        const next = new Set(current);
        if (open) {
          next.delete(id);
        } else {
          next.add(id);
        }
        return next;
      });
    };

    return {
      expanded,
      toggle: (id) => setOpen(id, !expanded.has(id)),
      setOpen,
      selectedId,
      select: setSelectedId,
    };
  }, [opened, closed, revealed, selectedId]);

  // A new search reveals its own matches afresh; what was closed under the previous one no longer applies.
  const changeFilter = (next: string) => {
    setFilter(next);
    setClosed(new Set());
  };

  const changeRequired = (next: boolean) => {
    setShowRequired(next);
    setClosed(new Set());
  };

  const chooseLens = (next: Lens) => {
    setClosed(new Set());
    if (next === "failing") {
      valueCheck?.setFilter(lens === "failing" ? null : "any");
      return;
    }

    valueCheck?.setFilter(null);
    setBaseLens(next === lens ? "filled" : next);
  };

  const expandAll = () => {
    setOpened(new Set(holds.keys()));
    setClosed(new Set());
  };

  const collapseAll = () => {
    setOpened(new Set());
    setClosed(new Set(holds.keys()));
  };

  const selected = selectedId === null ? undefined : byPath.get(selectedId);
  // A search in a narrower view says how many more attributes it finds in the whole template, a click away.
  const elsewhere = lens !== null && lens !== "all" && lensCounts !== null && term !== "" ? lensCounts.all - tree.matched : 0;
  const failingLabel = failingFilter === null || failingFilter === "any" ? "Failing" : outcomeVisual(failingFilter).label;

  return (
    <CoverageContext.Provider value={covered}>
      <div className={cn("flex flex-col gap-3", fill && "min-h-0 flex-1")}>
        <FilterBar>
          <SearchInput
            value={filter}
            onChange={changeFilter}
            placeholder="Search attributes, columns, types"
            label="Filter the variables"
            testId="templates-view-variables-filter"
          />
          {lens !== null && lensCounts !== null && (
            <div className="flex items-center gap-1.5">
              <LensBar
                lens={lens}
                counts={lensCounts}
                failing={checkedAny ? lensCounts.failing : null}
                failingLabel={failingLabel}
                onChoose={chooseLens}
              />
              <GlyphRef icon={Info} title="What each view shows" body={LENS_HELP} testId="templates-view-lens-help" />
            </div>
          )}
          <div className="flex items-center gap-1 sm:ml-auto">
            <span
              className="mr-1 text-xs text-muted-foreground tabular-nums"
              title={`${tree.matched} attributes shown, of the ${listed.length} the template has`}
              data-testid="templates-view-variables-count"
            >
              {`${tree.matched} of ${listed.length}`}
            </span>
            <ViewOptions
              required={showRequired}
              minted={showMinted}
              nested={showNested}
              onRequired={changeRequired}
              onMinted={setShowMinted}
              onNested={setShowNested}
            />
            <IconAction label="Expand all" icon={<ChevronsUpDown />} onClick={expandAll} data-testid="templates-view-expand-all" />
            <IconAction label="Collapse all" icon={<ChevronsDownUp />} onClick={collapseAll} data-testid="templates-view-collapse-all" />
          </div>
        </FilterBar>
        <div
          className={cn("min-h-[360px] overflow-hidden rounded-lg border", fill ? "flex-1" : "h-[min(640px,calc(100vh-300px))]")}
          data-testid="templates-view-variables"
        >
          <ResizablePanelGroup orientation="horizontal" defaultLayout={layout.defaultLayout} onLayoutChanged={layout.onLayoutChanged}>
            {/* The properties side gets the larger share: a variable's description is the longest thing either side shows. */}
            <ResizablePanel id="template-variables-tree" defaultSize="45" minSize="30">
              <TreeRoot label="Variables" state={treeState} className="h-full overflow-y-auto overflow-x-hidden p-1" testId="templates-view-variables-tree">
                {tree.roots.length === 0
                  ? (
                    <p className="px-2 py-3 text-[13px] text-muted-foreground" data-testid="templates-view-variables-empty">
                      {lens === "failing" && term === ""
                        ? "No attribute checked has rows that fail this way."
                        : lens === "missing" && term === ""
                          ? "Nothing the schema requires of this record is missing: every required variable the record reaches is filled on every row."
                          : "No variable matches the filter."}
                    </p>
                  )
                  : tree.roots.map((node) => <VariableBranch key={node.variable.path} node={node} />)}
                {elsewhere > 0 && (
                  <button
                    type="button"
                    onClick={() => chooseLens("all")}
                    className="mx-2 my-2 text-left text-[12px] text-primary hover:underline"
                    data-testid="templates-view-search-all"
                  >
                    {`${elsewhere} more ${elsewhere === 1 ? "attribute matches" : "attributes match"} in the whole template`}
                  </button>
                )}
              </TreeRoot>
            </ResizablePanel>
            <ResizableHandle withHandle />
            <ResizablePanel id="template-variables-properties" defaultSize="55" minSize="30">
              <div className="h-full overflow-y-auto p-3" data-testid="templates-view-properties">
                {selected === undefined
                  ? <p className="text-[13px] text-muted-foreground">Select a variable to see its properties.</p>
                  : <VariableProperties variable={selected} holds={holds.get(selected.path) ?? 0} tab={tab} onTab={setTab} />}
              </div>
            </ResizablePanel>
          </ResizablePanelGroup>
        </div>
      </div>
    </CoverageContext.Provider>
  );
}
