import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import {
  Archive, ArrowLeftRight, ArrowRight, ChevronDown, ChevronsDownUp, ChevronsUpDown, Circle, CircleAlert, CircleCheck, CircleDot,
  CirclePlus, Minus, PencilLine, Plus, TriangleAlert, Type, type LucideIcon,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { cn } from "@/lib/utils";
import {
  deliveryApi,
  type DeliveryOsduComparison,
  type DeliveryOsduReferencedFile,
  type DeliveryOsduRelease,
  type DeliveryOsduSchema,
  type DeliveryTemplateChangeImpact,
  type DeliveryTemplateFieldChange,
  type DeliveryTemplateVariableChange,
} from "../../api/delivery";
import { DiffView } from "@/components/DiffView";
import { EmptyState } from "@/components/EmptyState";
import { FilterBar } from "@/components/FilterBar";
import { LinkRef } from "@/components/LinkRef";
import { RichTooltip } from "@/components/RichTooltip";
import { SearchInput } from "@/components/SearchInput";
import { StatePill } from "@/components/StatusBadge";
import { HeadClippedText } from "./HeadClippedText";
import { TemplateCompareReport } from "./TemplateCompareReport";
import { weightCounts, WEIGHTS, type ChangeWeight } from "./templateMoveReport";
import { condenseDiff, diffList, diffText, type DiffPart } from "./textDiff";
import { entityName, kindStem, roleLabel, splitPath } from "./templateFormat";
import { ProblemView, TaskProgress } from "./TemplateSheet";

/** One side of a comparison: a release of the OSDU data definitions, and a version of the kind in it. */
export interface CompareSide {
  release: string;
  kind: string;
}

/** The two versions a comparison opens with. */
export interface CompareStart {
  from: CompareSide;
  to: CompareSide;
}

const CHANGE_VISUAL: Record<DeliveryTemplateVariableChange["change"], { tone: "success" | "destructive" | "info"; icon: LucideIcon }> = {
  Added: { tone: "success", icon: Plus },
  Removed: { tone: "destructive", icon: Minus },
  Changed: { tone: "info", icon: PencilLine },
};

const IMPACT_VISUAL: Record<DeliveryTemplateChangeImpact, { tone: "destructive" | "info" | "muted"; icon: LucideIcon; text: string; edge: string; hint: string }> = {
  Breaking: {
    tone: "destructive",
    icon: TriangleAlert,
    text: "text-destructive",
    edge: "border-l-destructive/70",
    hint: "A mapping written for the older version can stop rendering, or render a record the newer version does not accept.",
  },
  Additive: {
    tone: "info",
    icon: CirclePlus,
    text: "text-info",
    edge: "border-l-info/70",
    hint: "Something a mapping may now use; nothing a mapping of the older version relies on changed.",
  },
  Wording: { tone: "muted", icon: Type, text: "text-muted-foreground", edge: "border-l-border", hint: "Only a title or a description reads differently." },
};

const IMPACTS: readonly DeliveryTemplateChangeImpact[] = ["Breaking", "Additive", "Wording"];

/**
 * How much a change weighs for the synced mappings of the kind, read against what their documents write. Significant is the
 * one that needs someone, so it is the one in a warning tone; the others stay quiet.
 */
const WEIGHT_VISUAL: Record<ChangeWeight, { tone: "warning" | "muted"; icon: LucideIcon; label: string; hint: string }> = {
  Significant: {
    tone: "warning",
    icon: CircleAlert,
    label: "Significant",
    hint: "It asks something of a synced mapping that writes what it changes: the mapping has to change for it, or what it writes is deprecated.",
  },
  Minor: {
    tone: "muted",
    icon: CircleDot,
    label: "Minor",
    hint: "It touches what a synced mapping writes and asks nothing of it: how it reads, a rule relaxed, more kinds it may point to.",
  },
  Unused: {
    tone: "muted",
    icon: Circle,
    label: "Not in use",
    hint: "No synced mapping writes what it changes, so it has the least impact: it matters only to a mapping that starts to use it.",
  },
};

/** The impact filter's value for every change. */
const ALL = "All";
type ImpactFilter = typeof ALL | DeliveryTemplateChangeImpact;
const IMPACT_FILTERS: readonly ImpactFilter[] = [ALL, ...IMPACTS];
type WeightFilter = typeof ALL | ChangeWeight;
const WEIGHT_FILTERS: readonly WeightFilter[] = [ALL, ...WEIGHTS];

/** The file picker's value for the kind's own schema file; a shared schema is picked by its name. */
const KIND_FILE = "kind";

/** Tokens of unchanged text kept either side of an edit in a long description, before the rest is cut. */
const PROSE_CONTEXT = 16;

/** The three-part version at the end of a schema file's path (abstract/AbstractFacility.1.1.0.json), or null. */
function fileVersion(path: string | null): string | null {
  const match = path === null ? null : /\.(\d+\.\d+\.\d+)\.json$/.exec(path);
  return match === null ? null : match[1];
}

/** What happened to a shared schema between the versions, as the file picker words it. */
function referencedState(file: DeliveryOsduReferencedFile): string {
  if (file.fromPath === null) {
    return "only in To";
  }

  if (file.toPath === null) {
    return "only in From";
  }

  const before = fileVersion(file.fromPath);
  const after = fileVersion(file.toPath);
  return before !== null && after !== null && before !== after ? `${before} → ${after}` : "changed";
}

const FIELD_LABELS: Record<string, string> = {
  type: "type",
  format: "format",
  pattern: "pattern",
  keyValueType: "free key type",
  unitContext: "unit context",
  role: "written by",
  nested: "nested list",
  required: "required",
  relationships: "points to",
  title: "title",
  description: "description",
};

/** The separator the comparison joins a variable's relationships with. */
const RELATIONSHIP_SEPARATOR = ", ";

/** The versions of the kind a release publishes, newest first, and the one a side resolves to: its own, or the newest there. */
function useSideVersions(side: CompareSide, stem: string) {
  const index = useQuery({
    queryKey: ["delivery", "osdu-definitions", "schemas", side.release],
    queryFn: () => deliveryApi.osduSchemas(side.release),
    staleTime: Infinity,
  });
  const versions = useMemo(
    (): DeliveryOsduSchema[] => (index.data?.schemas ?? []).filter((schema) => kindStem(schema.kind) === stem),
    [index.data, stem],
  );
  const kind = versions.some((schema) => schema.kind === side.kind) ? side.kind : versions[0]?.kind ?? null;
  return { index, versions, kind };
}

interface SidePickerProps {
  caption: string;
  side: CompareSide;
  kind: string | null;
  releases: DeliveryOsduRelease[];
  versions: DeliveryOsduSchema[];
  loading: boolean;
  onChange: (side: CompareSide) => void;
  testId: string;
}

/**
 * One side of the comparison: the release, and the version of the kind in it. Each picker says which of the two it is,
 * because the release and the schema version both look like a dotted version and two sides on one release would
 * otherwise read as the same schema.
 */
function SidePicker({ caption, side, kind, releases, versions, loading, onChange, testId }: SidePickerProps) {
  return (
    <div className="flex min-w-0 flex-wrap items-end gap-2">
      <div className="flex min-w-0 flex-col gap-1.5">
        <Label className="text-xs font-normal text-muted-foreground">{`${caption} release`}</Label>
        <Select value={side.release} onValueChange={(release) => onChange({ release, kind: side.kind })}>
          <SelectTrigger size="sm" className="h-8 w-32" data-testid={`${testId}-release`} aria-label={`${caption} release`}>
            <SelectValue placeholder={side.release} />
          </SelectTrigger>
          <SelectContent>
            {releases.map((release) => (
              <SelectItem key={release.name} value={release.name}><span className="font-mono">{release.name}</span></SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>
      <div className="flex min-w-0 flex-col gap-1.5">
        <Label className="text-xs font-normal text-muted-foreground">{`${caption} schema version`}</Label>
        <Select value={kind ?? ""} onValueChange={(next) => onChange({ release: side.release, kind: next })} disabled={versions.length === 0}>
          <SelectTrigger size="sm" className="h-8 w-44" data-testid={`${testId}-version`} aria-label={`${caption} schema version`}>
            <SelectValue placeholder={loading ? "Loading versions" : "Not in this release"} />
          </SelectTrigger>
          <SelectContent>
            {versions.map((schema) => (
              <SelectItem key={schema.kind} value={schema.kind}>
                <span className="font-mono">{schema.version}</span>
                {schema.status !== null && schema.status !== "PUBLISHED" && (
                  <span className="text-[11px] text-muted-foreground">{schema.status.toLowerCase()}</span>
                )}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>
    </div>
  );
}

/**
 * What the two sides actually are, in words, so a comparison is never read as one thing when it is another: the axis
 * that differs is the one the sentence leads with, and two sides that are the same version of the same release say so.
 */
function comparingText(fromRelease: string, fromVersion: string, toRelease: string, toVersion: string): string {
  const sameRelease = fromRelease === toRelease;
  const sameVersion = fromVersion === toVersion;
  if (sameRelease && sameVersion) {
    return `Both sides are schema version ${fromVersion} from release ${fromRelease}.`;
  }

  if (sameRelease) {
    return `Comparing schema versions ${fromVersion} and ${toVersion}, both from release ${fromRelease}.`;
  }

  if (sameVersion) {
    return `Comparing schema version ${fromVersion} as releases ${fromRelease} and ${toRelease} publish it.`;
  }

  return `Comparing schema version ${fromVersion} from release ${fromRelease} with ${toVersion} from release ${toRelease}.`;
}

/** A removed stretch struck through on red, an added one on green, and unchanged text quiet so the edits carry the eye. */
function DiffParts({ parts }: { parts: DiffPart[] }) {
  return (
    <>
      {parts.map((part, index) => {
        switch (part.kind) {
          case "same":
            return <span key={index} className="text-muted-foreground">{part.text}</span>;
          case "removed":
            return (
              // A gap before the addition that replaces it, so the two never read as one word.
              <del key={index} className="rounded-sm bg-destructive/15 text-destructive decoration-destructive/60 [&+ins]:ml-1">
                {part.text}
              </del>
            );
          case "added":
            return <ins key={index} className="rounded-sm bg-success/15 text-success no-underline">{part.text}</ins>;
        }
      })}
    </>
  );
}

/** The two sides of a value that was replaced outright, older struck and newer after an arrow. */
function Replacement({ before, after, prose }: { before: string; after: string; prose: boolean }) {
  return (
    <span className={cn("flex min-w-0 gap-x-1.5 gap-y-1", prose ? "flex-col" : "flex-wrap items-baseline")}>
      <DiffParts parts={[{ kind: "removed", text: before }]} />
      {!prose && <ArrowRight className="size-3.5 shrink-0 self-center text-muted-foreground" aria-label="became" />}
      <DiffParts parts={[{ kind: "added", text: after }]} />
    </span>
  );
}

/** A title or description with its edits marked in place; a long one shows the words around each edit until asked for all. */
function ProseDiff({ parts }: { parts: DiffPart[] }) {
  const [whole, setWhole] = useState(false);
  const condensed = useMemo(() => condenseDiff(parts, PROSE_CONTEXT), [parts]);
  return (
    <span className="flex min-w-0 flex-col items-start gap-0.5">
      <span className="whitespace-pre-wrap text-[13px] leading-5 [overflow-wrap:anywhere]">
        <DiffParts parts={whole ? parts : condensed.parts} />
      </span>
      {condensed.condensed && (
        <Button variant="link" size="xs" className="h-auto px-0 text-xs" onClick={() => setWhole(!whole)}>
          {whole ? "Show only the edits" : "Show the whole text"}
        </Button>
      )}
    </span>
  );
}

/** What one field went from and to, laid out for the kind of value it holds. */
function FieldValue({ field }: { field: DeliveryTemplateFieldChange }) {
  const prose = field.field === "title" || field.field === "description";
  const parts = useMemo(
    () => (field.before === null || field.after === null ? null : diffText(field.before, field.after)),
    [field.before, field.after],
  );

  if (field.field === "relationships") {
    return (
      <span className="flex min-w-0 flex-wrap gap-1 font-mono text-[12px]">
        {diffList(field.before, field.after, RELATIONSHIP_SEPARATOR).map((entry) => (
          <span key={entry.value} className="max-w-full [overflow-wrap:anywhere]">
            <DiffParts parts={[{ kind: entry.kind, text: entry.value }]} />
          </span>
        ))}
      </span>
    );
  }

  const valueClass = prose ? "text-[13px] leading-5" : "font-mono text-[12px] leading-5";
  if (field.before === null || field.after === null) {
    const kind = field.before === null ? "added" : "removed";
    return (
      <span className={cn("min-w-0 whitespace-pre-wrap [overflow-wrap:anywhere]", valueClass)}>
        <DiffParts parts={[{ kind, text: field.after ?? field.before ?? "" }]} />
      </span>
    );
  }

  // Too long to compare word by word, or nothing kept in common: the older and newer value, whole.
  if (parts === null || parts.every((part) => part.kind !== "same")) {
    return (
      <span className={cn("min-w-0 whitespace-pre-wrap [overflow-wrap:anywhere]", valueClass)}>
        <Replacement before={field.before} after={field.after} prose={prose} />
      </span>
    );
  }

  return prose ? (
    <ProseDiff parts={parts} />
  ) : (
    <span className={cn("min-w-0 whitespace-pre-wrap [overflow-wrap:anywhere]", valueClass)}>
      <DiffParts parts={parts} />
    </span>
  );
}

const IMPACT_RANK: Record<DeliveryTemplateChangeImpact, number> = { Breaking: 0, Additive: 1, Wording: 2 };

/** A copy of a set with one key put in or taken out. */
function withKey<T>(set: ReadonlySet<T>, key: T, present: boolean): Set<T> {
  const next = new Set(set);
  if (present) {
    next.add(key);
  } else {
    next.delete(key);
  }

  return next;
}

/** The disclosure chevron: pointing down when its row is open, right when it is folded. */
function Chevron() {
  return (
    <ChevronDown className="size-4 shrink-0 text-muted-foreground transition-transform duration-120 group-data-[state=closed]:-rotate-90" aria-hidden />
  );
}

interface ChangeCardProps {
  change: DeliveryTemplateVariableChange;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Whether to name the mappings that use what it changes: when several are weighed, so it is not plain which. */
  showUsedBy: boolean;
}

/**
 * A variable that differs: its path and what happened to it, opening onto each field that changed with its edit marked.
 * Folded, the header names the fields that changed, so a scan down the list still says what moved.
 */
function ChangeCard({ change, open, onOpenChange, showUsedBy }: ChangeCardProps) {
  const { parent, leaf } = splitPath(change.path);
  const writer = roleLabel(change.role);
  const fields = useMemo(() => [...change.fields].sort((a, b) => IMPACT_RANK[a.impact] - IMPACT_RANK[b.impact]), [change.fields]);
  return (
    <Collapsible open={open} onOpenChange={onOpenChange} asChild>
      <li
        className={cn("min-w-0 overflow-hidden rounded-lg border border-l-2 bg-card", IMPACT_VISUAL[change.impact].edge)}
        data-testid="templates-compare-change-row"
      >
        <CollapsibleTrigger
          className="group flex w-full min-w-0 items-center gap-2 px-3 py-2 text-left hover:bg-accent/50 data-[state=open]:border-b"
          data-testid={`templates-compare-change-toggle-${change.path}`}
        >
          <Chevron />
          <span className={cn("flex min-w-0 font-mono text-[12px]", change.weight === "Unused" && "text-muted-foreground")}>
            <HeadClippedText head={parent} body={leaf} title="Variable" />
          </span>
          <span className="min-w-0 flex-1 basis-0 truncate text-xs text-muted-foreground">
            {open ? "" : fields.map((field) => FIELD_LABELS[field.field] ?? field.field).join(" · ")}
          </span>
          {writer !== null && <span className="shrink-0 text-[11px] text-muted-foreground">{writer}</span>}
          {showUsedBy && change.usedBy !== null && change.usedBy.length > 0 && (
            <RichTooltip title="Used by" body={change.usedBy.join(", ")}>
              <span className="max-w-48 shrink-0 truncate font-mono text-[11px] text-muted-foreground" data-testid={`templates-compare-used-by-${change.path}`}>
                {change.usedBy.length === 1 ? change.usedBy[0] : `${change.usedBy.length} mappings`}
              </span>
            </RichTooltip>
          )}
          {change.deprecates && (
            <RichTooltip title="Deprecated" body="Its description now marks it deprecated, as OSDU writes a deprecation: still accepted, and the description says what replaces it.">
              <span className="shrink-0">
                {/* Loud only where a synced mapping writes what it deprecates; elsewhere it is something to know, not to act on. */}
                <StatePill
                  tone={change.weight === null || change.weight === "Significant" ? "warning" : "muted"}
                  label="Deprecated"
                  icon={Archive}
                  testId={`templates-compare-deprecated-${change.path}`}
                />
              </span>
            </RichTooltip>
          )}
          <StatePill
            tone={CHANGE_VISUAL[change.change].tone}
            label={change.change}
            icon={CHANGE_VISUAL[change.change].icon}
            testId={`templates-compare-change-${change.path}`}
          />
        </CollapsibleTrigger>
        <CollapsibleContent asChild>
      <dl className="flex flex-col divide-y divide-border/60">
        {fields.map((field) => {
          const visual = IMPACT_VISUAL[field.impact];
          return (
            <div
              key={field.field}
              className="grid grid-cols-[8.5rem_minmax(0,1fr)] items-baseline gap-3 px-3 py-1.5"
              data-testid={`templates-compare-field-${change.path}-${field.field}`}
            >
              <dt className="min-w-0">
                <RichTooltip title={field.impact} body={visual.hint}>
                  <span className="inline-flex items-center gap-1.5 text-xs text-muted-foreground">
                    <visual.icon className={cn("size-3.5 shrink-0 self-center", visual.text)} aria-hidden />
                    <span className="sr-only">{`${field.impact}:`}</span>
                    {FIELD_LABELS[field.field] ?? field.field}
                  </span>
                </RichTooltip>
              </dt>
              <dd className="min-w-0">
                <FieldValue field={field} />
              </dd>
            </div>
          );
        })}
      </dl>
        </CollapsibleContent>
      </li>
    </Collapsible>
  );
}

/** What changes are listed under: an impact, or how much they weigh for the synced mappings, with what that means. */
interface GroupHeading {
  key: string;
  label: string;
  icon: LucideIcon;
  text: string;
  hint: string;
}

const impactHeading = (impact: DeliveryTemplateChangeImpact): GroupHeading => ({
  key: impact, label: impact, icon: IMPACT_VISUAL[impact].icon, text: IMPACT_VISUAL[impact].text, hint: IMPACT_VISUAL[impact].hint,
});

const weightHeading = (weight: ChangeWeight): GroupHeading => ({
  key: weight,
  label: WEIGHT_VISUAL[weight].label,
  icon: WEIGHT_VISUAL[weight].icon,
  text: weight === "Significant" ? "text-warning" : "text-muted-foreground",
  hint: WEIGHT_VISUAL[weight].hint,
});

interface ChangeGroupProps {
  heading: GroupHeading;
  changes: DeliveryTemplateVariableChange[];
  open: boolean;
  onOpenChange: (open: boolean) => void;
  openCards: ReadonlySet<string>;
  onCardOpenChange: (path: string, open: boolean) => void;
  showUsedBy: boolean;
}

/** Changes under a heading that says what they mean: for a mapping of the older version, or for the synced mappings. */
function ChangeGroup({ heading, changes, open, onOpenChange, openCards, onCardOpenChange, showUsedBy }: ChangeGroupProps) {
  return (
    <Collapsible open={open} onOpenChange={onOpenChange} asChild>
      <section className="flex min-w-0 flex-col gap-2" data-testid={`templates-compare-group-${heading.key.toLowerCase()}`}>
        <h3 className="min-w-0">
          <CollapsibleTrigger
            className="group -mx-1 flex w-full min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5 rounded-md px-1 py-1 text-left hover:bg-accent/50"
            data-testid={`templates-compare-group-toggle-${heading.key.toLowerCase()}`}
          >
            <Chevron />
            <heading.icon className={cn("size-4 shrink-0", heading.text)} aria-hidden />
            <span className="text-[13px] font-medium">{heading.label}</span>
            <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{changes.length.toLocaleString()}</span>
            <span className="text-xs font-normal text-muted-foreground">{heading.hint}</span>
          </CollapsibleTrigger>
        </h3>
        <CollapsibleContent asChild>
          <ol className="flex min-w-0 flex-col gap-2">
            {changes.map((change) => (
              <ChangeCard
                key={change.path}
                change={change}
                open={openCards.has(change.path)}
                onOpenChange={(next) => onCardOpenChange(change.path, next)}
                showUsedBy={showUsedBy}
              />
            ))}
          </ol>
        </CollapsibleContent>
      </section>
    </Collapsible>
  );
}

/** The comparison summed up: no change, or how many changes of each impact, and what that means for existing mappings. */
function Verdict({ data }: { data: DeliveryOsduComparison }) {
  if (data.changes.length === 0) {
    const text = data.sameFile
      ? "The two versions are the same schema, file for file."
      : data.onlyIdentifiersDiffer
        ? "The files differ only in the version identifiers each one carries; the schema is the same."
        : "No template variable differs. The files differ in what a template does not hold, such as annotations.";
    return (
      <div className="flex flex-wrap items-center gap-2 text-[13px]" data-testid="templates-compare-verdict">
        <StatePill tone="success" label="No change" icon={CircleCheck} testId="templates-compare-no-change" />
        <span className="text-muted-foreground">{text}</span>
      </div>
    );
  }

  return (
    <div className="flex flex-wrap items-center gap-2 text-[13px]" data-testid="templates-compare-verdict">
      {IMPACTS.map((impact) => {
        const count = impact === "Breaking" ? data.breaking : impact === "Additive" ? data.additive : data.wording;
        return count === 0 ? null : (
          <RichTooltip key={impact} title={impact} body={IMPACT_VISUAL[impact].hint}>
            <span>
              <StatePill
                tone={IMPACT_VISUAL[impact].tone}
                label={`${count} ${impact.toLowerCase()}`}
                icon={IMPACT_VISUAL[impact].icon}
                testId={`templates-compare-count-${impact.toLowerCase()}`}
              />
            </span>
          </RichTooltip>
        );
      })}
      <span className="text-muted-foreground">
        {`${data.unchanged.toLocaleString()} variable${data.unchanged === 1 ? "" : "s"} unchanged. `}
        {data.breaking > 0
          ? "A mapping written for the older version needs a look at the breaking changes before it moves."
          : "Nothing a mapping written for the older version relies on changed."}
        {data.sameFile && data.referencedFiles.length > 0 && (
          <span data-testid="templates-compare-from-shared">
            {` The kind's own file is the same in both; these changes come from the ${data.referencedFiles.length} shared schema${data.referencedFiles.length === 1 ? "" : "s"} it refers to that changed, listed under Published files.`}
          </span>
        )}
      </span>
    </div>
  );
}

/**
 * What the changes mean for the synced mappings of the kind: read against what the mappings moving from another version
 * write, how many are significant, minor, or touch nothing any of them uses. With none to weigh against, it says so.
 */
function Weighed({ data }: { data: DeliveryOsduComparison }) {
  if (data.changes.length === 0) {
    return null;
  }

  const names = data.weighedMappings;
  if (names.length === 0) {
    return (
      <p className="text-[13px] text-muted-foreground" data-testid="templates-compare-weighed">
        No synced mapping of this kind moves from another version, so no change is weighed against what a mapping writes.
      </p>
    );
  }

  const counts = weightCounts(data.changes);
  return (
    <div className="flex flex-wrap items-center gap-2 text-[13px]" data-testid="templates-compare-weighed">
      <span className="text-muted-foreground">
        {"Read against what "}
        {names.length === 1 ? (
          <span className="font-mono text-[12px] text-foreground">{names[0]}</span>
        ) : (
          <RichTooltip title="Synced mappings moving to this version" body={names.join(", ")}>
            <span className="text-foreground underline decoration-dotted underline-offset-2">{`${names.length} synced mappings`}</span>
          </RichTooltip>
        )}
        {names.length === 1 ? " writes:" : " write:"}
      </span>
      {WEIGHTS.map((weight) => (
        <RichTooltip key={weight} title={WEIGHT_VISUAL[weight].label} body={WEIGHT_VISUAL[weight].hint}>
          <span>
            <StatePill
              tone={counts[weight] === 0 ? "muted" : WEIGHT_VISUAL[weight].tone}
              label={`${counts[weight].toLocaleString()} ${WEIGHT_VISUAL[weight].label.toLowerCase()}`}
              icon={WEIGHT_VISUAL[weight].icon}
              testId={`templates-compare-weighed-${weight.toLowerCase()}`}
            />
          </span>
        </RichTooltip>
      ))}
    </div>
  );
}

interface TemplateCompareSheetProps {
  start: CompareStart;
  onClose: () => void;
}

/**
 * Two versions of an OSDU kind compared, each picked from any release of the OSDU data definitions: a verdict first (no
 * change, or how many breaking, additive and wording changes), then every variable that differs, grouped by what it means
 * for a mapping of the older version, with each changed field's edit marked in place, and the two schema files side by
 * side exactly as the repository publishes them. Its Report copies or saves what moving from one to the other takes, the
 * synced mappings of the kind included.
 */
export function TemplateCompareSheet({ start, onClose }: TemplateCompareSheetProps) {
  const stem = kindStem(start.to.kind);
  const [from, setFrom] = useState<CompareSide>(start.from);
  const [to, setTo] = useState<CompareSide>(start.to);
  const [filter, setFilter] = useState("");
  const [impact, setImpact] = useState<ImpactFilter>(ALL);
  const [weight, setWeight] = useState<WeightFilter>(ALL);
  const [pickedFile, setPickedFile] = useState(KIND_FILE);

  const releases = useQuery({
    queryKey: ["delivery", "osdu-definitions", "releases"],
    queryFn: deliveryApi.osduReleases,
    staleTime: 10 * 60_000,
  });
  const left = useSideVersions(from, stem);
  const right = useSideVersions(to, stem);

  const comparison = useQuery({
    queryKey: ["delivery", "osdu-definitions", "compare", from.release, left.kind, to.release, right.kind],
    queryFn: () => deliveryApi.osduCompare({ release: from.release, kind: left.kind! }, { release: to.release, kind: right.kind! }),
    enabled: left.kind !== null && right.kind !== null,
    // The versions never change, but the synced mappings each change is weighed against can, with a repository's sync.
    staleTime: 60_000,
  });

  // The path search narrows first, so each weight's count says how many of the searched changes it holds; the weight then
  // narrows what each impact counts.
  const pathMatched = useMemo(() => {
    const term = filter.trim().toLowerCase();
    const changes = comparison.data?.changes ?? [];
    return term === "" ? changes : changes.filter((change) => change.path.toLowerCase().includes(term));
  }, [comparison.data, filter]);
  const searched = useMemo(
    () => (weight === ALL ? pathMatched : pathMatched.filter((change) => change.weight === weight)),
    [pathMatched, weight],
  );

  // Read against synced mappings, the changes are listed by how much they weigh for them, the significant first, and each
  // weight's changes by impact; with none to weigh against, by impact alone.
  const weighed = (comparison.data?.weighedMappings.length ?? 0) > 0;
  const groups = useMemo(() => {
    const narrowed = impact === ALL ? searched : searched.filter((change) => change.impact === impact);
    const listed = weighed
      ? WEIGHTS.map((candidate) => ({
          heading: weightHeading(candidate),
          changes: narrowed
            .filter((change) => change.weight === candidate)
            .map((change, index) => ({ change, index }))
            .sort((a, b) => IMPACT_RANK[a.change.impact] - IMPACT_RANK[b.change.impact] || a.index - b.index)
            .map((entry) => entry.change),
        }))
      : IMPACTS.map((candidate) => ({ heading: impactHeading(candidate), changes: narrowed.filter((change) => change.impact === candidate) }));
    return listed.filter((group) => group.changes.length > 0);
  }, [searched, impact, weighed]);

  const countOf = (value: ImpactFilter) => (value === ALL ? searched.length : searched.filter((change) => change.impact === value).length);
  const weightCountOf = (value: WeightFilter) => (value === ALL ? pathMatched.length : pathMatched.filter((change) => change.weight === value).length);

  // The list opens with its headings open, but for the changes no synced mapping uses, which weigh least, and with every
  // variable folded; a variable opens onto its fields.
  const [openGroups, setOpenGroups] = useState<ReadonlySet<string>>(() => new Set<string>([...IMPACTS, "Significant", "Minor"]));
  const [openCards, setOpenCards] = useState<ReadonlySet<string>>(() => new Set());
  const allOpen = groups.length > 0
    && groups.every((group) => openGroups.has(group.heading.key) && group.changes.every((change) => openCards.has(change.path)));
  const toggleAll = () => {
    if (allOpen) {
      setOpenGroups(new Set());
      setOpenCards(new Set());
    } else {
      setOpenGroups(new Set(groups.map((group) => group.heading.key)));
      setOpenCards(new Set(groups.flatMap((group) => group.changes.map((change) => change.path))));
    }
  };

  const releaseList = releases.data?.releases ?? [];
  const fromVersion = left.versions.find((schema) => schema.kind === left.kind)?.version ?? null;
  const toVersion = right.versions.find((schema) => schema.kind === right.kind)?.version ?? null;
  const data = comparison.data;
  // A shared schema picked for another pair of versions falls back to the kind's own file when this pair has no such difference.
  const shownShared = data?.referencedFiles.find((file) => file.name === pickedFile) ?? null;
  const fileKey = shownShared === null ? KIND_FILE : shownShared.name;
  const missing = (!left.index.isPending && left.kind === null) || (!right.index.isPending && right.kind === null);

  return (
    <Sheet open onOpenChange={(next) => { if (!next) { onClose(); } }}>
      <SheetContent className="w-full gap-0 sm:max-w-6xl" data-testid="templates-compare-sheet">
        <SheetHeader>
          <SheetTitle className="pr-6 text-[15px]">{`Compare ${entityName(start.to.kind)} versions`}</SheetTitle>
          <SheetDescription>
            Two versions from the OSDU data definitions, each from any release, compared variable by variable and as the files
            are published, each change weighed against what the synced mappings of the kind write.
          </SheetDescription>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-3 overflow-y-auto px-4 pb-4">
          <div className="flex flex-wrap items-end gap-4">
            <SidePicker
              caption="From"
              side={{ release: from.release, kind: left.kind ?? from.kind }}
              kind={left.kind}
              releases={releaseList}
              versions={left.versions}
              loading={left.index.isPending}
              onChange={setFrom}
              testId="templates-compare-from"
            />
            <Button
              variant="ghost"
              size="icon-sm"
              aria-label="Swap the two versions"
              onClick={() => { setFrom({ release: to.release, kind: right.kind ?? to.kind }); setTo({ release: from.release, kind: left.kind ?? from.kind }); }}
              data-testid="templates-compare-swap"
            >
              <ArrowLeftRight />
            </Button>
            <SidePicker
              caption="To"
              side={{ release: to.release, kind: right.kind ?? to.kind }}
              kind={right.kind}
              releases={releaseList}
              versions={right.versions}
              loading={right.index.isPending}
              onChange={setTo}
              testId="templates-compare-to"
            />
            <div className="ml-auto">
              <TemplateCompareReport
                from={{ release: from.release, kind: left.kind ?? from.kind }}
                to={{ release: to.release, kind: right.kind ?? to.kind }}
                disabled={comparison.data === undefined}
              />
            </div>
          </div>

          {fromVersion !== null && toVersion !== null && (
            <p className="text-[13px] text-muted-foreground" data-testid="templates-compare-comparing">
              {comparingText(from.release, fromVersion, to.release, toVersion)}
            </p>
          )}

          {releases.isError && <ProblemView error={releases.error} testId="templates-compare-error" />}
          {left.index.isError && <ProblemView error={left.index.error} testId="templates-compare-error" />}
          {right.index.isError && <ProblemView error={right.index.error} testId="templates-compare-error" />}
          {missing && (
            <p className="text-[13px] text-muted-foreground" data-testid="templates-compare-missing">
              {`${entityName(start.to.kind)} is not in ${left.kind === null ? from.release : to.release}. Pick another release.`}
            </p>
          )}
          {comparison.isError && <ProblemView error={comparison.error} testId="templates-compare-error" />}
          {comparison.fetchStatus === "fetching" && data === undefined && (
            <TaskProgress label="Reading both versions from the OSDU data definitions and comparing them" testId="templates-compare-progress" />
          )}

          {data !== undefined && (
            <>
              <Verdict data={data} />
              <Weighed data={data} />
              <Tabs defaultValue="changes">
                <TabsList data-testid="templates-compare-tabs">
                  <TabsTrigger value="changes" data-testid="templates-compare-tab-changes">Changes</TabsTrigger>
                  <TabsTrigger value="files" data-testid="templates-compare-tab-files">Published files</TabsTrigger>
                </TabsList>
                <TabsContent value="changes" className="flex flex-col gap-3 pt-1">
                  <FilterBar>
                    <SearchInput
                      value={filter}
                      onChange={setFilter}
                      placeholder="Variable path"
                      label="Filter the changes"
                      testId="templates-compare-filter"
                    />
                    <ToggleGroup
                      type="single"
                      variant="outline"
                      size="sm"
                      value={impact}
                      onValueChange={(value) => {
                        if (value !== "") {
                          setImpact(value as ImpactFilter);
                        }
                      }}
                      aria-label="Impact of the change"
                      data-testid="templates-compare-impact-filter"
                    >
                      {IMPACT_FILTERS.map((value) => {
                        const count = countOf(value);
                        const Icon = value === ALL ? null : IMPACT_VISUAL[value].icon;
                        return (
                          <ToggleGroupItem
                            key={value}
                            value={value}
                            // An impact with nothing to show cannot be picked, but the one already picked stays reachable.
                            disabled={count === 0 && value !== impact}
                            className="h-8 gap-1.5 text-[13px]"
                            data-testid={`templates-compare-impact-filter-${value.toLowerCase()}`}
                          >
                            {Icon !== null && value !== ALL && <Icon className={IMPACT_VISUAL[value].text} aria-hidden />}
                            {value}
                            <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{count.toLocaleString()}</span>
                          </ToggleGroupItem>
                        );
                      })}
                    </ToggleGroup>
                    {weighed && (
                      <ToggleGroup
                        type="single"
                        variant="outline"
                        size="sm"
                        value={weight}
                        onValueChange={(value) => {
                          if (value !== "") {
                            setWeight(value as WeightFilter);
                          }
                        }}
                        aria-label="How much the change weighs for the synced mappings"
                        data-testid="templates-compare-weight-filter"
                      >
                        {WEIGHT_FILTERS.map((value) => {
                          const count = weightCountOf(value);
                          const Icon = value === ALL ? null : WEIGHT_VISUAL[value].icon;
                          return (
                            <ToggleGroupItem
                              key={value}
                              value={value}
                              // A weight with nothing to show cannot be picked, but the one already picked stays reachable.
                              disabled={count === 0 && value !== weight}
                              className="h-8 gap-1.5 text-[13px]"
                              data-testid={`templates-compare-weight-filter-${value.toLowerCase()}`}
                            >
                              {Icon !== null && value !== ALL && (
                                <Icon className={value === "Significant" ? "text-warning" : "text-muted-foreground"} aria-hidden />
                              )}
                              {value === ALL ? "Any weight" : WEIGHT_VISUAL[value].label}
                              <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{count.toLocaleString()}</span>
                            </ToggleGroupItem>
                          );
                        })}
                      </ToggleGroup>
                    )}
                    <Button variant="outline" size="sm" className="h-8" onClick={toggleAll} disabled={groups.length === 0} data-testid="templates-compare-expand-all">
                      {allOpen ? <ChevronsDownUp /> : <ChevronsUpDown />}
                      {allOpen ? "Collapse all" : "Expand all"}
                    </Button>
                  </FilterBar>
                  {groups.length === 0 ? (
                    <div className="rounded-lg border bg-card">
                      <EmptyState
                        title={data.changes.length === 0 ? "No variable differs between the two versions." : "No change matches the filter."}
                        data-testid="templates-compare-changes-empty"
                      />
                    </div>
                  ) : (
                    <div className="flex min-w-0 flex-col gap-5" data-testid="templates-compare-changes">
                      {groups.map((group) => (
                        <ChangeGroup
                          key={group.heading.key}
                          heading={group.heading}
                          changes={group.changes}
                          open={openGroups.has(group.heading.key)}
                          onOpenChange={(next) => setOpenGroups((current) => withKey(current, group.heading.key, next))}
                          openCards={openCards}
                          onCardOpenChange={(path, next) => setOpenCards((current) => withKey(current, path, next))}
                          showUsedBy={data.weighedMappings.length > 1}
                        />
                      ))}
                    </div>
                  )}
                </TabsContent>
                <TabsContent value="files" className="flex flex-col gap-2 pt-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <Select value={fileKey} onValueChange={setPickedFile}>
                      <SelectTrigger size="sm" className="h-8 w-96 max-w-full" data-testid="templates-compare-file-picker" aria-label="The file to compare">
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectItem value={KIND_FILE}>
                          <span className="font-mono">{data.to.path.slice(data.to.path.lastIndexOf("/") + 1)}</span>
                          <span className="text-[11px] text-muted-foreground">{data.sameFile ? "the kind, the same" : "the kind"}</span>
                        </SelectItem>
                        {data.referencedFiles.map((file) => (
                          <SelectItem key={file.name} value={file.name}>
                            <span className="font-mono">{file.name}</span>
                            <span className="text-[11px] text-muted-foreground">{referencedState(file)}</span>
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                    <span className="text-xs text-muted-foreground" data-testid="templates-compare-referenced-summary">
                      {data.referencedFiles.length === 0
                        ? `Every shared schema it refers to is the same in both (${data.sameReferencedFiles}).`
                        : `${data.referencedFiles.length} shared schema${data.referencedFiles.length === 1 ? "" : "s"} it refers to ${data.referencedFiles.length === 1 ? "differs" : "differ"}; ${data.sameReferencedFiles} ${data.sameReferencedFiles === 1 ? "is" : "are"} the same.`}
                    </span>
                  </div>
                  {shownShared === null ? (
                    <>
                      <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-muted-foreground">
                        <span className="inline-flex items-center gap-1">
                          <span className="font-mono">{`${data.from.release.name} ${data.from.path}`}</span>
                          <LinkRef url={data.from.webUrl} title="From, in the OSDU data definitions" testId="templates-compare-from-link" copyTestId="templates-compare-from-copy" />
                        </span>
                        <span data-testid="templates-compare-file-state">
                          {data.sameFile ? "The files are identical." : data.onlyIdentifiersDiffer ? "The files differ only in their version identifiers." : null}
                        </span>
                        <span className="inline-flex items-center gap-1">
                          <span className="font-mono">{`${data.to.release.name} ${data.to.path}`}</span>
                          <LinkRef url={data.to.webUrl} title="To, in the OSDU data definitions" testId="templates-compare-to-link" copyTestId="templates-compare-to-copy" />
                        </span>
                      </div>
                      <DiffView
                        key={KIND_FILE}
                        original={data.from.fileText}
                        modified={data.to.fileText}
                        sideLabels={{
                          original: `${data.from.kind}  (${data.from.release.name}, template ${data.from.templateVersion})`,
                          modified: `${data.to.kind}  (${data.to.release.name}, template ${data.to.templateVersion})`,
                        }}
                        foldUnchanged
                        language="json"
                        height={620}
                        data-testid="templates-compare-files"
                      />
                    </>
                  ) : (
                    <>
                      <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-muted-foreground">
                        <span className="inline-flex items-center gap-1">
                          <span className="font-mono">{`${data.from.release.name} ${shownShared.fromPath ?? "does not refer to it"}`}</span>
                          <LinkRef url={shownShared.fromWebUrl} title="From, in the OSDU data definitions" testId="templates-compare-from-link" copyTestId="templates-compare-from-copy" />
                        </span>
                        <span className="inline-flex items-center gap-1">
                          <span className="font-mono">{`${data.to.release.name} ${shownShared.toPath ?? "does not refer to it"}`}</span>
                          <LinkRef url={shownShared.toWebUrl} title="To, in the OSDU data definitions" testId="templates-compare-to-link" copyTestId="templates-compare-to-copy" />
                        </span>
                      </div>
                      <DiffView
                        key={shownShared.name}
                        original={shownShared.fromText ?? ""}
                        modified={shownShared.toText ?? ""}
                        sideLabels={{
                          original: `${data.from.release.name}  ${shownShared.fromPath ?? "not referred to"}`,
                          modified: `${data.to.release.name}  ${shownShared.toPath ?? "not referred to"}`,
                        }}
                        foldUnchanged
                        language="json"
                        height={620}
                        data-testid="templates-compare-files"
                      />
                    </>
                  )}
                </TabsContent>
              </Tabs>
            </>
          )}
        </div>
      </SheetContent>
    </Sheet>
  );
}
