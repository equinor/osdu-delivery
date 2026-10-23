import { useMemo, useState } from "react";
import { Link as RouterLink, useSearchParams } from "react-router-dom";
import { keepPreviousData, useMutation, useQuery } from "@tanstack/react-query";
import { CircleCheck, GitPullRequest, Loader2, OctagonAlert, PencilRuler, Plus, RotateCcw, TriangleAlert, X } from "lucide-react";
import { toast } from "sonner";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import {
  deliveryApi, type DeliveryBuilderFlow, type DeliveryMappingComposeResult, type DeliveryTemplateVariable, type MappingDraft,
  type MappingDraftEntry, type MappingDraftIssue, type MappingDraftLookup,
} from "../../api/delivery";
import { useAuth } from "@/auth/AuthContext";
import { CodeView } from "@/components/CodeView";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { CopyButton } from "@/components/CopyButton";
import { EmptyState } from "@/components/EmptyState";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { RichTooltip } from "@/components/RichTooltip";
import { OutcomePill } from "@/components/StatusBadge";
import { useActivePartition } from "./activePartition";
import { MappingBuilderLookups, type LookupEditing } from "./MappingBuilderLookups";
import { MappingBuilderVariables } from "./MappingBuilderVariables";
import { MappingEntryEditor, type EntryEditorTarget } from "./MappingEntryEditor";
import { MappingProposeSheet } from "./MappingProposeSheet";
import { bareColumn, COLUMN_NAME, emptyEntry, LOOKUP_TARGET_PREFIX, putEntry, renameLookup } from "./mappingDraft";
import { entityName } from "./templateFormat";
import { problemText } from "./problemText";
import { ProblemView } from "./TemplateSheet";
import { variableRows } from "./variableRows";

/** How long the draft has to stay unchanged before it is written and checked again. */
const COMPOSE_DELAY_MS = 500;

/** What a compose request carries, kept as one JSON text so an unchanged draft is recognised and not checked again. */
interface ComposeRequest {
  scope: string | null;
  draft: MappingDraft;
  parameters: Record<string, string>;
  /** The repository the mapping is written for, and its path there, whose template folders settle its templates. */
  repoId: string | null;
  path: string | null;
}

/**
 * The folder a new mapping goes in for the template folder its template is in: the mappings folder beside it, in the same
 * project (welldb/templates gives welldb/mappings, a top templates folder gives mappings), where the mapping reads that
 * template folder.
 */
function mappingsFolderFor(templateFolder: string): string {
  const at = templateFolder.lastIndexOf("templates");
  const project = at < 0 ? "" : templateFolder.slice(0, at);
  return `${project}mappings`;
}

/** The folder a path is in, or "" for a path at the top. */
function folderOf(path: string): string {
  const at = path.lastIndexOf("/");
  return at < 0 ? "" : path.slice(0, at);
}

/**
 * Where a check value was prefilled from: the reference the flow's run reads it from, or, when the control plane cannot
 * resolve that reference, which one it is and that the check needs a value in its place. A flow that works in partitions
 * supplies the partition itself, so its dataPartition is said to be that partition. Nothing is said for a value the flow
 * writes out, or once the author has typed their own.
 */
function CheckValueSource({ flow, parameter, edited }: { flow: DeliveryBuilderFlow | null; parameter: string; edited: boolean }) {
  if (flow !== null && !edited && parameter === "dataPartition" && flow.partition) {
    return (
      <p className="text-xs text-muted-foreground" data-testid={`mapping-builder-check-source-${parameter}`}>
        The partition <span className="font-mono">{flow.partition}</span>, one of the partitions the flow names, as a run
        delivering there mints its ids in.
      </p>
    );
  }

  const reference = flow?.parameterReferences[parameter];
  if (flow === null || reference === undefined || edited) {
    return null;
  }

  return flow.parameters[parameter] !== undefined
    ? (
        <p className="text-xs text-muted-foreground" data-testid={`mapping-builder-check-source-${parameter}`}>
          Read from <span className="font-mono">{reference}</span>, as a run of the flow reads it.
        </p>
      )
    : (
        <p className="text-xs text-warning" data-testid={`mapping-builder-check-unresolved-${parameter}`}>
          The flow reads this from <span className="font-mono">{reference}</span>, which a run resolves on its node and the
          control plane cannot. Give a value to check with.
        </p>
      );
}

/**
 * The mapping builder: pick the repository the mapping lives in and one of the templates its template folders hold, and fill
 * the template's variables from the dataset, the cache of the partition picked in the title bar and static values. The
 * page writes the mapping as YAML and checks it, at the path it would have in the repository, against the template read
 * from its file and the cache's current version as it changes. An existing mapping opens with its entries filled in, at
 * its own path, reading the cache of the partition its repository's delivery flow delivers to.
 */
export default function MappingBuilderPage() {
  const { hasScope } = useAuth();
  const canAuthor = hasScope("author");
  const [searchParams, setSearchParams] = useSearchParams();
  const mappingId = searchParams.get("mappingId");

  const [repoId, setRepoId] = useState("");
  // The cache the builder reads is the one of the partition picked in the title bar, as every page's is.
  const [active] = useActivePartition();
  const [templateChoice, setTemplateChoice] = useState("");
  const [name, setName] = useState("");
  const [mappingVersion, setMappingVersion] = useState("1.0.0");
  const [system, setSystem] = useState("");
  const [description, setDescription] = useState("");
  const [draft, setDraft] = useState<MappingDraft | null>(null);
  const [checkEdits, setCheckEdits] = useState<Record<string, string>>({});
  const [keyInput, setKeyInput] = useState("");
  const [editing, setEditing] = useState<EntryEditorTarget | null>(null);
  const [lookupEditing, setLookupEditing] = useState<LookupEditing | null>(null);
  const [session, setSession] = useState(0);
  const [proposeOpen, setProposeOpen] = useState(false);
  const [startOverOpen, setStartOverOpen] = useState(false);
  const [loadedFrom, setLoadedFrom] = useState<string | null>(null);

  const repos = useQuery({ queryKey: ["delivery", "mapping-builder", "repos"], queryFn: deliveryApi.builderRepos });
  const caches = useQuery({ queryKey: ["delivery", "mapping-builder", "caches"], queryFn: deliveryApi.builderCaches });
  const templates = useQuery({ queryKey: ["delivery", "templates", "list"], queryFn: deliveryApi.templates });
  const mappingDetail = useQuery({
    queryKey: ["delivery", "mapping", mappingId],
    queryFn: () => deliveryApi.mapping(mappingId!),
    enabled: mappingId !== null,
  });
  const opening = mappingDetail.data;
  const parsed = useQuery({
    queryKey: ["delivery", "mapping-builder", "parse", mappingId, opening?.mapping.contentHash ?? null],
    queryFn: () => deliveryApi.parseMapping(opening!.yaml, opening!.mapping.relativePath, opening!.mapping.repoId),
    enabled: mappingId !== null && opening !== undefined,
    staleTime: Infinity,
  });

  // An existing mapping opens once per link: its draft, its repository and its header fill the page. Leaving the link
  // lets the same mapping open again later.
  const openedDraft = parsed.data?.draft ?? null;
  if (mappingId === null && loadedFrom !== null) {
    setLoadedFrom(null);
  }

  if (mappingId !== null && loadedFrom !== mappingId && openedDraft !== null && opening !== undefined) {
    setLoadedFrom(mappingId);
    setDraft(openedDraft);
    setRepoId(opening.mapping.repoId);
    setTemplateChoice(opening.mapping.templatePath);
    setName(openedDraft.name);
    setMappingVersion(openedDraft.version);
    setSystem(openedDraft.system);
    setDescription(openedDraft.description ?? "");
    setCheckEdits({});
    setEditing(null);
    setLookupEditing(null);
  }

  const repo = (repos.data ?? []).find((candidate) => candidate.repoId === repoId) ?? null;
  // The templates the chosen repository's folders hold, each a file; the one the draft fills is named by its path.
  const repoTemplates = useMemo(
    () => (templates.data ?? []).filter((template) => template.repoId === repoId && template.kind !== null && template.version !== null),
    [templates.data, repoId],
  );
  const templatePath = templateChoice !== ""
    ? templateChoice
    : draft === null
      ? ""
      : repoTemplates.find((template) => template.kind === draft.templateKind && template.version === draft.templateVersion)?.path ?? "";
  const chosenTemplate = repoTemplates.find((template) => template.path === templatePath) ?? null;
  const opened = loadedFrom !== null && opening !== undefined ? opening.mapping : null;

  const reference = `${name.trim()}@${mappingVersion.trim()}`;
  // Where the mapping is, or goes: beside the one it was opened from, or in the mappings folder of the template's project.
  const mappingFolder = opened !== null ? folderOf(opened.relativePath) : chosenTemplate !== null ? mappingsFolderFor(chosenTemplate.folder) : null;
  const mappingPath = mappingFolder === null ? null : `${mappingFolder === "" ? "" : `${mappingFolder}/`}${reference}.yaml`;
  // A flow that works in partitions is offered once per partition, each with that partition's values; the check takes the
  // one for the title bar's partition, so the values it renders with and the cache it reads agree.
  const usingMapping = repo === null ? [] : repo.flows.filter((flow) => flow.mapping === reference);
  const checkFlow = repo === null
    ? null
    : usingMapping.find((flow) => active !== null && flow.cacheScope === active)
      ?? repo.flows.find((flow) => active !== null && flow.cacheScope === active)
      ?? usingMapping[0]
      ?? repo.flows[0]
      ?? null;
  const checkFlowName = checkFlow === null ? "" : `${checkFlow.name}${checkFlow.partition ? ` in partition ${checkFlow.partition}` : ""}`;
  const cache = active === null ? null : (caches.data ?? []).find((candidate) => candidate.scope === active) ?? null;

  // The template is read from the repository's file, so the variables are what the file holds.
  const detail = useQuery({
    queryKey: ["delivery", "templates", "detail", repoId, templatePath, cache?.scope ?? null],
    queryFn: () => deliveryApi.templateDetail(repoId, templatePath, cache?.scope),
    enabled: draft !== null && repoId !== "" && templatePath !== "",
    // A cache change keeps the variables in view while their cached types load; another template never does.
    placeholderData: (previous, previousQuery) => (previousQuery !== undefined
      && previousQuery.queryKey[3] === repoId
      && previousQuery.queryKey[4] === templatePath
      ? previous
      : undefined),
  });

  const rows = useMemo(
    () => (detail.data !== undefined && draft !== null ? variableRows(detail.data, draft) : []),
    [detail.data, draft],
  );
  const order = useMemo(
    () => new Map((detail.data?.variables ?? []).map((variable, index) => [variable.path, index])),
    [detail.data],
  );

  const composedDraft = useMemo<MappingDraft | null>(() => (draft === null ? null : {
    ...draft,
    name: name.trim(),
    version: mappingVersion.trim(),
    system: system.trim(),
    description: description.trim() === "" ? null : description.trim(),
  }), [draft, name, mappingVersion, system, description]);

  const checkValues = useMemo(() => {
    const values: Record<string, string> = {};
    for (const parameter of draft?.parameters ?? []) {
      const value = checkEdits[parameter.name] ?? checkFlow?.parameters[parameter.name] ?? "";
      if (value.trim() !== "") {
        values[parameter.name] = value.trim();
      }
    }

    return values;
  }, [draft, checkEdits, checkFlow]);

  const composeKey = composedDraft === null
    ? null
    : JSON.stringify({
      scope: cache?.scope ?? null,
      draft: composedDraft,
      parameters: checkValues,
      repoId: repoId === "" ? null : repoId,
      path: repoId === "" ? null : mappingPath,
    } satisfies ComposeRequest);
  const settledKey = useDebouncedValue(composeKey, COMPOSE_DELAY_MS);
  const compose = useQuery({
    queryKey: ["delivery", "mapping-builder", "compose", settledKey],
    queryFn: () => {
      const request = JSON.parse(settledKey!) as ComposeRequest;
      return deliveryApi.composeMapping(
        request.scope, request.draft, Object.keys(request.parameters).length > 0 ? request.parameters : null, request.repoId, request.path,
      );
    },
    enabled: settledKey !== null,
    placeholderData: keepPreviousData,
    gcTime: 60_000,
  });
  const composing = composeKey !== null && (composeKey !== settledKey || compose.isFetching);
  const result: DeliveryMappingComposeResult | undefined = composeKey === null ? undefined : compose.data;

  const issues = useMemo(
    () => [...(result?.issues ?? [])].sort((a, b) => (a.severity === b.severity ? 0 : a.severity === "error" ? -1 : 1)),
    [result],
  );
  const errorCount = issues.filter((issue) => issue.severity === "error").length;
  const warningCount = issues.length - errorCount;

  const start = useMutation({
    mutationFn: () => {
      if (repoId === "" || chosenTemplate === null) {
        throw new Error("Pick the repository and the template of its template folders the mapping fills.");
      }

      return deliveryApi.draftMapping({
        scope: cache?.scope ?? null,
        repoId,
        templatePath: chosenTemplate.path,
        name: name.trim(),
        mappingVersion: mappingVersion.trim(),
        system: system.trim(),
      });
    },
    onSuccess: (started) => {
      setDraft(started);
      setName(started.name);
      setMappingVersion(started.version);
      setSystem(started.system);
      setCheckEdits({});
      const prefilled = started.entries.filter((entry) => entry.prefilled).length;
      toast.success(prefilled === 0
        ? `Started ${started.name}@${started.version}. The cache prefilled no entries.`
        : `Started ${started.name}@${started.version}, with ${prefilled} ${prefilled === 1 ? "entry" : "entries"} prefilled from the cache.`);
    },
    onError: (error) => toast.error(problemText(error)),
  });

  const openEditor = (target: Omit<EntryEditorTarget, "session">) => {
    const next = session + 1;
    setSession(next);
    setEditing({ ...target, session: next });
  };

  const saveEntry = (target: string, entry: MappingDraftEntry | null) => {
    setDraft((current) => (current === null ? current : {
      ...current,
      entries: entry === null
        ? current.entries.filter((candidate) => candidate.target !== target)
        : putEntry(current.entries, entry, order),
    }));
    setEditing(null);
  };

  const fillFromCache = (variable: DeliveryTemplateVariable) => {
    if (draft === null || variable.cacheTypes.length === 0) {
      return;
    }

    const typeName = variable.cacheTypes[0];
    const cached = cache?.types.find((type) => type.name === typeName);
    const entry: MappingDraftEntry = {
      ...emptyEntry(variable.path, "Cache"),
      cacheType: typeName,
      cacheField: "id",
      findBy: [{ field: cached?.fields[0] ?? "id", column: "", literal: null }],
    };
    setDraft({ ...draft, entries: putEntry(draft.entries, entry, order) });
    // The entry still needs the dataset column its record is found by, so it opens for that.
    openEditor({ variable, entry, keyHolder: null, outside: null });
  };

  const openLookup = (lookup: MappingDraftLookup | null) => {
    const next = session + 1;
    setSession(next);
    setLookupEditing({ lookup, session: next });
  };

  // A renamed lookup keeps its readers: every entry reading the old name reads the new one.
  const saveLookup = (previous: string | null, lookup: MappingDraftLookup) => {
    setDraft((current) => (current === null ? current : {
      ...current,
      lookups: previous === null ? [...current.lookups, lookup] : current.lookups.map((candidate) => (candidate.name === previous ? lookup : candidate)),
      entries: previous !== null && previous !== lookup.name ? renameLookup(current.entries, previous, lookup.name) : current.entries,
    }));
    setLookupEditing(null);
  };

  const removeLookup = (name: string) => {
    setDraft((current) => (current === null ? current : { ...current, lookups: current.lookups.filter((candidate) => candidate.name !== name) }));
  };

  /** The lookup an issue is about, when it is about one the draft declares. */
  const lookupOf = (issue: MappingDraftIssue) => (issue.target !== null && issue.target.startsWith(LOOKUP_TARGET_PREFIX)
    ? draft?.lookups.find((candidate) => candidate.name === issue.target!.slice(LOOKUP_TARGET_PREFIX.length))
    : undefined);

  const openIssue = (issue: MappingDraftIssue) => {
    const lookup = lookupOf(issue);
    if (lookup !== undefined) {
      openLookup(lookup);
      return;
    }

    const row = issue.target === null ? undefined : rows.find((candidate) => candidate.key === issue.target);
    if (row !== undefined) {
      openEditor({ variable: row.variable, entry: row.entry, keyHolder: null, outside: row.outside });
    }
  };

  const keyColumn = bareColumn(keyInput);
  const keyProblem = keyInput.trim() === ""
    ? null
    : !COLUMN_NAME.test(keyColumn)
      ? "A column name holds letters, digits, underscores and hyphens."
      : draft !== null && draft.key.includes(keyColumn) ? `${keyColumn} is already part of the key.` : null;

  const addKey = () => {
    if (draft === null || keyInput.trim() === "" || keyProblem !== null) {
      return;
    }

    setDraft({ ...draft, key: [...draft.key, keyColumn] });
    setKeyInput("");
  };

  const startOver = () => {
    setDraft(null);
    setEditing(null);
    setLookupEditing(null);
    setCheckEdits({});
    setStartOverOpen(false);
    if (mappingId !== null) {
      setSearchParams((current) => {
        const next = new URLSearchParams(current);
        next.delete("mappingId");
        return next;
      }, { replace: true });
    }
  };

  const proposeBlocker = repo === null
    ? "Pick the repository the mapping lives in."
    : repo.sourceId === null
      ? `${repo.name} is not tracked from a git source, so there is nowhere to open a pull request. Copy the YAML instead.`
      : composing
        ? "Wait for the check of the latest changes."
        : result === undefined
          ? "Wait for the first check."
          : !result.valid ? "Fix the errors the check found first." : null;

  const canStart = repo !== null && chosenTemplate !== null && name.trim() !== "" && mappingVersion.trim() !== ""
    && system.trim() !== "" && !start.isPending;

  return (
    <Page data-testid="page-delivery-mapping-builder">
      <PageHeader
        title="Mapping builder"
        subtitle="Fill one of a repository's templates from the dataset, the cache and static values. The YAML is checked where it would be in the repository, against the template's file and the cache's current version, as you go."
        actions={draft !== null ? (
          <>
            {result !== undefined && <CopyButton label="Copy YAML" text={result.yaml} testId="mapping-builder-copy-yaml" />}
            {canAuthor && (proposeBlocker === null ? (
              <Button size="sm" onClick={() => setProposeOpen(true)} data-testid="mapping-builder-propose">
                <GitPullRequest />
                Propose to repository
              </Button>
            ) : (
              <RichTooltip body={proposeBlocker} title="Cannot propose yet" delayDuration={100}>
                <span tabIndex={0} className="inline-flex rounded-md" data-testid="mapping-builder-propose-blocked">
                  <Button size="sm" disabled data-testid="mapping-builder-propose">
                    <GitPullRequest />
                    Propose to repository
                  </Button>
                </span>
              </RichTooltip>
            ))}
            <Button variant="ghost" size="sm" onClick={() => setStartOverOpen(true)} data-testid="mapping-builder-start-over">
              <RotateCcw />
              Start over
            </Button>
          </>
        ) : undefined}
      />

      {mappingId !== null && draft === null && (
        mappingDetail.isError ? <ProblemView error={mappingDetail.error} testId="mapping-builder-open-error" />
          : parsed.isError ? <ProblemView error={parsed.error} testId="mapping-builder-open-error" />
            : parsed.data !== undefined && parsed.data.draft === null ? (
              <Alert variant="destructive" data-testid="mapping-builder-open-error">
                <OctagonAlert />
                <AlertTitle>This mapping cannot be opened in the builder</AlertTitle>
                <AlertDescription>
                  <ul className="flex list-disc flex-col gap-0.5 pl-4">
                    {parsed.data.issues.map((issue, index) => <li key={`${index}-${issue.message}`}>{issue.message}</li>)}
                  </ul>
                  <p>Fix the document in its repository, or start a new mapping below.</p>
                </AlertDescription>
              </Alert>
            ) : (
              <div className="flex items-center gap-2 text-[13px] text-muted-foreground" data-testid="mapping-builder-opening">
                <Loader2 className="size-4 animate-spin" />
                Opening {opening?.mapping.reference ?? "the mapping"}
              </div>
            )
      )}

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
        <div className="flex min-w-0 flex-col gap-4">
          <Card className="gap-3 rounded-lg p-4" data-testid="mapping-builder-setup">
            <div className="flex flex-wrap items-baseline gap-2">
              <h2 className="text-[13px] font-medium">Setup</h2>
              {loadedFrom !== null && opening !== undefined && (
                <span className="text-xs text-muted-foreground" data-testid="mapping-builder-opened-from">
                  Opened from <span className="font-mono">{opening.mapping.relativePath}</span> as the last sync found it.
                </span>
              )}
            </div>
            {repos.isError && <ProblemView error={repos.error} testId="mapping-builder-repos-error" />}
            {caches.isError && <ProblemView error={caches.error} testId="mapping-builder-caches-error" />}
            {templates.isError && <ProblemView error={templates.error} testId="mapping-builder-templates-error" />}
            <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="mapping-builder-repo">Repository</Label>
                <Select
                  value={repoId}
                  disabled={draft !== null}
                  onValueChange={(next) => {
                    // Another repository holds other templates, so the template is picked again from its folders.
                    setRepoId(next);
                    setTemplateChoice("");
                  }}
                >
                  <SelectTrigger id="mapping-builder-repo" size="sm" className="h-8 w-full" data-testid="mapping-builder-repo">
                    <SelectValue placeholder={repos.data === undefined ? "Loading the repositories" : "Pick a repository"} />
                  </SelectTrigger>
                  <SelectContent>
                    {(repos.data ?? []).map((candidate) => (
                      <SelectItem key={candidate.repoId} value={candidate.repoId}>
                        {candidate.name}
                        {candidate.sourceId === null && <span className="text-[11px] text-muted-foreground">not tracked from git</span>}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <p className="text-xs text-muted-foreground">
                  {repo === null
                    ? "The repository the mapping lives in: its template folders hold the template it fills, and its delivery flow supplies the parameters the check renders with."
                    : checkFlow === null
                      ? `${repo.name} has no delivery flow, so the check renders without flow parameters.`
                      : `The check renders with the parameters of ${checkFlowName}.`}
                </p>
              </div>
              <div className="flex flex-col gap-1.5">
                <span className="text-[13px] font-medium">Partition cache</span>
                <span className="font-mono text-[13px]" data-testid="mapping-builder-cache">
                  {active ?? "none picked in the title bar"}
                </span>
                <p className="text-xs text-muted-foreground" data-testid="mapping-builder-cache-note">
                  {cache === null
                    ? active === null
                      ? "Pick a partition in the title bar: the builder reads its cache. Without one nothing is prefilled, and cache entries are not checked."
                      : caches.data === undefined
                        ? "Reading the caches."
                        : `No cache is kept for ${active}, the partition picked in the title bar, so nothing is prefilled from it and cache entries are not checked.`
                    : cache.currentVersion === null
                      ? `The cache of ${cache.scope} has no version yet, so nothing is prefilled from it. Refresh one of its cache flows on the Cache page.`
                      : `The cache of ${cache.scope} holds ${cache.types.length} type${cache.types.length === 1 ? "" : "s"} at version ${cache.currentVersion}.`}
                </p>
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="mapping-builder-template">Template</Label>
                <Select
                  value={templatePath}
                  disabled={draft !== null || repo === null}
                  onValueChange={(next) => {
                    setTemplateChoice(next);
                    const chosen = repoTemplates.find((template) => template.path === next);
                    if (chosen !== undefined && chosen.kind !== null && name.trim() === "") {
                      setName(entityName(chosen.kind));
                    }
                  }}
                >
                  <SelectTrigger id="mapping-builder-template" size="sm" className="h-8 w-full" data-testid="mapping-builder-template">
                    <SelectValue placeholder={repo === null
                      ? "Pick the repository first"
                      : templates.data === undefined ? "Loading the repository's templates" : "Pick a template of the repository"} />
                  </SelectTrigger>
                  <SelectContent>
                    {repoTemplates.map((template) => (
                      <SelectItem key={template.path} value={template.path}>
                        <span className="font-mono text-[12px]">{template.kind}</span>
                        <span className="font-mono text-[11px] text-muted-foreground">{template.path}</span>
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <p className="text-xs text-muted-foreground" data-testid="mapping-builder-template-note">
                  {draft !== null
                    ? mappingPath === null ? "Start over to fill another template." : <>Start over to fill another template. The mapping is <span className="font-mono">{mappingPath}</span>.</>
                    : repo !== null && templates.data !== undefined && repoTemplates.length === 0
                      ? <>{repo.name} holds no template in a folder named templates yet. Download one from Browse OSDU on the <RouterLink to="/delivery/templates" className="text-primary hover:underline">Templates page</RouterLink>, commit it to the repository beside the documents that fill it, and sync it.</>
                      : <>A kind the repository holds no template of is downloaded from Browse OSDU on the <RouterLink to="/delivery/templates" className="text-primary hover:underline">Templates page</RouterLink>, committed to a template folder, and synced.</>}
                </p>
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="mapping-builder-name">Name</Label>
                <Input
                  id="mapping-builder-name"
                  className="h-8 font-mono"
                  placeholder="WellLog"
                  value={name}
                  onChange={(event) => setName(event.target.value)}
                  data-testid="mapping-builder-name"
                />
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="mapping-builder-version">Mapping version</Label>
                <Input
                  id="mapping-builder-version"
                  className="h-8 font-mono"
                  placeholder="1.0.0"
                  value={mappingVersion}
                  onChange={(event) => setMappingVersion(event.target.value)}
                  data-testid="mapping-builder-version"
                />
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="mapping-builder-system">Source system</Label>
                <Input
                  id="mapping-builder-system"
                  className="h-8 font-mono"
                  placeholder="wells"
                  value={system}
                  onChange={(event) => setSystem(event.target.value)}
                  data-testid="mapping-builder-system"
                />
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="mapping-builder-description">Description</Label>
                <Input
                  id="mapping-builder-description"
                  className="h-8"
                  placeholder="What the mapping delivers (optional)"
                  value={description}
                  onChange={(event) => setDescription(event.target.value)}
                  data-testid="mapping-builder-description"
                />
              </div>
            </div>
            <p className="text-xs text-muted-foreground">
              The file is <span className="font-mono">mappings/{reference}.yaml</span>, and a flow pins it as {reference}. The
              source system is part of every record&apos;s key.
            </p>
            {start.isError && <ProblemView error={start.error} testId="mapping-builder-start-error" />}
            {draft === null && (
              <div className="flex justify-end">
                <Button size="sm" onClick={() => start.mutate()} disabled={!canStart} data-testid="mapping-builder-start">
                  {start.isPending ? <Loader2 className="animate-spin" /> : <PencilRuler />}
                  Start mapping
                </Button>
              </div>
            )}
          </Card>

          {draft !== null && (
            <Card className="gap-3 rounded-lg p-4" data-testid="mapping-builder-dataset">
              <h2 className="text-[13px] font-medium">Dataset</h2>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="mapping-builder-key-input">Key columns</Label>
                <div className="flex flex-wrap items-center gap-1.5" data-testid="mapping-builder-key">
                  {draft.key.map((column) => (
                    <Badge key={column} variant="secondary" className="gap-1 font-mono text-[12px]" data-testid={`mapping-builder-key-chip-${column}`}>
                      {column}
                      <button
                        type="button"
                        aria-label={`Remove ${column} from the key`}
                        className="rounded-sm text-muted-foreground hover:text-destructive"
                        onClick={() => setDraft({ ...draft, key: draft.key.filter((candidate) => candidate !== column) })}
                        data-testid={`mapping-builder-key-remove-${column}`}
                      >
                        <X className="size-3" />
                      </button>
                    </Badge>
                  ))}
                  <Input
                    id="mapping-builder-key-input"
                    className="h-7 w-44 font-mono text-[12px]"
                    placeholder="column"
                    value={keyInput}
                    onChange={(event) => setKeyInput(event.target.value)}
                    onKeyDown={(event) => {
                      if (event.key === "Enter") {
                        event.preventDefault();
                        addKey();
                      }
                    }}
                    aria-invalid={keyProblem !== null}
                    data-testid="mapping-builder-key-input"
                  />
                  <Button
                    variant="outline"
                    size="xs"
                    onClick={addKey}
                    disabled={keyInput.trim() === "" || keyProblem !== null}
                    data-testid="mapping-builder-key-add"
                  >
                    <Plus />
                    Add
                  </Button>
                </div>
                {keyProblem !== null
                  ? <p className="text-xs text-destructive" data-testid="mapping-builder-key-error">{keyProblem}</p>
                  : (
                    <p className="text-xs text-muted-foreground">
                      The columns that identify a record, in order. The delivery key, and so the OSDU id, is derived from them.
                    </p>
                  )}
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="mapping-builder-id-from">OSDU id</Label>
                <Select
                  value={draft.idFrom === "key" ? "key" : "deliveryKey"}
                  onValueChange={(value) => {
                    setDraft((current) => (current === null ? current : { ...current, idFrom: value === "key" ? "key" : null }));
                  }}
                >
                  <SelectTrigger id="mapping-builder-id-from" size="sm" className="h-8 w-full md:w-72" data-testid="mapping-builder-id-from">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="deliveryKey">Made from the delivery key</SelectItem>
                    <SelectItem value="key">Made from the key&apos;s values</SelectItem>
                  </SelectContent>
                </Select>
                <p className="text-xs text-muted-foreground">
                  The last part of every record&apos;s OSDU id: a GUID derived from the key, or the key&apos;s values themselves, so the id
                  reads as the code it stands for (<span className="font-mono">WELLDB::GAPI</span>). A record keeps the id it was first
                  delivered under, so choose before anything is delivered.
                </p>
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="mapping-builder-label">Label</Label>
                <Input
                  id="mapping-builder-label"
                  className="h-8 font-mono"
                  placeholder="{name} ({id})"
                  value={draft.label ?? ""}
                  onChange={(event) => {
                    const value = event.target.value;
                    setDraft((current) => (current === null ? current : { ...current, label: value === "" ? null : value }));
                  }}
                  data-testid="mapping-builder-label"
                />
                <p className="text-xs text-muted-foreground">
                  Display text for the ledger and the GUI, with {"{column}"} tokens naming columns of the dataset's own row. It never enters the record.
                </p>
              </div>
            </Card>
          )}

          {draft !== null && (
            <MappingBuilderLookups
              draft={draft}
              cacheTypes={cache?.types ?? []}
              issues={issues}
              editing={lookupEditing}
              onOpen={openLookup}
              onClose={() => setLookupEditing(null)}
              onSave={saveLookup}
              onRemove={removeLookup}
            />
          )}

          {draft !== null && draft.parameters.length > 0 && (
            <Card className="gap-3 rounded-lg p-4" data-testid="mapping-builder-check-values">
              <div className="flex flex-col gap-0.5">
                <h2 className="text-[13px] font-medium">Check values</h2>
                <p className="text-xs text-muted-foreground">
                  The check renders static values with these. They are not written into the mapping.
                  {checkFlow !== null && <> Prefilled from flow <span className="font-mono">{checkFlowName}</span>.</>}
                </p>
              </div>
              <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
                {draft.parameters.map((parameter) => (
                  <div key={parameter.name} className="flex flex-col gap-1">
                    <Label htmlFor={`mapping-builder-check-${parameter.name}`} className="font-mono text-[12px]">
                      {parameter.name}{parameter.required && parameter.default === null ? " (required)" : ""}
                    </Label>
                    <Input
                      id={`mapping-builder-check-${parameter.name}`}
                      className="h-8 font-mono"
                      placeholder={parameter.default ?? ""}
                      value={checkEdits[parameter.name] ?? checkFlow?.parameters[parameter.name] ?? ""}
                      onChange={(event) => {
                        const value = event.target.value;
                        setCheckEdits((current) => ({ ...current, [parameter.name]: value }));
                      }}
                      data-testid={`mapping-builder-check-${parameter.name}`}
                    />
                    {parameter.description !== null && <p className="text-xs text-muted-foreground">{parameter.description}</p>}
                    <CheckValueSource flow={checkFlow} parameter={parameter.name} edited={checkEdits[parameter.name] !== undefined} />
                  </div>
                ))}
              </div>
            </Card>
          )}

          {draft !== null && (detail.isError ? (
            <Card className="gap-2 rounded-lg p-4" data-testid="mapping-builder-template-error">
              <ProblemView error={detail.error} />
              <p className="text-xs text-muted-foreground">
                A mapping is built against a template its repository's folders hold, read from the file. Put the template's file in
                a template folder on the mapping's path (Browse OSDU on the{" "}
                <RouterLink to="/delivery/templates" className="text-primary hover:underline">Templates page</RouterLink> downloads it),
                sync the repository, then open the mapping here again.
              </p>
            </Card>
          ) : detail.data === undefined ? (
            <Skeleton className="h-96 w-full rounded-lg" />
          ) : (
            <MappingBuilderVariables rows={rows} issues={issues} onOpen={openEditor} onUseCache={fillFromCache} />
          ))}
        </div>

        <div className="flex min-w-0 flex-col gap-4 xl:sticky xl:top-0 xl:self-start">
          {draft === null ? (
            <Card className="gap-0 rounded-lg p-0">
              <EmptyState
                icon={<PencilRuler />}
                title="No mapping yet"
                description="Pick a repository and one of its templates, name the mapping and start it. The YAML and what the check finds appear here."
                data-testid="mapping-builder-empty"
              />
            </Card>
          ) : (
            <>
              <Card className="gap-2 rounded-lg p-3" data-testid="mapping-builder-check">
                <div className="flex flex-wrap items-center gap-2 text-[13px] font-medium">
                  Check
                  {result !== undefined && (result.valid
                    ? <OutcomePill tone="success" label="loads and passes" icon={CircleCheck} testId="mapping-builder-valid" />
                    : <OutcomePill tone="destructive" label={`${errorCount} error${errorCount === 1 ? "" : "s"}`} icon={OctagonAlert} testId="mapping-builder-invalid" />)}
                  {warningCount > 0 && (
                    <OutcomePill tone="warning" label={`${warningCount} warning${warningCount === 1 ? "" : "s"}`} icon={TriangleAlert} testId="mapping-builder-warnings" />
                  )}
                  {composing && (
                    <span className="ml-auto inline-flex items-center gap-1 text-xs font-normal text-muted-foreground" data-testid="mapping-builder-composing">
                      <Loader2 className="size-3.5 animate-spin" />
                      Checking
                    </span>
                  )}
                </div>
                {compose.isError && <ProblemView error={compose.error} testId="mapping-builder-compose-error" />}
                {result !== undefined && issues.length === 0 && (
                  <p className="text-xs text-muted-foreground">The check found nothing to fix.</p>
                )}
                <ul className="flex max-h-80 flex-col gap-0.5 overflow-y-auto" data-testid="mapping-builder-issues">
                  {issues.map((issue, index) => {
                    const openable = issue.target !== null && (rows.some((row) => row.key === issue.target) || lookupOf(issue) !== undefined);
                    const icon = issue.severity === "error"
                      ? <OctagonAlert className="mt-0.5 size-3.5 shrink-0 text-destructive" />
                      : <TriangleAlert className="mt-0.5 size-3.5 shrink-0 text-warning" />;
                    return (
                      <li key={`${index}-${issue.message}`} data-testid="mapping-builder-issue" data-severity={issue.severity}>
                        {openable ? (
                          <button
                            type="button"
                            className="flex w-full items-start gap-1.5 rounded-sm px-1 py-0.5 text-left text-xs hover:bg-accent"
                            onClick={() => openIssue(issue)}
                            data-testid="mapping-builder-issue-open"
                          >
                            {icon}
                            <span>{issue.message}</span>
                          </button>
                        ) : (
                          <div className="flex items-start gap-1.5 px-1 py-0.5 text-xs">
                            {icon}
                            <span>{issue.message}</span>
                          </div>
                        )}
                      </li>
                    );
                  })}
                </ul>
              </Card>
              <Card className="gap-2 rounded-lg p-3" data-testid="mapping-builder-yaml-card">
                <div className="text-[13px] font-medium">
                  <span className="font-mono">mappings/{reference}.yaml</span>
                </div>
                {result !== undefined
                  ? <CodeView value={result.yaml} language="yaml" height={560} data-testid="mapping-builder-yaml" />
                  : compose.isError
                    ? <EmptyState title="The YAML is written once the check answers." data-testid="mapping-builder-yaml-unavailable" />
                    : <Skeleton className="h-[560px] w-full rounded-lg" />}
              </Card>
            </>
          )}
        </div>
      </div>

      {draft !== null && (
        <MappingEntryEditor
          target={editing}
          draft={draft}
          variables={detail.data?.variables ?? []}
          cacheTypes={cache?.types ?? []}
          parent={detail.data?.parent ?? null}
          childKinds={detail.data?.children ?? []}
          issues={issues}
          onSave={saveEntry}
          onClose={() => setEditing(null)}
        />
      )}
      {repo !== null && repo.sourceId !== null && result !== undefined && draft !== null && mappingPath !== null && (
        <MappingProposeSheet
          open={proposeOpen}
          onClose={() => setProposeOpen(false)}
          repo={repo}
          sourceId={repo.sourceId}
          name={name.trim()}
          version={mappingVersion.trim()}
          path={mappingPath}
          yaml={result.yaml}
          template={chosenTemplate?.path ?? null}
        />
      )}
      <ConfirmDialog
        open={startOverOpen}
        title="Start over"
        message="Discard this draft and its entries? Nothing was saved or proposed from it, so what is not copied or proposed is gone."
        confirmLabel="Discard the draft"
        danger
        onConfirm={startOver}
        onClose={() => setStartOverOpen(false)}
      />
    </Page>
  );
}
