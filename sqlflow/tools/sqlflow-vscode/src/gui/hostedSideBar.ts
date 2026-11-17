import * as vscode from 'vscode';
import { HostedBuild } from './hostedFiles';
import { HostedLink, HostedSurroundings } from './hostedLink';

/** The view the side bar is contributed as. */
export const SIDE_BAR_VIEW = 'sqlflow.sideBar';

/**
 * The extension's side bar: one drawn view in place of the editor's own trees. It is the hosted build's side bar entry,
 * the workbench's own navigation: its search, a sync of a repository on the control plane signed in to, who is signed
 * in, and the menu, each page opening in a tab of the editor. The files of the folder open in the editor are listed by
 * the editor's own Explorer, so the side bar lists none.
 */
export class HostedSideBar implements vscode.WebviewViewProvider, vscode.Disposable {
    private link: HostedLink | undefined;

    constructor(
        private readonly build: () => HostedBuild,
        private readonly around: () => HostedSurroundings
    ) {}

    dispose(): void {
        this.link?.dispose();
    }

    resolveWebviewView(view: vscode.WebviewView): void {
        const around = this.around();
        view.webview.options = HostedLink.options(around.folder);
        const link = new HostedLink(view.webview, around, {});
        this.link?.dispose();
        this.link = link;
        view.onDidDispose(() => {
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
