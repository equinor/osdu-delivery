import { addressProblem, isEnvironmentStage, savedAddress, type SavedEnvironment } from '../../../../gui/src/embed/environments.ts';
import type { ControlPlaneReach, EnvironmentRequest } from '../../../../gui/src/embed/protocol.ts';

/**
 * What the side bar asks of the person's environments, and whether an address answers as a control plane: the parts of
 * it that need no editor, so the unit suite reads them. A message from a webview is read as coming from anyone, so a
 * request is taken only in the shape the protocol gives it.
 */

/** A request that is not one the protocol makes. */
export class RequestRefused extends Error {}

function isRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function text(value: unknown, what: string): string {
    if (typeof value !== 'string') {
        throw new RequestRefused(`The request carried no ${what}.`);
    }
    return value;
}

/** An environment as a request carries it, in the shape a saved one has; whether it is a good one is checked later. */
function environmentIn(value: unknown): SavedEnvironment {
    if (!isRecord(value)) {
        throw new RequestRefused('The request carried no environment.');
    }
    if (!isEnvironmentStage(value.stage)) {
        throw new RequestRefused('The environment carried no stage the editor knows.');
    }
    if (value.guiUrl !== null && typeof value.guiUrl !== 'string') {
        throw new RequestRefused('The environment carried a GUI address that is not text.');
    }
    return {
        name: text(value.name, 'name'),
        url: text(value.url, 'address'),
        stage: value.stage,
        guiUrl: value.guiUrl,
    };
}

/** The request a page sent, in the protocol's shape, or a refusal saying what it lacked. */
export function readEnvironmentRequest(raw: unknown): EnvironmentRequest {
    if (!isRecord(raw)) {
        throw new RequestRefused('The request was not one the editor reads.');
    }
    switch (raw.kind) {
        case 'use':
            return { kind: 'use', url: text(raw.url, 'address') };
        case 'remove':
            return { kind: 'remove', url: text(raw.url, 'address') };
        case 'probe':
            return { kind: 'probe', url: text(raw.url, 'address') };
        case 'save':
            if (raw.replaces !== null && typeof raw.replaces !== 'string') {
                throw new RequestRefused('The request named the environment it changes by something that is not an address.');
            }
            if (typeof raw.use !== 'boolean') {
                throw new RequestRefused('The request did not say whether to work against the environment it saves.');
            }
            return { kind: 'save', replaces: raw.replaces, environment: environmentIn(raw.environment), use: raw.use };
        default:
            throw new RequestRefused('The request asked for something the editor does not do to environments.');
    }
}

// ---- Whether an address answers -------------------------------------------------------------------------------------

/** How long an address is waited for: long enough for a control plane waking from idle, short enough to read as silence. */
export const PROBE_TIMEOUT_MS = 8000;

/** The sign-in methods a control plane's anonymous `GET /api/v1/auth/providers` says, as a person reads them. */
function methodsOf(body: Record<string, unknown>): string {
    const entra = isRecord(body.entra) && body.entra.enabled === true;
    const local = body.local === true;
    if (entra && local) {
        return 'Microsoft Entra or an account';
    }
    if (entra) {
        return 'Microsoft Entra';
    }
    return local ? 'an account' : 'an access token';
}

/**
 * What an answer from `GET /api/v1/auth/providers` says of an address: a control plane answers it with its sign-in
 * methods, to anyone, so anything else at the address is not one (a GUI, a proxy's page, another service).
 */
export function reachOf(status: number, body: unknown): ControlPlaneReach {
    if (status < 200 || status > 299) {
        return {
            answers: false,
            detail: `It answers with HTTP ${status}, so no control plane is at this address. Check the address, and its path when the control plane has one.`,
        };
    }
    if (!isRecord(body) || typeof body.local !== 'boolean' || !isRecord(body.entra)) {
        return { answers: false, detail: 'It answers, but not as a control plane: it may be the GUI\'s address rather than the control plane\'s.' };
    }
    return { answers: true, detail: `It answers as a control plane, signing in with ${methodsOf(body)}.` };
}

/** Why a call did not reach an address, in a few words: the transport's own code when it gives one. */
function whyNot(error: unknown): string {
    if (error instanceof Error && (error.name === 'TimeoutError' || error.name === 'AbortError')) {
        return `nothing came back in ${PROBE_TIMEOUT_MS / 1000} seconds`;
    }
    const cause = error instanceof Error ? (error as Error & { cause?: unknown }).cause : undefined;
    const code = isRecord(cause) && typeof cause.code === 'string' ? cause.code : undefined;
    const message = error instanceof Error ? error.message : String(error);
    return code === undefined ? message : `${message} (${code})`;
}

/**
 * Asks an address whether it is a control plane, by the one call a control plane answers anyone: its sign-in methods. No
 * credential is sent, so asking an address a person is still typing gives nothing away.
 */
export async function probeControlPlane(
    url: string,
    userAgent: string,
    fetchFn: typeof fetch = fetch,
    timeoutMs: number = PROBE_TIMEOUT_MS
): Promise<ControlPlaneReach> {
    const problem = addressProblem(url);
    if (problem !== null) {
        return { answers: false, detail: problem };
    }
    let response: Response;
    try {
        response = await fetchFn(`${savedAddress(url)}/api/v1/auth/providers`, {
            headers: { accept: 'application/json', 'user-agent': userAgent },
            signal: AbortSignal.timeout(timeoutMs),
        });
    } catch (error) {
        return { answers: false, detail: `It does not answer: ${whyNot(error)}. Check the address, the network, and that it is running.` };
    }
    let body: unknown;
    try {
        body = await response.json();
    } catch {
        body = undefined;
    }
    return reachOf(response.status, body);
}

// ---- A setting the window has not loaded ----------------------------------------------------------------------------

/**
 * A setting of the extension that VS Code, in this window, has no record of yet: the extension was installed or updated
 * under a window that has not reloaded since, so the window still holds the settings of the version before, and VS Code
 * refuses to write one it does not know, in words that do not say what to do. Reloading the window loads them.
 */
export class SettingNotLoaded extends Error {
    readonly setting: string;

    constructor(setting: string) {
        super(
            `This window has not loaded the setting ${setting}, which this version of the extension adds, so VS Code cannot `
            + 'save it: the extension was installed or updated after the window opened. Reload the window (Developer: '
            + 'Reload Window) and save again.'
        );
        this.setting = setting;
    }
}

/** Whether an error VS Code gave for a write is its refusal of a setting it has no record of. */
export function isUnknownSettingRefusal(error: unknown): boolean {
    const message = error instanceof Error ? error.message : String(error);
    return /is not a registered configuration/i.test(message);
}
