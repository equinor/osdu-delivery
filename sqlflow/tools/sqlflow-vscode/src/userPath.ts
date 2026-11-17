import { execFile } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { promisify } from 'util';

const run = promisify(execFile);

/**
 * A folder on the PATH of everything a person starts, not only the editor's terminals: an assistant's shell (Claude
 * Code, Copilot's command line, Codex), a terminal of its own, a script. On Windows that is the user environment's
 * `Path`; on macOS and Linux it is a line in the shell's start-up files that reads `~/.sqlflow/path.sh`. The folder goes
 * at the end, so a program the person has put on their PATH themselves keeps answering to its name.
 */

/** Where a change was made, in a person's words: "the user environment's Path", or a file's path. */
export interface PathChange {
    changed: string[];
}

// ---- Windows: the user environment's Path, in the registry ------------------------------------------------------

/** The user environment's key under HKEY_CURRENT_USER. */
export const USER_ENVIRONMENT = 'Environment';

/** `%NAME%` replaced by the variable's value, the name read without regard to case as Windows reads it. */
export function expandWindows(value: string, env: NodeJS.ProcessEnv): string {
    const names = Object.keys(env);
    return value.replace(/%([^%]+)%/g, (whole, name: string) => {
        const key = names.find((candidate) => candidate.toLowerCase() === name.toLowerCase());
        return key === undefined ? whole : (env[key] ?? whole);
    });
}

function sameWindowsFolder(entry: string, folder: string, env: NodeJS.ProcessEnv): boolean {
    const normal = (text: string): string => path.win32.normalize(expandWindows(text.trim(), env)).replace(/[\\/]+$/, '').toLowerCase();
    return entry.trim() !== '' && normal(entry) === normal(folder);
}

/** Whether a `Path` value names `folder`, however it is written: `%USERPROFILE%\.sqlflow\bin` and a trailing `\` alike. */
export function windowsPathHas(value: string, folder: string, env: NodeJS.ProcessEnv): boolean {
    return value.split(';').some((entry) => sameWindowsFolder(entry, folder, env));
}

/** `value` with `folder` at its end, unchanged when it names the folder already. */
export function windowsPathWith(value: string, folder: string, env: NodeJS.ProcessEnv): string {
    if (windowsPathHas(value, folder, env)) {
        return value;
    }
    const kept = value.replace(/;+$/, '');
    return kept === '' ? folder : `${kept};${folder}`;
}

/** `value` without any entry naming `folder`; every other entry stays as it was written. */
export function windowsPathWithout(value: string, folder: string, env: NodeJS.ProcessEnv): string {
    return value.split(';').filter((entry) => !sameWindowsFolder(entry, folder, env)).join(';');
}

/** The user environment's `Path` as written (its `%VARIABLES%` unexpanded), and its registry kind; empty when it has none. */
interface StoredPath {
    value: string;
    kind: 'String' | 'ExpandString' | '';
}

const POWERSHELL = path.join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');

/** Runs a PowerShell script, its inputs passed in the environment so that no value is ever parsed as script. */
async function powershell(script: string, inputs: Record<string, string>): Promise<string> {
    const encoded = Buffer.from(`$ErrorActionPreference = 'Stop'\n[Console]::OutputEncoding = [Text.Encoding]::UTF8\n${script}`, 'utf16le').toString('base64');
    const { stdout } = await run(POWERSHELL, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', encoded], {
        env: { ...process.env, ...inputs },
        windowsHide: true,
    });
    return stdout;
}

/** Reads the `Path` of the user environment at `key` (a key under HKEY_CURRENT_USER), as written. */
export async function readWindowsPath(key: string = USER_ENVIRONMENT): Promise<StoredPath> {
    const answer = await powershell(
        [
            '$key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($env:SQLFLOW_PATH_KEY)',
            "if ($null -eq $key -or $key.GetValueNames() -notcontains 'Path') { '{\"value\":\"\",\"kind\":\"\"}'; exit 0 }",
            'try {',
            "  $value = $key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)",
            "  [pscustomobject]@{ value = [string]$value; kind = $key.GetValueKind('Path').ToString() } | ConvertTo-Json -Compress",
            '} finally { $key.Close() }',
        ].join('\n'),
        { SQLFLOW_PATH_KEY: key }
    );
    const parsed = JSON.parse(answer.trim()) as { value: string; kind: string };
    if (parsed.kind !== '' && parsed.kind !== 'String' && parsed.kind !== 'ExpandString') {
        throw new Error(`HKEY_CURRENT_USER\\${key} holds Path as ${parsed.kind}, not as a string; it is left as it is.`);
    }
    return { value: parsed.value, kind: parsed.kind as StoredPath['kind'] };
}

/**
 * Writes the `Path` of the user environment at `key`, in `kind` (an expandable string unless it was a plain one), and
 * tells the programs running (Explorer first) that the environment changed, so what they start next has it. An empty
 * value removes `Path`.
 */
export async function writeWindowsPath(value: string, kind: StoredPath['kind'], key: string = USER_ENVIRONMENT): Promise<void> {
    await powershell(
        [
            '$key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($env:SQLFLOW_PATH_KEY)',
            'try {',
            "  if ($env:SQLFLOW_PATH_VALUE -eq '') { $key.DeleteValue('Path', $false) }",
            "  else { $key.SetValue('Path', $env:SQLFLOW_PATH_VALUE, [Microsoft.Win32.RegistryValueKind]::($env:SQLFLOW_PATH_KIND)) }",
            '} finally { $key.Close() }',
            "Add-Type -Namespace SqlFlowSetup -Name Native -MemberDefinition '[DllImport(\"user32.dll\", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);'",
            '$result = [UIntPtr]::Zero',
            "[void][SqlFlowSetup.Native]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]$result)",
        ].join('\n'),
        // A Path written for the first time is expandable, as Windows writes its own.
        { SQLFLOW_PATH_KEY: key, SQLFLOW_PATH_VALUE: value, SQLFLOW_PATH_KIND: kind === '' ? 'ExpandString' : kind }
    );
}

// ---- macOS and Linux: a line in the shell's start-up files ------------------------------------------------------

/** The script a shell reads to have the folder on its PATH. Not `~/.sqlflow/env`: the command line reads a file of that name as settings. */
export const PATH_SCRIPT = 'path.sh';

/** The line each start-up file gets: it reads the script while there is one, so removing the script breaks no shell. */
export const PROFILE_LINE = '[ -f "$HOME/.sqlflow/path.sh" ] && . "$HOME/.sqlflow/path.sh"';

/** The script that puts `~/.sqlflow/bin` at the end of a POSIX shell's PATH, once however often it is read. */
export function posixPathScript(product: string): string {
    return [
        `# The sqlflow command line on the PATH of every shell, written by ${product} for VS Code.`,
        '# Its command "Remove the Command Line and Server from Other Tools" removes this file and the lines that read it.',
        'case ":${PATH}:" in',
        '  *:"$HOME/.sqlflow/bin":*) ;;',
        '  *) PATH="${PATH:+$PATH:}$HOME/.sqlflow/bin"; export PATH ;;',
        'esac',
        '',
    ].join('\n');
}

/** The fish shell's equivalent, kept in its `conf.d`, which fish reads at every start. */
export function fishPathScript(product: string): string {
    return [
        `# The sqlflow command line on the PATH of every fish shell, written by ${product} for VS Code.`,
        'if not contains -- $HOME/.sqlflow/bin $PATH',
        '    set -gx PATH $PATH $HOME/.sqlflow/bin',
        'end',
        '',
    ].join('\n');
}

/** A start-up file's text with the line added at its end, unchanged when the line is there already. */
export function withProfileLine(text: string): string {
    if (text.split(/\r?\n/).some((line) => line.trim() === PROFILE_LINE)) {
        return text;
    }
    const newline = text.includes('\r\n') ? '\r\n' : '\n';
    const body = text === '' || text.endsWith('\n') ? text : `${text}${newline}`;
    return `${body}${PROFILE_LINE}${newline}`;
}

/** A start-up file's text without the line; every other line stays as it was. */
export function withoutProfileLine(text: string): string {
    const newline = text.includes('\r\n') ? '\r\n' : '\n';
    return text.split(/\r?\n/).filter((line) => line.trim() !== PROFILE_LINE).join(newline);
}

/**
 * The start-up files the line goes into: `.profile`, which login shells read, always; `.bash_profile` and `.bashrc` when
 * there are such files; `.zshenv`, which every zsh reads, when there is one, the person's shell is zsh, or the machine
 * is a Mac, where zsh is the shell.
 */
export function profileFiles(home: string, shell: string, platform: NodeJS.Platform, exists: (file: string) => boolean): string[] {
    const files = [path.join(home, '.profile')];
    for (const name of ['.bash_profile', '.bashrc']) {
        const file = path.join(home, name);
        if (exists(file)) {
            files.push(file);
        }
    }
    const zshenv = path.join(home, '.zshenv');
    if (exists(zshenv) || path.basename(shell) === 'zsh' || platform === 'darwin') {
        files.push(zshenv);
    }
    return files;
}

function fishConfig(home: string): string {
    return path.join(home, '.config', 'fish');
}

/** Writes `~/.sqlflow/path.sh` and the line that reads it into the start-up files of `shell` on `platform`, and fish's own. */
export function addToShellProfiles(product: string, home: string, shell: string, platform: NodeJS.Platform): PathChange {
    const changed: string[] = [];
    const script = path.join(home, '.sqlflow', PATH_SCRIPT);
    const content = posixPathScript(product);
    if (!fs.existsSync(script) || fs.readFileSync(script, 'utf8') !== content) {
        fs.mkdirSync(path.dirname(script), { recursive: true });
        fs.writeFileSync(script, content, 'utf8');
        changed.push(script);
    }
    for (const file of profileFiles(home, shell, platform, fs.existsSync)) {
        const before = fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : '';
        const after = withProfileLine(before);
        if (after !== before) {
            fs.writeFileSync(file, after, 'utf8');
            changed.push(file);
        }
    }
    if (fs.existsSync(fishConfig(home))) {
        const fish = path.join(fishConfig(home), 'conf.d', 'sqlflow.fish');
        const fishContent = fishPathScript(product);
        if (!fs.existsSync(fish) || fs.readFileSync(fish, 'utf8') !== fishContent) {
            fs.mkdirSync(path.dirname(fish), { recursive: true });
            fs.writeFileSync(fish, fishContent, 'utf8');
            changed.push(fish);
        }
    }
    return { changed };
}

/** Takes the line out of every start-up file that has it, and removes `~/.sqlflow/path.sh` and fish's file. */
export function removeFromShellProfiles(home: string): PathChange {
    const changed: string[] = [];
    for (const name of ['.profile', '.bash_profile', '.bashrc', '.zshenv']) {
        const file = path.join(home, name);
        if (!fs.existsSync(file)) {
            continue;
        }
        const before = fs.readFileSync(file, 'utf8');
        const after = withoutProfileLine(before);
        if (after !== before) {
            fs.writeFileSync(file, after, 'utf8');
            changed.push(file);
        }
    }
    for (const file of [path.join(home, '.sqlflow', PATH_SCRIPT), path.join(fishConfig(home), 'conf.d', 'sqlflow.fish')]) {
        if (fs.existsSync(file)) {
            fs.rmSync(file);
            changed.push(file);
        }
    }
    return { changed };
}

// ---- Both ------------------------------------------------------------------------------------------------------

/** Puts `folder` (`~/.sqlflow/bin`) on the person's own PATH, and says where that was done; nothing when it is there already. */
export async function addToUserPath(folder: string, product: string, home: string = os.homedir()): Promise<PathChange> {
    if (process.platform !== 'win32') {
        return addToShellProfiles(product, home, process.env.SHELL ?? '', process.platform);
    }
    const stored = await readWindowsPath();
    const value = windowsPathWith(stored.value, folder, process.env);
    if (value === stored.value) {
        return { changed: [] };
    }
    await writeWindowsPath(value, stored.kind);
    return { changed: ["the user environment's Path"] };
}

/** Takes `folder` off the person's own PATH again, and says where; nothing when it was not there. */
export async function removeFromUserPath(folder: string, home: string = os.homedir()): Promise<PathChange> {
    if (process.platform !== 'win32') {
        return removeFromShellProfiles(home);
    }
    const stored = await readWindowsPath();
    const value = windowsPathWithout(stored.value, folder, process.env);
    if (value === stored.value) {
        return { changed: [] };
    }
    await writeWindowsPath(value, stored.kind);
    return { changed: ["the user environment's Path"] };
}
