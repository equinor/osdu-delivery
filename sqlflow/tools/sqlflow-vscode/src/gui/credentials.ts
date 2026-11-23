import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

/**
 * The credential a page's calls are carried under: the one the MCP server holds for the control plane, read where the
 * server reads it. A person signs in once (`~/.sqlflow/credentials.json`, one credential per control plane, which the
 * server and the command line both keep), and the server renews a session there while it runs, so reading the file
 * when a call is made gives the credential as it stands. Nothing here writes: signing in, renewing and signing out
 * stay the server's, and this follows its order of where a credential comes from (`ControlPlane::from_env` in
 * sqlflow-mcp): the environment's, while it has not lapsed, then the file's.
 *
 * `sqlflow-mcp/tests/shared-credentials.json` holds this reader, the server's and the command line's to one format.
 */

/** How close to its end a credential the environment gives is passed over, as the server passes it over. */
const EXPIRY_FLOOR_MS = 30_000;

/** The file every client of a control plane shares a person's sign-in through. */
export function credentialFile(env: NodeJS.ProcessEnv = process.env): string {
    return env.SQLFLOW_CREDENTIALS_FILE?.trim() || path.join(os.homedir(), '.sqlflow', 'credentials.json');
}

/**
 * The key a control plane's credential is kept under: scheme and host in lower case, a default port left out and no
 * trailing slash, as the server's `credential_key` and the command line's `CredentialStore.Normalize` make it.
 */
export function credentialKey(url: string): string {
    let key: string;
    try {
        const parsed = new URL(url);
        key = `${parsed.origin}${parsed.pathname}`;
    } catch {
        key = url;
    }
    return key.replace(/\/+$/, '').toLowerCase();
}

/** When a token that is a JWT says it ends, in milliseconds; undefined for one that says nothing (an access token). */
export function tokenExpiry(token: string): number | undefined {
    const parts = token.split('.');
    if (parts.length !== 3) {
        return undefined;
    }
    try {
        const claims = JSON.parse(Buffer.from(parts[1], 'base64url').toString('utf8')) as { exp?: unknown };
        return typeof claims.exp === 'number' && Number.isFinite(claims.exp) ? claims.exp * 1000 : undefined;
    } catch {
        return undefined;
    }
}

/** What the shared file keeps, by control plane; empty when it is missing or does not parse, as the server reads it. */
export function readCredentialFile(file: string): Record<string, unknown> {
    let entries: unknown;
    try {
        entries = JSON.parse(fs.readFileSync(file, 'utf8'));
    } catch {
        // No file yet means nobody has signed in on this machine; one that does not parse holds nothing, as the server reads it.
        return {};
    }
    return entries !== null && typeof entries === 'object' && !Array.isArray(entries) ? (entries as Record<string, unknown>) : {};
}

/** The entry kept for a control plane in what the shared file holds, or undefined for none. */
function entryOf(entries: Record<string, unknown>, url: string): Record<string, unknown> | undefined {
    const entry = entries[credentialKey(url)];
    return entry !== null && typeof entry === 'object' && !Array.isArray(entry) ? (entry as Record<string, unknown>) : undefined;
}

/** The token the shared file keeps for a control plane, or null when it keeps none, is missing or does not parse. */
export function fileCredential(file: string, url: string): string | null {
    const token = entryOf(readCredentialFile(file), url)?.token;
    return typeof token === 'string' && token !== '' ? token : null;
}

/** Who a JWT says it is (its `sub`), or undefined for a token that says nothing (an access token). */
function tokenSubject(token: string): string | undefined {
    const parts = token.split('.');
    if (parts.length !== 3) {
        return undefined;
    }
    try {
        const claims = JSON.parse(Buffer.from(parts[1], 'base64url').toString('utf8')) as { sub?: unknown };
        return typeof claims.sub === 'string' && claims.sub.trim() !== '' ? claims.sub : undefined;
    } catch {
        return undefined;
    }
}

/**
 * The sign-in the shared file keeps for a control plane, when it keeps one that has not ended, with who it is when the
 * entry or its token says; null for none. What a person is shown of an environment they are not working against: a
 * server renews only the session of the control plane it uses, so one that has ended asks for a sign-in when the person
 * switches back to it, and one that has not signs in with no browser.
 */
export function heldSignIn(entries: Record<string, unknown>, url: string, now: number = Date.now()): { account: string | null } | null {
    const entry = entryOf(entries, url);
    const token = entry?.token;
    if (entry === undefined || typeof token !== 'string' || token === '') {
        return null;
    }
    const stated = typeof entry.expiresUtc === 'string' ? Date.parse(entry.expiresUtc) : Number.NaN;
    const ends = Number.isFinite(stated) ? stated : tokenExpiry(token);
    if (ends !== undefined && ends <= now + EXPIRY_FLOOR_MS) {
        return null;
    }
    const username = typeof entry.username === 'string' && entry.username.trim() !== '' ? entry.username : undefined;
    return { account: username ?? tokenSubject(token) ?? null };
}

/** The credential to present to a control plane now, or null while nobody is signed in to it. */
export function currentCredential(url: string, env: NodeJS.ProcessEnv = process.env, now: number = Date.now()): string | null {
    const ambient = env.SQLFLOW_CONTROL_PLANE_TOKEN?.trim();
    if (ambient) {
        const ends = tokenExpiry(ambient);
        if (ends === undefined || now + EXPIRY_FLOOR_MS < ends) {
            return ambient;
        }
    }
    return fileCredential(credentialFile(env), url);
}
