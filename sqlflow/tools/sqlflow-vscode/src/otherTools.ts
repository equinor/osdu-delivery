import * as vscode from 'vscode';
import { bundledCli } from './cli';
import { claudeHere, registerWithClaude, serverRegistration, unregisterFromClaude } from './mcp/claudeCode';
import { McpServer, serverEnvironment } from './mcp/mcpServer';
import { MODES } from './mcp/mcpSetup';
import { stableCopy, userBinFolder } from './userBin';
import { addToUserPath, removeFromUserPath } from './userPath';

/** Set when the person undid it, so a sign-in does not do it again; the command that does it clears it. */
const UNDONE = 'sqlflow.otherTools.undone';
/** What was last done, so a sign-in with nothing changed does nothing and says nothing. */
const DONE = 'sqlflow.otherTools.done';
/** The names the server was registered with Claude Code under, so undoing removes exactly those. */
const CLAUDE_NAMES = 'sqlflow.otherTools.claudeNames';

/**
 * What a person who signs in to the extension has in their other tools too: the `sqlflow` command line on their own PATH
 * (every terminal, so the shells of Claude Code and of Copilot's command line as well as the editor's), and the MCP
 * server registered with Claude Code for all its sessions. Both run from `~/.sqlflow/bin`, which an update refreshes in
 * place, and both sign in with the extension's sign-in. It is done when they sign in, again when what it rests on
 * changes (the control plane they use), and undone by its command; once undone, only its command does it again.
 */
export class OtherTools {
    private readonly context: vscode.ExtensionContext;
    private readonly product: string;
    private readonly mcp: McpServer;
    private readonly output: vscode.LogOutputChannel;
    private running: Promise<void> = Promise.resolve();

    constructor(context: vscode.ExtensionContext, product: string, mcp: McpServer, output: vscode.LogOutputChannel) {
        this.context = context;
        this.product = product;
        this.mcp = mcp;
        this.output = output;
    }

    /** After a sign-in, or a change to the control plane while signed in: does what is not done yet, and says so. */
    signedIn(): void {
        if (this.context.globalState.get<boolean>(UNDONE) === true) {
            return;
        }
        // A test run signs in to a control plane of its own, and must not change the PATH or the Claude Code of the
        // machine it runs on; its commands still do, when a test asks for them.
        if (this.context.extensionMode === vscode.ExtensionMode.Test) {
            this.output.info('[other tools] not offered to other tools: the extension runs under tests.');
            return;
        }
        void this.queue(() => this.share(false));
    }

    /** The command: does it all again, and turns doing it on sign-in back on if it was undone. */
    async shareNow(): Promise<void> {
        await this.context.globalState.update(UNDONE, undefined);
        await this.queue(() => this.share(true));
    }

    /** The undo: takes the command line off the PATH and the server out of Claude Code, and keeps a sign-in from redoing it. */
    async unshareNow(): Promise<void> {
        await this.queue(() => this.unshare());
    }

    /** One change at a time: a sign-in during the command waits for it, rather than writing the same files beside it. */
    private queue(work: () => Promise<void>): Promise<void> {
        const next = this.running.then(work, work);
        this.running = next.catch(() => undefined);
        return next;
    }

    private async share(asked: boolean): Promise<void> {
        const server = this.mcp.binary();
        const cli = bundledCli(this.context.extensionPath);
        if (server === null && cli === undefined) {
            const message = `${this.product}: this installation carries neither the command line nor the MCP server, so there is nothing to make available to your other tools.`;
            this.output.warn(`[other tools] ${message}`);
            if (asked) {
                void vscode.window.showWarningMessage(message);
            }
            return;
        }

        const folder = userBinFolder();
        const env = serverEnvironment();
        try {
            // Refreshed on every sign-in, so an update reaches the other tools without being registered again.
            const stableServer = server === null ? undefined : stableCopy(server, folder);
            if (cli !== undefined) {
                stableCopy(cli, folder);
            }
            const claude = claudeHere();
            const state = JSON.stringify({ folder, server: stableServer, cli: cli !== undefined, claude: claude?.program, env });
            if (!asked && this.context.globalState.get<string>(DONE) === state) {
                return;
            }

            const done: string[] = [];
            if (cli !== undefined) {
                const change = await addToUserPath(folder, this.product);
                done.push(change.changed.length > 0 ? `sqlflow is on your PATH (${change.changed.join(', ')})` : 'sqlflow is on your PATH');
            }
            if (stableServer !== undefined && claude !== undefined) {
                const registrations = await Promise.all(MODES.map((mode) => serverRegistration(stableServer, mode.id, env)));
                await registerWithClaude(claude, registrations, env);
                const names = registrations.map((registration) => registration.name);
                await this.context.globalState.update(CLAUDE_NAMES, names);
                done.push(`Claude Code has the MCP server (${names.join(', ')})`);
            } else if (stableServer !== undefined) {
                this.output.info('[other tools] Claude Code was not found, so the MCP server was not registered with it.');
            }
            await this.context.globalState.update(DONE, state);
            if (done.length === 0) {
                // A build with the server but no command line, on a machine without Claude Code: nothing to say unless asked.
                if (asked) {
                    void vscode.window.showInformationMessage(`${this.product}: Claude Code is not installed here, and this installation carries no command line, so nothing was changed.`);
                }
                return;
            }
            this.output.info(`[other tools] ${done.join('; ')}.`);

            const pick = await vscode.window.showInformationMessage(
                `${this.product}: ${done.join(', and ')}, signed in as you. New terminals and Claude Code sessions have them; a program already running, VS Code included, has the PATH once it is restarted.`,
                'Undo'
            );
            if (pick === 'Undo') {
                await this.unshareNow();
            }
        } catch (err) {
            const message = err instanceof Error ? err.message : String(err);
            this.output.error(`[other tools] ${message}`);
            void vscode.window.showWarningMessage(`${this.product}: the command line and the MCP server could not be made available to your other tools. ${message}`);
        }
    }

    private async unshare(): Promise<void> {
        await this.context.globalState.update(UNDONE, true);
        await this.context.globalState.update(DONE, undefined);
        try {
            const undone: string[] = [];
            const change = await removeFromUserPath(userBinFolder());
            if (change.changed.length > 0) {
                undone.push(`sqlflow is off your PATH (${change.changed.join(', ')})`);
            }
            const claude = claudeHere();
            const names = this.context.globalState.get<string[]>(CLAUDE_NAMES) ?? [];
            if (claude !== undefined && names.length > 0) {
                const removed = await unregisterFromClaude(claude, names);
                if (removed.length > 0) {
                    undone.push(`Claude Code no longer has the MCP server (${removed.join(', ')})`);
                }
            }
            await this.context.globalState.update(CLAUDE_NAMES, undefined);
            const said = undone.length > 0 ? `${undone.join(', and ')}.` : 'your other tools had neither the command line nor the MCP server from here.';
            this.output.info(`[other tools] ${said}`);
            void vscode.window.showInformationMessage(
                `${this.product}: ${said} The editor's own terminals and chat keep both. Signing in again does not bring them back; "Make the Command Line and Server Available to Other Tools" does.`
            );
        } catch (err) {
            const message = err instanceof Error ? err.message : String(err);
            this.output.error(`[other tools] ${message}`);
            void vscode.window.showWarningMessage(`${this.product}: the command line and the MCP server could not be taken out of your other tools. ${message}`);
        }
    }
}
