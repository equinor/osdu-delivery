import * as crypto from 'crypto';
import * as path from 'path';
import * as vscode from 'vscode';
import {
    carriedBytes,
    isGuiPath,
    isHostAction,
    PROTOCOL_VERSION,
    type HostAction,
    type HostMessage,
    type PageMessage,
} from '../../../../gui/src/embed/protocol';
import { DocumentIndex } from '../documents/documentIndex';
import { Carrier } from './carrier';
import { Environments } from './environments';
import { HostedEntry } from './hostedFiles';
import { hostedHtml, messageHtml } from './hostedHtml';
import { HostedSession } from './hostedSession';

/** What every hosted surface is given: where its files are, who it runs as, and what it may ask of the editor. */
export interface HostedSurroundings {
    product: string;
    /** The folder of the hosted build, the only one a webview may read. */
    folder: vscode.Uri;
    session: HostedSession;
    /** The person's saved environments, which the side bar switches between and changes. */
    environments: Environments;
    documents: DocumentIndex;
    output: vscode.LogOutputChannel;
    /** Who the carrier says it is to the control plane. */
    userAgent: string;
    /** Opens a page of the GUI in a tab of the editor. */
    openPage(path: string): void;
}

/** What one surface adds to the wiring every surface shares. */
export interface LinkHooks {
    /** The page shown, and what it is called. */
    located?(path: string, title: string, flowKind: string | undefined): void;
    /**
     * Whether this surface may switch, save and remove the person's environments: the side bar only. A switch moves where
     * the person's sign-in goes, so a page's tab, which shows what the estate holds, is never the surface that asks it.
     */
    environments?: boolean;
    /** The surface has been given what it starts with, and so listens for what follows it. */
    started?(): void;
}

/** The command each thing a page may ask of the editor runs. */
const ACTION_COMMANDS: Readonly<Record<HostAction, string>> = {
    signIn: 'sqlflow.signIn',
    signInWithToken: 'sqlflow.signInWithToken',
    cancelSignIn: 'sqlflow.cancelSignIn',
    signOut: 'sqlflow.signOut',
    manageEnvironments: 'sqlflow.manageEnvironments',
    refreshEstate: 'sqlflow.refreshCatalog',
    connectAssistant: 'sqlflow.setupMcp',
    searchDocs: 'sqlflow.searchDocs',
};

/** How long typing settles before a watched document's text is sent: a page redraws from it, and need not per keystroke. */
const WATCH_SETTLE_MS = 250;

/**
 * One webview's link to the extension: the page it loads, and everything said between them (protocol.ts). The pages'
 * tabs and the side bar are both this, each with what it adds. A message from a webview is read as coming from anyone:
 * a page asks for a call to the control plane, a file of the workspace or an action by name, and each is checked here
 * against what a page may have, so nothing a page says reaches a command, a file or an address of its choosing.
 */
export class HostedLink implements vscode.Disposable {
    private readonly webview: vscode.Webview;
    private readonly around: HostedSurroundings;
    private readonly hooks: LinkHooks;
    private readonly carrier: Carrier;
    private readonly watches = new Map<string, vscode.Disposable>();
    private readonly subscriptions: vscode.Disposable[] = [];
    private ready = false;

    constructor(webview: vscode.Webview, around: HostedSurroundings, hooks: LinkHooks) {
        this.webview = webview;
        this.around = around;
        this.hooks = hooks;
        this.carrier = new Carrier(
            {
                userAgent: around.userAgent,
                access: () => around.session.access(),
                refused: () => around.session.refused(),
                // The session's own check, made when the credential was refused, has already told every surface.
                signedOut: () => undefined,
            },
            (message) => this.post(message)
        );
        this.subscriptions.push(
            webview.onDidReceiveMessage((message: unknown) => this.receive(message)),
            around.session.changed(() => void this.start())
        );
    }

    /** The options a webview showing a hosted surface is made with. */
    static options(folder: vscode.Uri): vscode.WebviewOptions {
        return { enableScripts: true, localResourceRoots: [folder] };
    }

    /** Loads an entry of the hosted build into the webview. */
    load(entry: HostedEntry, surface: 'page' | 'side-bar', title: string, pagePath?: string): void {
        this.ready = false;
        this.webview.html = hostedHtml({
            entry,
            surface,
            title,
            path: pagePath,
            baseUri: this.webview.asWebviewUri(this.around.folder).toString().replace(/\/+$/, ''),
            cspSource: this.webview.cspSource,
            nonce: crypto.randomBytes(16).toString('hex'),
        });
    }

    /** Shows a sentence in place of a surface that could not be loaded. */
    say(message: string): void {
        this.ready = false;
        this.webview.html = messageHtml(message);
    }

    /** Whether the page has said it listens. */
    get listening(): boolean {
        return this.ready;
    }

    /** Says something to the page. Nothing is said before the page has said it listens. */
    post(message: HostMessage): void {
        if (this.ready) {
            void this.webview.postMessage(message);
        }
    }

    dispose(): void {
        this.carrier.dispose();
        this.watches.forEach((watch) => watch.dispose());
        this.watches.clear();
        this.subscriptions.forEach((subscription) => subscription.dispose());
    }

    private async start(): Promise<void> {
        if (this.ready) {
            this.post({ type: 'start', start: await this.around.session.start() });
            this.hooks.started?.();
        }
    }

    private receive(raw: unknown): void {
        if (raw === null || typeof raw !== 'object' || typeof (raw as { type?: unknown }).type !== 'string') {
            return;
        }
        const message = raw as PageMessage;
        switch (message.type) {
            case 'ready':
                if (message.protocol !== PROTOCOL_VERSION) {
                    this.around.output.warn(`[pages] a page speaks protocol ${message.protocol}, this extension ${PROTOCOL_VERSION}.`);
                }
                this.ready = true;
                void this.start();
                return;
            case 'request':
                if (typeof message.id === 'number' && typeof message.url === 'string' && typeof message.method === 'string') {
                    void this.carrier.carry({ ...message, headers: message.headers ?? {}, stream: message.stream === true });
                }
                return;
            case 'abort':
                this.carrier.abort(message.id);
                return;
            case 'located':
                if (isGuiPath(message.path) && typeof message.title === 'string') {
                    this.hooks.located?.(message.path, message.title, typeof message.flowKind === 'string' ? message.flowKind : undefined);
                }
                return;
            case 'open':
                if (isGuiPath(message.path)) {
                    this.around.openPage(message.path);
                }
                return;
            case 'external':
                this.openExternal(message.url);
                return;
            case 'save':
                void this.save(message.name, message.bytes).catch((error: unknown) => this.failed('The file could not be saved', error));
                return;
            case 'act':
                void this.act(message.action).catch((error: unknown) => this.failed('That could not be done', error));
                return;
            case 'watch':
                void this.watch(message.document).catch((error: unknown) => this.failed('The document could not be read', error));
                return;
            case 'environment':
                if (typeof message.id === 'number') {
                    void this.environment(message.id, message.request);
                }
                return;
            case 'unwatch':
                this.watches.get(message.document)?.dispose();
                this.watches.delete(message.document);
                return;
            default:
                return;
        }
    }

    private failed(what: string, error: unknown): void {
        const message = error instanceof Error ? error.message : String(error);
        this.around.output.error(`[pages] ${what}: ${message}`);
        void vscode.window.showErrorMessage(`${this.around.product}: ${what}. ${message}`);
    }

    /** An address outside the GUI opens where the person browses; only an address a browser or a mail client takes. */
    private openExternal(url: unknown): void {
        if (typeof url !== 'string' || !/^(https?|mailto):/i.test(url)) {
            return;
        }
        void vscode.env.openExternal(vscode.Uri.parse(url, true));
    }

    /** Does what the side bar asked of the person's environments, and answers it; any other surface is told it may not. */
    private async environment(id: number, request: unknown): Promise<void> {
        const answer = this.hooks.environments === true
            ? await this.around.environments.handle(request)
            : { ok: false as const, problem: 'Environments are switched and changed in the side bar.' };
        this.post({ type: 'environmentAnswer', id, answer });
    }

    private async act(action: unknown): Promise<void> {
        if (!isHostAction(action)) {
            return;
        }
        await vscode.commands.executeCommand(ACTION_COMMANDS[action]);
    }

    /**
     * The file a page names, when it is a pipeline document of the open workspace: the only files a page is shown. A page
     * names a document by what the extension called it, so anything else is a name the extension never gave.
     */
    private async workspaceDocument(name: unknown): Promise<vscode.Uri | undefined> {
        if (typeof name !== 'string') {
            return undefined;
        }
        let uri: vscode.Uri;
        try {
            uri = vscode.Uri.parse(name, true);
        } catch {
            return undefined;
        }
        if (uri.scheme !== 'file' || vscode.workspace.getWorkspaceFolder(uri) === undefined) {
            return undefined;
        }
        return (await this.around.documents.at(uri))?.uri;
    }

    /** Sends a document's text as the editor holds it, now and after every change, until the page stops watching. */
    private async watch(name: unknown): Promise<void> {
        if (typeof name !== 'string' || this.watches.has(name)) {
            return;
        }
        const uri = await this.workspaceDocument(name);
        if (!uri) {
            this.post({ type: 'document', watched: { document: name, text: null, path: null, unsaved: false } });
            return;
        }

        const send = async (): Promise<void> => {
            try {
                const document = await vscode.workspace.openTextDocument(uri);
                this.post({
                    type: 'document',
                    watched: {
                        document: name,
                        text: document.getText(),
                        path: vscode.workspace.asRelativePath(uri, false).replace(/\\/g, '/'),
                        unsaved: document.isDirty,
                    },
                });
            } catch {
                // The file is gone: the page is told there is no text, which is what it shows.
                this.post({ type: 'document', watched: { document: name, text: null, path: null, unsaved: false } });
            }
        };

        let settle: NodeJS.Timeout | undefined;
        const soon = (): void => {
            clearTimeout(settle);
            settle = setTimeout(() => void send(), WATCH_SETTLE_MS);
        };
        const same = (other: vscode.Uri): boolean => other.toString() === uri.toString();
        const subscriptions = [
            vscode.workspace.onDidChangeTextDocument((event) => same(event.document.uri) && soon()),
            vscode.workspace.onDidSaveTextDocument((document) => same(document.uri) && soon()),
            vscode.workspace.onDidDeleteFiles((event) => event.files.some(same) && soon()),
            vscode.workspace.onDidRenameFiles((event) => event.files.some((file) => same(file.oldUri)) && soon()),
        ];
        this.watches.set(name, new vscode.Disposable(() => {
            clearTimeout(settle);
            subscriptions.forEach((subscription) => subscription.dispose());
        }));
        await send();
    }

    /** Hands the person a file a page made: they say where it goes, and may open it from there. */
    private async save(name: unknown, bytes: unknown): Promise<void> {
        const fileName = typeof name === 'string' && name.trim() !== '' ? path.basename(name.replace(/\\/g, '/')) : 'download';
        const folder = vscode.workspace.workspaceFolders?.[0]?.uri;
        const target = await vscode.window.showSaveDialog({
            defaultUri: folder ? vscode.Uri.joinPath(folder, fileName) : vscode.Uri.file(fileName),
            saveLabel: 'Save',
        });
        if (!target) {
            return;
        }
        await vscode.workspace.fs.writeFile(target, carriedBytes(bytes));
        const pick = await vscode.window.showInformationMessage(`${this.around.product}: saved ${path.basename(target.fsPath)}.`, 'Open');
        if (pick === 'Open') {
            await vscode.env.openExternal(target);
        }
    }
}
