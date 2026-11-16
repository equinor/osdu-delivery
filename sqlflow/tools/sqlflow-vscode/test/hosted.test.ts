import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { hostedEntry, readHostedBuild } from '../src/gui/hostedFiles.ts';
import { hostedCsp, hostedHtml, messageHtml } from '../src/gui/hostedHtml.ts';
import { sessionProblemText } from '../src/gui/sessionText.ts';

// A manifest as the bundler writes one: two entries, what they share in a chunk of its own, and a page loaded later.
const manifest = {
    '_shared-abc.js': { file: 'chunks/shared-abc.js', css: ['assets/shared-abc.css'] },
    '_tree-def.js': { file: 'chunks/tree-def.js', imports: ['_shared-abc.js'] },
    'src/embed/page.tsx': { file: 'page.js', isEntry: true, imports: ['_shared-abc.js'], css: ['assets/page-123.css'], dynamicImports: ['src/pages/Runs.tsx'] },
    'src/embed/side-bar.tsx': { file: 'side-bar.js', isEntry: true, imports: ['_tree-def.js', '_shared-abc.js'] },
    'src/pages/Runs.tsx': { file: 'chunks/Runs-456.js', css: ['assets/Runs-456.css'], isDynamicEntry: true },
};

test('an entry loads its own stylesheets and those of what it imports, the shared ones first and each once', () => {
    assert.deepEqual(hostedEntry(manifest, 'page.js'), { script: 'page.js', styles: ['assets/shared-abc.css', 'assets/page-123.css'] });
    assert.deepEqual(hostedEntry(manifest, 'side-bar.js'), { script: 'side-bar.js', styles: ['assets/shared-abc.css'] });
});

test('an entry the build does not have is said to be missing', () => {
    assert.throws(() => hostedEntry(manifest, 'other.js'), /no entry 'other.js'/);
    assert.throws(() => hostedEntry({ 'x.ts': { file: 'page.js' } }, 'page.js'), /no entry 'page.js'/);
});

test('a build is read from its folder, and a folder without one says how to make it', () => {
    const folder = mkdtempSync(join(tmpdir(), 'sqlflow-hosted-'));
    try {
        assert.throws(() => readHostedBuild(folder), /not built[\s\S]*bundle script/);
        mkdirSync(join(folder, '.vite'));
        writeFileSync(join(folder, '.vite', 'manifest.json'), JSON.stringify(manifest));
        const build = readHostedBuild(folder);
        assert.equal(build.folder, folder);
        assert.equal(build.page.script, 'page.js');
        assert.equal(build.sideBar.script, 'side-bar.js');
    } finally {
        rmSync(folder, { recursive: true, force: true });
    }
});

const page = {
    entry: { script: 'page.js', styles: ['assets/a.css', 'assets/b.css'] },
    baseUri: 'https://file.resource.example/ext/dist/hosted',
    cspSource: 'https://*.resource.example',
    nonce: 'n0nce',
    title: 'A product',
    path: '/runs/1?tab=trace',
    surface: 'page' as const,
};

test('a hosted page may reach the extension\'s files and nothing else', () => {
    const csp = hostedCsp('https://*.resource.example', 'n0nce');
    const directives = new Map(csp.split('; ').map((directive) => [directive.split(' ')[0], directive.split(' ').slice(1)]));

    assert.deepEqual(directives.get('default-src'), ["'none'"]);
    assert.deepEqual(directives.get('script-src'), ["'nonce-n0nce'", 'https://*.resource.example']);
    assert.deepEqual(directives.get('connect-src'), ['https://*.resource.example', 'blob:', 'data:']);
    assert.deepEqual(directives.get('worker-src'), ['blob:', 'data:']);
    assert.deepEqual(directives.get('base-uri'), ["'none'"]);
    assert.deepEqual(directives.get('form-action'), ["'none'"]);
    assert.deepEqual(directives.get('frame-src'), ["'none'"]);
    // Every source named is the extension's own or what the page holds in memory: no other host, and no way to run text
    // as a script.
    for (const [name, sources] of directives) {
        for (const source of sources) {
            assert.ok(
                ["'none'", "'nonce-n0nce'", "'unsafe-inline'", 'https://*.resource.example', 'blob:', 'data:'].includes(source),
                `${name} admits ${source}`
            );
        }
    }
    assert.ok(!directives.get('script-src')!.includes("'unsafe-inline'"));
});

test('the page names its one script by a nonce, and loads its stylesheets from the build', () => {
    const html = hostedHtml(page);

    assert.equal(html.match(/<script/g)?.length, 1);
    assert.ok(html.includes('<script type="module" nonce="n0nce" src="https://file.resource.example/ext/dist/hosted/page.js"></script>'));
    assert.equal(html.match(/<link rel="stylesheet"/g)?.length, 2);
    assert.ok(html.indexOf('assets/a.css') < html.indexOf('assets/b.css'));
    assert.ok(html.includes('<div id="root" data-path="/runs/1?tab=trace"></div>'));
    assert.ok(html.includes('Content-Security-Policy'));
});

test('what is written into the page is escaped, whatever it holds', () => {
    const html = hostedHtml({ ...page, title: '</title><script>alert(1)</script>', path: '/x"><script>alert(2)</script>' });

    assert.equal(html.match(/<script/g)?.length, 1);
    assert.ok(html.includes('&lt;/title&gt;&lt;script&gt;alert(1)&lt;/script&gt;'));
    assert.ok(html.includes('data-path="/x&quot;&gt;&lt;script&gt;alert(2)&lt;/script&gt;"'));
});

test('the side bar stands on the side bar\'s ground and is given no page', () => {
    const html = hostedHtml({ ...page, surface: 'side-bar', path: undefined, entry: { script: 'side-bar.js', styles: [] } });

    assert.ok(html.includes('background: var(--vscode-sideBar-background)'));
    assert.ok(html.includes('<div id="root"></div>'));
    assert.ok(!html.includes('<link rel="stylesheet"'));
});

test('a view that cannot load says why, and runs and loads nothing', () => {
    const html = messageHtml('The pages are not built: <run> the "bundle" script.');

    assert.ok(html.includes('The pages are not built: &lt;run&gt; the &quot;bundle&quot; script.'));
    assert.ok(html.includes("default-src 'none'"));
    assert.ok(!html.includes('<script') && !html.includes('<link'));
});

test('why nobody is signed in is said for a person, and the instruction to an assistant is left out', () => {
    const unreachable = 'Could not verify authentication with the control plane: could not reach the control plane: error sending request for url '
        + '(http://127.0.0.1:1/api/v1/me): client error (Connect): tcp connect error. Check check_connectivity, then try again.';
    assert.equal(
        sessionProblemText(unreachable, 'http://127.0.0.1:1'),
        'The control plane at http://127.0.0.1:1 did not answer. Check the address, and that it is running.'
    );
    assert.equal(
        sessionProblemText('The stored session ended: A session renews for at most 30 days after signing in. Call login to sign in again.', 'https://cp'),
        'The stored session ended: A session renews for at most 30 days after signing in.'
    );
    assert.equal(sessionProblemText(undefined, 'https://cp'), null);
    assert.equal(sessionProblemText('  ', 'https://cp'), null);
});
