import { build, context } from 'esbuild';
import { execSync } from 'node:child_process';
import { cpSync, existsSync, mkdirSync, rmSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { languageClientOnMinimatch11 } from '../../../sqlflow/tools/sqlflow-vscode/languageClientMinimatch.mts';
import { shippedPrograms } from './programs.mts';

// The OSDU Delivery extension is SQLFlow's extension with the delivery module installed. SQLFlow's sources are compiled in
// place from the vendored tree, never copied, as the GUI's are, and every package either tree imports resolves from this
// build's node_modules, so sqlflow/tools/sqlflow-vscode needs no install of its own. SQLFlow's bundle rewrites
// vscode-languageclient's makeRe() checks for minimatch 11 (languageClientMinimatch.mts), and this one does the same.

const here = dirname(fileURLToPath(import.meta.url));
const repository = join(here, '..', '..', '..');
const watch = process.argv.includes('--watch');
const production = process.argv.includes('--production');

/** The documents the extension carries, in the layout they hold in the repository, so their links between each other work
 * offline in the markdown preview. */
function copyDocuments() {
    const corpus = join(here, 'dist', 'corpus');
    rmSync(corpus, { recursive: true, force: true });
    for (const folder of ['osdu/docs/reference', 'osdu/docs/wiki', 'sqlflow/docs/reference', 'sqlflow/docs/wiki']) {
        const source = join(repository, folder);
        if (existsSync(source)) {
            cpSync(source, join(corpus, folder), { recursive: true });
        }
    }

    const census = join(here, 'dist', 'census');
    rmSync(census, { recursive: true, force: true });
    mkdirSync(census, { recursive: true });
    cpSync(join(repository, 'osdu', 'docs', 'census'), census, {
        recursive: true,
        filter: (source) => !source.endsWith('.md'),
    });
}

/** Stages the built programs into bin/ when they exist. A checkout finds them in their target folders; a package has to
 * hold them, and holds release builds only (programs.mts). */
function copyBinaries() {
    const bin = join(here, 'bin');
    mkdirSync(bin, { recursive: true });
    for (const program of shippedPrograms({ production })) {
        if (program.built) {
            cpSync(program.built, join(bin, program.file));
            console.log(`[esbuild] staged ${program.built}`);
        } else if (production) {
            throw new Error(`[esbuild] ${program.name} has no release build, and a package has to hold it: node package.mjs builds it. Looked in ${program.candidates.join(', ')}.`);
        }
    }
}

/**
 * Builds the pages and the side bar the extension shows: the GUI's hosted build (osdu/gui, `npm run build:hosted`),
 * which writes into dist/hosted. A package always builds them anew, so it never carries pages older than its sources;
 * a checkout builds them when it has none, and `npm run bundle:hosted` builds them again after the GUI changed.
 */
function buildHostedPages() {
    if (!production && existsSync(join(here, 'dist', 'hosted', '.vite', 'manifest.json'))) {
        return;
    }
    const gui = join(repository, 'osdu', 'gui');
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
    nodePaths: [join(here, 'node_modules')],
    plugins: [languageClientOnMinimatch11],
    sourcemap: !production,
    minify: production,
    logLevel: 'info',
};

copyDocuments();
copyBinaries();
buildHostedPages();
// A package carries the licence the repository is under, from the one file that states it.
cpSync(join(repository, 'LICENSE'), join(here, 'LICENSE'));

if (watch) {
    const ctx = await context(options);
    await ctx.watch();
    console.log('[esbuild] watching…');
} else {
    await build(options);
    console.log('[esbuild] build complete');
}
