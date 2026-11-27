import * as vscode from 'vscode';
import { HostedBuild } from './hostedFiles';
import { HostedLink, HostedSurroundings } from './hostedLink';

/** The view the side bar is contributed as. */
export const SIDE_BAR_VIEW = 'sqlflow.sideBar';

/**
 * The extension's side bar: one drawn view in place of the editor's own trees. It is the hosted build's side bar entry,
 * the workbench's own navigation: the environment it works against, its search, a sync of a repository on the control
 * plane signed in to, who is signed in, and the menu, each page opening in a tab of the editor. It is where the person's
 * environments are switched, added and changed, and the only surface that may. The files of the folder open in the editor
 * are listed by the editor's own Explorer, so the side bar lists none.
 */
export class HostedSideBar implements vscode.WebviewViewProvider, vscode.Disposable {
    private link: HostedLink | undefined;
    /** Whether the environments were asked for before the side bar could show them: shown once it has started. */
    private environmentsAsked = false;

    constructor(
        private readonly build: () => HostedBuild,
        private readonly around: () => HostedSurroundings
    ) {}

    dispose(): void {
        this.link?.dispose();
    }

    /**
     * Shows the person's environments in the side bar: at once when it is drawn, or as soon as it starts when the editor
     * is still drawing it. The view itself is brought forward by its own focus command, which the caller runs.
     */
    showEnvironments(): void {
        this.environmentsAsked = true;
        if (this.link?.listening === true) {
            this.environmentsAsked = false;
            this.link.post({ type: 'showEnvironments' });
        }
    }

    resolveWebviewView(view: vscode.WebviewView): void {
        const around = this.around();
        view.webview.options = HostedLink.options(around.folder);
        const link = new HostedLink(view.webview, around, {
            environments: true,
            started: () => {
                if (this.environmentsAsked) {
                    this.environmentsAsked = false;
                    link.post({ type: 'showEnvironments' });
                }
            },
        });
        this.link?.dispose();
        this.link = link;
        // A side bar the person has closed, or moved behind another view, asks the control plane nothing until shown.
        link.setShown(view.visible);
        const visibility = view.onDidChangeVisibility(() => link.setShown(view.visible));
        view.onDidDispose(() => {
            visibility.dispose();
            link.dispose();
            if (this.link === link) {
                this.link = undefined;
            }
        });
        try {
            link.load(this.build().sideBar, 'side-bar', around.product);
        } catch (error) {
            // A checkout whose pages are not built yet: the view says how to build them, where it would have been drawn.
            const message = error instanceof Error ? error.message : String(error);
            around.output.error(`[side bar] ${message}`);
            link.say(message);
        }
    }
}
