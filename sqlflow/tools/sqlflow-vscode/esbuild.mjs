import { build, context } from 'esbuild';
import { execSync } from 'node:child_process';
import { cpSync, existsSync, mkdirSync, rmSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { languageClientOnMinimatch11 } from './languageClientMinimatch.mts';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(here, '..', '..');
const watch = process.argv.includes('--watch');
const production = process.argv.includes('--production');

/** Copy the reference corpus into dist so the docs browser works offline. */
function copyDocs() {
    const src = join(repoRoot, 'docs', 'reference');
    const dst = join(here, 'dist', 'reference');
    if (!existsSync(src)) {
        console.warn(`[esbuild] docs/reference not found at ${src}; docs browser will be empty.`);
        return;
    }
    rmSync(dst, { recursive: true, force: true });
    mkdirSync(dst, { recursive: true });
    cpSync(src, dst, { recursive: true });
    console.log('[esbuild] copied docs/reference -> dist/reference');
}

/** Stage the built Rust binaries into bin/ if they exist (release preferred). */
function copyBinaries() {
    const exe = process.platform === 'win32' ? '.exe' : '';
    const bin = join(here, 'bin');
    mkdirSync(bin, { recursive: true });
    for (const name of ['sqlflow-lsp', 'sqlflow-mcp']) {
        for (const profile of ['release', 'debug']) {
            const candidate = join(here, '..', 'target', profile, `${name}${exe}`);
            if (existsSync(candidate)) {
                cpSync(candidate, join(bin, `${name}${exe}`));
                console.log(`[esbuild] staged ${name} (${profile})`);
                break;
            }
        }
    }
}

/**
 * Builds the pages and the side bar the extension shows: the GUI's hosted build (`npm run build:hosted` in gui/),
 * which writes into dist/hosted. A package always builds them anew; a checkout builds them when it has none, and
 * `npm run bundle:hosted` builds them again after the GUI changed.
 */
function buildHostedPages() {
    if (!production && existsSync(join(here, 'dist', 'hosted', '.vite', 'manifest.json'))) {
        return;
    }
    const gui = join(repoRoot, 'gui');
    if (!existsSync(join(gui, 'node_modules'))) {
        throw new Error(`[esbuild] the pages are built from ${gui}, which has no packages installed: run npm ci there first.`);
    }
    console.log('[esbuild] building the hosted pages');
    execSync('npm run build:hosted', { cwd: gui, stdio: 'inherit' });
}

const options = {
    entryPoints: [join(here, 'src', 'extension.ts')],
    bundle: true,
    outfile: join(here, 'dist', 'extension.js'),
    platform: 'node',
    target: 'node18',
    format: 'cjs',
    external: ['vscode'],
    plugins: [languageClientOnMinimatch11],
    sourcemap: !production,
    minify: production,
    logLevel: 'info',
};

copyDocs();
copyBinaries();
buildHostedPages();

if (watch) {
    const ctx = await context(options);
    await ctx.watch();
    console.log('[esbuild] watching…');
} else {
    await build(options);
    console.log('[esbuild] build complete');
}
