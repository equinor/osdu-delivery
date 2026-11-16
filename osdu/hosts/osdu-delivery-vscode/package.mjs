import { execFileSync, execSync } from 'node:child_process';
import { existsSync, readFileSync, rmSync, statSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

// Packages the extension for one platform: `node package.mjs [--target <platform>] [--skip-programs]`.
//
// A package carries three programs built for the platform it installs on (the language server, the MCP server and the
// sqlflow command line), so the Marketplace holds one package per platform and gives each editor its own. This builds
// the three for the platform of the machine it runs on and packages them under that platform's name; the release
// workflow runs it once on a runner of each platform. `--skip-programs` packages the programs already built, for a
// second package from the same build.

const here = dirname(fileURLToPath(import.meta.url));
const repository = join(here, '..', '..', '..');

/** A Marketplace platform, the .NET runtime the command line is published for, and the machine that can build it. */
const PLATFORMS = {
    'win32-x64': { runtime: 'win-x64', platform: 'win32', arch: 'x64' },
    'linux-x64': { runtime: 'linux-x64', platform: 'linux', arch: 'x64' },
    'darwin-arm64': { runtime: 'osx-arm64', platform: 'darwin', arch: 'arm64' },
    'darwin-x64': { runtime: 'osx-x64', platform: 'darwin', arch: 'x64' },
};

function argument(name) {
    const at = process.argv.indexOf(name);
    return at < 0 ? undefined : process.argv[at + 1];
}

const target = argument('--target') ?? `${process.platform}-${process.arch}`;
const platform = PLATFORMS[target];
if (platform === undefined) {
    throw new Error(`'${target}' is not a platform this extension is packaged for: ${Object.keys(PLATFORMS).join(', ')}.`);
}
// The Rust programs are built by the machine's own toolchain, so a package is built on the platform it is for.
if (platform.platform !== process.platform || platform.arch !== process.arch) {
    throw new Error(`A ${target} package is built on a ${target} machine; this one is ${process.platform}-${process.arch}.`);
}

const run = (command, cwd = here) => {
    console.log(`[package] ${command}`);
    execSync(command, { cwd, stdio: 'inherit' });
};

if (!process.argv.includes('--skip-programs')) {
    run('cargo build --release --manifest-path sqlflow/tools/Cargo.toml -p sqlflow-lsp', repository);
    run('cargo build --release --manifest-path osdu/hosts/osdu-delivery-mcp/Cargo.toml', repository);
    // A file of the last platform's command line would be staged as this one's: the folder holds one platform at a time.
    rmSync(join(here, 'publish', 'cli'), { recursive: true, force: true });
    run(
        'dotnet publish ../SqlFlow.Delivery.Cli.Host/SqlFlow.Delivery.Cli.Host.csproj -c Release '
        + `-r ${platform.runtime} --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        + '-p:EnableCompressionInSingleFile=true -m:1 -o publish/cli'
    );
}

const manifest = JSON.parse(readFileSync(join(here, 'package.json'), 'utf8'));
const out = join(here, `${manifest.name}-${target}-${manifest.version}.vsix`);
rmSync(out, { force: true });

// vsce runs vscode:prepublish itself, which bundles the extension, builds the pages it shows and stages the programs,
// and stops when one of them is missing. --no-dependencies: the bundle holds every package the extension imports.
const vsce = join(here, 'node_modules', '@vscode', 'vsce', 'vsce');
execFileSync(process.execPath, [vsce, 'package', '--target', target, '--no-dependencies', '--out', out], { cwd: here, stdio: 'inherit' });

if (!existsSync(out)) {
    throw new Error(`[package] vsce reported no error and wrote no ${out}.`);
}
console.log(`[package] ${out} (${(statSync(out).size / (1024 * 1024)).toFixed(1)} MB)`);
