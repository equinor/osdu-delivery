import * as path from 'path';
import * as vscode from 'vscode';
import { McpServer } from '../../../../sqlflow/tools/sqlflow-vscode/src/mcp/mcpServer';
import { templateFileName } from './runOptions';

interface Releases {
    releases: { name: string; local?: boolean; publishedUtc?: string | null }[];
}

interface SchemaIndex {
    schemas: { kind: string; entityType: string; version: string; status?: string | null }[];
}

interface SchemaFile {
    kind: string;
    version: string;
    schema: Record<string, unknown>;
}

export interface KindInRelease {
    release: string;
    kind: string;
}

/** Picks a kind from the OSDU data definitions: a release, then what it publishes, so nothing is typed from memory. */
export async function pickKind(mcp: McpServer, purpose: string): Promise<KindInRelease | undefined> {
    // Only a release in the control plane's local copy can be read: the rest are names until someone syncs them.
    const releases = (await mcp.call<Releases>('delivery_osdu_schemas')).releases.filter((entry) => entry.local);
    if (releases.length === 0) {
        void vscode.window.showInformationMessage('The control plane holds no release of the OSDU data definitions yet: sync one from the Templates page.');
        return undefined;
    }
    const release = await vscode.window.showQuickPick(
        releases.map((entry) => ({ label: entry.name, description: entry.publishedUtc?.slice(0, 10) })),
        { placeHolder: `${purpose}: which release?`, ignoreFocusOut: true }
    );
    if (!release) {
        return undefined;
    }

    const { schemas } = await mcp.call<SchemaIndex>('delivery_osdu_schemas', { release: release.label });
    const kind = await vscode.window.showQuickPick(
        schemas.map((schema) => ({ label: schema.kind, description: schema.status ?? undefined })),
        { placeHolder: `${purpose}: which kind?`, matchOnDescription: true, ignoreFocusOut: true }
    );
    return kind ? { release: release.label, kind: kind.label } : undefined;
}

/** The template folder a new template goes in: the nearest on the open document's path, else one the person picks. */
async function templateFolder(): Promise<vscode.Uri | undefined> {
    const open = vscode.window.activeTextEditor?.document.uri;
    const root = open ? vscode.workspace.getWorkspaceFolder(open) : vscode.workspace.workspaceFolders?.[0];
    if (open && root) {
        let folder = path.dirname(open.fsPath);
        while (folder.startsWith(root.uri.fsPath)) {
            const candidate = vscode.Uri.file(path.join(folder, 'templates'));
            const found = await Promise.resolve(vscode.workspace.fs.stat(candidate)).then(() => true, () => false);
            if (found) {
                return candidate;
            }
            const parent = path.dirname(folder);
            if (parent === folder) {
                break;
            }
            folder = parent;
        }
    }

    const picked = await vscode.window.showOpenDialog({
        canSelectFolders: true,
        canSelectFiles: false,
        defaultUri: root?.uri,
        openLabel: 'Keep the template here',
        title: 'A folder named templates, which a document reads when it is on the document\'s path',
    });
    return picked?.[0];
}

/** Brings a template into the working copy, where a mapping reads it, as the GUI's Templates page downloads one. */
export async function fetchTemplate(mcp: McpServer, product: string): Promise<void> {
    const chosen = await pickKind(mcp, 'Fetch a template');
    if (!chosen) {
        return;
    }
    const file = await mcp.call<SchemaFile>('delivery_osdu_schemas', { release: chosen.release, kind: chosen.kind });
    const folder = await templateFolder();
    if (!folder) {
        return;
    }

    const target = vscode.Uri.joinPath(folder, templateFileName(file.kind));
    await vscode.workspace.fs.writeFile(target, Buffer.from(`${JSON.stringify(file.schema, null, 2)}\n`));
    void vscode.window.showInformationMessage(`${product}: ${file.kind} (version ${file.version}) is in ${vscode.workspace.asRelativePath(target)}. Commit it, and the next sync records it.`);
    await vscode.window.showTextDocument(target, { preview: true });
}
