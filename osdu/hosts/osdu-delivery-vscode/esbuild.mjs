import { build, context } from 'esbuild';
import { cpSync, existsSync, mkdirSync, rmSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

// The OSDU Delivery extension is SQLFlow's extension with the delivery module installed. SQLFlow's sources are compiled in
// place from the vendored tree, never copied, as the GUI's are, and every package either tree imports resolves from this
// build's node_modules, so sqlflow/tools/sqlflow-vscode needs no install of its own.

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

/** Stages the built binaries into bin/ when they exist (release preferred). A checkout finds them in their target
 * folders; a package has to hold them. */
function copyBinaries() {
    const exe = process.platform === 'win32' ? '.exe' : '';
    const bin = join(here, 'bin');
    mkdirSync(bin, { recursive: true });
    const programs = [
        ['osdu-delivery-mcp', join(repository, 'osdu', 'hosts', 'osdu-delivery-mcp', 'target')],
        ['sqlflow-lsp', join(repository, 'sqlflow', 'tools', 'target')],
    ];
    // The command line, which `npm run publish:cli` publishes as a single file for this machine's platform. It goes in
    // under the name it is documented by.
    const cli = join(here, 'publish', 'cli', `SqlFlow.Delivery.Cli.Host${exe}`);
    if (existsSync(cli)) {
        cpSync(cli, join(bin, `sqlflow${exe}`));
        console.log(`[esbuild] staged ${cli}`);
    } else if (production) {
        throw new Error('[esbuild] the sqlflow command line is not published, and a package has to hold it: npm run publish:cli.');
    }

    for (const [name, target] of programs) {
        const roots = [process.env.CARGO_TARGET_DIR, target].filter(Boolean);
        const built = roots
            .flatMap((root) => ['release', 'debug'].map((profile) => join(root, profile, `${name}${exe}`)))
            .find((candidate) => existsSync(candidate));
        if (built) {
            cpSync(built, join(bin, `${name}${exe}`));
            console.log(`[esbuild] staged ${built}`);
        } else if (production) {
            throw new Error(`[esbuild] ${name} is not built, and a package has to hold it.`);
        }
    }
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
    sourcemap: !production,
    minify: production,
    logLevel: 'info',
};

copyDocuments();
copyBinaries();
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
