import * as fs from 'fs';
import * as path from 'path';

/** The files one entry of the hosted build needs in the page that loads it: its script, and every stylesheet under it. */
export interface HostedEntry {
    /** The entry's script, from the build's folder, forward slashed. */
    script: string;
    /** The stylesheets of the entry and of everything it imports when it loads, in the order they apply. */
    styles: string[];
}

/** The hosted build: the GUI's pages and its side bar, as files an editor's webview loads. */
export interface HostedBuild {
    /** The folder the files are in, which a webview is given to read from and nothing else. */
    folder: string;
    page: HostedEntry;
    sideBar: HostedEntry;
}

/** One chunk of the bundler's manifest (`.vite/manifest.json`). */
interface ManifestChunk {
    file: string;
    isEntry?: boolean;
    css?: string[];
    imports?: string[];
}

type Manifest = Record<string, ManifestChunk>;

/**
 * The files of an entry, from the bundler's manifest. A stylesheet may belong to a chunk the entry imports rather than
 * to the entry itself (what two entries share is a chunk of its own), so the stylesheets are gathered from everything
 * the entry imports when it loads, the imported chunks' first: the order the bundler would link them in.
 */
export function hostedEntry(manifest: Manifest, script: string): HostedEntry {
    const key = Object.keys(manifest).find((candidate) => manifest[candidate].isEntry === true && manifest[candidate].file === script);
    if (key === undefined) {
        throw new Error(`The hosted build has no entry '${script}'.`);
    }

    const styles: string[] = [];
    const seen = new Set<string>();
    const gather = (name: string): void => {
        const chunk = manifest[name];
        if (chunk === undefined || seen.has(name)) {
            return;
        }
        seen.add(name);
        for (const imported of chunk.imports ?? []) {
            gather(imported);
        }
        for (const style of chunk.css ?? []) {
            if (!styles.includes(style)) {
                styles.push(style);
            }
        }
    };
    gather(key);
    return { script, styles };
}

/**
 * Reads the hosted build from its folder. An extension run from a checkout has one only once the GUI's hosted build has
 * run, and a package always holds one, so a missing build says how to make it rather than leaving a blank view.
 */
export function readHostedBuild(folder: string): HostedBuild {
    const manifestFile = path.join(folder, '.vite', 'manifest.json');
    let manifest: Manifest;
    try {
        manifest = JSON.parse(fs.readFileSync(manifestFile, 'utf8')) as Manifest;
    } catch (error) {
        throw new Error(
            `The pages this extension shows are not built: ${manifestFile} could not be read (${error instanceof Error ? error.message : error}). `
            + 'Run the extension\'s bundle script, which builds them.'
        );
    }
    return { folder, page: hostedEntry(manifest, 'page.js'), sideBar: hostedEntry(manifest, 'side-bar.js') };
}
