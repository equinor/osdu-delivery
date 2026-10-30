import * as path from 'path';
import * as vscode from 'vscode';
import { DocumentIndex } from '../../../../sqlflow/tools/sqlflow-vscode/src/documents/documentIndex';
import { keyPathAtLine } from '../../../../sqlflow/tools/sqlflow-vscode/src/documents/yamlPath';
import {
    checkRecord,
    childrenOf,
    declaredParameters,
    describeVariable,
    parentRecordPath,
    recordEntries,
    recordPath,
    templateKind,
    TemplateVariable,
    templateVariables,
} from './templates';

export interface TemplateFile {
    uri: vscode.Uri;
    kind: string;
}

/**
 * The templates the working copy holds, read from its template folders (folders named `templates`). This is what a
 * mapping fills as a person edits it, before anything is pushed, and a document reads the folders on its path, as a run
 * does: the nearest one that holds the kind wins.
 */
export class WorkingCopyTemplates implements vscode.Disposable {
    private files: Promise<TemplateFile[]> | undefined;
    private readonly parsed = new Map<string, Promise<TemplateVariable[]>>();
    private readonly watcher = vscode.workspace.createFileSystemWatcher('**/templates/*.json');

    constructor() {
        const drop = (): void => {
            this.files = undefined;
            this.parsed.clear();
        };
        this.watcher.onDidCreate(drop);
        this.watcher.onDidChange(drop);
        this.watcher.onDidDelete(drop);
    }

    dispose(): void {
        this.watcher.dispose();
    }

    all(): Promise<TemplateFile[]> {
        this.files ??= this.read();
        return this.files;
    }

    private async read(): Promise<TemplateFile[]> {
        const uris = await vscode.workspace.findFiles('**/templates/*.json', '**/{node_modules,.git,dist,bin,obj}/**', 2000);
        const held = await Promise.all(uris.map(async (uri) => {
            const kind = await this.kindOf(uri);
            return kind ? { uri, kind } : undefined;
        }));
        return held.filter((file): file is TemplateFile => file !== undefined);
    }

    private async kindOf(uri: vscode.Uri): Promise<string | undefined> {
        try {
            return templateKind(JSON.parse(Buffer.from(await vscode.workspace.fs.readFile(uri)).toString('utf8')), path.basename(uri.fsPath));
        } catch {
            return undefined;
        }
    }

    /** The file of a kind a document reads: the folders on its path, nearest first. */
    async forDocument(kind: string, document: vscode.Uri): Promise<TemplateFile | undefined> {
        const withKind = (await this.all()).filter((file) => file.kind === kind);
        const distance = (file: TemplateFile): number => {
            const folder = path.dirname(path.dirname(file.uri.fsPath));
            const relative = path.relative(folder, path.dirname(document.fsPath));
            return relative.startsWith('..') ? Number.MAX_SAFE_INTEGER : relative.split(path.sep).length;
        };
        return withKind.sort((a, b) => distance(a) - distance(b))[0];
    }

    variables(file: TemplateFile): Promise<TemplateVariable[]> {
        const key = file.uri.toString();
        const held = this.parsed.get(key);
        if (held) {
            return held;
        }
        const variables = Promise.resolve(vscode.workspace.fs.readFile(file.uri))
            .then((bytes) => templateVariables(JSON.parse(Buffer.from(bytes).toString('utf8'))));
        this.parsed.set(key, variables);
        return variables;
    }
}

/** A mapping's template, from the text in front of the person. */
function templateOf(text: string): string | undefined {
    return /^template\s*:\s*\r?\n\s+kind\s*:\s*["']?([^\s"'#]+)/m.exec(text)?.[1];
}

function isMapping(document: vscode.TextDocument): boolean {
    return /^documentType\s*:\s*["']?mapping\b/m.test(document.getText());
}

/**
 * Editing a mapping with its template open beside it: the keys it can take as they are typed, what the schema says of
 * the one under the cursor, what is missing as it goes. All of it reads the working copy, so it holds offline and for a
 * template that has not been pushed.
 */
export class MappingEditing implements vscode.CompletionItemProvider, vscode.HoverProvider, vscode.Disposable {
    private readonly diagnostics = vscode.languages.createDiagnosticCollection('osdu-delivery-mapping');
    private readonly subscriptions: vscode.Disposable[] = [];
    private timer: NodeJS.Timeout | undefined;

    constructor(private readonly templates: WorkingCopyTemplates, private readonly documents: DocumentIndex) {
        const check = (document: vscode.TextDocument): void => {
            clearTimeout(this.timer);
            this.timer = setTimeout(() => void this.check(document), 300);
        };
        this.subscriptions.push(
            vscode.workspace.onDidOpenTextDocument(check),
            vscode.workspace.onDidChangeTextDocument((event) => check(event.document)),
            vscode.workspace.onDidCloseTextDocument((document) => this.diagnostics.delete(document.uri))
        );
        vscode.workspace.textDocuments.forEach((document) => void this.check(document));
    }

    dispose(): void {
        clearTimeout(this.timer);
        this.subscriptions.forEach((subscription) => subscription.dispose());
        this.diagnostics.dispose();
    }

    private async variablesFor(document: vscode.TextDocument): Promise<{ kind: string; variables: TemplateVariable[] } | undefined> {
        const kind = templateOf(document.getText());
        const file = kind ? await this.templates.forDocument(kind, document.uri) : undefined;
        return kind && file ? { kind, variables: await this.templates.variables(file) } : undefined;
    }

    private async check(document: vscode.TextDocument): Promise<void> {
        if (!isMapping(document)) {
            return;
        }
        const template = await this.variablesFor(document);
        if (!template) {
            this.diagnostics.delete(document.uri);
            return;
        }
        const name = template.kind.split(':')[2] ?? template.kind;
        this.diagnostics.set(document.uri, checkRecord(document.getText().split(/\r?\n/), template.variables, name).map((finding) => {
            const diagnostic = new vscode.Diagnostic(
                new vscode.Range(finding.line, finding.keyStart, finding.line, finding.keyEnd),
                finding.message,
                vscode.DiagnosticSeverity.Warning
            );
            diagnostic.source = 'template';
            return diagnostic;
        }));
    }

    async provideCompletionItems(document: vscode.TextDocument, position: vscode.Position): Promise<vscode.CompletionItem[]> {
        if (!isMapping(document)) {
            return [];
        }
        const line = document.lineAt(position.line).text;
        const before = line.slice(0, position.character);

        const parameter = /\$param\.\w*$/.test(before);
        if (parameter) {
            return declaredParameters(document.getText().split(/\r?\n/)).map((declared) => {
                const item = new vscode.CompletionItem(declared.name, vscode.CompletionItemKind.Variable);
                item.detail = declared.required ? 'required parameter' : 'parameter';
                item.documentation = declared.description;
                return item;
            });
        }
        if (/\$cache\.\w*$/.test(before)) {
            return this.cachedTypes();
        }

        // A key: the line holds nothing but the start of one.
        if (!/^\s*(-\s+)?[\w]*$/.test(before)) {
            return [];
        }
        const lines = document.getText().split(/\r?\n/);
        const parent = parentRecordPath(lines, position.line);
        const template = parent === null ? undefined : await this.variablesFor(document);
        if (!template || parent === null) {
            return [];
        }
        const filled = new Set(recordEntries(lines).map((entry) => entry.path));
        return childrenOf(template.variables, parent)
            .filter((variable) => !filled.has(variable.path))
            .map((variable) => {
                const name = variable.path.slice(variable.path.lastIndexOf('.') + 1);
                const item = new vscode.CompletionItem(name, vscode.CompletionItemKind.Property);
                item.detail = `${variable.type}${variable.required ? ', required' : ''}`;
                item.documentation = new vscode.MarkdownString(describeVariable(variable));
                item.insertText = `${name}: `;
                item.sortText = `${variable.required ? '0' : '1'}${name}`;
                return item;
            });
    }

    /** The names `$cache.` reads: what the repository's cache flows declare under `types`. */
    private async cachedTypes(): Promise<vscode.CompletionItem[]> {
        const flows = (await this.documents.all()).filter((document) => document.flowType === 'cache');
        const names = new Map<string, string>();
        await Promise.all(flows.map(async (flow) => {
            const text = Buffer.from(await vscode.workspace.fs.readFile(flow.uri)).toString('utf8');
            for (const match of text.matchAll(/^\s+(?:-\s+)?name\s*:\s*["']?([\w.-]+)/gm)) {
                names.set(match[1], flow.relativePath);
            }
        }));
        return [...names].map(([name, flow]) => {
            const item = new vscode.CompletionItem(name, vscode.CompletionItemKind.Reference);
            item.detail = flow;
            return item;
        });
    }

    async provideHover(document: vscode.TextDocument, position: vscode.Position): Promise<vscode.Hover | undefined> {
        if (!isMapping(document)) {
            return undefined;
        }
        const key = keyPathAtLine(document.getText().split(/\r?\n/), position.line);
        const path_ = key ? recordPath(key.path) : null;
        if (!path_) {
            return undefined;
        }
        const template = await this.variablesFor(document);
        const variable = template?.variables.find((candidate) => candidate.path.replace(/\[\]/g, '') === path_.replace(/\[\]/g, ''));
        return variable ? new vscode.Hover(new vscode.MarkdownString(describeVariable(variable))) : undefined;
    }
}
