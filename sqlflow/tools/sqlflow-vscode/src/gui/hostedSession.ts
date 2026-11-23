import * as vscode from 'vscode';
import { PROTOCOL_VERSION, type HostStart } from '../../../../gui/src/embed/protocol';
import { McpServer } from '../mcp/mcpServer';
import { SignIn } from '../mcp/signIn';
import { CarrierAccess } from './carrier';
import { currentCredential } from './credentials';
import { Environments } from './environments';
import { sessionProblemText } from './sessionText';

/**
 * What the pages this extension shows run under: which control plane, and who is signed in to it, or the browser sign-in
 * waiting for them. The first two are the MCP server's to say, as they are for every view and command here: it names the
 * control plane it talks to (the setting, or the one it was last given) and keeps the sign-in, in the file the command
 * line shares. The pages read the estate's own API with that sign-in, through the carrier, so a page shows what the same
 * person sees in the GUI. With them go the person's saved environments, and which of them the control plane is, which
 * every surface shows in its stage's colour.
 */
export class HostedSession implements vscode.Disposable {
    private readonly mcp: McpServer;
    private readonly signIn: SignIn;
    private readonly product: string;
    private readonly environments: Environments;
    private readonly onChange = new vscode.EventEmitter<void>();
    /** Fires when who is signed in, or where to, may have changed: every page is then started again with what holds now. */
    readonly changed = this.onChange.event;
    private readonly subscriptions: vscode.Disposable[];
    private controlPlane: Promise<string> | undefined;

    constructor(mcp: McpServer, signIn: SignIn, product: string, environments: Environments) {
        this.mcp = mcp;
        this.signIn = signIn;
        this.product = product;
        this.environments = environments;
        this.subscriptions = [
            signIn.changed(() => this.onChange.fire()),
            environments.changed(() => this.onChange.fire()),
            signIn.waitingChanged(() => this.onChange.fire()),
            // A server that stopped is one the settings restarted, or one that died: the next one may name another
            // control plane, so what this one said is not kept.
            mcp.stateChanged(() => {
                if (!mcp.running) {
                    this.controlPlane = undefined;
                    this.onChange.fire();
                }
            }),
            vscode.workspace.onDidChangeConfiguration((event) => {
                if (event.affectsConfiguration('sqlflow.guiUrl')) {
                    this.onChange.fire();
                }
            }),
        ];
    }

    dispose(): void {
        this.subscriptions.forEach((subscription) => subscription.dispose());
        this.onChange.dispose();
    }

    /** The control plane the server talks to. */
    controlPlaneUrl(): Promise<string> {
        this.controlPlane ??= this.mcp.call<{ url: string }>('get_control_plane_url').then(
            (answer) => answer.url.replace(/\/+$/, ''),
            (error: unknown) => {
                this.controlPlane = undefined;
                throw error;
            }
        );
        return this.controlPlane;
    }

    /** What a page starts with, as it holds now. Never throws: what could not be learned is said in the start itself. */
    async start(): Promise<HostStart> {
        const gui = vscode.workspace.getConfiguration('sqlflow').get<string>('guiUrl', '').trim().replace(/\/+$/, '');
        const base = { protocol: PROTOCOL_VERSION, product: this.product, guiBaseUrl: gui === '' ? null : gui };
        let apiBaseUrl: string;
        try {
            apiBaseUrl = await this.controlPlaneUrl();
        } catch (error) {
            return {
                ...base,
                ...this.environments.describe(null),
                apiBaseUrl: null,
                session: null,
                sessionProblem: error instanceof Error ? error.message : String(error),
                signingIn: null,
            };
        }
        const identity = this.signIn.identity;
        return {
            ...base,
            ...this.environments.describe(apiBaseUrl),
            apiBaseUrl,
            session: identity === undefined ? null : { subject: identity.subject, role: identity.role, scopes: identity.scopes },
            sessionProblem: identity === undefined ? sessionProblemText(this.signIn.problem, apiBaseUrl) : null,
            signingIn: identity === undefined ? (this.signIn.pending ?? null) : null,
        };
    }

    /** Where a call goes and the credential it presents, read as the call is made. */
    async access(): Promise<CarrierAccess> {
        const apiBaseUrl = await this.controlPlaneUrl();
        return { apiBaseUrl, token: currentCredential(apiBaseUrl) };
    }

    /**
     * The control plane refused a credential. The server looks at its sign-in again, which renews a session that is due,
     * takes up one another client renewed and lets go of one that has ended, and the credential is read again after it.
     */
    async refused(): Promise<string | null> {
        await this.signIn.check();
        return currentCredential(await this.controlPlaneUrl());
    }
}
