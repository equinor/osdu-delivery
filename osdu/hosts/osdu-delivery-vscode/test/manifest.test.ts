import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { rowsOf } from '../src/jsonRows.ts';

const here = dirname(fileURLToPath(import.meta.url));
const manifest = JSON.parse(readFileSync(join(here, '..', 'package.json'), 'utf8'));
const sqlflow = JSON.parse(readFileSync(join(here, '..', '..', '..', '..', 'sqlflow', 'tools', 'sqlflow-vscode', 'package.json'), 'utf8'));

// VS Code reads a manifest as a file, so this product's cannot be composed the way its code is: it restates SQLFlow's
// contributions. These hold the two together, so a command SQLFlow adds cannot go missing here.
test('every command SQLFlow contributes is contributed here', () => {
    const here_ = new Set(manifest.contributes.commands.map((command: { command: string }) => command.command));
    const missing = sqlflow.contributes.commands.map((command: { command: string }) => command.command).filter((id: string) => !here_.has(id));
    assert.deepEqual(missing, []);
});

test('every setting SQLFlow contributes is contributed here, with the same scope', () => {
    for (const [name, setting] of Object.entries<{ scope?: string }>(sqlflow.contributes.configuration.properties)) {
        assert.equal(manifest.contributes.configuration.properties[name]?.scope, setting.scope, name);
        assert.ok(manifest.contributes.configuration.properties[name], name);
    }
});

test('every view SQLFlow contributes is contributed here', () => {
    const views = manifest.contributes.views.sqlflow.map((view: { id: string }) => view.id);
    for (const view of sqlflow.contributes.views.sqlflow) {
        assert.ok(views.includes(view.id), view.id);
    }
});

test('a workspace cannot name the programs this extension runs', () => {
    const settings = manifest.contributes.configuration.properties;
    for (const name of ['sqlflow.lspBinaryPath', 'sqlflow.mcpBinaryPath', 'sqlflow.cliPath']) {
        assert.equal(settings[name].scope, 'machine', name);
    }
});

test('an untrusted workspace cannot choose where a sign-in goes', () => {
    assert.deepEqual(manifest.capabilities.untrustedWorkspaces.restrictedConfigurations.sort(), ['sqlflow.controlPlaneUrl', 'sqlflow.guiUrl']);
});

test('every menu names a contributed command', () => {
    const commands = new Set(manifest.contributes.commands.map((command: { command: string }) => command.command));
    const named = Object.values<{ command: string }[]>(manifest.contributes.menus).flat().map((item) => item.command);
    assert.deepEqual(named.filter((id) => !commands.has(id)), []);
});

test('the language files match SQLFlow\'s, which they are copies of', () => {
    for (const file of ['syntaxes/sqlflow-flow.tmLanguage.json', 'language-configuration.json']) {
        const copy = readFileSync(join(here, '..', file), 'utf8');
        const original = readFileSync(join(here, '..', '..', '..', '..', 'sqlflow', 'tools', 'sqlflow-vscode', file), 'utf8');
        assert.equal(copy.replace(/\r\n/g, '\n'), original.replace(/\r\n/g, '\n'), file);
    }
});

test('an answer reads as rows, a named object by its name and an empty field left out', () => {
    const rows = rowsOf({
        total: 3,
        missing: null,
        links: { page: '/x' },
        interfaces: [{ name: 'wellbores', mapping: 'Wellbore@1.3.0' }],
        tags: ['a', 'b'],
    });

    assert.deepEqual(rows.map((row) => row.label), ['total', 'interfaces', 'tags']);
    assert.equal(rows[0].description, '3');
    assert.equal(rows[1].children?.[0].label, 'wellbores');
    assert.equal(rows[2].description, 'a, b');
});
