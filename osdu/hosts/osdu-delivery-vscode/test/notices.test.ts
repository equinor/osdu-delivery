import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { after, test } from 'node:test';
import {
    crateComponents,
    fileText,
    noticesText,
    npmComponents,
    nugetComponents,
    unlicensed,
    type CargoMetadata,
    type Component,
    type ProjectAssets,
} from '../notices.mts';

// What a package's THIRD-PARTY-NOTICES.txt is read from (notices.mts), over folders laid out the way npm, cargo and NuGet
// leave them.

const root = mkdtempSync(join(tmpdir(), 'osdu-notices-'));
after(() => rmSync(root, { recursive: true, force: true }));

function write(path: string, content: string | Buffer): void {
    mkdirSync(join(path, '..'), { recursive: true });
    writeFileSync(path, content);
}

test('an npm project ships its packages that are not for development, with the license each states and its license files', () => {
    const project = join(root, 'gui');
    write(join(project, 'package-lock.json'), JSON.stringify({
        lockfileVersion: 3,
        packages: {
            '': { name: 'gui' },
            'node_modules/a': { version: '1.0.0', license: 'MIT' },
            'node_modules/@scope/b': { version: '3.0.0' },
            'node_modules/vite': { version: '8.0.0', dev: true },
            'node_modules/only-elsewhere': { version: '1.0.0', optional: true },
            'node_modules/gui-lint': { link: true },
        },
    }));
    write(join(project, 'node_modules', 'a', 'package.json'), JSON.stringify({ name: 'a', license: 'MIT' }));
    write(join(project, 'node_modules', 'a', 'LICENSE'), 'MIT License\nCopyright (c) A');
    write(join(project, 'node_modules', '@scope', 'b', 'package.json'), JSON.stringify({ license: { type: 'ISC' }, repository: 'git+https://example.com/b.git' }));
    write(join(project, 'node_modules', '@scope', 'b', 'LICENSE.md'), 'ISC License');

    const found = npmComponents([project]).sort((x, y) => x.name.localeCompare(y.name));
    assert.deepEqual(found.map((component) => [component.name, component.version, component.license, component.source]), [
        ['@scope/b', '3.0.0', 'ISC', 'https://example.com/b'],
        ['a', '1.0.0', 'MIT', undefined],
    ]);
    assert.match(found[1].text, /^----- LICENSE -----\nMIT License\nCopyright \(c\) A$/);
});

test('an npm package the lock names but nobody installed stops the notices', () => {
    const project = join(root, 'uninstalled');
    write(join(project, 'package-lock.json'), JSON.stringify({ lockfileVersion: 3, packages: { '': {}, 'node_modules/missing': { version: '1.0.0' } } }));
    assert.throws(() => npmComponents([project]), /not installed: run npm ci/);
});

test('a program ships the crates its normal dependencies reach, never a build script\'s or a test\'s, nor the workspace\'s own', () => {
    const crate = (name: string, license: string | null) => ({
        id: `${name} 1.0.0 (registry)`, name, version: '1.0.0', source: 'registry+https://github.com/rust-lang/crates.io-index',
        license, license_file: null, repository: `https://example.com/${name}`, manifest_path: join(root, 'crates', name, 'Cargo.toml'),
    });
    const member = (name: string) => ({
        id: `${name} 0.1.0 (path)`, name, version: '0.1.0', source: null, license: 'GPL-3.0-or-later', license_file: null, repository: null,
        manifest_path: join(root, 'workspace', name, 'Cargo.toml'),
    });
    write(join(root, 'crates', 'serde', 'LICENSE-MIT'), 'MIT for serde');
    write(join(root, 'crates', 'serde', 'LICENSE-APACHE'), 'Apache for serde');
    const metadata: CargoMetadata = {
        packages: [member('server'), member('lang'), crate('serde', 'MIT OR Apache-2.0'), crate('memchr', 'MIT'), crate('proptest', 'MIT'), crate('cc', 'MIT')],
        resolve: {
            nodes: [
                { id: 'server 0.1.0 (path)', deps: [
                    { pkg: 'serde 1.0.0 (registry)', dep_kinds: [{ kind: null }] },
                    { pkg: 'lang 0.1.0 (path)', dep_kinds: [{ kind: null }] },
                    { pkg: 'proptest 1.0.0 (registry)', dep_kinds: [{ kind: 'dev' }] },
                    { pkg: 'cc 1.0.0 (registry)', dep_kinds: [{ kind: 'build' }] },
                ] },
                { id: 'lang 0.1.0 (path)', deps: [{ pkg: 'memchr 1.0.0 (registry)', dep_kinds: [{ kind: 'dev' }, { kind: null }] }] },
            ],
        },
    };

    const found = crateComponents(metadata, ['server']).sort((x, y) => x.name.localeCompare(y.name));
    assert.deepEqual(found.map((component) => [component.name, component.license]), [['memchr', 'MIT'], ['serde', 'MIT OR Apache-2.0']]);
    assert.equal(found[1].text, '----- LICENSE-APACHE -----\nApache for serde\n\n----- LICENSE-MIT -----\nMIT for serde');
    assert.throws(() => crateComponents(metadata, ['absent']), /names no crate absent/);
});

test('a published .NET program ships the packages that put something into it, and the runtime it carries', () => {
    const packages = join(root, 'nuget');
    write(join(packages, 'a', '1.0.0', 'a.nuspec'), '<package><metadata><license type="expression">MIT</license><projectUrl>https://example.com/a</projectUrl></metadata></package>');
    write(join(packages, 'a', '1.0.0', 'LICENSE.txt'), 'MIT for A');
    write(join(packages, 'b', '2.0.0', 'b.nuspec'), '<package><metadata><license type="file">docs\\EULA.txt</license></metadata></package>');
    write(join(packages, 'b', '2.0.0', 'docs', 'EULA.txt'), 'The terms of B');
    write(join(packages, 'microsoft.netcore.app.runtime.win-x64', '9.0.1', 'LICENSE.TXT'), 'MIT for the runtime');
    write(join(packages, 'microsoft.netcore.app.crossgen2.win-x64', '9.0.1', 'LICENSE.TXT'), 'MIT for a build tool');
    const assets: ProjectAssets = {
        targets: {
            'net9.0': {},
            'net9.0/win-x64': {
                'A/1.0.0': { type: 'package', runtime: { 'lib/net9.0/A.dll': {} } },
                'B/2.0.0': { type: 'package', native: { 'runtimes/win-x64/native/b.dll': {} } },
                'Analyzers/3.0.0': { type: 'package', runtime: { 'lib/netstandard2.0/_._': {} } },
                'SqlFlow.Core/1.0.0': { type: 'project', runtime: { 'bin/SqlFlow.Core.dll': {} } },
            },
        },
        libraries: { 'A/1.0.0': { type: 'package', path: 'a/1.0.0' }, 'B/2.0.0': { type: 'package', path: 'b/2.0.0' } },
        project: {
            frameworks: {
                'net9.0': { downloadDependencies: [
                    { name: 'Microsoft.NETCore.App.Runtime.win-x64', version: '[9.0.1]' },
                    { name: 'Microsoft.NETCore.App.Crossgen2.win-x64', version: '[9.0.1]' },
                ] },
            },
        },
    };

    const found = nugetComponents(assets, packages);
    assert.deepEqual(found.map((component) => [component.name, component.version, component.license]), [
        ['A', '1.0.0', 'MIT'],
        ['B', '2.0.0', 'see docs\\EULA.txt'],
        ['Microsoft.NETCore.App.Runtime.win-x64', '9.0.1', 'MIT'],
    ]);
    assert.equal(found[0].source, 'https://example.com/a');
    assert.match(found[1].text, /The terms of B/);
    assert.match(found[2].text, /MIT for the runtime/);
});

test('a license file is read as text whatever it was written in', () => {
    const utf16 = Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from('MIT License\r\nCopyright A', 'utf16le')]);
    assert.equal(fileText('LICENSE', utf16), 'MIT License\nCopyright A');
    assert.equal(fileText('LICENSE', Buffer.from('﻿MIT\u0000 License', 'utf8')), 'MIT License');
    const rtf = '{\\rtf1\\ansi{\\fonttbl{\\f0\\fswiss Tahoma;}}{\\*\\generator Riched20;}\\pard\\f0 THIRD-PARTY NOTICES\\par Caf\\\'e9 \\{1\\}\\par}';
    assert.equal(fileText('ThirdPartyNotices.rtf', Buffer.from(rtf)), 'THIRD-PARTY NOTICES\nCafé {1}');
});

test('the notices write a text several components share once, and name what states no license at all', () => {
    const component = (name: string, text: string, license = 'MIT'): Component => ({ name, version: '1.0.0', license, text });
    const sections = [{ title: 'npm packages', about: 'Bundled.', components: [component('b', 'Same text'), component('a', 'Same text'), component('c', '', 'Apache-2.0')] }];
    const text = noticesText(sections);
    assert.equal(text.split('Same text').length - 1, 1);
    assert.match(text, /Its license and notices are word for word those of a 1\.0\.0, above\./);
    assert.match(text, /No license file ships with this component\. It is under Apache-2\.0/);
    assert.deepEqual(unlicensed(sections), []);
    assert.deepEqual(unlicensed([{ title: 'Rust crates', about: '', components: [component('quiet', '', '')] }]), ['Rust crates: quiet 1.0.0']);
});
