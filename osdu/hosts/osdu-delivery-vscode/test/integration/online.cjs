const assert = require('node:assert/strict');
const vscode = require('vscode');

/**
 * The extension's reads against a real control plane, when the run names one with a token: the answers a typed read
 * expects are the control plane's real shapes, which no fake can prove. It reads, and starts nothing.
 */
module.exports = (estate) => ({
    async 'the catalog reads as typed'() {
        const repos = await estate.repos();
        assert.ok(repos.length > 0 && repos[0].id && repos[0].name, JSON.stringify(repos[0]));
        const sources = await estate.repoSources();
        assert.ok(sources.length > 0 && sources[0].id && sources[0].branch !== undefined, JSON.stringify(sources[0]));
        const pipelines = await estate.pipelines({ repoId: repos[0].id });
        const delivery = pipelines.find((pipeline) => pipeline.kind === 'delivery');
        assert.ok(delivery?.repoId && delivery.relativePath, JSON.stringify(delivery));
        assert.equal((await estate.pipelineNamed(delivery.name, delivery.relativePath))?.id, delivery.id);
        const detail = await estate.pipeline(delivery.id);
        assert.ok(detail.yaml.includes('flowType: delivery'));
        assert.ok(Array.isArray(await estate.pipelineColumns(delivery.id)));
        assert.ok(Array.isArray(await estate.dependencies(delivery.repoId, delivery.id)));
        const dependencies = await estate.dependencies(delivery.repoId, delivery.id);
        if (dependencies.length > 0) {
            assert.ok(dependencies[0].fromFlow && dependencies[0].toFlow, JSON.stringify(dependencies[0]));
        }
    },

    async 'a run reads as typed, with its trace'() {
        const [run] = await estate.runs({ status: 'succeeded', pageSize: 1 });
        assert.ok(run?.runId && run.flowName && run.status, JSON.stringify(run));
        const detail = await estate.run(run.runId);
        assert.equal(detail.runId, run.runId);
        const trace = await estate.runTrace(run.runId, 50);
        assert.ok(trace.entries.length > 0, 'a finished run left a trace');
        assert.ok(trace.entries[0].kind && trace.entries[0].level, JSON.stringify(trace.entries[0]));
        assert.ok(Array.isArray(await estate.runStatements(run.runId)));
        assert.ok(Array.isArray(await estate.runAssertions(run.runId)));
        assert.ok(Array.isArray(await estate.runFiles(run.runId)));
        await vscode.commands.executeCommand('sqlflow.openRun', run.runId);
    },

    async 'the kinds carry the operations a run form offers'() {
        const kinds = await estate.flowKinds();
        assert.ok(kinds.flowKinds.includes('delivery') && kinds.flowKinds.includes('ing'));
        assert.ok(kinds.documentKinds.includes('mapping'));
        const delivery = kinds.runOperations.find((kind) => kind.flowType === 'delivery');
        assert.ok(delivery.operations.some((operation) => operation.name === 'deliver' && operation.writesTarget), JSON.stringify(delivery));
    },

    async 'schedules, datasources and streams read as typed'() {
        const schedules = await estate.schedules();
        // An estate may hold none: the end-to-end one strips them, since a fire would write to its target.
        assert.ok(Array.isArray(schedules) && (schedules.length === 0 || schedules[0].id), `schedules: ${JSON.stringify(schedules).slice(0, 300)}`);
        const datasources = await estate.datasources();
        assert.ok(datasources.length > 0 && datasources[0].reference, `datasources: ${JSON.stringify(datasources).slice(0, 300)}`);
        const board = await estate.dataStreams(60);
        assert.ok(typeof board.analyzedStreams === 'number' && Array.isArray(board.streams), `streams: ${JSON.stringify(board).slice(0, 300)}`);
    },

    async 'the run order renders'() {
        const repos = await estate.repos();
        const waves = await estate.lineageWaves(repos[0].id);
        const rows = Array.isArray(waves) ? waves : waves.items;
        assert.ok(rows.length > 0 && rows[0].pipelines, JSON.stringify(waves).slice(0, 300));
    },

    async 'the delivery tools answer what the views read'() {
        const mcp = estate.mcp;
        const [flow] = await estate.pipelines({ kind: 'delivery' });
        const stats = await mcp.call('delivery_flow', { pipelineId: flow.id });
        assert.ok(stats.stats !== undefined && stats.interfaces !== undefined, JSON.stringify(stats).slice(0, 300));
        const issues = await mcp.call('delivery_flow_issues', { pipelineId: flow.id, interface: undefined });
        assert.ok(Array.isArray(issues.issues), JSON.stringify(issues).slice(0, 300));
        const partitions = await mcp.call('delivery_partitions');
        assert.ok(Array.isArray(partitions) || Array.isArray(partitions.partitions) || Array.isArray(partitions.items), JSON.stringify(partitions).slice(0, 300));
        const templates = await mcp.call('delivery_templates');
        const files = Array.isArray(templates) ? templates : templates.items;
        assert.ok(files.length > 0 && files[0].repoId && files[0].path, JSON.stringify(templates).slice(0, 300));
        for (const tool of ['delivery_mappings', 'delivery_caches', 'delivery_assertions', 'delivery_dimensions', 'delivery_inventories', 'delivery_removals', 'delivery_config']) {
            await mcp.call(tool);
        }
    },

    async 'the control plane checks a mapping from the working copy'() {
        const uris = await vscode.workspace.findFiles('**/mappings/Wellbore@1.3.0.yaml', undefined, 1);
        const text = Buffer.from(await vscode.workspace.fs.readFile(uris[0])).toString('utf8');
        const [repo] = await estate.repos();
        const answer = await estate.mcp.call('delivery_check_mapping', { yaml: text, repoId: repo.id });
        assert.ok(answer.requiredAndEmpty !== undefined || answer.issues !== undefined, JSON.stringify(answer).slice(0, 400));
    },

    async 'the data definitions read the way the template commands walk them'() {
        const { releases } = await estate.mcp.call('delivery_osdu_schemas');
        assert.ok(releases.length > 0 && releases[0].name, JSON.stringify(releases).slice(0, 200));
        // The command offers only a release in the local copy, which a hermetic estate has none of.
        const local = releases.find((release) => release.local);
        if (local) {
            const index = await estate.mcp.call('delivery_osdu_schemas', { release: local.name });
            assert.ok(index.schemas.length > 0 && index.schemas[0].kind, JSON.stringify(index).slice(0, 200));
            const file = await estate.mcp.call('delivery_osdu_schemas', { release: local.name, kind: index.schemas[0].kind });
            assert.ok(file.kind && file.version && file.schema, JSON.stringify(file).slice(0, 200));
        }
    },

    async 'a table reads the way a mapping asks for its columns'() {
        const listed = await estate.mcp.call('lineage_objects', { name: 'Wellbore' });
        const objects = Array.isArray(listed) ? listed : listed.items;
        assert.ok(objects.length > 0 && objects[0].key, JSON.stringify(listed).slice(0, 300));
        const answer = await estate.mcp.call('lineage_object_columns', { key: objects[0].key });
        const columns = Array.isArray(answer) ? answer : answer.items;
        assert.ok(Array.isArray(columns), JSON.stringify(answer).slice(0, 300));
    },

    async 'an admin reads where the AI contexts stand'() {
        assert.ok(Array.isArray(await estate.aiContexts()));
    },

    async 'a record is found the way the command looks for it'() {
        const answer = await estate.mcp.call('delivery_find_records', {});
        assert.ok(Array.isArray(answer.items ?? answer.hits ?? answer), JSON.stringify(answer).slice(0, 300));
    },
});
