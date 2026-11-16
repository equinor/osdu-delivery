// How a hosted build is laid out (src/embed/): the pages and the side bar an editor shows in its webviews, built from
// the same sources as the browser's GUI into the folder the editor's extension serves. SQLFlow's own hosted build
// (vite.hosted.config.ts) and a module build's share this, so a host finds the same files in either. Kept free of
// imports, so a module build's config reads it whether or not this folder has packages installed.

/** The entries of a hosted build, by the path of their source. */
export interface HostedEntries {
  /** The entry that calls renderHosted. */
  page: string;
  /** The entry that calls renderHostedSideBar. */
  sideBar: string;
}

/**
 * The build options of a hosted build. An extension writes the page that loads an entry itself (it has to: the page
 * carries a content security policy with a nonce of its own), so the entries keep their names, `page.js` and
 * `side-bar.js`, and the manifest beside them (`.vite/manifest.json`) says which stylesheets each one needs. Everything
 * else is named by its content, and addressed relative to the file that asks for it, since the folder is served from an
 * address only the editor knows.
 */
export function hostedBuild(entries: HostedEntries, outDir: string) {
  return {
    outDir,
    emptyOutDir: true,
    sourcemap: false,
    manifest: true,
    cssCodeSplit: true,
    // The code editor is bundled, as in the browser's build, and it is large; an entry that shows code holds it, and
    // is read from the extension's own folder rather than fetched.
    chunkSizeWarningLimit: 8192,
    rollupOptions: {
      input: { page: entries.page, "side-bar": entries.sideBar },
      output: {
        entryFileNames: "[name].js",
        chunkFileNames: "chunks/[name]-[hash].js",
        assetFileNames: "assets/[name]-[hash][extname]",
        // The icons are hundreds of files of a kilobyte each, every one a chunk of its own when left alone. They go
        // out as one file, which loads once and is read from the extension's folder, where a file opened costs more
        // than its bytes. The library is a leaf, importing nothing of the GUI, so grouping it changes no order anything
        // runs in. The code editor's grammars stay a file each: grouped, they would bring the editor with them into
        // whatever asks for a single one, the side bar included.
        codeSplitting: {
          groups: [{ name: "icons", test: /node_modules[\\/]lucide-react[\\/]/ }],
        },
      },
    },
  };
}

/**
 * Builds every worker a source imports (`…?worker`) into the script that starts it, rather than into a file of its
 * own. An editor's webview may start a worker only from what the page already holds, never from a file of the
 * extension's folder, so a worker left as a file would never start there.
 */
export function workersInline() {
  return {
    name: "hosted:workers-inline",
    enforce: "pre" as const,
    async resolveId(
      this: { resolve(source: string, importer: string | undefined, options: Record<string, unknown>): Promise<unknown> },
      source: string,
      importer: string | undefined,
      options: Record<string, unknown>,
    ): Promise<unknown> {
      if (!/[?&]worker$/.test(source)) {
        return null;
      }

      return this.resolve(`${source}&inline`, importer, { ...options, skipSelf: true });
    },
  };
}
