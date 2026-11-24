// Serves the GUI to the end-to-end suite the way a deployment serves it: the production build of this checkout's sources,
// pointed at the suite's control plane by its config.json. Playwright starts it as one of the suite's servers:
//
//   node e2e/serve-gui.ts --port <port> --api <the control plane's base URL>
//
// The suite tests what ships rather than the dev server. The dev server hands a browser each module of a lazily loaded
// page on its own request, a wave per level of imports, and every test opens a new browser context that asks for them
// all again; on a busy machine a page's first visit outran a spec's wait while its modules were still arriving. The
// build hands a page over as a few bundled chunks.

import { writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { parseArgs } from "node:util";
import { build, preview } from "vite";

const { values } = parseArgs({ options: { port: { type: "string" }, api: { type: "string" } } });

const port = Number(values.port);
if (!Number.isInteger(port) || port < 1 || port > 65535) {
  throw new Error(`--port must name a TCP port, not '${values.port ?? ""}'.`);
}

const api = values.api ?? "";
if (!/^https?:\/\/[^/]+/.test(api)) {
  throw new Error(`--api must be the control plane's base URL, such as http://localhost:5299, not '${api}'.`);
}

const guiRoot = resolve(import.meta.dirname, "..");
const configFile = resolve(guiRoot, "vite.config.ts");
// Under e2e/.fixtures, which git, lint and the dev server's watcher all leave alone, and apart from dist, which is the
// developer's own build.
const outDir = resolve(import.meta.dirname, ".fixtures", "gui");

await build({ root: guiRoot, configFile, logLevel: "warn", build: { outDir, emptyOutDir: true } });

// The build carries public/config.json, which names a developer's control plane. A deployment swaps the file for its
// own, and so does the suite: a production build reads its control plane from this file before anything else.
writeFileSync(resolve(outDir, "config.json"), `${JSON.stringify({ apiBaseUrl: api }, null, 2)}\n`);

const server = await preview({ root: guiRoot, configFile, build: { outDir }, preview: { port, strictPort: true } });
server.printUrls();
