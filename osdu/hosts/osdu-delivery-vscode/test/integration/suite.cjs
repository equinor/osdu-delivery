const assert = require('node:assert/strict');
const path = require('node:path');
const vscode = require('vscode');

const EXTENSION = 'delta-forge.osdu-delivery';

const sample = (relative) => vscode.Uri.file(path.join(vscode.workspace.workspaceFolders[0].uri.fsPath, relative));

/** Waits for a condition the editor reaches on its own time: a language server's answer, a language assignment. */
async function until(what, condition, timeoutMs = 30000) {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
        const value = await condition();
        if (value) {
            return value;
        }
        if (Date.now() > deadline) {
            throw new Error(`Timed out waiting for ${what}.`);
        }
        await new Promise((resolve) => setTimeout(resolve, 200));
    }
}

async function open(relative) {
    const document = await vscode.workspace.openTextDocument(sample(relative));
    await vscode.window.showTextDocument(document);
    await until(`${relative} to be a pipeline document`, () =>
        vscode.window.activeTextEditor?.document.uri.fsPath === sample(relative).fsPath
        && vscode.window.activeTextEditor?.document.languageId === 'sqlflow-flow');
    return vscode.window.activeTextEditor.document;
}

const hoverText = (hovers) => hovers.flatMap((hover) => hover.contents).map((content) => content.value ?? String(content)).join('\n');

/** The tabs showing a page of the GUI: each is a webview of the extension's page kind. */
const pageTabs = () => vscode.window.tabGroups.all
    .flatMap((group) => group.tabs)
    .filter((tab) => tab.input instanceof vscode.TabInputWebview && tab.input.viewType.includes('sqlflow.page'));

const checks = {
    async 'the extension activates and contributes its views'() {
        const extension = vscode.extensions.getExtension(EXTENSION);
        assert.ok(extension, 'the extension is installed');
        await extension.activate();
        // One drawn side bar, and no tree of the editor's own.
        assert.deepEqual(extension.packageJSON.contributes.views.sqlflow.map((view) => [view.id, view.type]), [['sqlflow.sideBar', 'webview']]);
    },

    async 'every command the manifest names is registered'() {
        const extension = vscode.extensions.getExtension(EXTENSION);
        const registered = new Set(await vscode.commands.getCommands(true));
        const missing = extension.packageJSON.contributes.commands.map((command) => command.command).filter((id) => !registered.has(id));
        assert.deepEqual(missing, []);
    },

    async 'the packaged programs are there, and a terminal finds the command line'() {
        const extension = vscode.extensions.getExtension(EXTENSION);
        const fs = require('node:fs');
        const exe = process.platform === 'win32' ? '.exe' : '';
        for (const program of ['sqlflow-lsp', 'osdu-delivery-mcp', 'sqlflow']) {
            assert.ok(fs.existsSync(path.join(extension.extensionPath, 'bin', `${program}${exe}`)), program);
        }
        const answer = await extension.exports.estate.mcp.call('get_environment');
        assert.ok(answer.commandLine.path.endsWith(`sqlflow${exe}`), answer.commandLine.path);
        assert.ok(answer.commandLine.conventions.includes('--json'));
        const uses = answer.commandLine.uses.map((use) => use.command);
        assert.ok(uses.some((command) => command.endsWith('validate .')), uses.join(', '));
        assert.ok(uses.some((command) => command.endsWith('check <flow.yaml>')), uses.join(', '));
        const commands = await vscode.commands.getCommands(true);
        for (const id of ['sqlflow.shareWithOtherTools', 'sqlflow.unshareFromOtherTools']) {
            assert.ok(commands.includes(id), id);
        }
    },

    async 'a plain yaml flow opens as a pipeline document'() {
        const document = await open('flows/welldb-wellbore-03-delivery.yaml');
        assert.equal(document.languageId, 'sqlflow-flow');
    },

    async 'yaml that is no pipeline document stays yaml'() {
        const document = await vscode.workspace.openTextDocument({ language: 'yaml', content: 'services:\n  db:\n    image: x\n' });
        assert.equal(document.languageId, 'yaml');
    },

    async 'the language server knows the module kinds through their census'() {
        const document = await open('flows/welldb-wellbore-03-delivery.yaml');
        const line = document.getText().split(/\r?\n/).findIndex((text) => text.startsWith('render:'));
        const hover = await until('a hover on render', async () => {
            const hovers = await vscode.commands.executeCommand('vscode.executeHoverProvider', document.uri, new vscode.Position(line, 2));
            return hovers?.length ? hovers : undefined;
        });
        assert.ok(hoverText(hover).includes('render'), hoverText(hover));
    },

    async 'a delivery flow from the sample has no language errors'() {
        const document = await open('flows/welldb-wellbore-03-delivery.yaml');
        await new Promise((resolve) => setTimeout(resolve, 1500));
        const errors = vscode.languages.getDiagnostics(document.uri).filter((d) => d.severity === vscode.DiagnosticSeverity.Error);
        assert.deepEqual(errors.map((d) => d.message), []);
    },

    async 'the language server flags an unknown key of a module kind'() {
        const document = await vscode.workspace.openTextDocument({
            language: 'sqlflow-flow',
            content: 'flowType: delivery\nname: x\nnotAKey: 1\n',
        });
        await vscode.window.showTextDocument(document);
        const diagnostics = await until('a diagnostic', () => {
            const found = vscode.languages.getDiagnostics(document.uri);
            return found.length ? found : undefined;
        });
        assert.ok(diagnostics.some((d) => d.message.includes('notAKey')), diagnostics.map((d) => d.message).join('; '));
    },

    async 'a mapping completes its properties from the template folder'() {
        const document = await open('mappings/Wellbore@1.3.0.yaml');
        const lines = document.getText().split(/\r?\n/);
        const editor = vscode.window.activeTextEditor;
        const line = lines.findIndex((text) => text === '  data:') + 1;
        await editor.edit((edit) => edit.insert(new vscode.Position(line, 0), '    Se\n'));
        const list = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, new vscode.Position(line, 6));
        const labels = list.items.map((item) => (typeof item.label === 'string' ? item.label : item.label.label));
        assert.ok(labels.includes('SequenceNumber'), labels.slice(0, 20).join(', '));
        await vscode.commands.executeCommand('undo');
    },

    async 'a mapping hover says what the template says'() {
        const document = await open('mappings/Wellbore@1.3.0.yaml');
        const line = document.getText().split(/\r?\n/).findIndex((text) => text === '    WellID:');
        const hover = await until('a hover on WellID', async () => {
            const hovers = await vscode.commands.executeCommand('vscode.executeHoverProvider', document.uri, new vscode.Position(line, 6));
            return hoverText(hovers ?? []).includes('points to Well') ? hovers : undefined;
        });
        assert.ok(hoverText(hover).includes('`string`'));
    },

    async 'a key the template lacks is reported as it is typed'() {
        const document = await open('mappings/Wellbore@1.3.0.yaml');
        const editor = vscode.window.activeTextEditor;
        const line = document.getText().split(/\r?\n/).findIndex((text) => text === '  data:') + 1;
        await editor.edit((edit) => edit.insert(new vscode.Position(line, 0), '    NotInTheTemplate: x\n'));
        const found = await until('a template finding', () => vscode.languages.getDiagnostics(document.uri).find((d) => d.message.includes('NotInTheTemplate')));
        assert.equal(found.source, 'template');
        await vscode.commands.executeCommand('undo');
    },

    async 'a flow links to the mapping it pins and a schedule to its library'() {
        const document = await open('flows/welldb-wellbore-03-delivery.yaml');
        const links = await until('document links', async () => {
            const found = await vscode.commands.executeCommand('vscode.executeLinkProvider', document.uri);
            return found?.some((link) => link.target?.fsPath.endsWith('Wellbore@1.3.0.yaml')) ? found : undefined;
        });
        assert.ok(links.some((link) => link.target?.fsPath.endsWith('schedules.yaml')));
    },

    async 'a mapping links to its template file'() {
        const document = await open('mappings/Wellbore@1.3.0.yaml');
        const links = await until('a template link', async () => {
            const found = await vscode.commands.executeCommand('vscode.executeLinkProvider', document.uri);
            return found?.find((link) => link.target?.fsPath.endsWith('osdu_wks_master-data--Wellbore_1.3.0.json'));
        });
        assert.ok(links);
    },

    async 'a flow is run from its page, so the editor offers no run of its own'() {
        const document = await open('flows/welldb-wellbore-03-delivery.yaml');
        const lenses = await vscode.commands.executeCommand('vscode.executeCodeLensProvider', document.uri);
        assert.ok(!lenses.some((lens) => lens.command?.command === 'sqlflow.runFlow'));
        assert.ok(!(await vscode.commands.getCommands(true)).includes('sqlflow.runFlow'));
    },

    async 'a mapping offers its preview'() {
        const document = await open('mappings/Wellbore@1.3.0.yaml');
        const lenses = await vscode.commands.executeCommand('vscode.executeCodeLensProvider', document.uri);
        assert.ok(lenses.some((lens) => lens.command?.command === 'osduDelivery.previewMapping'));
    },

    async 'the pages are built, and the side bar and a page open with nobody signed in'() {
        const extension = vscode.extensions.getExtension(EXTENSION);
        const fs = require('node:fs');
        const hosted = path.join(extension.extensionPath, 'dist', 'hosted');
        for (const file of ['page.js', 'side-bar.js', path.join('.vite', 'manifest.json')]) {
            assert.ok(fs.existsSync(path.join(hosted, file)), `${file} is built`);
        }
        await vscode.commands.executeCommand('workbench.view.extension.sqlflow');
        await vscode.commands.executeCommand('sqlflow.openHome');
        await until('a page in a tab', () => pageTabs().length === 1);
        // The same page asked for again is the tab already on it.
        await vscode.commands.executeCommand('sqlflow.openHome');
        await new Promise((resolve) => setTimeout(resolve, 500));
        assert.equal(pageTabs().length, 1);
        await vscode.commands.executeCommand('workbench.action.closeAllEditors');
    },

    async 'a mapping\'s preview opens beside its file'() {
        const document = await open('mappings/Wellbore@1.3.0.yaml');
        await vscode.commands.executeCommand('osduDelivery.previewMapping', document.uri);
        const [tab] = await until('the preview\'s tab', () => (pageTabs().length === 1 ? pageTabs() : undefined));
        assert.notEqual(tab.group.viewColumn, vscode.window.activeTextEditor?.viewColumn ?? vscode.window.visibleTextEditors[0].viewColumn);
        await vscode.commands.executeCommand('workbench.action.closeAllEditors');
    },

    async 'every skeleton of New Document is one the engine takes'() {
        const extension = vscode.extensions.getExtension(EXTENSION);
        for (const skeleton of extension.exports.skeletons) {
            const document = await vscode.workspace.openTextDocument({ language: 'sqlflow-flow', content: skeleton.content });
            const diagnostics = await until(`the engine's word on ${skeleton.label}`, async () => {
                await vscode.window.showTextDocument(document);
                await new Promise((resolve) => setTimeout(resolve, 700));
                return [vscode.languages.getDiagnostics(document.uri)];
            });
            const errors = diagnostics[0].filter((d) => d.severity === vscode.DiagnosticSeverity.Error).map((d) => d.message);
            assert.deepEqual(errors, [], skeleton.label);
        }
    },

    async 'a lineage description is understood as it is typed: its keys checked and documented'() {
        // SQLFlow's own sample of the kind, with one key the kind does not have put first: the language server packaged
        // here knows flowType lin, so the sample's own keys pass, the stray one is refused on its line, and a key is
        // explained on hover. A package built from sources older than the kind fails here.
        await vscode.commands.executeCommand('workbench.action.closeAllEditors');
        const fs = require('node:fs');
        const extension = vscode.extensions.getExtension(EXTENSION);
        const original = fs.readFileSync(
            path.join(extension.extensionPath, '..', '..', '..', 'sqlflow', 'samples', 'lineage-described', 'lineage', 'orders-u1-curate.yaml'),
            'utf8'
        );
        const text = `notAKeyOfLin: 1\n${original}`;
        const described = sample('flows/lineage-probe.yaml');
        await vscode.workspace.fs.writeFile(described, Buffer.from(text));
        try {
            const document = await open('flows/lineage-probe.yaml');
            const errors = await until('the language server\'s word on the lineage description', () => {
                const found = vscode.languages.getDiagnostics(document.uri).filter((d) => d.severity === vscode.DiagnosticSeverity.Error);
                return found.length > 0 ? found : undefined;
            });
            assert.deepEqual(errors.map((d) => [d.range.start.line, d.message.includes('notAKeyOfLin')]), [[0, true]], errors.map((d) => d.message).join('; '));

            const systemLine = text.split('\n').findIndex((line) => line.startsWith('system:'));
            const hovers = await until('a hover on system', async () => {
                const found = await vscode.commands.executeCommand('vscode.executeHoverProvider', document.uri, new vscode.Position(systemLine, 2));
                return found?.length ? found : undefined;
            });
            assert.match(hoverText(hovers), /the system that performs the step/i);
        } finally {
            await vscode.commands.executeCommand('workbench.action.closeAllEditors');
            await vscode.workspace.fs.delete(described);
        }
    },

    async 'a report is understood as it is typed: its keys checked and documented'() {
        // SQLFlow's own sample of the kind, with one key the kind does not have put first: the language server packaged
        // here knows flowType rpt, so the sample's own keys pass, the stray one is refused on its line, and a key is
        // explained on hover. A package built from sources older than the kind fails here.
        await vscode.commands.executeCommand('workbench.action.closeAllEditors');
        const fs = require('node:fs');
        const extension = vscode.extensions.getExtension(EXTENSION);
        const original = fs.readFileSync(
            path.join(extension.extensionPath, '..', '..', '..', 'sqlflow', 'samples', 'reports', 'staging-overview.yaml'),
            'utf8'
        );
        const text = `notAKeyOfRpt: 1\n${original}`;
        const report = sample('flows/report-probe.yaml');
        await vscode.workspace.fs.writeFile(report, Buffer.from(text));
        try {
            const document = await open('flows/report-probe.yaml');
            const errors = await until('the language server\'s word on the report', () => {
                const found = vscode.languages.getDiagnostics(document.uri).filter((d) => d.severity === vscode.DiagnosticSeverity.Error);
                return found.length > 0 ? found : undefined;
            });
            assert.deepEqual(errors.map((d) => [d.range.start.line, d.message.includes('notAKeyOfRpt')]), [[0, true]], errors.map((d) => d.message).join('; '));

            const queriesLine = text.split(/\r?\n/).findIndex((line) => line.startsWith('queries:'));
            const hovers = await until('a hover on queries', async () => {
                const found = await vscode.commands.executeCommand('vscode.executeHoverProvider', document.uri, new vscode.Position(queriesLine, 2));
                return found?.length ? found : undefined;
            });
            assert.match(hoverText(hovers), /never as part of a flow/i);

            // A report is never run, so it offers no run, here or anywhere.
            const lenses = await vscode.commands.executeCommand('vscode.executeCodeLensProvider', document.uri);
            assert.ok(!lenses.some((lens) => lens.command?.command === 'sqlflow.runFlow'));
        } finally {
            await vscode.commands.executeCommand('workbench.action.closeAllEditors');
            await vscode.workspace.fs.delete(report);
        }
    },

    async 'Open Reports opens the Reports page in a tab'() {
        await vscode.commands.executeCommand('workbench.action.closeAllEditors');
        await vscode.commands.executeCommand('sqlflow.openReports');
        await until('the Reports page in a tab', () => pageTabs().length === 1);
        // Asked for again, it is the tab already on it.
        await vscode.commands.executeCommand('sqlflow.openReports');
        await new Promise((resolve) => setTimeout(resolve, 500));
        assert.equal(pageTabs().length, 1);
        await vscode.commands.executeCommand('workbench.action.closeAllEditors');
    },

    async 'the repository validates through the server, offline, and finds a broken file nobody has open'() {
        await vscode.commands.executeCommand('workbench.action.closeAllEditors');
        const broken = sample('flows/validate-probe.yaml');
        await vscode.workspace.fs.writeFile(broken, Buffer.from('flowType: delivery\nname: validate-probe\nnotAKey: 1\n'));
        try {
            await vscode.commands.executeCommand('sqlflow.refreshRepository');
            await vscode.commands.executeCommand('sqlflow.validateRepository');
            const flagged = vscode.languages.getDiagnostics(broken).filter((d) => d.source === 'OSDU Delivery');
            assert.ok(flagged.some((d) => d.message.includes('notAKey')), flagged.map((d) => d.message).join('; '));

            const errors = vscode.languages.getDiagnostics()
                .filter(([uri]) => uri.fsPath !== broken.fsPath)
                .flatMap(([uri, diagnostics]) => diagnostics
                    .filter((d) => d.source === 'OSDU Delivery' && d.severity === vscode.DiagnosticSeverity.Error)
                    .map((d) => `${path.basename(uri.fsPath)}: ${d.message}`));
            assert.deepEqual(errors, []);
        } finally {
            await vscode.workspace.fs.delete(broken);
        }
    },
};

exports.run = async () => {
    const failures = [];
    const extension = vscode.extensions.getExtension(EXTENSION);
    await extension.activate();
    if (process.env.SQLFLOW_CONTROL_PLANE_TOKEN) {
        Object.assign(checks, require('./online.cjs')(extension.exports.estate, pageTabs, until));
    }
    for (const [name, check] of Object.entries(checks)) {
        try {
            await check();
            console.log(`ok   ${name}`);
        } catch (err) {
            failures.push(name);
            console.log(`FAIL ${name}\n     ${err instanceof Error ? err.message : err}`);
        }
    }
    await vscode.commands.executeCommand('workbench.action.closeAllEditors');
    if (failures.length > 0) {
        throw new Error(`${failures.length} of ${Object.keys(checks).length} checks failed: ${failures.join('; ')}`);
    }
    console.log(`All ${Object.keys(checks).length} checks passed.`);
};
