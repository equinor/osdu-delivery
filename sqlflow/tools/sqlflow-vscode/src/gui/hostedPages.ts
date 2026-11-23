import * as vscode from 'vscode';
import { isGuiPath } from '../../../../gui/src/embed/protocol';
import { HostedBuild } from './hostedFiles';
import { HostedLink, HostedSurroundings } from './hostedLink';

/** The kind the editor knows a page's tab by, and restores it under. */
export const PAGE_VIEW_TYPE = 'sqlflow.page';

/** Where a page opens: in the tab group in front, or beside it, for a page read next to the file it is about. */
export interface OpenPage {
    beside?: boolean;
}

interface OpenedPage {
    panel: vscode.WebviewPanel;
    link: HostedLink;
    /** The page the tab shows, as the page last said. */
    path: string;
}

function pathnameOf(pagePath: string): string {
    const cut = pagePath.search(/[?#]/);
    return cut < 0 ? pagePath : pagePath.slice(0, cut);
}

/**
 * The GUI's pages in the editor's own tabs: each a webview that loads the hosted build and shows one page of the
 * workbench, which the person moves through as they would in a browser tab. A page asked for that a tab already shows
 * is brought to the front rather than opened twice, a tab keeps its page while hidden, and the tabs open when the
 * editor closes come back on the pages they showed.
 */
export class HostedPages implements vscode.Disposable {
    private readonly pages = new Set<OpenedPage>();
    private readonly subscriptions: vscode.Disposable[] = [];
    private readonly build: () => HostedBuild;
    private readonly around: () => HostedSurroundings;
    private readonly icon: vscode.Uri | undefined;

    /** `icon` is the picture a page's tab carries: the extension's own, when its manifest names one. */
    constructor(build: () => HostedBuild, around: () => HostedSurroundings, icon?: vscode.Uri) {
        this.build = build;
        this.around = around;
        this.icon = icon;
        this.subscriptions.push(vscode.window.registerWebviewPanelSerializer(PAGE_VIEW_TYPE, {
            deserializeWebviewPanel: async (panel: vscode.WebviewPanel, state: unknown) => {
                const kept = (state as { path?: unknown } | undefined)?.path;
                this.adopt(panel, isGuiPath(kept) ? kept : '/');
            },
        }));
    }

    dispose(): void {
        this.subscriptions.forEach((subscription) => subscription.dispose());
        for (const page of [...this.pages]) {
            page.panel.dispose();
        }
    }

    /**
     * Closes every page's tab, and says how many there were: what the editor does when it moves to another control plane,
     * since a page's address names that control plane's own flows, runs and records by their ids, and the same address on
     * another one names something else.
     */
    closeAll(): number {
        const open = [...this.pages];
        open.forEach((page) => page.panel.dispose());
        return open.length;
    }

    /** Shows a page of the GUI: in the tab already on it, or in a new one. */
    open(pagePath: string, where: OpenPage = {}): void {
        if (!isGuiPath(pagePath)) {
            throw new Error(`'${pagePath}' is not a page of the GUI.`);
        }
        const showing = [...this.pages].find((page) => pathnameOf(page.path) === pathnameOf(pagePath));
        if (showing) {
            showing.panel.reveal(undefined, false);
            if (showing.path !== pagePath) {
                showing.path = pagePath;
                showing.link.post({ type: 'navigate', path: pagePath });
            }
            return;
        }

        const around = this.around();
        const panel = vscode.window.createWebviewPanel(
            PAGE_VIEW_TYPE,
            around.product,
            { viewColumn: where.beside ? vscode.ViewColumn.Beside : vscode.ViewColumn.Active, preserveFocus: false },
            { ...HostedLink.options(around.folder), retainContextWhenHidden: true }
        );
        this.adopt(panel, pagePath);
    }

    private adopt(panel: vscode.WebviewPanel, pagePath: string): void {
        const around = this.around();
        panel.webview.options = HostedLink.options(around.folder);
        panel.iconPath = this.icon;
        const page: OpenedPage = {
            panel,
            path: pagePath,
            link: new HostedLink(panel.webview, around, {
                located: (located, title) => {
                    page.path = located;
                    panel.title = title;
                },
            }),
        };
        this.pages.add(page);
        panel.onDidDispose(() => {
            this.pages.delete(page);
            page.link.dispose();
        });
        try {
            page.link.load(this.build().page, 'page', around.product, pagePath);
        } catch (error) {
            panel.dispose();
            throw error;
        }
    }
}
