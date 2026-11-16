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

/** The token the shared file keeps for a control plane, or null when it keeps none, is missing or does not parse. */
export function fileCredential(file: string, url: string): string | null {
    let entries: unknown;
    try {
        entries = JSON.parse(fs.readFileSync(file, 'utf8'));
    } catch {
        // No file yet means nobody has signed in on this machine; one that does not parse holds nothing, as the server reads it.
        return null;
    }
    if (entries === null || typeof entries !== 'object' || Array.isArray(entries)) {
        return null;
    }
    const entry = (entries as Record<string, unknown>)[credentialKey(url)];
    const token = entry !== null && typeof entry === 'object' ? (entry as { token?: unknown }).token : undefined;
    return typeof token === 'string' && token !== '' ? token : null;
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
