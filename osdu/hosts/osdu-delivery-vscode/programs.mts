import { existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

// The three programs a package carries (the language server, the MCP server and the sqlflow command line): where a build
// leaves each and the name it goes by in bin/. The packager stages them from here (esbuild.mjs), and a release signs them
// here before it does (.github/workflows/vscode-build.yml), so what is signed is what is staged.
//
// `node programs.mts` prints the path of each release build, one a line, and fails when one is missing.

const here = dirname(fileURLToPath(import.meta.url));
const repository = join(here, '..', '..', '..');

/** A program a package carries. */
export interface ShippedProgram {
    /** Its name, as the extension and the documentation call it. */
    name: string;
    /** Its file in bin/. */
    file: string;
    /** Where a build left it, the first of `candidates` that exists. */
    built: string | undefined;
    /** Where a build leaves it, in the order they are looked at. */
    candidates: string[];
}

/**
 * Each program a package carries. A package takes release builds only: a debug build left in a target folder is never
 * shipped by mistake. A checkout, which runs the extension from its folder, takes a debug build when no release one is
 * there.
 */
export function shippedPrograms(options: { production: boolean; platform?: NodeJS.Platform; cargoTargetDir?: string; root?: string }): ShippedProgram[] {
    const platform = options.platform ?? process.platform;
    const cargoTargetDir = options.cargoTargetDir ?? process.env.CARGO_TARGET_DIR;
    const root = options.root ?? repository;
    const extension = join(root, 'osdu', 'hosts', 'osdu-delivery-vscode');
    const exe = platform === 'win32' ? '.exe' : '';
    const profiles = options.production ? ['release'] : ['release', 'debug'];
    const rust = (name: string, target: string): string[] => {
        const roots = [cargoTargetDir, target].filter((folder): folder is string => typeof folder === 'string' && folder !== '');
        return roots.flatMap((folder) => profiles.map((profile) => join(folder, profile, `${name}${exe}`)));
    };
    const programs = [
        { name: 'sqlflow-lsp', candidates: rust('sqlflow-lsp', join(root, 'sqlflow', 'tools', 'target')) },
        { name: 'osdu-delivery-mcp', candidates: rust('osdu-delivery-mcp', join(root, 'osdu', 'hosts', 'osdu-delivery-mcp', 'target')) },
        // The command line, published as a single file for one platform (package.mjs), under the name it is documented by.
        { name: 'sqlflow', candidates: [join(extension, 'publish', 'cli', `sqlflow${exe}`)] },
    ];
    return programs.map((program) => ({
        ...program,
        file: `${program.name}${exe}`,
        built: program.candidates.find((candidate) => existsSync(candidate)),
    }));
}

if (process.argv[1] !== undefined && fileURLToPath(import.meta.url) === process.argv[1]) {
    const programs = shippedPrograms({ production: true });
    const missing = programs.filter((program) => program.built === undefined);
    for (const program of missing) {
        console.error(`[programs] ${program.name} has no release build; looked in ${program.candidates.join(', ')}`);
    }
    if (missing.length > 0) {
        process.exit(1);
    }
    for (const program of programs) {
        console.log(program.built);
    }
}
