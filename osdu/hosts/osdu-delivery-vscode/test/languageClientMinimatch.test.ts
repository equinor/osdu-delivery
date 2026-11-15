import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { build, type Plugin } from 'esbuild';
import { languageClientOnMinimatch11, rewriteMakeReChecks } from '../../../../sqlflow/tools/sqlflow-vscode/languageClientMinimatch.mts';

// The extension resolves minimatch 11, past GHSA-g3hv-75wg-m249, under a vscode-languageclient that still calls makeRe(),
// which minimatch 11 removed. These run the client from this install, bundled as the extension bundles it.

const here = dirname(fileURLToPath(import.meta.url));
const client = join(here, '..', 'node_modules', 'vscode-languageclient', 'lib');

type MatchGlobPattern = (pattern: string, resource: { fsPath: string }) => boolean;

/** The client's glob test, which a diagnostic or notebook filter's pattern goes through, bundled with these plugins. */
async function bundledMatchGlobPattern(plugins: Plugin[]): Promise<MatchGlobPattern> {
    const result = await build({
        entryPoints: [join(client, 'common', 'utils', 'globPattern.js')],
        bundle: true,
        write: false,
        platform: 'node',
        format: 'cjs',
        external: ['vscode'],
        plugins,
        logLevel: 'silent',
    });
    const module = { exports: {} as { matchGlobPattern: MatchGlobPattern } };
    // The client reaches the vscode module only to build a Uri for a relative pattern, and a string pattern never does.
    const load = (id: string): unknown => {
        if (id === 'vscode') {
            return {};
        }
        throw new Error(`the bundle asked for ${id}, which it should hold itself`);
    };
    new Function('require', 'module', 'exports', result.outputFiles[0].text)(load, module, module.exports);
    return module.exports.matchGlobPattern;
}

test('no minimatch in the lock is older than 11.0.1, the release that fixed GHSA-g3hv-75wg-m249', () => {
    const lock = JSON.parse(readFileSync(join(here, '..', 'package-lock.json'), 'utf8')) as { packages: Record<string, { version: string }> };
    const older = Object.entries(lock.packages)
        .filter(([path]) => path === 'node_modules/minimatch' || path.endsWith('/node_modules/minimatch'))
        .filter(([, entry]) => {
            const [major, minor, patch] = entry.version.split('.').map(Number);
            return major < 11 || (major === 11 && minor === 0 && patch < 1);
        })
        .map(([path, entry]) => `${path} ${entry.version}`);
    assert.deepEqual(older, []);
});

test('the client as published calls makeRe(), which minimatch 11 removed', async () => {
    const matchGlobPattern = await bundledMatchGlobPattern([]);
    assert.throws(
        () => matchGlobPattern('**/*.sql', { fsPath: '/repo/flows/load.sql' }),
        /makeRe is not a function/,
        'vscode-languageclient no longer calls makeRe(): drop languageClientOnMinimatch11 and both extensions\' minimatch overrides',
    );
});

test('bundled with the rewrite, the client matches glob patterns on minimatch 11', async () => {
    const matchGlobPattern = await bundledMatchGlobPattern([languageClientOnMinimatch11]);
    assert.equal(matchGlobPattern('**/*.sql', { fsPath: '/repo/flows/load.sql' }), true);
    assert.equal(matchGlobPattern('**/*.sql', { fsPath: '/repo/flows/load.yaml' }), false);
    assert.equal(matchGlobPattern('/repo/{flows,mappings}/*.yaml', { fsPath: '/repo/mappings/well.yaml' }), true);
    // A comment leaves minimatch an empty set, for which makeRe() answered false: the pattern matches nothing.
    assert.equal(matchGlobPattern('#flows', { fsPath: '#flows' }), false);
});

test('every client module that calls makeRe() calls it in the form the rewrite knows', () => {
    const calling = readdirSync(client, { encoding: 'utf8', recursive: true })
        .filter((file) => file.endsWith('.js'))
        .filter((file) => /\bmakeRe\b/.test(readFileSync(join(client, file), 'utf8')));
    assert.ok(calling.length > 0, 'vscode-languageclient no longer calls makeRe(): drop the rewrite and the overrides');
    for (const file of calling) {
        assert.doesNotMatch(rewriteMakeReChecks(readFileSync(join(client, file), 'utf8'), file), /\bmakeRe\b/, file);
    }
});
