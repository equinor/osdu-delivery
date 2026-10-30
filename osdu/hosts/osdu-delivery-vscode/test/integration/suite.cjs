const assert = require('node:assert/strict');
const path = require('node:path');
const vscode = require('vscode');

const EXTENSION = 'business-iq.osdu-delivery';

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

const checks = {
    async 'the extension activates and contributes its views'() {
        const extension = vscode.extensions.getExtension(EXTENSION);
        assert.ok(extension, 'the extension is installed');
        await extension.activate();
        assert.ok(extension.packageJSON.contributes.views.sqlflow.some((view) => view.id === 'osduDelivery'));
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
        const answer = await extension.exports.estate.mcp.call('get_cli');
        assert.ok(answer.path.endsWith(`sqlflow${exe}`), answer.path);
        assert.ok(answer.conventions.includes('--json'));
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

    async 'a flow carries its actions at the top'() {
        const document = await open('flows/welldb-wellbore-03-delivery.yaml');
        const lenses = await vscode.commands.executeCommand('vscode.executeCodeLensProvider', document.uri);
        assert.ok(lenses.some((lens) => lens.command?.command === 'sqlflow.runFlow'));
    },

    async 'a mapping offers its check'() {
        const document = await open('mappings/Wellbore@1.3.0.yaml');
        const lenses = await vscode.commands.executeCommand('vscode.executeCodeLensProvider', document.uri);
        assert.ok(lenses.some((lens) => lens.command?.command === 'osduDelivery.checkMapping'));
    },

    async 'the views render with nobody signed in'() {
        await vscode.commands.executeCommand('workbench.view.extension.sqlflow');
        await new Promise((resolve) => setTimeout(resolve, 1000));
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
        Object.assign(checks, require('./online.cjs')(extension.exports.estate));
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
