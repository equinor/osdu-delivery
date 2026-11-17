import * as path from 'path';
import * as vscode from 'vscode';
import { cliCommand, offerCliInTerminals } from './cli';
import { Docs } from './docs/docs';
import { DocumentIndex, IndexedDocument, recogniseOpenedDocuments } from './documents/documentIndex';
import { keyPathAtLine } from './documents/yamlPath';
import {
    cliTaskProvider,
    CliVerb,
    compareWithSynced,
    DocumentLenses,
    DocumentSkeleton,
    newDocument,
    RepositoryValidation,
    runCliVerb,
    SYNCED_SCHEME,
    SyncedDefinitions,
    TASK_TYPE,
} from './editor/documentActions';
import { builtInReferences, References } from './editor/references';
import { HostedBuild, readHostedBuild } from './gui/hostedFiles';
import { HostedSurroundings } from './gui/hostedLink';
import { HostedPages, OpenPage } from './gui/hostedPages';
import { HostedSession } from './gui/hostedSession';
import { HostedSideBar, SIDE_BAR_VIEW } from './gui/hostedSideBar';
import { startLspClient, stopLspClient } from './lsp/lspClient';
import { Estate, Pipeline } from './mcp/estate';
import { connectAssistant, registerWithEditor } from './mcp/mcpSetup';
import { McpServer } from './mcp/mcpServer';
import { SignIn } from './mcp/signIn';
import { SignInNeeded } from './mcp/toolAnswer';

const DOCUMENT_SELECTOR: vscode.DocumentSelector = [{ language: 'sqlflow-flow' }];

/**
 * What a product built on SQLFlow gives this extension, as it gives the control plane, the CLI, the GUI and the MCP
 * server: its names, its own server, and modules. The extension here is that composition: SQLFlow's own extension is this
 * host with no modules, and a product's is a package of its own that activates with its host.
 */
export interface VscodeHost {
    /** The product's name, as people read it in notifications and views. */
    product: string;
    /** The MCP server the product composes. */
    mcp: {
        /** Its program. */
        program: string;
        /** The id of the manifest's `mcpServerDefinitionProviders`. */
        providerId: string;
        /** Build folders to look in when running from a checkout. */
        searchPaths(extensionPath: string): string[];
    };
    /** Build folders of the language server when running from a checkout. */
    lspSearchPaths(extensionPath: string): string[];
    /** The folders of the key census files its modules add. */
    censusDirectories(extensionPath: string): string[];
    /** The reference corpora it ships, each a folder holding a `manifest.json`. */
    docCorpora(extensionPath: string): string[];
    /** The skeletons `New Document` offers. */
    skeletons: DocumentSkeleton[];
    /** The verbs its command line takes on a document. */
    cliVerbs: CliVerb[];
    modules?: VscodeModule[];
}

/** A module's part of the extension: what it adds when the host activates. */
export interface VscodeModule {
    activate(context: ModuleContext): void;
}

/** The GUI's pages, as the extension opens them: each in a tab of the editor, read with the person's sign-in. */
export interface Pages {
    /** Shows a page by its path in the GUI (`/runs`, `/pipelines/<id>`), in the tab already on it or a new one. */
    open(path: string, where?: OpenPage): void;
}

/** What a module adds to, and what it reads through. */
export interface ModuleContext {
    extension: vscode.ExtensionContext;
    product: string;
    estate: Estate;
    documents: DocumentIndex;
    /** The GUI's pages, the module's own among them: what a module shows instead of drawing a view of its own. */
    pages: Pages;
    output: vscode.LogOutputChannel;
    /** What keys refer to: a module adds a provider per key it owns. */
    references: References;
    lenses: DocumentLenses;
    /** Registers a command that reads the estate: a failure is shown, and a missing sign-in offers one. */
    command(id: string, action: (...args: unknown[]) => Promise<void>): void;
    /** Everything read from the estate changed: read it again. */
    refreshed: vscode.Event<void>;
}

/** The folder of an extension the hosted build is in: the GUI's pages and side bar, built for the editor's webviews. */
function hostedFolder(extensionPath: string): string {
    return path.join(extensionPath, 'dist', 'hosted');
}

/** Composes the extension for a host. */
export async function activateHost(context: vscode.ExtensionContext, host: VscodeHost): Promise<{ estate: Estate; pages: Pages }> {
    // A log channel, since the language client writes its log here too and vscode-languageclient 10 accepts nothing
    // else.
    const output = vscode.window.createOutputChannel(host.product, { log: true });
    context.subscriptions.push(output);

    const { extensionPath } = context;
    const version = String(context.extension.packageJSON.version ?? '0.0.0');
    const mcp = new McpServer({
        program: host.mcp.program,
        searchPaths: host.mcp.searchPaths(extensionPath),
        extensionPath,
        version,
        output,
    });
    const estate = new Estate(mcp);
    const signIn = new SignIn(mcp, host.product);
    const documents = new DocumentIndex(output);
    const docs = new Docs(host.docCorpora(extensionPath), mcp);
    const session = new HostedSession(mcp, signIn, host.product);
    context.subscriptions.push(mcp, signIn, documents, session);

    const onRefresh = new vscode.EventEmitter<void>();
    const onServerChange = new vscode.EventEmitter<void>();
    context.subscriptions.push(onRefresh, onServerChange);
    const references = new References(documents);
    builtInReferences(documents, estate).forEach((provider) => references.register(provider));
    const lenses = new DocumentLenses(documents, estate);
    const validation = new RepositoryValidation(documents, estate, host.product);
    context.subscriptions.push(validation);

    /** Runs an action that reads the estate: a missing sign-in offers one and tries again, anything else is shown. */
    const guarded = async (action: () => Promise<void>): Promise<void> => {
        try {
            await action();
        } catch (err) {
            if (err instanceof SignInNeeded) {
                const pick = await vscode.window.showWarningMessage(`${host.product}: sign in to do this.`, 'Sign In');
                if (pick === 'Sign In' && (await signIn.signIn())) {
                    await guarded(action);
                }
                return;
            }
            const message = err instanceof Error ? err.message : String(err);
            output.error(message);
            void vscode.window.showErrorMessage(`${host.product}: ${message}`);
        }
    };

    // The hosted build is read when the first surface is shown, and once: a checkout whose pages are not built yet still
    // has its language support, its lenses and its commands.
    let hosted: HostedBuild | undefined;
    const build = (): HostedBuild => (hosted ??= readHostedBuild(hostedFolder(extensionPath)));
    const around = (): HostedSurroundings => ({
        product: host.product,
        folder: vscode.Uri.file(hostedFolder(extensionPath)),
        session,
        documents,
        output,
        userAgent: `${host.mcp.program}-vscode/${version}`,
        openPage: (pagePath) => pages.open(pagePath),
    });
    const icon = context.extension.packageJSON.icon as string | undefined;
    const hostedPages = new HostedPages(build, around, icon ? vscode.Uri.joinPath(context.extensionUri, icon) : undefined);
    const pages: Pages = {
        open: (pagePath, where) => {
            try {
                hostedPages.open(pagePath, where);
            } catch (err) {
                const message = err instanceof Error ? err.message : String(err);
                output.error(message);
                void vscode.window.showErrorMessage(`${host.product}: ${message}`);
            }
        },
    };
    const sideBar = new HostedSideBar(build, around);
    context.subscriptions.push(
        hostedPages,
        sideBar,
        vscode.window.registerWebviewViewProvider(SIDE_BAR_VIEW, sideBar, { webviewOptions: { retainContextWhenHidden: true } })
    );

    const refreshAll = (): void => {
        estate.forget();
        lenses.refresh();
        onRefresh.fire();
    };
    context.subscriptions.push(
        signIn.changed(refreshAll),
        // A page tells a person what did not answer; why, in the server's own words, is kept here.
        signIn.changed(() => {
            if (signIn.problem !== undefined) {
                output.warn(`[sign-in] ${signIn.problem}`);
            }
        }),
        signIn.changed(() => onServerChange.fire()),
        vscode.workspace.onDidChangeConfiguration((event) => {
            if (event.affectsConfiguration('sqlflow.controlPlaneUrl') || event.affectsConfiguration('sqlflow.guiUrl')
                || event.affectsConfiguration('sqlflow.mcpBinaryPath')) {
                mcp.restart();
                onServerChange.fire();
                offerCliInTerminals(context, host.product);
                void signIn.check();
            }
        })
    );

    const register = (id: string, fn: (...args: unknown[]) => unknown): void => {
        context.subscriptions.push(vscode.commands.registerCommand(id, fn));
    };
    const command = (id: string, action: (...args: unknown[]) => Promise<void>): void => {
        register(id, (...args: unknown[]) => guarded(() => action(...args)));
    };

    register('sqlflow.signIn', async () => {
        await signIn.signIn();
    });
    register('sqlflow.signInWithToken', async () => {
        await signIn.signInWithToken();
    });
    command('sqlflow.signOut', () => signIn.signOut());

    register('sqlflow.setControlPlaneUrl', async () => {
        const current = vscode.workspace.getConfiguration('sqlflow').get<string>('controlPlaneUrl', '');
        const url = await vscode.window.showInputBox({
            prompt: `${host.product} control plane base URL`,
            value: current,
            ignoreFocusOut: true,
        });
        if (url) {
            await vscode.workspace.getConfiguration('sqlflow').update('controlPlaneUrl', url.trim(), vscode.ConfigurationTarget.Global);
        }
    });

    register('sqlflow.newFlow', () => newDocument(host.skeletons));

    register('sqlflow.validateFlow', async () => {
        const editor = vscode.window.activeTextEditor;
        if (!editor || editor.document.languageId !== 'sqlflow-flow') {
            void vscode.window.showInformationMessage('Open a pipeline document to validate.');
            return;
        }
        const diags = vscode.languages.getDiagnostics(editor.document.uri);
        const errors = diags.filter((d) => d.severity === vscode.DiagnosticSeverity.Error).length;
        const warnings = diags.filter((d) => d.severity === vscode.DiagnosticSeverity.Warning).length;
        if (diags.length === 0) {
            void vscode.window.showInformationMessage(`${host.product}: no problems found.`);
        } else {
            void vscode.commands.executeCommand('workbench.actions.view.problems');
            void vscode.window.showWarningMessage(`${host.product}: ${errors} error(s), ${warnings} warning(s).`);
        }
    });
    command('sqlflow.validateRepository', () => validation.run());
    command('sqlflow.runVerb', () => runCliVerb(host.cliVerbs, documents, cliCommand(extensionPath)));

    register('sqlflow.searchDocs', () => docs.search());
    register('sqlflow.openDocsForKey', async () => {
        const editor = vscode.window.activeTextEditor;
        if (!editor) {
            return;
        }
        const key = keyPathAtLine(editor.document.getText().split(/\r?\n/), editor.selection.active.line);
        if (!key) {
            void vscode.window.showInformationMessage('No key at the cursor.');
            return;
        }
        if (!(await docs.openForYamlPath(key.path))) {
            void vscode.window.showInformationMessage(`No reference page documents '${key.path}'.`);
        }
    });

    register('sqlflow.refreshCatalog', refreshAll);
    command('sqlflow.refreshRepository', () => documents.refresh());

    command('sqlflow.syncRepository', async () => {
        const source = await pickSource(estate);
        if (!source) {
            return;
        }
        await estate.syncRepoSource(source.id);
        void vscode.window.showInformationMessage(`${host.product}: syncing ${source.name}.`);
        refreshAll();
    });

    /** The catalog's flow a command is about: the document given, or the one in the editor, or one the person picks. */
    const resolvePipeline = async (arg: unknown): Promise<{ pipeline: Pipeline; document?: IndexedDocument } | undefined> => {
        const uri = arg instanceof vscode.Uri ? arg : vscode.window.activeTextEditor?.document.uri;
        const document = uri ? await documents.at(uri) : undefined;
        if (document?.kind === 'flow' && document.name) {
            const pipeline = await estate.pipelineNamed(document.name, document.relativePath, document.flowType);
            if (pipeline) {
                return { pipeline, document };
            }
            const pick = await vscode.window.showWarningMessage(
                `${host.product}: '${document.name}' is not in the estate. Push it and sync the repository first.`,
                'Sync Repository'
            );
            if (pick === 'Sync Repository') {
                await vscode.commands.executeCommand('sqlflow.syncRepository');
            }
            return undefined;
        }
        const pipelines = await estate.pipelines({ active: true });
        const chosen = await vscode.window.showQuickPick(
            pipelines.map((p) => ({ label: p.name, description: p.kind, pipeline: p })),
            { placeHolder: 'Select a flow' }
        );
        return chosen ? { pipeline: chosen.pipeline } : undefined;
    };

    const openRun = (runId: string, where?: OpenPage): void => pages.open(`/runs/${encodeURIComponent(runId)}`, where);
    const openFlowPage = (pipeline: Pipeline, where?: OpenPage): void => pages.open(`/pipelines/${encodeURIComponent(pipeline.id)}`, where);

    command('sqlflow.openPipeline', async (arg: unknown) => {
        const resolved = await resolvePipeline(arg);
        if (resolved) {
            openFlowPage(resolved.pipeline, { beside: resolved.document !== undefined });
        }
    });

    command('sqlflow.openRun', async (arg: unknown) => {
        if (typeof arg === 'string' && arg !== '') {
            openRun(arg);
        }
    });

    register('sqlflow.openPage', (arg: unknown) => {
        if (typeof arg === 'string') {
            pages.open(arg);
        }
    });
    register('sqlflow.openHome', () => pages.open('/'));
    register('sqlflow.openRuns', () => pages.open('/runs'));
    register('sqlflow.showLineage', () => pages.open('/lineage'));

    command('sqlflow.compareWithSynced', async (arg: unknown) => {
        const uri = arg instanceof vscode.Uri ? arg : vscode.window.activeTextEditor?.document.uri;
        if (uri) {
            await compareWithSynced(estate, documents, uri);
        }
    });

    command('sqlflow.openInGui', async (arg: unknown) => {
        const resolved = await resolvePipeline(arg);
        if (!resolved) {
            return;
        }
        const page = resolved.pipeline.links?.page;
        if (!page) {
            void vscode.window.showInformationMessage(`${host.product}: set sqlflow.guiUrl to open the GUI in the browser from here.`);
            return;
        }
        await vscode.env.openExternal(vscode.Uri.parse(page));
    });

    register('sqlflow.setupMcp', () => connectAssistant(
        { providerId: host.mcp.providerId, product: host.product, program: host.mcp.program, version },
        mcp
    ));

    register('sqlflow.showStatus', async () => {
        const signedIn = signIn.who !== undefined;
        const pick = await vscode.window.showQuickPick(
            [
                signedIn
                    ? { label: '$(sign-out) Sign Out', id: 'sqlflow.signOut' }
                    : { label: '$(sign-in) Sign In with the Browser', id: 'sqlflow.signIn' },
                ...(signedIn ? [] : [{ label: '$(key) Sign In with an Access Token', id: 'sqlflow.signInWithToken' }]),
                { label: '$(gear) Set Control Plane URL', id: 'sqlflow.setControlPlaneUrl' },
                { label: '$(hubot) Connect an AI Assistant', id: 'sqlflow.setupMcp' },
                { label: '$(checklist) Validate Repository', id: 'sqlflow.validateRepository' },
                { label: '$(book) Search Docs', id: 'sqlflow.searchDocs' },
            ],
            { placeHolder: signedIn ? `${host.product}: signed in as ${signIn.who}` : `${host.product}: not signed in` }
        );
        if (pick) {
            await vscode.commands.executeCommand(pick.id);
        }
    });

    const status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 50);
    status.command = 'sqlflow.showStatus';
    const showStatus = (): void => {
        status.text = signIn.who ? `$(account) ${host.product}` : `$(sign-in) ${host.product}: sign in`;
        status.tooltip = signIn.who ? `Signed in as ${signIn.who}` : 'Sign in with the browser';
        status.show();
    };
    context.subscriptions.push(status, signIn.changed(showStatus));
    showStatus();

    context.subscriptions.push(
        recogniseOpenedDocuments(),
        vscode.languages.registerCompletionItemProvider(DOCUMENT_SELECTOR, references, ':', ' '),
        vscode.languages.registerDocumentLinkProvider(DOCUMENT_SELECTOR, references),
        vscode.languages.registerDefinitionProvider(DOCUMENT_SELECTOR, references),
        vscode.languages.registerCodeLensProvider(DOCUMENT_SELECTOR, lenses),
        vscode.workspace.registerTextDocumentContentProvider(SYNCED_SCHEME, new SyncedDefinitions(estate)),
        vscode.tasks.registerTaskProvider(TASK_TYPE, cliTaskProvider(() => cliCommand(extensionPath))),
        registerWithEditor(
            { providerId: host.mcp.providerId, product: host.product, program: host.mcp.program, version },
            mcp,
            onServerChange.event
        )
    );

    const moduleContext: ModuleContext = {
        extension: context,
        product: host.product,
        estate,
        documents,
        pages,
        output,
        references,
        lenses,
        command,
        refreshed: onRefresh.event,
    };
    for (const module of host.modules ?? []) {
        module.activate(moduleContext);
    }

    // Start the language server (optional; every other feature works without it).
    void startLspClient(context, output, {
        searchPaths: host.lspSearchPaths(extensionPath),
        censusDirectories: host.censusDirectories(extensionPath),
    });

    offerCliInTerminals(context, host.product);
    void signIn.check();
    output.info(`${host.product} extension activated.`);
    return { estate, pages };
}

export async function deactivateHost(): Promise<void> {
    await stopLspClient();
}

async function pickSource(estate: Estate): Promise<{ id: string; name: string } | undefined> {
    const sources = await estate.repoSources();
    if (sources.length === 1) {
        return sources[0];
    }
    const chosen = await vscode.window.showQuickPick(
        sources.map((source) => ({ label: source.name, description: source.lastSyncedSha?.slice(0, 8), source })),
        { placeHolder: 'Sync which repository?' }
    );
    return chosen?.source;
}
