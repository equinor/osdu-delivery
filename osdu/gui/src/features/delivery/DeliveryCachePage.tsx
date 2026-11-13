import { useEffect, useState, type ReactNode } from "react";
import { Link as RouterLink, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, DatabaseZap, Info, Play, ScrollText, ShieldAlert } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { cn } from "@/lib/utils";
import { isApiError } from "@/api/client";
import { deliveryApi, type DeliveryCache, type DeliveryCacheFlow } from "../../api/delivery";
import { CorrelationError } from "@/components/CorrelationError";
import { EmptyState } from "@/components/EmptyState";
import { FilterCombobox, type FilterOption } from "@/components/FilterCombobox";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { SummaryStrip, type SummaryCell } from "@/components/SummaryStrip";
import { TriggerRunDialog } from "@/features/runs/TriggerRunDialog";
import { scheduleCadence, summarizeTypes, type CachedTypeSummary } from "./cacheFormat";
import { isPartitionId, useActivePartition } from "./activePartition";
import { DeliveryCacheApprovals } from "./DeliveryCacheApprovals";
import { DeliveryCacheSetup } from "./DeliveryCacheSetup";
import { parseSetupNode, type SetupNode } from "./cacheSetupNode";
import { CacheCompareDialog, DeliveryCacheHistory } from "./DeliveryCacheHistory";
import { DEFAULT_VERSIONS_KEPT } from "./cacheVersionsKept";
import { DeliveryCacheRecords } from "./DeliveryCacheRecords";

/**
 * What the type picker says beside a type: its family and how much the current version holds of it. A lookup table says
 * where its rows come from and counts rows, since they are not OSDU records; a type whose changes wait says so.
 */
function typeHint(type: CachedTypeSummary): string {
  const count = type.items.toLocaleString();
  const held = type.key === null
    ? `${type.family.toLowerCase()} · ${count} record${type.items === 1 ? "" : "s"}`
    : `lookup table from ${type.origin === "dictionary" ? "a dictionary" : type.origin === "dimension" ? "a dimension" : "an ingestion table"} · ${count} row${type.items === 1 ? "" : "s"}`;
  return held + (type.onChange === "approve" ? " · changes need approval" : "");
}

/**
 * The four questions the page answers, a tab each: what the cache holds, how it changed, what it means for the records
 * already in OSDU, and how it is filled.
 */
type Tab = "records" | "history" | "deliveries" | "setup";

/** A section of a tab a link can land on: what records were built without, a state of the Deliveries list, and the partition's OSDU feature flags. */
type Section = "gaps" | "flags";

/**
 * The tab a link names, and the section of it the link pointed at. The tabs were once Versions, Changes, Built without,
 * Definition and OSDU feature flags (and before that System properties), and a link from then still lands where it
 * pointed: on the tab that holds it now, scrolled to its section.
 */
function linkedTab(value: string | null): { tab: Tab; section: Section | null } {
  switch (value) {
    case "history":
    case "versions":
      return { tab: "history", section: null };
    case "deliveries":
    case "changes":
      return { tab: "deliveries", section: null };
    case "gaps":
      return { tab: "deliveries", section: "gaps" };
    case "setup":
    case "definition":
      return { tab: "setup", section: null };
    case "flags":
    case "system":
      return { tab: "setup", section: "flags" };
    default:
      return { tab: "records", section: null };
  }
}

/** What each tab is for, as hovering its name says it. */
const TAB_PURPOSE: Record<Tab, string> = {
  records: "What the cache holds, a type at a time: now, or as an earlier version held it.",
  history: "Every version of the cache: which refresh wrote it and which types it changed. Pick a type to see its own versions.",
  deliveries: "What the cache means for records already in OSDU: those a cache change updates, which happens on their next delivery unless the type asks for approval, and those built without a value the cache did not hold yet.",
  setup: "How the cache is filled: the cache flows and the types each declares, the partition's OSDU feature flags, and how a mapping reads the cache, one at a time from the list beside them.",
};

/** A tab's name, with what the tab is for on hover. The hover sits on the name, so it never touches the tab's own state. */
function TabName({ tab, children }: { tab: Tab; children: ReactNode }) {
  return (
    <RichTooltip body={TAB_PURPOSE[tab]}>
      <span>{children}</span>
    </RichTooltip>
  );
}

/**
 * One section of a tab: its title, what it is for on the info mark beside it, and its content. A link that named the
 * section by an earlier tab scrolls it into view.
 */
function TabSection({ id, title, about, children }: { id: Section | "updates"; title: string; about: string; children: ReactNode }) {
  return (
    <section id={`delivery-cache-part-${id}`} className="flex scroll-mt-4 flex-col gap-2" data-testid={`delivery-cache-part-${id}`}>
      <h2 className="flex items-center gap-1.5 text-[14px] font-medium">
        {title}
        <RichTooltip title={title} body={about}>
          <span className="inline-flex text-muted-foreground">
            <Info className="size-3.5" aria-label={`What ${title} is`} />
          </span>
        </RichTooltip>
      </h2>
      {children}
    </section>
  );
}

/**
 * The OSDU cache: the reference data, master data and lookup tables every delivered document is built from, one cache per OSDU partition. The
 * header names the partition and the cache flow files that fill it, with Cache files and Refresh for them; a summary row says
 * which version deliveries read, how much it holds, how it is refreshed and whether anything waits for a decision. Below
 * are four tabs, one per question: Records (what the cache holds, browsed a type at a time from a list of the types beside
 * them), History (its versions, and with a type picked that type's own), Deliveries (what the cache means for the records
 * already in OSDU: those a change updates, and those built without a value the cache did not hold) and Setup (the cache
 * flows and types, and the partition's OSDU feature flags), with a searchable type picker in the tab bar for the history
 * (and for the records on a screen too narrow for the list). The partition is the one picked in the title bar, which every page follows: the page has no partition
 * picker of its own. The tab and the type live in the URL, so a link lands on the same view; a link that names a partition
 * (?partition=) or a cache flow (?flow=) makes that partition the title bar's, so it lands on the cache it names.
 */
export default function DeliveryCachePage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const [active, setActive] = useActivePartition();
  const [refreshing, setRefreshing] = useState<DeliveryCacheFlow | null>(null);

  const caches = useQuery({ queryKey: ["delivery", "cache", "caches"], queryFn: () => deliveryApi.caches() });
  const all = caches.data ?? [];
  const byFlow = searchParams.get("flow");
  // A link names the partition (`partition`, or `scope` as older links do), or a cache flow, whose cache is the title
  // bar's partition's when the flow fills it there and else the first partition's it fills. Otherwise the page shows the
  // title bar's partition, and no other: a partition with no cache says so rather than show another's.
  const linkedScope = searchParams.get("partition") ?? searchParams.get("scope");
  const fills = (candidate: DeliveryCache) => byFlow !== null && candidate.flows.some((flow) => flow.name === byFlow);
  const cache = (linkedScope === null ? undefined : all.find((candidate) => candidate.scope === linkedScope))
    ?? (byFlow === null ? undefined : all.find((candidate) => candidate.scope === active && fills(candidate)) ?? all.find(fills))
    ?? all.find((candidate) => candidate.scope === active)
    ?? null;

  const { tab, section } = linkedTab(searchParams.get("tab"));

  const update = (changes: Record<string, string | null>) => setSearchParams((current) => {
    const next = new URLSearchParams(current);
    for (const [key, value] of Object.entries(changes)) {
      if (value === null) {
        next.delete(key);
      } else {
        next.set(key, value);
      }
    }

    return next;
  }, { replace: true });

  // The partition a link named becomes the title bar's, so the title bar says which cache is in view; the link has done
  // its work then, and the title bar decides from there on. A cache kept under a partition the sync could not resolve (a
  // reference) is shown when a link names it, but is no partition to work in.
  const shownScope = cache?.scope ?? null;
  const linked = linkedScope !== null || byFlow !== null;
  useEffect(() => {
    if (!linked || shownScope === null || !isPartitionId(shownScope)) {
      return;
    }

    if (shownScope !== active) {
      setActive(shownScope);
    }

    setSearchParams((current) => {
      const next = new URLSearchParams(current);
      next.delete("partition");
      next.delete("scope");
      next.delete("flow");
      return next;
    }, { replace: true });
  }, [linked, shownScope, active, setActive, setSearchParams]);

  return (
    <Page data-testid="page-delivery-cache">
      <PageHeader
        title="Cache"
        subtitle={cache === null
          ? "The reference data and lookup tables mappings resolve against, one cache per data partition."
          : <CacheSubtitle cache={cache} />}
        actions={cache === null ? undefined : <CacheFlowActions flows={cache.flows} scope={cache.scope} onRefresh={setRefreshing} />}
      />

      {caches.isError && (isApiError(caches.error)
        ? <CorrelationError error={caches.error} />
        : <p className="text-[13px] text-destructive">{String(caches.error)}</p>)}

      {caches.isPending
        ? (
          <>
            <Skeleton className="h-16 w-full rounded-lg" />
            <Skeleton className="h-96 w-full rounded-lg" />
          </>
        )
        : cache === null && all.length > 0
          ? (
            <Card className="gap-0 rounded-lg p-0">
              <EmptyState
                icon={<DatabaseZap />}
                title={active === null ? "No partition is picked in the title bar" : `No cache is kept for ${active}`}
                description={active === null
                  ? "The cache shown is the one of the partition picked in the title bar. Pick a partition there."
                  : `No cache flow fills the cache of ${active}, the partition picked in the title bar. A cache flow fills every partition it names under partitions, the one in its data-partition-id, or when it names neither, every registered partition. Pick another partition in the title bar, or add ${active} to a cache flow and sync its repository.`}
                data-testid="delivery-cache-none-here"
              />
            </Card>
          )
        : cache === null
          ? (
            <Card className="gap-0 rounded-lg p-0">
              <EmptyState
                icon={<DatabaseZap />}
                title="No cache is defined yet"
                description="A cache is filled by cache flows: YAML files in a repository with flowType: cache, listing the OSDU types to cache and the paths of each record to keep, and the dictionaries and ingestion tables to hold as lookup tables. Each fills the cache of every partition it names under partitions, of the one in its data-partition-id, or when it names neither, of every registered partition. Sync the repository and each partition's cache appears here; refresh a flow to capture the first version."
                data-testid="delivery-cache-none"
              />
            </Card>
          )
          : (
            <CacheWorkbench
              key={cache.scope}
              cache={cache}
              tab={tab}
              section={section}
              node={parseSetupNode(searchParams.get("node") ?? (section === "flags" ? "flags" : null))}
              onNode={(next) => update({ node: next === "overview" ? null : next })}
              type={searchParams.get("type")}
              onTab={(next) => update({ tab: next === "records" ? null : next })}
              onType={(name) => update({ type: name })}
              onView={(next, name) => update({ tab: next === "records" ? null : next, type: name })}
              onRefresh={setRefreshing}
            />
          )}

      {refreshing !== null && refreshing.pipelineId !== null && (
        <TriggerRunDialog
          open
          onClose={() => setRefreshing(null)}
          repoId={refreshing.repoId}
          flowName={refreshing.name}
          flowId={refreshing.pipelineId}
          flowKind="cache"
          initialValues={null}
        />
      )}
    </Page>
  );
}

/** How many cache flow files the header names before it counts them instead. */
const NAMED_FILES = 3;

/** A cache flow file as the header names it: its file name, the folders above it being on hover. */
function fileName(path: string): string {
  return path.split("/").at(-1) ?? path;
}

/**
 * The partition and the cache flow files that fill its cache, on one line: each file by name with its path in the
 * repository on hover, or how many when there are more than a few. What a cache flow is, who reads the cache and how to
 * change what it holds are on the info mark rather than spelled out under the title.
 */
function CacheSubtitle({ cache }: { cache: DeliveryCache }) {
  const paths = cache.flows.map((flow) => flow.relativePath);
  return (
    <span className="flex flex-wrap items-baseline gap-x-1.5 gap-y-0.5">
      <span>Partition</span>
      <span className="font-mono text-foreground" data-testid="delivery-cache-name">{cache.scope}</span>
      <span className="-ml-1.5">,</span>
      <span className="inline-flex min-w-0 flex-wrap items-baseline gap-x-1.5" data-testid="delivery-cache-defined-in">
        <span>filled by</span>
        {paths.length <= NAMED_FILES
          ? paths.map((path, index) => (
            <span key={path} className="inline-flex items-baseline">
              <RichTooltip title="Cache flow file" body={path} mono>
                <span className="font-mono text-foreground" data-path={path}>{fileName(path)}</span>
              </RichTooltip>
              {index < paths.length - 1 && <span>,</span>}
            </span>
          ))
          : (
            <RichTooltip title="Cache flow files" body={paths.join("\n")} mono>
              <span className="text-foreground underline decoration-dotted underline-offset-2">{paths.length} cache flows</span>
            </RichTooltip>
          )}
        <RichTooltip
          title="How the cache is filled"
          body={`Cache flows are YAML files in a repository that list the OSDU types, dictionaries and ingestion tables to cache for the partition. Every delivery flow that delivers to ${cache.scope} reads this cache. To change what it holds, edit a cache flow file and sync its repository; Cache files lists them, and Refresh runs one.`}
        >
          <span className="inline-flex self-center text-muted-foreground" data-testid="delivery-cache-about">
            <Info className="size-3.5" aria-label="How the cache is filled" />
          </span>
        </RichTooltip>
      </span>
    </span>
  );
}

/**
 * Where the cache flow files are, as a filter on Pipelines: every cache flow, narrowed to the repository when one repository
 * holds every flow filling the partition. A partition can be filled by several files, so the header points at the list of
 * them rather than at one file; each cache flow on the Setup tab still opens its own file.
 */
function cacheFilesLink(flows: DeliveryCacheFlow[]): string {
  const repos = new Set(flows.map((flow) => flow.repoId));
  const params = new URLSearchParams({ kind: "cache" });
  if (repos.size === 1) {
    params.set("repo", [...repos][0]);
  }

  return `/pipelines?${params.toString()}`;
}

/**
 * The header's actions for the cache flows filling the partition: Cache files opens them as a filter on Pipelines, and
 * Refresh runs one flow's capture (a button when one flow fills the partition, a menu naming the flows when several do,
 * because a refresh captures what one flow declares, not the partition's whole cache). A flow that works in partitions
 * refreshes the partition in view: the dialog opens on it, and says which others the flow builds.
 */
function CacheFlowActions({ flows, scope, onRefresh }: {
  flows: DeliveryCacheFlow[];
  scope: string;
  onRefresh: (flow: DeliveryCacheFlow) => void;
}) {
  const runnable = flows.filter((flow) => flow.pipelineId !== null);
  const files = (
    <Button asChild size="sm" variant="outline" data-testid="delivery-cache-files">
      <RouterLink to={cacheFilesLink(flows)}>
        <ScrollText />
        Cache files
        <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{flows.length}</span>
      </RouterLink>
    </Button>
  );

  if (runnable.length === 0) {
    return files;
  }

  if (runnable.length === 1) {
    const flow = runnable[0];
    return (
      <>
        {files}
        <Button size="sm" onClick={() => onRefresh(flow)} data-testid="delivery-cache-refresh">
          <Play />
          Refresh now
        </Button>
      </>
    );
  }

  return (
    <>
      {files}
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button size="sm" data-testid="delivery-cache-refresh">
            <Play />
            Refresh
            <ChevronDown />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="w-96 max-w-[calc(100vw-2rem)]">
          <DropdownMenuLabel>Refresh what one cache flow captures</DropdownMenuLabel>
          {runnable.map((flow) => (
            <DropdownMenuItem
              key={`${flow.repoId}:${flow.name}`}
              onSelect={() => onRefresh(flow)}
              className="flex-col items-start gap-0.5"
              title={flow.types.join(", ")}
              data-testid={`delivery-cache-refresh-${flow.name}`}
            >
              <span className="flex w-full items-baseline justify-between gap-2">
                <span className="truncate font-mono text-[12px]">{flow.name}</span>
                <span className="shrink-0 text-[11px] tabular-nums text-muted-foreground">
                  {flow.types.length} type{flow.types.length === 1 ? "" : "s"}
                </span>
              </span>
              <span className="line-clamp-2 w-full break-words text-[11px] text-muted-foreground">
                {flow.types.join(", ")}
              </span>
              {(flow.partitions ?? []).length > 1 && (
                <span className="w-full truncate text-[11px] text-muted-foreground">
                  Builds {flow.partitions!.join(", ")}; refreshes {scope}
                </span>
              )}
            </DropdownMenuItem>
          ))}
        </DropdownMenuContent>
      </DropdownMenu>
    </>
  );
}

/** One partition cache's summary and its working tabs. Keyed by the partition, so another partition starts from a clean view. */
function CacheWorkbench({ cache, tab, section, node, onNode, type, onTab, onType, onView, onRefresh }: {
  cache: DeliveryCache;
  tab: Tab;
  /** The section of the tab a link pointed at by an earlier tab's name, scrolled into view; null for none. */
  section: Section | null;
  /** The part of the setup in view. */
  node: SetupNode;
  onNode: (node: SetupNode) => void;
  /** The type in scope from the URL; a name the cache does not hold scopes nothing. */
  type: string | null;
  onTab: (tab: Tab) => void;
  onType: (type: string | null) => void;
  /**
   * Opens a tab with a type in scope (or none), in one change of the URL: two changes made one after the other in the same
   * event each start from the URL as it was, so the second would undo the first.
   */
  onView: (tab: Tab, type: string | null) => void;
  onRefresh: (flow: DeliveryCacheFlow) => void;
}) {
  const versions = useQuery({
    queryKey: ["delivery", "cache", "versions", cache.scope],
    queryFn: () => deliveryApi.cacheVersions(cache.scope),
  });
  const pending = useQuery({
    queryKey: ["delivery", "cache", "tags", "pending-count", cache.scope],
    queryFn: () => deliveryApi.updateTags({ page: 1, pageSize: 1, status: "pending", scope: cache.scope }),
  });
  const pendingTotal = pending.data?.total ?? 0;
  const [comparing, setComparing] = useState<string | null>(null);

  // A link that named a section by an earlier tab lands on the tab holding it, with the section in view.
  useEffect(() => {
    if (section !== null) {
      document.getElementById(`delivery-cache-part-${section}`)?.scrollIntoView({ block: "start" });
    }
  }, [section]);

  const types = summarizeTypes(cache);
  const scoped = type === null ? null : types.find((candidate) => candidate.name === type) ?? null;
  const approvalTypes = cache.types.filter((candidate) => candidate.onChange === "approve").map((candidate) => candidate.name);
  const records = types.reduce((sum, candidate) => sum + candidate.items, 0);
  const heldTypes = types.filter((candidate) => candidate.items > 0).length;
  const current = cache.current;
  const schedules = cache.flows.flatMap((flow) => flow.schedules);
  const typeOptions: FilterOption[] = types.map((candidate) => ({
    value: candidate.name,
    label: candidate.name,
    hint: typeHint(candidate),
  }));

  const changesCell: SummaryCell = approvalTypes.length === 0 && pendingTotal === 0
    ? {
      label: "Changes",
      value: "automatic",
      caption: "each flow carries a change on its next run",
      onClick: () => onTab("deliveries"),
      testId: "delivery-cache-approval",
    }
    : {
      label: "Waiting for approval",
      value: pendingTotal.toLocaleString(),
      tone: pendingTotal > 0 ? "warning" : undefined,
      caption: `${approvalTypes.length} of ${types.length} type${types.length === 1 ? "" : "s"} ask for approval`,
      onClick: () => onTab("deliveries"),
      testId: "delivery-cache-approval",
    };

  return (
    <>
      <SummaryStrip
        data-testid="delivery-cache-summary"
        cells={[
          {
            label: "Current version",
            value: current === null ? "none yet" : current.version,
            caption: current === null
              ? "refresh a cache flow to capture one"
              : <>written by {current.flow} <RelativeTime value={current.capturedUtc} absolute={false} /> for {current.capturedBy}</>,
            onClick: () => onTab("history"),
            testId: "delivery-cache-current",
          },
          {
            label: "Records",
            value: records.toLocaleString(),
            caption: heldTypes === types.length
              ? `in ${types.length} type${types.length === 1 ? "" : "s"}`
              : `in ${heldTypes} of the ${types.length} types declared; the others hold nothing yet`,
            onClick: () => {
              onView("records", null);
            },
            testId: "delivery-cache-records",
          },
          {
            label: "Refreshed",
            value: schedules.length === 0 ? "on demand" : "on schedule",
            caption: cache.flows.length > 1
              ? `by ${cache.flows.length} cache flows`
              : schedules.length === 0 ? "no schedule; use Refresh now" : schedules.map(scheduleCadence).join("; "),
            onClick: () => onTab("setup"),
            testId: "delivery-cache-schedules",
          },
          changesCell,
        ]}
      />

      {pendingTotal > 0 && (
        <Alert className="border-warning/40 bg-warning/8" data-testid="delivery-cache-pending-banner">
          <ShieldAlert className="text-warning" />
          <AlertTitle>
            {pendingTotal.toLocaleString()} change{pendingTotal === 1 ? " waits" : "s wait"} for your approval
          </AlertTitle>
          <AlertDescription className="flex flex-wrap items-center gap-x-3 gap-y-1">
            <span>The delivered records built from these values are held back until each change is approved or rejected.</span>
            <Button size="xs" variant="outline" onClick={() => onTab("deliveries")} data-testid="delivery-cache-review-changes">
              Review changes
            </Button>
          </AlertDescription>
        </Alert>
      )}

      <Tabs value={tab} onValueChange={(value) => onTab(value as Tab)} className="min-w-0 gap-3">
        <div className="flex flex-wrap items-center justify-between gap-2 border-b border-border">
          <TabsList variant="line" data-testid="delivery-cache-tabs">
            <TabsTrigger value="records" data-testid="delivery-cache-tab-records">
              <TabName tab="records">{scoped === null ? "Records" : `${scoped.name} records`}</TabName>
            </TabsTrigger>
            {/* A tab is named for the question it answers and carries no count: what waits for a decision is said by the
                summary and the banner above, and every tab says how much it holds once it is open. */}
            <TabsTrigger value="history" data-testid="delivery-cache-tab-history"><TabName tab="history">History</TabName></TabsTrigger>
            <TabsTrigger value="deliveries" data-testid="delivery-cache-tab-deliveries"><TabName tab="deliveries">Deliveries</TabName></TabsTrigger>
            <TabsTrigger value="setup" data-testid="delivery-cache-tab-setup"><TabName tab="setup">Setup</TabName></TabsTrigger>
          </TabsList>
          {/* The type narrows the records and the history; what the cache means for deliveries, and how it is set up, always
              cover the whole cache, and name each row's type. The records pick it from the list beside them, which gives
              way to this picker on a narrow screen. */}
          {(tab === "records" || tab === "history") && (
            <FilterCombobox
              options={typeOptions}
              value={scoped?.name ?? ""}
              onChange={(value) => onType(value === "" ? null : value)}
              placeholder="All types"
              searchPlaceholder="Type, family or captured name"
              emptyText="No cached type matches."
              ariaLabel="Cached type"
              testId="delivery-cache-type"
              className={cn("mb-1 w-60", tab === "records" && "lg:hidden")}
            />
          )}
        </div>

        <TabsContent value="records">
          <DeliveryCacheRecords
            scope={cache.scope}
            types={types}
            type={scoped?.name ?? null}
            onType={onType}
            versions={versions.data ?? []}
            onCompare={setComparing}
          />
          {comparing !== null && current !== null && (
            <CacheCompareDialog
              scope={cache.scope}
              from={comparing}
              to={current.version}
              type={scoped?.name ?? null}
              onClose={() => setComparing(null)}
            />
          )}
        </TabsContent>

        <TabsContent value="history">
          <DeliveryCacheHistory
            scope={cache.scope}
            type={scoped?.name ?? null}
            retentionDays={cache.retentionDays ?? 7}
            versionsKept={cache.versionsKept ?? DEFAULT_VERSIONS_KEPT}
          />
        </TabsContent>

        <TabsContent value="deliveries">
          <TabSection
            id="updates"
            title="Updated by the cache"
            about="Records already in OSDU that the cache has changed or will change. Missing from cache: records built without something the cache did not hold when they were rendered (a wellbore loaded after its logs, an access group not listed yet for a field, a reference written unverified); nothing needs doing, since the refresh that brings it lists them as a change. The other states are those changes: a type set to onChange: auto, the default, is approved as found and each record is updated on its flow's next delivery; a type set to onChange: approve waits for someone to approve or reject the change. A change is rolling out until every flow that read the value has rendered its records again, since several flows can read one cache, and rolled out once the last of them has. Fix the source when something should not be missing at all."
          >
            <DeliveryCacheApprovals
              scope={cache.scope}
              approvalTypes={approvalTypes}
              pendingTotal={pending.data?.total}
              openMissing={section === "gaps"}
            />
          </TabSection>
        </TabsContent>

        <TabsContent value="setup">
          <DeliveryCacheSetup
            cache={cache}
            version={current}
            versions={versions.data ?? []}
            node={node}
            onNode={onNode}
            onRefresh={onRefresh}
            onOpen={(name, next) => onView(next, name)}
          />
        </TabsContent>
      </Tabs>
    </>
  );
}
