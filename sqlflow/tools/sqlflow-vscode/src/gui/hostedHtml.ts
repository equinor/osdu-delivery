import type { HostedEntry } from './hostedFiles.ts';

/** What the page of a webview is written from. */
export interface HostedPage {
    /** The entry the page loads. */
    entry: HostedEntry;
    /** The address the editor serves the hosted build's folder at, without a trailing slash. */
    baseUri: string;
    /** The source the editor serves an extension's files from, as a content security policy names it. */
    cspSource: string;
    /** A value made for this page alone, which the one script the page names carries. */
    nonce: string;
    title: string;
    /** The page of the GUI to show first; absent for the side bar, which shows none. */
    path?: string;
    /** The ground the page stands on until its stylesheet has loaded: the editor's, or the side bar's. */
    surface: 'page' | 'side-bar';
}

function escapeHtml(text: string): string {
    return text
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

/**
 * The content security policy of a hosted page. The page reaches nothing but the extension's own files: every call it
 * makes to the control plane goes through the extension, which holds the credential, so the page may not connect
 * anywhere, load a script from anywhere, or be framed, posted or rebased anywhere.
 *
 * - Scripts: the entry, by its nonce, and the chunks it imports, from the extension's folder. Nothing inline, no eval.
 * - Styles: the build's stylesheets, and inline styles, which the components set on their own elements.
 * - Images and fonts: the build's, and ones a page holds in memory (a drawn chart, a pasted image).
 * - Workers: only from what the page already holds, which is how the code editor's workers are built into it.
 * - `connect-src` admits the extension's files and what the page holds in memory (a file it built, read back to be
 *   saved), and no host: a page cannot call out even if something in it tried to.
 */
export function hostedCsp(cspSource: string, nonce: string): string {
    return [
        "default-src 'none'",
        `script-src 'nonce-${nonce}' ${cspSource}`,
        `style-src ${cspSource} 'unsafe-inline'`,
        `img-src ${cspSource} data: blob:`,
        `font-src ${cspSource} data:`,
        'worker-src blob: data:',
        `connect-src ${cspSource} blob: data:`,
        "base-uri 'none'",
        "form-action 'none'",
        "frame-src 'none'",
    ].join('; ');
}

/**
 * The page a webview loads: a root for the hosted entry to render into, its stylesheets and its one script. Nothing
 * from the estate is written into it (the entry reads everything through the extension once it runs), so the only
 * values placed here are the extension's own, and they are escaped all the same.
 */
export function hostedHtml(page: HostedPage): string {
    const ground = page.surface === 'side-bar' ? 'var(--vscode-sideBar-background)' : 'var(--vscode-editor-background)';
    const styles = page.entry.styles
        .map((style) => `<link rel="stylesheet" href="${escapeHtml(`${page.baseUri}/${style}`)}">`)
        .join('\n');
    const path = page.path === undefined ? '' : ` data-path="${escapeHtml(page.path)}"`;
    return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="${escapeHtml(hostedCsp(page.cspSource, page.nonce))}">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>${escapeHtml(page.title)}</title>
<style nonce="${escapeHtml(page.nonce)}">html, body { height: 100%; margin: 0; padding: 0; background: ${ground}; }</style>
${styles}
</head>
<body>
<div id="root"${path}></div>
<script type="module" nonce="${escapeHtml(page.nonce)}" src="${escapeHtml(`${page.baseUri}/${page.entry.script}`)}"></script>
</body>
</html>`;
}

/**
 * A page that only says something: what a view shows when the hosted build cannot be loaded, so the reason is read where
 * the view would have been rather than in a log. It runs no script and loads nothing.
 */
export function messageHtml(message: string): string {
    return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
</head>
<body style="margin: 0; padding: 12px; font-family: var(--vscode-font-family); font-size: var(--vscode-font-size); color: var(--vscode-foreground);">
<p role="alert" style="margin: 0;">${escapeHtml(message)}</p>
</body>
</html>`;
}
