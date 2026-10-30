import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { join } from 'node:path';

// The tools the extension calls and the arguments it passes, held against the schemas the server lists. An interactive
// command reaches a tool only behind quick picks a run cannot answer, so this is what catches an argument a tool does not
// take, or one it requires and is not given, before a person does. A call added to the extension is added here.
const CALLS = {
    delivery_flow: ['pipelineId'],
    delivery_flow_issues: ['pipelineId'],
    delivery_submissions: ['pipelineId', 'max'],
    delivery_caches: ['repoId'],
    delivery_activities: ['pipelineId', 'pageSize'],
    delivery_assertions: ['pipelineId'],
    delivery_dimensions: ['pipelineId'],
    delivery_retrievals: ['pipelineId'],
    delivery_inventories: ['pipelineId'],
    delivery_removals: ['pipelineId'],
    delivery_check_mapping: ['yaml', 'path', 'repoId', 'shape'],
    delivery_scaffold_mapping: ['repoId', 'templatePath', 'name', 'mappingVersion', 'system'],
    delivery_osdu_schema_compare: ['fromRelease', 'fromKind', 'toRelease', 'toKind'],
    delivery_find_records: ['search'],
    delivery_record: ['flowId', 'deliveryKey'],
    delivery_probe_target: ['pipelineId'],
    delivery_run: ['id'],
    delivery_osdu_schemas: ['release', 'kind'],
    delivery_cache_changes: [],
    lineage_objects: ['name'],
    lineage_object_columns: ['key'],
    list_ai_contexts: [],
    get_cli: [],
    delivery_partitions: [],
    delivery_templates: [],
    delivery_mappings: [],
    delivery_config: [],
    list_repos: [],
    list_repo_sources: [],
    sync_repo_source: ['id'],
    list_pipelines: ['repoId', 'kind', 'active', 'name', 'pageSize'],
    get_pipeline: ['id'],
    pipeline_columns: ['id'],
    list_runs: ['pipelineId', 'repoId', 'status', 'pageSize'],
    get_run: ['runId'],
    run_trace: ['runId', 'page', 'pageSize'],
    run_statements: ['runId'],
    run_assertions: ['runId'],
    run_files: ['runId'],
    trigger_run: ['repoId', 'flowName', 'fullLoad', 'operation', 'values', 'payload'],
    cancel_run: ['runId'],
    list_schedules: [],
    run_schedule: ['id'],
    get_schedule_plan: ['id'],
    list_datasources: [],
    list_flow_kinds: [],
    lineage_waves: ['repoId'],
    lineage_dependencies: ['repoId', 'pipelineId'],
    flow_definition_file_history: ['pipelineId'],
    detect_stream_anomalies: ['days', 'pipelineId'],
    validate_flow: ['yaml'],
    search_docs: ['query'],
    login: [],
    check_auth_status: [],
    set_access_token: ['token'],
    logout: [],
};

/** What the server lists, over its own protocol. */
async function listTools(program) {
    const child = spawn(program, [], { stdio: 'pipe', env: { ...process.env, SQLFLOW_CONTROL_PLANE_URL: 'http://127.0.0.1:1' } });
    const pending = new Map();
    let buffer = '';
    child.stdout.setEncoding('utf8').on('data', (chunk) => {
        buffer += chunk;
        for (let end = buffer.indexOf('\n'); end >= 0; end = buffer.indexOf('\n')) {
            const line = buffer.slice(0, end);
            buffer = buffer.slice(end + 1);
            if (line.startsWith('{')) {
                const message = JSON.parse(line);
                pending.get(message.id)?.(message.result);
            }
        }
    });

    let id = 0;
    const request = (method, params) => new Promise((resolve) => {
        id += 1;
        pending.set(id, resolve);
        child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id, method, params })}\n`);
    });

    try {
        await request('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'tool-calls', version: '1' } });
        child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' })}\n`);
        return (await request('tools/list', {})).tools;
    } finally {
        child.kill();
    }
}

/** Every call the extension makes, against the tools the packaged server lists. Throws naming each that does not fit. */
export async function checkToolCalls(extensionPath) {
    const program = join(extensionPath, 'bin', process.platform === 'win32' ? 'osdu-delivery-mcp.exe' : 'osdu-delivery-mcp');
    if (!existsSync(program)) {
        throw new Error(`${program} is not built: npm run bundle stages it.`);
    }

    const tools = await listTools(program);
    const problems = [];
    for (const [name, args] of Object.entries(CALLS)) {
        const tool = tools.find((candidate) => candidate.name === name);
        if (!tool) {
            problems.push(`${name} is not offered`);
            continue;
        }
        const taken = Object.keys(tool.inputSchema?.properties ?? {});
        const unknown = args.filter((arg) => !taken.includes(arg));
        const unmet = (tool.inputSchema?.required ?? []).filter((arg) => !args.includes(arg));
        if (unknown.length > 0 || unmet.length > 0) {
            problems.push(`${name}: it takes no ${JSON.stringify(unknown)}, and requires ${JSON.stringify(unmet)}`);
        }
    }

    if (problems.length > 0) {
        throw new Error(`The extension's tool calls do not fit the server:\n  ${problems.join('\n  ')}`);
    }
    console.log(`ok   ${Object.keys(CALLS).length} tool calls fit what the server lists`);
}
