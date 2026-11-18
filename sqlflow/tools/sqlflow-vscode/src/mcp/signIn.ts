import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { credentialFile } from '../gui/credentials';
import { McpServer } from './mcpServer';
import { parseAuthStatus, parseSignIn, SignInNeeded, SignInRequest, ToolFailed } from './toolAnswer';

/** Who is signed in: the person, and what the control plane lets them do. */
export interface SignedIn {
    subject: string;
    /** The person's role; null for a credential with no account behind it. */
    role: string | null;
    scopes: string[];
}

/** The longest a browser sign-in is waited for: the code's own life is the control plane's, and it says when it ends. */
const SIGN_IN_WAIT_MS = 10 * 60 * 1000;
/** A little past the five seconds the device grant asks, since a poll sooner than that is told to slow down. */
const POLL_MS = 5500;

/**
 * Who is signed in. The sign-in belongs to the MCP server: it keeps the session, renews it and sends it with every call,
 * in the file `sqlflow login` keeps, so a person signs in once for this extension, for every assistant that uses the same
 * server and for the command line, whichever of them they sign in with. Signing in here is the
 * server's device grant with the browser in front of it: the browser opens the control plane's approval, which goes on
 * to the GUI, where the person signs in the way they always do (Microsoft Entra single sign-on, or an account), and
 * approves the code this editor shows.
 */
export class SignIn implements vscode.Disposable {
    private readonly mcp: McpServer;
    private readonly product: string;
    private readonly onChange = new vscode.EventEmitter<void>();
    readonly changed = this.onChange.event;
    private readonly onWaiting = new vscode.EventEmitter<void>();
    /** Fires when a browser sign-in starts waiting for the person, and when it stops, however it ended. */
    readonly waitingChanged = this.onWaiting.event;
    private person: string | undefined;
    private held: SignedIn | undefined;
    private why: string | undefined;
    private waiting: { request: SignInRequest; stop: vscode.CancellationTokenSource } | undefined;
    private readonly watch: fs.FSWatcher | undefined;

    constructor(mcp: McpServer, product: string) {
        this.mcp = mcp;
        this.product = product;
        // Another window, an assistant or `sqlflow login` may sign in or out: every client keeps the same file, and a
        // server takes up what changed there on its next call.
        this.watch = watchFile(credentialFile(), () => void this.check());
    }

    dispose(): void {
        this.watch?.close();
        this.waiting?.stop.cancel();
        this.onChange.dispose();
        this.onWaiting.dispose();
    }

    /** The person signed in, as last checked. */
    get who(): string | undefined {
        return this.person;
    }

    /** Who is signed in, with the role and scopes the control plane gave them, as last checked. */
    get identity(): SignedIn | undefined {
        return this.held;
    }

    /** Why nobody is signed in, when the last check said more than that nobody is: the control plane did not answer, say. */
    get problem(): string | undefined {
        return this.why;
    }

    /** The browser sign-in waiting for the person's approval, while one is. */
    get pending(): SignInRequest | undefined {
        return this.waiting?.request;
    }

    /** Stops waiting for the browser sign-in, if one waits: the person signs in again when they want to. */
    cancel(): void {
        this.waiting?.stop.cancel();
    }

    /** Asks the server who its credential is. Reachable or not, this never throws: no answer reads as signed out. */
    async check(): Promise<boolean> {
        try {
            const status = parseAuthStatus(await this.mcp.text('check_auth_status'));
            if (status.state === 'signed-in') {
                this.set({ subject: status.who ?? 'signed in', role: status.role ?? null, scopes: status.scopes ?? [] }, undefined);
            } else {
                this.set(undefined, status.state === 'signed-out' && !/^Not authenticated/i.test(status.reason) ? status.reason : undefined);
            }
        } catch (err) {
            this.set(undefined, err instanceof SignInNeeded ? undefined : err instanceof Error ? err.message : String(err));
        }
        return this.person !== undefined;
    }

    private set(identity: SignedIn | undefined, problem: string | undefined): void {
        const changed = JSON.stringify(identity) !== JSON.stringify(this.held) || problem !== this.why;
        this.held = identity;
        this.person = identity?.subject;
        this.why = problem;
        void vscode.commands.executeCommand('setContext', 'sqlflow.signedIn', identity !== undefined);
        if (changed) {
            this.onChange.fire();
        }
    }

    /**
     * Signs in through the browser. While it waits for the person, every view that shows the sign-in shows its code, and
     * asking again opens the same page again rather than starting a second sign-in with another code.
     */
    async signIn(): Promise<boolean> {
        if (this.waiting !== undefined) {
            await vscode.env.openExternal(vscode.Uri.parse(this.waiting.request.url));
            return false;
        }

        let request: SignInRequest;
        try {
            request = parseSignIn(await this.mcp.text('login'));
        } catch (err) {
            void vscode.window.showErrorMessage(`${this.product}: sign-in could not start. ${err instanceof Error ? err.message : err}`);
            return false;
        }

        const stop = new vscode.CancellationTokenSource();
        this.waiting = { request, stop };
        this.waitingNow();
        try {
            await vscode.env.clipboard.writeText(request.code);
            await vscode.env.openExternal(vscode.Uri.parse(request.url));
            return await vscode.window.withProgress(
                {
                    location: vscode.ProgressLocation.Notification,
                    title: `${this.product}: approve the sign-in in your browser. Code ${request.code} (copied)`,
                    cancellable: true,
                },
                async (_progress, cancel) => {
                    // Cancelled from the notification or from a view that shows the code: either stops the one wait.
                    const linked = cancel.onCancellationRequested(() => stop.cancel());
                    try {
                        return await this.waitForApproval(stop.token);
                    } finally {
                        linked.dispose();
                    }
                }
            );
        } finally {
            this.waiting = undefined;
            stop.dispose();
            this.waitingNow();
        }
    }

    /** Says that a sign-in started or stopped waiting: to the views, and to the command that cancels it. */
    private waitingNow(): void {
        void vscode.commands.executeCommand('setContext', 'sqlflow.signingIn', this.waiting !== undefined);
        this.onWaiting.fire();
    }

    private async waitForApproval(cancel: vscode.CancellationToken): Promise<boolean> {
        const deadline = Date.now() + SIGN_IN_WAIT_MS;
        let wait = POLL_MS;
        while (Date.now() < deadline && !cancel.isCancellationRequested) {
            await pause(wait, cancel);
            if (cancel.isCancellationRequested) {
                break;
            }
            let status;
            try {
                status = parseAuthStatus(await this.mcp.text('check_auth_status'));
            } catch (err) {
                if (err instanceof ToolFailed) {
                    continue;
                }
                throw err;
            }
            switch (status.state) {
                case 'signed-in':
                    await this.check();
                    void vscode.window.showInformationMessage(`${this.product}: signed in as ${this.person}.`);
                    return true;
                case 'slow-down':
                    wait += POLL_MS;
                    break;
                case 'denied':
                case 'expired':
                    void vscode.window.showWarningMessage(`${this.product}: the sign-in was ${status.state === 'denied' ? 'denied' : 'not approved in time'}.`);
                    return false;
                default:
                    break;
            }
        }
        return false;
    }

    /** Signs in with a token a person pasted, for a place no browser reaches. */
    async signInWithToken(): Promise<boolean> {
        const token = await vscode.window.showInputBox({
            prompt: `Paste a ${this.product} access token`,
            password: true,
            ignoreFocusOut: true,
        });
        if (!token) {
            return false;
        }
        try {
            await this.mcp.text('set_access_token', { token: token.trim() });
        } catch (err) {
            void vscode.window.showErrorMessage(`${this.product}: the token was not taken. ${err instanceof Error ? err.message : err}`);
            return false;
        }
        return this.check();
    }

    async signOut(): Promise<void> {
        await this.mcp.text('logout');
        this.set(undefined, undefined);
        void vscode.window.showInformationMessage(`${this.product}: signed out.`);
    }
}

/** Waits `ms`, or less when the wait is cancelled first, so a cancelled sign-in stops at once rather than at its next poll. */
function pause(ms: number, cancel: vscode.CancellationToken): Promise<void> {
    return new Promise((resolve) => {
        const timer = setTimeout(done, ms);
        const cancelled = cancel.onCancellationRequested(done);
        function done(): void {
            clearTimeout(timer);
            cancelled.dispose();
            resolve();
        }
    });
}

/** Watches a file by its folder, which outlives it. A folder that is not there yet means nobody has signed in on this
 * machine; the sign-in here makes it, and this window already knows. */
function watchFile(file: string, changed: () => void): fs.FSWatcher | undefined {
    try {
        let timer: NodeJS.Timeout | undefined;
        return fs.watch(path.dirname(file), (_event, name) => {
            if (name === path.basename(file)) {
                clearTimeout(timer);
                timer = setTimeout(changed, 500);
            }
        });
    } catch {
        return undefined;
    }
}
