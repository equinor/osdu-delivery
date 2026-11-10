import type { ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { BookOpen, Database, History, Info, LayoutDashboard, Play, Power, ScrollText, ShieldCheck, Table2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Select, SelectContent, SelectGroup, SelectItem, SelectLabel, SelectTrigger, SelectValue } from "@/components/ui/select";
import { cn } from "@/lib/utils";
import type { DeliveryCache, DeliveryCacheFlow, DeliveryCacheTypeSource, DeliveryCacheVersion } from "../../api/delivery";
import { ConnectionRef } from "@/components/ConnectionRef";
import { DataTable, type Column } from "@/components/DataTable";
import { RichTooltip } from "@/components/RichTooltip";
import { StatePill } from "@/components/StatusBadge";
import { CacheMappingGuide, Snippet } from "./CacheMappingReference";
import { DeliveryCacheFlags } from "./DeliveryCacheFlags";
import { KindText } from "./KindText";
import {
  cacheEntryExample, cacheReference, lookupEntryExample, lookupReplaceExample, scheduleCadence, summarizeTypes,
  type CachedTypeSummary,
} from "./cacheFormat";
import type { SetupNode } from "./cacheSetupNode";

/** The partition's feature flag OSDU Delivery reads, which the overview names by its state. */
const KEYWORD_LOWER = { service: "indexer", name: "featureFlag.keywordLower.enabled", short: "keywordLower" };

/** The retention of a cache flow that declares no `retentionDays`, in days, as the module applies it. */
const DEFAULT_RETENTION_DAYS = 7;

/** A number of days as the setup says it. */
function days(count: number): string {
  return `${count.toLocaleString()} day${count === 1 ? "" : "s"}`;
}

/** What each part of the setup is for, as the info mark beside its title says it. */
const ABOUT = {
  overview: "How this partition's cache is filled, in brief: the cache flows that fill it, the types they declare, what a changed value does, the OSDU feature flags the engine reads, and how a mapping reads the cache. Pick any of them for the whole of it.",
  flow: "A cache flow is a YAML file in a repository that says which types to cache for the partition, where each comes from and which values to keep. This is what the last repository sync found in it: edit the file and sync its repository to change it. Several flows may fill one partition; what they declare for one type is merged into one type.",
  type: "One type the cache holds, as its cache flows together declare it: where its records come from, every value kept for each record and the name a mapping reads it by, and what a changed value does to the records already delivered. A refresh of any flow declaring it that changes what the cache holds writes a new version, which becomes current.",
  flags: "Settings of the OSDU platform for this partition, read with every refresh and never changed by OSDU Delivery. The one the engine relies on changes how a lookup matches.",
  mapping: "How a delivery mapping reads this cache. A mapping never names the cache: every delivery flow that delivers to the partition reads its cache.",
};

/** A part's title with what it is for on the info mark beside it. */
function PartTitle({ title, about, children }: { title: ReactNode; about: string; children?: ReactNode }) {
  return (
    <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1.5">
      <h2 className="flex min-w-0 items-center gap-1.5 text-[15px] font-medium">
        {title}
        <RichTooltip body={about}>
          <span className="inline-flex text-muted-foreground">
            <Info className="size-3.5" aria-label="What this is" />
          </span>
        </RichTooltip>
      </h2>
      {children !== undefined && <div className="ml-auto flex flex-wrap items-center gap-2">{children}</div>}
    </div>
  );
}

/** One line of facts: a label, and what it says. */
function Fact({ label, children, testId }: { label: string; children: ReactNode; testId?: string }) {
  return (
    <div className="grid gap-x-6 gap-y-0.5 py-2.5 sm:grid-cols-[11rem_minmax(0,1fr)]" data-testid={testId}>
      <dt className="text-[12px] text-muted-foreground">{label}</dt>
      <dd className="min-w-0 text-[13px]">{children}</dd>
    </div>
  );
}

/** A name that opens its part of the setup. */
function NodeLink({ label, onClick, mono = true, testId }: { label: string; onClick: () => void; mono?: boolean; testId?: string }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn("self-start text-left text-primary underline-offset-2 hover:underline focus-visible:underline focus-visible:outline-none", mono && "font-mono text-[12.5px]")}
      data-testid={testId}
    >
      {label}
    </button>
  );
}

/** Names that open their parts, separated by commas. */
function NodeLinks({ items }: { items: { key: string; label: string; onClick: () => void }[] }) {
  return (
    <span className="flex flex-wrap items-baseline gap-x-1">
      {items.map((item, index) => (
        <span key={item.key} className="inline-flex items-baseline">
          <NodeLink label={item.label} onClick={item.onClick} />
          {index < items.length - 1 && <span className="text-muted-foreground">,</span>}
        </span>
      ))}
    </span>
  );
}

/**
 * Where one flow's declaration of a type takes its records from: the kind it searches on OSDU, the ingestion table it reads
 * with the column rows are keyed by and the most rows its capture takes, or the dictionary document it holds and the name
 * of its key.
 */
function SourceText({ source }: { source: DeliveryCacheTypeSource }) {
  switch (source.origin) {
    case "table":
      return (
        <span className="flex min-w-0 flex-col font-mono text-[12px]">
          <span className="truncate" title={source.sourceObject ?? undefined}>table {source.sourceObject}</span>
          <span className="truncate text-[11px] text-muted-foreground">
            keyed by {source.keyField}
            {source.maxRows !== null && <>, at most {source.maxRows.toLocaleString("en-US")} rows</>}
          </span>
        </span>
      );
    case "dictionary":
      return (
        <span className="flex min-w-0 flex-col font-mono text-[12px]">
          <span className="truncate" title={source.dictionaryPath ?? undefined}>dictionary {source.dictionaryPath}</span>
          <span className="truncate text-[11px] text-muted-foreground">keyed by {source.keyField}</span>
        </span>
      );
    case "dimension":
      return (
        <span className="flex min-w-0 flex-col font-mono text-[12px]">
          <span className="truncate" title={source.sourceObject ?? undefined}>dimension {source.sourceObject}</span>
          <span className="truncate text-[11px] text-muted-foreground">its members, keyed by {source.keyField}</span>
        </span>
      );
    default:
      return source.kind === null ? null : <KindText kind={source.kind} />;
  }
}

/** What a changed value of a type does: it goes out on the next delivery, or it waits for approval. */
function ChangeRule({ onChange }: { onChange: string }) {
  return onChange === "approve"
    ? <StatePill tone="warning" label="waits for approval" icon={ShieldCheck} testId="delivery-cache-rule-approve" />
    : <span className="text-[12.5px]">goes out on each record's next delivery</span>;
}

/**
 * Where an OSDU type's values are read: the projection the search index returns, or each record whole from storage, which
 * every capture of the type does when any flow declaring it asks for that (types[].fetchRecords).
 */
function ValuesReadFrom({ type, onNode }: { type: CachedTypeSummary; onNode: (node: SetupNode) => void }) {
  if (!type.fetchRecords) {
    return (
      <span className="text-[12.5px]">
        the search index, which holds what the kind's schema describes
      </span>
    );
  }

  const asking = type.sources.filter((source) => source.fetchRecords).map((source) => source.flow);
  return (
    <span className="flex flex-col gap-0.5">
      <span className="text-[12.5px]">each record whole, from storage: every path a record holds, such as one under data.ExtensionProperties</span>
      {type.sources.length > 1 && (
        <span className="flex flex-wrap items-baseline gap-x-1.5 text-[11.5px] text-muted-foreground">
          <span>asked by</span>
          {asking.map((flow) => <NodeLink key={flow} label={flow} onClick={() => onNode(`flow:${flow}`)} />)}
        </span>
      )}
    </span>
  );
}

/** Where a flow captures from: the OSDU endpoint it searches, the ingestion connection it reads, or its repository. */
function CapturedFrom({ flow }: { flow: DeliveryCacheFlow }) {
  if (flow.endpoint === null && flow.connection === null) {
    return <span className="text-[12.5px]">its repository (dictionaries)</span>;
  }

  return (
    <span className="flex flex-col items-start gap-0.5">
      {flow.endpoint !== null && <ConnectionRef value={flow.endpoint} maxWidth={320} />}
      {flow.connection !== null && <ConnectionRef value={flow.connection} maxWidth={320} />}
    </span>
  );
}

/** When a flow runs: its schedules, or on demand. */
function Cadence({ flow }: { flow: DeliveryCacheFlow }) {
  return flow.schedules.length === 0
    ? <span className="text-[12.5px]">on demand, with Refresh</span>
    : <span className="font-mono text-[12.5px]">{flow.schedules.map(scheduleCadence).join("; ")}</span>;
}

/** One entry of the navigator on the left. */
function TreeItem({ label, icon, selected, depth = 0, mono = false, onClick, testId, node }: {
  label: string;
  icon?: ReactNode;
  selected: boolean;
  depth?: 0 | 1;
  mono?: boolean;
  onClick: () => void;
  testId: string;
  node: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-current={selected ? "true" : undefined}
      className={cn(
        "relative flex h-7 w-full items-center gap-2 rounded-md px-2 text-left text-[13px] outline-none transition-colors duration-120 focus-visible:bg-accent/60",
        depth === 1 && "pl-8 text-[12.5px]",
        selected ? "bg-accent font-medium text-foreground" : "text-foreground/90 hover:bg-accent/60",
      )}
      data-testid={testId}
      data-node={node}
    >
      {selected && <span className="absolute -left-1.5 h-4 w-0.5 rounded-r bg-primary" />}
      {icon}
      <span className={cn("min-w-0 flex-1 truncate", mono && "font-mono text-[12px]")} title={label}>{label}</span>
    </button>
  );
}

function TreeHeading({ children }: { children: ReactNode }) {
  return (
    <h3 className="mt-2 flex h-6 items-center px-2 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">{children}</h3>
  );
}

/**
 * How the cache is filled, as a tree: the partition's overview, each cache flow with the types it declares under it, and
 * the partition's own settings (its OSDU feature flags, and how a mapping reads the cache). A type several flows declare
 * is under each of them, and picking it anywhere opens the one type they declare together.
 */
function SetupTree({ cache, node, onNode }: { cache: DeliveryCache; node: SetupNode; onNode: (node: SetupNode) => void }) {
  return (
    <nav
      aria-label="Cache setup"
      className="flex min-h-0 flex-col overflow-hidden rounded-lg border border-border bg-card lg:sticky lg:top-0 lg:max-h-[calc(100dvh-14rem)]"
      data-testid="delivery-cache-setup-tree"
    >
      <div className="min-h-0 flex-1 overflow-y-auto px-2 py-1.5">
        <TreeItem
          label={`Partition ${cache.scope}`}
          icon={<LayoutDashboard className="size-4 shrink-0 opacity-75" />}
          selected={node === "overview"}
          onClick={() => onNode("overview")}
          testId="delivery-cache-setup-overview"
          node="overview"
        />
        <TreeHeading>Cache flows</TreeHeading>
        {cache.flows.map((flow) => (
          <div key={`${flow.repoId}:${flow.name}`}>
            <TreeItem
              label={flow.name}
              mono
              icon={<ScrollText className="size-4 shrink-0 opacity-75" />}
              selected={node === `flow:${flow.name}`}
              onClick={() => onNode(`flow:${flow.name}`)}
              testId="delivery-cache-setup-flow"
              node={`flow:${flow.name}`}
            />
            {[...flow.types].sort((a, b) => a.localeCompare(b)).map((type) => (
              <TreeItem
                key={type}
                label={type}
                depth={1}
                selected={node === `type:${type}`}
                onClick={() => onNode(`type:${type}`)}
                testId="delivery-cache-setup-type"
                node={`type:${type}`}
              />
            ))}
          </div>
        ))}
        <TreeHeading>Partition</TreeHeading>
        <TreeItem
          label="OSDU feature flags"
          icon={<Power className="size-4 shrink-0 opacity-75" />}
          selected={node === "flags"}
          onClick={() => onNode("flags")}
          testId="delivery-cache-setup-flags"
          node="flags"
        />
        <TreeItem
          label="Reading it in a mapping"
          icon={<BookOpen className="size-4 shrink-0 opacity-75" />}
          selected={node === "mapping"}
          onClick={() => onNode("mapping")}
          testId="delivery-cache-setup-mapping"
          node="mapping"
        />
      </div>
    </nav>
  );
}

/**
 * The navigator as a picker, for a screen too narrow for the tree beside the part: the same entries, grouped the same way,
 * so the part picked is right under it rather than below the whole tree.
 */
function SetupPicker({ cache, node, onNode }: { cache: DeliveryCache; node: SetupNode; onNode: (node: SetupNode) => void }) {
  // A picker offers each entry once, so a type several flows declare is listed under the first of them.
  const listed = new Set<string>();
  const groups = cache.flows.map((flow) => {
    const types: string[] = [];
    for (const type of [...flow.types].sort((a, b) => a.localeCompare(b))) {
      if (!listed.has(type)) {
        listed.add(type);
        types.push(type);
      }
    }

    return { flow, types };
  });
  return (
    <Select value={node} onValueChange={(value) => onNode(value as SetupNode)}>
      <SelectTrigger size="sm" className="h-8 w-full text-left sm:w-80" aria-label="Part of the setup" data-testid="delivery-cache-setup-picker">
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value="overview">Partition {cache.scope}</SelectItem>
        {groups.map(({ flow, types }) => (
          <SelectGroup key={`${flow.repoId}:${flow.name}`}>
            <SelectLabel>Cache flow</SelectLabel>
            <SelectItem value={`flow:${flow.name}`}><span className="font-mono text-[12px]">{flow.name}</span></SelectItem>
            {types.map((type) => (
              <SelectItem key={type} value={`type:${type}`}>
                <span className="pl-3">{type}</span>
              </SelectItem>
            ))}
          </SelectGroup>
        ))}
        <SelectGroup>
          <SelectLabel>Partition</SelectLabel>
          <SelectItem value="flags">OSDU feature flags</SelectItem>
          <SelectItem value="mapping">Reading it in a mapping</SelectItem>
        </SelectGroup>
      </SelectContent>
    </Select>
  );
}

/**
 * The partition's setup in brief, each fact one line with what it names opening its part: the flows that fill the cache,
 * the types they declare, what a changed value does, when a new version is written, the feature flag the engine reads, and
 * how a mapping reads the cache.
 */
function SetupOverview({ cache, types, version, onNode }: {
  cache: DeliveryCache;
  types: CachedTypeSummary[];
  version: DeliveryCacheVersion | null;
  onNode: (node: SetupNode) => void;
}) {
  const approving = types.filter((type) => type.onChange === "approve");
  const keywordLower = version?.systemProperties.find((p) => p.service === KEYWORD_LOWER.service && p.name === KEYWORD_LOWER.name);
  const otherFlags = (version?.systemProperties.length ?? 0) - (keywordLower === undefined ? 0 : 1);

  return (
    <div className="flex flex-col gap-3" data-testid="delivery-cache-setup-summary">
      <PartTitle title={<>Partition <span className="font-mono">{cache.scope}</span></>} about={ABOUT.overview} />
      <Card className="gap-0 rounded-lg px-4 py-1.5">
        <dl className="divide-y divide-border">
          <Fact label="Filled by" testId="delivery-cache-setup-fact-flows">
            {cache.flows.length === 0
              ? <span className="text-muted-foreground">no synced cache flow</span>
              : <NodeLinks items={cache.flows.map((flow) => ({ key: `${flow.repoId}:${flow.name}`, label: flow.name, onClick: () => onNode(`flow:${flow.name}`) }))} />}
          </Fact>
          <Fact label="Types" testId="delivery-cache-setup-fact-types">
            {types.length === 0
              ? <span className="text-muted-foreground">none declared</span>
              : <NodeLinks items={types.map((type) => ({ key: type.name, label: type.name, onClick: () => onNode(`type:${type.name}`) }))} />}
          </Fact>
          <Fact label="When a value changes">
            {approving.length === 0
              ? "The records built from it are updated on their next delivery; no type asks for approval."
              : (
                <span className="flex flex-wrap items-baseline gap-x-1">
                  <NodeLinks items={approving.map((type) => ({ key: type.name, label: type.name, onClick: () => onNode(`type:${type.name}`) }))} />
                  <span>{approving.length === 1 ? "waits" : "wait"} for approval; every other type is updated on its records' next delivery.</span>
                </span>
              )}
          </Fact>
          <Fact label="A new version">
            Written whenever a refresh of any of its flows changes what the cache holds, and current from then on.
          </Fact>
          <Fact label="History kept" testId="delivery-cache-setup-fact-retention">
            A replaced version&apos;s records for{" "}
            <span className="font-mono text-[12.5px]">{days(cache.retentionDays ?? DEFAULT_RETENTION_DAYS)}</span>, then pruned by the next refresh;
            the version stays listed with what it changed.{" "}
            <RichTooltip
              title="Retention"
              body={"The longest retentionDays any cache flow of the partition declares (7 when none does). The current version, the one it replaced and every version a delivery flow pins keep their records whatever their age. A delivered record keeps the cached values it was built from, so pruning never touches what was delivered."}
            >
              <span className="inline-flex align-[-2px] text-muted-foreground">
                <Info className="size-3.5" aria-label="How the retention works" />
              </span>
            </RichTooltip>
          </Fact>
          <Fact label="OSDU feature flags">
            {version === null
              ? <span className="text-muted-foreground">none read yet: no refresh has run</span>
              : (
                <span className="flex flex-wrap items-baseline gap-x-1.5">
                  <NodeLink
                    label={keywordLower === undefined ? "the flags read" : `${KEYWORD_LOWER.short} ${keywordLower.state === "Enabled" ? "on" : keywordLower.state === "Disabled" ? "off" : "unknown"}`}
                    onClick={() => onNode("flags")}
                  />
                  {keywordLower !== undefined && <span className="text-muted-foreground">read by OSDU Delivery</span>}
                  {otherFlags > 0 && <span className="text-muted-foreground">and {otherFlags.toLocaleString()} more the platform reports</span>}
                </span>
              )}
          </Fact>
          <Fact label="In a mapping">
            <span className="flex flex-wrap items-baseline gap-x-1.5">
              <span className="font-mono text-[12.5px]">$cache: &lt;Type&gt;.&lt;name&gt;</span>
              <span className="text-muted-foreground">found with $findBy;</span>
              <NodeLink label="how a mapping reads it" mono={false} onClick={() => onNode("mapping")} />
            </span>
          </Fact>
        </dl>
      </Card>
    </div>
  );
}

/**
 * One cache flow: its file and where it captures from, when it runs, the partitions it builds, the history it asks the
 * partition to keep, and the types it declares.
 */
function FlowDetail({ flow, types, partitionRetention, onNode, onRefresh }: {
  flow: DeliveryCacheFlow;
  types: CachedTypeSummary[];
  /** The retention the partition keeps: the longest of its flows'. */
  partitionRetention: number;
  onNode: (node: SetupNode) => void;
  onRefresh: (flow: DeliveryCacheFlow) => void;
}) {
  const retention = flow.retentionDays ?? DEFAULT_RETENTION_DAYS;
  const navigate = useNavigate();
  const declared = types.filter((type) => flow.types.includes(type.name));
  const columns: Column<CachedTypeSummary>[] = [
    {
      id: "type",
      header: "Type",
      render: (type) => (
        <span className="flex flex-col">
          <NodeLink label={type.name} onClick={() => onNode(`type:${type.name}`)} testId="delivery-cache-setup-flow-type" />
          <span className="font-mono text-[11px] text-muted-foreground">{type.entityType}</span>
        </span>
      ),
    },
    {
      id: "source",
      header: "Comes from",
      fill: true,
      floor: 180,
      render: (type) => {
        const source = type.sources.find((candidate) => candidate.flow === flow.name) ?? type.sources[0];
        return source === undefined ? null : <SourceText source={source} />;
      },
    },
    {
      id: "keeps",
      header: "Keeps",
      render: (type) => <span className="font-mono text-[12px]">{[type.key, ...type.fields.map((field) => field.as)].filter((name) => name !== null).join(", ")}</span>,
    },
    { id: "change", header: "When a value changes", render: (type) => <ChangeRule onChange={type.onChange} /> },
  ];

  return (
    <div className="flex flex-col gap-3" data-testid="delivery-cache-setup-flow-detail">
      <PartTitle title={<span className="font-mono">{flow.name}</span>} about={ABOUT.flow}>
        {flow.pipelineId !== null && (
          <>
            <Button size="sm" variant="outline" onClick={() => navigate(`/pipelines/${flow.pipelineId}?tab=yaml`)} data-testid="delivery-cache-flow-yaml">
              <ScrollText />
              View YAML
            </Button>
            <Button size="sm" variant="outline" onClick={() => onRefresh(flow)} data-testid="delivery-cache-flow-refresh">
              <Play />
              Refresh
            </Button>
          </>
        )}
      </PartTitle>
      <Card className="gap-0 rounded-lg px-4 py-1.5">
        <dl className="divide-y divide-border">
          <Fact label="File">
            <span className="flex flex-wrap items-baseline gap-x-2">
              <span className="break-all font-mono text-[12.5px]">{flow.relativePath}</span>
              <span className="text-[12px] text-muted-foreground">in {flow.repoName}</span>
            </span>
          </Fact>
          <Fact label="Captures from"><CapturedFrom flow={flow} /></Fact>
          <Fact label="Runs"><Cadence flow={flow} /></Fact>
          {(flow.partitions ?? []).length > 0 && (
            <Fact label="Builds">
              <span className="font-mono text-[12.5px]" data-testid={`delivery-cache-flow-partitions-${flow.name}`}>{flow.partitions!.join(", ")}</span>
            </Fact>
          )}
          <Fact label="Keeps history" testId={`delivery-cache-flow-retention-${flow.name}`}>
            <span className="flex flex-wrap items-baseline gap-x-1.5">
              <span className="font-mono text-[12.5px]">{days(retention)}</span>
              <span className="text-[12px] text-muted-foreground">
                {flow.retentionDays === undefined || flow.retentionDays === DEFAULT_RETENTION_DAYS ? "retentionDays, the default" : "retentionDays"}
                {partitionRetention > retention && `; the partition keeps ${days(partitionRetention)}, which another of its flows asks for`}
              </span>
            </span>
          </Fact>
        </dl>
      </Card>
      <DataTable
        columns={columns}
        rows={declared}
        rowKey={(type) => type.name}
        emptyMessage="The flow declares no type the cache holds."
        data-testid="delivery-cache-definition-flows"
      />
    </div>
  );
}

/**
 * One type the cache holds: where its records come from (per flow declaring it), every value kept with the name a mapping
 * reads it by, what a changed value does, and the mapping entry that reads it, ready to copy. Its records and its own
 * versions are a click away.
 */
function TypeDetail({ type, onNode, onOpen }: {
  type: CachedTypeSummary;
  onNode: (node: SetupNode) => void;
  onOpen: (type: string, tab: "records" | "history") => void;
}) {
  const several = type.sources.length > 1;
  const valueName = type.fields.find((field) => field.as !== type.key)?.as ?? null;
  const fieldColumns: Column<CachedTypeSummary["fields"][number]>[] = [
    { id: "name", header: "Read as", render: (field) => <span className="font-mono text-[12px]">{cacheReference(type.name, field.as)}</span> },
    { id: "path", header: "From", fill: true, floor: 160, render: (field) => <span className="font-mono text-[12px] text-muted-foreground">{field.path}</span> },
    ...(several
      ? [{ id: "flows", header: "Declared by", render: (field: CachedTypeSummary["fields"][number]) => <span className="font-mono text-[12px]">{field.flows.join(", ")}</span> }]
      : []),
  ];

  return (
    <div className="flex flex-col gap-3" data-testid="delivery-cache-setup-type-detail">
      <PartTitle
        title={(
          <span className="flex flex-wrap items-baseline gap-x-2">
            <span className="font-mono">{type.name}</span>
            <span className="font-mono text-[12px] font-normal text-muted-foreground">{type.entityType}</span>
          </span>
        )}
        about={ABOUT.type}
      >
        <Button size="sm" variant="outline" onClick={() => onOpen(type.name, "records")} data-testid="delivery-cache-setup-open-records">
          <Table2 />
          Records
        </Button>
        <Button size="sm" variant="outline" onClick={() => onOpen(type.name, "history")} data-testid="delivery-cache-setup-open-history">
          <History />
          History
        </Button>
      </PartTitle>
      <Card className="gap-0 rounded-lg px-4 py-1.5">
        <dl className="divide-y divide-border">
          <Fact label="Comes from" testId="delivery-cache-setup-type-source">
            <span className="flex flex-col gap-1.5">
              {type.sources.map((source) => (
                <span key={source.flow} className="flex min-w-0 flex-col gap-0.5" data-testid={`delivery-cache-source-${source.origin}`}>
                  <SourceText source={source} />
                  <span className="flex flex-wrap items-baseline gap-x-1.5 text-[11.5px] text-muted-foreground">
                    <span>declared by</span>
                    <NodeLink label={source.flow} onClick={() => onNode(`flow:${source.flow}`)} />
                    {source.query !== null && source.query !== "*" && <span className="font-mono">where {source.query}</span>}
                  </span>
                </span>
              ))}
            </span>
          </Fact>
          {type.origin === "osdu" && (
            <Fact label="Values read from" testId="delivery-cache-type-read">
              <ValuesReadFrom type={type} onNode={onNode} />
            </Fact>
          )}
          {type.key !== null && (
            <Fact label="Kept under">
              <span className="font-mono text-[12.5px]" data-testid="delivery-cache-type-key">{type.key}</span>
              <span className="text-muted-foreground">, each row's key: a row is found by it</span>
            </Fact>
          )}
          <Fact label="When a value changes"><ChangeRule onChange={type.onChange} /></Fact>
          <Fact label="Held now">
            <span className="font-mono text-[12.5px] tabular-nums">{type.items.toLocaleString()}</span>
            <span className="text-muted-foreground"> {type.key === null ? "record" : "row"}{type.items === 1 ? "" : "s"} in the current version</span>
          </Fact>
        </dl>
      </Card>

      <section className="flex flex-col gap-2">
        <h3 className="text-[13px] font-medium">Values kept</h3>
        <DataTable
          columns={fieldColumns}
          rows={type.fields}
          rowKey={(field) => field.as}
          emptyMessage="No value is kept beyond the record's id."
          data-testid="delivery-cache-definition-types"
        />
      </section>

      <section className="flex flex-col gap-2" data-testid="delivery-cache-setup-type-mapping">
        <h3 className="text-[13px] font-medium">In a mapping</h3>
        {type.key === null
          ? <Snippet text={cacheEntryExample(type.name, type.fields[0]?.as ?? null)} testId="delivery-cache-setup-type-entry" />
          : (
            <>
              <Snippet text={lookupEntryExample(type.name, type.key, valueName)} testId="delivery-cache-setup-type-entry" />
              <Snippet text={lookupReplaceExample(type.name, type.key, type.fields.map((field) => field.as))} testId="delivery-cache-setup-type-replace" />
            </>
          )}
      </section>
    </div>
  );
}

/**
 * How the partition's cache is filled, as a navigator: a tree of the cache flows with the types each declares, and the
 * partition's own settings, beside the one part picked. It opens on the partition's overview, a few lines each naming what
 * opens its whole: nothing is left out, and nothing is shown before it is asked for.
 */
export function DeliveryCacheSetup({ cache, version, versions, node, onNode, onRefresh, onOpen }: {
  cache: DeliveryCache;
  /** The current version: its OSDU feature flags. */
  version: DeliveryCacheVersion | null;
  /** Every version, newest first, to tell when each flag last changed. */
  versions: DeliveryCacheVersion[];
  node: SetupNode;
  onNode: (node: SetupNode) => void;
  onRefresh: (flow: DeliveryCacheFlow) => void;
  /** Opens a type on the Records or History tab. */
  onOpen: (type: string, tab: "records" | "history") => void;
}) {
  const types = summarizeTypes(cache);
  const flow = node.startsWith("flow:") ? cache.flows.find((candidate) => `flow:${candidate.name}` === node) ?? null : null;
  const type = node.startsWith("type:") ? types.find((candidate) => `type:${candidate.name}` === node) ?? null : null;
  // A flow or a type the cache no longer holds (a stale link) falls back to the overview.
  const shown: SetupNode = (node.startsWith("flow:") && flow === null) || (node.startsWith("type:") && type === null) ? "overview" : node;

  return (
    <div className="grid items-start gap-4 lg:grid-cols-[16rem_minmax(0,1fr)]" data-testid="delivery-cache-definition">
      <div className="hidden lg:block">
        <SetupTree cache={cache} node={shown} onNode={onNode} />
      </div>
      <div className="flex min-w-0 flex-col gap-3">
        <div className="lg:hidden">
          <SetupPicker cache={cache} node={shown} onNode={onNode} />
        </div>
        {shown === "overview" && <SetupOverview cache={cache} types={types} version={version} onNode={onNode} />}
        {flow !== null && (
          <FlowDetail
            flow={flow}
            types={types}
            partitionRetention={cache.retentionDays ?? DEFAULT_RETENTION_DAYS}
            onNode={onNode}
            onRefresh={onRefresh}
          />
        )}
        {type !== null && <TypeDetail type={type} onNode={onNode} onOpen={onOpen} />}
        {shown === "flags" && (
          <div id="delivery-cache-part-flags" className="flex flex-col gap-3" data-testid="delivery-cache-part-flags">
            <PartTitle title="OSDU feature flags" about={ABOUT.flags} />
            <DeliveryCacheFlags scope={cache.scope} version={version} versions={versions} />
          </div>
        )}
        {shown === "mapping" && (
          <div className="flex flex-col gap-3" data-testid="delivery-cache-setup-mapping-detail">
            <PartTitle title="Reading the cache in a mapping" about={ABOUT.mapping} />
            <CacheMappingGuide types={types} scope={cache.scope} />
          </div>
        )}
        {cache.flows.length === 0 && shown === "overview" && (
          <p className="mt-3 flex items-center gap-2 text-[12.5px] text-muted-foreground">
            <Database className="size-4" />
            Sync a repository holding a cache flow for {cache.scope} to fill it.
          </p>
        )}
      </div>
    </div>
  );
}
