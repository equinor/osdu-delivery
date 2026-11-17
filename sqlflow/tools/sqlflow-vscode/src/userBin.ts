import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

/**
 * The folder a person's other tools run this extension's programs from: `~/.sqlflow/bin`, beside the sign-in. The
 * extension's own folder carries its version, so a tool given a path into it would break on the next update.
 */
export function userBinFolder(home: string = os.homedir()): string {
    return path.join(home, '.sqlflow', 'bin');
}

/** Whether `copy` holds what `binary` does: as large, and written no earlier. */
function upToDate(copy: string, binary: string): boolean {
    if (!fs.existsSync(copy)) {
        return false;
    }
    const held = fs.statSync(copy);
    const current = fs.statSync(binary);
    return held.size === current.size && held.mtimeMs >= current.mtimeMs;
}

/**
 * The copy of `binary` in `folder` when it is up to date, else none. A tool started from it runs from a path that does
 * not change with the extension's version, which a sandboxed shell can be allowed once.
 */
export function upToDateCopy(binary: string, folder: string = userBinFolder()): string | undefined {
    const copy = path.join(folder, path.basename(binary));
    return upToDate(copy, binary) ? copy : undefined;
}

/**
 * Copies `binary` into `folder` (the user's bin folder unless named), and returns the copy's path. A copy as new as the
 * program is kept as it is. A running copy cannot be overwritten on Windows but can be renamed, so an update moves it
 * aside, and what was moved aside is cleared once nothing runs it.
 */
export function stableCopy(binary: string, folder: string = userBinFolder()): string {
    const target = path.join(folder, path.basename(binary));
    fs.mkdirSync(folder, { recursive: true });

    if (upToDate(target, binary)) {
        return target;
    }
    if (fs.existsSync(target)) {
        fs.renameSync(target, `${target}.${Date.now()}.old`);
    }
    fs.copyFileSync(binary, target);
    for (const name of fs.readdirSync(folder).filter((file) => file.endsWith('.old'))) {
        try {
            fs.rmSync(path.join(folder, name), { force: true, maxRetries: 0 });
        } catch {
            // Still running somewhere: the next copy clears it.
            continue;
        }
    }
    return target;
}
