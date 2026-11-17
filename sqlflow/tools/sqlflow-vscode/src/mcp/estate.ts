import { NEVER_RUN_KINDS, neverRunKind } from '../documents/recognition';
import { McpServer } from './mcpServer';

/** A page of results from the control plane's PagedResult<T> shape. */
export interface Paged<T> {
    items: T[];
    page: number;
    pageSize: number;
    total: number;
}

/** The links a tool's answer carries: `page` is the row's own GUI page, the rest are named for what they open. */
export interface WithLinks {
    links?: Record<string, string>;
}

export interface Repo extends WithLinks {
    id: string;
    name: string;
    remoteUrl?: string | null;
    rootPath?: string | null;
    lastSyncUtc?: string;
}

export interface RepoSource {
    id: string;
    name: string;
    remoteUrl?: string | null;
    localPath?: string | null;
    branch: string;
    enabled: boolean;
    lastSyncUtc?: string | null;
    lastSyncedSha?: string | null;
    lastError?: string | null;
    syncPending?: boolean;
}

export interface Pipeline extends WithLinks {
    id: string;
    repoId: string;
    name: string;
    kind: string;
    batch?: string | null;
    wave?: number;
    active?: boolean;
    lifecycle?: string;
    executionMode?: string;
    sourceServer?: string | null;
    targetServer?: string | null;
    relativePath?: string;
    lastSeenUtc?: string;
}

export interface PipelineDetail extends Pipeline {
    yaml: string;
    contentHash?: string;
    runsOnSchedule?: boolean;
}

export interface Run extends WithLinks {
    runId: string;
    pipelineId: string;
    repoId?: string | null;
    flowName: string;
    flowKind?: string;
    status: string;
    success?: boolean;
    commitSha?: string | null;
    operation?: string | null;
    requestedBy?: string | null;
    triggerSource?: string | null;
    /** What started the run, as the control plane words it; absent from a control plane that predates it. */
    startedBy?: { kind: string; summary: string; who?: string | null } | null;
    startUtc?: string | null;
    endUtc?: string | null;
    writtenUtc?: string;
    durationSeconds?: number | null;
    rowsLoaded?: number | null;
    rowsInserted?: number | null;
    rowsUpdated?: number | null;
    rowsDeleted?: number | null;
    fileCount?: number;
    lastAction?: string | null;
    error?: string | null;
    failedStatementStep?: string | null;
    failedStatementSql?: string | null;
}

export interface Schedule extends WithLinks {
    id: string;
    repoId: string;
    name?: string;
    cron?: string | null;
    intervalSeconds?: number | null;
    timezone?: string;
    enabled?: boolean;
    paused?: boolean;
    nextFireUtc?: string | null;
    lastFireUtc?: string | null;
    memberPipelineIds?: string[];
    afterSchedules?: string[] | null;
}

export interface Datasource {
    reference: string;
    kind?: string | null;
    resolvable: boolean;
    sourcePipelines: number;
    targetPipelines: number;
}

export interface DocumentProblem {
    severity: string;
    line: number;
    column: number;
    code?: string | null;
    message: string;
}

/** How long a read of the catalog is reused. Lenses and views ask together as a person moves between files. */
const MEMO_MS = 15_000;

/** The flows read at a time when every one is wanted, and the most pages read: 20,000 flows, past any estate. */
const EVERY_PIPELINE_PAGE = 200;
const EVERY_PIPELINE_PAGES = 100;

/** Unwrap either a PagedResult<T> or a bare T[] into T[]. */
export function items<T>(result: { items: T[] } | T[]): T[] {
    return Array.isArray(result) ? result : result.items;
}

/**
 * The estate as this extension reads it: named reads over the server's tools, typed to the answers they give. Reads a
 * person's movement repeats are memoised for a moment, and any action drops the memo.
 */
export class Estate {
    private readonly memo = new Map<string, { at: number; value: Promise<unknown> }>();
    readonly mcp: McpServer;

    constructor(mcp: McpServer) {
        this.mcp = mcp;
    }

    forget(): void {
        this.memo.clear();
    }

    private remembered<T>(tool: string, args: Record<string, unknown> = {}): Promise<T> {
        const key = `${tool} ${JSON.stringify(args)}`;
        const held = this.memo.get(key);
        if (held && Date.now() - held.at < MEMO_MS) {
            return held.value as Promise<T>;
        }
        const value = this.mcp.call<T>(tool, args);
        this.memo.set(key, { at: Date.now(), value });
        value.catch(() => this.memo.delete(key));
        return value;
    }

    async repos(): Promise<Repo[]> {
        return items(await this.remembered<Paged<Repo> | Repo[]>('list_repos'));
    }

    async repoSources(): Promise<RepoSource[]> {
        return items(await this.remembered<Paged<RepoSource> | RepoSource[]>('list_repo_sources'));
    }

    async syncRepoSource(id: string): Promise<void> {
        await this.mcp.text('sync_repo_source', { id });
        this.forget();
    }

    async pipelines(query: { repoId?: string; kind?: string; active?: boolean; name?: string; pageSize?: number } = {}): Promise<Pipeline[]> {
        return items(await this.remembered<Paged<Pipeline> | Pipeline[]>('list_pipelines', { ...query, pageSize: query.pageSize ?? 200 }));
    }

    /**
     * Every flow of the catalog, read a page at a time: what lists a whole working copy against the estate needs them
     * all, and an estate holds more flows than one page does.
     */
    async everyPipeline(): Promise<Pipeline[]> {
        // The catalog lists the flows that run, and lists the documents that are never run as a flow (a lineage
        // description of a step another system runs, a report of read-only queries) when asked for by their kind. A
        // working copy holds all of them.
        let all = await this.everyPipelineOf(undefined);
        for (const kind of NEVER_RUN_KINDS) {
            all = all.concat(await this.everyPipelineOf(kind));
        }
        return all;
    }

    private async everyPipelineOf(kind: string | undefined): Promise<Pipeline[]> {
        const all: Pipeline[] = [];
        for (let page = 1; page <= EVERY_PIPELINE_PAGES; page++) {
            const answer = await this.remembered<Paged<Pipeline> | Pipeline[]>(
                'list_pipelines', { page, pageSize: EVERY_PIPELINE_PAGE, ...(kind === undefined ? {} : { kind }) });
            const listed = items(answer);
            all.push(...listed);
            if (Array.isArray(answer) || listed.length < EVERY_PIPELINE_PAGE || all.length >= answer.total) {
                break;
            }
        }
        return all;
    }

    /**
     * The catalog's flow of a name, the one at `relativePath` when several repositories hold one. `flowType` is the
     * document's own: a lineage description and a report are listed by their kind, so each is asked for by it.
     */
    async pipelineNamed(name: string, relativePath?: string, flowType?: string): Promise<Pipeline | undefined> {
        const kind = neverRunKind(flowType);
        const named = (await this.pipelines({ name, ...(kind === undefined ? {} : { kind }) })).filter((pipeline) => pipeline.name === name);
        const normalised = relativePath?.replace(/\\/g, '/');
        return named.find((pipeline) => normalised !== undefined && pipeline.relativePath !== undefined
            && (normalised.endsWith(pipeline.relativePath) || pipeline.relativePath.endsWith(normalised))) ?? named[0];
    }

    pipeline(id: string): Promise<PipelineDetail> {
        return this.remembered('get_pipeline', { id });
    }

    async runs(query: { pipelineId?: string; repoId?: string; status?: string; pageSize?: number } = {}): Promise<Run[]> {
        return items(await this.mcp.call<Paged<Run> | Run[]>('list_runs', { ...query, pageSize: query.pageSize ?? 50 }));
    }

    async schedules(): Promise<Schedule[]> {
        return items(await this.remembered<Paged<Schedule> | Schedule[]>('list_schedules'));
    }

    async datasources(): Promise<Datasource[]> {
        return items(await this.remembered<Paged<Datasource> | Datasource[]>('list_datasources'));
    }

    /** What the language engine reports of a document's text: the same analysis as the editor's, without opening it. */
    async validate(yaml: string): Promise<DocumentProblem[]> {
        const answer = await this.mcp.text('validate_flow', { yaml });
        if (answer.startsWith('OK')) {
            return [];
        }
        return (JSON.parse(answer) as { problems: DocumentProblem[] }).problems;
    }
}
