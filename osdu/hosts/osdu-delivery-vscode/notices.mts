import { execFileSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

// The third-party notices a package carries (THIRD-PARTY-NOTICES.txt): every component of another party the package
// ships, with its license as the component states it. Three kinds ship: the npm packages bundled into the pages and the
// extension, the Rust crates compiled into the language server and the MCP server, and the NuGet packages and .NET
// runtime published into the sqlflow command line. Each is read offline from what its build left: the package locks and
// node_modules, `cargo metadata` and the crates' own folders, and the command line's restore file and NuGet's package
// folder. The packager writes the file before every package (package.mjs); `node notices.mts <target> [file]` writes it
// alone. A component that states no license at all stops it: shipping one is a decision, not a default.

const here = dirname(fileURLToPath(import.meta.url));
const repository = join(here, '..', '..', '..');

/** A component of another party that a package ships. */
export interface Component {
    name: string;
    version: string;
    /** The license it declares (an SPDX expression, a URL, or the name of its license file); empty when it declares none. */
    license: string;
    /** Where its source is, when it says. */
    source?: string;
    /** The text of its license and notice files, each under its file's name; empty when it ships none. */
    text: string;
}

/** The files a component's license, copyright and notices are in, at the top of its folder. */
const LICENSE_FILE = /^(licen[cs]e|copying|copyright|notice|unlicense|third-?party-?notices)([-._ ].*)?$/i;
/** Larger than this is no license file; a component that names one this size is shown by its name. */
const LARGEST_TEXT = 512 * 1024;

function readJson<T>(file: string): T | undefined {
    try {
        return JSON.parse(readFileSync(file, 'utf8')) as T;
    } catch {
        return undefined;
    }
}

/** The words of a rich text file, without its control words and groups: a notices file some vendors ship as RTF. */
export function rtfText(rtf: string): string {
    // A brace or backslash written as itself (\{ \} \\) is set aside first, so taking the groups' braces leaves it.
    const kept: Record<string, string> = { '{': '\u0001', '}': '\u0002', '\\': '\u0003' };
    return rtf
        .replace(/\\([{}\\])/g, (_, character: string) => kept[character])
        .replace(/\{\\(fonttbl|colortbl|stylesheet|info)(\{[^{}]*\}|[^{}])*\}/g, '')
        .replace(/\{\\\*[^{}]*\}/g, '')
        .replace(/\\'([0-9a-f]{2})/gi, (_, hex: string) => String.fromCharCode(parseInt(hex, 16)))
        .replace(/\\(par|line)\b ?/g, '\n')
        .replace(/\\[a-z]+-?\d* ?/gi, '')
        .replace(/[{}]/g, '')
        .replace(/\u0001/g, '{')
        .replace(/\u0002/g, '}')
        .replace(/\u0003/g, '\\')
        .replace(/[ \t]+\n/g, '\n')
        .replace(/\n{3,}/g, '\n\n');
}

/** A license file as text, whatever form it was written in: UTF-8 or UTF-16, plain or rich text. */
export function fileText(name: string, bytes: Buffer): string {
    let text: string;
    if (bytes[0] === 0xff && bytes[1] === 0xfe) {
        text = bytes.subarray(2).toString('utf16le');
    } else if (bytes[0] === 0xfe && bytes[1] === 0xff) {
        const swapped = Buffer.from(bytes.subarray(2));
        swapped.swap16();
        text = swapped.toString('utf16le');
    } else {
        text = bytes.toString('utf8').replace(/^﻿/, '');
    }
    text = text.replace(/\u0000/g, '').replace(/\r\n?/g, '\n');
    return (name.toLowerCase().endsWith('.rtf') ? rtfText(text) : text).trim();
}

/**
 * The text of the license files at the top of a folder, and of any other file the component names, each under its name.
 * A component names a file inside it with either separator (a NuGet package writes `docs\LICENSE.txt`), and the name
 * is read the same on every platform a package is built on.
 */
export function licenseTexts(folder: string, named: string[] = []): string {
    let entries: string[] = [];
    try {
        entries = readdirSync(folder);
    } catch {
        return '';
    }
    const inside = named.map((file) => file.replace(/\\/g, '/'));
    const files = [...new Set([...entries.filter((entry) => LICENSE_FILE.test(entry)).sort(), ...inside])];
    const texts: string[] = [];
    for (const file of files) {
        const path = join(folder, ...file.split('/'));
        try {
            const stat = statSync(path);
            if (stat.isFile() && stat.size <= LARGEST_TEXT) {
                texts.push(`----- ${file} -----\n${fileText(file, readFileSync(path))}`);
            }
        } catch {
            // A file the component names that is not there: its declared license still says what applies.
        }
    }
    return texts.join('\n\n');
}

// ---- npm --------------------------------------------------------------------------------------------------------------

interface LockEntry {
    version?: string;
    license?: string;
    dev?: boolean;
    devOptional?: boolean;
    optional?: boolean;
    link?: boolean;
}

interface PackageJson {
    license?: string | { type?: string };
    licenses?: (string | { type?: string })[];
    repository?: string | { url?: string };
}

function declaredNpmLicense(pkg: PackageJson | undefined): string {
    if (pkg === undefined) {
        return '';
    }
    if (typeof pkg.license === 'string') {
        return pkg.license;
    }
    if (pkg.license !== undefined && typeof pkg.license === 'object' && pkg.license.type) {
        return pkg.license.type;
    }
    if (Array.isArray(pkg.licenses)) {
        return pkg.licenses.map((license) => (typeof license === 'string' ? license : license.type ?? '')).filter(Boolean).join(' OR ');
    }
    return '';
}

/**
 * The npm packages of the projects that a build ships: every package in each project's lock that is not a development
 * one, as installed in its node_modules. A package installed for another platform only (an optional one not here) is not
 * in this build, so it is not listed.
 */
export function npmComponents(projects: string[]): Component[] {
    const found = new Map<string, Component>();
    for (const project of projects) {
        const lock = readJson<{ packages?: Record<string, LockEntry> }>(join(project, 'package-lock.json'));
        if (lock?.packages === undefined) {
            throw new Error(`[notices] ${project} has no package-lock.json to read its packages from.`);
        }
        for (const [path, entry] of Object.entries(lock.packages)) {
            if (path === '' || entry.dev || entry.devOptional || entry.link || !path.includes('node_modules/')) {
                continue;
            }
            const folder = join(project, path);
            if (!existsSync(folder)) {
                if (entry.optional) {
                    continue;
                }
                throw new Error(`[notices] ${path} is in ${project}'s lock but not installed: run npm ci there first.`);
            }
            const name = path.slice(path.lastIndexOf('node_modules/') + 'node_modules/'.length);
            const version = entry.version ?? '';
            const key = `${name}@${version}`;
            if (found.has(key)) {
                continue;
            }
            const pkg = readJson<PackageJson>(join(folder, 'package.json'));
            const repositoryField = pkg?.repository;
            const source = typeof repositoryField === 'string' ? repositoryField : repositoryField?.url;
            found.set(key, {
                name,
                version,
                license: entry.license ?? declaredNpmLicense(pkg),
                source: source?.replace(/^git\+/, '').replace(/\.git$/, ''),
                text: licenseTexts(folder),
            });
        }
    }
    return [...found.values()];
}

// ---- Rust -------------------------------------------------------------------------------------------------------------

/** What `cargo metadata --format-version 1` says, as far as it is read here. */
export interface CargoMetadata {
    packages: { id: string; name: string; version: string; source: string | null; license: string | null; license_file: string | null; repository: string | null; manifest_path: string }[];
    resolve: { nodes: { id: string; deps: { pkg: string; dep_kinds: { kind: string | null }[] }[] }[] } | null;
}

/**
 * The crates compiled into the named programs: everything their packages reach through normal dependencies, never a
 * build script's or a test's. The workspace's own crates are this project's, under its license, so they are not listed.
 */
export function crateComponents(metadata: CargoMetadata, programs: string[]): Component[] {
    if (metadata.resolve === null) {
        throw new Error('[notices] cargo metadata answered without the dependency graph.');
    }
    const packages = new Map(metadata.packages.map((pkg) => [pkg.id, pkg]));
    const nodes = new Map(metadata.resolve.nodes.map((node) => [node.id, node]));
    const roots = metadata.packages.filter((pkg) => pkg.source === null && programs.includes(pkg.name)).map((pkg) => pkg.id);
    const missing = programs.filter((program) => !metadata.packages.some((pkg) => pkg.source === null && pkg.name === program));
    if (missing.length > 0) {
        throw new Error(`[notices] cargo metadata names no crate ${missing.join(', ')} in its workspace.`);
    }
    const reached = new Set<string>();
    const queue = [...roots];
    while (queue.length > 0) {
        const id = queue.shift()!;
        if (reached.has(id)) {
            continue;
        }
        reached.add(id);
        for (const dep of nodes.get(id)?.deps ?? []) {
            if (dep.dep_kinds.some((kind) => kind.kind === null)) {
                queue.push(dep.pkg);
            }
        }
    }
    const components: Component[] = [];
    for (const id of reached) {
        const pkg = packages.get(id);
        if (pkg === undefined || pkg.source === null) {
            continue;
        }
        const folder = dirname(pkg.manifest_path);
        components.push({
            name: pkg.name,
            version: pkg.version,
            license: pkg.license ?? (pkg.license_file ? `see ${pkg.license_file}` : ''),
            source: pkg.repository ?? undefined,
            text: licenseTexts(folder, pkg.license_file ? [pkg.license_file] : []),
        });
    }
    return components;
}

/** `cargo metadata` of a workspace, for one platform's build. */
export function cargoMetadata(manifestPath: string, triple: string): CargoMetadata {
    const answer = execFileSync('cargo', ['metadata', '--format-version', '1', '--locked', '--manifest-path', manifestPath, '--filter-platform', triple], {
        encoding: 'utf8',
        maxBuffer: 256 * 1024 * 1024,
    });
    return JSON.parse(answer) as CargoMetadata;
}

// ---- .NET -------------------------------------------------------------------------------------------------------------

/** What a project's obj/project.assets.json says, as far as it is read here. */
export interface ProjectAssets {
    targets: Record<string, Record<string, { type: string; runtime?: Record<string, unknown>; native?: Record<string, unknown>; runtimeTargets?: Record<string, unknown>; resource?: Record<string, unknown> }>>;
    libraries: Record<string, { type: string; path?: string }>;
    project: {
        restore?: { packagesPath?: string };
        frameworks?: Record<string, { downloadDependencies?: { name: string; version: string }[] }>;
    };
}

/** Whether a package puts anything into the published program: an assembly, a native library or a resource. */
function ships(assets: Record<string, unknown> | undefined): boolean {
    return assets !== undefined && Object.keys(assets).some((file) => !file.endsWith('_._'));
}

function nuspecField(nuspec: string, pattern: RegExp): string | undefined {
    return pattern.exec(nuspec)?.[1]?.trim();
}

/**
 * The NuGet packages a published .NET program holds, and the runtime a self-contained one carries: every package of the
 * restore that puts an assembly, a native library or a resource into it (an analyzer or a build tool puts none), and the
 * runtime and host packs it was published with. The project's own references are this project's, so they are not listed.
 */
export function nugetComponents(assets: ProjectAssets, packagesRoot: string): Component[] {
    const targetKeys = Object.keys(assets.targets);
    const targetKey = targetKeys.find((key) => key.includes('/')) ?? targetKeys[0];
    if (targetKey === undefined) {
        throw new Error('[notices] the restore of the command line names no target.');
    }
    const components: Component[] = [];
    for (const [key, entry] of Object.entries(assets.targets[targetKey])) {
        if (entry.type !== 'package' || !(ships(entry.runtime) || ships(entry.native) || ships(entry.runtimeTargets) || ships(entry.resource))) {
            continue;
        }
        const [name, version] = key.split('/');
        const folder = join(packagesRoot, assets.libraries[key]?.path ?? `${name.toLowerCase()}/${version.toLowerCase()}`);
        const nuspecFile = (() => {
            try {
                return readdirSync(folder).find((file) => file.toLowerCase().endsWith('.nuspec'));
            } catch {
                return undefined;
            }
        })();
        if (nuspecFile === undefined) {
            throw new Error(`[notices] ${name} ${version} is in the command line's restore but not in ${folder}: restore it first.`);
        }
        const nuspec = readFileSync(join(folder, nuspecFile), 'utf8');
        const expression = nuspecField(nuspec, /<license[^>]*type="expression"[^>]*>([^<]+)<\/license>/i);
        const licenseFile = nuspecField(nuspec, /<license[^>]*type="file"[^>]*>([^<]+)<\/license>/i);
        const url = nuspecField(nuspec, /<licenseUrl>([^<]+)<\/licenseUrl>/i);
        components.push({
            name,
            version,
            license: expression ?? (licenseFile ? `see ${licenseFile}` : url ?? ''),
            source: nuspecField(nuspec, /<projectUrl>([^<]+)<\/projectUrl>/i),
            text: licenseTexts(folder, licenseFile ? [licenseFile] : []),
        });
    }

    // The runtime and host packs a self-contained program is published with: the .NET runtime itself, under its own notices.
    for (const framework of Object.values(assets.project.frameworks ?? {})) {
        for (const pack of framework.downloadDependencies ?? []) {
            if (!/\.(Runtime|Host)\.[A-Za-z0-9-]+$/.test(pack.name)) {
                continue;
            }
            const version = pack.version.replace(/[[\]()\s]/g, '').split(',')[0];
            const folder = join(packagesRoot, pack.name.toLowerCase(), version);
            if (!existsSync(folder)) {
                continue;
            }
            components.push({ name: pack.name, version, license: 'MIT', source: 'https://github.com/dotnet/runtime', text: licenseTexts(folder) });
        }
    }
    return components;
}

// ---- The file ---------------------------------------------------------------------------------------------------------

/** A kind of component and the components of it a package ships. */
export interface NoticeSection {
    title: string;
    about: string;
    components: Component[];
}

/** The components that state no license at all, by section: a package does not ship one without a decision. */
export function unlicensed(sections: NoticeSection[]): string[] {
    return sections.flatMap((section) => section.components
        .filter((component) => component.license.trim() === '' && component.text.trim() === '')
        .map((component) => `${section.title}: ${component.name} ${component.version}`));
}

const RULE = '='.repeat(80);

/**
 * The notices as the package carries them: a heading, then each section's components in name order, each with its
 * license. A text several components ship word for word (a vendor's notices file in each of its packages) is written
 * once, at the first of them, and the others point to it.
 */
export function noticesText(sections: NoticeSection[]): string {
    const written = new Map<string, string>();
    const lines: string[] = [
        'OSDU Delivery, powered by SQLFlow, for VS Code: third-party notices',
        RULE,
        '',
        'OSDU Delivery is free software under the GNU General Public License, version 3 or later (LICENSE in this',
        'package). The package also carries components of other parties, each under the license reproduced with it below.',
        'This file is written when the package is built (osdu/hosts/osdu-delivery-vscode/notices.mts in the repository).',
        '',
        ...sections.map((section) => `${section.title}: ${section.components.length}`),
        '',
    ];
    for (const section of sections) {
        lines.push(RULE, section.title.toUpperCase(), section.about, RULE, '');
        const sorted = [...section.components].sort((a, b) => a.name.localeCompare(b.name) || a.version.localeCompare(b.version));
        for (const component of sorted) {
            lines.push('-'.repeat(80), `${component.name} ${component.version}`, `License: ${component.license || '(see the text below)'}`);
            if (component.source) {
                lines.push(`Source: ${component.source}`);
            }
            lines.push('-'.repeat(80), '');
            const first = component.text === '' ? undefined : written.get(component.text);
            if (component.text === '') {
                lines.push(`No license file ships with this component. It is under ${component.license}; the full text is at its source.`, '');
            } else if (first !== undefined) {
                lines.push(`Its license and notices are word for word those of ${first}, above.`, '');
            } else {
                written.set(component.text, `${component.name} ${component.version}`);
                lines.push(component.text, '');
            }
        }
    }
    return `${lines.join('\n')}\n`;
}

/** The Rust platform a package's programs are built for. */
export const RUST_TRIPLES: Readonly<Record<string, string>> = {
    'win32-x64': 'x86_64-pc-windows-msvc',
    'linux-x64': 'x86_64-unknown-linux-gnu',
    'darwin-arm64': 'aarch64-apple-darwin',
    'darwin-x64': 'x86_64-apple-darwin',
};

/** Reads every component a package for `target` ships, from this repository's builds, and writes the notices to `file`. */
export function writeNotices(file: string, target: string): NoticeSection[] {
    const triple = RUST_TRIPLES[target];
    if (triple === undefined) {
        throw new Error(`[notices] '${target}' is not a platform this extension is packaged for.`);
    }
    const assetsFile = join(repository, 'osdu', 'hosts', 'SqlFlow.Delivery.Cli.Host', 'obj', 'project.assets.json');
    const assets = readJson<ProjectAssets>(assetsFile);
    if (assets === undefined) {
        throw new Error(`[notices] ${assetsFile} is missing: the command line is published (and so restored) before a package is made.`);
    }
    const packagesRoot = assets.project.restore?.packagesPath ?? join(process.env.NUGET_PACKAGES ?? join(process.env.USERPROFILE ?? process.env.HOME ?? '', '.nuget', 'packages'));

    const sections: NoticeSection[] = [
        {
            title: 'npm packages',
            about: 'Bundled into the pages the extension shows and into the extension itself.',
            components: npmComponents([join(repository, 'osdu', 'gui'), here]),
        },
        {
            title: 'Rust crates',
            about: 'Compiled into the language server (sqlflow-lsp) and the MCP server (osdu-delivery-mcp).',
            components: [
                ...crateComponents(cargoMetadata(join(repository, 'sqlflow', 'tools', 'Cargo.toml'), triple), ['sqlflow-lsp']),
                ...crateComponents(cargoMetadata(join(repository, 'osdu', 'hosts', 'osdu-delivery-mcp', 'Cargo.toml'), triple), ['osdu-delivery-mcp']),
            ].filter((component, index, all) => all.findIndex((other) => other.name === component.name && other.version === component.version) === index),
        },
        {
            title: '.NET packages and runtime',
            about: 'Published into the sqlflow command line, which carries its own .NET runtime.',
            components: nugetComponents(assets, packagesRoot),
        },
    ];
    const none = unlicensed(sections);
    if (none.length > 0) {
        throw new Error(`[notices] these components state no license, so the package cannot carry them without a decision: ${none.join('; ')}.`);
    }
    writeFileSync(file, noticesText(sections));
    return sections;
}

if (process.argv[1] !== undefined && fileURLToPath(import.meta.url) === process.argv[1]) {
    const target = process.argv[2] ?? `${process.platform}-${process.arch}`;
    const file = process.argv[3] ?? join(here, 'THIRD-PARTY-NOTICES.txt');
    const sections = writeNotices(file, target);
    console.log(`[notices] ${file}: ${sections.map((section) => `${section.components.length} ${section.title}`).join(', ')}`);
}
