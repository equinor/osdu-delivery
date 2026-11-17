import { execFile } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { promisify } from 'util';

const run = promisify(execFile);

/**
 * Registers the MCP server with Claude Code, for every Claude Code session of the person (its `user` scope), through
 * Claude Code's own command line, so nothing writes Claude Code's files behind it. The registration is the server's own
 * `install` answer (its name, program and arguments, one per mode), so there is one definition of it; the environment
 * is the extension's, which names the control plane the person signed in to.
 */

/** How Claude Code's command line is started here: a program, and what comes before Claude Code's own arguments. */
export interface ClaudeCommand {
    program: string;
    prefix: string[];
}

/** A server registration as the server's `install` answers it: its name, and the program with its arguments. */
export interface Registration {
    name: string;
    command: string;
    args: string[];
}

function onPath(env: NodeJS.ProcessEnv, platform: NodeJS.Platform): string[] {
    const value = Object.entries(env).find(([name]) => (platform === 'win32' ? name.toLowerCase() === 'path' : name === 'PATH'))?.[1] ?? '';
    return value.split(platform === 'win32' ? ';' : ':').filter((folder) => folder.trim() !== '');
}

/**
 * Finds Claude Code's command line: `claude` on the PATH, else where its installers put it (`~/.local/bin`, and
 * `~/.claude/local` for an older install). npm leaves a `claude.cmd` on Windows, which no program starts without a shell,
 * so the script it runs is started with Node instead; one whose script is not where npm puts it is not used.
 */
export function findClaude(
    env: NodeJS.ProcessEnv,
    platform: NodeJS.Platform,
    home: string,
    exists: (file: string) => boolean
): ClaudeCommand | undefined {
    const join = platform === 'win32' ? path.win32.join : path.posix.join;
    const folders = [...onPath(env, platform), join(home, '.local', 'bin'), join(home, '.claude', 'local')];
    for (const folder of folders) {
        if (platform !== 'win32') {
            const program = join(folder, 'claude');
            if (exists(program)) {
                return { program, prefix: [] };
            }
            continue;
        }
        const exe = join(folder, 'claude.exe');
        if (exists(exe)) {
            return { program: exe, prefix: [] };
        }
        const cmd = join(folder, 'claude.cmd');
        const script = join(folder, 'node_modules', '@anthropic-ai', 'claude-code', 'cli.js');
        if (exists(cmd) && exists(script)) {
            const besideIt = join(folder, 'node.exe');
            const node = exists(besideIt) ? besideIt : onPath(env, platform).map((dir) => join(dir, 'node.exe')).find(exists);
            if (node !== undefined) {
                return { program: node, prefix: [script] };
            }
        }
    }
    return undefined;
}

/** The registrations in a server's `install vscode` answer (`{"mcpServers": {name: {command, args}}}`). */
export function registrationsOf(answer: string): Registration[] {
    let parsed: unknown;
    try {
        parsed = JSON.parse(answer);
    } catch {
        throw new Error(`the server's install answer is not JSON: ${answer.slice(0, 200)}`);
    }
    const servers = (parsed as { mcpServers?: Record<string, { command?: unknown; args?: unknown }> }).mcpServers;
    if (servers === undefined || typeof servers !== 'object') {
        throw new Error(`the server's install answer names no server: ${answer.slice(0, 200)}`);
    }
    return Object.entries(servers).map(([name, server]) => {
        if (typeof server.command !== 'string' || (server.args !== undefined && !Array.isArray(server.args))) {
            throw new Error(`the server's install answer for '${name}' has no program to run`);
        }
        return { name, command: server.command, args: ((server.args as unknown[] | undefined) ?? []).map(String) };
    });
}

/** What `claude mcp add-json` is given for a registration: a stdio server with the extension's environment. */
export function claudeServerJson(registration: Registration, env: Record<string, string>): string {
    return JSON.stringify({ type: 'stdio', command: registration.command, args: registration.args, env });
}

/** Asks the server at `program` for its registration in `mode` (`estate` or `data`), as `install` answers it. */
export async function serverRegistration(program: string, mode: string, env: Record<string, string>): Promise<Registration> {
    const { stdout } = await run(program, ['install', 'vscode', '--mode', mode], { windowsHide: true, env: { ...process.env, ...env } });
    const found = registrationsOf(stdout);
    if (found.length !== 1) {
        throw new Error(`the server answered ${found.length} registrations for mode '${mode}', not one`);
    }
    return found[0];
}

async function claude(command: ClaudeCommand, args: string[]): Promise<{ ok: boolean; output: string }> {
    try {
        const { stdout, stderr } = await run(command.program, [...command.prefix, ...args], { windowsHide: true, timeout: 60_000 });
        return { ok: true, output: `${stdout}${stderr}`.trim() };
    } catch (err) {
        const failed = err as { stdout?: string; stderr?: string; message?: string };
        return { ok: false, output: `${failed.stdout ?? ''}${failed.stderr ?? ''}`.trim() || (failed.message ?? String(err)) };
    }
}

/**
 * Registers each registration with Claude Code for every session of the person: one it holds under that name already is
 * replaced, so a new program path or control plane takes the place of the old.
 */
export async function registerWithClaude(command: ClaudeCommand, registrations: Registration[], env: Record<string, string>): Promise<void> {
    for (const registration of registrations) {
        // Not there is the usual answer to the removal, and is no failure.
        await claude(command, ['mcp', 'remove', '--scope', 'user', registration.name]);
        const added = await claude(command, ['mcp', 'add-json', '--scope', 'user', registration.name, claudeServerJson(registration, env)]);
        if (!added.ok) {
            throw new Error(`Claude Code did not register '${registration.name}': ${added.output}`);
        }
    }
}

/** Removes the registrations from Claude Code's user scope; one it does not hold is passed over. */
export async function unregisterFromClaude(command: ClaudeCommand, names: string[]): Promise<string[]> {
    const removed: string[] = [];
    for (const name of names) {
        if ((await claude(command, ['mcp', 'remove', '--scope', 'user', name])).ok) {
            removed.push(name);
        }
    }
    return removed;
}

/** Claude Code's command line on this machine, if Claude Code is installed. */
export function claudeHere(): ClaudeCommand | undefined {
    return findClaude(process.env, process.platform, os.homedir(), fs.existsSync);
}
