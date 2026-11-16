const assert = require('node:assert/strict');
const vscode = require('vscode');

/**
 * The extension's reads against a real control plane, when the run names one with a token: the answers a typed read
 * expects are the control plane's real shapes, which no fake can prove. It reads, and starts nothing.
 */
module.exports = (estate, pageTabs, until) => ({
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
        const every = await estate.everyPipeline();
        assert.ok(every.length >= pipelines.length && every.some((pipeline) => pipeline.id === delivery.id));
    },

    async 'a run reads as typed, and its page shows it'() {
        const [run] = await estate.runs({ status: 'succeeded', pageSize: 1 });
        assert.ok(run?.runId && run.flowName && run.status, JSON.stringify(run));
        await vscode.commands.executeCommand('sqlflow.openRun', run.runId);
        // The page names its own tab once it has read the run through the extension: the pages load, are signed in and
        // reach the control plane.
        await until('the run\'s page to name its tab', () => pageTabs().some((tab) => tab.label === `Run ${run.runId.slice(0, 8)}` || tab.label.includes(run.flowName)), 60000);
        await vscode.commands.executeCommand('workbench.action.closeAllEditors');
    },

    async 'a page of the estate shows what the control plane holds'() {
        await vscode.commands.executeCommand('sqlflow.openRuns');
        await until('the Runs page to name its tab', () => pageTabs().some((tab) => tab.label === 'Runs'), 60000);
        await vscode.commands.executeCommand('workbench.action.closeAllEditors');
    },

    async 'a mapping\'s preview reads its file and places it in the estate'() {
        const [uri] = await vscode.workspace.findFiles('**/mappings/Wellbore@1.3.0.yaml', undefined, 1);
        await vscode.window.showTextDocument(await vscode.workspace.openTextDocument(uri));
        await vscode.commands.executeCommand('osduDelivery.previewMapping', uri);
        await until('the preview to name its tab after the mapping', () => pageTabs().some((tab) => tab.label === 'Wellbore@1.3.0 (working copy)'), 60000);
        await vscode.commands.executeCommand('workbench.action.closeAllEditors');
    },

    async 'the kinds carry the operations a run form offers'() {
        const kinds = await estate.flowKinds();
        assert.ok(kinds.flowKinds.includes('delivery') && kinds.flowKinds.includes('ing'));
        assert.ok(kinds.documentKinds.includes('mapping'));
        const delivery = kinds.runOperations.find((kind) => kind.flowType === 'delivery');
        assert.ok(delivery.operations.some((operation) => operation.name === 'deliver' && operation.writesTarget), JSON.stringify(delivery));
    },

    async 'schedules and datasources read as typed'() {
        const schedules = await estate.schedules();
        // An estate may hold none: the end-to-end one strips them, since a fire would write to its target.
        assert.ok(Array.isArray(schedules) && (schedules.length === 0 || schedules[0].id), `schedules: ${JSON.stringify(schedules).slice(0, 300)}`);
        const datasources = await estate.datasources();
        assert.ok(datasources.length > 0 && datasources[0].reference, `datasources: ${JSON.stringify(datasources).slice(0, 300)}`);
    },

    async 'the delivery tools answer what the commands read'() {
        const mcp = estate.mcp;
        const [flow] = await estate.pipelines({ kind: 'delivery' });
        const stats = await mcp.call('delivery_flow', { pipelineId: flow.id });
        assert.ok(stats.stats !== undefined && stats.interfaces !== undefined, JSON.stringify(stats).slice(0, 300));
        const partitions = await mcp.call('delivery_partitions');
        assert.ok(Array.isArray(partitions) || Array.isArray(partitions.partitions) || Array.isArray(partitions.items), JSON.stringify(partitions).slice(0, 300));
        const templates = await mcp.call('delivery_templates');
        const files = Array.isArray(templates) ? templates : templates.items;
        assert.ok(files.length > 0 && files[0].repoId && files[0].path, JSON.stringify(templates).slice(0, 300));
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
});
