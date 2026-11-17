import { execFile } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import { promisify } from 'util';
import * as vscode from 'vscode';
import { stableCopy, upToDateCopy } from '../userBin';
import { claudeHere } from './claudeCode';
import { McpServer, serverEnvironment } from './mcpServer';

const run = promisify(execFile);

/** The two ways an assistant's session can be registered: questions about the estate, and questions answered from data. */
export const MODES = [
    { id: 'estate', label: 'estate', detail: 'Flows, runs, lineage, the catalog and the docs.' },
    { id: 'data', label: 'data', detail: 'Questions answered from the data an approved AI context opens.' },
];

export interface McpRegistration {
    /** The id the manifest's `mcpServerDefinitionProviders` declares. */
    providerId: string;
    product: string;
    program: string;
    version: string;
}

/**
 * Hands the server to VS Code's own assistants, so an agent chat has this estate's tools the moment the extension is
 * installed, with nothing to configure. One definition per mode, as the server asks an editor to hold them: a chat turns
 * on the one it needs, and each lists only its mode's tools. Each runs the same program under the same sign-in as this
 * extension, so signing in here signs them in.
 */
export function registerWithEditor(registration: McpRegistration, mcp: McpServer, changed: vscode.Event<void>): vscode.Disposable {
    // An editor built on VS Code that has no MCP support of its own has no such function.
    if (typeof vscode.lm?.registerMcpServerDefinitionProvider !== 'function') {
        return new vscode.Disposable(() => undefined);
    }

    const emitter = new vscode.EventEmitter<void>();
    const subscription = changed(() => emitter.fire());
    const provider = vscode.lm.registerMcpServerDefinitionProvider(registration.providerId, {
        onDidChangeMcpServerDefinitions: emitter.event,
        provideMcpServerDefinitions: () => {
            const bundled = mcp.binary();
            if (!bundled) {
                return [];
            }
            // The copy a sign-in keeps in ~/.sqlflow/bin, while it is up to date: the command line the server points an
            // assistant to is then the one beside it there, which a sandboxed shell can be allowed once.
            const binary = upToDateCopy(bundled) ?? bundled;
            return MODES.map((mode) => new vscode.McpStdioServerDefinition(
                `${registration.product} (${mode.label})`,
                binary,
                ['--mode', mode.id],
                serverEnvironment(),
                registration.version
            ));
        },
    });
    return vscode.Disposable.from(provider, subscription, emitter);
}

/**
 * Connects an assistant that runs outside VS Code's own (Claude Code, Cursor, Codex). The registration is the program's
 * own `install` answer, so there is one definition of it. Claude Code is registered through its own command line, as a
 * sign-in registers it (`registerWithClaudeCode`); for Cursor and Codex the answer is shown for the person to add.
 */
export async function connectAssistant(
    registration: McpRegistration,
    mcp: McpServer,
    registerWithClaudeCode: () => Promise<void>
): Promise<void> {
    const binary = mcp.binary();
    if (!binary) {
        void vscode.window.showErrorMessage(`${registration.program} was not found. Install the packaged extension, or set sqlflow.mcpBinaryPath.`);
        return;
    }

    const client = await vscode.window.showQuickPick(
        [
            { label: 'Claude Code', id: 'claude' },
            { label: 'Cursor', id: 'cursor' },
            { label: 'Codex', id: 'codex' },
            { label: 'VS Code (GitHub Copilot)', id: 'vscode', detail: 'Already connected by this extension: its tools are listed in the chat.' },
        ],
        { placeHolder: `Which assistant should use the ${registration.product} server?` }
    );
    if (!client) {
        return;
    }
    if (client.id === 'vscode') {
        void vscode.window.showInformationMessage(`${registration.product}: connected. Turn on "${registration.product} (estate)" or "(data)" among the chat's tools.`);
        return;
    }
    if (client.id === 'claude' && claudeHere() !== undefined) {
        await registerWithClaudeCode();
        return;
    }

    const mode = await vscode.window.showQuickPick(
        MODES.map((m) => ({ ...m, description: undefined })),
        { placeHolder: 'Register which kind of question? Register again for the other.' }
    );
    if (!mode) {
        return;
    }

    let stable: string;
    try {
        stable = stableCopy(binary);
        // The command line goes beside it, where the server finds it, so the assistant can script with it too.
        const cli = path.join(path.dirname(binary), process.platform === 'win32' ? 'sqlflow.exe' : 'sqlflow');
        if (fs.existsSync(cli)) {
            stableCopy(cli);
        }
    } catch (err) {
        void vscode.window.showErrorMessage(`${registration.product}: the server could not be copied to ~/.sqlflow/bin. ${err instanceof Error ? err.message : err}`);
        return;
    }
    const { stdout } = await run(stable, ['install', client.id, '--mode', mode.id], { windowsHide: true, env: { ...process.env, ...serverEnvironment() } });
    const answer = stdout.trim();

    if (client.id === 'claude') {
        const terminal = vscode.window.createTerminal(`${registration.product}: connect Claude Code`);
        terminal.show();
        terminal.sendText(answer, false);
        void vscode.window.showInformationMessage(`${registration.product}: press Enter in the terminal to register the server with Claude Code.`);
        return;
    }

    const content = client.id === 'codex'
        ? `# Add to ~/.codex/config.toml\n\n\`\`\`toml\n${answer}\n\`\`\`\n`
        : `# Add to your ${client.label} MCP configuration\n\n\`\`\`json\n${answer}\n\`\`\`\n`;
    const doc = await vscode.workspace.openTextDocument({ language: 'markdown', content });
    await vscode.window.showTextDocument(doc);
}
