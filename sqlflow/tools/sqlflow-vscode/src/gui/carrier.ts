import {
    CARRIED_METHODS,
    carriedBytes,
    carriedPath,
    passedHeaders,
    type CarriedBody,
    type HostMessage,
    type PageMessage,
} from '../../../../gui/src/embed/protocol.ts';

/** Where a page's calls go, and under whose credential, as they stand when a call is made. */
export interface CarrierAccess {
    /** The control plane's base address. */
    apiBaseUrl: string;
    /** The credential to present; null while nobody is signed in. */
    token: string | null;
}

/** What the carrier is given: how to learn where calls go, and what to do when the control plane refuses a credential. */
export interface CarrierOptions {
    /** Who the carrier says it is to the control plane: the extension's name and version. */
    userAgent: string;
    access(): Promise<CarrierAccess>;
    /**
     * The control plane answered 401 to this credential. Has whoever keeps the sign-in look at it again (a session may
     * have been renewed, or ended, since the call was made) and gives the credential as it stands afterwards.
     */
    refused(token: string): Promise<string | null>;
    /** The control plane refused the credential and looking again gave no other: nobody is signed in any more. */
    signedOut(): void;
}

type CarriedRequest = Extract<PageMessage, { type: 'request' }>;

/** A call the carrier does not make, because a page may not ask for it. */
class NotCarried extends Error {}

/** Whether an answer is text as a page reads it, by what the control plane says it is. */
function isText(contentType: string): boolean {
    return /^(text\/|application\/(json|problem\+json|x-ndjson|xml|yaml|javascript))/i.test(contentType) || /\+json/i.test(contentType);
}

function bodyOf(body: CarriedBody | undefined): string | Uint8Array | undefined {
    if (body === undefined) {
        return undefined;
    }
    return 'text' in body ? body.text : carriedBytes(body.bytes);
}

/**
 * Carries a page's calls to the control plane. A hosted page cannot call it itself: its own origin is not one the
 * control plane's CORS allows, its content security policy admits no host, and the credential is not the page's to
 * hold. So a page hands each call over (protocol.ts) and this makes it, with the person's credential, and hands back
 * what the control plane answered: status, headers and body as they were, the body in pieces where the page asked for a
 * stream.
 *
 * What a page may ask is bounded here rather than trusted: only the control plane's own address, only the methods the
 * API has, and only the headers a page has reason to set. The credential is added here and a page's own
 * `Authorization` is dropped, so nothing a page sends can pick whom a call is made as or where it goes.
 */
export class Carrier {
    private readonly options: CarrierOptions;
    private readonly send: (message: HostMessage) => void;
    private readonly inFlight = new Map<number, AbortController>();

    constructor(options: CarrierOptions, send: (message: HostMessage) => void) {
        this.options = options;
        this.send = send;
    }

    /** Makes a page's call and answers it. Never throws: whatever goes wrong is the call's answer. */
    async carry(request: CarriedRequest): Promise<void> {
        const abort = new AbortController();
        this.inFlight.set(request.id, abort);
        try {
            await this.make(request, abort.signal);
        } catch (error) {
            const aborted = abort.signal.aborted;
            this.send({
                type: 'failed',
                id: request.id,
                aborted,
                reason: aborted
                    ? 'The call was dropped.'
                    : error instanceof NotCarried ? error.message : `The control plane did not answer: ${reasonOf(error)}`,
            });
        } finally {
            this.inFlight.delete(request.id);
        }
    }

    /** Drops a call the page no longer waits for. */
    abort(id: number): void {
        this.inFlight.get(id)?.abort();
    }

    /** Drops every call in flight: the page is gone. */
    dispose(): void {
        for (const abort of this.inFlight.values()) {
            abort.abort();
        }
        this.inFlight.clear();
    }

    private async make(request: CarriedRequest, signal: AbortSignal): Promise<void> {
        const access = await this.options.access();
        const target = carriedPath(request.url, access.apiBaseUrl);
        if (target === null) {
            throw new NotCarried('A page may call the control plane it is signed in to, and no other address.');
        }
        const method = request.method.toUpperCase();
        if (!CARRIED_METHODS.includes(method)) {
            throw new NotCarried(`A page may not make a ${method} call.`);
        }

        const url = new URL(target, new URL(access.apiBaseUrl).origin);
        const body = bodyOf(request.body);
        const call = (token: string | null): Promise<Response> => fetch(url, {
            method,
            headers: {
                ...passedHeaders(request.headers),
                'User-Agent': this.options.userAgent,
                ...(token === null ? {} : { Authorization: `Bearer ${token}` }),
            },
            body: method === 'GET' ? undefined : body,
            signal,
            redirect: 'error',
        });

        let response = await call(access.token);
        if (response.status === 401 && access.token !== null) {
            // The credential in hand was refused. A session renewed between reading it and presenting it is the ordinary
            // case, and the call is made once more with the credential as it now stands; none, or the same one, means the
            // sign-in is over, which every page is then told.
            const current = await this.options.refused(access.token);
            if (current !== null && current !== access.token) {
                await response.body?.cancel();
                response = await call(current);
            }
            if (response.status === 401) {
                this.options.signedOut();
            }
        }

        const headers: Record<string, string> = {};
        response.headers.forEach((value, name) => {
            headers[name] = value;
        });
        const head = { type: 'response' as const, id: request.id, status: response.status, statusText: response.statusText, headers };

        if (request.stream && response.ok && response.body !== null) {
            this.send({ ...head, streamed: true });
            const decoder = new TextDecoder();
            const reader = response.body.getReader();
            for (;;) {
                const { done, value } = await reader.read();
                if (done) {
                    break;
                }
                const text = decoder.decode(value, { stream: true });
                if (text !== '') {
                    this.send({ type: 'chunk', id: request.id, text });
                }
            }
            const rest = decoder.decode();
            if (rest !== '') {
                this.send({ type: 'chunk', id: request.id, text: rest });
            }
            this.send({ type: 'end', id: request.id });
            return;
        }

        const bytes = new Uint8Array(await response.arrayBuffer());
        const answered: CarriedBody | undefined = bytes.byteLength === 0
            ? undefined
            : isText(headers['content-type'] ?? '')
                ? { text: new TextDecoder().decode(bytes) }
                : { bytes };
        this.send({ ...head, streamed: false, body: answered });
    }
}

function reasonOf(error: unknown): string {
    if (!(error instanceof Error)) {
        return String(error);
    }
    // Node's fetch says "fetch failed" and keeps why (refused, no such host, a certificate) in the cause.
    const cause = (error as { cause?: unknown }).cause;
    return cause instanceof Error && cause.message !== '' ? `${error.message}: ${cause.message}` : error.message;
}
