import assert from 'node:assert/strict';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const folder = join(here, '..');
const manifest = JSON.parse(readFileSync(join(folder, 'package.json'), 'utf8'));
const sqlflowFolder = join(here, '..', '..', '..', '..', 'sqlflow', 'tools', 'sqlflow-vscode');
const sqlflow = JSON.parse(readFileSync(join(sqlflowFolder, 'package.json'), 'utf8'));

type Menus = Record<string, { command: string }[]>;

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

test('the side bar is one drawn view, in both manifests, and neither contributes a tree of the editor\'s own', () => {
    for (const contributes of [manifest.contributes, sqlflow.contributes]) {
        const views = Object.values<{ id: string; type?: string }[]>(contributes.views).flat();
        assert.deepEqual(views.map((view) => [view.id, view.type]), [['sqlflow.sideBar', 'webview']]);
        // A welcome text is how the editor fills an empty tree; a drawn view says what it has to say itself.
        assert.equal(contributes.viewsWelcome, undefined);
    }
});

test('a page\'s tab wakes the extension, so the tabs open when the editor closed come back', () => {
    for (const events of [manifest.activationEvents, sqlflow.activationEvents]) {
        assert.ok(events.includes('onWebviewPanel:sqlflow.page'));
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

test('every menu and key names a contributed command, in both manifests', () => {
    for (const contributes of [manifest.contributes, sqlflow.contributes]) {
        const commands = new Set(contributes.commands.map((command: { command: string }) => command.command));
        const named = [
            ...Object.values(contributes.menus as Menus).flat().map((item) => item.command),
            ...(contributes.keybindings ?? []).map((key: { command: string }) => key.command),
        ];
        assert.deepEqual(named.filter((id) => !commands.has(id)), []);
    }
});

test('every command a host registers is one its manifest contributes, and the other way round', () => {
    const registered = (file: string): string[] =>
        [...readFileSync(file, 'utf8').matchAll(/\b(?:register|command)\('((?:sqlflow|osduDelivery)\.[A-Za-z]+)'/g)].map((match) => match[1]);
    const library = registered(join(sqlflowFolder, 'src', 'host.ts'));
    const module = registered(join(folder, 'src', 'deliveryModule.ts'));
    const contributed = (from: { contributes: { commands: { command: string }[] } }): string[] => from.contributes.commands.map((command) => command.command).sort();

    assert.deepEqual([...library].sort(), contributed(sqlflow));
    assert.deepEqual([...library, ...module].sort(), contributed(manifest));
});

test('the language files match SQLFlow\'s, which they are copies of', () => {
    for (const file of ['syntaxes/sqlflow-flow.tmLanguage.json', 'language-configuration.json']) {
        const copy = readFileSync(join(folder, file), 'utf8');
        const original = readFileSync(join(sqlflowFolder, file), 'utf8');
        assert.equal(copy.replace(/\r\n/g, '\n'), original.replace(/\r\n/g, '\n'), file);
    }
});

// What the Marketplace needs of a package, and refuses one without.
test('the package says what the Marketplace shows of it', () => {
    // DeltaForge's publisher, whose token the release workflow publishes with: an extension's id never changes.
    assert.equal(manifest.publisher, 'delta-forge');
    assert.match(manifest.version, /^\d+\.\d+\.\d+$/);
    assert.ok(manifest.displayName && manifest.description.length <= 250, 'a name and a description that fits a listing');
    assert.ok(manifest.repository?.url && manifest.bugs?.url && manifest.homepage && manifest.license);
    assert.ok(Array.isArray(manifest.keywords) && manifest.keywords.length > 0 && manifest.keywords.length <= 30, 'at most thirty keywords');
    assert.equal(new Set(manifest.keywords).size, manifest.keywords.length, 'no keyword twice');
    assert.match(manifest.galleryBanner.color, /^#[0-9a-f]{6}$/i);
    const categories = ['Programming Languages', 'Snippets', 'Linters', 'Themes', 'Debuggers', 'Formatters', 'Keymaps', 'SCM Providers', 'Other',
        'Extension Packs', 'Language Packs', 'Data Science', 'Machine Learning', 'Visualization', 'Notebooks', 'Education', 'Testing', 'AI', 'Chat'];
    for (const category of manifest.categories) {
        assert.ok(categories.includes(category), category);
    }
});

test('the icon is a PNG of at least 128 pixels a side, which the Marketplace asks for', () => {
    assert.match(manifest.icon, /\.png$/);
    const icon = readFileSync(join(folder, manifest.icon));
    assert.equal(icon.subarray(1, 4).toString('latin1'), 'PNG');
    const [width, height] = [icon.readUInt32BE(16), icon.readUInt32BE(20)];
    assert.equal(width, height, 'square');
    assert.ok(width >= 128, `${width} pixels`);
});

test('the activity bar and the side bar carry the product\'s own mark, not the glyph a flow\'s file carries', () => {
    const container = manifest.contributes.viewsContainers.activitybar[0];
    const view = manifest.contributes.views.sqlflow[0];
    const flowGlyph = manifest.contributes.languages.find((language: { id: string }) => language.id === 'sqlflow-flow').icon.dark;
    for (const icon of [container.icon, view.icon]) {
        assert.equal(icon, './resources/osdu-delivery.svg');
        assert.notEqual(icon, flowGlyph);
        const mark = readFileSync(join(folder, icon), 'utf8');
        // The editor draws an activity bar icon as a mask in the theme's colour: one colour, in a box of 24, with no
        // picture or page of its own to fetch.
        assert.match(mark, /^<svg [^>]*viewBox="0 0 24 24"[^>]*fill="currentColor"/);
        assert.doesNotMatch(mark, /<image|href=|#[0-9a-f]{3,6}\b/i);
    }
});

test('the package carries a readme and a change log, and every picture and page they and the walkthrough name', () => {
    for (const file of ['README.md', 'CHANGELOG.md']) {
        assert.ok(existsSync(join(folder, file)), file);
    }
    const readme = readFileSync(join(folder, 'README.md'), 'utf8');
    // The Marketplace shows a picture only from an https address; a path into the repository would show nothing there.
    for (const [, source] of readme.matchAll(/!\[[^\]]*\]\(([^)\s]+)/g)) {
        assert.match(source, /^https:\/\/.+\.(png|gif|jpg)$/, source);
        // A picture of this folder is fetched from main, so it has to be in the folder to be there once this lands.
        const own = source.match(/^https:\/\/raw\.githubusercontent\.com\/TahirRiaz\/osdu-delivery\/main\/osdu\/hosts\/osdu-delivery-vscode\/(.+)$/);
        if (own) {
            assert.ok(existsSync(join(folder, own[1])), own[1]);
        }
    }
    for (const walkthrough of manifest.contributes.walkthroughs) {
        for (const step of walkthrough.steps) {
            assert.ok(existsSync(join(folder, step.media.markdown)), step.media.markdown);
        }
    }
    assert.match(readFileSync(join(folder, 'CHANGELOG.md'), 'utf8'), new RegExp(`^## \\[${manifest.version.replace(/\./g, '\\.')}\\]`, 'm'));
});

// Publishing is a person's decision, taken per release. Nothing builds or publishes the extension on a push, a merge,
// a tag, a release or a schedule, and no other workflow starts the two that do.
test('the extension is built and published only when a person starts the workflow', () => {
    const workflows = join(folder, '..', '..', '..', '.github', 'workflows');
    /** The events a workflow starts on: the keys under its top-level `on:`. */
    const startsOn = (file: string): string[] => {
        const lines = readFileSync(join(workflows, file), 'utf8').split(/\r?\n/);
        const from = lines.findIndex((line) => /^on:\s*$/.test(line));
        assert.ok(from >= 0, `${file} names what starts it under a top-level 'on:'`);
        const events: string[] = [];
        for (const line of lines.slice(from + 1)) {
            if (/^\S/.test(line)) {
                break;
            }
            const event = /^ {2}([a-z_]+):/.exec(line);
            if (event) {
                events.push(event[1]);
            }
        }
        return events;
    };
    for (const file of ['vscode-build.yml', 'vscode-publish.yml']) {
        assert.deepEqual(startsOn(file), ['workflow_dispatch'], file);
    }
    for (const file of readdirSync(workflows).filter((name) => /\.ya?ml$/.test(name))) {
        const text = readFileSync(join(workflows, file), 'utf8');
        if (file !== 'vscode-publish.yml') {
            assert.doesNotMatch(text, /\b(vsce|ovsx)\b[^\n]*\bpublish\b/, `${file} publishes nothing to a marketplace`);
        }
        assert.doesNotMatch(text, /gh\s+workflow\s+run\s+\S*vscode-(build|publish)/, `${file} starts neither release workflow`);
        assert.doesNotMatch(text, /vscode-(build|publish)\.yml\/dispatches/, `${file} starts neither release workflow`);
        assert.doesNotMatch(text, /uses:\s*\.\/\.github\/workflows\/vscode-(build|publish)\.yml/, `${file} calls neither release workflow`);
    }
});

// OSDU is a registered trademark of The Open Group, which asks for the mark with its symbol at its first use and an
// attribution naming it. The product is independent of them, so its listing says so: "powered by SQLFlow" says whose it is.
test('the listing names the product as powered by SQLFlow, and attributes the OSDU trademark to The Open Group', () => {
    assert.equal(manifest.displayName, 'OSDU Delivery, powered by SQLFlow');
    assert.match(manifest.description, /OSDU® data platform/);
    const readme = readFileSync(join(folder, 'README.md'), 'utf8');
    assert.match(readme, /^# OSDU Delivery, powered by SQLFlow$/m);
    const first = readme.indexOf('OSDU', readme.indexOf('\n'));
    assert.equal(readme.slice(first, first + 5), 'OSDU®', 'the first use of the mark after the heading carries its symbol');
    assert.match(readme, /^## Trademarks$/m);
    assert.match(readme, /OSDU is a registered trademark of The Open Group\./);
    assert.match(readme, /not affiliated with, sponsored by, endorsed by or certified by The Open Group/);
});

test('a package leaves out its sources and what a hosted page never loads, and keeps what it runs', () => {
    const ignored = readFileSync(join(folder, '.vscodeignore'), 'utf8').split(/\r?\n/).filter((line) => line !== '');
    for (const pattern of ['src/**', 'test/**', 'node_modules/**', 'dist/hosted/assets/*.wasm', 'dist/hosted/assets/*.worker-*.js']) {
        assert.ok(ignored.includes(pattern), pattern);
    }
    assert.ok(ignored.includes('!dist/hosted/.vite/manifest.json'), 'the list of the hosted build\'s files, which the extension reads');
    for (const kept of ['dist/**', 'bin/**', 'resources/**', 'dist/hosted/**', 'THIRD-PARTY-NOTICES.txt', 'LICENSE']) {
        assert.ok(!ignored.includes(kept), kept);
    }
    // The packager's own scripts and the signing configuration are a release's, not the extension's.
    for (const script of ['package.mjs', 'notices.mts', 'programs.mts', 'signing/**']) {
        assert.ok(ignored.includes(script), script);
    }
});
